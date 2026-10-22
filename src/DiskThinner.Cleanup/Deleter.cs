using System.Runtime.InteropServices;
using DiskThinner.Cleanup.Native;
using DiskThinner.Core;
using DiskThinner.Rules;
using Microsoft.Win32.SafeHandles;

namespace DiskThinner.Cleanup;

/// <summary>一次删除的结果。字节数都按分配大小（实际占用的簇）计。</summary>
public sealed class DeleteOutcome
{
    /// <summary>每类最多记这么多条路径，其余只计数。</summary>
    public const int MaxListed = 200;

    public int FilesDeleted { get; internal set; }

    /// <summary>删除的文件占用（含硬链接共享的部分）。</summary>
    public long BytesDeleted { get; internal set; }

    /// <summary>其中还有别的硬链接名、删掉这个名字不释放空间的部分。</summary>
    public long SharedBytes { get; internal set; }

    public int DirsDeleted { get; internal set; }

    /// <summary>删掉的 junction 和符号链接（只删链接本身，不进入目标）。</summary>
    public int LinksRemoved { get; internal set; }

    /// <summary>按过滤条件保留的文件（太新、keep_latest）。</summary>
    public int Kept { get; internal set; }

    public int LockedCount { get; internal set; }

    /// <summary>被占用而跳过的文件（最多 <see cref="MaxListed"/> 条）。</summary>
    public List<string> Locked { get; } = [];

    public int ProtectedCount { get; internal set; }

    /// <summary>受保护名单保护而跳过的路径。</summary>
    public List<(string Path, string Reason)> Protected { get; } = [];

    /// <summary>不是 junction 或符号链接的重解析点（云文件占位符、去重文件等），不碰。</summary>
    public int OtherReparseSkipped { get; internal set; }

    public int ErrorCount { get; internal set; }

    public List<string> Errors { get; } = [];

    public bool Cancelled { get; internal set; }

    /// <summary>预计这次能释放的空间：删掉的占用减去硬链接共享的部分。</summary>
    public long FreedEstimate => BytesDeleted - SharedBytes;

    internal void AddLocked(string path)
    {
        LockedCount++;
        if (Locked.Count < MaxListed)
        {
            Locked.Add(path);
        }
    }

    internal void AddError(string path, int code)
    {
        ErrorCount++;
        if (Errors.Count < MaxListed)
        {
            Errors.Add(L.F("{0}：{1}（{2}）", path, new System.ComponentModel.Win32Exception(code).Message, code));
        }
    }

    internal void AddProtected(string path, string reason)
    {
        ProtectedCount++;
        if (Protected.Count < MaxListed)
        {
            Protected.Add((path, reason));
        }
    }
}

/// <summary>删除时的进度。</summary>
public readonly record struct DeleteProgress(int Files, long Bytes, string Current);

/// <summary>
/// 删除器（研发方案 3.6 节）：
/// - 自底向上删除；路径加 \\?\ 前缀，不受长度限制。
/// - 遇到 junction 或符号链接只删链接本身，绝不进入目标；其他重解析点（云文件占位符等）不碰。
/// - 每个文件和目录都先打开句柄（不跟随重解析点），用句柄确认类型后再按句柄删除，确认和删除之间不会被换成别的东西；
///   正在处理的目录用不允许删除共享的句柄占住，处理期间不能被改名或替换。只读文件直接忽略只读属性删除。
/// - 每一级都再查一次保护名单，受保护的路径和它下面的内容全部跳过。
/// - 被占用的文件跳过并记下来，交给 Restart Manager 查是谁占用。
/// </summary>
internal sealed unsafe class Deleter
{
    private readonly ProtectionScope _protection;
    private readonly bool _allowSoft;
    private readonly long _cutoff;
    private readonly CancellationToken _token;
    private readonly IProgress<DeleteProgress>? _progress;
    private readonly DeleteOutcome _outcome = new();
    private int _sinceReport;

    /// <param name="protection">这个位置下面可能命中的保护项。</param>
    /// <param name="allowSoft">软保护（用户文件）下面也可以删：只有实测过的规则可以。</param>
    /// <param name="olderThanUtc">只删最后修改时间早于它的文件（UTC FILETIME）；null 表示不限。</param>
    public Deleter(ProtectionScope protection, bool allowSoft, DateTime? olderThanUtc, IProgress<DeleteProgress>? progress, CancellationToken token)
    {
        _protection = protection;
        _allowSoft = allowSoft;
        _cutoff = olderThanUtc?.ToFileTimeUtc() ?? long.MaxValue;
        _progress = progress;
        _token = token;
    }

    /// <summary>删除一个文件（scope 只能是 self）。</summary>
    public DeleteOutcome DeleteFile(string path)
    {
        var ext = Win32.Extended(path);
        var h = Win32.FindFirst(ext, out var data);
        if (h == -1)
        {
            return _outcome; // 已经不在了
        }

        Win32.FindClose(h);
        DeleteEntry(path, ref data);
        return _outcome;
    }

    /// <summary>
    /// 清理一个目录。<paramref name="removeSelf"/> 为 true 时删除目录本身（scope = self），否则保留目录、删除里面的内容。
    /// <paramref name="keepLatest"/>：直接子项按最后修改时间（目录取子树里最新的文件）保留最新的几个。
    /// </summary>
    public DeleteOutcome DeleteDirectory(string path, bool removeSelf, int? keepLatest)
    {
        using var hold = Hold(path);
        if (hold is null)
        {
            _outcome.AddError(path, Marshal.GetLastPInvokeError() is var e && e != 0 ? e : Win32.ErrorAccessDenied);
            return _outcome;
        }

        HashSet<string>? keep = null;
        if (keepLatest is > 0)
        {
            keep = LatestChildren(path, keepLatest.Value);
            _outcome.Kept += keep.Count;
        }

        var emptied = DeleteChildren(path, keep);
        hold.Dispose();
        if (removeSelf && emptied && !_outcome.Cancelled)
        {
            RemoveByHandle(path, directory: true, reparse: false);
        }

        return _outcome;
    }

    /// <summary>删除目录里的内容。返回目录是否已经空了。</summary>
    private bool DeleteChildren(string dir, HashSet<string>? keep)
    {
        var emptied = true;
        var h = Win32.FindFirst(Win32.Extended(dir) + @"\*", out var data);
        if (h == -1)
        {
            var e = Marshal.GetLastPInvokeError();
            if (e is not (Win32.ErrorFileNotFound or Win32.ErrorPathNotFound))
            {
                _outcome.AddError(dir, e);
                return false;
            }

            return true;
        }

        try
        {
            do
            {
                if (_token.IsCancellationRequested)
                {
                    _outcome.Cancelled = true;
                    return false;
                }

                var name = NameOf(ref data);
                if (name is "." or "..")
                {
                    continue;
                }

                if (keep is not null && keep.Contains(name))
                {
                    emptied = false;
                    continue;
                }

                if (!DeleteEntry(dir + "\\" + name, ref data))
                {
                    emptied = false;
                }
            }
            while (Win32.FindNext(h, out data));
        }
        finally
        {
            Win32.FindClose(h);
        }

        return emptied;
    }

    /// <summary>处理一个目录项。返回它是否已经不在了（删掉了或本来就没有）。</summary>
    private bool DeleteEntry(string path, ref Win32.FindData data)
    {
        if (_protection.EntryAt(path) is { } entry && Blocks(entry.Level))
        {
            _outcome.AddProtected(path, entry.Reason);
            return false;
        }

        var attributes = data.FileAttributes;
        var isDir = (attributes & Win32.FileAttributeDirectory) != 0;
        if ((attributes & Win32.FileAttributeReparsePoint) != 0)
        {
            if (data.Reserved0 is not (Win32.ReparseTagMountPoint or Win32.ReparseTagSymlink))
            {
                _outcome.OtherReparseSkipped++;
                return false;
            }

            if (data.LastWriteUtc >= _cutoff)
            {
                _outcome.Kept++;
                return false;
            }

            if (RemoveByHandle(path, isDir, reparse: true))
            {
                _outcome.LinksRemoved++;
                return true;
            }

            return false;
        }

        if (isDir)
        {
            bool emptied;
            using (var hold = Hold(path))
            {
                if (hold is null)
                {
                    var e = Marshal.GetLastPInvokeError();
                    if (e is Win32.ErrorFileNotFound or Win32.ErrorPathNotFound)
                    {
                        return true;
                    }

                    _outcome.AddError(path, e);
                    return false;
                }

                emptied = DeleteChildren(path, null);
            }

            // 有时间条件时，最近建立或改动过的空目录也留着（可能是正在运行的程序刚建的工作目录）。
            // 时间取枚举时看到的值：删掉里面的文件会改变目录的修改时间。
            return emptied && !_outcome.Cancelled && data.LastWriteUtc < _cutoff && RemoveByHandle(path, directory: true, reparse: false);
        }

        if (data.LastWriteUtc >= _cutoff)
        {
            _outcome.Kept++;
            return false;
        }

        return RemoveByHandle(path, directory: false, reparse: false);
    }

    private bool Blocks(ProtectionLevel level) =>
        level is ProtectionLevel.Hard or ProtectionLevel.Guide || (level == ProtectionLevel.Soft && !_allowSoft);

    /// <summary>
    /// 打开句柄（不跟随重解析点），确认它还是枚举时看到的类型，再按句柄删除。
    /// 先用 POSIX 语义 + 忽略只读属性；文件系统不支持时清掉只读属性再用普通方式删除。
    /// </summary>
    private bool RemoveByHandle(string path, bool directory, bool reparse)
    {
        var flags = Win32.FlagOpenReparsePoint | (directory ? Win32.FlagBackupSemantics : 0);
        using var h = Win32.CreateFile(Win32.Extended(path), Win32.AccessDelete | Win32.FileReadAttributes | Win32.FileWriteAttributes,
            Win32.ShareRead | Win32.ShareWrite | Win32.ShareDelete, 0, Win32.OpenExisting, flags, 0);
        if (h.IsInvalid)
        {
            return Failed(path, Marshal.GetLastPInvokeError());
        }

        Win32.AttributeTagInfo tag;
        if (!Win32.GetFileInformationByHandleEx(h, Win32.FileAttributeTagInfoClass, &tag, (uint)sizeof(Win32.AttributeTagInfo)))
        {
            return Failed(path, Marshal.GetLastPInvokeError());
        }

        var isReparse = (tag.FileAttributes & Win32.FileAttributeReparsePoint) != 0;
        var isDir = (tag.FileAttributes & Win32.FileAttributeDirectory) != 0;
        if (isReparse != reparse || isDir != directory ||
            (isReparse && tag.ReparseTag is not (Win32.ReparseTagMountPoint or Win32.ReparseTagSymlink)))
        {
            // 枚举之后被换成了别的东西：不删
            _outcome.AddError(path, Win32.ErrorAccessDenied);
            return false;
        }

        long alloc = 0;
        var shared = false;
        if (!directory && !reparse)
        {
            Win32.StandardInfo info;
            if (Win32.GetFileInformationByHandleEx(h, Win32.FileStandardInfoClass, &info, (uint)sizeof(Win32.StandardInfo)))
            {
                alloc = info.AllocationSize;
                shared = info.NumberOfLinks > 1;
            }
        }

        var ex = Win32.DispositionDelete | Win32.DispositionPosixSemantics | Win32.DispositionIgnoreReadOnly;
        if (!Win32.SetFileInformationByHandle(h, Win32.FileDispositionInfoExClass, &ex, sizeof(uint)))
        {
            var e = Marshal.GetLastPInvokeError();
            if (e is not (Win32.ErrorInvalidParameter or Win32.ErrorNotSupported or Win32.ErrorInvalidFunction))
            {
                return Failed(path, e);
            }

            // 旧系统或不支持 POSIX 删除的文件系统：清掉只读属性，再用普通删除
            if ((tag.FileAttributes & Win32.FileAttributeReadOnly) != 0)
            {
                var basic = default(Win32.BasicInfo);
                basic.FileAttributes = tag.FileAttributes & ~Win32.FileAttributeReadOnly;
                if (basic.FileAttributes == 0)
                {
                    basic.FileAttributes = Win32.FileAttributeNormal;
                }

                Win32.SetFileInformationByHandle(h, Win32.FileBasicInfoClass, &basic, (uint)sizeof(Win32.BasicInfo));
            }

            byte delete = 1;
            if (!Win32.SetFileInformationByHandle(h, Win32.FileDispositionInfoClass, &delete, 1))
            {
                return Failed(path, Marshal.GetLastPInvokeError());
            }
        }

        if (directory && !reparse)
        {
            _outcome.DirsDeleted++;
        }
        else if (!reparse)
        {
            _outcome.FilesDeleted++;
            _outcome.BytesDeleted += alloc;
            if (shared)
            {
                _outcome.SharedBytes += alloc;
            }

            if (++_sinceReport >= 256)
            {
                _sinceReport = 0;
                _progress?.Report(new DeleteProgress(_outcome.FilesDeleted, _outcome.BytesDeleted, path));
            }
        }

        return true;
    }

    private bool Failed(string path, int error)
    {
        switch (error)
        {
            case Win32.ErrorFileNotFound or Win32.ErrorPathNotFound:
                return true; // 已经不在了
            case Win32.ErrorSharingViolation or Win32.ErrorLockViolation:
                _outcome.AddLocked(path);
                return false;
            case Win32.ErrorDirNotEmpty:
                return false; // 里面还有被跳过的内容
            default:
                _outcome.AddError(path, error);
                return false;
        }
    }

    /// <summary>
    /// 占住一个目录：打开句柄（不跟随重解析点、不允许删除共享），确认它是普通目录。
    /// 句柄存在期间这个目录不能被改名、删除或换成 junction。失败或它是重解析点时返回 null。
    /// </summary>
    private static SafeFileHandle? Hold(string path)
    {
        var ext = Win32.Extended(path);
        const uint access = Win32.FileListDirectory | Win32.FileReadAttributes | Win32.AccessSynchronize;
        var h = Win32.CreateFile(ext, access, Win32.ShareRead | Win32.ShareWrite, 0, Win32.OpenExisting,
            Win32.FlagBackupSemantics | Win32.FlagOpenReparsePoint, 0);
        if (h.IsInvalid && Marshal.GetLastPInvokeError() == Win32.ErrorSharingViolation)
        {
            // 别的程序以删除权限打开了这个目录：退而允许删除共享，只做类型确认
            h.Dispose();
            h = Win32.CreateFile(ext, access, Win32.ShareRead | Win32.ShareWrite | Win32.ShareDelete, 0, Win32.OpenExisting,
                Win32.FlagBackupSemantics | Win32.FlagOpenReparsePoint, 0);
        }

        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }

        Win32.AttributeTagInfo tag;
        if (!Win32.GetFileInformationByHandleEx(h, Win32.FileAttributeTagInfoClass, &tag, (uint)sizeof(Win32.AttributeTagInfo)) ||
            (tag.FileAttributes & Win32.FileAttributeReparsePoint) != 0 || (tag.FileAttributes & Win32.FileAttributeDirectory) == 0)
        {
            h.Dispose();
            Marshal.SetLastPInvokeError(Win32.ErrorAccessDenied);
            return null;
        }

        return h;
    }

    /// <summary>直接子项里最新的几个（目录按子树里最新的文件算，和扫描树的口径一致）。</summary>
    private HashSet<string> LatestChildren(string dir, int count)
    {
        var children = new List<(string Name, long Time)>();
        var h = Win32.FindFirst(Win32.Extended(dir) + @"\*", out var data);
        if (h != -1)
        {
            try
            {
                do
                {
                    var name = NameOf(ref data);
                    if (name is "." or "..")
                    {
                        continue;
                    }

                    var isDir = (data.FileAttributes & Win32.FileAttributeDirectory) != 0 && (data.FileAttributes & Win32.FileAttributeReparsePoint) == 0;
                    children.Add((name, isDir ? Math.Max(data.LastWriteUtc, NewestBelow(dir + "\\" + name, 0)) : data.LastWriteUtc));
                }
                while (Win32.FindNext(h, out data));
            }
            finally
            {
                Win32.FindClose(h);
            }
        }

        return children.OrderByDescending(c => c.Time).Take(count).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private long NewestBelow(string dir, int depth)
    {
        long newest = 0;
        if (depth > 64 || _token.IsCancellationRequested)
        {
            return newest;
        }

        var h = Win32.FindFirst(Win32.Extended(dir) + @"\*", out var data);
        if (h == -1)
        {
            return newest;
        }

        try
        {
            do
            {
                var name = NameOf(ref data);
                if (name is "." or "..")
                {
                    continue;
                }

                newest = Math.Max(newest, data.LastWriteUtc);
                if ((data.FileAttributes & Win32.FileAttributeDirectory) != 0 && (data.FileAttributes & Win32.FileAttributeReparsePoint) == 0)
                {
                    newest = Math.Max(newest, NewestBelow(dir + "\\" + name, depth + 1));
                }
            }
            while (Win32.FindNext(h, out data));
        }
        finally
        {
            Win32.FindClose(h);
        }

        return newest;
    }

    private static string NameOf(ref Win32.FindData data)
    {
        fixed (char* p = data.FileName)
        {
            return new string(p);
        }
    }
}

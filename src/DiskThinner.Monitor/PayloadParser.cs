using System.Buffers.Binary;
using System.Text;

namespace DiskThinner.Monitor;

/// <summary>
/// 按字节读 Kernel-File / Kernel-Process 的载荷（docs/phase2a-design.md 5.4 节）。每个（事件号, 版本）的字段顺序手写，
/// 和系统清单导出的 tests/fixtures/monitor/templates.txt 核对（TemplateConformanceTests）。不认识的事件号、版本返回 false。
/// </summary>
public static class PayloadParser
{
    private const int MaxStringChars = 32_767;

    public static bool TryParse(in RawEvent raw, MonitorEvent e)
    {
        e.ProcessId = raw.ProcessId;
        e.ThreadId = raw.ThreadId;
        e.TimestampUtcTicks = raw.TimestampUtcTicks;
        e.Path = null;
        e.ImageName = null;
        e.FileKey = e.Irp = e.ByteOffset = e.Sequence = e.ParentSequence = 0;
        e.IoSize = e.IoFlags = e.CreateOptions = 0;
        var r = new Reader(raw.Payload, raw.PointerSize);
        if (raw.Provider == Providers.KernelFile)
        {
            return ParseFile(ref r, raw.Id, raw.Version, e);
        }

        if (raw.Provider == Providers.KernelProcess)
        {
            return ParseProcess(ref r, raw.Id, raw.Version, e);
        }

        return false;
    }

    /// <summary>这个（提供程序, 事件号, 版本）有手写的模板。</summary>
    public static bool Supports(Guid provider, int id, int version) =>
        provider == Providers.KernelFile ? id is 10 or 11 && version == 0 || id is 16 or 26 or 27 or 28 or 30 && version is 0 or 1
        : provider == Providers.KernelProcess && (id == 1 && version is >= 0 and <= 4 || id is 2 or 15 && version is >= 0 and <= 2);

    private static bool ParseFile(ref Reader r, int id, int version, MonitorEvent e)
    {
        switch (id)
        {
            case 10 or 11 when version == 0:
            {
                e.Kind = id == 10 ? MonitorEventKind.NameCreate : MonitorEventKind.NameDelete;
                if (!r.Pointer(out var key) || !r.Unicode(out var name))
                {
                    return false;
                }

                e.FileKey = key;
                e.Path = name;
                return true;
            }

            case 16 when version == 0:
                e.Kind = MonitorEventKind.Write;
                return r.U64(out var off0) && r.Pointer(out var irp0) && r.Pointer(out _) && r.Pointer(out _) && r.Pointer(out var fk0)
                    && r.U32(out var size0) && r.U32(out var flags0) && SetWrite(e, off0, irp0, fk0, size0, flags0);

            case 16 when version == 1:
                e.Kind = MonitorEventKind.Write;
                return r.U64(out var off1) && r.Pointer(out var irp1) && r.Pointer(out _) && r.Pointer(out var fk1) && r.U32(out _)
                    && r.U32(out var size1) && r.U32(out var flags1) && SetWrite(e, off1, irp1, fk1, size1, flags1);

            case 26 or 27 or 28 when version is 0 or 1:
            {
                e.Kind = id switch { 26 => MonitorEventKind.DeletePath, 27 => MonitorEventKind.RenamePath, _ => MonitorEventKind.SetLinkPath };
                ulong irp = 0, fk = 0;
                bool ok = version == 0
                    ? r.Pointer(out irp) && r.Pointer(out _) && r.Pointer(out _) && r.Pointer(out fk) && r.Pointer(out _) && r.U32(out _)
                    : r.Pointer(out irp) && r.Pointer(out _) && r.Pointer(out fk) && r.Pointer(out _) && r.U32(out _) && r.U32(out _);
                if (!ok || !r.Unicode(out var path))
                {
                    return false;
                }

                e.Irp = irp;
                e.FileKey = fk;
                e.Path = path;
                return true;
            }

            case 30 when version is 0 or 1:
            {
                e.Kind = MonitorEventKind.CreateNewFile;
                ulong irp = 0;
                uint options = 0;
                bool ok = version == 0
                    ? r.Pointer(out irp) && r.Pointer(out _) && r.Pointer(out _) && r.U32(out options) && r.U32(out _) && r.U32(out _)
                    : r.Pointer(out irp) && r.Pointer(out _) && r.U32(out _) && r.U32(out options) && r.U32(out _) && r.U32(out _);
                if (!ok || !r.Unicode(out var path))
                {
                    return false;
                }

                e.Irp = irp;
                e.CreateOptions = options;
                e.Path = path;
                return true;
            }

            default:
                return false;
        }
    }

    private static bool ParseProcess(ref Reader r, int id, int version, MonitorEvent e)
    {
        // ProcessRundown v0/v1/v2 的字段和 ProcessStart v2/v3/v4 一样
        if (id == 15)
        {
            if (version is < 0 or > 2)
            {
                return false;
            }

            e.Kind = MonitorEventKind.ProcessRundown;
            return ParseStart(ref r, version + 2, e);
        }

        if (id == 1)
        {
            e.Kind = MonitorEventKind.ProcessStart;
            return version is >= 0 and <= 4 && ParseStart(ref r, version, e);
        }

        if (id == 2 && version is >= 0 and <= 2)
        {
            e.Kind = MonitorEventKind.ProcessStop;
            if (!r.U32(out var pid))
            {
                return false;
            }

            ulong seq = 0;
            if (version == 2 && !r.U64(out seq))
            {
                return false;
            }

            if (!r.U64(out var create) || !r.U64(out var exit) || !r.U32(out _) || !r.U32(out _) || !r.U32(out _) || !r.U64(out _) || !r.U64(out _))
            {
                return false;
            }

            if (version >= 1 && !(r.U64(out _) && r.U32(out _) && r.U32(out _) && r.U32(out _) && r.U32(out _) && r.U32(out _)))
            {
                return false;
            }

            if (!r.Ansi(out var image))
            {
                return false;
            }

            e.TargetProcessId = (int)pid;
            e.Sequence = seq;
            e.CreateTimeUtcTicks = FileTimeTicks(create);
            e.ExitTimeUtcTicks = FileTimeTicks(exit);
            e.ImageName = image;
            return true;
        }

        return false;
    }

    private static bool ParseStart(ref Reader r, int version, MonitorEvent e)
    {
        uint pid, ppid;
        ulong seq = 0, parentSeq = 0, create;
        if (version <= 2)
        {
            if (!(r.U32(out pid) && r.U64(out create) && r.U32(out ppid) && r.U32(out _)))
            {
                return false;
            }

            if (version >= 1 && !r.U32(out _))
            {
                return false;
            }
        }
        else if (!(r.U32(out pid) && r.U64(out seq) && r.U64(out create) && r.U32(out ppid) && r.U64(out parentSeq)
                   && r.U32(out _) && r.U32(out _) && r.U32(out _) && r.U32(out _) && r.Sid()))
        {
            return false;
        }

        if (!r.Unicode(out var image))
        {
            return false;
        }

        e.TargetProcessId = (int)pid;
        e.ParentProcessId = (int)ppid;
        e.Sequence = seq;
        e.ParentSequence = parentSeq;
        e.CreateTimeUtcTicks = FileTimeTicks(create);
        e.ImageName = image;
        return true;
    }

    private const long FileTimeEpoch = 504911232000000000L; // 0001-01-01 到 1601-01-01 的刻度数

    private static long FileTimeTicks(ulong fileTime) =>
        fileTime == 0 || fileTime > (ulong)(DateTime.MaxValue.Ticks - FileTimeEpoch) ? 0 : (long)fileTime + FileTimeEpoch;

    private static bool SetWrite(MonitorEvent e, ulong offset, ulong irp, ulong key, uint size, uint flags)
    {
        e.ByteOffset = offset;
        e.Irp = irp;
        e.FileKey = key;
        e.IoSize = size;
        e.IoFlags = flags;
        return true;
    }

    private ref struct Reader(ReadOnlySpan<byte> data, int pointerSize)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public bool U32(out uint v)
        {
            if (_pos + 4 > _data.Length)
            {
                v = 0;
                return false;
            }

            v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
            _pos += 4;
            return true;
        }

        public bool U64(out ulong v)
        {
            if (_pos + 8 > _data.Length)
            {
                v = 0;
                return false;
            }

            v = BinaryPrimitives.ReadUInt64LittleEndian(_data[_pos..]);
            _pos += 8;
            return true;
        }

        public bool Pointer(out ulong v)
        {
            if (pointerSize == 8)
            {
                return U64(out v);
            }

            var ok = U32(out var v32);
            v = v32;
            return ok;
        }

        public bool Unicode(out string s)
        {
            var rest = _data[_pos..];
            for (var i = 0; i + 1 < rest.Length && i / 2 <= MaxStringChars; i += 2)
            {
                if (rest[i] == 0 && rest[i + 1] == 0)
                {
                    s = Encoding.Unicode.GetString(rest[..i]);
                    _pos += i + 2;
                    return true;
                }
            }

            s = "";
            return false;
        }

        public bool Ansi(out string s)
        {
            var rest = _data[_pos..];
            var end = rest.IndexOf((byte)0);
            if (end < 0 || end > MaxStringChars)
            {
                s = "";
                return false;
            }

            s = Encoding.ASCII.GetString(rest[..end]);
            _pos += end + 1;
            return true;
        }

        /// <summary>按 winnt.h 的 _SID 布局跳过：Revision(1) + SubAuthorityCount(1) + IdentifierAuthority(6) + SubAuthority(4×n)，n ≤ 15。</summary>
        public bool Sid()
        {
            if (_pos + 8 > _data.Length)
            {
                return false;
            }

            var n = _data[_pos + 1];
            if (n > 15 || _pos + 8 + 4 * n > _data.Length)
            {
                return false;
            }

            _pos += 8 + 4 * n;
            return true;
        }
    }
}

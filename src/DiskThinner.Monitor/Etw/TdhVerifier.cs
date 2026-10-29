using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DiskThinner.Monitor.Etw;

/// <summary>
/// 解析器的差分核对（--verify-parser，docs/phase2a-design.md 5.4 节，2b 改用 TDH：docs/phase2b-design.md 5.1 节）：
/// 每个（提供程序, 事件号, 版本）的前 200 个事件，用 TdhGetProperty 按名字取用到的字段（系统按清单解码），和手写解析器的结果比对。
/// 整数按原始字节当无符号数比较（哨兵偏移 0xFFFFFFFFFFFFFFFF 不会被当成有符号数），字符串按序数比较。
/// 这个版本的清单里没有的字段跳过。只在泵线程上调用，不加锁。
/// </summary>
public sealed unsafe class TdhVerifier
{
    public const int PerKind = 200;

    private readonly Dictionary<(Guid, int, int), int> _seen = [];

    /// <summary>核对不一致的事件数。</summary>
    public long Mismatches { get; private set; }

    /// <summary>至少核对上一个字段的事件数（清单读不到时为 0，核对就是空的）。</summary>
    public long Verified { get; private set; }

    /// <summary>第一处不一致：提供程序/事件号/版本:字段（不记值，值里可能有路径）。</summary>
    public string? FirstMismatch { get; private set; }

    public void Check(in RawEvent raw, EtwRecord record)
    {
        var key = (raw.Provider, raw.Id, raw.Version);
        var n = _seen.GetValueOrDefault(key);
        if (n >= PerKind || !PayloadParser.Supports(raw.Provider, raw.Id, raw.Version))
        {
            return;
        }

        _seen[key] = n + 1;
        var parsed = new MonitorEvent();
        if (!PayloadParser.TryParse(raw, parsed))
        {
            Mismatch(raw, "parse");
            return;
        }

        (string Field, object? Ours)[] fields = parsed.Kind switch
        {
            MonitorEventKind.NameCreate or MonitorEventKind.NameDelete => [("FileKey", parsed.FileKey), ("FileName", parsed.Path)],
            MonitorEventKind.Write => [("FileKey", parsed.FileKey), ("ByteOffset", parsed.ByteOffset), ("IOSize", parsed.IoSize), ("IOFlags", parsed.IoFlags)],
            MonitorEventKind.CreateNewFile => [("CreateOptions", parsed.CreateOptions), ("FileName", parsed.Path)],
            MonitorEventKind.DeletePath or MonitorEventKind.RenamePath or MonitorEventKind.SetLinkPath => [("FileKey", parsed.FileKey), ("FilePath", parsed.Path)],
            MonitorEventKind.ProcessStart or MonitorEventKind.ProcessRundown =>
                [("ProcessID", (uint)parsed.TargetProcessId), ("ParentProcessID", (uint)parsed.ParentProcessId), ("ImageName", parsed.ImageName), ("ProcessSequenceNumber", parsed.Sequence)],
            // ProcessStop 的 ImageName 在清单里是 win:AnsiString（EPROCESS 里 15 个字符的映像名），其他字符串都是 UTF-16
            MonitorEventKind.ProcessStop => [("ProcessID", (uint)parsed.TargetProcessId), ("ImageName", parsed.ImageName)],
            _ => [],
        };
        var ansi = parsed.Kind == MonitorEventKind.ProcessStop;

        var compared = 0;
        string? failed = null;
        foreach (var (field, ours) in fields)
        {
            var result = Compare(record, field, ours, ansi);
            if (result is null)
            {
                continue; // 这个版本没有这个字段
            }

            compared++;
            if (result == false)
            {
                failed ??= field;
            }
        }

        if (compared > 0)
        {
            Verified++;
        }

        if (failed is not null)
        {
            Mismatch(raw, failed);
        }
    }

    private void Mismatch(in RawEvent raw, string field)
    {
        Mismatches++;
        FirstMismatch ??= $"{(raw.Provider == Providers.KernelFile ? "Kernel-File" : "Kernel-Process")}/{raw.Id}/v{raw.Version}:{field}";
    }

    /// <summary>null：清单里没有这个字段；true / false：一致 / 不一致。</summary>
    private static bool? Compare(EtwRecord record, string field, object? ours, bool ansi)
    {
        var bytes = Property(record, field);
        if (bytes is null)
        {
            return null;
        }

        if (ours is string or null)
        {
            return string.Equals(ansi ? Ansi(bytes) : Utf16(bytes), ours as string, StringComparison.Ordinal);
        }

        ulong theirs = bytes.Length switch
        {
            1 => bytes[0],
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            8 => BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            _ => ulong.MaxValue - 1,
        };
        ulong? mine = ours switch
        {
            ulong u => u,
            long l => unchecked((ulong)l),
            uint u => u,
            int i => unchecked((uint)i),
            ushort u => u,
            byte b => b,
            _ => null,
        };
        return mine is { } m && m == theirs;
    }

    private static string Ansi(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        return System.Text.Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static string Utf16(byte[] bytes)
    {
        var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(0, bytes.Length & ~1));
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    /// <summary>TdhGetPropertySize + TdhGetProperty；字段不存在、取不到时返回 null。</summary>
    internal static byte[]? Property(EtwRecord record, string name)
    {
        fixed (char* n = name)
        {
            var d = new NativeEtw.PropertyDataDescriptor { PropertyName = (ulong)n, ArrayIndex = uint.MaxValue };
            uint size = 0;
            if (NativeEtw.TdhGetPropertySize(record.Record, 0, null, 1, &d, &size) != NativeEtw.ErrorSuccess || size == 0 || size > 65_536)
            {
                return null;
            }

            var buffer = new byte[size];
            fixed (byte* b = buffer)
            {
                return NativeEtw.TdhGetProperty(record.Record, 0, null, 1, &d, size, b) == NativeEtw.ErrorSuccess ? buffer : null;
            }
        }
    }
}

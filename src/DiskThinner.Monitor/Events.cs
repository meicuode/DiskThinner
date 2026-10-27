namespace DiskThinner.Monitor;

/// <summary>写入监控用到的两个提供程序和关键字（docs/phase2a-design.md 2.1、5.2 节）。</summary>
public static class Providers
{
    public static readonly Guid KernelFile = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    public static readonly Guid KernelProcess = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");

    /// <summary>CREATE_NEW_FILE | DELETE_PATH | RENAME_SETLINK_PATH。</summary>
    public const ulong LightKeywords = 0x1C00;

    /// <summary>轻量 + WRITE | FILENAME。</summary>
    public const ulong PreciseKeywords = 0x1E10;

    /// <summary>WINEVENT_KEYWORD_PROCESS。</summary>
    public const ulong ProcessKeyword = 0x10;
}

/// <summary>记录模式。</summary>
public enum MonitorMode : byte
{
    /// <summary>只记录新建、删除、重命名。</summary>
    Light,

    /// <summary>另外记录每次写入。</summary>
    Precise,
}

/// <summary>一个原始事件：事件源（TraceEvent 或以后的 ETW API）直接构造，不复制载荷。</summary>
public readonly ref struct RawEvent
{
    public RawEvent(Guid provider, int id, int version, int processId, int threadId, long timestampUtcTicks, int pointerSize, ReadOnlySpan<byte> payload)
    {
        Provider = provider;
        Id = id;
        Version = version;
        ProcessId = processId;
        ThreadId = threadId;
        TimestampUtcTicks = timestampUtcTicks;
        PointerSize = pointerSize;
        Payload = payload;
    }

    public Guid Provider { get; }
    public int Id { get; }
    public int Version { get; }
    public int ProcessId { get; }
    public int ThreadId { get; }
    public long TimestampUtcTicks { get; }
    public int PointerSize { get; }
    public ReadOnlySpan<byte> Payload { get; }
}

public enum MonitorEventKind : byte
{
    NameCreate,
    NameDelete,
    Write,
    DeletePath,
    RenamePath,
    SetLinkPath,
    CreateNewFile,
    ProcessStart,
    ProcessStop,
    ProcessRundown,
}

/// <summary>解析后的事件。文件事件的路径是设备路径（\Device\HarddiskVolumeN\…），换成盘符在汇总时做。</summary>
public sealed class MonitorEvent
{
    public MonitorEventKind Kind { get; set; }

    /// <summary>事件头里的进程号（发起操作的进程，V1）。</summary>
    public int ProcessId { get; set; }

    public int ThreadId { get; set; }
    public long TimestampUtcTicks { get; set; }

    // ---- 文件 ----
    public ulong FileKey { get; set; }
    public ulong Irp { get; set; }
    public ulong ByteOffset { get; set; }
    public uint IoSize { get; set; }
    public uint IoFlags { get; set; }
    public uint CreateOptions { get; set; }

    /// <summary>NameCreate / NameDelete / CreateNewFile 的 FileName，DeletePath / RenamePath / SetLinkPath 的 FilePath。</summary>
    public string? Path { get; set; }

    // ---- 进程（ProcessStart / ProcessStop / ProcessRundown） ----
    public int TargetProcessId { get; set; }
    public int ParentProcessId { get; set; }
    public long CreateTimeUtcTicks { get; set; }
    public long ExitTimeUtcTicks { get; set; }
    public ulong Sequence { get; set; }
    public ulong ParentSequence { get; set; }
    public string? ImageName { get; set; }

    public MonitorEvent Clone() => (MonitorEvent)MemberwiseClone();

    /// <summary>非分页写入（IRP_PAGING_IO 0x2 没有置位）。</summary>
    public bool IsPagingWrite => (IoFlags & 0x2) != 0;

    /// <summary>快速 I/O 路径的写入（IOFlags 为 0）。失败时会再发一个同样的 IRP 写入重试（V2，2a.1 实测）。</summary>
    public bool IsFastIoWrite => IoFlags == 0;
}

namespace DiskThinner.Scanner;

/// <summary>
/// 一个文件有多个硬链接名时，大小算在哪个名字所在的目录里（其余的名字标为 <see cref="NodeFlags.HardLinkSecondary"/>）。
/// 只影响各目录之间怎么分，总量不变。
/// </summary>
public enum HardLinkAttribution
{
    /// <summary>MFT 记录里的第一个文件名。</summary>
    FirstName,

    /// <summary>MFT 记录里的最后一个文件名。</summary>
    LastName,

    /// <summary>所在目录的 MFT 记录号最小的那个名字。</summary>
    LowestParentRecord,

    /// <summary>
    /// 优先算在 Windows\WinSxS 以外。组件存储里大量文件和 System32 等目录共用，
    /// 这样 WinSxS 显示的就是它独有的部分，与 DISM /AnalyzeComponentStore 的口径一致。
    /// </summary>
    OutsideComponentStore,
}

public enum ScanMode
{
    /// <summary>直接读取 NTFS 主文件表，需要管理员权限。</summary>
    Mft,

    /// <summary>逐个目录列出内容，普通权限可用；硬链接会重复计算，分配大小为估算值。</summary>
    Walk,
}

public sealed class ScanStats
{
    public ScanMode Mode { get; init; }
    public string Root { get; init; } = "";
    public TimeSpan ReadDuration { get; set; }
    public TimeSpan BuildDuration { get; set; }
    public TimeSpan FinalizeDuration { get; set; }
    public TimeSpan TotalDuration => ReadDuration + BuildDuration + FinalizeDuration;

    public long VolumeTotalBytes { get; set; }
    public long VolumeFreeBytes { get; set; }
    public int ClusterSize { get; set; }

    // MFT 专用
    public long MftRecords { get; set; }
    public long MftRecordsInUse { get; set; }
    public long MftBadRecords { get; set; }
    public long MftMalformedRuns { get; set; }
    public long MftBytesRead { get; set; }
    public int MftRecordSize { get; set; }
    public int MftDataSegments { get; set; }
    public long AlternateStreamBytes { get; set; }
    public long DirectoryIndexBytes { get; set; }
    public int HardLinkSecondaryNames { get; set; }

    // 遍历专用
    public int AccessDeniedDirectories { get; set; }
    public int ReparsePointDirectories { get; set; }
    public int ErrorDirectories { get; set; }

    public int Nodes { get; set; }
    public int Unreachable { get; set; }

    /// <summary>名称池的已用字符数和容量（MFT），用于调整预分配大小。</summary>
    public long NameChars { get; set; }
    public long NameCharsCapacity { get; set; }

    /// <summary>没有使用 MFT 扫描的原因（为空表示用了 MFT，或者调用方本来就要求遍历）。</summary>
    public string? MftUnavailableReason { get; set; }
}

public sealed record ScanResult(NodeTree Tree, ScanStats Stats);

/// <summary>
/// 扫描进度。MFT 扫描：Done/Total 是记录数。遍历扫描：Done 是已列出的目录数，Total 为 0（总数未知），
/// Bytes 是已统计的文件大小，可以和卷已用空间相比估算进度。
/// </summary>
public readonly record struct ScanProgress(long Done, long Total, string Phase, long Bytes = 0);

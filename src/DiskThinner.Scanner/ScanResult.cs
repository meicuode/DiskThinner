namespace DiskThinner.Scanner;

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
}

public sealed record ScanResult(NodeTree Tree, ScanStats Stats);

public readonly record struct ScanProgress(long Done, long Total, string Phase);

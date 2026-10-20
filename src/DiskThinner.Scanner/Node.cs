using System.Runtime.InteropServices;

namespace DiskThinner.Scanner;

[Flags]
public enum NodeFlags : ushort
{
    None = 0,
    Directory = 1 << 0,
    ReparsePoint = 1 << 1,      // junction / 符号链接 / 云文件占位符等，目录不展开
    HardLinkSecondary = 1 << 2, // 同一文件的第二个及以后的硬链接名
    Hidden = 1 << 3,
    System = 1 << 4,
    Metadata = 1 << 5,          // NTFS 元数据文件（$MFT、$LogFile、$Extend 下的内容等）
    Compressed = 1 << 6,
    Sparse = 1 << 7,
    CloudPlaceholder = 1 << 8,  // 数据不在本地（OneDrive 等按需下载的文件）
    AccessDenied = 1 << 9,      // 遍历扫描时无法列出内容的目录
    Incomplete = 1 << 10,       // 子树里有无法列出的目录，汇总值偏小
    Orphan = 1 << 11,           // MFT 中找不到父目录的记录
    HardLinked = 1 << 12,       // 这个文件有多个硬链接名（主名字和其他名字都带这个标记；只有 MFT 扫描能识别）
}

/// <summary>
/// 紧凑的树节点。用结构体数组存储，节点之间用下标互相引用，避免百万级对象带来的内存和 GC 开销。
/// 字段按大小排列，结构体共 56 字节。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Node
{
    /// <summary>逻辑大小。目录为整棵子树的汇总。</summary>
    public long LogicalSize;

    /// <summary>实际占用的磁盘空间。目录为整棵子树的汇总。</summary>
    public long AllocSize;

    /// <summary>最后写入时间（UTC FILETIME）。目录取自身与子树中的最大值。</summary>
    public long LastWriteUtc;

    public int Parent;
    public int FirstChild;
    public int NextSibling;
    public int NameOffset;

    /// <summary>子树中的文件数（每个硬链接名都算一个）。文件自身为 0。</summary>
    public int FileCount;

    /// <summary>子树中的目录数，不含自身。</summary>
    public int DirCount;

    /// <summary>规则匹配等后续阶段使用的附加索引，-1 表示无。</summary>
    public int Tag;

    public ushort NameLength;
    public NodeFlags Flags;

    public readonly bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
}

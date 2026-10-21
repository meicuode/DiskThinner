namespace DiskThinner.Storage;

/// <summary>
/// 快照剪枝的阈值（研发方案 3.5 节）：只记录 50 MB 以上的目录和 100 MB 以上的文件，深度不超过 32 层，
/// 更小、更深的部分并入上级目录的"未单独记录部分"。父目录不小于子目录，所以剪枝后仍是一棵连通的树。
/// </summary>
public sealed record SnapshotOptions(long MinDirBytes = SnapshotOptions.DefaultMinDirBytes, long MinFileBytes = SnapshotOptions.DefaultMinFileBytes, int MaxDepth = 32)
{
    public const long DefaultMinDirBytes = 50L << 20;
    public const long DefaultMinFileBytes = 100L << 20;

    public static readonly SnapshotOptions Default = new();

    /// <summary>不剪枝：记录每个节点（测试里和剪枝后的结果对照）。</summary>
    public static readonly SnapshotOptions Everything = new(0, 0, 4096);
}

/// <summary>一次快照的概要。</summary>
public sealed record SnapshotInfo(
    long Id,
    DateTime TakenAtUtc,
    string Volume,
    long TotalBytes,
    long FreeBytes,
    string ScanMode,
    long MinDirBytes,
    long MinFileBytes,
    long RootBytes,
    int Entries)
{
    /// <summary>已用空间：整个盘的快照取卷的已用空间，文件夹快照取扫描到的总量。</summary>
    public long UsedBytes => TotalBytes > 0 && IsVolume ? TotalBytes - FreeBytes : RootBytes;

    /// <summary>扫描的是整个盘（"C:\"），不是一个文件夹。</summary>
    public bool IsVolume => Volume.Length <= 3;

    /// <summary>这个节点在这次快照里是凭自己的大小被记录的（不是"上次记录过、这次再记一次"）。</summary>
    internal bool MeetsThreshold(bool isDir, long bytes) => isDir ? bytes >= MinDirBytes : bytes >= MinFileBytes;
}

/// <summary>快照里的一项。</summary>
internal sealed class SnapshotEntry
{
    public long PathId;
    public int Parent = -1;
    public required string Name;
    public bool IsDir;
    public long Alloc;
    public int FileCount;
    public long LastWrite;

    /// <summary>目录自身大小减去已记录子项之和（更小、更深的部分）。文件为 0。</summary>
    public long Unrecorded;

    public List<int>? Children;
}

/// <summary>剪枝后的快照树。下标 0 是根；父项总在子项前面。</summary>
public sealed class SnapshotTree
{
    private readonly List<SnapshotEntry> _entries;

    internal SnapshotTree(SnapshotInfo info, List<SnapshotEntry> entries)
    {
        Info = info;
        _entries = entries;
        foreach (var e in entries)
        {
            e.Children = null;
        }

        for (var i = 1; i < entries.Count; i++)
        {
            var p = entries[i].Parent;
            (entries[p].Children ??= []).Add(i);
        }
    }

    public SnapshotInfo Info { get; }

    public int Count => _entries.Count;

    internal SnapshotEntry this[int index] => _entries[index];

    public long PathId(int i) => _entries[i].PathId;

    public int Parent(int i) => _entries[i].Parent;

    public string Name(int i) => _entries[i].Name;

    public bool IsDirectory(int i) => _entries[i].IsDir;

    public long Alloc(int i) => _entries[i].Alloc;

    public int FileCount(int i) => _entries[i].FileCount;

    public long Unrecorded(int i) => _entries[i].Unrecorded;

    public IReadOnlyList<int> Children(int i) => (IReadOnlyList<int>?)_entries[i].Children ?? [];

    public string GetPath(int i)
    {
        var parts = new List<string>();
        for (var n = i; n >= 0; n = _entries[n].Parent)
        {
            parts.Add(_entries[n].Name);
        }

        parts.Reverse();
        return JoinPath(parts);
    }

    internal static string JoinPath(List<string> parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (sb.Length > 0 && sb[^1] != '\\')
            {
                sb.Append('\\');
            }

            sb.Append(p);
        }

        return sb.ToString();
    }

    /// <summary>按完整路径查找（不区分大小写）；找不到返回 -1。</summary>
    public int Find(string path)
    {
        var root = _entries[0].Name.TrimEnd('\\');
        var span = path.AsSpan().TrimEnd('\\');
        if (!span.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var current = 0;
        foreach (var part in span[root.Length..].ToString().Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = -1;
            foreach (var c in Children(current))
            {
                if (string.Equals(_entries[c].Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    next = c;
                    break;
                }
            }

            if (next < 0)
            {
                return -1;
            }

            current = next;
        }

        return current;
    }
}

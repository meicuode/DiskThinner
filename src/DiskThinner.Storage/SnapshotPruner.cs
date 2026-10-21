using DiskThinner.Scanner;

namespace DiskThinner.Storage;

/// <summary>路径编号：同一个目录在不同快照里用同一个编号，对比时按编号连接。</summary>
internal interface IPathIds
{
    /// <summary>已有的编号；没有时返回 null。</summary>
    long? Find(long parentId, string name);

    /// <summary>已有的编号；没有时新建一个。</summary>
    long GetOrAdd(long parentId, string name);
}

/// <summary>只读的路径编号：新路径给一个负数编号（只在内存里用，和已有快照里的任何路径都对不上）。</summary>
internal sealed class TransientPathIds(Dictionary<(long, string), long> known) : IPathIds
{
    private readonly Dictionary<(long, string), long> _added = [];
    private long _next = -1;

    public long? Find(long parentId, string name)
    {
        var key = (parentId, PathKey(name));
        return known.TryGetValue(key, out var id) || _added.TryGetValue(key, out id) ? id : null;
    }

    public long GetOrAdd(long parentId, string name)
    {
        if (Find(parentId, name) is { } id)
        {
            return id;
        }

        _added[(parentId, PathKey(name))] = _next;
        return _next--;
    }

    internal static string PathKey(string name) => name.ToUpperInvariant();
}

/// <summary>把扫描得到的完整树剪成快照（研发方案 3.5 节）。</summary>
internal static class SnapshotPruner
{
    /// <summary>
    /// 从根往下走：目录不小于阈值、文件不小于阈值的子项记下来，并继续看记下来的目录的子项；
    /// 上次快照里凭自己大小记录过的路径（<paramref name="rerecord"/>），这次低于阈值也再记一次，这样"从 2 GB 降到 10 MB"才看得到。
    /// 每个记下来的目录另存"未单独记录部分" = 自身大小 − 已记录子项之和。
    /// </summary>
    /// <param name="rerecord">要再记一次的路径：父目录的路径编号 → 子项名称（不区分大小写）。按名称查，不用为每个子项生成字符串。</param>
    public static List<SnapshotEntry> Prune(NodeTree tree, SnapshotOptions options, IPathIds ids, IReadOnlyDictionary<long, HashSet<string>> rerecord)
    {
        var entries = new List<SnapshotEntry>();
        var root = tree.Root;
        ref var r = ref tree[root];
        var rootName = tree.GetNameString(root);
        entries.Add(new SnapshotEntry
        {
            PathId = ids.GetOrAdd(0, rootName),
            Name = rootName,
            IsDir = r.IsDirectory,
            Alloc = Effective(tree, root),
            FileCount = r.FileCount,
            LastWrite = r.LastWriteUtc,
        });

        var stack = new Stack<(int Node, int Entry, int Depth)>();
        stack.Push((root, 0, 0));
        while (stack.Count > 0)
        {
            var (n, ei, depth) = stack.Pop();
            var parent = entries[ei];
            long recorded = 0;
            if (depth < options.MaxDepth)
            {
                var hasRerecord = rerecord.TryGetValue(parent.PathId, out var names);
                var lookup = hasRerecord ? names!.GetAlternateLookup<ReadOnlySpan<char>>() : default;
                for (var c = tree[n].FirstChild; c >= 0; c = tree[c].NextSibling)
                {
                    ref var child = ref tree[c];
                    var bytes = Effective(tree, c);
                    var isDir = child.IsDirectory;
                    var meets = isDir ? bytes >= options.MinDirBytes : bytes >= options.MinFileBytes;
                    if (!meets && (!hasRerecord || !lookup.Contains(tree.GetName(c))))
                    {
                        continue;
                    }

                    var name = tree.GetNameString(c);
                    var entry = new SnapshotEntry
                    {
                        PathId = ids.GetOrAdd(parent.PathId, name),
                        Parent = ei,
                        Name = name,
                        IsDir = isDir,
                        Alloc = bytes,
                        FileCount = child.FileCount,
                        LastWrite = child.LastWriteUtc,
                    };
                    entries.Add(entry);
                    recorded += bytes;
                    if (isDir && child.FirstChild >= 0)
                    {
                        stack.Push((c, entries.Count - 1, depth + 1));
                    }
                }
            }

            if (parent.IsDir)
            {
                parent.Unrecorded = parent.Alloc - recorded;
            }
        }

        return entries;
    }

    /// <summary>和树的汇总口径一致：只算一次的硬链接副本不计大小。</summary>
    private static long Effective(NodeTree tree, int node)
    {
        ref var n = ref tree[node];
        return tree.CountHardLinksOnce && !n.IsDirectory && (n.Flags & NodeFlags.HardLinkSecondary) != 0 ? 0 : n.AllocSize;
    }
}

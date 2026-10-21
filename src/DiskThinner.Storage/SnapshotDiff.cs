namespace DiskThinner.Storage;

/// <summary>"变化最大的目录"里的一项。</summary>
public sealed record GrowthHotspot(
    string Path,
    long Change,
    long Increase,
    long Decrease,
    long Current,
    long Previous,
    bool IsDirectory,
    bool IsRemainder);

/// <summary>
/// 两次快照的对比和热点分解（研发方案 3.5 节）。两棵树按路径编号合并：
///   文件 S = |Δ 大小|；目录 S = Σ S(子项) + |Δ 未单独记录部分|。用绝对值，一增一减不会互相抵消。
/// 只在一边被记录的节点（另一边低于记录阈值，或者那时还不存在）：比热点阈值小时并入上级的"未单独记录部分"，
/// 这样不会出现"子项按 0 算增加、上级又按减少算一遍"的重复计数；比热点阈值大时保留，另一边按 0 算（误差不超过记录阈值）。
/// </summary>
public sealed class SnapshotDiff
{
    public const long DefaultThreshold = 100L << 20;

    private readonly List<Node> _nodes = [];

    private sealed class Node
    {
        public required string Name;
        public int Parent = -1;
        public List<int> Children = [];
        public bool IsDir;
        public long Old;
        public long New;
        public long OldUnrecorded;
        public long NewUnrecorded;
        public long S;
        public long Increase;
        public long Decrease;

        public long Change => New - Old;
    }

    private SnapshotDiff(SnapshotTree from, SnapshotTree to)
    {
        From = from;
        To = to;
    }

    public SnapshotTree From { get; }

    public SnapshotTree To { get; }

    /// <summary>整个范围的净变化（扫描总量之差）。</summary>
    public long NetChange => _nodes[0].Change;

    public long Increase => _nodes[0].Increase;

    public long Decrease => _nodes[0].Decrease;

    /// <summary>文件数的变化（每个硬链接名都算一个）。</summary>
    public long FileCountChange => (long)To.FileCount(0) - From.FileCount(0);

    /// <param name="foldBelow">只在一边记录、且小于这个大小的节点并入上级（默认等于热点阈值）。</param>
    public static SnapshotDiff Compute(SnapshotTree from, SnapshotTree to, long foldBelow = DefaultThreshold)
    {
        if (from.PathId(0) != to.PathId(0) && !string.Equals(from.Name(0), to.Name(0), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("两次快照不是同一个盘或同一个文件夹");
        }

        var diff = new SnapshotDiff(from, to);
        diff.Merge(0, 0, -1, foldBelow);
        diff.Sum(0);
        return diff;
    }

    private int Merge(int f, int t, int parent, long foldBelow)
    {
        var node = new Node
        {
            Name = t >= 0 ? To.Name(t) : From.Name(f),
            Parent = parent,
            IsDir = t >= 0 ? To.IsDirectory(t) : From.IsDirectory(f),
            Old = f >= 0 ? From.Alloc(f) : 0,
            New = t >= 0 ? To.Alloc(t) : 0,
            OldUnrecorded = f >= 0 ? From.Unrecorded(f) : 0,
            NewUnrecorded = t >= 0 ? To.Unrecorded(t) : 0,
        };
        var index = _nodes.Count;
        _nodes.Add(node);

        // 子项按路径编号配对；"当前扫描"里的新路径编号是负数，只会出现在 To 里
        var pairs = new Dictionary<long, (int F, int T)>();
        if (f >= 0)
        {
            foreach (var c in From.Children(f))
            {
                pairs[From.PathId(c)] = (c, -1);
            }
        }

        if (t >= 0)
        {
            foreach (var c in To.Children(t))
            {
                var id = To.PathId(c);
                pairs[id] = pairs.TryGetValue(id, out var p) ? (p.F, c) : (-1, c);
            }
        }

        foreach (var (fc, tc) in pairs.Values)
        {
            if (tc < 0 && From.Alloc(fc) < foldBelow)
            {
                node.OldUnrecorded += From.Alloc(fc);
                continue;
            }

            if (fc < 0 && To.Alloc(tc) < foldBelow)
            {
                node.NewUnrecorded += To.Alloc(tc);
                continue;
            }

            node.Children.Add(Merge(fc, tc, index, foldBelow));
        }

        return index;
    }

    private void Sum(int index)
    {
        var n = _nodes[index];
        if (!n.IsDir && n.Children.Count == 0)
        {
            var d = n.Change;
            n.S = Math.Abs(d);
            n.Increase = Math.Max(0, d);
            n.Decrease = Math.Max(0, -d);
            return;
        }

        var du = n.NewUnrecorded - n.OldUnrecorded;
        n.S = Math.Abs(du);
        n.Increase = Math.Max(0, du);
        n.Decrease = Math.Max(0, -du);
        foreach (var c in n.Children)
        {
            Sum(c);
            n.S += _nodes[c].S;
            n.Increase += _nodes[c].Increase;
            n.Decrease += _nodes[c].Decrease;
        }
    }

    private string PathOf(int index)
    {
        var parts = new List<string>();
        for (var n = index; n >= 0; n = _nodes[n].Parent)
        {
            parts.Add(_nodes[n].Name);
        }

        parts.Reverse();
        return SnapshotTree.JoinPath(parts);
    }

    /// <summary>
    /// 热点分解：从根往下拆，变化量 S 不到阈值的不看；命中规则位置（<paramref name="stopAt"/>）的直接报告，不再往下拆；
    /// 没有单个子项超过阈值的（变化分散在大量小文件里，比如 Temp）报告它自己；否则逐个拆开超过阈值的子项，
    /// 剩下的部分超过阈值时单独报告"其余部分"。各项互不重叠。结果按净变化从大到小排。
    /// </summary>
    public List<GrowthHotspot> Hotspots(long threshold = DefaultThreshold, Func<string, bool>? stopAt = null)
    {
        var output = new List<GrowthHotspot>();
        Visit(0, threshold, stopAt, output);
        output.Sort((a, b) => b.Change.CompareTo(a.Change));
        return output;
    }

    private void Visit(int index, long threshold, Func<string, bool>? stopAt, List<GrowthHotspot> output)
    {
        var n = _nodes[index];
        if (n.S < threshold)
        {
            return;
        }

        var path = PathOf(index);
        if (index != 0 && stopAt?.Invoke(path) == true)
        {
            output.Add(Report(n, path, false));
            return;
        }

        var big = n.Children.Where(c => _nodes[c].S >= threshold).ToList();
        if (big.Count == 0)
        {
            output.Add(Report(n, path, false));
            return;
        }

        foreach (var c in big)
        {
            Visit(c, threshold, stopAt, output);
        }

        var rest = n.S - big.Sum(c => _nodes[c].S);
        if (rest >= threshold)
        {
            output.Add(new GrowthHotspot(path,
                n.Change - big.Sum(c => _nodes[c].Change),
                n.Increase - big.Sum(c => _nodes[c].Increase),
                n.Decrease - big.Sum(c => _nodes[c].Decrease),
                n.New - big.Sum(c => _nodes[c].New),
                n.Old - big.Sum(c => _nodes[c].Old),
                true, true));
        }
    }

    private static GrowthHotspot Report(Node n, string path, bool remainder) =>
        new(path, n.Change, n.Increase, n.Decrease, n.New, n.Old, n.IsDir, remainder);
}

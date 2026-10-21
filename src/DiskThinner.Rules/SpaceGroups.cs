using DiskThinner.Scanner;

namespace DiskThinner.Rules;

/// <summary>汇总视图里的一个位置：一个目录（或文件）里归同一条规则、同一个应用的那部分空间。</summary>
public sealed record SpaceLocation(int Node, long Bytes, long CleanableBytes, int Rule, Owner? Owner)
{
    /// <summary>"其他 N 处"或"零散的小位置"这类合并项，没有对应的节点。</summary>
    public bool IsRemainder => Node < 0;

    /// <summary>未识别热点里"父目录剩下的部分"（不含已经单独列出的子目录）。</summary>
    public bool IsParentRemainder { get; init; }

    /// <summary>合并项里有多少个位置。</summary>
    public int MergedCount { get; init; }
}

/// <summary>"按归属汇总"或"按类别汇总"里的一组。</summary>
public sealed class SpaceGroup
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>按归属汇总时的应用；按类别汇总时为 null。</summary>
    public Owner? Owner { get; init; }

    /// <summary>按类别汇总时的类别；未识别的组为 null。</summary>
    public Kind? Kind { get; init; }

    /// <summary>没有规则认识（按类别）或不知道归谁（按归属）的组。</summary>
    public bool IsUnknown { get; init; }

    public List<SpaceLocation> Locations { get; } = [];

    public long Bytes { get; internal set; }

    public long CleanableBytes { get; internal set; }

    /// <summary>位置总数（含合并进"其他 N 处"的）。</summary>
    public int LocationCount { get; internal set; }
}

/// <summary>
/// 汇总视图（原型"按归属汇总""按类别汇总"）：把分散在多处的空间按应用或类别合并统计。
/// 两种汇总都是不重叠的划分：所有组的大小加起来等于扫描到的总量（硬链接只算一次）。
/// </summary>
public static class SpaceGroups
{
    /// <summary>每组最多单独列出的位置数，其余合并成"其他 N 处"。</summary>
    public const int MaxLocationsPerGroup = 200;

    public const long DefaultUnknownThreshold = 64L << 20;

    /// <summary>
    /// 按归属汇总。每个文件归到"规则命中的位置"和"确定归属的位置"中更深的那个目录下；
    /// 既没有规则也不知道归属的空间按未识别热点分组。
    /// </summary>
    public static List<SpaceGroup> ByOwner(OwnerMap owners, long unknownThreshold = DefaultUnknownThreshold, CancellationToken cancellationToken = default)
    {
        var c = owners.Classification;
        var tree = c.Tree;
        var rules = c.Engine.Rules;
        var hotspots = new HashSet<int>();
        var parentRemainders = new HashSet<int>();
        foreach (var (node, _, isRemainder) in c.UnexplainedHotspots(unknownThreshold))
        {
            hotspots.Add(node);
            if (isRemainder)
            {
                parentRemainders.Add(node);
            }
        }

        var cells = new Dictionary<(int Node, int Rule), Cell>();
        var stack = new Stack<Frame>();
        stack.Push(new Frame(tree.Root, 0, null, -1, -1, -1, -1, -1));
        var visited = 0;
        while (stack.Count > 0)
        {
            var f = stack.Pop();
            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var n = f.Node;
            ref var node = ref tree[n];

            // 更新"最近的归属位置""最近的规则位置""最近的未识别热点"
            var owner = f.Owner;
            var ownerRoot = f.OwnerRoot;
            var ownerDepth = f.OwnerDepth;
            var ruleIndex = c.RuleIndexOf(n);
            if (c.IsRoot(n) && ruleIndex >= 0 && rules.Locations[ruleIndex].AppId is not null && owners.RuleOwnerAt(ruleIndex) is { } appOwner)
            {
                (owner, ownerRoot, ownerDepth) = (appOwner, n, f.Depth);
            }
            else if (owners.InferredRoots.TryGetValue(n, out var inferred))
            {
                (owner, ownerRoot, ownerDepth) = (inferred, n, f.Depth);
            }

            var matchRoot = c.IsRoot(n) ? n : f.MatchRoot;
            var matchDepth = c.IsRoot(n) ? f.Depth : f.MatchDepth;
            var hotspot = hotspots.Contains(n) ? n : f.Hotspot;

            if (node.IsDirectory)
            {
                for (var ch = node.FirstChild; ch >= 0; ch = tree[ch].NextSibling)
                {
                    stack.Push(new Frame(ch, f.Depth + 1, owner, ownerRoot, ownerDepth, matchRoot, matchDepth, hotspot));
                }

                continue;
            }

            var alloc = tree.CountHardLinksOnce && (node.Flags & NodeFlags.HardLinkSecondary) != 0 ? 0 : node.AllocSize;
            if (alloc <= 0)
            {
                continue;
            }

            // 和 OwnerMap.Resolve 的优先级一致：系统和用户文件规则 → 最近的规则应用或推断位置 → 通用规则的兜底文字
            Owner? fileOwner;
            int location;
            if (ruleIndex >= 0 && owners.RuleOwnerIsFixed(ruleIndex))
            {
                fileOwner = owners.RuleOwnerAt(ruleIndex);
                location = matchRoot;
            }
            else if (owner is not null)
            {
                fileOwner = owner;
                location = ruleIndex >= 0 && matchDepth > ownerDepth ? matchRoot : ownerRoot;
            }
            else if (ruleIndex >= 0)
            {
                fileOwner = owners.RuleOwnerAt(ruleIndex);
                location = matchRoot;
            }
            else
            {
                fileOwner = null;
                location = hotspot; // 可能是 -1：零散的小位置
            }

            var key = (location, ruleIndex);
            if (!cells.TryGetValue(key, out var cell))
            {
                cells[key] = cell = new Cell(fileOwner);
            }

            cell.Bytes += alloc;
            cell.Cleanable += c.Cleanable(n);
        }

        var groups = new Dictionary<string, SpaceGroup>();
        foreach (var ((location, rule), cell) in cells)
        {
            var groupKey = cell.Owner?.Key ?? "unknown";
            if (!groups.TryGetValue(groupKey, out var group))
            {
                groups[groupKey] = group = cell.Owner is { } o
                    ? new SpaceGroup { Key = groupKey, Name = o.Display, Owner = o }
                    : new SpaceGroup { Key = groupKey, Name = "不知道归谁", IsUnknown = true };
            }
            else if (cell.Owner is { Source: OwnerSource.Rule } ruleOwner && group.Owner is { Source: OwnerSource.Inferred })
            {
                // 同一个应用既有规则又有推断：组标题用规则给的名字
                var replaced = new SpaceGroup { Key = groupKey, Name = ruleOwner.Display, Owner = ruleOwner };
                replaced.Locations.AddRange(group.Locations);
                groups[groupKey] = group = replaced;
            }

            group.Locations.Add(new SpaceLocation(location, cell.Bytes, cell.Cleanable, rule, cell.Owner)
            {
                IsParentRemainder = location >= 0 && parentRemainders.Contains(location) && rule < 0,
            });
        }

        return Finish(groups.Values);
    }

    /// <summary>按类别汇总：规则命中的位置按类别分组；没有规则认识的空间按未识别热点列出。</summary>
    public static List<SpaceGroup> ByKind(OwnerMap owners, long unknownThreshold = DefaultUnknownThreshold)
    {
        var c = owners.Classification;
        var rules = c.Engine.Rules;
        var groups = new Dictionary<string, SpaceGroup>();
        foreach (var root in c.Roots)
        {
            if (root.Bytes <= 0)
            {
                continue;
            }

            var kind = rules.Locations[root.Rule].Kind;
            var key = "k:" + Names.Code(kind);
            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = new SpaceGroup { Key = key, Name = Names.Display(kind), Kind = kind };
            }

            group.Locations.Add(new SpaceLocation(root.Node, root.Bytes, root.CleanableBytes, root.Rule, owners.OwnerOf(root.Node)));
        }

        var total = c.Unexplained(c.Tree.Root);
        if (total > 0)
        {
            var unknown = new SpaceGroup { Key = "unknown", Name = "未识别", IsUnknown = true };
            long listed = 0;
            foreach (var (node, bytes, isRemainder) in c.UnexplainedHotspots(unknownThreshold))
            {
                unknown.Locations.Add(new SpaceLocation(node, bytes, 0, -1, owners.OwnerOf(node)) { IsParentRemainder = isRemainder });
                listed += bytes;
            }

            if (total - listed > 0)
            {
                unknown.Locations.Add(new SpaceLocation(-1, total - listed, 0, -1, null) { MergedCount = -1 });
            }

            groups[unknown.Key] = unknown;
        }

        return Finish(groups.Values);
    }

    /// <summary>位置按大小排，超过上限的合并；组按大小排。</summary>
    private static List<SpaceGroup> Finish(IEnumerable<SpaceGroup> groups)
    {
        var list = groups.ToList();
        foreach (var g in list)
        {
            g.Locations.Sort((a, b) => a.IsRemainder != b.IsRemainder ? a.IsRemainder.CompareTo(b.IsRemainder) : b.Bytes.CompareTo(a.Bytes));
            g.Bytes = g.Locations.Sum(l => l.Bytes);
            g.CleanableBytes = g.Locations.Sum(l => l.CleanableBytes);
            g.LocationCount = g.Locations.Count(l => !l.IsRemainder);
            var named = g.Locations.Where(l => !l.IsRemainder).ToList();
            if (named.Count > MaxLocationsPerGroup)
            {
                var rest = named.Skip(MaxLocationsPerGroup).ToList();
                var scattered = g.Locations.Where(l => l.IsRemainder).ToList();
                g.Locations.Clear();
                g.Locations.AddRange(named.Take(MaxLocationsPerGroup));
                g.Locations.Add(new SpaceLocation(-1, rest.Sum(l => l.Bytes) + scattered.Sum(l => l.Bytes), rest.Sum(l => l.CleanableBytes) + scattered.Sum(l => l.CleanableBytes), -1, null)
                {
                    MergedCount = rest.Count,
                });
            }
        }

        list.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return list;
    }

    private readonly record struct Frame(int Node, int Depth, Owner? Owner, int OwnerRoot, int OwnerDepth, int MatchRoot, int MatchDepth, int Hotspot);

    private sealed class Cell(Owner? owner)
    {
        public Owner? Owner { get; } = owner;

        public long Bytes;
        public long Cleanable;
    }
}

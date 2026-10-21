using DiskThinner.Scanner;

namespace DiskThinner.Rules;

/// <summary>规则命中的一个位置（规则在这个节点上命中，整块归这条规则）。</summary>
public sealed class MatchRoot(int node, int rule)
{
    public int Node { get; } = node;
    public int Rule { get; } = rule;

    /// <summary>归这条规则的文件占用（不含更深处被别的规则认领的部分；硬链接只算一次）。</summary>
    public long Bytes { get; internal set; }

    /// <summary>其中可以清理的部分（风险为安全、需重新下载、丢历史或登录态，且不是硬链接共享的文件）。</summary>
    public long CleanableBytes { get; internal set; }

    /// <summary>和别的位置用硬链接共享的文件：删掉这里不会释放空间。</summary>
    public long SharedBytes { get; internal set; }

    public int Files { get; internal set; }
}

/// <summary>一次规则匹配的结果：每个节点归哪条规则、受哪项保护，以及按目录汇总的可清理和未识别空间。</summary>
public sealed class Classification
{
    // 每个节点一项：所属规则位置（规则编号从位置里取，不单独存）、保护名单项。百万节点时共约 6 MB
    private readonly int[] _rootOf;
    private readonly short[] _protOf;
    private readonly long[] _cleanable;
    private readonly long[] _unexplained;
    private readonly List<MatchRoot> _roots = [];
    private readonly long[] _cleanableByRisk = new long[3];

    internal Classification(RuleEngine engine, NodeTree tree)
    {
        Engine = engine;
        Tree = tree;
        if (engine.Protections.Count > short.MaxValue)
        {
            throw new InvalidOperationException("保护名单项太多");
        }

        _rootOf = new int[tree.Count];
        _protOf = new short[tree.Count];
        Array.Fill(_rootOf, -1);
        Array.Fill(_protOf, (short)-1);
        _cleanable = new long[tree.Count];
        _unexplained = new long[tree.Count];
    }

    public RuleEngine Engine { get; }

    public NodeTree Tree { get; }

    public IReadOnlyList<MatchRoot> Roots => _roots;

    public TimeSpan Duration { get; private set; }

    /// <summary>整个扫描范围内可以清理的空间，按风险（安全、需重新下载、丢历史或登录态）。</summary>
    public long CleanableBytes(Risk risk) => risk <= Risk.Medium ? _cleanableByRisk[(int)risk] : 0;

    public long TotalCleanable => _cleanableByRisk.Sum();

    /// <summary>子树中可以清理的空间。</summary>
    public long Cleanable(int node) => _cleanable[node];

    /// <summary>子树中没有任何规则解释的空间。</summary>
    public long Unexplained(int node) => _unexplained[node];

    public LocationRule? RuleOf(int node) => RuleIndexOf(node) is var r && r >= 0 ? Engine.Rules.Locations[r] : null;

    public int RuleIndexOf(int node) => _rootOf[node] >= 0 ? _roots[_rootOf[node]].Rule : -1;

    /// <summary>节点所属的规则位置；不属于任何规则时返回 null。</summary>
    public MatchRoot? RootOf(int node) => _rootOf[node] >= 0 ? _roots[_rootOf[node]] : null;

    public bool IsRoot(int node) => _rootOf[node] >= 0 && _roots[_rootOf[node]].Node == node;

    public ProtectionEntry? ProtectionOf(int node) => _protOf[node] >= 0 ? Engine.Protections[_protOf[node]] : null;

    public ProtectionLevel ProtectionLevelOf(int node) => ProtectionOf(node)?.Level ?? ProtectionLevel.None;

    /// <summary>综合规则和保护名单后的风险。没有规则也不受保护时返回 null（未识别）。</summary>
    public Risk? EffectiveRisk(int node)
    {
        var rule = RuleOf(node);
        Risk? risk = rule?.Risk;
        return ProtectionLevelOf(node) switch
        {
            ProtectionLevel.Hard => Risk.Never,
            ProtectionLevel.Guide => risk is { } g && g > Risk.Guide ? g : Risk.Guide,
            ProtectionLevel.Soft when rule?.Tier == Tier.Verified => risk,
            ProtectionLevel.Soft => risk is { } s && s > Risk.High ? s : Risk.High,
            _ => risk,
        };
    }

    public static bool IsCleanable(Risk? risk) => risk is Risk.Safe or Risk.Low or Risk.Medium;

    internal int AddRoot(int node, int rule)
    {
        _roots.Add(new MatchRoot(node, rule));
        return _roots.Count - 1;
    }

    /// <summary>
    /// 规则是否给出了可以执行的清理方式：不是兜底规则（under 只说明"这一片是什么"），并且有删除或官方命令动作。
    /// 只有说明（guide）的规则，例如"在扩展面板里卸载""删除旧版本"，不计入可清理，避免把正在使用的程序算进去。
    /// </summary>
    public static bool IsActionable(LocationRule rule) =>
        rule.Mode != MatchMode.Under && rule.Actions.Any(a => a is DeleteAction or CommandAction);

    /// <summary>这个节点是否算作可清理：规则可执行，风险不高于"丢历史或登录态"，且不受保护。</summary>
    public bool IsCleanableNode(int node) =>
        RuleOf(node) is { } rule && IsActionable(rule) && IsCleanable(EffectiveRisk(node));

    internal void Assign(int node, int ruleIndex, int rootIndex, int protection)
    {
        _rootOf[node] = ruleIndex >= 0 ? rootIndex : -1;
        _protOf[node] = (short)protection;

        ref var n = ref Tree[node];
        if (n.IsDirectory)
        {
            return;
        }

        // 和 NodeTree 的汇总口径一致：只算一次的硬链接副本不计入
        var alloc = Tree.CountHardLinksOnce && (n.Flags & NodeFlags.HardLinkSecondary) != 0 ? 0 : n.AllocSize;
        if (ruleIndex < 0)
        {
            _unexplained[node] = alloc;
            return;
        }

        var root = _roots[rootIndex];
        root.Bytes += alloc;
        root.Files++;
        var shared = (n.Flags & NodeFlags.HardLinked) != 0;
        if (shared)
        {
            root.SharedBytes += alloc;
        }

        var risk = EffectiveRisk(node);
        if (!shared && IsActionable(Engine.Rules.Locations[ruleIndex]) && IsCleanable(risk))
        {
            _cleanable[node] = alloc;
            root.CleanableBytes += alloc;
            _cleanableByRisk[(int)risk!.Value] += alloc;
        }
    }

    /// <summary>自底向上汇总每个目录的可清理和未识别空间。</summary>
    internal void Finish(TimeSpan duration)
    {
        var order = new List<int>(Tree.Count);
        var stack = new Stack<int>();
        stack.Push(Tree.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            for (var c = Tree[n].FirstChild; c >= 0; c = Tree[c].NextSibling)
            {
                stack.Push(c);
            }
        }

        for (var k = order.Count - 1; k > 0; k--)
        {
            var n = order[k];
            var p = Tree[n].Parent;
            _cleanable[p] += _cleanable[n];
            _unexplained[p] += _unexplained[n];
        }

        Duration = duration;
    }

    /// <summary>
    /// 还没被规则解释的大块（热点分解）：从根往下拆，拆到没有单个子项超过阈值为止；父目录剩下的部分单独列出。各项互不重叠。
    /// </summary>
    public List<(int Node, long Bytes, bool IsRemainder)> UnexplainedHotspots(long threshold, int? from = null)
    {
        var output = new List<(int, long, bool)>();
        var stack = new Stack<int>();
        stack.Push(from ?? Tree.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (_unexplained[n] < threshold)
            {
                continue;
            }

            long bigSum = 0;
            var any = false;
            for (var c = Tree[n].FirstChild; c >= 0; c = Tree[c].NextSibling)
            {
                if (_unexplained[c] >= threshold)
                {
                    any = true;
                    bigSum += _unexplained[c];
                    stack.Push(c);
                }
            }

            if (!any)
            {
                output.Add((n, _unexplained[n], false));
            }
            else if (_unexplained[n] - bigSum >= threshold)
            {
                output.Add((n, _unexplained[n] - bigSum, true));
            }
        }

        output.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return output;
    }
}

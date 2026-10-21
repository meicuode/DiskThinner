using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskThinner.Scanner;

namespace DiskThinner.Rules;

/// <summary>
/// 规则引擎：把规则和保护名单编译成路径模式，在扫描得到的树上从根往下走一遍，给每个节点打上规则和保护标记。
///
/// 匹配方式是按段推进的状态机：每个节点继承父节点的"进行中"状态集合，用自己的名字推进一步；
/// 模式里的 ** 可以停在原地吞掉任意层。走到模式末尾就算命中。
///
/// 命中规则后的语义（与阶段 0 的覆盖率实验一致）：
///   dir / file：这个节点连同整棵子树都归这条规则，规则不再往下匹配（保护名单仍然继续匹配）；
///   under：兜底，这个目录下没有被更具体的规则认领的部分归它，匹配继续往下找。
/// 同一个节点被多条规则命中时，取纯文字段最多（最具体）的一条，再按规则顺序。
/// 保护名单：同一节点取最具体的一项，并覆盖从上级继承来的保护级别（所以例外项能放开子目录）。
/// </summary>
public sealed class RuleEngine
{
    private const int SegBits = 6;
    private const int MaxSegments = (1 << SegBits) - 1;

    private readonly Entry[] _entries;
    private readonly int[] _seen;
    private readonly ProtectionEntry[] _extraProtection;
    private int _stamp;

    public RuleEngine(RuleSet rules, PathContext context, IEnumerable<ProtectionEntry>? extraProtection = null)
    {
        Rules = rules;
        Context = context;
        _extraProtection = [.. extraProtection ?? []];
        Protections = [.. ProtectionList.BuiltIn, .. _extraProtection, MetadataProtection];
        MetadataProtectionIndex = Protections.Count - 1;

        var entries = new List<Entry>();
        for (var i = 0; i < rules.Locations.Count; i++)
        {
            var rule = rules.Locations[i];
            entries.Add(new Entry(Compile(rule.Pattern, context), i, -1, rule.Mode, CompileConditions(rule.Conditions, context)));
        }

        for (var i = 0; i < Protections.Count - 1; i++)
        {
            entries.Add(new Entry(Compile(Protections[i].Pattern, context), -1, i, MatchMode.Dir, []));
        }

        _entries = [.. entries];
        _seen = new int[_entries.Length << SegBits];
    }

    /// <summary>NTFS 元数据文件（$MFT 等）由扫描器标记，不靠路径匹配。</summary>
    private static readonly ProtectionEntry MetadataProtection = new("", ProtectionLevel.Hard, "NTFS 内部文件，由文件系统管理");

    public RuleSet Rules { get; }

    public PathContext Context { get; }

    public IReadOnlyList<ProtectionEntry> Protections { get; }

    internal int MetadataProtectionIndex { get; }

    private static CompiledPattern Compile(string pattern, PathContext context)
    {
        var compiled = CompiledPattern.Compile(PathTokens.Expand(pattern, context));
        if (compiled.Segments.Count > MaxSegments)
        {
            throw new FormatException($"路径层数过多：{pattern}");
        }

        return compiled;
    }

    private static CompiledCondition[] CompileConditions(IReadOnlyList<Condition> conditions, PathContext context) =>
        conditions.Select(c => c.Type == ConditionType.NotUnder
            ? new CompiledCondition(c.Type, [], c.Patterns.Select(p => Compile(p, context)).ToArray())
            : new CompiledCondition(c.Type, c.Patterns.Select(Segment.Parse).ToArray(), [])).ToArray();

    public Classification Classify(NodeTree tree, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new Classification(this, tree);
        var arena = new List<int>(1024);
        var stack = new List<Frame>(256);

        // 根节点：所有模式从第 0 段开始，按根的完整路径逐段推进（扫描的可能是文件夹，根就是一个深层路径）
        var start = arena.Count;
        _stamp++;
        for (var e = 0; e < _entries.Length; e++)
        {
            AddState(arena, e, 0);
        }

        var count = arena.Count - start;
        foreach (var part in tree.GetPath(tree.Root).TrimEnd('\\').Split('\\'))
        {
            var next = arena.Count;
            var produced = Advance(arena, start, count, part);
            arena.RemoveRange(start, next - start);
            start = arena.Count - produced;
            count = produced;
        }

        var root = ProcessNode(tree, result, tree.Root, arena, start, count, new WalkContext(-1, -1, -1, -1, -1));
        if (tree[tree.Root].IsDirectory && root.Push)
        {
            stack.Add(new Frame(tree.Root, start, root.StateCount, root.Ctx, tree[tree.Root].FirstChild));
        }

        var visited = 0;
        while (stack.Count > 0)
        {
            ref var top = ref CollectionsMarshal.AsSpan(stack)[^1];
            var child = top.NextChild;
            if (child < 0)
            {
                arena.RemoveRange(top.StateStart, arena.Count - top.StateStart);
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            top.NextChild = tree[child].NextSibling;
            var parent = top; // 下面可能扩容 stack，先复制

            if ((++visited & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var childStart = arena.Count;
            var childCount = Advance(arena, parent.StateStart, parent.StateCount, tree.GetName(child));
            var outcome = ProcessNode(tree, result, child, arena, childStart, childCount, parent.Ctx);
            if (outcome.Push && tree[child].FirstChild >= 0)
            {
                stack.Add(new Frame(child, childStart, outcome.StateCount, outcome.Ctx, tree[child].FirstChild));
            }
            else
            {
                arena.RemoveRange(childStart, arena.Count - childStart);
            }
        }

        result.Finish(sw.Elapsed);
        return result;
    }

    private readonly record struct WalkContext(int Fallback, int FallbackRoot, int Claim, int ClaimRoot, int Protection);

    private struct Frame(int node, int stateStart, int stateCount, WalkContext ctx, int nextChild)
    {
        public readonly int Node = node;
        public readonly int StateStart = stateStart;
        public readonly int StateCount = stateCount;
        public readonly WalkContext Ctx = ctx;
        public int NextChild = nextChild;
    }

    private readonly record struct Outcome(bool Push, int StateCount, WalkContext Ctx);

    /// <summary>根据节点的状态集合决定它的规则和保护，返回是否需要继续处理子节点。</summary>
    private Outcome ProcessNode(NodeTree tree, Classification result, int node, List<int> arena, int start, int count, WalkContext inherited)
    {
        ref var n = ref tree[node];
        var isDir = n.IsDirectory;
        var ctx = inherited;

        // 1. 保护名单：取最具体的一项，覆盖继承来的级别
        var bestProt = -1;
        var bestProtScore = -1;
        var bestClaim = -1;
        var bestClaimScore = -1;
        var bestUnder = -1;
        var bestUnderScore = -1;
        var states = CollectionsMarshal.AsSpan(arena).Slice(start, count);
        foreach (var s in states)
        {
            var e = s >> SegBits;
            var i = s & MaxSegments;
            var entry = _entries[e];
            if (i != entry.Pattern.Segments.Count)
            {
                continue;
            }

            if (entry.Protection >= 0)
            {
                var score = entry.Pattern.LiteralCount * 8 + (int)Protections[entry.Protection].Level;
                if (score > bestProtScore)
                {
                    bestProtScore = score;
                    bestProt = entry.Protection;
                }

                continue;
            }

            if (inherited.Claim >= 0)
            {
                continue; // 已被上级规则认领，规则不再往下匹配
            }

            var fits = entry.Mode switch
            {
                MatchMode.File => !isDir,
                _ => isDir,
            };
            if (!fits || !ConditionsHold(tree, node, entry.Conditions))
            {
                continue;
            }

            // 越具体越优先；同样具体时规则顺序靠前的优先
            var ruleScore = entry.Pattern.LiteralCount * 100_000 - entry.Rule;
            if (entry.Mode == MatchMode.Under)
            {
                if (ruleScore > bestUnderScore)
                {
                    bestUnderScore = ruleScore;
                    bestUnder = entry.Rule;
                }
            }
            else if (ruleScore > bestClaimScore)
            {
                bestClaimScore = ruleScore;
                bestClaim = entry.Rule;
            }
        }

        if (bestProt >= 0)
        {
            ctx = ctx with { Protection = bestProt };
        }

        if ((n.Flags & NodeFlags.Metadata) != 0)
        {
            ctx = ctx with { Protection = MetadataProtectionIndex };
        }

        // 2. 规则
        if (ctx.Claim < 0)
        {
            if (bestClaim >= 0)
            {
                ctx = ctx with { Claim = bestClaim, ClaimRoot = result.AddRoot(node, bestClaim) };
            }
            else if (bestUnder >= 0)
            {
                ctx = ctx with { Fallback = bestUnder, FallbackRoot = result.AddRoot(node, bestUnder) };
            }
        }

        var rule = ctx.Claim >= 0 ? ctx.Claim : ctx.Fallback;
        var rootIndex = ctx.Claim >= 0 ? ctx.ClaimRoot : ctx.FallbackRoot;
        result.Assign(node, rule, rootIndex, ctx.Protection);

        if (!isDir)
        {
            return new Outcome(false, 0, ctx);
        }

        // 3. 已被认领的子树：规则状态不再需要，只保留保护名单的状态；没有保护状态时直接整棵填充
        if (ctx.Claim >= 0)
        {
            var kept = 0;
            for (var k = 0; k < count; k++)
            {
                var s = arena[start + k];
                if (_entries[s >> SegBits].Protection >= 0)
                {
                    arena[start + kept++] = s;
                }
            }

            arena.RemoveRange(start + kept, count - kept);
            if (kept == 0)
            {
                FillSubtree(tree, result, node, ctx);
                return new Outcome(false, 0, ctx);
            }

            return new Outcome(true, kept, ctx);
        }

        return new Outcome(true, count, ctx);
    }

    /// <summary>整棵子树都归同一条规则、保护级别也不会再变：不再匹配，直接填充。</summary>
    private void FillSubtree(NodeTree tree, Classification result, int node, WalkContext ctx)
    {
        var stack = new Stack<int>();
        for (var c = tree[node].FirstChild; c >= 0; c = tree[c].NextSibling)
        {
            stack.Push(c);
        }

        while (stack.Count > 0)
        {
            var n = stack.Pop();
            var prot = (tree[n].Flags & NodeFlags.Metadata) != 0 ? MetadataProtectionIndex : ctx.Protection;
            result.Assign(n, ctx.Claim, ctx.ClaimRoot, prot);
            for (var c = tree[n].FirstChild; c >= 0; c = tree[c].NextSibling)
            {
                stack.Push(c);
            }
        }
    }

    private int Advance(List<int> arena, int start, int count, ReadOnlySpan<char> name)
    {
        _stamp++;
        var before = arena.Count;
        for (var k = 0; k < count; k++)
        {
            var s = arena[start + k];
            var e = s >> SegBits;
            var i = s & MaxSegments;
            var segments = _entries[e].Pattern.Segments;
            if (i >= segments.Count)
            {
                continue;
            }

            var seg = segments[i];
            if (seg.AnyDepth)
            {
                AddState(arena, e, i); // ** 吞掉这一层，留在原地
            }
            else if (seg.Matches(name))
            {
                AddState(arena, e, i + 1);
            }
        }

        return arena.Count - before;
    }

    private void AddState(List<int> arena, int entry, int segment)
    {
        while (true)
        {
            var key = (entry << SegBits) | segment;
            if (_seen[key] == _stamp)
            {
                return;
            }

            _seen[key] = _stamp;
            arena.Add(key);
            var segments = _entries[entry].Pattern.Segments;
            if (segment < segments.Count && segments[segment].AnyDepth)
            {
                segment++; // ** 也可以匹配 0 层
                continue;
            }

            return;
        }
    }

    /// <summary>和这个引擎同一套保护名单（内置 + 追加）的逐路径检查。</summary>
    public ProtectionCheck CreateProtectionCheck() => new(Context, _extraProtection);

    /// <summary>
    /// 执行清理前的复核：规则 <paramref name="ruleIndex"/> 现在是否仍然命中 <paramref name="path"/>。
    /// 不走扫描树，直接看路径模式，条件（has_any、sibling_any、not_under）按磁盘上现在的内容判断。
    /// 只检查这条规则本身，不考虑更具体的规则（扫描时已经决定了归属）。
    /// </summary>
    public bool StillMatches(int ruleIndex, string path, bool isDirectory)
    {
        if (ruleIndex < 0 || ruleIndex >= Rules.Locations.Count)
        {
            return false;
        }

        var entry = _entries[ruleIndex];
        if (entry.Rule != ruleIndex || entry.Mode == MatchMode.Under || (entry.Mode == MatchMode.File) == isDirectory || !entry.Pattern.Matches(path))
        {
            return false;
        }

        foreach (var c in entry.Conditions)
        {
            var ok = c.Type switch
            {
                ConditionType.HasAny => isDirectory && AnyEntry(path, null, c.Names),
                ConditionType.SiblingAny => Path.GetDirectoryName(path) is { } parent && AnyEntry(parent, Path.GetFileName(path), c.Names),
                ConditionType.NotUnder => !c.Paths.Any(p => p.MatchesSelfOrAncestor(path)),
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyEntry(string directory, string? except, Segment[] names)
    {
        try
        {
            foreach (var full in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(full.AsSpan());
                if (except is not null && name.Equals(except, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var n in names)
                {
                    if (n.Matches(name))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private bool ConditionsHold(NodeTree tree, int node, CompiledCondition[] conditions)
    {
        foreach (var c in conditions)
        {
            var ok = c.Type switch
            {
                ConditionType.HasAny => AnyChild(tree, node, -1, c.Names),
                ConditionType.SiblingAny => node != tree.Root && AnyChild(tree, tree[node].Parent, node, c.Names),
                ConditionType.NotUnder => !c.Paths.Any(p => p.MatchesSelfOrAncestor(tree.GetPath(node))),
                _ => false,
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnyChild(NodeTree tree, int parent, int except, Segment[] names)
    {
        for (var c = tree[parent].FirstChild; c >= 0; c = tree[c].NextSibling)
        {
            if (c == except)
            {
                continue;
            }

            var name = tree.GetName(c);
            foreach (var n in names)
            {
                if (n.Matches(name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private sealed record Entry(CompiledPattern Pattern, int Rule, int Protection, MatchMode Mode, CompiledCondition[] Conditions);

    private sealed record CompiledCondition(ConditionType Type, Segment[] Names, CompiledPattern[] Paths);
}

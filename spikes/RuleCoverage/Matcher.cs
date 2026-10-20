using DiskThinner.Scanner;

namespace RuleCoverage;

internal sealed class MatchResult(int count)
{
    /// <summary>每个节点的子树中被规则解释的字节数。</summary>
    public long[] Explained { get; } = new long[count];
    public long[] Cleanable { get; } = new long[count];
    public long[] Guide { get; } = new long[count];
    public long[] Unexplained { get; } = new long[count];
    public Dictionary<string, long> ByRule { get; } = [];
    public Dictionary<string, int> HitNodes { get; } = [];
    public Dictionary<string, long> ByRiskUser { get; } = [];
    public Dictionary<string, long> ByRiskAll { get; } = [];
}

/// <summary>
/// 从根往下匹配：节点先按文件顺序找第一条命中的 dir/file 规则，命中就整棵子树归这条规则；
/// 否则记下命中的 under 规则作为兜底，继续往下找。文件没被具体规则命中时归最近的兜底规则。
/// </summary>
internal static class Matcher
{
    /// <param name="userRoot">按风险统计时单独汇总的范围（当前用户目录）。</param>
    public static MatchResult Run(NodeTree tree, List<Rule> rules, int userRoot)
    {
        var result = new MatchResult(tree.Count);
        var dirRules = rules.Where(r => r.Kind == RuleKind.Dir).ToList();
        var fileRules = rules.Where(r => r.Kind == RuleKind.File).ToList();
        var underRules = rules.Where(r => r.Kind == RuleKind.Under).ToList();

        var explainedOwn = new long[tree.Count];
        var cleanableOwn = new long[tree.Count];
        var guideOwn = new long[tree.Count];

        Visit(tree.Root, @"C:", null, false);

        // 自底向上汇总
        var order = new List<int>(tree.Count);
        var stack = new Stack<int>();
        stack.Push(tree.Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            foreach (var c in tree.Children(n))
            {
                stack.Push(c);
            }
        }

        for (var k = order.Count - 1; k >= 0; k--)
        {
            var n = order[k];
            result.Explained[n] += explainedOwn[n];
            result.Cleanable[n] += cleanableOwn[n];
            result.Guide[n] += guideOwn[n];
            result.Unexplained[n] = tree[n].AllocSize - result.Explained[n];
            if (n != tree.Root)
            {
                var p = tree[n].Parent;
                result.Explained[p] += result.Explained[n];
                result.Cleanable[p] += result.Cleanable[n];
                result.Guide[p] += result.Guide[n];
            }
        }

        return result;

        void Visit(int n, string path, Rule? fallback, bool inUsers)
        {
            var isDir = tree[n].IsDirectory;
            var name = tree.GetNameString(n);
            inUsers |= n == userRoot;

            foreach (var rule in isDir ? dirRules : fileRules)
            {
                if (Matches(rule, n, name, path))
                {
                    Claim(n, rule, inUsers);
                    return;
                }
            }

            if (!isDir)
            {
                if (fallback is not null)
                {
                    Claim(n, fallback, inUsers);
                }

                return;
            }

            foreach (var rule in underRules)
            {
                if (Matches(rule, n, name, path))
                {
                    fallback = rule; // 越深的 under 规则越具体
                    break;
                }
            }

            foreach (var c in tree.Children(n))
            {
                Visit(c, path + "\\" + tree.GetNameString(c), fallback, inUsers);
            }
        }

        bool Matches(Rule rule, int n, string name, string path)
        {
            if (rule.NameRegex is not null && !rule.NameRegex.IsMatch(name))
            {
                return false;
            }

            if (!rule.PathRegex.IsMatch(path))
            {
                return false;
            }

            foreach (var c in rule.Conditions)
            {
                var ok = c.Type switch
                {
                    "has" => tree.Children(n).Any(x => c.NameSet!.IsMatch(tree.GetNameString(x))),
                    "sibling" => n != tree.Root && tree.Children(tree[n].Parent).Any(x => x != n && c.NameSet!.IsMatch(tree.GetNameString(x))),
                    "notpath" => !c.PathRegex!.IsMatch(path),
                    _ => false,
                };
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        void Claim(int n, Rule rule, bool inUsers)
        {
            var bytes = tree[n].AllocSize;
            explainedOwn[n] += bytes;
            if (rule.IsCleanable)
            {
                cleanableOwn[n] += bytes;
            }
            else if (rule.Risk == "guide")
            {
                guideOwn[n] += bytes;
            }

            result.ByRule[rule.Id] = result.ByRule.GetValueOrDefault(rule.Id) + bytes;
            result.HitNodes[rule.Id] = result.HitNodes.GetValueOrDefault(rule.Id) + 1;
            result.ByRiskAll[rule.Risk] = result.ByRiskAll.GetValueOrDefault(rule.Risk) + bytes;
            if (inUsers)
            {
                result.ByRiskUser[rule.Risk] = result.ByRiskUser.GetValueOrDefault(rule.Risk) + bytes;
            }
        }
    }
}

/// <summary>热点分解：大块往下拆，拆到没有单个子项超过阈值为止；父目录剩下的部分单独报告。各项互不重叠。</summary>
internal static class Hotspots
{
    public static List<(int Node, long Bytes, bool IsRemainder)> Find(NodeTree tree, long[] value, long threshold)
    {
        var output = new List<(int, long, bool)>();
        Visit(tree.Root);
        return output;

        void Visit(int n)
        {
            if (value[n] < threshold)
            {
                return;
            }

            var big = tree.Children(n).Where(c => value[c] >= threshold).ToList();
            if (big.Count == 0)
            {
                output.Add((n, value[n], false));
                return;
            }

            foreach (var c in big)
            {
                Visit(c);
            }

            var rest = value[n] - big.Sum(c => value[c]);
            if (rest >= threshold)
            {
                output.Add((n, rest, true));
            }
        }
    }
}

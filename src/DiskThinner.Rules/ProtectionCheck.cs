namespace DiskThinner.Rules;

/// <summary>
/// 对一个具体路径计算保护级别，口径和 <see cref="RuleEngine"/> 在树上的结果一致：
/// 从上往下逐级看，最深一级命中的保护项（同一级取最具体的一项）决定结果。用于规则校验。
/// </summary>
public sealed class ProtectionCheck
{
    private readonly (CompiledPattern Pattern, ProtectionEntry Entry)[] _entries;

    public ProtectionCheck(PathContext context, IEnumerable<ProtectionEntry>? extra = null)
    {
        _entries = ProtectionList.BuiltIn.Concat(extra ?? [])
            .Select(e => (CompiledPattern.Compile(PathTokens.Expand(e.Pattern, context)), e))
            .ToArray();
    }

    public ProtectionEntry? EntryFor(string absolutePath)
    {
        var parts = absolutePath.TrimEnd('\\').Split('\\');
        ProtectionEntry? result = null;
        for (var n = 1; n <= parts.Length; n++)
        {
            var prefix = string.Join('\\', parts[..n]);
            ProtectionEntry? best = null;
            var bestScore = -1;
            foreach (var (pattern, entry) in _entries)
            {
                if (pattern.Matches(prefix))
                {
                    var score = pattern.LiteralCount * 8 + (int)entry.Level;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = entry;
                    }
                }
            }

            if (best is not null)
            {
                result = best;
            }
        }

        return result;
    }

    public ProtectionLevel LevelFor(string absolutePath) => EntryFor(absolutePath)?.Level ?? ProtectionLevel.None;

    /// <summary>
    /// 把路径模式变成几条具体的示例路径：* 换成 sample，? 换成 x，** 当作 0 层，{a,b} 逐个展开（最多 32 条）。
    /// </summary>
    public static IEnumerable<string> SamplePaths(string absolutePattern)
    {
        IEnumerable<string> results = [""];
        foreach (var seg in absolutePattern.TrimEnd('\\').Split('\\'))
        {
            if (seg == "**")
            {
                continue;
            }

            var options = ExpandBraces(seg).Select(o => o.Replace("*", "sample", StringComparison.Ordinal).Replace('?', 'x')).ToList();
            results = results.SelectMany(r => options.Select(o => r.Length == 0 ? o : r + "\\" + o)).Take(32).ToList();
        }

        return results;
    }

    private static IEnumerable<string> ExpandBraces(string text)
    {
        var open = text.IndexOf('{');
        var close = open < 0 ? -1 : text.IndexOf('}', open);
        if (open < 0 || close < 0)
        {
            return [text];
        }

        var head = text[..open];
        var tail = text[(close + 1)..];
        return text[(open + 1)..close].Split(',').SelectMany(o => ExpandBraces(tail).Select(t => head + o.Trim() + t));
    }
}

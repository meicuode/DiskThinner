namespace DiskThinner.Rules;

/// <summary>
/// 路径模式中的一段。支持 <c>*</c>（段内任意字符）、<c>?</c>（一个字符）、<c>{a,b}</c>（任选其一），大小写不敏感；
/// 单独的 <c>**</c> 表示任意层目录（包括 0 层）。
/// </summary>
public sealed class Segment
{
    private readonly string[] _alternatives;
    private readonly bool _literal;

    private Segment(string text, bool anyDepth)
    {
        Text = text;
        AnyDepth = anyDepth;
        _alternatives = anyDepth ? [] : ExpandBraces(text).ToArray();
        _literal = !anyDepth && _alternatives.Length == 1 && _alternatives[0].IndexOfAny(['*', '?']) < 0;
    }

    public string Text { get; }

    public bool AnyDepth { get; }

    public bool IsLiteral => _literal;

    public static Segment Parse(string text) => new(text, text == "**");

    public bool Matches(ReadOnlySpan<char> name)
    {
        if (_literal)
        {
            return name.Equals(_alternatives[0], StringComparison.OrdinalIgnoreCase);
        }

        foreach (var alt in _alternatives)
        {
            if (Wildcard(alt, name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>经典的回溯通配：只需记住最后一个 * 的位置，线性时间。</summary>
    private static bool Wildcard(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static IEnumerable<string> ExpandBraces(string text)
    {
        var open = text.IndexOf('{');
        if (open < 0)
        {
            yield return text;
            yield break;
        }

        var close = text.IndexOf('}', open);
        if (close < 0)
        {
            throw new FormatException($"花括号没有闭合：{text}");
        }

        var head = text[..open];
        var tail = text[(close + 1)..];
        foreach (var option in text[(open + 1)..close].Split(','))
        {
            foreach (var rest in ExpandBraces(tail))
            {
                yield return head + option.Trim() + rest;
            }
        }
    }
}

/// <summary>编译后的路径模式：按 \ 分成若干段，第一段是盘符（如 C:）。</summary>
public sealed class CompiledPattern
{
    private CompiledPattern(string text, Segment[] segments)
    {
        Text = text;
        Segments = segments;
        LiteralCount = segments.Count(s => s.IsLiteral);
    }

    public string Text { get; }

    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>纯文字的段数，用来衡量具体程度：同一个节点被多条规则命中时，越具体的越优先。</summary>
    public int LiteralCount { get; }

    public static CompiledPattern Compile(string absolutePattern)
    {
        var parts = absolutePattern.TrimEnd('\\').Split('\\');
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
        {
            throw new FormatException($"路径格式错误：{absolutePattern}");
        }

        return new CompiledPattern(absolutePattern, parts.Select(Segment.Parse).ToArray());
    }

    /// <summary>直接判断完整路径是否匹配（不走树，测试和 not_under 条件用）。</summary>
    public bool Matches(string path) => MatchFrom(path.TrimEnd('\\').Split('\\'), 0, 0);

    /// <summary>
    /// 这个模式能不能匹配 <paramref name="path"/> 下面（更深处）的某个路径：路径的每一段都被模式的前几段消耗掉，
    /// 而且模式还有剩下的段（或者停在 ** 上）。清理时用它挑出和某个目录有关的保护项，逐项检查只看这几项。
    /// </summary>
    public bool CouldMatchBelow(string path) => PartialFrom(path.TrimEnd('\\').Split('\\'), 0, 0);

    private bool PartialFrom(string[] parts, int pi, int si)
    {
        if (si == Segments.Count)
        {
            return false;
        }

        if (Segments[si].AnyDepth)
        {
            return true; // ** 能吞掉剩下的所有段，并且继续匹配更深处
        }

        if (pi == parts.Length)
        {
            return true;
        }

        return Segments[si].Matches(parts[pi]) && PartialFrom(parts, pi + 1, si + 1);
    }

    /// <summary>路径本身或它的某个上级目录匹配。</summary>
    public bool MatchesSelfOrAncestor(string path)
    {
        var parts = path.TrimEnd('\\').Split('\\');
        for (var n = parts.Length; n > 0; n--)
        {
            if (MatchFrom(parts[..n], 0, 0))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchFrom(string[] parts, int pi, int si)
    {
        if (si == Segments.Count)
        {
            return pi == parts.Length;
        }

        var seg = Segments[si];
        if (seg.AnyDepth)
        {
            for (var k = pi; k <= parts.Length; k++)
            {
                if (MatchFrom(parts, k, si + 1))
                {
                    return true;
                }
            }

            return false;
        }

        return pi < parts.Length && seg.Matches(parts[pi]) && MatchFrom(parts, pi + 1, si + 1);
    }
}

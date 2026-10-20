using System.Text;
using System.Text.RegularExpressions;

namespace RuleCoverage;

internal enum RuleKind { Dir, File, Under }

internal sealed record Condition(string Type, Regex? NameSet, Regex? PathRegex);

/// <summary>一条候选规则。路径令牌展开后转成正则；另外从最后一段生成只匹配名称的正则，用来快速过滤。</summary>
internal sealed class Rule
{
    public required string Id { get; init; }
    public required string Layer { get; init; }
    public required string Category { get; init; }
    public required string Risk { get; init; }
    public required RuleKind Kind { get; init; }
    public required string Pattern { get; init; }
    public required Regex PathRegex { get; init; }
    public Regex? NameRegex { get; init; }
    public List<Condition> Conditions { get; } = [];
    public int Order { get; init; }

    public bool IsCleanable => Risk is "safe" or "low" or "medium";

    private static readonly (string Token, string Value)[] Tokens =
    [
        ("%USERPROFILE%", @"C:\Users\*"),
        ("%LOCALAPPDATA%", @"C:\Users\*\AppData\Local"),
        ("%APPDATA%", @"C:\Users\*\AppData\Roaming"),
        ("%LOCALLOW%", @"C:\Users\*\AppData\LocalLow"),
        ("%PROGRAMDATA%", @"C:\ProgramData"),
        ("%WINDIR%", @"C:\Windows"),
    ];

    public static List<Rule> Load(string file)
    {
        var rules = new List<Rule>();
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 5)
            {
                throw new FormatException($"规则格式错误：{line}");
            }

            var clauses = parts[4].Split('&', StringSplitOptions.TrimEntries);
            var head = clauses[0];
            var colon = head.IndexOf(':');
            var kind = head[..colon] switch
            {
                "dir" => RuleKind.Dir,
                "file" => RuleKind.File,
                "under" => RuleKind.Under,
                _ => throw new FormatException($"未知模式类型：{head}"),
            };
            var glob = Expand(head[(colon + 1)..]);
            var lastSegment = glob[(glob.LastIndexOf('\\') + 1)..];

            var rule = new Rule
            {
                Id = parts[0],
                Layer = parts[1],
                Category = parts[2],
                Risk = parts[3],
                Kind = kind,
                Pattern = glob,
                PathRegex = GlobToRegex(glob),
                NameRegex = lastSegment == "**" ? null : SegmentRegex(lastSegment),
                Order = rules.Count,
            };

            foreach (var clause in clauses.Skip(1))
            {
                var c = clause.IndexOf(':');
                var type = clause[..c];
                var value = clause[(c + 1)..];
                rule.Conditions.Add(type switch
                {
                    "has" or "sibling" => new Condition(type, SegmentRegex(value), null),
                    "notpath" => new Condition(type, null, GlobToRegex(Expand(value))),
                    _ => throw new FormatException($"未知条件：{clause}"),
                });
            }

            rules.Add(rule);
        }

        return rules;
    }

    private static string Expand(string glob)
    {
        foreach (var (token, value) in Tokens)
        {
            glob = glob.Replace(token, value, StringComparison.OrdinalIgnoreCase);
        }

        return glob;
    }

    /// <summary>** 匹配任意层目录（含 0 层），* 匹配一段内任意字符，{a,b} 任选其一。</summary>
    public static Regex GlobToRegex(string glob)
    {
        var segments = glob.Split('\\');
        var sb = new StringBuilder("^");
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var last = i == segments.Length - 1;
            if (seg == "**")
            {
                sb.Append(last ? @"(?:\\.*)?" : @"(?:\\[^\\]+)*");
                continue;
            }

            if (i > 0)
            {
                sb.Append(@"\\");
            }

            sb.Append(SegmentPattern(seg));
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    private static Regex SegmentRegex(string segment) =>
        new("^" + SegmentPattern(segment) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string SegmentPattern(string segment)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            switch (ch)
            {
                case '*':
                    sb.Append(@"[^\\]*");
                    break;
                case '{':
                    var end = segment.IndexOf('}', i);
                    var options = segment[(i + 1)..end].Split(',').Select(o => SegmentPattern(o.Trim()));
                    sb.Append("(?:").Append(string.Join('|', options)).Append(')');
                    i = end;
                    break;
                default:
                    sb.Append(Regex.Escape(ch.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }
}

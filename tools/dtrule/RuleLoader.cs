using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiskThinner.Rules;
using YamlDotNet.RepresentationModel;

namespace DiskThinner.RuleTools;

public sealed record RuleIssue(string File, string? RuleId, string Message)
{
    public override string ToString() => RuleId is null ? $"{File}: {Message}" : $"{File} [{RuleId}]: {Message}";
}

/// <summary>
/// 读取 rules/ 目录下的 YAML 源文件。每个文件可以有一个 app 段（或一个 apps 列表）和一个 locations 列表；
/// 位置可以用 app: &lt;id&gt; 指明归哪个应用（应用可以定义在别的文件里）。
/// 用 YAML 的节点模型手动映射，不认识的键直接报错（拼错字段时不会被静默忽略）。
/// </summary>
public static partial class RuleLoader
{
    private static readonly HashSet<string> AppKeys = ["id", "name", "vendor", "exes", "processes"];
    private static readonly HashSet<string> LocationKeys =
    [
        "id", "app", "dir", "file", "under", "has_any", "sibling_any", "not_under", "kind", "risk", "tier", "label",
        "regenerates", "requires_closed", "purpose", "impact", "actions", "relocate", "evidence",
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9.\\-]*$")]
    private static partial Regex IdPattern();

    public static (RuleSet Rules, List<RuleIssue> Issues) LoadDirectory(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('/', '\\'))
            .Order(StringComparer.Ordinal)
            .ToList();

        var apps = new List<AppInfo>();
        var locations = new List<LocationRule>();
        var issues = new List<RuleIssue>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var rel in files)
        {
            var text = File.ReadAllText(Path.Combine(directory, rel), Encoding.UTF8);
            hash.AppendData(Encoding.UTF8.GetBytes(rel.ToLowerInvariant() + "\n" + text.ReplaceLineEndings("\n") + "\n"));
            LoadFile(rel, text, apps, locations, issues);
        }

        var version = Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
        return (new RuleSet(apps, locations) { Version = version }, issues);
    }

    public static void LoadFile(string file, string text, List<AppInfo> apps, List<LocationRule> locations, List<RuleIssue> issues)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode m)
            {
                issues.Add(new RuleIssue(file, null, "文件内容应该是一个映射（包含 app 和 locations）"));
                return;
            }

            root = m;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            issues.Add(new RuleIssue(file, null, $"YAML 格式错误（第 {ex.Start.Line} 行）：{ex.Message}"));
            return;
        }

        string? appId = null;
        foreach (var (k, v) in root.Children)
        {
            var key = ((YamlScalarNode)k).Value;
            switch (key)
            {
                case "app" when v is YamlMappingNode appNode:
                    // 整个文件只讲一个应用：所有位置默认归它
                    var app = ReadApp(file, appNode, issues);
                    if (app is not null)
                    {
                        apps.Add(app);
                        appId = app.Id;
                    }

                    break;
                case "apps" when v is YamlSequenceNode appList:
                    // 一个文件讲多个应用：位置用 app: <id> 指明归属
                    foreach (var item in appList.Children)
                    {
                        if (item is YamlMappingNode m && ReadApp(file, m, issues) is { } listed)
                        {
                            apps.Add(listed);
                        }
                        else if (item is not YamlMappingNode)
                        {
                            issues.Add(new RuleIssue(file, null, "apps 的每一项应该是映射"));
                        }
                    }

                    break;
                case "apps":
                    issues.Add(new RuleIssue(file, null, "apps 应该是列表"));
                    break;
                case "locations":
                    break;
                default:
                    issues.Add(new RuleIssue(file, null, $"不认识的顶层键：{key}"));
                    break;
            }
        }

        if (root.Children.TryGetValue(new YamlScalarNode("locations"), out var list))
        {
            if (list is not YamlSequenceNode seq)
            {
                issues.Add(new RuleIssue(file, null, "locations 应该是列表"));
                return;
            }

            foreach (var item in seq.Children)
            {
                if (item is YamlMappingNode map && ReadLocation(file, map, appId, issues) is { } rule)
                {
                    locations.Add(rule);
                }
                else if (item is not YamlMappingNode)
                {
                    issues.Add(new RuleIssue(file, null, "locations 的每一项应该是映射"));
                }
            }
        }
    }

    private static AppInfo? ReadApp(string file, YamlMappingNode node, List<RuleIssue> issues)
    {
        CheckKeys(file, null, node, AppKeys, issues);
        var id = Str(node, "id");
        var name = Str(node, "name");
        if (id is null || name is null)
        {
            issues.Add(new RuleIssue(file, id, "app 必须有 id 和 name"));
            return null;
        }

        if (!IdPattern().IsMatch(id))
        {
            issues.Add(new RuleIssue(file, id, "id 只能用小写字母、数字、点和横线"));
        }

        return new AppInfo(id, name, Str(node, "vendor"), List(node, "exes"), List(node, "processes"));
    }

    private static LocationRule? ReadLocation(string file, YamlMappingNode node, string? appId, List<RuleIssue> issues)
    {
        var id = Str(node, "id");
        void Issue(string message) => issues.Add(new RuleIssue(file, id, message));

        CheckKeys(file, id, node, LocationKeys, issues);
        if (id is null)
        {
            Issue("缺少 id");
            return null;
        }

        if (!IdPattern().IsMatch(id))
        {
            Issue("id 只能用小写字母、数字、点和横线");
        }

        var modes = new[] { ("dir", MatchMode.Dir), ("file", MatchMode.File), ("under", MatchMode.Under) }
            .Where(m => Str(node, m.Item1) is not null).ToList();
        if (modes.Count != 1)
        {
            Issue("dir、file、under 必须且只能写一个");
            return null;
        }

        var (modeKey, mode) = modes[0];
        var pattern = Str(node, modeKey)!;

        Kind kind = default;
        Risk risk = default;
        var tier = Tier.Reviewed;
        if (Str(node, "kind") is not { } kindText || !Names.TryParse(kindText, out kind))
        {
            Issue($"kind 无效：{Str(node, "kind")}");
        }

        if (Str(node, "risk") is not { } riskText || !Names.TryParse(riskText, out risk))
        {
            Issue($"risk 无效：{Str(node, "risk")}");
        }

        if (Str(node, "tier") is { } tierText && !Names.TryParse(tierText, out tier))
        {
            Issue($"tier 无效：{tierText}");
        }

        var conditions = new List<Condition>();
        foreach (var (key, type) in new[] { ("has_any", ConditionType.HasAny), ("sibling_any", ConditionType.SiblingAny), ("not_under", ConditionType.NotUnder) })
        {
            var values = List(node, key);
            if (values.Count > 0)
            {
                conditions.Add(new Condition(type, values));
            }
        }

        var actions = new List<RuleAction>();
        if (node.Children.TryGetValue(new YamlScalarNode("actions"), out var actionsNode))
        {
            if (actionsNode is YamlSequenceNode seq)
            {
                foreach (var a in seq.Children.OfType<YamlMappingNode>())
                {
                    if (ReadAction(a, Issue) is { } action)
                    {
                        actions.Add(action);
                    }
                }
            }
            else
            {
                Issue("actions 应该是列表");
            }
        }

        Relocation? relocate = null;
        if (node.Children.TryGetValue(new YamlScalarNode("relocate"), out var relocateNode) && relocateNode is YamlMappingNode rm)
        {
            relocate = new Relocation(Str(rm, "env"), Str(rm, "note"));
        }

        var evidence = new List<Evidence>();
        if (node.Children.TryGetValue(new YamlScalarNode("evidence"), out var evidenceNode) && evidenceNode is YamlSequenceNode es)
        {
            foreach (var e in es.Children.OfType<YamlMappingNode>())
            {
                evidence.Add(new Evidence(Str(e, "url"), Str(e, "note")));
            }
        }

        return new LocationRule
        {
            Id = id,
            AppId = Str(node, "app") ?? appId,
            Mode = mode,
            Pattern = pattern,
            Conditions = conditions,
            Kind = kind,
            Risk = risk,
            Tier = tier,
            Label = Str(node, "label"),
            Regenerates = Bool(node, "regenerates"),
            RequiresClosed = Bool(node, "requires_closed"),
            Purpose = Str(node, "purpose") ?? "",
            Impact = Str(node, "impact") ?? "",
            Actions = actions,
            Relocate = relocate,
            Evidence = evidence,
            SourceFile = file,
        };
    }

    private static RuleAction? ReadAction(YamlMappingNode a, Action<string> issue)
    {
        if (Str(a, "delete") is { } scope)
        {
            var s = scope switch
            {
                "contents" => DeleteScope.Contents,
                "children" => DeleteScope.Children,
                "self" => DeleteScope.Self,
                _ => (DeleteScope?)null,
            };
            if (s is null)
            {
                issue($"delete 的范围无效：{scope}（contents / children / self）");
                return null;
            }

            return new DeleteAction(s.Value, Int(a, "older_than_days"), Int(a, "keep_latest"));
        }

        if (Str(a, "command") is { } command)
        {
            return new CommandAction(command);
        }

        if (Str(a, "guide") is { } guide)
        {
            return new GuideAction(guide, Str(a, "url"));
        }

        issue("动作必须是 delete、command 或 guide 之一");
        return null;
    }

    private static void CheckKeys(string file, string? id, YamlMappingNode node, HashSet<string> allowed, List<RuleIssue> issues)
    {
        foreach (var k in node.Children.Keys.OfType<YamlScalarNode>())
        {
            if (k.Value is { } key && !allowed.Contains(key))
            {
                issues.Add(new RuleIssue(file, id, $"不认识的字段：{key}"));
            }
        }
    }

    private static string? Str(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s && !string.IsNullOrWhiteSpace(s.Value) ? s.Value.Trim() : null;

    private static bool Bool(YamlMappingNode node, string key) => Str(node, key) is "true" or "yes";

    private static int? Int(YamlMappingNode node, string key) =>
        Str(node, key) is { } s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static List<string> List(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var v))
        {
            return [];
        }

        return v switch
        {
            YamlSequenceNode seq => seq.Children.OfType<YamlScalarNode>().Select(s => s.Value ?? "").Where(s => s.Length > 0).ToList(),
            YamlScalarNode s when !string.IsNullOrWhiteSpace(s.Value) => [s.Value],
            _ => [],
        };
    }
}

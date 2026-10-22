using System.Globalization;
using System.Text.Json;
using DiskThinner.Scanner;
using DiskThinner.Tests.Shared;

namespace DiskThinner.Rules.Tests;

/// <summary>
/// 规则匹配的 JSON fixtures（tests/fixtures/matching/*.json，格式见 tests/fixtures/README.md）。
/// 每个文件里的每个用例是一个测试；以后换别的语言实现时读同样的文件做同样的检查。
/// </summary>
public sealed class FixtureTests
{
    private const string Kind = "matching";

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var file in Fixtures.Files(Kind))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            {
                data.Add(Path.GetFileName(file), c.GetProperty("name").GetString()!);
            }
        }

        return data;
    }

    [Fact]
    public void FixturesExist() => Assert.True(Cases().Count >= 15, $"只找到 {Cases().Count} 个用例");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matching(string file, string name)
    {
        using var doc = Fixtures.Load(Kind, file);
        var root = doc.RootElement;
        var tree = Fixtures.Tree(root.GetProperty("tree"));
        var testCase = root.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var rules = testCase.GetProperty("rules").EnumerateArray().Select(ReadRule).ToArray();
        var c = new RuleEngine(new RuleSet([], rules), PathContext.Default).Classify(tree);

        var expect = testCase.GetProperty("expect");
        var failures = new List<string>();
        if (expect.TryGetProperty("paths", out var paths))
        {
            foreach (var p in paths.EnumerateObject())
            {
                var node = tree.FindPath(Fixtures.Path(p.Name));
                if (node < 0)
                {
                    failures.Add($"{p.Name}：树里没有这个路径");
                    continue;
                }

                foreach (var check in p.Value.EnumerateObject())
                {
                    var (expected, actual) = check.Name switch
                    {
                        "rule" => (Str(check.Value), c.RuleOf(node)?.Id),
                        "risk" => (Str(check.Value), c.EffectiveRisk(node) is { } r ? Names.Code(r) : null),
                        "protection" => (check.Value.GetString(), c.ProtectionLevelOf(node).ToString().ToLowerInvariant()),
                        "cleanable" => (Num(Fixtures.Size(check.Value)), Num(c.Cleanable(node))),
                        "unexplained" => (Num(Fixtures.Size(check.Value)), Num(c.Unexplained(node))),
                        "root" => (check.Value.GetBoolean().ToString(), c.IsRoot(node).ToString()),
                        "root_bytes" => (Num(Fixtures.Size(check.Value)), c.RootOf(node) is { } m ? Num(m.Bytes) : null),
                        "root_cleanable" => (Num(Fixtures.Size(check.Value)), c.RootOf(node) is { } m ? Num(m.CleanableBytes) : null),
                        "root_shared" => (Num(Fixtures.Size(check.Value)), c.RootOf(node) is { } m ? Num(m.SharedBytes) : null),
                        _ => throw new InvalidDataException($"{file} / {name}：不认识的检查项 {check.Name}"),
                    };
                    if (expected != actual)
                    {
                        failures.Add($"{p.Name} 的 {check.Name}：期望 {expected ?? "null"}，实际 {actual ?? "null"}");
                    }
                }
            }
        }

        if (expect.TryGetProperty("totals", out var totals))
        {
            foreach (var t in totals.EnumerateObject())
            {
                switch (t.Name)
                {
                    case "cleanable":
                        Compare("可清理合计", Fixtures.Size(t.Value), c.TotalCleanable);
                        break;
                    case "roots":
                        Compare("规则位置个数", t.Value.GetInt64(), c.Roots.Count);
                        break;
                    case "cleanable_by_risk":
                        foreach (var r in t.Value.EnumerateObject())
                        {
                            Assert.True(Names.TryParse(r.Name, out Risk risk), r.Name);
                            Compare($"风险 {r.Name} 的可清理", Fixtures.Size(r.Value), c.CleanableBytes(risk));
                        }

                        break;
                    default:
                        throw new InvalidDataException($"{file} / {name}：不认识的合计项 {t.Name}");
                }
            }
        }

        // 逐路径的保护检查（执行清理前复核用的）和引擎在树上的结果一致
        var protection = new ProtectionCheck(PathContext.Default);
        for (var n = 0; n < tree.Count; n++)
        {
            if ((tree[n].Flags & NodeFlags.Metadata) == 0 && n != tree.Root
                && protection.LevelFor(tree.GetPath(n)) is var level && level != c.ProtectionLevelOf(n))
            {
                failures.Add($"{tree.GetPath(n)}：逐路径检查是 {level}，引擎是 {c.ProtectionLevelOf(n)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));

        void Compare(string what, long expected, long actual)
        {
            if (expected != actual)
            {
                failures.Add($"{what}：期望 {expected}，实际 {actual}");
            }
        }
    }

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    /// <summary>fixtures 里的规则：字段和 rules/*.yaml 一样，路径用正斜杠。</summary>
    internal static LocationRule ReadRule(JsonElement r)
    {
        var modes = new[] { ("dir", MatchMode.Dir), ("file", MatchMode.File), ("under", MatchMode.Under) }
            .Where(m => r.TryGetProperty(m.Item1, out _)).ToList();
        Assert.Single(modes);
        var (key, mode) = modes[0];
        Assert.True(Names.TryParse(r.GetProperty("kind").GetString()!, out Rules.Kind kind), "kind");
        Assert.True(Names.TryParse(r.GetProperty("risk").GetString()!, out Risk risk), "risk");
        var tier = Tier.Reviewed;
        if (r.TryGetProperty("tier", out var t))
        {
            Assert.True(Names.TryParse(t.GetString()!, out tier), "tier");
        }

        var conditions = new List<Condition>();
        foreach (var (name, type) in new[] { ("has_any", ConditionType.HasAny), ("sibling_any", ConditionType.SiblingAny), ("not_under", ConditionType.NotUnder) })
        {
            if (r.TryGetProperty(name, out var list))
            {
                conditions.Add(new Condition(type, list.EnumerateArray().Select(v => Fixtures.Path(v.GetString()!)).ToList()));
            }
        }

        // 不写 actions 时：风险不高于 medium 的默认带一个删除动作（只有可执行的规则才计入可清理）
        List<RuleAction> actions = risk <= Risk.Medium ? [new DeleteAction(DeleteScope.Contents)] : [];
        if (r.TryGetProperty("actions", out var a))
        {
            actions = a.EnumerateArray().Select(ReadAction).ToList();
        }

        return new LocationRule
        {
            Id = r.GetProperty("id").GetString()!,
            Mode = mode,
            Pattern = Fixtures.Path(r.GetProperty(key).GetString()!),
            Kind = kind,
            Risk = risk,
            Tier = tier,
            Purpose = "测试",
            Impact = "测试",
            Conditions = conditions,
            Actions = actions,
        };
    }

    private static RuleAction ReadAction(JsonElement a)
    {
        if (a.TryGetProperty("delete", out var d))
        {
            var scope = d.GetString() switch
            {
                "contents" => DeleteScope.Contents,
                "children" => DeleteScope.Children,
                "self" => DeleteScope.Self,
                var s => throw new InvalidDataException("delete 的范围无效：" + s),
            };
            int? Int(string name) => a.TryGetProperty(name, out var v) ? v.GetInt32() : null;
            return new DeleteAction(scope, Int("older_than_days"), Int("keep_latest"));
        }

        if (a.TryGetProperty("command", out var c))
        {
            return new CommandAction(c.GetString()!);
        }

        return new GuideAction(a.GetProperty("guide").GetString()!);
    }
}

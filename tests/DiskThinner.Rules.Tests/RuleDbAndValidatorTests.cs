using DiskThinner.Rules;
using DiskThinner.RuleTools;
using Microsoft.Data.Sqlite;
using static DiskThinner.Rules.Tests.Helpers;

namespace DiskThinner.Rules.Tests;

public sealed class RuleDbTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-rules-" + Guid.NewGuid().ToString("N"));

    public RuleDbTests() => Directory.CreateDirectory(_dir);

    private string DbPath(string name = "rules.db") => Path.Combine(_dir, name);

    private static RuleSet SampleRules() => new(
        [new AppInfo("astral.uv", "uv", "Astral", ["uv.exe"], ["uv.exe"])],
        [
            Rule("a", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low, Kind.PackageCache) with
            {
                AppId = "astral.uv",
                Actions = [new CommandAction("uv.cache-clean"), new DeleteAction(DeleteScope.Children, 7, 2), new GuideAction("说明", "https://example.com")],
                Relocate = new Relocation("UV_CACHE_DIR", null),
                Conditions = [new Condition(ConditionType.SiblingAny, ["x", "y"])],
                Label = "标签",
                Regenerates = true,
            },
            Rule("b", MatchMode.Under, @"%WINDIR%", Risk.Never, Kind.System),
        ])
    { Version = "test" };

    [Fact]
    public void RoundTrip()
    {
        RuleDb.Write(SampleRules(), DbPath());
        var back = RuleDb.Read(DbPath());
        Assert.Equal("test", back.Version);
        Assert.Equal(2, back.Locations.Count);
        var a = back.Locations[0];
        Assert.Equal("astral.uv", a.AppId);
        Assert.Equal(MatchMode.Dir, a.Mode);
        Assert.Equal(Risk.Low, a.Risk);
        Assert.Equal("标签", a.Label);
        Assert.True(a.Regenerates);
        Assert.Equal("UV_CACHE_DIR", a.Relocate?.Env);
        Assert.Equal(["x", "y"], a.Conditions.Single().Patterns);
        Assert.IsType<CommandAction>(a.Actions[0]);
        Assert.Equal(new DeleteAction(DeleteScope.Children, 7, 2), a.Actions[1]);
        Assert.Equal("https://example.com", ((GuideAction)a.Actions[2]).Url);
        Assert.Equal(["uv.exe"], back.Apps.Single().DetectExes);
        Assert.Equal(MatchMode.Under, back.Locations[1].Mode);
    }

    [Fact]
    public void SameInputGivesSameFile()
    {
        RuleDb.Write(SampleRules(), DbPath("1.db"));
        RuleDb.Write(SampleRules(), DbPath("2.db"));
        Assert.Equal(File.ReadAllBytes(DbPath("1.db")), File.ReadAllBytes(DbPath("2.db")));
    }

    [Fact]
    public void RejectsExtraObjects()
    {
        RuleDb.Write(SampleRules(), DbPath());
        Modify(DbPath(), "CREATE TRIGGER t AFTER INSERT ON meta BEGIN SELECT 1; END");
        Assert.Throws<InvalidDataException>(() => RuleDb.Read(DbPath()));
    }

    [Fact]
    public void RejectsWrongVersion()
    {
        RuleDb.Write(SampleRules(), DbPath());
        Modify(DbPath(), "PRAGMA user_version = 99");
        Assert.Throws<InvalidDataException>(() => RuleDb.Read(DbPath()));
    }

    [Fact]
    public void UnknownValuesBecomeMostConservative()
    {
        RuleDb.Write(SampleRules(), DbPath());
        Modify(DbPath(), "UPDATE locations SET risk = 'super-safe' WHERE id = 'a'");
        var rule = RuleDb.Read(DbPath()).Locations.First(r => r.Id == "a");
        Assert.Equal(Risk.Never, rule.Risk);
    }

    [Fact]
    public void UnknownCommandIsDropped()
    {
        RuleDb.Write(SampleRules(), DbPath());
        Modify(DbPath(), "UPDATE actions SET command_id = 'cmd.format-c' WHERE type = 'command'");
        var rule = RuleDb.Read(DbPath()).Locations.First(r => r.Id == "a");
        Assert.DoesNotContain(rule.Actions, a => a is CommandAction);
    }

    private static void Modify(string path, string sql)
    {
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}

public class RuleValidatorTests
{
    private static List<RuleIssue> Errors(params LocationRule[] rules) =>
        RuleValidator.Validate(new RuleSet([], rules), []).Errors;

    [Fact]
    public void RepositoryRulesHaveNoErrors()
    {
        var (rules, issues) = RuleLoader.LoadDirectory(Path.Combine(RepoRoot(), "rules"));
        var (errors, _) = RuleValidator.Validate(rules, issues);
        Assert.Empty(errors);
        Assert.True(rules.Locations.Count >= 100, $"规则只有 {rules.Locations.Count} 条");
    }

    [Fact]
    public void RepositoryRulesClassifyCredentialsAsNever()
    {
        var (rules, _) = RuleLoader.LoadDirectory(Path.Combine(RepoRoot(), "rules"));
        var tree = Tree(
            (@"C:\Users\demo\.claude\.credentials.json", 1),
            (@"C:\Users\demo\.claude\projects\p\s.jsonl", 100),
            (@"C:\Users\demo\.codex\auth.json", 1),
            (@"C:\Users\demo\AppData\Roaming\Code\Local Storage\leveldb\000003.log", 50),
            (@"C:\Users\demo\AppData\Roaming\Code\Cache\data", 50),
            (@"C:\Windows\System32\x.dll", 1),
            (@"C:\Windows\Temp\t.tmp", 1));
        var c = new RuleEngine(rules, PathContext.Default).Classify(tree);
        Assert.Equal(Risk.Never, c.RiskAt(@"C:\Users\demo\.claude\.credentials.json"));
        Assert.Equal(Risk.Never, c.RiskAt(@"C:\Users\demo\.codex\auth.json"));
        Assert.Equal(Risk.Medium, c.RiskAt(@"C:\Users\demo\.claude\projects\p\s.jsonl"));
        Assert.Equal(Risk.Never, c.RiskAt(@"C:\Windows\System32\x.dll"));
        Assert.Equal(Risk.Safe, c.RiskAt(@"C:\Windows\Temp\t.tmp"));
        Assert.Equal(Risk.Safe, c.RiskAt(@"C:\Users\demo\AppData\Roaming\Code\Cache\data"));

        // LevelDB 的事务日志不能被当成普通日志删除；它只被兜底规则说明，不算可清理
        var leveldb = tree.FindPath(@"C:\Users\demo\AppData\Roaming\Code\Local Storage\leveldb\000003.log");
        Assert.False(c.IsCleanableNode(leveldb));
        Assert.Equal(MatchMode.Under, c.RuleOf(leveldb)?.Mode);
        Assert.Equal(0, c.Cleanable(leveldb));
        Assert.True(c.IsCleanableNode(tree.FindPath(@"C:\Users\demo\AppData\Roaming\Code\Cache\data")));
    }

    [Fact]
    public void CleanableRuleInsideProtectionIsRejected()
    {
        Assert.Contains(Errors(Rule("bad", MatchMode.Dir, @"%USERPROFILE%\.ssh")), e => e.Message.Contains("保护名单"));
        Assert.Contains(Errors(Rule("bad", MatchMode.Dir, @"%WINDIR%\System32\DriverStore")), e => e.Message.Contains("保护名单"));
        Assert.Contains(Errors(Rule("bad", MatchMode.File, @"%SYSTEMDRIVE%\pagefile.sys")), e => e.Message.Contains("系统工具"));
        Assert.Empty(Errors(Rule("ok", MatchMode.Dir, @"%WINDIR%\Temp")));
    }

    [Fact]
    public void PathRulesAreEnforced()
    {
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"C:\Temp")));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%USERPROFILE%")));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%USERPROFILE%\**")));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%USERPROFILE%\..\x")));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%USERPROFILE%\**\a\**\b")));
        Assert.Empty(Errors(Rule("ok", MatchMode.Under, @"%WINDIR%", Risk.Never, Kind.System)));
    }

    [Fact]
    public void RiskAndActionRulesAreEnforced()
    {
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.File, @"%USERPROFILE%\.foo\token", Risk.Medium, Kind.Credential)));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%LOCALAPPDATA%\foo", Risk.Safe, Kind.Cache, Tier.Draft)));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%LOCALAPPDATA%\foo", Risk.Low) with { Evidence = [] }));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%LOCALAPPDATA%\foo", Risk.Low) with { Actions = [new CommandAction("rm.rf")] }));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%LOCALAPPDATA%\foo", Risk.High, Kind.AppData) with { Actions = [new DeleteAction(DeleteScope.Contents)] }));
        Assert.NotEmpty(Errors(Rule("bad", MatchMode.Dir, @"%LOCALAPPDATA%\foo", Risk.Low, Kind.Cache, Tier.Draft) with { Actions = [new CommandAction("uv.cache-clean")] }));
        Assert.Empty(Errors(Rule("ok", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low) with { Actions = [new CommandAction("uv.cache-clean")] }));
    }

    [Fact]
    public void LoaderRejectsUnknownFields()
    {
        var apps = new List<AppInfo>();
        var locations = new List<LocationRule>();
        var issues = new List<RuleIssue>();
        RuleLoader.LoadFile("t.yaml", """
            locations:
              - id: x.y
                dir: '%LOCALAPPDATA%\x'
                kind: cache
                risk: safe
                purpose: a
                impact: b
                evidenc:
                  - note: typo
            """, apps, locations, issues);
        Assert.Contains(issues, i => i.Message.Contains("evidenc"));
    }

    [Fact]
    public void LoaderReadsAppListAndPerLocationApp()
    {
        var apps = new List<AppInfo>();
        var locations = new List<LocationRule>();
        var issues = new List<RuleIssue>();
        RuleLoader.LoadFile("t.yaml", """
            apps:
              - { id: uv, name: uv, vendor: Astral, exes: [uv.exe] }
              - { id: npm, name: npm }
            locations:
              - id: dev.uv.cache
                app: uv
                dir: '%LOCALAPPDATA%\uv\cache'
                kind: package-cache
                risk: low
                purpose: a
                impact: b
                evidence:
                  - note: c
              - id: dev.other
                dir: '%LOCALAPPDATA%\other'
                kind: cache
                risk: high
                purpose: a
                impact: b
            """, apps, locations, issues);
        Assert.Empty(issues);
        Assert.Equal(["uv", "npm"], apps.Select(a => a.Id));
        Assert.Equal(["uv.exe"], apps[0].DetectExes);
        Assert.Equal("uv", locations[0].AppId);
        Assert.Null(locations[1].AppId);
    }

    [Fact]
    public void ValidatorRejectsUndefinedApp()
    {
        var rule = Rule("dev.x", MatchMode.Dir, @"%LOCALAPPDATA%\x", Risk.High) with { AppId = "missing" };
        Assert.Contains(Errors(rule), e => e.Message.Contains("没有定义的应用"));
    }

    [Fact]
    public void RepositoryRulesNameTheirApps()
    {
        var (rules, _) = RuleLoader.LoadDirectory(Path.Combine(RepoRoot(), "rules"));
        Assert.Equal("uv", rules.App(rules.Locations.Single(r => r.Id == "dev.uv.cache").AppId)?.Name);
        Assert.Equal("Claude Code", rules.App(rules.Locations.Single(r => r.Id == "anthropic.claude-code.projects").AppId)?.Name);

        // 同时覆盖几种编辑器的规则不写应用，由推断决定
        Assert.Null(rules.Locations.Single(r => r.Id == "dev.vscode.workspace-storage").AppId);
        Assert.True(rules.Apps.Count >= 30);
    }
}

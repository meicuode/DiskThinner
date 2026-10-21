using DiskThinner.Rules;
using DiskThinner.Storage;

namespace DiskThinner.Cleanup.Tests;

/// <summary>清理计划：分组和默认处理方式、过滤条件下的预计释放、账户和用户目录、执行前的规则复核。</summary>
public sealed class PlanTests : IDisposable
{
    private const long Kb = 1024;
    private readonly Fixture _f = new();
    private static readonly DateTime Now = DateTime.UtcNow;

    public void Dispose() => _f.Dispose();

    private string L(string relative) => Path.Combine(_f.Local, relative);

    [Fact]
    public void GroupsByRiskWithDefaultMethods()
    {
        _f.File(L(@"Cache\a.bin"), 256 * Kb);
        _f.File(L(@"uv\cache\wheel.whl"), 512 * Kb);
        _f.File(Path.Combine(_f.Profile, @".tool\sessions\s1.jsonl"), 128 * Kb);
        var uv = KnownCommands.Find("uv.cache-clean")!;
        var rules = new[]
        {
            Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents)),
            Fixture.Rule("t.uv", @"%LOCALAPPDATA%\uv\cache", Risk.Low, new CommandAction(uv.Id), new DeleteAction(DeleteScope.Contents)),
            Fixture.Rule("t.sessions", @"%USERPROFILE%\.tool\sessions", Risk.Medium, new DeleteAction(DeleteScope.Children)),
        };
        var env = _f.Plan(rules, new PlanOptions { ResolveCommand = c => c == uv ? @"C:\tools\uv.exe" : null });

        var cache = env.Item(L("Cache"));
        Assert.Equal(PlanGroup.Safe, cache.Group);
        Assert.Equal(MethodKind.Delete, cache.Method!.Kind);
        Assert.True(cache.Selected);

        var cmd = env.Item(L(@"uv\cache"));
        Assert.Equal(PlanGroup.Redownload, cmd.Group);
        Assert.Equal(MethodKind.Command, cmd.Method!.Kind);
        Assert.Equal(@"C:\tools\uv.exe", cmd.Method.CommandPath);
        Assert.Equal(["uv cache clean", "直接删除", "手动清理（打开所在位置）"], cmd.Methods.Select(m => m.Display));

        var sessions = env.Item(Path.Combine(_f.Profile, @".tool\sessions"));
        Assert.Equal(PlanGroup.Irreversible, sessions.Group);
        Assert.Equal([PlanGroup.Safe, PlanGroup.Redownload, PlanGroup.Irreversible], env.Plan.Items.Select(i => i.Group));
    }

    [Fact]
    public void CommandsThatAreNotFoundFallBackToDeleteAndSayWhy()
    {
        _f.File(L(@"uv\cache\wheel.whl"), 512 * Kb);
        var env = _f.Plan([Fixture.Rule("t.uv", @"%LOCALAPPDATA%\uv\cache", Risk.Low, new CommandAction("uv.cache-clean"), new DeleteAction(DeleteScope.Contents))]);
        var item = env.Item(L(@"uv\cache"));
        Assert.Equal(MethodKind.Delete, item.Method!.Kind);
        Assert.Contains("没有找到 uv.exe", item.Hint);
    }

    [Fact]
    public void OlderThanKeepsRecentFilesAndRecentDirectories()
    {
        var old = Now.AddDays(-3);
        _f.File(L(@"Temp\old.bin"), 128 * Kb, old);
        var fresh = _f.File(L(@"Temp\new.bin"), 128 * Kb);
        _f.File(L(@"Temp\olddir\x.bin"), 64 * Kb, old);
        _f.Dir(L(@"Temp\olddir"), old);
        _f.File(L(@"Temp\busydir\y.bin"), 64 * Kb, old);
        var rule = Fixture.Rule("t.temp", @"%LOCALAPPDATA%\Temp", Risk.Safe, new DeleteAction(DeleteScope.Children, OlderThanDays: 1));
        var env = _f.Plan([rule], new PlanOptions { NowUtc = Now });
        var item = env.Item(L("Temp"));
        Assert.Equal((128 + 64 + 64) * Kb, item.EstimatedBytes);
        Assert.Contains("只删 24 小时前的文件", item.Hint);

        var r = Assert.Single(env.Run(item).Results);
        Assert.Equal(item.EstimatedBytes, r.Delete!.FreedEstimate);
        Assert.True(File.Exists(fresh));
        Assert.False(Directory.Exists(L(@"Temp\olddir")));
        Assert.False(File.Exists(L(@"Temp\busydir\y.bin")));
        Assert.True(Directory.Exists(L(@"Temp\busydir")), "最近改动过的目录即使空了也保留");
    }

    [Fact]
    public void KeepLatestKeepsTheNewestChildren()
    {
        for (var i = 1; i <= 4; i++)
        {
            var t = Now.AddDays(-10 + i);
            _f.File(L($@"Tool\versions\v{i}\tool.bin"), 64 * Kb, t);
            _f.Dir(L($@"Tool\versions\v{i}"), t);
        }

        var rule = Fixture.Rule("t.versions", @"%LOCALAPPDATA%\Tool\versions", Risk.Safe, new DeleteAction(DeleteScope.Children, KeepLatest: 2));
        var env = _f.Plan([rule]);
        var item = env.Item(L(@"Tool\versions"));
        Assert.Equal(2 * 64 * Kb, item.EstimatedBytes);

        env.Run(item);
        Assert.Equal(["v3", "v4"], Directory.GetDirectories(L(@"Tool\versions")).Select(d => Path.GetFileName(d)).Order());
    }

    [Fact]
    public void AccountMismatchBlocksUserDirectoriesAndCommands()
    {
        _f.File(L(@"Cache\a.bin"), 256 * Kb);
        var account = new AccountInfo(@"PC\admin", @"PC\demo", Elevated: true);
        var env = _f.Plan([Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents))], new PlanOptions { Account = account });

        var item = Assert.Single(env.Plan.Items);
        Assert.Equal(PlanGroup.Blocked, item.Group);
        Assert.False(item.Selectable);
        Assert.Contains("不一致", item.BlockReason);
        Assert.NotNull(env.Plan.Warning);
    }

    [Fact]
    public void ExecutorRefusesUserDirectoriesWhenTheAccountDoesNotMatch()
    {
        _f.File(L(@"Cache\a.bin"), 256 * Kb);
        var env = _f.Plan([Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents))]);
        var r = Assert.Single(env.Run(new ExecutorOptions { Account = new AccountInfo(@"PC\admin", @"PC\demo", true) }, env.Item(L("Cache"))).Results);
        Assert.Equal(AuditResult.NotRun, r.Result);
        Assert.True(File.Exists(L(@"Cache\a.bin")));
    }

    [Fact]
    public void OtherUsersDirectoriesAreBlocked()
    {
        var other = Path.Combine(_f.Root, @"Users\someone\AppData\Local\Cache\a.bin");
        _f.File(other, 256 * Kb);
        var env = _f.Plan([Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents))]);
        var item = Assert.Single(env.Plan.Items);
        Assert.Equal(PlanGroup.Blocked, item.Group);
        Assert.Equal("其他用户的目录", item.BlockReason);
    }

    [Fact]
    public void ProjectDependenciesAreListedButNotSelected()
    {
        _f.File(Path.Combine(_f.Profile, @"src\app\node_modules\pkg\index.js"), 128 * Kb);
        var rule = Fixture.Rule("t.node", @"%USERPROFILE%\**\node_modules", Risk.Low, new DeleteAction(DeleteScope.Self)) with { Kind = Kind.BuildOutput };
        var env = _f.Plan([rule]);
        var item = env.Item(Path.Combine(_f.Profile, @"src\app\node_modules"));
        Assert.False(item.Selected);
        Assert.Contains("默认不勾选", item.Hint);
    }

    [Fact]
    public void ConditionsAreCheckedAgainAgainstTheDisk()
    {
        var marker = _f.File(Path.Combine(_f.Profile, @"proj\.venv\pyvenv.cfg"), 1 * Kb);
        _f.File(Path.Combine(_f.Profile, @"proj\.venv\Lib\site.bin"), 256 * Kb);
        var rule = Fixture.Rule("t.venv", @"%USERPROFILE%\**\.venv", Risk.Low, new DeleteAction(DeleteScope.Self)) with
        {
            Conditions = [new Condition(ConditionType.HasAny, ["pyvenv.cfg"])],
        };
        var env = _f.Plan([rule]);
        var item = env.Item(Path.Combine(_f.Profile, @"proj\.venv"));
        item.Selected = true;

        File.Delete(marker); // 计划之后它已经不再是虚拟环境了
        var r = Assert.Single(env.Run(item).Results);
        Assert.Equal(AuditResult.NotRun, r.Result);
        Assert.Contains("不再命中", r.Text);
        Assert.True(File.Exists(Path.Combine(_f.Profile, @"proj\.venv\Lib\site.bin")));
    }

    [Fact]
    public void ManualItemsAreNeverExecuted()
    {
        var file = _f.File(L(@"Cache\a.bin"), 256 * Kb);
        var env = _f.Plan([Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents))]);
        var item = env.Item(L("Cache"));
        item.Method = PlanMethod.Manual;
        Assert.Equal(0, item.EstimatedBytes);

        var run = env.Run(item);
        Assert.Empty(run.Results);
        Assert.True(File.Exists(file));
        Assert.Empty(env.Audit.List());
    }

    [Fact]
    public void CurrentAccountMatchesTheLoggedOnUser()
    {
        // 测试和登录用户是同一个账户运行的：不应判为不一致
        var account = AccountInfo.Current();
        Assert.False(string.IsNullOrEmpty(account.ProcessUser));
        Assert.NotNull(account.SessionUser);
        Assert.False(account.Mismatch, $"{account.ProcessUser} / {account.SessionUser}");
        Assert.Null(account.MismatchText);
    }

    [Fact]
    public void TopLevelDirectoriesAreNeverDeleted()
    {
        Assert.True(Verifier.IsStructural(@"C:\", PathContext.Default));
        Assert.True(Verifier.IsStructural(@"C:\Users", PathContext.Default));
        Assert.True(Verifier.IsStructural(@"C:\Users\demo", PathContext.Default));
        Assert.True(Verifier.IsStructural(@"C:\Windows", PathContext.Default));
        Assert.True(Verifier.IsStructural(@"C:\Program Files", PathContext.Default));
        Assert.False(Verifier.IsStructural(@"C:\Users\demo\AppData\Local\Temp", PathContext.Default));
        Assert.False(Verifier.IsStructural(@"C:\Windows\Temp", PathContext.Default));
    }

    [Fact]
    public void MethodsOutsideThePlanAreRefused()
    {
        var file = _f.File(L(@"Cache\a.bin"), 256 * Kb);
        var env = _f.Plan([Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents))]);
        var item = env.Item(L("Cache"));
        item.Method = new PlanMethod(MethodKind.Delete, "直接删除") { Delete = new DeleteAction(DeleteScope.Self) };

        var r = Assert.Single(env.Run(item).Results);
        Assert.Equal(AuditResult.NotRun, r.Result);
        Assert.True(File.Exists(file));
    }
}

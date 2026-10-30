using DiskThinner.App.Agent;
using DiskThinner.App.Dialogs;
using DiskThinner.App.Growth;
using DiskThinner.App.Monitor;
using DiskThinner.App.Pages;
using DiskThinner.App.Rules;
using DiskThinner.App.Settings;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Agent;
using DiskThinner.Monitor.Etw;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;
using Microsoft.Data.Sqlite;

namespace DiskThinner.App.Tests;

/// <summary>
/// 常驻服务在界面上的表现（docs/phase2b-design.md 3.2 节 Q1–Q7、5.6 节、8.1 节）：会话合并、数据合并、服务状态的文字、
/// 设置页服务一组的各种状态、安装对话框、增长分析的"常驻"行、ETW 证据的写法、实测归属（E11）、命令行参数、MonitorHost 的状态和合并查询。
/// 服务换成假的 <see cref="IAgentSource"/>，不装真实的服务。
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class AgentUiTests : IDisposable
{
    private const long Mb = 1L << 20;
    private const string Local = @"Users\demo\AppData\Local";
    private static readonly DateTime T0 = new(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-agent-ui-" + Guid.NewGuid().ToString("N"));

    private static readonly StoredChain Uv = new(ChainKind.Normal, new StoredImage(@"C:\Users\demo\.local\bin\uv.exe", "", "uv", "Astral"),
        new StoredImage(@"C:\Program Files\Code\Code.exe", "", null, null), "uv.exe ← pwsh.exe ← Code.exe", 0);

    private static readonly StoredChain Node = new(ChainKind.Normal, new StoredImage(@"C:\nodejs\node.exe", "", null, null), new StoredImage(@"C:\nodejs\node.exe", "", null, null), "node.exe", 0);

    public AgentUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (var i = 0; i < 10; i++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>常驻服务的一天一行（会话号取负数），最后一行可以没有结束。</summary>
    private static List<SessionRecord> AgentDays(DateTime start, int days, bool lastOpen, MonitorMode mode = MonitorMode.Precise)
    {
        var list = new List<SessionRecord>();
        for (var d = 0; d < days; d++)
        {
            var s = start.AddDays(d);
            var open = lastOpen && d == days - 1;
            list.Add(new SessionRecord
            {
                Id = -(d + 1), Name = "DiskThinner-Agent-" + d, StartedUtc = s, PlannedEndUtc = s.AddDays(1), Mode = mode, Source = SessionSource.Agent,
                EndedUtc = open ? null : s.AddDays(1), EndReason = open ? null : "rotate", LastFlushUtc = open ? s.AddHours(1) : s.AddDays(1),
                Stats = new MonitorStats { Received = 1000, Lost = 0 },
            });
        }

        return list;
    }

    private static IndexRow Row(long session, string dir, StoredChain chain, long growth, long creates = 10) =>
        new(session, T0, dir, chain, new Counters { Growth = growth, Creates = creates, WriteBytes = Math.Max(0, growth) });

    // ---------------- 会话合并、数据合并 ----------------

    [Fact]
    public void AgentRotatedRowsMergeIntoOneSpan()
    {
        var sessions = AgentDays(T0, 3, lastOpen: true);
        var diag = new SessionRecord
        {
            Id = 5, Name = "DiskThinner-Diag-x", StartedUtc = T0.AddHours(5), PlannedEndUtc = T0.AddHours(9), EndedUtc = T0.AddHours(9), LastFlushUtc = T0.AddHours(9),
            Mode = MonitorMode.Light, EndReason = "timeout",
        };
        var spans = MonitorText.Spans([.. sessions, diag], null, diagnosticRunning: false, agentLive: true, nowUtc: T0.AddDays(2).AddHours(6));
        Assert.Equal(2, spans.Count);
        var agent = spans.Single(s => s.Source == SessionSource.Agent);
        Assert.Equal((T0, T0.AddDays(2).AddHours(6), true, 3), (agent.StartUtc, agent.EndUtc, agent.Live, agent.Rows));
        Assert.Equal(3000, agent.Received);

        // 中间断开过（服务停止）：分成两段
        sessions[0].EndReason = "service-stop";
        Assert.Equal(2, MonitorText.Spans(sessions, null, false, true, T0.AddDays(3)).Count);

        // 覆盖：服务在记录时最后一行算到现在
        Assert.Equal(54, MonitorText.CoveredHours(AgentDays(T0, 3, true), T0, T0.AddDays(5), nowUtc: T0.AddDays(2).AddHours(6), agentLive: true), 3);
    }

    [Fact]
    public void WriteIndexMergeKeepsBothSources()
    {
        var local = new WriteIndex([new SessionRecord { Id = 1, Name = "d", StartedUtc = T0, PlannedEndUtc = T0.AddHours(1), Mode = MonitorMode.Precise }],
            [Row(1, @"C:\a", Uv, 10)], [new HourCoverage(1, T0, MonitorMode.Precise, 1, 0, 0, 0, 0, 0)]);
        var agent = new WriteIndex(AgentDays(T0, 1, false), [Row(-1, @"C:\a", Uv, 20)], [new HourCoverage(-1, T0, MonitorMode.Precise, 1, 0, 0, 0, 0, 0)])
        {
            HiddenOtherUsers = new Counters { Creates = 7 }, HiddenRows = 2,
        };
        var merged = WriteIndex.Merge(local, agent);
        Assert.Equal(2, merged.Sessions.Count);
        Assert.True(merged.HasAgent);
        Assert.Equal(30, merged.Under(@"C:\a").Sum(r => r.Counters.Growth));
        Assert.Equal(1, merged.MonitoredHours); // 同一个小时只算一次
        Assert.Equal(7, merged.HiddenOtherUsers!.Value.Creates);
        Assert.Same(local, WriteIndex.Merge(local, null, WriteIndex.Empty));
        Assert.Throws<ArgumentException>(() => WriteIndex.Merge(local, local));
    }

    // ---------------- 服务状态的文字 ----------------

    private static ServiceInfo Running => new("running", 4321, "\"C:\\Program Files\\DiskThinner\\Agent\\x\\DiskThinner.Agent.exe\"", 0, 0);

    private static AgentSnapshot Recording(string? version = null, string mode = "precise") => new(Running, version ?? AppVersion.Current, true,
        new AgentStatus { State = "recording", Mode = mode, KeepDays = 90, RecordingSinceUtc = DateTime.UtcNow.AddDays(-6.5), DbBytes = (long)(12.4 * Mb), Received = 999, Lost = 1 }, null);

    [Fact]
    public void SnapshotProblemsAndVersions()
    {
        Assert.Null(AgentSnapshot.None.Problem);
        var rec = Recording();
        Assert.True(rec.Recording);
        Assert.Null(rec.Problem);
        Assert.False(rec.NeedsUpdate);
        Assert.Equal("已记录 6 天", rec.RecordedFor(DateTime.UtcNow));
        Assert.Equal(0.001, rec.LostRate, 6);
        Assert.True(Recording("0.0.1").NeedsUpdate);

        var stopped = AgentSnapshot.None with { Service = new ServiceInfo("stopped", 0, Running.BinaryPath, 0, 0) };
        Assert.Equal("已停止", stopped.Problem);
        Assert.False(stopped.Recording);
        var oldProtocol = new AgentSnapshot(Running, "0.1.0", false, null, null);
        Assert.StartsWith("需要更新（服务 0.1.0", oldProtocol.Problem, StringComparison.Ordinal);
        Assert.True(oldProtocol.NeedsUpdate);
        Assert.Equal("连不上服务", new AgentSnapshot(Running, null, false, null, "connect").Problem);
        var broken = rec with { Status = new AgentStatus { State = "error", LastError = "etw 1450" } };
        Assert.Equal("最近出错：etw 1450", broken.Problem);
    }

    // ---------------- 设置页 ----------------

    [Fact]
    public void SettingsAgentGroupStates() => UiThread.Run(() =>
    {
        var page = new SettingsPage();
        using var form = UiThread.Host(page, 1200, 900);
        page.SetSettings(new AppSettings());
        Assert.Equal("", page.AgentStatusText); // 没选、没装：不显示

        page.SetSettings(new AppSettings { Monitor = new MonitorSettings { Enabled = MonitorSettings.Agent } });
        page.SetAgent(AgentSnapshot.None, elevated: true, sourceAvailable: true);
        Assert.Equal("没有安装", page.AgentStatusText);
        Assert.Equal(["安装…"], page.AgentButtons.Select(b => b.Text));
        page.SetAgent(AgentSnapshot.None, elevated: false, sourceAvailable: true);
        Assert.Equal(["以管理员身份重启并安装"], page.AgentButtons.Select(b => b.Text));
        page.SetAgent(AgentSnapshot.None, elevated: true, sourceAvailable: false);
        Assert.False(page.AgentButtons.Single().Enabled);

        string? action = null;
        page.AgentActionRequested += (_, a) => action = a;
        (string? Mode, int? Keep)? configured = null;
        page.AgentConfigureRequested += (_, c) => configured = c;

        page.SetAgent(Recording(mode: "light"), elevated: true, sourceAvailable: true);
        Assert.StartsWith("运行中 · 轻量 · 版本 ", page.AgentStatusText, StringComparison.Ordinal);
        Assert.EndsWith("开始记录 · 数据 12.4 MB", page.AgentStatusText, StringComparison.Ordinal);
        Assert.Equal(["停止服务", "卸载…"], page.AgentButtons.Select(b => b.Text));
        Assert.Equal(0, page.MonitorMode.SelectedIndex); // 模式显示服务的
        page.AgentButtons.First().PerformClick();
        Assert.Equal("stop", action);
        page.MonitorMode.SelectedIndex = 1;
        Assert.Equal(("precise", (int?)null), configured);

        page.SetAgent(Recording("0.0.1"), elevated: true, sourceAvailable: true);
        Assert.Contains("可以更新到", page.AgentStatusText, StringComparison.Ordinal);
        Assert.Equal(["停止服务", "更新服务", "卸载…"], page.AgentButtons.Select(b => b.Text));

        var stopped = AgentSnapshot.None with { Service = new ServiceInfo("stopped", 0, Running.BinaryPath, 0, 0) };
        page.SetAgent(stopped, elevated: true, sourceAvailable: true);
        Assert.Equal("已停止", page.AgentStatusText);
        Assert.Contains("启动服务", page.AgentButtons.Select(b => b.Text));

        // 普通权限：只有"以管理员身份重启"，模式和保留不能改
        page.SetAgent(Recording(), elevated: false, sourceAvailable: true);
        Assert.Equal(["以管理员身份重启"], page.AgentButtons.Select(b => b.Text));
        Assert.False(page.MonitorMode.Enabled);

        // 选回临时诊断：已经装了时服务一组还在，模式可以改（作用于诊断）
        page.MonitorDiagnostic.Checked = true;
        Assert.True(page.MonitorMode.Enabled);
        Assert.NotEqual("", page.AgentStatusText);
    });

    [Fact]
    public void InstallDialogStates() => UiThread.Run(() =>
    {
        using (var admin = new AgentInstallDialog(precise: true, keepDays: 180, elevated: true, @"C:\Program Files\DiskThinner\Agent", @"C:\ProgramData\DiskThinner\Agent"))
        {
            Assert.Equal("安装并启动", admin.OkButton.Text);
            Assert.Equal((true, 180), (admin.Precise, admin.KeepDays));
            Assert.Contains(Texts(admin), t => t.Contains(@"C:\ProgramData\DiskThinner\Agent\", StringComparison.Ordinal));
        }

        using var user = new AgentInstallDialog(false, 90, elevated: false, @"C:\Program Files\DiskThinner\Agent", @"C:\ProgramData\DiskThinner\Agent");
        Assert.Equal("以管理员身份重启并安装", user.OkButton.Text);
        Assert.Equal((false, 90), (user.Precise, user.KeepDays));

        static IEnumerable<string> Texts(Control c) => c.Controls.Cast<Control>().SelectMany(x => Texts(x).Prepend(x.Text));
    });

    // ---------------- 增长分析、ETW 证据 ----------------

    [Fact]
    public void GrowthPageShowsAgentRowsAndHidesDiagnose() => UiThread.Run(() =>
    {
        var now = DateTime.UtcNow;
        var store = new SnapshotStore(Path.Combine(_dir, "snapshots.db"));
        static ScanStats Stats(long free) => new() { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = free };
        store.Save(TestTrees.Build(($@"{Local}\uv\cache\a.whl", 400 * Mb)), Stats(60L << 30), now.AddDays(-3));
        var tree = TestTrees.Build(($@"{Local}\uv\cache\a.whl", 1000 * Mb));
        var result = new ScanResult(tree, Stats(58L << 30));
        var model = GrowthModel.Build(store, result, null, autoSave: false, now);
        var start = now.Date.AddDays(-3);
        var index = new WriteIndex(AgentDays(start, 4, lastOpen: true), [Row(-4, $@"C:\{Local}\uv\cache", Uv, 600 * Mb, 120)], [])
        {
            HiddenOtherUsers = new Counters { Creates = 33, WriteBytes = 5 * Mb }, HiddenRows = 3,
        };

        var page = new GrowthPage();
        using var form = UiThread.Host(page, 1400, 1400);
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        page.SetMonitor((_, _) => index, enabled: true);
        page.SetLive(MonitorStatus.Idle, null, agentLive: true);
        page.SetModel(model, result, null, null, null);
        Wait(page);

        Assert.Contains(page.SessionTexts, t => t == "常驻 · 精确模式");
        Assert.Contains(page.SessionTexts, t => t.EndsWith("– 进行中", StringComparison.Ordinal));
        Assert.DoesNotContain(page.SessionTexts, t => t.StartsWith("另有", StringComparison.Ordinal)); // 4 行合成 1 段
        Assert.False(page.DiagnoseButton.Visible);
        Assert.StartsWith("常驻服务记录中", page.MonitorCard.FootText, StringComparison.Ordinal);
        Assert.Contains(page.Writers.Items, w => w.Cells[0] == "其他用户的目录（需要管理员）" && w.Cells[3] == "33");
    });

    [Fact]
    public void EvidenceUsesResidentWordingAndHidesOtherUsersPrograms()
    {
        var now = T0.AddDays(26).AddHours(1);
        var index = new WriteIndex(AgentDays(T0, 27, lastOpen: true),
            [Row(-3, @"C:\Users\demo\AppData\Local\uv\cache", Uv, 1200 * Mb, 8921)], [new HourCoverage(-3, T0, MonitorMode.Precise, 1, 0, 0, 0, 0, 0)]);
        var line = Assert.Single(MonitorEvidence.For(index, @"C:\Users\demo\AppData\Local\uv", isDirectory: true, nowUtc: now));
        Assert.StartsWith("近 30 天常驻监控（共 26 天）中，uv.exe 在这里新建了 8,921 个文件", line, StringComparison.Ordinal);
        Assert.Contains("进程链：uv.exe ← pwsh.exe ← Code.exe", line, StringComparison.Ordinal);

        var hidden = new StoredChain(ChainKind.Normal, new StoredImage(AgentProtocol.OtherUserImage, "", null, null), null, AgentProtocol.OtherUserImage, 0);
        Assert.Equal("其他用户的程序", MonitorText.Writer(hidden));
        Assert.Equal("其他用户的程序", MonitorText.FullChain(hidden));
    }

    // ---------------- 实测归属（E11） ----------------

    [Fact]
    public void ObservedOwnersNeedCoverageAndShare()
    {
        var tree = TestTrees.Build(($@"{Local}\Mystery\data\a.bin", 900 * Mb), ($@"{Local}\Mixed\b.bin", 500 * Mb), ($@"{Local}\Small\c.bin", 1 * Mb));
        var c = new RuleEngine(new RuleSet([], []), PathContext.Default).Classify(tree);
        var owners = OwnerMap.RulesOnly(c);
        var rows = new List<IndexRow>
        {
            Row(-1, $@"C:\{Local}\Mystery\data", Uv, 850 * Mb),
            Row(-2, $@"C:\{Local}\Mystery\data", Node, 50 * Mb),
            Row(-1, $@"C:\{Local}\Mixed", Uv, 300 * Mb),
            Row(-1, $@"C:\{Local}\Mixed", Node, 200 * Mb),
            Row(-1, $@"C:\{Local}\Small", Uv, 1 * Mb),
        };

        // 覆盖不到 7 天：不给
        var shortIndex = new WriteIndex(AgentDays(T0, 5, false), rows, []);
        Assert.Empty(ObservedOwners.Compute(shortIndex, tree, owners, T0.AddDays(6)));

        var index = new WriteIndex(AgentDays(T0, 10, false), rows, []);
        var observed = ObservedOwners.Compute(index, tree, owners, T0.AddDays(11));
        var (node, owner) = Assert.Single(observed);
        Assert.Equal(tree.FindPath($@"C:\{Local}\Mystery"), node); // 最上层的一个（Mystery 和 data 都达标，只取 Mystery）
        Assert.Equal("实测：uv.exe（Code.exe）", owner.Display);
        Assert.Equal(OwnerSource.Observed, owner.Source);
        Assert.Contains("94%", owner.Evidence[0].Text, StringComparison.Ordinal);

        owners.SetObserved(observed);
        Assert.Equal(owner, owners.OwnerOf(tree.FindPath($@"C:\{Local}\Mystery\data\a.bin")));
        Assert.Null(owners.OwnerOf(tree.FindPath($@"C:\{Local}\Mixed\b.bin"))); // 60%，不到 80%
        Assert.Equal(("实测", RuleView.ObservedSource), RuleView.SourceTag(OwnerSource.Observed));

        // 诊断的数据不算
        var diagOnly = new WriteIndex([new SessionRecord { Id = 1, Name = "d", StartedUtc = T0, PlannedEndUtc = T0.AddDays(10), EndedUtc = T0.AddDays(10), Mode = MonitorMode.Precise }],
            [Row(1, $@"C:\{Local}\Mystery\data", Uv, 850 * Mb)], []);
        Assert.Empty(ObservedOwners.Compute(diagOnly, tree, OwnerMap.RulesOnly(c), T0.AddDays(11)));
    }

    // ---------------- 命令行、MonitorHost ----------------

    [Fact]
    public void AgentCommandLineOptions()
    {
        var a = AppOptions.Parse(["--install-agent", "light:30", "--agent-pipe", "DiskThinner.AgentTest.1", "--agent-service", "DiskThinnerAgentTest", "--show-agent-dialog"]);
        Assert.Equal((false, 30), a.InstallAgent);
        Assert.Equal("DiskThinner.AgentTest.1", a.AgentPipe);
        Assert.Equal("DiskThinnerAgentTest", a.AgentService);
        Assert.True(a.ShowAgentDialog);
        var b = AppOptions.Parse(["--install-agent", "fast:7", "--agent-pipe", @"..\x"]);
        Assert.Null(b.InstallAgent);
        Assert.Null(b.AgentPipe);
        Assert.Single(b.Ignored);

        var path = Path.Combine(_dir, "s.json");
        new AppSettings { Monitor = new MonitorSettings { Enabled = MonitorSettings.Agent } }.Save(path);
        Assert.True(AppSettings.Load(path, out _).Monitor.IsAgent);
    }

    [Fact]
    public void HostStatesAndMergedLoad() => UiThread.Run(() =>
    {
        // WinForms 的计时器要在同一个有消息循环的线程上建和销毁：在界面测试线程上跑，等后台查询时处理消息
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var db = Path.Combine(_dir, "mon.db");
        var store = MonitorStore.Open(db);
        var s = new SessionRecord { Name = "DiskThinner-Diag-20261020010000", StartedUtc = T0.AddHours(1), PlannedEndUtc = T0.AddHours(2), Mode = MonitorMode.Precise };
        store.BeginSession(s);
        s.EndedUtc = T0.AddHours(2);
        s.EndReason = "timeout";
        store.Flush(s, [new HourData(T0.AddHours(1), MonitorMode.Precise, new MonitorStats { Received = 1 }, 0,
            [new StoredRow(@"C:\a", new ProcessChain(ChainKind.Normal, @"C:\x\uv.exe", "", @"C:\x\uv.exe", "", "uv.exe"), new Counters { Creates = 1 })])]);

        var fake = new FakeAgent { Snapshot = Recording(), Index = new WriteIndex(AgentDays(T0, 2, true), [Row(-2, @"C:\a", Uv, 10, 5)], []) };
        using var owner = new Control();
        using var host = new MonitorHost(owner, db, Path.Combine(_dir, "snap.db"), fake);
        Assert.Equal(MonitorUiState.Idle, host.State);
        Pump(host.RefreshAgentAsync());
        Assert.Equal(MonitorUiState.Agent, host.State);
        var merged = host.Load(T0, T0.AddDays(3));
        Assert.Equal(3, merged.Sessions.Count);
        Assert.Equal(6, merged.Under(@"C:\a").Sum(r => r.Counters.Creates));

        fake.Snapshot = AgentSnapshot.None with { Service = new ServiceInfo("stopped", 0, null, 0, 0) };
        Pump(host.RefreshAgentAsync());
        Assert.Equal(MonitorUiState.AgentDown, host.State);
        Assert.Single(host.Load(T0, T0.AddDays(3)).Sessions); // 服务没运行：只有诊断库的
        Assert.Equal(1, fake.Queries);
    });

    private static void Pump(Task task)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        Assert.True(task.IsCompleted, "10 秒内没有完成");
        Application.DoEvents();
    }

    private sealed class FakeAgent : IAgentSource
    {
        public AgentSnapshot Snapshot { get; set; } = AgentSnapshot.None;
        public WriteIndex? Index { get; set; }
        public int Queries { get; private set; }
        public string ServiceName => "DiskThinnerAgentTest";

        public AgentSnapshot Probe() => Snapshot;

        public WriteIndex? Query(DateTime fromUtc, DateTime toUtc)
        {
            Queries++;
            return Index;
        }

        public AgentResponse? Control(string action, string? mode = null, int? keepDays = null) => new() { Type = "ok" };

        public string? SetServiceRunning(bool run) => null;
    }

    private static void Wait(GrowthPage page)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (page.Pending is { IsCompleted: false } && sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }

        Application.DoEvents();
        Assert.True(page.Pending?.IsCompleted ?? true, "增长对比 10 秒内没有算完");
        if (page.Pending?.Exception is { } ex)
        {
            throw ex.InnerException ?? ex;
        }
    }
}

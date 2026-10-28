using DiskThinner.App.Dialogs;
using DiskThinner.App.Growth;
using DiskThinner.App.Monitor;
using DiskThinner.App.Pages;
using DiskThinner.App.Rules;
using DiskThinner.App.Settings;
using DiskThinner.Monitor;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;
using Microsoft.Data.Sqlite;
using static DiskThinner.App.Pages.OverviewPage;

namespace DiskThinner.App.Tests;

/// <summary>
/// 写入监控在界面上的表现（docs/phase2a-design.md 5.9 节，第 1.3 节第 9–12 条）：进程链和时长的文字、"写入进程"一格的几种情况、
/// 增长分析页（用合成的 monitor.db）、空间浏览的 ETW 证据、对话框、设置和命令行参数。
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class MonitorUiTests : IDisposable
{
    private const long Mb = 1L << 20;
    private const string Local = @"Users\demo\AppData\Local";
    private static readonly DateTime T0 = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-monitor-ui-" + Guid.NewGuid().ToString("N"));

    private static readonly ProcessChain Uv = new(ChainKind.Normal, @"C:\Users\demo\.local\bin\uv.exe", "", @"C:\Program Files\Code\Code.exe", "", "uv.exe ← pwsh.exe ← Code.exe");
    private static readonly ProcessChain Node = new(ChainKind.Normal, @"C:\nodejs\node.exe", "", @"C:\Users\demo\.local\bin\claude.exe", "", "node.exe ← claude.exe");
    private static readonly ProcessChain Svc = new(ChainKind.Normal, @"C:\Windows\System32\svchost.exe", "wuauserv", @"C:\Windows\System32\svchost.exe", "wuauserv", "svchost.exe[wuauserv]");

    public MonitorUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static Counters C(long growth = 0, long bytes = 0, long creates = 0, long deletes = 0, bool sampled = false) =>
        new() { Growth = growth, WriteBytes = bytes, WriteOps = bytes > 0 ? 1 : 0, Creates = creates, Deletes = deletes, Sampled = sampled };

    /// <summary>一个 [start, start + hours) 的会话，第一个小时里有这些行。</summary>
    private string Db(DateTime start, double hours, MonitorMode mode, long unresolvedBytes, params StoredRow[] rows)
    {
        var path = Path.Combine(_dir, $"mon-{Guid.NewGuid():N}.db");
        var store = MonitorStore.Open(path);
        var s = new SessionRecord { Name = SessionDeadline.NewSessionName(start), StartedUtc = start, PlannedEndUtc = start.AddHours(hours), Mode = mode };
        store.BeginSession(s);
        s.EndedUtc = start.AddHours(hours);
        s.EndReason = "timeout";
        var hour = new DateTime(start.Ticks - start.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        var stats = new MonitorStats { Received = 1000, UnresolvedBytes = unresolvedBytes };
        store.Flush(s, [new HourData(hour, mode, stats, 0, rows)], p => p.EndsWith("uv.exe", StringComparison.OrdinalIgnoreCase) ? ("uv", "Astral") : (null, null));
        return path;
    }

    private static WriteIndex Load(string db, DateTime from, DateTime to) => MonitorStore.OpenExisting(db)!.LoadIndex(from, to);

    // ---------------- 文字 ----------------

    [Fact]
    public void ChainsAndDurationsAreFormatted()
    {
        Assert.Equal("svchost.exe（服务 wuauserv、bits）", MonitorText.Link("svchost.exe[wuauserv,bits]"));
        Assert.Equal("服务 a、b 等 4 个", MonitorText.Services("a,b,c,d"));
        Assert.Equal(MonitorText.SystemWriter, MonitorText.Link("System"));
        Assert.Equal("3 小时 12 分", MonitorText.Remaining(TimeSpan.FromMinutes(192)));
        Assert.Equal("12 分钟", MonitorText.Remaining(TimeSpan.FromMinutes(12.5)));
        Assert.Equal("3:05", MonitorText.RemainingShort(TimeSpan.FromMinutes(185)));
        Assert.Equal("2.5", MonitorText.Hours(2.59));
        Assert.Equal("14", MonitorText.Hours(14));
        Assert.Equal("丢失 0.2%", MonitorText.Lost(0.002));
        Assert.Equal("35 分钟", MonitorText.Duration(TimeSpan.FromMinutes(35)));
        Assert.Equal("4 小时", MonitorText.Duration(TimeSpan.FromHours(4)));
        Assert.Equal("14 / 168 小时", MonitorText.Coverage(14.04, 168.0001));
        Assert.Equal("1 / 2 分钟", MonitorText.Coverage(1.5 / 60, 2.0 / 60));
    }

    [Fact]
    public void CoveredHoursMergeOverlapsAndClipToTheRange()
    {
        SessionRecord S(DateTime start, double hours, DateTime? ended = null) => new()
        {
            Name = SessionDeadline.NewSessionName(start), StartedUtc = start, PlannedEndUtc = start.AddHours(hours), Mode = MonitorMode.Light,
            EndedUtc = ended ?? start.AddHours(hours), LastFlushUtc = start,
        };

        // 两个重叠的会话 [8, 12) 和 [10, 14)，范围 [9, 13)：覆盖 4 小时
        var sessions = new[] { S(T0, 4), S(T0.AddHours(2), 4) };
        Assert.Equal(4, MonitorText.CoveredHours(sessions, T0.AddHours(1), T0.AddHours(5)), 3);

        // 没有结束时间的会话：正在监控的算到现在，其他的算到最后一次落盘
        var open = new SessionRecord { Name = "DiskThinner-Diag-20261027080000", StartedUtc = T0, PlannedEndUtc = T0.AddHours(8), Mode = MonitorMode.Light, LastFlushUtc = T0.AddHours(1) };
        Assert.Equal(1, MonitorText.CoveredHours([open], T0, T0.AddHours(10)), 3);
        Assert.Equal(3, MonitorText.CoveredHours([open], T0, T0.AddHours(10), open.Name, T0.AddHours(3)), 3);
    }

    [Fact]
    public void MainChainFollowsTheDesignRules()
    {
        var a = new ChainWrites(StoredChain.Other with { Kind = ChainKind.Normal, Links = "a.exe" }, C(growth: 700, creates: 1));
        var b = new ChainWrites(StoredChain.Other with { Kind = ChainKind.Normal, Links = "b.exe" }, C(growth: 300, creates: 50, deletes: 40));

        // 净增长、精确：按估算增长，第一名 70%
        var main = MonitorText.MainChain([a, b], netDecrease: false, precise: true)!.Value;
        Assert.Same(a, main.Main);
        Assert.Equal(0.7, main.Share, 3);

        // 轻量：按新建 + 删除 + 重命名；净减少：按删除数
        Assert.Same(b, MonitorText.MainChain([a, b], netDecrease: false, precise: false)!.Value.Main);
        Assert.Same(b, MonitorText.MainChain([a, b], netDecrease: true, precise: true)!.Value.Main);
        Assert.Null(MonitorText.MainChain([], false, true));
    }

    // ---------------- "写入进程"一格 ----------------

    [Fact]
    public void WriterCellCoversEachCase()
    {
        var uvCache = @"C:\Users\demo\AppData\Local\uv\cache";
        var db = Db(T0, 2, MonitorMode.Precise, 0,
            new StoredRow(uvCache, Uv, C(growth: 1200 * Mb, bytes: 1300 * Mb, creates: 8921)),
            new StoredRow(@"C:\Users\demo\AppData\Local\Temp", Node, C(growth: -50 * Mb, creates: 20, deletes: 30)),
            new StoredRow(@"C:\Users\demo\AppData\Local\Temp", Uv, C(growth: -40 * Mb, deletes: 25)),
            new StoredRow(@"C:\Users\demo\AppData\Local\Temp", Svc, C(growth: -30 * Mb, deletes: 25)),
            new StoredRow(@"C:\Users\demo\Docker", Node, C(growth: 10 * Mb, bytes: 20 * Mb, sampled: true)));
        var index = Load(db, T0, T0.AddDays(7));

        // 没有监控：调用方显示"未监控"
        Assert.Null(MonitorText.Cell(null, 0, 168, uvCache, 1, false, false));
        Assert.Null(MonitorText.Cell(index, 0, 168, uvCache, 1, false, false));

        // 有写入：完整进程链 + 实测；概览用短链
        var cell = MonitorText.Cell(index, 2, 168, uvCache, 1200 * Mb, false, false)!;
        Assert.Equal(("uv.exe ← pwsh.exe ← Code.exe", "实测", true), (cell.Text, cell.Tag, cell.Observed));
        Assert.Contains("新建 8,921 个文件", cell.Note);
        Assert.Equal("uv.exe ← Code.exe", MonitorText.Cell(index, 2, 168, uvCache, 1200 * Mb, false, false, shortChain: true)!.Text);

        // 净减少：按删除数选；三个进程都不到 60% 时写"X 等 N 个进程"
        Assert.Equal("node.exe 等 3 个进程", MonitorText.Cell(index, 2, 168, @"C:\Users\demo\AppData\Local\Temp", -120 * Mb, false, false)!.Text);

        // 抽样估算加"≈"；虚拟磁盘和文件的后缀
        var docker = MonitorText.Cell(index, 2, 168, @"C:\Users\demo\Docker\disk.vhdx", 10 * Mb, isFile: true, vmDisk: true)!;
        Assert.Equal("≈ node.exe ← claude.exe（虚拟磁盘，看不到内部是谁写的）（所在目录）", docker.Text);

        // 子目录有写入但这里没有单独记录：写入记录在上级目录
        var child = MonitorText.Cell(index, 2, 168, uvCache + @"\new-wheel", 1, false, false)!;
        Assert.False(child.Observed);
        Assert.StartsWith("写入记录在上级目录", child.Text);

        // 监控了但没有写入
        Assert.Equal("监控的 2 小时里没有写入", MonitorText.Cell(index, 2, 168, @"C:\Windows\Logs", 1, false, false)!.Text);
    }

    [Fact]
    public void WriterCellDoesNotClaimNoWritesWhenWritesWereUnresolvedOrLight()
    {
        var unresolved = Load(Db(T0, 1, MonitorMode.Precise, 500 * Mb, new StoredRow(@"C:\a", Uv, C(bytes: 10 * Mb))), T0, T0.AddDays(1));
        Assert.Equal("没有能对上这里的写入", MonitorText.Cell(unresolved, 1, 24, @"C:\b", 1, false, false)!.Text);

        var light = Load(Db(T0, 1, MonitorMode.Light, 0, new StoredRow(@"C:\a", Uv, C(creates: 3))), T0, T0.AddDays(1));
        Assert.Equal("监控的 1 小时里没有新建或删除", MonitorText.Cell(light, 1, 24, @"C:\b", 1, false, false)!.Text);
    }

    [Fact]
    public void EtwEvidenceForTheExplorerDetails()
    {
        var index = Load(Db(T0, 2, MonitorMode.Precise, 0, new StoredRow(@"C:\Users\demo\AppData\Local\uv\cache", Uv, C(growth: 1200 * Mb, creates: 8921))), T0, T0.AddDays(1));
        var lines = MonitorEvidence.For(index, @"C:\Users\demo\AppData\Local\uv", isDirectory: true, nowUtc: T0.AddHours(3));
        var first = Assert.Single(lines);
        Assert.Contains("uv.exe 在这里新建了 8,921 个文件", first);
        Assert.Contains("进程链：uv.exe ← pwsh.exe ← Code.exe", first);
        Assert.Contains("精确", first);

        Assert.Equal(["近 30 天 1 次诊断（共 2 小时）里没有程序在这里写入"], MonitorEvidence.For(index, @"C:\Windows", true, T0.AddHours(3)));
        Assert.Empty(MonitorEvidence.For(WriteIndex.Empty, @"C:\Windows", true));
        Assert.Empty(MonitorEvidence.For(null, @"C:\Windows", true));
    }

    // ---------------- 列表的标签列 ----------------

    [Fact]
    public void ItemListTagColumnKeepsTextAndTagTogether() => UiThread.Run(() =>
    {
        var list = new ItemList("测试", ["目录", "变化", "写入者"], [86, 190]);
        using var form = UiThread.Host(list, 700, 300);
        list.SetItems(
        [
            new ListItem(1, ["a", "+1", "uv.exe ← Code.exe"], null, RuleView.ObservedSource, "实测", TagColumn: 2),
            new ListItem(2, ["b", "+1", "Docker Desktop"], null, RuleView.NotMonitored, "", TagColumn: 2),
            new ListItem(3, ["c", "+1", ""], null, RuleView.Unknown, "未识别"),
        ]);
        Application.DoEvents();
        var view = list.Controls.OfType<ListView>().Single();
        Assert.Equal("uv.exe ← Code.exe 实测", view.Items[0].SubItems[2].Text);
        Assert.Equal("Docker Desktop", view.Items[1].SubItems[2].Text);
        Assert.Equal("未识别", view.Items[2].SubItems[2].Text);

        list.SetHint("近 7 天没有开过写入监控。", "开启 4 小时诊断");
        Assert.Equal("近 7 天没有开过写入监控。 开启 4 小时诊断", list.HintText);
        list.SetHint(null);
        Assert.Null(list.HintText);
    });

    // ---------------- 增长分析页 ----------------

    [Fact]
    public void GrowthPageShowsWritersCoverageAndRanking() => UiThread.Run(() =>
    {
        var now = DateTime.UtcNow;
        var store = new SnapshotStore(Path.Combine(_dir, "snapshots.db"));
        static ScanStats Stats(long free) => new() { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = free };
        var before = TestTrees.Build(($@"{Local}\uv\cache\a.whl", 400 * Mb), (@"Windows\w.bin", 2000 * Mb));
        store.Save(before, Stats(60L << 30), now.AddDays(-8));
        var tree = TestTrees.Build(($@"{Local}\uv\cache\a.whl", 1000 * Mb), ($@"{Local}\NewTool\data.bin", 300 * Mb), (@"Windows\w.bin", 2000 * Mb));
        var result = new ScanResult(tree, Stats(58L << 30));

        // 热点停在规则命中的 uv\cache（和 GrowthUiTests 一样）
        var rules = new RuleSet([new AppInfo("uv", "uv", "Astral", [], [])],
        [
            new LocationRule
            {
                Id = "dev.uv.cache", AppId = "uv", Mode = MatchMode.Dir, Pattern = @"%LOCALAPPDATA%\uv\cache", Kind = Kind.PackageCache, Risk = Risk.Low,
                Tier = Tier.Reviewed, Purpose = "uv 缓存", Impact = "需重新下载", Evidence = [new Evidence(null, "测试")],
            },
        ]);
        var c = new RuleEngine(rules, PathContext.Default).Classify(tree);
        var model = GrowthModel.Build(store, result, c, autoSave: false, now);
        var db = Db(now.AddHours(-3), 1, MonitorMode.Precise, 0, new StoredRow(@"C:\" + Local + @"\uv\cache", Uv, C(growth: 600 * Mb, bytes: 700 * Mb, creates: 120)));

        var page = new GrowthPage();
        using var form = UiThread.Host(page, 1400, 1400);
        UseUiContext();
        page.SetMonitor((f, t) => Load(db, f, t), enabled: true);
        page.SetModel(model, result, c, OwnerMap.RulesOnly(c), null);
        Wait(page);

        Assert.True(page.MessageText is null, page.MessageText);
        var rows = page.Changes.Items;
        Assert.Equal(["+600.0 MB", "+300.0 MB"], rows.Select(r => r.Cells[1]));
        Assert.Equal(("uv.exe ← pwsh.exe ← Code.exe", "实测"), (rows[0].Cells[4], rows[0].Tag));
        Assert.Equal(("监控的 1 小时里没有写入", ""), (rows[1].Cells[4], rows[1].Tag));
        Assert.StartsWith("1 / 192 小时", page.MonitorCard.ValueText, StringComparison.Ordinal);
        Assert.Single(page.Timeline.Spans);
        Assert.Contains(page.SessionTexts, t => t == "诊断 · 精确模式");
        Assert.Equal("开启诊断", page.DiagnoseButton.Text);

        var writer = Assert.Single(page.Writers.Items);
        Assert.Equal("uv.exe", writer.Cells[0]);
        Assert.Equal("uv · Astral", writer.Cells[1]);
        Assert.Equal("Code.exe", writer.Cells[2]);
        Assert.Equal("120", writer.Cells[3]);
        Assert.Equal("700.0 MB", writer.Cells[4]);
        Assert.Equal(tree.FindPath($@"C:\{Local}\uv\cache"), writer.Node);

        // 设置为"关闭"时不显示"开启诊断"
        page.SetMonitor((f, t) => Load(db, f, t), enabled: false);
        Assert.False(page.DiagnoseButton.Visible);
    });

    [Fact]
    public void GrowthPageMarksUnmonitoredRanges() => UiThread.Run(() =>
    {
        var now = DateTime.UtcNow;
        var store = new SnapshotStore(Path.Combine(_dir, "snapshots2.db"));
        var stats = new ScanStats { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = 60L << 30 };
        store.Save(TestTrees.Build((@"a\x.bin", 100 * Mb)), stats, now.AddDays(-8));
        var result = new ScanResult(TestTrees.Build((@"a\x.bin", 900 * Mb)), stats);
        var page = new GrowthPage();
        using var form = UiThread.Host(page, 1400, 1400);
        UseUiContext();
        page.SetMonitor((_, _) => WriteIndex.Empty, enabled: true); // 库不存在：空索引
        page.SetModel(GrowthModel.Build(store, result, null, autoSave: false, now), result, null, null, null);
        Wait(page);
        Assert.Equal("未监控", page.Changes.Items[0].Tag);
        Assert.StartsWith("0 / ", page.MonitorCard.ValueText, StringComparison.Ordinal);
        Assert.Empty(page.Writers.Items);
    });

    // ---------------- 对话框、设置、命令行 ----------------

    [Fact]
    public void MonitorDialogStates() => UiThread.Run(() =>
    {
        using (var admin = new MonitorDialog(8, precise: true, elevated: true, otherInstance: false, processUser: null))
        {
            Assert.Equal("开始监控", admin.StartButton.Text);
            Assert.True(admin.StartButton.Enabled);
            Assert.Equal((8, true), (admin.Hours, admin.Precise));
        }

        using (var user = new MonitorDialog(4, precise: false, elevated: false, otherInstance: false, processUser: null))
        {
            Assert.Equal("以管理员身份重启并开始", user.StartButton.Text);
            Assert.Equal((4, false), (user.Hours, user.Precise));
        }

        using (var busy = new MonitorDialog(4, true, elevated: true, otherInstance: true, processUser: "admin2"))
        {
            Assert.False(busy.StartButton.Enabled);
            var texts = Texts(busy).ToList();
            Assert.Contains(texts, t => t.Contains("另一个 DiskThinner 窗口正在监控", StringComparison.Ordinal));
            Assert.Contains(texts, t => t.Contains("你用 admin2 账户提权", StringComparison.Ordinal));
        }

        static IEnumerable<string> Texts(Control c) => c.Controls.Cast<Control>().SelectMany(x => Texts(x).Prepend(x.Text));
    });

    [Fact]
    public void MonitorSettingsDefaultAndNormalize()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "snapshot": { "keepDays": 180 } }""");
        var old = AppSettings.Load(path, out var error);
        Assert.Null(error);
        Assert.Equal(new MonitorSettings(), old.Monitor); // 旧文件没有这一组：默认临时诊断、精确、4 小时、90 天
        Assert.True(old.Monitor.IsPrecise);
        Assert.False(old.Monitor.IsOff);

        File.WriteAllText(path, """{ "monitor": { "enabled": "service", "mode": "light", "defaultHours": 30, "keepDays": 1000 } }""");
        var odd = AppSettings.Load(path, out _);
        Assert.Equal(MonitorSettings.Diagnostic, odd.Monitor.Enabled);
        Assert.Equal(MonitorSettings.Light, odd.Monitor.Mode);
        Assert.Equal(24, odd.Monitor.DefaultHours);
        Assert.Equal(180, odd.Monitor.KeepDays);

        var s = new AppSettings { Monitor = new MonitorSettings { Enabled = MonitorSettings.Off, Mode = MonitorSettings.Light, DefaultHours = 8, KeepDays = 30 } };
        s.Save(path);
        Assert.Equal(s.Monitor, AppSettings.Load(path, out _).Monitor);
        Assert.DoesNotContain("isOff", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPageMonitorChoicesRaiseChanges() => UiThread.Run(() =>
    {
        var page = new SettingsPage();
        using var form = UiThread.Host(page, 1200, 800);
        page.SetSettings(new AppSettings());
        page.SetMonitorInfo(@"C:\x\monitor.db", 3 * Mb);
        Assert.Equal("当前占用 3.00 MB", page.MonitorDbSizeText);
        Assert.True(page.MonitorDiagnostic.Checked);
        AppSettings? changed = null;
        page.SettingsChanged += (_, s) => changed = s;

        page.MonitorOff.Checked = true;
        Assert.Equal(MonitorSettings.Off, changed?.Monitor.Enabled);
        Assert.False(page.MonitorDiagnostic.Checked); // 同一个容器里，单选互斥

        page.MonitorMode.SelectedIndex = 0;
        Assert.Equal(MonitorSettings.Light, changed?.Monitor.Mode);
    });

    [Fact]
    public void MonitorCommandLineOptions()
    {
        var a = AppOptions.Parse(["--start-monitor", "4", "--monitor-mode", "light", "--monitor-db", @"D:\m.db", "--show-monitor-dialog"]);
        Assert.Equal(TimeSpan.FromHours(4), a.StartMonitor);
        Assert.False(a.MonitorPrecise);
        Assert.False(a.MonitorForAutomation);
        Assert.Equal(@"D:\m.db", a.MonitorDb);
        Assert.True(a.ShowMonitorDialog);
        Assert.Empty(a.Ignored);

        // --monitor-seconds 优先；超出范围的值忽略并记下
        var b = AppOptions.Parse(["--monitor-seconds", "20", "--start-monitor", "4"]);
        Assert.Equal(TimeSpan.FromSeconds(20), b.StartMonitor);
        Assert.True(b.MonitorForAutomation);
        var c = AppOptions.Parse(["--start-monitor", "25", "--monitor-seconds", "5", "--monitor-mode", "fast"]);
        Assert.Null(c.StartMonitor);
        Assert.Null(c.MonitorPrecise);
        Assert.Equal(3, c.Ignored.Count);
    }

    /// <summary>
    /// 和主程序（Application.Run）一样，让后台查询完成后回到界面线程：测试里没有消息循环，Application.DoEvents 结束时会卸掉 WinForms 的
    /// 同步上下文，后台任务的后续就跑到线程池上了（会话表要新建控件，不能在别的线程上做）。
    /// </summary>
    private static void UseUiContext() => SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

    private static void Wait(GrowthPage page)
    {
        UiThread.Step("等待增长对比的结果（GrowthPage.Pending）");
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

using System.Runtime.ExceptionServices;
using DiskThinner.App.Growth;
using DiskThinner.App.Pages;
using DiskThinner.App.Shell;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;
using Microsoft.Data.Sqlite;

namespace DiskThinner.App.Tests;

/// <summary>快照在界面上的表现：概览的"近 7 天净增长"和"增长最快"、增长分析页的对比、计划任务的参数。</summary>
public sealed class GrowthUiTests : IDisposable
{
    private const long Mb = 1L << 20;
    private const string Local = @"Users\demo\AppData\Local";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-growth-ui-" + Guid.NewGuid().ToString("N"));

    public GrowthUiTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static void RunOnSta(Action body)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                body();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    private static Form Host(Control page)
    {
        var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(1300, 850), ShowInTaskbar = false };
        page.Dock = DockStyle.Fill;
        form.Controls.Add(page);
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static ScanStats Stats(long free) => new() { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = free };

    /// <summary>8 天前和 1 天前各一个快照；当前扫描里 uv 缓存涨了 600 MB，新出现一个 300 MB 的目录。</summary>
    private (GrowthModel Model, ScanResult Result, Classification C, OwnerMap Owners) Sample(DateTime now)
    {
        var store = new SnapshotStore(Path.Combine(_dir, "snapshots.db"));
        var before = TestTrees.Build(($@"{Local}\uv\cache\a.whl", 400 * Mb), (@"Windows\w.bin", 2000 * Mb));
        store.Save(before, Stats(60L << 30), now.AddDays(-8));
        store.Save(before, Stats(59L << 30), now.AddDays(-1));

        var tree = TestTrees.Build(($@"{Local}\uv\cache\a.whl", 1000 * Mb), ($@"{Local}\NewTool\data.bin", 300 * Mb), (@"Windows\w.bin", 2000 * Mb));
        var result = new ScanResult(tree, Stats(58L << 30));
        var rules = new RuleSet([new AppInfo("uv", "uv", "Astral", [], [])],
        [
            new LocationRule
            {
                Id = "dev.uv.cache", AppId = "uv", Mode = MatchMode.Dir, Pattern = @"%LOCALAPPDATA%\uv\cache", Kind = Kind.PackageCache, Risk = Risk.Low,
                Tier = Tier.Reviewed, Purpose = "uv 缓存", Impact = "需重新下载", Evidence = [new Evidence(null, "测试")],
            },
        ]);
        var c = new RuleEngine(rules, PathContext.Default).Classify(tree);
        var owners = OwnerMap.RulesOnly(c);
        return (GrowthModel.Build(store, result, c, autoSave: false, now), result, c, owners);
    }

    [Fact]
    public void OverviewShowsWeeklyGrowthAndFastestDirectories() => RunOnSta(() =>
    {
        var now = DateTime.UtcNow;
        var (model, result, c, owners) = Sample(now);
        Assert.NotNull(model.WeekBaseline);
        Assert.False(model.CurrentSaved);

        var page = new OverviewPage();
        using var form = Host(page);
        page.SetResult(result, c, owners);
        page.SetGrowth(model, result.Tree, c, owners);

        // 概览的净增长按卷的已用空间算（60 GB 可用降到 58 GB），和"约 N 天后写满"同一个口径
        Assert.Equal("+2.00 GB", page.GrowthCard.ValueText);
        Assert.Equal(3, page.Trend.PointCount); // 两个快照 + 当前扫描
        var rows = page.Fastest.Items;
        Assert.Equal(2, rows.Count);
        Assert.Contains("uv", rows[0].Cells[0], StringComparison.Ordinal);
        Assert.Equal("+600.0 MB", rows[0].Cells[1]);
        Assert.Equal("uv（Astral）", rows[0].Cells[2]); // 热点停在规则位置 uv\cache，归属来自规则
        Assert.Equal(result.Tree.FindPath($@"C:\{Local}\uv\cache"), rows[0].Node);
    });

    [Fact]
    public void GrowthPageComparesSnapshotsWithTheCurrentScan() => RunOnSta(() =>
    {
        var now = DateTime.UtcNow;
        var (model, result, c, owners) = Sample(now);
        var page = new GrowthPage();
        using var form = Host(page);
        page.SetModel(model, result, c, owners, null);
        Wait(page);

        Assert.Equal("当前扫描", page.ToBox.Text);
        Assert.Equal(2, page.FromBox.Items.Count);
        Assert.Equal("+900.0 MB", page.NetCard.ValueText);
        var rows = page.Changes.Items;
        Assert.Equal(["+600.0 MB", "+300.0 MB"], rows.Select(r => r.Cells[1]));
        Assert.Equal("未监控", rows[0].Tag);
        Assert.Equal(2, page.Daily.Bars.Count);

        // 换成两个快照之间对比：没有变化
        page.ToBox.SelectedIndex = 1;
        Wait(page);
        Assert.Empty(page.Changes.Items);
        Assert.Equal("这段时间里没有超过 100 MB 的变化", page.Changes.PlaceholderText);
    });

    [Fact]
    public void GrowthPageExplainsWhenThereIsNothingToCompare() => RunOnSta(() =>
    {
        var store = new SnapshotStore(Path.Combine(_dir, "empty.db"));
        var tree = TestTrees.Build((@"a.bin", 10 * Mb));
        var result = new ScanResult(tree, Stats(10L << 30));
        var model = GrowthModel.Build(store, result, null, autoSave: false, DateTime.UtcNow);
        var page = new GrowthPage();
        using var form = Host(page);
        page.SetModel(model, result, null, null, null);
        Assert.Null(page.Pending);
        Assert.Empty(page.Changes.Items);
    });

    [Fact]
    public void AutoSnapshotOnlyForWholeVolumesAndAtMostEverySixHours()
    {
        var store = new SnapshotStore(Path.Combine(_dir, "auto.db"));
        var now = DateTime.UtcNow;
        var volume = new ScanResult(TestTrees.Build((@"a.bin", 200 * Mb)), Stats(10L << 30));
        Assert.True(GrowthModel.Build(store, volume, null, autoSave: true, now).CurrentSaved);
        Assert.False(GrowthModel.Build(store, volume, null, autoSave: true, now.AddHours(2)).CurrentSaved);
        Assert.True(GrowthModel.Build(store, volume, null, autoSave: true, now.AddHours(7)).CurrentSaved);
        Assert.Equal(2, store.List().Count);

        // 文件夹扫描（没有卷信息）不自动保存，但可以手动保存
        var folder = new ScanResult(TestTrees.Build((@"a.bin", 200 * Mb)), new ScanStats { Mode = ScanMode.Walk, Root = @"C:\" });
        Assert.False(GrowthModel.Build(store, folder, null, autoSave: true, now.AddHours(20)).CurrentSaved);
        Assert.True(GrowthModel.SaveCurrent(store, folder, null, now.AddHours(21)).CurrentSaved);
    }

    [Fact]
    public void ScheduledTaskArgumentsAreFixedAndQuoted()
    {
        var args = ScheduledSnapshot.CreateArguments(@"C:\Program Files\DiskThinner\DiskThinner.exe", "12:00", highest: true, drive: @"C:\");
        Assert.Equal(["/Create", "/TN", ScheduledSnapshot.TaskName, "/TR", "\"C:\\Program Files\\DiskThinner\\DiskThinner.exe\" snapshot --drive C:",
            "/SC", "DAILY", "/ST", "12:00", "/F", "/RL", "HIGHEST"], args);
        Assert.DoesNotContain("/RL", ScheduledSnapshot.CreateArguments(@"C:\x.exe", "08:30", highest: false, drive: null));
        Assert.Throws<ArgumentException>(() => ScheduledSnapshot.CreateArguments(@"C:\x.exe", "25:00", false, null));
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
        Assert.True(page.Pending?.IsCompleted ?? true);
    }
}

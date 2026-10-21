using System.Runtime.ExceptionServices;
using DiskThinner.App.Pages;
using DiskThinner.App.Settings;
using DiskThinner.Rules;

namespace DiskThinner.App.Tests;

/// <summary>settings.json 的读写和容错、追加的保护目录、设置页。</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name) => Path.Combine(_dir, name);

    [Fact]
    public void RoundTripsAndNormalizes()
    {
        var path = File("settings.json");
        var s = new AppSettings
        {
            Snapshot = new SnapshotSettings { AutoAfterScan = false, DailyTime = "03:00", MinDirMb = 100, KeepDays = 365 },
            Cleanup = new CleanupSettings { TempMinAgeDays = 7, AskWhenLocked = true, ProtectedPaths = [@"D:\work\keep"] },
        };
        s.Save(path);
        var loaded = AppSettings.Load(path, out var error);
        Assert.Null(error);
        Assert.Equal(s.Snapshot, loaded.Snapshot);
        Assert.Equal(7, loaded.Cleanup.TempMinAgeDays);
        Assert.True(loaded.Cleanup.AskWhenLocked);
        Assert.Equal([@"D:\work\keep"], loaded.Cleanup.ProtectedPaths);
        Assert.Equal(100L << 20, loaded.SnapshotOptions().MinDirBytes);

        // 手改出来的奇怪值收回到界面能选的值；无效的保护目录丢掉
        System.IO.File.WriteAllText(path, """
            { "snapshot": { "minDirMb": 63, "keepDays": 9999, "dailyTime": "25:99" },
              "cleanup": { "tempMinAgeDays": 3, "protectedPaths": ["C:\\", "relative\\dir", "D:\\a*b", "D:\\ok", "d:\\OK"] } }
            """);
        var odd = AppSettings.Load(path, out error);
        Assert.Null(error);
        Assert.Equal(50, odd.Snapshot.MinDirMb);
        Assert.Equal(365, odd.Snapshot.KeepDays);
        Assert.Equal("12:00", odd.Snapshot.DailyTime);
        Assert.Equal(1, odd.Cleanup.TempMinAgeDays);
        Assert.Equal([@"D:\ok"], odd.Cleanup.ProtectedPaths);
    }

    [Fact]
    public void BrokenFilesFallBackToDefaultsAndAreKept()
    {
        var path = File("settings.json");
        System.IO.File.WriteAllText(path, "{ not json");
        var s = AppSettings.Load(path, out var error);
        Assert.NotNull(error);
        Assert.Equal(new AppSettings().Snapshot, s.Snapshot);
        Assert.False(System.IO.File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "settings.json.bad-*")); // 坏文件改名留着，不删除
    }

    [Fact]
    public void ProtectedFoldersOnAnyDriveBecomeHardProtection()
    {
        var s = new AppSettings { Cleanup = new CleanupSettings { ProtectedPaths = [@"D:\work\keep", @"C:\Users\demo\AppData\Local\Temp\mine"] } };
        var check = new ProtectionCheck(PathContext.Default, s.ProtectionEntries());
        Assert.Equal(ProtectionLevel.Hard, check.LevelFor(@"D:\work\keep\a\b.txt"));
        Assert.Equal(ProtectionLevel.Hard, check.LevelFor(@"C:\Users\demo\AppData\Local\Temp\mine\x.tmp"));
        Assert.Equal(ProtectionLevel.None, check.LevelFor(@"C:\Users\demo\AppData\Local\Temp\other.tmp"));

        // 规则引擎同样认得（扫描时就标成受保护）
        var tree = TestTrees.Build((@"Users\demo\AppData\Local\Temp\mine\x.tmp", 1 << 20), (@"Users\demo\AppData\Local\Temp\y.tmp", 1 << 20));
        var rules = new RuleSet([], [new LocationRule
        {
            Id = "t.temp", Mode = MatchMode.Dir, Pattern = @"%LOCALAPPDATA%\Temp", Kind = Kind.Temp, Risk = Risk.Safe, Tier = Tier.Reviewed,
            Purpose = "临时文件", Impact = "无", Actions = [new DeleteAction(DeleteScope.Contents)],
        }]);
        var c = new RuleEngine(rules, PathContext.Default, s.ProtectionEntries()).Classify(tree);
        Assert.Equal(Risk.Never, c.EffectiveRisk(tree.FindPath(@"C:\Users\demo\AppData\Local\Temp\mine\x.tmp")));
        Assert.Equal(1L << 20, c.Cleanable(tree.FindPath(@"C:\Users\demo\AppData\Local\Temp")));
    }

    [Fact]
    public void SettingsPageRaisesChanges() => RunOnSta(() =>
    {
        var page = new SettingsPage();
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(1200, 800), ShowInTaskbar = false };
        page.Dock = DockStyle.Fill;
        form.Controls.Add(page);
        form.Show();
        page.SetSettings(new AppSettings());
        AppSettings? changed = null;
        page.SettingsChanged += (_, s) => changed = s;

        page.TempAgeBox.SelectedIndex = 1;
        Assert.Equal(7, changed?.Cleanup.TempMinAgeDays);
        Assert.Equal(7, page.Settings.Cleanup.TempMinAgeDays);

        // 重新绑定不触发事件
        changed = null;
        page.SetSettings(new AppSettings { Cleanup = new CleanupSettings { ProtectedPaths = [@"D:\keep"] } });
        Assert.Null(changed);
        Assert.Equal([@"D:\keep"], page.ProtectedList.Items.Cast<string>());
    });

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
}

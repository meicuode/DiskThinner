using DiskThinner.Scanner;
using Microsoft.Data.Sqlite;
using static DiskThinner.Storage.Tests.Trees;

namespace DiskThinner.Storage.Tests;

/// <summary>
/// 热点分解的 fixtures（研发方案 M3 验收）：一增一减、并列增长源、大量小文件、命中规则即停；
/// 每个用例都用同一份数据分别做"完整树对比"（不剪枝）和"剪枝后对比"，热点列表必须一致。
/// </summary>
public sealed class HotspotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-hot-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);

    public HotspotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private const string Local = @"C:\Users\demo\AppData\Local";

    /// <summary>同一对树：先存"之前"，再和"之后"对比，分别用不剪枝和默认剪枝两种方式。返回两份热点列表。</summary>
    private (List<GrowthHotspot> Full, List<GrowthHotspot> Pruned) Compare(NodeTree before, NodeTree after, Func<string, bool>? stopAt = null)
    {
        List<GrowthHotspot> Run(SnapshotOptions options, string name)
        {
            var store = new SnapshotStore(Path.Combine(_dir, name));
            var a = store.Save(before, Stats(), T0, options);
            var b = store.Save(after, Stats(), T0.AddDays(1), options);
            return SnapshotDiff.Compute(a, b).Hotspots(stopAt: stopAt);
        }

        return (Run(SnapshotOptions.Everything, "full.db"), Run(SnapshotOptions.Default, "pruned.db"));
    }

    private static void AssertSame(List<GrowthHotspot> full, List<GrowthHotspot> pruned)
    {
        Assert.Equal(full.Select(h => (h.Path, h.Change, h.IsRemainder)), pruned.Select(h => (h.Path, h.Change, h.IsRemainder)));
    }

    /// <summary>一个包里有 n 个 1 MB 的小文件。</summary>
    private static IEnumerable<(string, long)> Small(string dir, int n, string prefix = "f") =>
        Enumerable.Range(0, n).Select(i => ($@"{dir}\{prefix}{i}.tmp", Mb));

    [Fact]
    public void OneUpOneDownAreBothReported()
    {
        var before = Build([($@"{Local}\A\a.bin", 300 * Mb), ($@"{Local}\B\b.bin", 600 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var after = Build([($@"{Local}\A\a.bin", 600 * Mb), ($@"{Local}\B\b.bin", 300 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var (full, pruned) = Compare(before, after);

        // 净变化为 0，但两个方向的变化都要看到
        Assert.Equal([($@"{Local}\A\a.bin", 300 * Mb), ($@"{Local}\B\b.bin", -300 * Mb)], full.Select(h => (h.Path, h.Change)));
        AssertSame(full, pruned);
    }

    [Fact]
    public void ParallelGrowthSourcesAreReportedSeparately()
    {
        var before = Build([($@"{Local}\uv\cache\w.whl", 500 * Mb), .. Small($@"{Local}\Temp", 100), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var after = Build([($@"{Local}\uv\cache\w.whl", 500 * Mb), ($@"{Local}\uv\cache\x.whl", 200 * Mb), .. Small($@"{Local}\Temp", 250), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var (full, pruned) = Compare(before, after);

        Assert.Equal([($@"{Local}\uv\cache\x.whl", 200 * Mb), ($@"{Local}\Temp", 150 * Mb)], full.Select(h => (h.Path, h.Change)));
        AssertSame(full, pruned);
    }

    [Fact]
    public void ChangesSpreadOverManySmallFilesStopAtTheirDirectory()
    {
        // Temp 里删了 300 个、新建了 450 个小文件：没有单个子项超过阈值，报告 Temp 本身
        var before = Build([.. Small($@"{Local}\Temp", 300, "old"), ($@"{Local}\Other\o.bin", 400 * Mb)]);
        var after = Build([.. Small($@"{Local}\Temp", 450, "new"), ($@"{Local}\Other\o.bin", 400 * Mb)]);
        var (full, pruned) = Compare(before, after);

        var temp = Assert.Single(full);
        Assert.Equal($@"{Local}\Temp", temp.Path);
        Assert.Equal(150 * Mb, temp.Change);
        Assert.False(temp.IsRemainder);
        AssertSame(full, pruned);
    }

    [Fact]
    public void RuleLocationsStopTheDecomposition()
    {
        var before = Build([($@"{Local}\Docker\wsl\disk\data.vhdx", 4000 * Mb)]);
        var after = Build([($@"{Local}\Docker\wsl\disk\data.vhdx", 7000 * Mb)]);
        bool Stop(string path) => string.Equals(path, $@"{Local}\Docker\wsl", StringComparison.OrdinalIgnoreCase);
        var (full, pruned) = Compare(before, after, Stop);

        Assert.Equal([($@"{Local}\Docker\wsl", 3000 * Mb)], full.Select(h => (h.Path, h.Change)));
        AssertSame(full, pruned);
    }

    [Fact]
    public void RemainderIsReportedWhenTheRestIsBigEnough()
    {
        // Local 下：Big 增加 500 MB，另有 150 个散落的小目录各增加 1 MB
        var before = Build([($@"{Local}\Big\b.bin", 100 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var after = Build([($@"{Local}\Big\b.bin", 600 * Mb), .. Enumerable.Range(0, 150).Select(i => ($@"{Local}\s{i}\x.tmp", Mb)), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var (full, pruned) = Compare(before, after);

        Assert.Equal([($@"{Local}\Big\b.bin", 500 * Mb, false), (Local, 150 * Mb, true)], full.Select(h => (h.Path, h.Change, h.IsRemainder)));
        AssertSame(full, pruned);
    }

    [Fact]
    public void DirectoryThatCrossesTheThresholdIsNotCountedTwice()
    {
        // X 从 40 MB（低于记录阈值）涨到 90 MB：剪枝后"之前"没有 X，不能把 +90 和上级的 −40 都算进变化量
        var before = Build([($@"{Local}\X\x.bin", 40 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var after = Build([($@"{Local}\X\x.bin", 90 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]);
        var (full, pruned) = Compare(before, after);
        Assert.Empty(full);
        Assert.Empty(pruned);

        var store = new SnapshotStore(Path.Combine(_dir, "x.db"));
        var diff = SnapshotDiff.Compute(store.Save(before, Stats(), T0), store.Save(after, Stats(), T0.AddDays(1)));
        Assert.Equal(50 * Mb, diff.NetChange);
        Assert.Equal(50 * Mb, diff.Increase);
        Assert.Equal(0, diff.Decrease);
    }

    [Fact]
    public void CurrentScanIsPrunedAgainstTheBaselineSoShrunkFilesKeepTheirRealSize()
    {
        // 起点和终点之间还有一次快照（k.bin 已经变小，没再记录）；和起点对比时 k.bin 应该是 −150 MB，而不是按 0 算的 −200 MB
        var store = new SnapshotStore(Path.Combine(_dir, "shrunk.db"));
        var from = store.Save(Build([($@"{Local}\keep\k.bin", 200 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]), Stats(), T0);
        var shrunk = Build([($@"{Local}\keep\k.bin", 50 * Mb), (@"C:\Windows\w.bin", 1000 * Mb)]);
        store.Save(shrunk, Stats(), T0.AddDays(1));
        store.Save(shrunk, Stats(), T0.AddDays(2));

        var current = store.Preview(shrunk, Stats(), T0.AddDays(3), compareWith: from);
        var hot = Assert.Single(SnapshotDiff.Compute(from, current).Hotspots());
        Assert.Equal(($@"{Local}\keep\k.bin", -150 * Mb, 50 * Mb), (hot.Path, hot.Change, hot.Current));
    }

    [Fact]
    public void ComparesWithTheCurrentScanWithoutSaving()
    {
        var store = new SnapshotStore(Path.Combine(_dir, "current.db"));
        var saved = store.Save(Build([($@"{Local}\A\a.bin", 200 * Mb)]), Stats(), T0);
        var current = store.Preview(Build([($@"{Local}\A\a.bin", 200 * Mb), ($@"{Local}\New\n.bin", 300 * Mb)]), Stats(), T0.AddDays(1));
        var hot = Assert.Single(SnapshotDiff.Compute(saved, current).Hotspots());
        Assert.Equal(($@"{Local}\New\n.bin", 300 * Mb, 300 * Mb, 0L), (hot.Path, hot.Change, hot.Current, hot.Previous));
    }
}

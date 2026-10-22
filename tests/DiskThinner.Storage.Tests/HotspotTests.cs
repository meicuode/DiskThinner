using DiskThinner.Scanner;
using Microsoft.Data.Sqlite;
using static DiskThinner.Storage.Tests.Trees;
namespace DiskThinner.Storage.Tests;
/// <summary>
/// 热点分解的基本用例（一增一减、并列增长源、大量小文件、命中规则即停、其余部分、跨过记录阈值）
/// 写在 JSON fixtures 里：tests/fixtures/hotspots/*.json，由 <see cref="HotspotFixtureTests"/> 运行。
/// 这里只留需要多次快照或"当前扫描不保存"的用例。
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

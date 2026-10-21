using DiskThinner.Scanner;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Storage.Tests;

internal static class Trees
{
    public const long Mb = 1L << 20;

    /// <summary>用"路径 → 大小"列表构造树，路径从 C:\ 开始。</summary>
    public static NodeTree Build(params (string Path, long Size)[] files)
    {
        var tree = new NodeTree(64);
        tree.Root = tree.Add(-1, @"C:\", NodeFlags.Directory, 0, 0, 0);
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["C:"] = tree.Root };

        int Dir(string path)
        {
            if (dirs.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var slash = path.LastIndexOf('\\');
            var index = tree.Add(Dir(path[..slash]), path.AsSpan(slash + 1), NodeFlags.Directory, 0, 0, 0);
            dirs[path] = index;
            return index;
        }

        foreach (var (path, size) in files)
        {
            var slash = path.LastIndexOf('\\');
            tree.Add(Dir(path[..slash]), path.AsSpan(slash + 1), NodeFlags.None, size, size, 0);
        }

        tree.Finalize();
        return tree;
    }

    public static ScanStats Stats(long total = 200L << 30, long free = 50L << 30) =>
        new() { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = total, VolumeFreeBytes = free };
}

public sealed class SnapshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-snap-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);

    public SnapshotTests() => Directory.CreateDirectory(_dir);

    private SnapshotStore Store(string name = "snapshots.db") => new(Path.Combine(_dir, name));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void PrunesByThresholdAndKeepsUnrecordedBytes()
    {
        var tree = Trees.Build(
            (@"C:\Big\a.bin", 120 * Trees.Mb),      // 大文件：单独记录
            (@"C:\Big\small\x.txt", 30 * Trees.Mb), // 小目录：并入 Big 的未记录部分
            (@"C:\Small\y.txt", 10 * Trees.Mb));
        var snap = Store().Save(tree, Trees.Stats(), T0);

        Assert.Equal([@"C:\", @"C:\Big", @"C:\Big\a.bin"], Enumerable.Range(0, snap.Count).Select(snap.GetPath).Order(StringComparer.Ordinal));
        var big = snap.Find(@"C:\Big");
        Assert.Equal(150 * Trees.Mb, snap.Alloc(big));
        Assert.Equal(30 * Trees.Mb, snap.Unrecorded(big));
        Assert.Equal(10 * Trees.Mb, snap.Unrecorded(0)); // 根下的 Small
        Assert.Equal(160 * Trees.Mb, snap.Info.RootBytes);
    }

    [Fact]
    public void DepthLimitMergesDeeperLevels()
    {
        var tree = Trees.Build((@"C:\a\b\c\d\big.bin", 200 * Trees.Mb));
        var snap = Store().Save(tree, Trees.Stats(), T0, new SnapshotOptions(MaxDepth: 2));
        Assert.True(snap.Find(@"C:\a\b") >= 0);
        Assert.Equal(-1, snap.Find(@"C:\a\b\c"));
        Assert.Equal(200 * Trees.Mb, snap.Unrecorded(snap.Find(@"C:\a\b")));
    }

    [Fact]
    public void RoundTripKeepsPathIdsStableAndRerecordsShrunkNodes()
    {
        var store = Store();
        var first = store.Save(Trees.Build((@"C:\Cache\blob.bin", 2048 * Trees.Mb), (@"C:\Keep\k.bin", 500 * Trees.Mb)), Trees.Stats(), T0);

        // 第二次：Cache 降到 10 MB，低于阈值，也要再记一次
        var second = store.Save(Trees.Build((@"C:\Cache\blob.bin", 10 * Trees.Mb), (@"C:\Keep\k.bin", 500 * Trees.Mb)), Trees.Stats(), T0.AddDays(1));
        var cache = second.Find(@"C:\Cache");
        Assert.True(cache >= 0);
        Assert.Equal(10 * Trees.Mb, second.Alloc(cache));
        Assert.Equal(first.PathId(first.Find(@"C:\Cache")), second.PathId(cache));

        // 第三次：上次已经是低于阈值的"再记一次"，这次不再记
        var third = store.Save(Trees.Build((@"C:\Cache\blob.bin", 10 * Trees.Mb), (@"C:\Keep\k.bin", 500 * Trees.Mb)), Trees.Stats(), T0.AddDays(2));
        Assert.Equal(-1, third.Find(@"C:\Cache"));

        var loaded = store.Load(second.Info.Id);
        Assert.Equal(second.Count, loaded.Count);
        Assert.Equal(10 * Trees.Mb, loaded.Alloc(loaded.Find(@"C:\Cache")));
        Assert.Equal(second.Info, loaded.Info);
        Assert.Equal(3, store.List(@"C:\").Count);
    }

    [Fact]
    public void PreviewDoesNotWrite()
    {
        var store = Store();
        store.Save(Trees.Build((@"C:\A\a.bin", 200 * Trees.Mb)), Trees.Stats(), T0);
        var size = store.FileBytes;
        var preview = store.Preview(Trees.Build((@"C:\A\a.bin", 300 * Trees.Mb), (@"C:\New\n.bin", 200 * Trees.Mb)), Trees.Stats(), T0.AddDays(1));
        Assert.True(preview.PathId(preview.Find(@"C:\New")) < 0);
        Assert.True(preview.PathId(preview.Find(@"C:\A")) > 0);
        Assert.Single(store.List());
        Assert.Equal(size, store.FileBytes);
    }

    /// <summary>验收：单次快照 ≤ 1 MB。用一棵 30 万个文件、两百多 GB 的树：绝大多数是 1 MB 以内的小文件，少数几百 MB 到 2 GB 的大文件。</summary>
    [Fact]
    public void SnapshotOfALargeTreeIsUnderOneMegabyte()
    {
        var random = new Random(42);
        var files = new List<(string, long)>();
        for (var i = 0; i < 300_000; i++)
        {
            var depth = random.Next(2, 9);
            var parts = Enumerable.Range(0, depth).Select(d => $"d{d}_{random.Next(0, d < 2 ? 12 : 40)}");
            var size = random.NextDouble() < 0.0003
                ? (long)((100 + random.NextDouble() * 1900) * Trees.Mb)
                : (long)(random.NextDouble() * Trees.Mb);
            files.Add(($@"C:\{string.Join('\\', parts)}\f{i}.bin", size));
        }

        var store = Store();
        var before = store.FileBytes;
        var snap = store.Save(Trees.Build([.. files]), Trees.Stats(), T0);
        var bytes = store.FileBytes - before;
        Assert.True(bytes <= 1 << 20, $"快照占用 {bytes:N0} 字节，{snap.Count:N0} 项");
    }

    [Fact]
    public void RetentionThinsOldSnapshotsToOnePerWeek()
    {
        var now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<SnapshotInfo>();
        for (var d = 0; d < 200; d++)
        {
            list.Add(new SnapshotInfo(d + 1, now.AddDays(-d), @"C:\", 0, 0, "mft", 0, 0, 0, 1));
        }

        var remove = SnapshotRetention.ToRemove(list, now.AddDays(-180)).ToHashSet();
        Assert.All(list.Where(s => s.TakenAtUtc >= now.AddDays(-180)), s => Assert.DoesNotContain(s.Id, remove));
        var old = list.Where(s => s.TakenAtUtc < now.AddDays(-180)).ToList();
        var kept = old.Where(s => !remove.Contains(s.Id)).ToList();
        Assert.Equal(old.Select(s => System.Globalization.ISOWeek.GetWeekOfYear(s.TakenAtUtc)).Distinct().Count(), kept.Count);
    }

    [Fact]
    public void RetentionDeletesFromTheStore()
    {
        var store = Store();
        var tree = Trees.Build((@"C:\A\a.bin", 200 * Trees.Mb));
        var old = T0.AddDays(-400);
        store.Save(tree, Trees.Stats(), old);            // 同一周的两次，早于保留期
        store.Save(tree, Trees.Stats(), old.AddHours(5));
        store.Save(tree, Trees.Stats(), T0);
        Assert.Equal(1, store.ApplyRetention(T0));
        var left = store.List();
        Assert.Equal(2, left.Count);
        Assert.Equal(old.AddHours(5), left[0].TakenAtUtc);
    }

    [Fact]
    public void GrowthRateAndDailyChanges()
    {
        var points = new[]
        {
            new GrowthStats.Point(T0, 100L << 30, 100L << 30),
            new GrowthStats.Point(T0.AddDays(1), 101L << 30, 99L << 30),
            new GrowthStats.Point(T0.AddDays(1).AddHours(2), 102L << 30, 98L << 30), // 同一天取最后一个
            new GrowthStats.Point(T0.AddDays(3), 105L << 30, 95L << 30),             // 中间缺一天
        };
        var daily = GrowthStats.Daily(points, T0.AddDays(-1), T0.AddDays(5), TimeZoneInfo.Utc);
        Assert.Equal([(DateOnly.FromDateTime(T0.AddDays(1)), 2L << 30), (DateOnly.FromDateTime(T0.AddDays(3)), 3L << 30)], daily);

        var (change, days, toFull) = GrowthStats.Rate(points[0], points[^1]);
        Assert.Equal(5L << 30, change);
        Assert.Equal(3, days, 3);
        Assert.Equal(57, toFull!.Value, 1); // 95 GB / (5 GB / 3 天)
        Assert.Null(GrowthStats.Rate(points[^1], points[0]).DaysToFull);
    }
}

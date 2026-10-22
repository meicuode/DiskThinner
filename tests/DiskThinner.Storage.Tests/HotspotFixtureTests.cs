using System.Text.Json;
using DiskThinner.Scanner;
using DiskThinner.Tests.Shared;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Storage.Tests;

/// <summary>
/// 热点分解的 JSON fixtures（tests/fixtures/hotspots/*.json，格式见 tests/fixtures/README.md）。
/// M3 验收：同一份数据分别用"完整树对比"（不剪枝）和"剪枝后对比"，热点列表都必须等于期望。
/// </summary>
public sealed class HotspotFixtureTests : IDisposable
{
    private const string Kind = "hotspots";
    private static readonly DateTime T0 = new(2026, 10, 1, 4, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-hotfx-" + Guid.NewGuid().ToString("N"));

    public HotspotFixtureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

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
    public void FixturesExist() => Assert.True(Cases().Count >= 6, $"只找到 {Cases().Count} 个用例");

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hotspots(string file, string name)
    {
        using var doc = Fixtures.Load(Kind, file);
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var before = Fixtures.Tree(c.GetProperty("before"));
        var after = Fixtures.Tree(c.GetProperty("after"));
        var stops = c.TryGetProperty("stop_at", out var s)
            ? s.EnumerateArray().Select(v => Fixtures.Path(v.GetString()!)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        Func<string, bool>? stopAt = stops.Count == 0 ? null : stops.Contains;
        var expected = c.GetProperty("expect").EnumerateArray()
            .Select(e => (Fixtures.Path(e.GetProperty("path").GetString()!).ToUpperInvariant(), Fixtures.Size(e.GetProperty("change")),
                e.TryGetProperty("remainder", out var r) && r.GetBoolean()))
            .ToList();

        foreach (var (label, options) in new[] { ("不剪枝", SnapshotOptions.Everything), ("默认剪枝", SnapshotOptions.Default) })
        {
            var store = new SnapshotStore(Path.Combine(_dir, $"{label}-{Guid.NewGuid():N}.db"));
            var diff = SnapshotDiff.Compute(store.Save(before, Stats(), T0, options), store.Save(after, Stats(), T0.AddDays(1), options));
            var actual = diff.Hotspots(stopAt: stopAt).Select(h => (h.Path.ToUpperInvariant(), h.Change, h.IsRemainder)).ToList();
            Assert.True(expected.SequenceEqual(actual),
                $"{label}：\n期望 {string.Join("; ", expected)}\n实际 {string.Join("; ", actual)}");

            if (c.TryGetProperty("totals", out var totals))
            {
                Assert.Equal((Fixtures.Size(totals.GetProperty("net")), Fixtures.Size(totals.GetProperty("increase")), Fixtures.Size(totals.GetProperty("decrease"))),
                    (diff.NetChange, diff.Increase, diff.Decrease));
            }
        }
    }

    private static ScanStats Stats() => new() { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = 50L << 30 };
}

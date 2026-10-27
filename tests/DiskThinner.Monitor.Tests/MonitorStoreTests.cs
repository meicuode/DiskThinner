using Microsoft.Data.Sqlite;

namespace DiskThinner.Monitor.Tests;

/// <summary>monitor.db（docs/phase2a-design.md 5.8 节）：落盘、查询、孤儿会话、保留和最坏情况的体积。</summary>
public sealed class MonitorStoreTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-mon-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime T0 = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private MonitorStore Store(string name = "m.db")
    {
        Directory.CreateDirectory(_dir);
        return MonitorStore.Open(Path.Combine(_dir, name));
    }

    private static readonly ProcessChain Uv = new(ChainKind.Normal, @"C:\Users\demo\.local\bin\uv.exe", "", @"C:\Program Files\Code\Code.exe", "", "uv.exe ← pwsh.exe ← Code.exe");
    private static readonly ProcessChain Node = new(ChainKind.Normal, @"C:\nodejs\node.exe", "", @"C:\Users\demo\.local\bin\claude.exe", "", "node.exe ← claude.exe");

    private static SessionRecord Session(DateTime start, string? name = null) =>
        new() { Name = name ?? SessionDeadline.NewSessionName(start), StartedUtc = start, PlannedEndUtc = start.AddHours(4), Mode = MonitorMode.Precise };

    private static HourData Hour(DateTime hour, params StoredRow[] rows) => new(hour, MonitorMode.Precise, new MonitorStats { Received = 100, Lost = 1 }, 0, rows);

    private static Counters C(long growth = 0, long bytes = 0, long creates = 0) => new() { Growth = growth, WriteBytes = bytes, WriteOps = bytes > 0 ? 1 : 0, Creates = creates };

    [Fact]
    public void FlushAndQueryRoundTrip()
    {
        var store = Store();
        var s = Session(T0);
        store.BeginSession(s);
        store.Flush(s, [Hour(T0,
            new StoredRow(@"C:\Users\demo\AppData\Local\uv\cache", Uv, C(growth: 1200, bytes: 1500, creates: 8921)),
            new StoredRow(@"C:\Users\demo\.claude\projects", Node, C(growth: 620)),
            new StoredRow(null, Node, C(bytes: 3_000_000)))], path => path.EndsWith("uv.exe", StringComparison.OrdinalIgnoreCase) ? ("uv", "Astral") : (null, null));

        var index = store.LoadIndex(T0.AddHours(-1), T0.AddHours(2));
        Assert.Single(index.Sessions);
        Assert.Equal(1, index.MonitoredHours);
        var uv = Assert.Single(index.Under(@"C:\Users\demo\AppData\Local"));
        Assert.Equal((1200L, 8921L), (uv.Counters.Growth, uv.Counters.Creates));
        Assert.Equal("uv", uv.Chain.Writer!.Product);
        Assert.Equal("Code.exe", uv.Chain.Top!.ExeName);
        Assert.Equal("uv.exe ← pwsh.exe ← Code.exe", uv.Chain.Links);
        Assert.Empty(index.Under(@"C:\Windows"));
        Assert.Equal(3_000_000, index.Rows.Where(r => r.Dir is null).Sum(r => r.Counters.WriteBytes)); // 对不上目录
        Assert.DoesNotContain(index.Under(@"C:\"), x => x.Counters.WriteBytes == 3_000_000);

        // 排行包含对不上目录的写入
        var top = index.Writers()[0];
        Assert.Equal("node.exe", top.Writer!.ExeName);
        Assert.Contains(null, top.MainDirs);
    }

    [Fact]
    public void FlushingTheSameHourAgainReplacesItsRows()
    {
        var store = Store();
        var s = Session(T0);
        store.BeginSession(s);
        store.Flush(s, [Hour(T0, new StoredRow(@"C:\a", Uv, C(growth: 100)))]);
        store.Flush(s, [Hour(T0, new StoredRow(@"C:\a", Uv, C(growth: 300)))]);
        Assert.Equal(300, store.LoadIndex(T0, T0.AddHours(1)).Under(@"C:\a").Single().Counters.Growth);
    }

    [Fact]
    public void ExcludedHotspotsAreSubtracted()
    {
        var store = Store();
        var s = Session(T0);
        store.BeginSession(s);
        store.Flush(s, [Hour(T0, new StoredRow(@"C:\x", Uv, C(growth: 100)), new StoredRow(@"C:\x\hot", Uv, C(growth: 900)), new StoredRow(@"C:\x\hotter", Uv, C(growth: 5)))]);
        var index = store.LoadIndex(T0, T0.AddHours(1));
        Assert.Equal(105, index.Under(@"C:\x", [@"C:\x\hot"]).Single().Counters.Growth); // "x 的其余部分"
        Assert.Equal(100, index.Under(@"C:\x", exact: true).Single().Counters.Growth);
        Assert.Equal(@"C:\x", index.NearestRecordedAncestor(@"C:\x\new-dir"));
    }

    [Fact]
    public void OpenSessionsAreMarkedCrashedOnlyIfUnchanged()
    {
        var store = Store();
        var s = Session(T0);
        store.BeginSession(s);
        var open = Assert.Single(store.OpenSessions());
        Assert.Equal(s.Name, open.SessionName);

        // 读到之后别的实例又落盘了一次：不改
        s.LastFlushUtc = T0.AddMinutes(5);
        store.Flush(s, []);
        Assert.Equal(0, store.MarkCrashed([open]));
        Assert.Equal(1, store.MarkCrashed(store.OpenSessions()));
        Assert.Empty(store.OpenSessions());
        Assert.Equal("crashed", store.Sessions(T0.AddDays(-1), T0.AddDays(1)).Single().EndReason);
    }

    [Fact]
    public void RetentionDeletesWholeOldSessions()
    {
        var store = Store();
        foreach (var days in new[] { 100, 50, 1 })
        {
            var s = Session(T0.AddDays(-days));
            store.BeginSession(s);
            s.EndedUtc = s.StartedUtc.AddHours(1);
            s.LastFlushUtc = s.EndedUtc.Value;
            store.Flush(s, [Hour(s.StartedUtc, new StoredRow($@"C:\d{days}", Uv, C(growth: days)))]);
        }

        var running = Session(T0, "DiskThinner-Diag-20261027080000");
        store.BeginSession(running); // 正在进行的会话不删

        Assert.Equal(1, store.Prune(T0, TimeSpan.FromDays(90), long.MaxValue));
        var left = store.Sessions(T0.AddDays(-200), T0.AddDays(1));
        Assert.Equal(3, left.Count);
        Assert.DoesNotContain(store.LoadIndex(T0.AddDays(-200), T0.AddDays(1)).Rows, r => r.Dir == @"C:\d100");
    }

    [Fact]
    public void SizeLimitDeletesOnlyTheOldestSessionsNeeded()
    {
        var store = Store();
        for (var i = 0; i < 3; i++)
        {
            var s = Session(T0.AddDays(i - 3));
            store.BeginSession(s);
            s.EndedUtc = s.StartedUtc.AddHours(1);
            s.LastFlushUtc = s.EndedUtc.Value;
            var rows = Enumerable.Range(0, 2000).Select(k => new StoredRow($@"C:\s{i}\dir{k:D5}-padding-padding", Uv, C(growth: k + 1))).ToArray();
            store.Flush(s, [Hour(s.StartedUtc, rows)]);
        }

        var used = store.UsedBytes;
        var deleted = store.Prune(T0, TimeSpan.FromDays(90), used * 3 / 4);
        Assert.Equal(1, deleted); // 超出上限时只删最早的一个
        Assert.True(store.UsedBytes <= used * 3 / 4, $"{store.UsedBytes} > {used * 3 / 4}");
        Assert.Equal(2, store.Sessions(T0.AddDays(-10), T0.AddDays(1)).Count);
    }

    [Theory]
    [InlineData(1000, 4L << 20)]
    [InlineData(2000, 8L << 20)]
    public void WorstCaseDayFitsTheSizeTarget(int rowsPerHour, long limit)
    {
        // 第 5.8 节的最坏情况：24 小时；每小时一半的行在固定的 500 个锚点上（深度 4），一半在这个小时新出现的目录上
        // （锚点下再加 2 段，每段 12 个字符）；500 种进程链、300 个映像（路径 80 个字符）；计数取上限附近的值
        var store = Store($"worst-{rowsPerHour}.db");
        var s = Session(T0);
        store.BeginSession(s);
        var images = Enumerable.Range(0, 300).Select(i => @"C:\Program Files\Vendor" + i.ToString("D3") + @"\Product\bin\" + new string('x', 40) + i + ".exe").ToArray();
        var chains = Enumerable.Range(0, 500).Select(i => new ProcessChain(ChainKind.Normal, images[i % 300], "", images[(i * 7) % 300], "", $"w{i}.exe ← p{i}.exe ← t{i}.exe")).ToArray();
        var anchors = Enumerable.Range(0, 500).Select(i => $@"C:\Users\demo\AppData\a{i:D3}").ToArray();
        var big = new Counters { Creates = 10_000, CreatedDirs = 100, Deletes = 10_000, Renames = 1000, WriteBytes = 1L << 40, WriteOps = 1_000_000, Growth = 1L << 39, Sampled = true };
        for (var h = 0; h < 24; h++)
        {
            var rows = new List<StoredRow>();
            for (var i = 0; i < rowsPerHour; i++)
            {
                var dir = i < rowsPerHour / 2 ? anchors[i % 500] : $@"{anchors[i % 500]}\h{h:D2}seg{i:D6}\x{h:D2}seg{i:D6}";
                rows.Add(new StoredRow(dir, chains[i % 500], big));
            }

            store.Flush(s, [Hour(T0.AddHours(h), [.. rows])]);
        }

        output.WriteLine($"{rowsPerHour} 行 / 小时：{store.UsedBytes:N0} 字节");
        Assert.True(store.UsedBytes <= limit, $"{rowsPerHour} 行 / 小时：{store.UsedBytes:N0} 字节 > {limit:N0}");
    }

    [Fact]
    public void NewerFormatIsRejected()
    {
        Store("future.db");
        var path = Path.Combine(_dir, "future.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => MonitorStore.Open(path));
        Assert.Null(MonitorStore.OpenExisting(Path.Combine(_dir, "missing.db")));
        Assert.False(File.Exists(Path.Combine(_dir, "missing.db")));
    }
}

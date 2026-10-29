using DiskThinner.Monitor.Agent;

namespace DiskThinner.Monitor.Tests;

/// <summary>
/// 管道协议、调用者过滤和查询的合并（docs/phase2b-design.md 5.5 节、8.1 节 ProtocolTests / CallerFilterTests）。
/// </summary>
public sealed class AgentProtocolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DiskThinner-test-" + Guid.NewGuid().ToString("N"));

    public AgentProtocolTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---------------- 请求的校验 ----------------

    [Theory]
    [InlineData("""{"cmd":"hello","protocol":1,"appVersion":"0.3.0"}""")]
    [InlineData("""{"cmd":"status"}""")]
    [InlineData("""{"cmd":"query","fromUtc":"2026-10-01T00:00:00Z","toUtc":"2026-10-31T00:00:00Z"}""")]
    [InlineData("""{"cmd":"control","action":"stop"}""")]
    [InlineData("""{"cmd":"control","action":"configure","mode":"light"}""")]
    [InlineData("""{"cmd":"control","action":"configure","keepDays":180}""")]
    [InlineData("""{"cmd":"status","somethingNew":42}""")]
    public void ValidRequests_Parse(string line) => Assert.NotNull(AgentProtocol.ParseRequest(line));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"cmd":"format-disk"}""")]
    [InlineData("""{"cmd":"query","fromUtc":"2026-10-31T00:00:00Z","toUtc":"2026-10-01T00:00:00Z"}""")]
    [InlineData("""{"cmd":"query","fromUtc":"2024-01-01T00:00:00Z","toUtc":"2026-10-01T00:00:00Z"}""")]
    [InlineData("""{"cmd":"query"}""")]
    [InlineData("""{"cmd":"control","action":"delete"}""")]
    [InlineData("""{"cmd":"control","action":"configure"}""")]
    [InlineData("""{"cmd":"control","action":"configure","keepDays":45}""")]
    [InlineData("""{"cmd":"control","action":"configure","mode":"fast"}""")]
    public void BadRequests_AreRejected(string line) => Assert.Null(AgentProtocol.ParseRequest(line));

    [Fact]
    public void OversizedRequest_IsRejected()
    {
        var line = """{"cmd":"status","appVersion":" """ + new string('x', AgentProtocol.MaxRequestBytes) + "\"}";
        Assert.Null(AgentProtocol.ParseRequest(line));
    }

    [Fact]
    public void Response_RoundTrips()
    {
        var row = new RowDto
        {
            SessionId = 7, HourUtc = new DateTime(2026, 10, 28, 3, 0, 0, DateTimeKind.Utc), Dir = @"C:\cache", Kind = (int)ChainKind.Normal,
            Writer = new ImageDto { Path = @"C:\x\uv.exe", Services = "", Product = "uv" }, Top = new ImageDto { Path = @"C:\x\Code.exe" }, Links = "uv.exe ← Code.exe",
            Counters = new CountersDto { Creates = 3, Growth = -4096, WriteBytes = 1 << 20, Sampled = true },
        };
        var r = new AgentResponse { Type = "rows", Rows = [row] };
        var back = AgentProtocol.ParseResponse(AgentProtocol.Serialize(r))!;
        var x = Assert.Single(back.Rows!).ToRow();
        Assert.Equal(row.ToRow(), x);
        Assert.Equal(DateTimeKind.Utc, x.HourUtc.Kind);
        Assert.Equal(-4096, x.Counters.Growth);
        Assert.True(x.Counters.Sampled);
        Assert.Equal("uv", x.Chain.Writer!.Product);
    }

    // ---------------- 调用者过滤 ----------------

    private static readonly Dictionary<string, string> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["S-1-5-21-1-1001"] = @"C:\Users\alice",
        ["S-1-5-21-1-1002"] = @"C:\Users\bob",
        ["S-1-5-18"] = @"C:\Windows\system32\config\systemprofile",
    };

    [Fact]
    public void Filter_HidesOtherUsersProfilesOnly()
    {
        Assert.Null(CallerFilter.For(elevatedAdmin: true, "S-1-5-21-1-1001", Profiles));
        var f = CallerFilter.For(elevatedAdmin: false, "S-1-5-21-1-1001", Profiles)!;
        Assert.True(f.HidesDir(@"C:\Users\bob\AppData\Local\Temp"));
        Assert.True(f.HidesDir(@"c:\users\BOB"));
        Assert.True(f.HidesDir(@"C:\Windows\System32\config\systemprofile\AppData"));
        Assert.False(f.HidesDir(@"C:\Users\alice\AppData"));
        Assert.False(f.HidesDir(@"C:\Users\bobby\x"));
        Assert.False(f.HidesDir(@"C:\ProgramData\x"));
        Assert.False(f.HidesDir(null));
        Assert.True(f.IsOtherUserImage(@"C:\Users\bob\tools\x.exe"));
        Assert.False(f.IsOtherUserImage(@"C:\Program Files\Git\git.exe"));

        // 没有身份（匿名）：所有个人目录都看不到
        var anonymous = CallerFilter.For(elevatedAdmin: false, "", Profiles)!;
        Assert.True(anonymous.HidesDir(@"C:\Users\alice\x"));
    }

    private static StoredChain Chain(string writer, string top = @"C:\Windows\explorer.exe") =>
        new(ChainKind.Normal, new StoredImage(writer, "", "P", "C"), new StoredImage(top, "", null, null), Path.GetFileName(writer) + " ← explorer.exe", 0);

    private static IndexRow Row(long session, int hour, string? dir, StoredChain chain, long creates, long growth = 0) =>
        new(session, new DateTime(2026, 10, 28, hour, 0, 0, DateTimeKind.Utc), dir, chain, new Counters { Creates = creates, Growth = growth });

    [Fact]
    public void Query_MergesAcrossSessions_FiltersAndSanitizes()
    {
        var uv = Chain(@"C:\tools\uv.exe");
        var bobTool = Chain(@"C:\Users\bob\bin\sync.exe");
        var index = new WriteIndex([], [
            Row(1, 1, @"C:\cache", uv, 3, 100),
            Row(2, 5, @"C:\cache", uv, 4, 50),
            Row(2, 6, @"C:\Users\bob\AppData\Temp", uv, 9),
            Row(2, 6, @"C:\Users\alice\AppData\Temp", uv, 2),
            Row(2, 7, @"C:\ProgramData\shared", bobTool, 5),
        ], []);
        var f = CallerFilter.For(false, "S-1-5-21-1-1001", Profiles);
        var result = AgentQuery.Build(index, f);
        Assert.Equal(1, result.HiddenRows);
        Assert.Equal(9, result.Hidden.Creates);
        Assert.False(result.Folded);
        var rows = result.Rows.Select(r => r.ToRow()).ToList();
        Assert.DoesNotContain(rows, r => r.Dir!.Contains("bob", StringComparison.OrdinalIgnoreCase));
        var cache = Assert.Single(rows, r => r.Dir == @"C:\cache");
        Assert.Equal(7, cache.Counters.Creates);
        Assert.Equal(150, cache.Counters.Growth);
        Assert.Equal(2, cache.SessionId); // 最近的会话
        Assert.Equal(1, cache.HourUtc.Hour); // 最早的小时
        var shared = Assert.Single(rows, r => r.Dir == @"C:\ProgramData\shared");
        Assert.Equal(AgentProtocol.OtherUserImage, shared.Chain.Writer!.Path);
        Assert.Null(shared.Chain.Writer.Product);
        Assert.Equal(AgentProtocol.OtherUserImage, shared.Chain.Links);
        Assert.Equal(@"C:\Windows\explorer.exe", shared.Chain.Top!.Path);

        // 管理员：不过滤
        var admin = AgentQuery.Build(index, null);
        Assert.Equal(0, admin.HiddenRows);
        Assert.Contains(admin.Rows, r => r.Dir == @"C:\Users\bob\AppData\Temp");
        Assert.Contains(admin.Rows, r => r.Writer!.Path == @"C:\Users\bob\bin\sync.exe");
    }

    [Fact]
    public void Query_FoldsLightestRowsIntoParents()
    {
        var uv = Chain(@"C:\tools\uv.exe");
        var rows = new List<IndexRow> { Row(1, 1, @"C:\big", uv, 1000) };
        for (var i = 0; i < 10; i++)
        {
            rows.Add(Row(1, 1, $@"C:\small\d{i}\x", uv, 1));
        }

        var result = AgentQuery.Build(new WriteIndex([], rows, []), null, maxRows: 3);
        Assert.True(result.Folded);
        Assert.True(result.Rows.Count <= 3);
        Assert.Equal(1010, result.Rows.Sum(r => r.Counters.Creates)); // 并到上级，计数不丢
        Assert.Contains(result.Rows, r => r.Dir == @"C:\big" && r.Counters.Creates == 1000);
    }

    [Fact]
    public void Store_LoadMerged_SumsAcrossSessionsAndHours()
    {
        var store = MonitorStore.Open(Path.Combine(_dir, "m.db"));
        var chain = new ProcessChain(ChainKind.Normal, @"C:\x\uv.exe", "", @"C:\x\Code.exe", "", "uv.exe ← Code.exe");
        var day = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        long first = 0;
        for (var s = 0; s < 2; s++)
        {
            var start = day.AddDays(s);
            var rec = new SessionRecord { Name = "DiskThinner-Agent-" + s, StartedUtc = start, PlannedEndUtc = start.AddDays(1), Mode = MonitorMode.Precise };
            store.BeginSession(rec);
            first = s == 0 ? rec.Id : first;
            store.Flush(rec, [
                new HourData(start.AddHours(2), MonitorMode.Precise, new MonitorStats { Received = 5 }, 0, [new StoredRow(@"C:\cache", chain, new Counters { Creates = 2, WriteBytes = 10 })]),
                new HourData(start.AddHours(3), MonitorMode.Precise, new MonitorStats { Received = 5 }, 0, [new StoredRow(@"C:\cache", chain, new Counters { Creates = 1, Sampled = s == 1 })]),
            ]);
        }

        var merged = store.LoadMerged(day, day.AddDays(3));
        var row = Assert.Single(merged.Rows);
        Assert.Equal(6, row.Counters.Creates);
        Assert.Equal(20, row.Counters.WriteBytes);
        Assert.True(row.Counters.Sampled);
        Assert.Equal(first + 1, row.SessionId);
        Assert.Equal(day.AddHours(2), row.HourUtc);
        Assert.Equal(4, merged.Hours.Count);
        Assert.Equal(2, merged.Sessions.Count);
        Assert.Equal(day, store.FirstSessionStartUtc());

        // 时间范围只取第二天
        var second = store.LoadMerged(day.AddDays(1), day.AddDays(2));
        Assert.Equal(3, Assert.Single(second.Rows).Counters.Creates);
    }
}

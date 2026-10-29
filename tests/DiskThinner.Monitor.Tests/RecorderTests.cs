using DiskThinner.Monitor.Etw;

namespace DiskThinner.Monitor.Tests;

/// <summary>
/// 服务的按天轮换（docs/phase2b-design.md 5.2 节、8.1 节 RecorderRotationTests）：用可注入的时钟跨过本地 0 点，
/// 会话行在 UTC 整点轮换，整点之前的小时留在旧行、之后的记到新行；估算增长不断（文件状态不清空）；汇总器换新时整点之后的计数不丢。
/// 不开 ETW 会话，事件直接送进 Recorder。
/// </summary>
public sealed class RecorderTests : IDisposable
{
    private const string Dev = @"\Device\HarddiskVolume3";
    private const int Pid = 4321;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DiskThinner-test-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 10, 27, 22, 30, 0, DateTimeKind.Utc);
    private ulong _nextKey = 0x5000;

    public RecorderTests() => Directory.CreateDirectory(_dir);

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

    private Recorder NewRecorder(int rebuildDirectories = 100_000) => new(new RecorderOptions
    {
        DbPath = Path.Combine(_dir, "agent.db"),
        Mode = MonitorMode.Precise,
        UtcNow = () => _now,
        ToLocal = u => u, // 本地时间就是 UTC，0 点 = UTC 0 点
        RotateName = b => "T-" + b.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture),
        Devices = new PrefixDeviceMap(new Dictionary<string, string> { [Dev] = "C:" }),
        Services = new NoServices(),
        SeedProcesses = false,
        RebuildDirectories = rebuildDirectories,
    });

    private static string D(string dosPath) => Dev + dosPath[2..];

    private MonitorEvent E(MonitorEventKind kind, DateTime at) => new() { Kind = kind, ProcessId = Pid, ThreadId = 1, TimestampUtcTicks = at.Ticks };

    private void Start(Recorder r)
    {
        Assert.Null(r.Open());
        r.Begin(new SessionRecord { Name = "T-start", StartedUtc = _now, PlannedEndUtc = _now.AddDays(1), Mode = MonitorMode.Precise });
        var p = E(MonitorEventKind.ProcessStart, _now);
        p.TargetProcessId = Pid;
        p.ParentProcessId = 4;
        p.CreateTimeUtcTicks = _now.AddHours(-1).Ticks;
        p.ImageName = D(@"C:\tools\uv.exe");
        r.OnParsed(p);
    }

    /// <summary>新建一个文件，返回 FileKey。</summary>
    private ulong Create(Recorder r, string path, DateTime at)
    {
        var c = E(MonitorEventKind.CreateNewFile, at);
        c.Path = D(path);
        r.OnParsed(c);
        var key = _nextKey++;
        var n = E(MonitorEventKind.NameCreate, at.AddMilliseconds(1));
        n.Path = D(path);
        n.FileKey = key;
        r.OnParsed(n);
        return key;
    }

    private static void Write(Recorder r, ulong key, ulong offset, uint size, DateTime at)
    {
        var w = new MonitorEvent { Kind = MonitorEventKind.Write, ProcessId = Pid, ThreadId = 1, TimestampUtcTicks = at.Ticks, FileKey = key, ByteOffset = offset, IoSize = size, IoFlags = 0x60A00, Irp = 1 };
        r.OnParsed(w);
    }

    private static Counters Sum(IEnumerable<IndexRow> rows)
    {
        var c = new Counters();
        foreach (var r in rows)
        {
            c.Add(r.Counters);
        }

        return c;
    }

    [Fact]
    public void RotatesAtFirstHourBoundaryAfterLocalMidnight_GrowthContinues()
    {
        using var r = NewRecorder();
        Start(r);
        var day1 = new DateTime(2026, 10, 27, 22, 40, 0, DateTimeKind.Utc);
        Create(r, @"C:\work\a\one.bin", day1);
        var big = Create(r, @"C:\work\a\big.bin", day1.AddHours(1).AddMinutes(15)); // 23:55
        Write(r, big, 0, 4096, day1.AddHours(1).AddMinutes(16));

        _now = new DateTime(2026, 10, 27, 23, 58, 0, DateTimeKind.Utc);
        r.FlushIfDue(); // 23:58 还在同一天：不轮换
        Assert.Equal("T-start", r.Record!.Name);

        _now = new DateTime(2026, 10, 28, 0, 0, 30, DateTimeKind.Utc);
        Create(r, @"C:\work\b\new.bin", _now.AddSeconds(-10)); // 00:00:20，整点之后
        r.FlushIfDue(); // 整点后不到 60 秒：还不轮换
        Assert.Equal("T-start", r.Record!.Name);

        _now = new DateTime(2026, 10, 28, 0, 1, 30, DateTimeKind.Utc);
        Write(r, big, 4096, 8192, _now.AddSeconds(-5)); // 00:01:25：跨天接着写同一个文件
        Create(r, @"C:\work\b\later.bin", _now.AddSeconds(-1));
        r.FlushIfDue();
        Assert.Equal("T-20261028000000", r.Record!.Name);

        _now = new DateTime(2026, 10, 28, 0, 20, 0, DateTimeKind.Utc);
        Write(r, big, 12288, 4096, _now.AddMinutes(-1));
        r.End("service-stop", TimeSpan.FromSeconds(10));

        var store = r.Store!;
        var sessions = store.Sessions(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(2, sessions.Count);
        var (old, next) = (sessions[0], sessions[1]);
        Assert.Equal("rotate", old.EndReason);
        Assert.Equal(new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc), old.EndedUtc);
        Assert.Equal(old.EndedUtc, next.StartedUtc);
        Assert.Equal("service-stop", next.EndReason);

        var index = store.LoadIndex(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc));
        Assert.All(index.Rows.Where(x => x.SessionId == old.Id), x => Assert.True(x.HourUtc < next.StartedUtc));
        Assert.All(index.Rows.Where(x => x.SessionId == next.Id), x => Assert.True(x.HourUtc >= next.StartedUtc));

        var a = index.Rows.Where(x => x.Dir == @"C:\work\a").ToList();
        var b = index.Rows.Where(x => x.Dir == @"C:\work\b").ToList();
        Assert.Equal(2, Sum(a.Where(x => x.SessionId == old.Id)).Creates);
        Assert.Equal(2, Sum(b).Creates);
        Assert.All(b, x => Assert.Equal(next.Id, x.SessionId));

        // 估算增长：23:55 新建的文件跨天写到 16 KB，两行加起来就是文件的大小
        Assert.Equal(4096, Sum(a.Where(x => x.SessionId == old.Id)).Growth);
        Assert.Equal(16384, Sum(a).Growth);

        // 统计：两行的收到数加起来是送进去的事件总数（12 个，含进程启动）
        Assert.Equal(12, old.Stats.Received + next.Stats.Received);
    }

    [Fact]
    public void RotationWithRebuild_KeepsCountsAfterBoundary()
    {
        using var r = NewRecorder(rebuildDirectories: 1);
        Start(r);
        Create(r, @"C:\work\a\one.bin", new DateTime(2026, 10, 27, 23, 10, 0, DateTimeKind.Utc));
        _now = new DateTime(2026, 10, 28, 0, 1, 30, DateTimeKind.Utc);
        Create(r, @"C:\work\b\x.bin", new DateTime(2026, 10, 28, 0, 0, 40, DateTimeKind.Utc)); // 整点之后、轮换之前
        Create(r, @"C:\work\b\y.bin", new DateTime(2026, 10, 28, 0, 1, 0, DateTimeKind.Utc));
        Create(r, @"C:\work\b\z.bin", new DateTime(2026, 10, 28, 0, 1, 20, DateTimeKind.Utc)); // 还在重排缓冲区里
        r.FlushIfDue();
        Assert.Equal("T-20261028000000", r.Record!.Name);

        // 换了新的汇总器：进程表也是新的，要再送一次进程启动
        var p = E(MonitorEventKind.ProcessStart, _now);
        p.TargetProcessId = Pid;
        p.ParentProcessId = 4;
        p.CreateTimeUtcTicks = _now.AddHours(-2).Ticks;
        p.ImageName = D(@"C:\tools\uv.exe");
        r.OnParsed(p);
        Create(r, @"C:\work\b\w.bin", new DateTime(2026, 10, 28, 0, 5, 0, DateTimeKind.Utc));
        _now = new DateTime(2026, 10, 28, 0, 10, 0, DateTimeKind.Utc);
        r.Flush(final: false);
        r.Flush(final: false); // 再落盘一次：合并进来的计数不会被覆盖掉
        r.End("service-stop", TimeSpan.FromSeconds(10));

        var index = r.Store!.LoadIndex(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc));
        var b = index.Rows.Where(x => x.Dir == @"C:\work\b").ToList();
        Assert.Equal(4, Sum(b).Creates);
        Assert.Single(b.Select(x => x.HourUtc).Distinct());
    }

    [Fact]
    public void NoRotationWithoutRotateName()
    {
        var r = new Recorder(new RecorderOptions
        {
            DbPath = Path.Combine(_dir, "diag.db"),
            Mode = MonitorMode.Light,
            UtcNow = () => _now,
            ToLocal = u => u,
            Devices = new PrefixDeviceMap(new Dictionary<string, string> { [Dev] = "C:" }),
            Services = new NoServices(),
            SeedProcesses = false,
        });
        Start(r);
        _now = _now.AddDays(1);
        r.FlushIfDue();
        Assert.Equal("T-start", r.Record!.Name);
        r.End("timeout", TimeSpan.FromSeconds(10));
        r.Dispose();
    }

    private sealed class NoServices : IServiceNames
    {
        public IReadOnlyList<(string Name, string Display)> For(int pid) => [];
    }
}

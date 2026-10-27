using System.Text;
using System.Xml;

namespace DiskThinner.Monitor.Tests;

/// <summary>会话的生命周期（docs/phase2a-design.md 5.1 节）：互斥量、残留判定、截止检查、兜底任务的 XML。</summary>
public class SessionLifecycleTests
{
    private static string TestLock() => @"Local\DiskThinner.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task LockIsReleasedFromAnotherThreadAndCanBeTakenAgain()
    {
        var name = TestLock();
        var first = MonitorLock.TryAcquire(name);
        Assert.NotNull(first);
        Assert.True(MonitorLock.ExistsElsewhere(name));
        Assert.Null(MonitorLock.TryAcquire(name)); // 另一个实例拿不到

        // 在线程池线程上释放（到时停止发生在计时器线程上）：不抛异常
        await Task.Run(first!.Dispose);
        first.Dispose(); // 重复调用无害

        using var again = MonitorLock.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void LockThatNobodyHoldsDoesNotExist() => Assert.False(MonitorLock.ExistsElsewhere(TestLock()));

    private static OpenSessionRow Row(long id, string name) => new(id, name, DateTime.UtcNow);

    [Fact]
    public void NobodyMonitoringMeansEverythingIsLeftOver()
    {
        var r = SessionLiveness.Classify(false, false, ["DiskThinner-Diag-20261026100000", "Other-Session"], [Row(1, "DiskThinner-Diag-20261026100000"), Row(2, "DiskThinner-Diag-20261025090000")]);
        Assert.False(r.Live);
        Assert.Equal(["DiskThinner-Diag-20261026100000"], r.Residual);
        Assert.Equal([1L, 2L], r.Orphans.Select(o => o.Id));
    }

    [Fact]
    public void AnotherInstanceMonitoringOnlyMarksRowsWithoutASession()
    {
        var r = SessionLiveness.Classify(false, true, ["DiskThinner-Diag-20261026100000"], [Row(1, "DiskThinner-Diag-20261026100000"), Row(2, "DiskThinner-Diag-20261025090000")]);
        Assert.True(r.Live);
        Assert.Empty(r.Residual);
        Assert.Equal([2L], r.Orphans.Select(o => o.Id));

        // 列不出会话时不动
        Assert.Empty(SessionLiveness.Classify(false, true, null, [Row(2, "DiskThinner-Diag-20261025090000")]).Orphans);
    }

    [Fact]
    public void OwnSessionIsNeverAnOrphan()
    {
        var r = SessionLiveness.Classify(true, false, [], [Row(1, "DiskThinner-Diag-20261026100000")], selfSession: "DiskThinner-Diag-20261026100000");
        Assert.Empty(r.Orphans);
    }

    [Fact]
    public void DeadlineStopsByWallClockOrElapsedTime()
    {
        var start = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
        var now = start;
        var elapsed = TimeSpan.Zero;
        var d = new SessionDeadline(start, TimeSpan.FromHours(4), () => now, () => elapsed);
        Assert.False(d.ShouldStop());

        now = start.AddHours(30); // 睡了一夜：计时器没走，唤醒后按墙钟停
        Assert.True(d.ShouldStop());

        now = start.AddHours(-2); // 系统时间往回调：按经过时间停
        elapsed = TimeSpan.FromHours(4);
        Assert.True(d.ShouldStop());

        elapsed = TimeSpan.FromHours(1);
        Assert.Equal(TimeSpan.FromHours(3), d.Remaining);
    }

    [Fact]
    public void DurationIsLimitedTo24Hours()
    {
        var now = DateTime.UtcNow;
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionDeadline(now, TimeSpan.FromHours(25), () => now, () => TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SessionDeadline(now, TimeSpan.FromSeconds(5), () => now, () => TimeSpan.Zero));
        Assert.Matches(SessionLiveness.NamePattern, SessionDeadline.NewSessionName(now));
    }

    [Fact]
    public void WatchdogXmlHasEverySetting()
    {
        var at = new DateTimeOffset(2026, 10, 26, 18, 1, 0, TimeSpan.FromHours(8));
        var bytes = WatchdogTask.BuildXml("DiskThinner-Diag-20261026100000", at, TimeSpan.FromDays(1), @"C:\Windows\System32\logman.exe", "说明");
        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]); // UTF-16 LE 带 BOM
        var doc = new XmlDocument();
        doc.LoadXml(Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
        string V(string xpath) => doc.SelectSingleNode(xpath, ns)!.InnerText;
        Assert.Equal("2026-10-26T18:01:00+08:00", V("//t:StartBoundary"));
        Assert.Equal("2026-10-27T18:01:00+08:00", V("//t:EndBoundary"));
        Assert.Equal("S-1-5-18", V("//t:UserId"));
        Assert.Equal("false", V("//t:DisallowStartIfOnBatteries"));
        Assert.Equal("false", V("//t:StopIfGoingOnBatteries"));
        Assert.Equal("true", V("//t:StartWhenAvailable"));
        Assert.Equal("PT5M", V("//t:ExecutionTimeLimit"));
        Assert.Equal("PT0S", V("//t:DeleteExpiredTaskAfter"));
        Assert.Equal(@"C:\Windows\System32\logman.exe", V("//t:Command"));
        Assert.Equal("stop DiskThinner-Diag-20261026100000 -ets", V("//t:Arguments"));
        Assert.Throws<ArgumentException>(() => WatchdogTask.BuildXml("evil & stop all", at, TimeSpan.FromDays(1), "logman.exe", ""));
    }
}

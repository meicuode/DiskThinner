namespace DiskThinner.Monitor.Tests;

public class ReorderBufferTests
{
    private static MonitorEvent At(long ms) => new() { Kind = MonitorEventKind.Write, TimestampUtcTicks = ms * TimeSpan.TicksPerMillisecond };

    [Fact]
    public void EventsAreReleasedInTimeOrderAfterTheWindow()
    {
        var buffer = new ReorderBuffer(TimeSpan.FromSeconds(1));
        var released = new List<long>();
        void Release(MonitorEvent e) => released.Add(e.TimestampUtcTicks / TimeSpan.TicksPerMillisecond);
        buffer.Add(At(100), Release);
        buffer.Add(At(50), Release);   // 晚到的早事件
        buffer.Add(At(900), Release);
        Assert.Empty(released);
        buffer.Add(At(1200), Release); // 1200 − 1000 = 200：早于 200 的放行
        Assert.Equal([50L, 100L], released);
        buffer.Flush(Release);
        Assert.Equal([50L, 100L, 900L, 1200L], released);
    }

    [Fact]
    public void OverflowReleasesTheEarliest()
    {
        var buffer = new ReorderBuffer(TimeSpan.FromHours(1), maxEvents: 3);
        var released = new List<long>();
        foreach (var ms in new long[] { 5, 3, 4, 1 })
        {
            buffer.Add(At(ms), e => released.Add(e.TimestampUtcTicks / TimeSpan.TicksPerMillisecond));
        }

        Assert.Equal([1L], released);
        Assert.Equal(1, buffer.Overflow);
    }
}

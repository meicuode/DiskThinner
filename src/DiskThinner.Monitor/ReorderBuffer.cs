namespace DiskThinner.Monitor;

/// <summary>
/// 事件重排（docs/phase2a-design.md 第 12.2 节第 2 条）：实时会话按处理器的缓冲区交付，不保证按时间顺序。事件按时间戳进窗口，
/// 时间戳早于"已收到的最大时间戳 − W"的按时间顺序放行。窗口最多 <see cref="MaxEvents"/> 个，满了按到达顺序放行最早到的并计溢出。
/// 2a.1 的实测里没有出现倒序（V22），W 取 1 秒。
/// </summary>
public sealed class ReorderBuffer(TimeSpan window, int maxEvents = ReorderBuffer.MaxEvents)
{
    public const int MaxEvents = 100_000;

    private readonly long _window = window.Ticks;
    private readonly PriorityQueue<MonitorEvent, (long Ticks, long Seq)> _queue = new();
    private long _maxTicks;
    private long _seq;

    public long Overflow { get; private set; }

    public int Count => _queue.Count;

    /// <summary>放进一个事件（会被保存，调用方之后不要再改它），把可以放行的事件交给 <paramref name="release"/>。</summary>
    public void Add(MonitorEvent e, Action<MonitorEvent> release)
    {
        _queue.Enqueue(e, (e.TimestampUtcTicks, _seq++));
        _maxTicks = Math.Max(_maxTicks, e.TimestampUtcTicks);
        while (_queue.Count > maxEvents)
        {
            Overflow++;
            release(_queue.Dequeue());
        }

        var limit = _maxTicks - _window;
        while (_queue.TryPeek(out _, out var key) && key.Ticks < limit)
        {
            release(_queue.Dequeue());
        }
    }

    /// <summary>停止时清空窗口，全部按时间顺序放行。</summary>
    public void Flush(Action<MonitorEvent> release)
    {
        while (_queue.TryDequeue(out var e, out _))
        {
            release(e);
        }
    }
}

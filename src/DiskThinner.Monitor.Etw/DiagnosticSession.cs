using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace DiskThinner.Monitor.Etw;

public enum StopReason
{
    Timeout,
    User,
    AppExit,
    Error,
    StoreError,
    External,
}

public enum StartStatus
{
    Started,
    NotElevated,
    AlreadyRunning,
    Failed,
}

public sealed record StartResult(StartStatus Status, string? Message = null);

/// <summary>会话的参数。名字前缀和互斥量名可以注入，提权测试用和真实会话不同的（第 12.2 节第 19 条）。</summary>
public sealed class SessionOptions
{
    public required TimeSpan Duration { get; init; }
    public required MonitorMode Mode { get; init; }
    public string NamePrefix { get; init; } = "DiskThinner-Diag-";
    public string LockName { get; init; } = MonitorLock.DefaultName;

    /// <summary>注册兜底计划任务（D7）。测试里关掉。</summary>
    public bool Watchdog { get; init; } = true;

    /// <summary>每个（事件号, 版本）的前 200 个事件同时用 TDH 解码，比对解析器（--verify-parser，第 5.4 节）。</summary>
    public bool VerifyParser { get; init; }

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public int BufferSizeMb => Mode == MonitorMode.Precise ? 32 : 8;
}

/// <summary>
/// 一个 ETW 实时诊断会话（docs/phase2a-design.md 5.1 节，第 12.2 节第 1、17 条）：唯一命名；全局互斥量由专用线程持有；
/// 停止只执行一次，重复调用拿到同一个任务；每秒按墙钟和经过时间检查截止时间；被外部停止时如实标出。
/// 事件泵线程上只做"原始事件 → 回调"，汇总由调用方做。
/// </summary>
public sealed unsafe class DiagnosticSession : IDisposable
{
    private readonly SessionOptions _options;
    private readonly MonitorLock _lock;
    private readonly TraceEventSession _session;
    private readonly Thread _pump;
    private readonly Timer _timer;
    private readonly SessionDeadline _deadline;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<(int, int, int), int> _verified = [];
    private readonly Action<RawEvent> _onEvent;
    private int _stopping;
    private int _timerErrors;
    private long _lastLost;
    private bool _watchdogRegistered;

    private DiagnosticSession(SessionOptions options, MonitorLock monitorLock, string name, Action<RawEvent> onEvent)
    {
        _options = options;
        _lock = monitorLock;
        _onEvent = onEvent;
        Name = name;
        StartUtc = options.UtcNow();
        var sw = Stopwatch.StartNew();
        _deadline = new SessionDeadline(StartUtc, options.Duration, options.UtcNow, () => sw.Elapsed);
        _session = new TraceEventSession(name) { BufferSizeMB = options.BufferSizeMb, StopOnDispose = true };
        try
        {
            _session.EnableProvider(Providers.KernelFile, TraceEventLevel.Verbose, options.Mode == MonitorMode.Precise ? Providers.PreciseKeywords : Providers.LightKeywords);
            _session.EnableProvider(Providers.KernelProcess, TraceEventLevel.Verbose, Providers.ProcessKeyword);
            if (options.VerifyParser)
            {
                var registered = new RegisteredTraceEventParser(_session.Source);
                registered.All += OnTraceEvent;
            }
            else
            {
                _session.Source.AllEvents += OnTraceEvent;
            }
        }
        catch
        {
            _session.Dispose();
            throw;
        }

        _pump = new Thread(Pump) { IsBackground = true, Name = "DiskThinner.Monitor.Pump" };
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Name { get; }
    public DateTime StartUtc { get; }
    public DateTime PlannedEndUtc => _deadline.EndUtc;
    public TimeSpan Remaining => _deadline.Remaining;
    public MonitorMode Mode { get; private set; }

    /// <summary>内核侧丢的事件数（EventsLost，实时会话的实时值）。</summary>
    public long EventsLost => _session.EventsLost;

    public long ParseMismatch { get; private set; }
    public StopReason? StopReason { get; private set; }
    public bool WatchdogFailed { get; private set; }

    /// <summary>每秒一次（计时器线程上）：丢失数的增量。</summary>
    public Action<long>? LostDelta { get; init; }

    /// <summary>Stop 的"等泵线程结束"之后、"删兜底任务"之前，在调用 Stop 的线程上同步调用（MonitorRunner 在这里做最后一次落盘）。</summary>
    public Action<StopReason>? Stopping { get; set; }

    /// <summary>第一次 Stop 的全部步骤完成时完成。</summary>
    public Task Stopped => _stopped.Task;

    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>开始一个会话：拿锁、取名、建会话、启用两个提供程序、启动泵线程和每秒的计时器、注册兜底任务。</summary>
    public static (DiagnosticSession? Session, StartResult Result) Start(SessionOptions options, Action<RawEvent> onEvent, Action<DiagnosticSession>? beforePump = null)
    {
        if (!IsElevated())
        {
            return (null, new StartResult(StartStatus.NotElevated));
        }

        var monitorLock = MonitorLock.TryAcquire(options.LockName);
        if (monitorLock is null)
        {
            return (null, new StartResult(StartStatus.AlreadyRunning));
        }

        try
        {
            var name = UniqueName(options);
            var s = new DiagnosticSession(options, monitorLock, name, onEvent) { Mode = options.Mode };
            beforePump?.Invoke(s);
            s._pump.Start();
            s._timer.Change(1000, 1000);
            if (options.Watchdog && options.NamePrefix == "DiskThinner-Diag-")
            {
                var (ok, _) = WindowsSystem.RegisterWatchdog(name, new DateTimeOffset(s.PlannedEndUtc + WatchdogTask.Margin).ToLocalTime());
                s._watchdogRegistered = ok;
                s.WatchdogFailed = !ok;
            }

            return (s, new StartResult(StartStatus.Started));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            monitorLock.Dispose();
            return (null, new StartResult(StartStatus.Failed, ex.Message));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or IOException)
        {
            monitorLock.Dispose();
            return (null, new StartResult(StartStatus.Failed, ex.Message));
        }
    }

    private static string UniqueName(SessionOptions options)
    {
        var active = TraceEventSession.GetActiveSessionNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = options.UtcNow();
        for (var i = 0; i < 5; i++)
        {
            var name = options.NamePrefix + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (!active.Contains(name))
            {
                return name;
            }

            now = now.AddSeconds(1); // 同一秒内连续开始：取下一秒的名字，不让 Create 把已有的会话停掉重建
        }

        return options.NamePrefix + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnTraceEvent(TraceEvent e)
    {
        var raw = new RawEvent(e.ProviderGuid, (int)e.ID, e.Version, e.ProcessID, e.ThreadID, e.TimeStamp.ToUniversalTime().Ticks, e.PointerSize,
            new ReadOnlySpan<byte>((void*)e.DataStart, e.EventDataLength));
        if (_options.VerifyParser)
        {
            Verify(e, raw);
        }

        _onEvent(raw);
    }

    /// <summary>差分核对：每个（事件号, 版本）的前 200 个事件，按名字取 TDH 解码的字段，和手写解析器的结果比对。</summary>
    private void Verify(TraceEvent e, RawEvent raw)
    {
        var key = (e.ProviderGuid == Providers.KernelFile ? 0 : 1, (int)e.ID, e.Version);
        var n = _verified.GetValueOrDefault(key);
        if (n >= 200 || !PayloadParser.Supports(raw.Provider, raw.Id, raw.Version))
        {
            return;
        }

        _verified[key] = n + 1;
        var parsed = new MonitorEvent();
        if (!PayloadParser.TryParse(raw, parsed))
        {
            Mismatch(raw, "parse");
            return;
        }

        string? failed = null;
        bool Same(string field, object? ours)
        {
            var theirs = e.PayloadByName(field);
            if (theirs is null)
            {
                return true; // 这个版本没有这个字段
            }

            var same = theirs switch
            {
                string s => string.Equals(s, ours as string, StringComparison.Ordinal),
                DateTime d => ours is long ticks && Math.Abs(d.ToUniversalTime().Ticks - ticks) < TimeSpan.TicksPerMillisecond,
                _ => Bits(theirs) is { } t && t == Bits(ours),
            };
            if (!same)
            {
                failed ??= field;
            }

            return same;
        }

        bool ok;
        try
        {
            ok = Compare(parsed, Same);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException or ArgumentException or IndexOutOfRangeException)
        {
            // 核对只是诊断：TDH 解码出错也只记一次不一致，不能让泵线程退出
            ok = false;
            failed ??= ex.GetType().Name;
        }

        if (!ok)
        {
            Mismatch(raw, failed ?? "?");
        }
    }

    /// <summary>整数字段按位比较：TDH 可能把 UInt64 的哨兵偏移（0xFFFFFFFFFFFFFFFF）解码成有符号的 -1，按同样宽度的无符号数换算。</summary>
    private static ulong? Bits(object? value) => value switch
    {
        null => null,
        ulong u => u,
        long l => unchecked((ulong)l),
        uint u => u,
        int i => unchecked((uint)i),
        ushort u => u,
        short s => unchecked((ushort)s),
        byte b => b,
        sbyte s => unchecked((byte)s),
        bool b => b ? 1UL : 0UL,
        Enum en => Convert.ToUInt64(en, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    private void Mismatch(RawEvent raw, string field)
    {
        ParseMismatch++;
        if (MismatchDetail is null)
        {
            var provider = raw.Provider == Providers.KernelFile ? "Kernel-File" : "Kernel-Process";
            MismatchDetail = $"{provider}/{raw.Id}/v{raw.Version}:{field}";
        }
    }

    /// <summary>第一处不一致（提供程序/事件号/版本:字段），给 monitor 子命令的报告用。</summary>
    public string? MismatchDetail { get; private set; }

    private static bool Compare(MonitorEvent parsed, Func<string, object?, bool> same) => parsed.Kind switch
    {
        MonitorEventKind.NameCreate or MonitorEventKind.NameDelete => same("FileKey", parsed.FileKey) && same("FileName", parsed.Path),
        MonitorEventKind.Write => same("FileKey", parsed.FileKey) && same("ByteOffset", parsed.ByteOffset) && same("IOSize", parsed.IoSize) && same("IOFlags", parsed.IoFlags),
        MonitorEventKind.CreateNewFile => same("CreateOptions", parsed.CreateOptions) && same("FileName", parsed.Path),
        MonitorEventKind.DeletePath or MonitorEventKind.RenamePath or MonitorEventKind.SetLinkPath => same("FileKey", parsed.FileKey) && same("FilePath", parsed.Path),
        MonitorEventKind.ProcessStart or MonitorEventKind.ProcessRundown => same("ProcessID", (uint)parsed.TargetProcessId) && same("ParentProcessID", (uint)parsed.ParentProcessId)
            && same("ImageName", parsed.ImageName) && same("ProcessSequenceNumber", parsed.Sequence),
        MonitorEventKind.ProcessStop => same("ProcessID", (uint)parsed.TargetProcessId) && same("ImageName", parsed.ImageName),
        _ => true,
    };

    private void Pump()
    {
        try
        {
            _session.Source.Process();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PumpError = ex;
            _ = Stop(Etw.StopReason.Error);
            return;
        }

        // 正常返回、但不是我们停的：有人运行了 logman stop，或者兜底任务先触发了
        if (Volatile.Read(ref _stopping) == 0)
        {
            _ = Stop(Etw.StopReason.External);
        }
    }

    /// <summary>泵线程因为异常退出时的异常（会话随后以 Error 停止）。</summary>
    public Exception? PumpError { get; private set; }

    /// <summary>每秒检查的最近一次异常（连续 3 次时会话以 Error 停止）。</summary>
    public Exception? TimerError { get; private set; }

    private void Tick()
    {
        try
        {
            var lost = _session.EventsLost;
            if (lost != _lastLost)
            {
                LostDelta?.Invoke(lost - _lastLost);
                _lastLost = lost;
            }

            if (_deadline.ShouldStop())
            {
                _ = Stop(Etw.StopReason.Timeout);
            }

            _timerErrors = 0;
        }
        catch (Exception ex)
        {
            // 计时器回调里的异常不能抛到线程池（会让进程退出）；连续 3 次出错时停止
            TimerError = ex;
            if (++_timerErrors >= 3)
            {
                _ = Stop(Etw.StopReason.Error);
            }
        }
    }

    /// <summary>精确模式降级为轻量（D9）：重新启用 Kernel-File，只开轻量的关键字（V17 确认立即生效）。</summary>
    public void Downgrade()
    {
        if (Mode == MonitorMode.Light)
        {
            return;
        }

        _session.EnableProvider(Providers.KernelFile, TraceEventLevel.Verbose, Providers.LightKeywords);
        Mode = MonitorMode.Light;
    }

    /// <summary>
    /// 停止：只执行一次，第二次及以后的调用返回同一个 Task。顺序：停 ETW 会话 → 等泵线程结束（当前线程就是泵线程时不等）
    /// → Stopping 回调（最后一次落盘）→ 删兜底任务 → 释放互斥量。每一步单独捕获异常，锁在 finally 里释放。
    /// </summary>
    public Task Stop(StopReason reason)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return _stopped.Task;
        }

        StopReason = reason;
        try
        {
            try
            {
                _timer.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _session.Stop(noThrow: true);
                _session.Dispose();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or InvalidOperationException)
            {
                StopError = ex.Message;
            }

            if (Thread.CurrentThread != _pump && _pump.IsAlive)
            {
                _pump.Join(TimeSpan.FromSeconds(5));
            }

            try
            {
                Stopping?.Invoke(reason);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            {
                StopError = ex.Message;
            }

            if (_watchdogRegistered)
            {
                WindowsSystem.DeleteWatchdog();
            }
        }
        finally
        {
            _lock.Dispose();
            _stopped.TrySetResult();
        }

        return _stopped.Task;
    }

    public string? StopError { get; private set; }

    /// <summary>
    /// 未处理异常、进程退出时：只按名字停会话，不落盘、不加锁、吞掉异常（第 5.1.3 节）。会话已经停了时什么都不做。
    /// </summary>
    public void EmergencyStop()
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        try
        {
            using var attached = new TraceEventSession(Name, TraceEventSessionOptions.Attach);
            attached.Stop(noThrow: true);
        }
        catch (Exception)
        {
            // 退出路径上，什么都不能抛
        }
    }

    public void Dispose() => Stop(Etw.StopReason.AppExit).Wait(TimeSpan.FromSeconds(10));

    /// <summary>
    /// 按名字停掉残留的会话（管理员）。只停名字符合正则的（默认 <see cref="SessionLiveness.NamePattern"/>）。返回停掉的个数。
    /// </summary>
    public static int StopResidual(IEnumerable<string> names, System.Text.RegularExpressions.Regex? namePattern = null)
    {
        var regex = namePattern ?? SessionLiveness.NamePattern;
        var n = 0;
        foreach (var name in names.Where(n => regex.IsMatch(n)))
        {
            try
            {
                using var s = new TraceEventSession(name, TraceEventSessionOptions.Attach);
                if (s.Stop(noThrow: true))
                {
                    n++;
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or FileNotFoundException)
            {
            }
        }

        return n;
    }

    /// <summary>活动会话里所有名字；列不出来时为 null（V21）。</summary>
    public static IReadOnlyList<string>? ActiveSessionNames()
    {
        try
        {
            return TraceEventSession.GetActiveSessionNames();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

using System.Diagnostics;

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

/// <summary>会话的参数。名字前缀和互斥量名可以注入，提权测试用和真实会话不同的（docs/phase2a-design.md 第 12.2 节第 19 条）。</summary>
public sealed class SessionOptions
{
    public required TimeSpan Duration { get; init; }
    public required MonitorMode Mode { get; init; }
    public string NamePrefix { get; init; } = "DiskThinner-Diag-";
    public string LockName { get; init; } = MonitorLock.DefaultName;

    /// <summary>注册兜底计划任务（D7）。测试里关掉。</summary>
    public bool Watchdog { get; init; } = true;

    /// <summary>每个（事件号, 版本）的前 200 个事件同时用 TDH 按名字取字段，比对解析器（--verify-parser）。</summary>
    public bool VerifyParser { get; init; }

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public int BufferSizeMb => Mode == MonitorMode.Precise ? 32 : 8;
}

/// <summary>
/// 一个 ETW 实时诊断会话（docs/phase2a-design.md 5.1 节，第 12.2 节第 1、17 条）：唯一命名；全局互斥量由专用线程持有；
/// 停止只执行一次，重复调用拿到同一个任务；每秒按墙钟和经过时间检查截止时间；被外部停止时如实标出。
/// 事件泵线程上只做"原始事件 → 回调"，汇总由调用方做。2b 起直接调用 ETW API（<see cref="NativeSession"/>），不再用 TraceEvent。
/// </summary>
public sealed class DiagnosticSession : IDisposable
{
    private readonly SessionOptions _options;
    private readonly MonitorLock _lock;
    private readonly NativeSession _session;
    private readonly Timer _timer;
    private readonly SessionDeadline _deadline;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TdhVerifier? _check;
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
        _check = options.VerifyParser ? new TdhVerifier() : null;
        Name = name;
        StartUtc = options.UtcNow();
        var sw = Stopwatch.StartNew();
        _deadline = new SessionDeadline(StartUtc, options.Duration, options.UtcNow, () => sw.Elapsed);
        _session = NativeSession.Start(name, options.BufferSizeMb,
        [
            (Providers.KernelFile, options.Mode == MonitorMode.Precise ? Providers.PreciseKeywords : Providers.LightKeywords),
            (Providers.KernelProcess, Providers.ProcessKeyword),
        ], OnRecord);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        _session.Completed.ContinueWith(t => OnPumpExit(t.Result), TaskScheduler.Default);
    }

    public string Name { get; }
    public DateTime StartUtc { get; }
    public DateTime PlannedEndUtc => _deadline.EndUtc;
    public TimeSpan Remaining => _deadline.Remaining;
    public MonitorMode Mode { get; private set; }

    /// <summary>内核侧丢的事件数。停止之后和查询失败时返回最后一次查到的值，不抛异常。</summary>
    public long EventsLost => _session.EventsLost;

    public long ParseMismatch => _check?.Mismatches ?? 0;

    /// <summary>至少核对上一个字段的事件数（--verify-parser；为 0 说明 TDH 没读到清单，核对是空的）。</summary>
    public long ParseVerified => _check?.Verified ?? 0;

    /// <summary>第一处不一致（提供程序/事件号/版本:字段），给 monitor 子命令的报告用。</summary>
    public string? MismatchDetail => _check?.FirstMismatch;

    public StopReason? StopReason { get; private set; }
    public bool WatchdogFailed { get; private set; }

    /// <summary>每秒一次（计时器线程上）：丢失数的增量。</summary>
    public Action<long>? LostDelta { get; init; }

    /// <summary>Stop 的"等泵线程结束"之后、"删兜底任务"之前，在调用 Stop 的线程上同步调用（MonitorRunner 在这里做最后一次落盘）。</summary>
    public Action<StopReason>? Stopping { get; set; }

    /// <summary>第一次 Stop 的全部步骤完成时完成。</summary>
    public Task Stopped => _stopped.Task;

    /// <summary>泵线程的回调抛出的异常（会话随后以 Error 停止）。</summary>
    public Exception? PumpError => _session.CallbackError;

    /// <summary>每秒检查的最近一次异常（连续 3 次时会话以 Error 停止）。</summary>
    public Exception? TimerError { get; private set; }

    public string? StopError { get; private set; }

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

        DiagnosticSession? s = null;
        try
        {
            var name = UniqueName(options);
            s = new DiagnosticSession(options, monitorLock, name, onEvent) { Mode = options.Mode };
            beforePump?.Invoke(s);
            s._session.StartPump("DiskThinner.Monitor.Pump");
            s._timer.Change(1000, 1000);
            if (options.Watchdog && options.NamePrefix == "DiskThinner-Diag-")
            {
                var (ok, _) = WindowsSystem.RegisterWatchdog(name, new DateTimeOffset(s.PlannedEndUtc + WatchdogTask.Margin).ToLocalTime());
                s._watchdogRegistered = ok;
                s.WatchdogFailed = !ok;
            }

            return (s, new StartResult(StartStatus.Started));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException or InvalidOperationException or ArgumentException or IOException)
        {
            // 会话已经建好（beforePump 里出错）时停掉它；还没建好时只释放锁
            if (s is not null)
            {
                s._session.Stop(TimeSpan.FromSeconds(5));
                s._timer.Dispose();
            }

            monitorLock.Dispose();
            return (null, new StartResult(StartStatus.Failed, ex.Message));
        }
    }

    private static string UniqueName(SessionOptions options)
    {
        var active = (ActiveSessionNames() ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = options.UtcNow();
        for (var i = 0; i < 5; i++)
        {
            var name = options.NamePrefix + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (!active.Contains(name))
            {
                return name;
            }

            now = now.AddSeconds(1); // 同一秒内连续开始：取下一秒的名字，不碰已有的会话
        }

        return options.NamePrefix + now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnRecord(in RawEvent raw, EtwRecord record)
    {
        if (_check is not null)
        {
            try
            {
                _check.Check(raw, record);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
            {
                // 核对只是诊断，出错不能让泵线程退出
            }
        }

        _onEvent(raw);
    }

    /// <summary>ProcessTrace 返回了：回调出错时以 Error 停止；不是我们停的（logman stop、兜底任务先触发）时以 External 停止。</summary>
    private void OnPumpExit(uint rc)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        _ = Stop(_session.CallbackError is not null ? Etw.StopReason.Error : Etw.StopReason.External);
    }

    private void Tick()
    {
        try
        {
            var lost = EventsLost;
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

        _session.Enable(Providers.KernelFile, Providers.LightKeywords);
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

            if (!_session.Stop(TimeSpan.FromSeconds(5)))
            {
                StopError = "pump did not exit";
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
            NativeSession.StopByName(Name);
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
        return names.Where(n => regex.IsMatch(n)).Count(NativeSession.StopByName);
    }

    /// <summary>活动会话里所有名字；列不出来时为 null（V21）。</summary>
    public static IReadOnlyList<string>? ActiveSessionNames() => NativeSession.ActiveSessionNames();
}

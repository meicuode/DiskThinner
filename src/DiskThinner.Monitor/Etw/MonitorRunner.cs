namespace DiskThinner.Monitor.Etw;

/// <summary>合并用的锚点：最近一次快照里记录的目录（第 5.7 节第 1 条、第 12.2 节第 12 条）。快照库不存在时只返回盘根，不建库。</summary>
public interface IAnchorSource
{
    IReadOnlyCollection<string> Load();
}

public enum MonitorState
{
    Idle,
    Running,
    Stopped,
}

/// <summary>界面和 monitor 子命令看的状态快照。</summary>
public sealed record MonitorStatus(
    MonitorState State, MonitorMode Mode, string? SessionName, DateTime? StartedUtc, DateTime? PlannedEndUtc, TimeSpan Remaining,
    long Received, long Lost, int SampleFactor, double CpuPercent, long WorkingSetBytes, string? LastError, StopReason? EndReason, bool Downgraded)
{
    public static readonly MonitorStatus Idle = new(MonitorState.Idle, MonitorMode.Precise, null, null, null, TimeSpan.Zero, 0, 0, 1, 0, 0, null, null, false);

    public double LostRate => Received + Lost == 0 ? 0 : (double)Lost / (Received + Lost);
}

public sealed class RunnerOptions
{
    public required string DbPath { get; init; }
    public required SessionOptions Session { get; init; }
    public IAnchorSource? Anchors { get; init; }

    /// <summary>映像的产品名和公司名（落盘线程上调用）。</summary>
    public Func<string, (string? Product, string? Company)>? Identity { get; init; }

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>停止时按这个天数和 64 MB 的上限删掉最早的会话（D5，设置里的"保留"）。</summary>
    public int KeepDays { get; init; } = 90;

    /// <summary>日志（不记路径）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 诊断会话 + 记录 + 状态（docs/phase2a-design.md 5.9.1 节，第 12.2 节第 1 条）。界面和 monitor 子命令共用。
/// 汇总、落盘、降级在 <see cref="Recorder"/> 里（2b 拆出来和服务共用）；这里只管诊断会话的开始、截止时间和停止。
/// </summary>
public sealed class MonitorRunner : IDisposable
{
    private readonly RunnerOptions _options;
    private readonly Recorder _recorder;
    private DiagnosticSession? _session;

    public MonitorRunner(RunnerOptions options)
    {
        _options = options;
        _recorder = new Recorder(new RecorderOptions
        {
            DbPath = options.DbPath,
            Mode = options.Session.Mode,
            Anchors = options.Anchors,
            Identity = options.Identity,
            FlushInterval = options.FlushInterval,
            KeepDays = options.KeepDays,
            MaxBytes = 64L << 20,
            Log = options.Log,
        });
        _recorder.Flushed += (_, _) => Flushed?.Invoke(this, EventArgs.Empty);
    }

    public MonitorStatus Status { get; private set; } = MonitorStatus.Idle;

    public Task Stopped => _session?.Stopped ?? Task.CompletedTask;

    public string? SessionName => _session?.Name;

    public long ParseMismatch => _session?.ParseMismatch ?? 0;

    /// <summary>第一处解析不一致（--verify-parser）。</summary>
    public string? MismatchDetail => _session?.MismatchDetail;

    /// <summary>核对过至少一个字段的事件数（--verify-parser）。为 0 时核对是空的（TDH 没读到清单）。</summary>
    public long ParseVerified => _session?.ParseVerified ?? 0;

    /// <summary>会话因为异常停止时的异常。</summary>
    public Exception? Failure => _session?.PumpError ?? _session?.TimerError;

    /// <summary>每个（提供程序/事件号/版本）收到的个数（monitor 子命令的 events_by_id，V5）。</summary>
    public IReadOnlyDictionary<string, long> EventsById => _recorder.EventsById;

    /// <summary>会话或落盘结束后调用（任何线程上）。界面用 System.Windows.Forms.Timer 轮询 <see cref="Status"/>，不直接订阅它。</summary>
    public event EventHandler? Flushed;

    public StartResult Start()
    {
        if (_recorder.Open() is { } openError)
        {
            return new StartResult(StartStatus.Failed, openError);
        }

        string? beginError = null;
        var (session, result) = DiagnosticSession.Start(_options.Session, _recorder.OnEvent, s =>
        {
            // 会话已建、提供程序已启用：立即插入会话行，再列举现有进程
            try
            {
                _recorder.Begin(new SessionRecord { Name = s.Name, StartedUtc = s.StartUtc, PlannedEndUtc = s.PlannedEndUtc, Mode = s.Mode });
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                beginError = ex.Message;
                throw new InvalidOperationException(ex.Message, ex);
            }
        });
        if (session is null)
        {
            return beginError is null ? result : new StartResult(StartStatus.Failed, beginError);
        }

        _session = session;
        _session.Stopping = OnStopping;
        _recorder.ExtraFlags = session.WatchdogFailed ? SessionFlags.WatchdogFailed : SessionFlags.None;
        _recorder.StoreFailed = () => _ = Stop(StopReason.StoreError);
        _recorder.Ticked = UpdateStatus;
        _recorder.Run(() => session.EventsLost, session.Downgrade);
        _options.Log?.Invoke($"monitor started {session.Name} mode={session.Mode} watchdog_failed={session.WatchdogFailed}");
        UpdateStatus();
        return result;
    }

    /// <summary>监控期间保存了新快照时重新读锚点。</summary>
    public void ReloadAnchors() => _recorder.ReloadAnchors();

    public Task Stop(StopReason reason) => _session?.Stop(reason) ?? Task.CompletedTask;

    public void EmergencyStop() => _session?.EmergencyStop();

    /// <summary>落盘（定时落盘之外，测试用来制造并发落盘）。</summary>
    public void Flush(bool final) => _recorder.Flush(final);

    private void UpdateStatus()
    {
        var s = _session;
        if (s is null)
        {
            return;
        }

        var (received, lost, factor) = _recorder.Counts;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var state = s.StopReason is null ? MonitorState.Running : MonitorState.Stopped;
        Status = new MonitorStatus(state, s.Mode, s.Name, s.StartUtc, s.PlannedEndUtc, s.Remaining, received, lost, factor, _recorder.CpuPercent, self.WorkingSet64,
            _recorder.LastError ?? s.StopError, s.StopReason, _recorder.Downgraded);
    }

    /// <summary>最后一次落盘（Stop 的回调，在调用 Stop 的线程上）：先停定时落盘并等它的回调退出，整体最多等 3 秒。</summary>
    private void OnStopping(StopReason reason)
    {
        _recorder.StopTimers();
        if (reason == StopReason.Error && (_session?.PumpError ?? _session?.TimerError) is { } failure)
        {
            // 状态和日志只记类型、消息和出错的方法（不记路径），完整异常给测试和报告用
            _recorder.LastError = failure.GetType().Name + ": " + failure.Message;
            var frame = failure.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            var inFile = frame.IndexOf(" in ", StringComparison.Ordinal);
            _options.Log?.Invoke("monitor stopped on error: " + failure.GetType().Name + " " + (inFile > 0 ? frame[..inFile] : frame));
        }

        _recorder.End(EndReasonText(reason), TimeSpan.FromSeconds(3));
        UpdateStatus();
        _options.Log?.Invoke($"monitor stopped {_recorder.Record?.Name} reason={_recorder.Record?.EndReason} received={Status.Received} lost={Status.Lost}");
        Flushed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>会话表里的结束原因。</summary>
    public static string EndReasonText(StopReason reason) => reason switch
    {
        StopReason.Timeout => "timeout",
        StopReason.User => "user",
        StopReason.AppExit => "app-exit",
        StopReason.StoreError => "store-error",
        StopReason.External => "external",
        _ => "error",
    };

    public void Dispose()
    {
        if (_session is { StopReason: null })
        {
            _session.Stop(StopReason.AppExit).Wait(TimeSpan.FromSeconds(10));
        }

        _recorder.Dispose();
    }
}

/// <summary>
/// 残留会话和未结束的会话记录的清理（第 5.1.2 节）：任何权限都标孤儿行；管理员下拿到锁才停残留会话、删残留的兜底任务。
/// 互斥量名和会话名的正则可以注入（提权测试用自己的，第 12.2 节第 19 条）；注入了正则时不碰兜底任务。
/// </summary>
public static class MonitorCleanup
{
    public sealed record Result(bool Live, int ResidualFound, int ResidualStopped, int OrphansMarked, bool TaskDeleted, bool ActiveListed);

    public static Result Run(string dbPath, string lockName = MonitorLock.DefaultName, string? selfSession = null, System.Text.RegularExpressions.Regex? namePattern = null)
    {
        var active = DiagnosticSession.ActiveSessionNames();
        var live = selfSession is not null || MonitorLock.ExistsElsewhere(lockName);
        MonitorStore? store = null;
        try
        {
            store = MonitorStore.OpenExisting(dbPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
        }

        var open = store?.OpenSessions() ?? [];
        var verdict = SessionLiveness.Classify(selfSession is not null, live && selfSession is null, active, open, selfSession, namePattern);
        var marked = store?.MarkCrashed(verdict.Orphans) ?? 0;
        var stopped = 0;
        var taskDeleted = false;

        // 兜底任务已经触发、停掉了会话时，残留的只有任务本身（过期一天后才自己删掉）：没有实例在监控时一起删
        var staleTask = namePattern is null && !verdict.Live && DiagnosticSession.IsElevated() && WindowsSystem.WatchdogExists();
        if ((verdict.Residual.Count > 0 || staleTask) && DiagnosticSession.IsElevated())
        {
            using var l = MonitorLock.TryAcquire(lockName);
            if (l is not null)
            {
                stopped = verdict.Residual.Count > 0 ? DiagnosticSession.StopResidual(verdict.Residual, namePattern) : 0;
                taskDeleted = namePattern is null && WindowsSystem.WatchdogExists() && WindowsSystem.DeleteWatchdog().Ok;
            }
        }

        return new Result(verdict.Live, verdict.Residual.Count, stopped, marked, taskDeleted, active is not null);
    }
}

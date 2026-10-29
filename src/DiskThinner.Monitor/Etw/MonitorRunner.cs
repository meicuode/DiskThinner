using System.Diagnostics;

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
/// 会话 + 汇总 + 定时落盘 + 状态（docs/phase2a-design.md 5.9.1 节，第 12.2 节第 1 条）。界面和 monitor 子命令共用，落盘和停止的逻辑只有这一份。
/// 泵线程每个事件拿一次 <see cref="_gate"/>；落盘线程拿锁后合并、复制出要写的行，写 SQLite 在锁外；所有落盘共用一个信号量，不会交错。
/// </summary>
public sealed class MonitorRunner : IDisposable
{
    private readonly RunnerOptions _options;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ReorderBuffer _reorder = new(TimeSpan.FromSeconds(1));
    private MonitorStore? _store;
    private MonitorAggregator? _aggregator;
    private WindowsSystem.Services? _services;
    private DiagnosticSession? _session;
    private SessionRecord? _record;
    private Timer? _statusTimer;
    private Timer? _flushTimer;
    private HashSet<int> _anchors = [];
    private IReadOnlyCollection<string> _anchorPaths = [];
    private DateTime _lastFlushUtc;
    private int _flushFailures;
    private string? _lastError;
    private long _lastLost;
    private readonly Queue<(long Received, long Lost)> _window = new();
    private TimeSpan _cpuStart;
    private readonly Stopwatch _wall = new();
    private long _peakWs;
    private long _peakEventsPerSecond;
    private long _lastReceived;
    private bool _downgraded;
    private int _foldFlags;

    public MonitorRunner(RunnerOptions options) => _options = options;

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
    public IReadOnlyDictionary<string, long> EventsById
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, long>(_byId);
            }
        }
    }

    private readonly Dictionary<string, long> _byId = new(StringComparer.Ordinal);

    /// <summary>会话或落盘结束后调用（任何线程上）。界面用 System.Windows.Forms.Timer 轮询 <see cref="Status"/>，不直接订阅它。</summary>
    public event EventHandler? Flushed;

    public StartResult Start()
    {
        try
        {
            _store = MonitorStore.Open(_options.DbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new StartResult(StartStatus.Failed, ex.Message);
        }

        _services = new WindowsSystem.Services();
        _aggregator = new MonitorAggregator(_options.Session.Mode, WindowsSystem.DeviceMap(), _services, Environment.ProcessId);
        ReloadAnchors();
        string? beginError = null;
        var (session, result) = DiagnosticSession.Start(_options.Session, OnEvent, s =>
        {
            // 会话已建、提供程序已启用：立即插入会话行（刚开始就被强制结束也能在清理时找到），再列举现有进程
            _record = new SessionRecord { Name = s.Name, StartedUtc = s.StartUtc, PlannedEndUtc = s.PlannedEndUtc, Mode = s.Mode };
            try
            {
                _store.BeginSession(_record);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                beginError = ex.Message;
                throw new InvalidOperationException(ex.Message, ex);
            }

            foreach (var (pid, ppid, create, image) in WindowsSystem.Processes())
            {
                _aggregator.Seed(pid, create, ppid, image);
            }
        });
        if (session is null)
        {
            return beginError is null ? result : new StartResult(StartStatus.Failed, beginError);
        }

        _session = session;
        _session.Stopping = OnStopping;
        _lastFlushUtc = session.StartUtc;
        _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        _wall.Start();
        _statusTimer = new Timer(_ => SafeTick(), null, 1000, 1000);
        _flushTimer = new Timer(_ => SafeFlush(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _options.Log?.Invoke($"monitor started {session.Name} mode={session.Mode} watchdog_failed={session.WatchdogFailed}");
        UpdateStatus();
        return result;
    }

    /// <summary>监控期间保存了新快照时重新读锚点。</summary>
    public void ReloadAnchors()
    {
        _anchorPaths = _options.Anchors?.Load() ?? [];
        if (_aggregator is null)
        {
            return;
        }

        lock (_gate)
        {
            var set = new HashSet<int>();
            foreach (var root in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            {
                set.Add(_aggregator.Directories.GetOrAdd(root.Name));
            }

            foreach (var p in _anchorPaths)
            {
                var id = _aggregator.Directories.GetOrAdd(p);
                if (id > 0)
                {
                    set.Add(id);
                }
            }

            _anchors = set;
        }
    }

    public Task Stop(StopReason reason) => _session?.Stop(reason) ?? Task.CompletedTask;

    public void EmergencyStop() => _session?.EmergencyStop();

    private void OnEvent(RawEvent raw)
    {
        var e = new MonitorEvent();
        var ok = PayloadParser.TryParse(raw, e);
        lock (_gate)
        {
            var key = $"{(raw.Provider == Providers.KernelFile ? "Kernel-File" : "Kernel-Process")}/{raw.Id}/v{raw.Version}";
            _byId[key] = _byId.GetValueOrDefault(key) + 1;
            if (!ok)
            {
                _aggregator!.Stats.ParseErrors++;
                return;
            }

            _reorder.Add(e, _aggregator!.Process);
        }
    }

    /// <summary>每秒的状态刷新（线程池计时器）：异常不能抛到线程池（会让进程退出），整体捕获、记下最后一个（第 5.1.3 节）。</summary>
    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
    }

    private void Tick()
    {
        var session = _session;
        if (session is null)
        {
            return;
        }

        var lost = session.EventsLost;
        long received;
        lock (_gate)
        {
            var delta = lost - _lastLost;
            _lastLost = lost;
            _aggregator!.Stats.Lost += delta;
            received = _aggregator.Stats.Received;
            _window.Enqueue((received, lost));
            while (_window.Count > 60)
            {
                _window.Dequeue();
            }

            // D9：抽样已经到 64 倍、最近 60 秒丢失率仍 > 5% 时降到轻量
            var (r0, l0) = _window.Peek();
            var dr = received - r0;
            var dl = lost - l0;
            if (!_downgraded && _aggregator.Mode == MonitorMode.Precise && _aggregator.SampleFactor >= 64 && dr + dl > 0 && (double)dl / (dr + dl) > 0.05)
            {
                session.Downgrade();
                _aggregator.Mode = MonitorMode.Light;
                _aggregator.MarkDowngraded();
                _downgraded = true;
                _record!.DowngradedUtc = DateTime.UtcNow;
                _record.Mode = MonitorMode.Light;
                _options.Log?.Invoke("monitor downgraded to light");
            }
        }

        _peakEventsPerSecond = Math.Max(_peakEventsPerSecond, received - _lastReceived);
        _lastReceived = received;
        using var self = Process.GetCurrentProcess();
        _peakWs = Math.Max(_peakWs, self.WorkingSet64);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var s = _session;
        if (s is null)
        {
            return;
        }

        long received, lost;
        int factor;
        lock (_gate)
        {
            received = _aggregator!.Stats.Received;
            lost = _aggregator.Stats.Lost;
            factor = _aggregator.SampleFactor;
        }

        using var self = Process.GetCurrentProcess();
        var cpu = _wall.Elapsed.TotalMilliseconds <= 0 ? 0 : (self.TotalProcessorTime - _cpuStart).TotalMilliseconds / _wall.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        var state = s.StopReason is null ? MonitorState.Running : MonitorState.Stopped;
        Status = new MonitorStatus(state, s.Mode, s.Name, s.StartUtc, s.PlannedEndUtc, s.Remaining, received, lost, factor, cpu, self.WorkingSet64, _lastError ?? s.StopError,
            s.StopReason, _downgraded);
    }

    private void SafeFlush()
    {
        try
        {
            var now = DateTime.UtcNow;
            var dueByInterval = now - _lastFlushUtc >= _options.FlushInterval;
            var dueByHour = now.Hour != _lastFlushUtc.Hour && now.Minute >= 1;
            if (dueByInterval || dueByHour)
            {
                Flush(final: false);
            }
        }
        catch (Exception ex)
        {
            // 定时落盘也在线程池计时器上：同样整体捕获（写库失败本身在 Flush 里按"连续 3 次就停"处理）
            _lastError = ex.Message;
            _options.Log?.Invoke("monitor timed flush failed: " + ex.GetType().Name);
        }
    }

    /// <summary>最后一次落盘（Stop 的回调，在调用 Stop 的线程上）：先停定时落盘并等它的回调退出，整体最多等 3 秒。</summary>
    private void OnStopping(StopReason reason)
    {
        using (var done = new ManualResetEvent(false))
        {
            if (_flushTimer?.Dispose(done) == true)
            {
                done.WaitOne(TimeSpan.FromSeconds(3));
            }
        }

        _statusTimer?.Dispose();
        if (reason == StopReason.Error && (_session?.PumpError ?? _session?.TimerError) is { } failure)
        {
            // 状态和日志只记类型、消息和出错的方法（不记路径），完整异常给测试和报告用
            _lastError = failure.GetType().Name + ": " + failure.Message;
            var frame = failure.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            var inFile = frame.IndexOf(" in ", StringComparison.Ordinal);
            _options.Log?.Invoke("monitor stopped on error: " + failure.GetType().Name + " " + (inFile > 0 ? frame[..inFile] : frame));
        }

        _record!.EndedUtc = DateTime.UtcNow;
        _record.EndReason = reason switch
        {
            StopReason.Timeout => "timeout",
            StopReason.User => "user",
            StopReason.AppExit => "app-exit",
            StopReason.StoreError => "store-error",
            StopReason.External => "external",
            _ => "error",
        };
        var final = Task.Run(() => Flush(final: true));
        if (!final.Wait(TimeSpan.FromSeconds(3)))
        {
            _options.Log?.Invoke("monitor final flush timed out");
        }

        try
        {
            _store!.Prune(DateTime.UtcNow, TimeSpan.FromDays(Math.Clamp(_options.KeepDays, 1, 3650)), 64L << 20);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _options.Log?.Invoke("monitor prune failed: " + ex.GetType().Name);
        }

        UpdateStatus();
        _options.Log?.Invoke($"monitor stopped {_record.Name} reason={_record.EndReason} received={Status.Received} lost={Status.Lost}");
        Flushed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 落盘：每 5 分钟、整点加 60 秒、停止时。锁里封存小时、合并、复制出要写的行；锁外写 SQLite。成功后丢掉已封存的小时；
    /// 连续 3 次失败时停止会话（数据还在内存里，下次再写）。
    /// </summary>
    public void Flush(bool final)
    {
        _flushGate.Wait();
        try
        {
            var now = DateTime.UtcNow;
            var hours = new List<HourData>();
            IReadOnlyList<long> sealedHours;
            lock (_gate)
            {
                var a = _aggregator!;
                if (final)
                {
                    _reorder.Flush(a.Process);
                    a.Finish(now.Ticks);
                }

                sealedHours = final ? [.. a.Hours] : a.SealBefore(now.Ticks);
                foreach (var hour in a.Hours)
                {
                    var (rows, flags) = WriteTreeFolder.Fold(a.HourCounters(hour), a.Directories, _anchors);
                    _foldFlags |= (int)flags;
                    var stored = rows.Select(r => new StoredRow(r.Dir == DirectoryTable.Unresolved ? null : a.Directories.PathOf(r.Dir), a.Chains[r.Chain], r.Counters)).ToList();
                    hours.Add(new HourData(new DateTime(hour, DateTimeKind.Utc), a.Mode, a.HourStats(hour), (int)flags, stored));
                }

                var rec = _record!;
                rec.Stats = Clone(a.Stats);
                rec.LastFlushUtc = now;
                rec.PeakEventsPerSecond = _peakEventsPerSecond;
                rec.PeakWorkingSetBytes = _peakWs;
                rec.AvgCpuPermille = (int)Math.Round(Status.CpuPercent * 10);
                rec.Flags = a.Flags
                    | ((_foldFlags & (int)FoldFlags.BudgetExceeded) != 0 ? SessionFlags.BudgetExceeded : 0)
                    | ((_foldFlags & (int)FoldFlags.AnchorsFolded) != 0 ? SessionFlags.AnchorsFolded : 0)
                    | (_session?.WatchdogFailed == true ? SessionFlags.WatchdogFailed : 0);
            }

            try
            {
                _store!.Flush(_record!, hours, _options.Identity);
                _flushFailures = 0;
                _lastFlushUtc = now;
                lock (_gate)
                {
                    foreach (var h in sealedHours)
                    {
                        _aggregator!.Drop(h);
                    }
                }
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                _lastError = ex.Message;
                _options.Log?.Invoke("monitor flush failed: " + ex.GetType().Name);
                if (!final && ++_flushFailures >= 3)
                {
                    _ = Stop(StopReason.StoreError);
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }

        if (!final)
        {
            Flushed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static MonitorStats Clone(MonitorStats s)
    {
        var c = new MonitorStats();
        c.Add(s);
        return c;
    }

    public void Dispose()
    {
        if (_session is { StopReason: null })
        {
            _session.Stop(StopReason.AppExit).Wait(TimeSpan.FromSeconds(10));
        }

        _statusTimer?.Dispose();
        _flushTimer?.Dispose();
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

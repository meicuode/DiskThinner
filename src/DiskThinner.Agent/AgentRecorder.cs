using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Agent;
using DiskThinner.Monitor.Etw;

namespace DiskThinner.Agent;

/// <summary>
/// 服务里长期运行的记录（docs/phase2b-design.md 5.2 节）：一个固定名字的 ETW 会话（<see cref="NativeSession"/>）加 2a 的 <see cref="Recorder"/>。
/// 会话行按天轮换（Recorder 做）；被外部停止后过 1 分钟重开，重开的会话连续 3 次都在 10 分钟内又被停掉就不再重开；
/// 写库连续失败时停会话，每 10 分钟重试一次。汇总器在这些重开之间一直用同一个，文件状态不丢。
/// </summary>
internal sealed class AgentRecorder : IDisposable
{
    public static readonly TimeSpan ShortLived = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StoreRetry = TimeSpan.FromMinutes(10);

    private readonly AgentSettings _settings;
    private readonly AgentConfig _config;
    private readonly AgentLog _log;
    private readonly Recorder _recorder;
    private readonly Lock _gate = new();
    private readonly Timer _retry;
    private NativeSession? _session;
    private DateTime _sessionStartUtc;
    private bool _sessionFromRestart;
    private int _shortStops;
    private bool _running;
    private bool _disposed;

    public AgentRecorder(AgentSettings settings, AgentConfig config, AgentLog log)
    {
        _settings = settings;
        _config = config;
        _log = log;
        _recorder = new Recorder(new RecorderOptions
        {
            DbPath = settings.DbPath,
            Mode = config.MonitorMode,
            Identity = AgentIdentity.Read,
            FlushInterval = config.FlushSeconds is { } s ? TimeSpan.FromSeconds(s) : TimeSpan.FromMinutes(5),
            KeepDays = config.KeepDays,
            MaxBytes = config.MaxBytes,
            Log = log.Write,
            RotateName = RowName,
        });
        _recorder.StoreFailed = () => ThreadPool.UnsafeQueueUserWorkItem(_ => OnStoreFailed(), null);
        _retry = new Timer(_ => OnRetry(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>recording / restarting / stopped / external-stopped / store-error / error / starting。</summary>
    public string State { get; private set; } = "starting";

    public string? LastError { get; private set; }

    public int ExternalStops { get; private set; }

    public int Restarts { get; private set; }

    public DateTime? SessionStartedUtc => _session is null ? null : _sessionStartUtc;

    public Recorder Recorder => _recorder;

    public MonitorStore? Store => _recorder.Store;

    public static string RowName(DateTime startUtc) => AgentProtocol.RowPrefix + startUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    /// <summary>打开库，把上次没有结束的会话行标成"程序意外退出"（结束时间取最后一次落盘）。失败时返回原因。</summary>
    public string? Open()
    {
        if (_recorder.Open() is { } error)
        {
            return error;
        }

        try
        {
            var open = _recorder.Store!.OpenSessions();
            var marked = _recorder.Store.MarkCrashed(open);
            if (marked > 0)
            {
                _log.Write($"marked {marked} unfinished session rows as crashed");
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>开会话（先按名字停掉残留的同名会话，那只能是自己上次留下的）。失败时状态里写原因，返回 false。</summary>
    public bool StartSession(bool fromRestart)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_session is not null)
            {
                return true;
            }

            if (NativeSession.StopByName(_settings.SessionName))
            {
                _log.Write("stopped a residual session");
            }

            var mode = _config.MonitorMode;
            var start = DateTime.UtcNow;
            NativeSession session;
            try
            {
                session = NativeSession.Start(_settings.SessionName, mode == MonitorMode.Precise ? 32 : 8,
                [
                    (Providers.KernelFile, mode == MonitorMode.Precise ? Providers.PreciseKeywords : Providers.LightKeywords),
                    (Providers.KernelProcess, Providers.ProcessKeyword),
                ], (in RawEvent raw, EtwRecord _) => _recorder.OnEvent(raw));
            }
            catch (Win32Exception ex)
            {
                LastError = "etw " + ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message;
                State = "error";
                _log.Write("session start failed: " + LastError);
                return false;
            }

            try
            {
                _recorder.Begin(new SessionRecord { Name = RowName(start), StartedUtc = start, PlannedEndUtc = start.AddDays(1), Mode = mode });
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                session.Stop(TimeSpan.FromSeconds(5));
                LastError = ex.Message;
                State = "store-error";
                _log.Write("session row insert failed: " + ex.GetType().Name);
                _retry.Change(StoreRetry, Timeout.InfiniteTimeSpan);
                return false;
            }

            session.Completed.ContinueWith(_ => OnPumpExit(session), TaskScheduler.Default);
            session.StartPump("DiskThinner.Agent.Pump");
            _session = session;
            _sessionStartUtc = start;
            _sessionFromRestart = fromRestart;
            if (!_running)
            {
                _running = true;
                _recorder.Run(() => _session?.EventsLost ?? 0, () => _session?.Enable(Providers.KernelFile, Providers.LightKeywords));
            }

            State = "recording";
            LastError = null;
            _log.Write($"session started mode={(mode == MonitorMode.Precise ? "precise" : "light")} restart={fromRestart}");
            return true;
        }
    }

    /// <summary>停会话并结束会话行（最后一次落盘最多 10 秒）。没有会话时什么都不做。</summary>
    public void StopSession(string reason, string state)
    {
        NativeSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
            _retry.Change(Timeout.Infinite, Timeout.Infinite);
            State = state;
        }

        if (session is null)
        {
            return;
        }

        if (!session.Stop(TimeSpan.FromSeconds(10)))
        {
            _log.Write("pump did not exit in time");
        }

        _recorder.End(reason, TimeSpan.FromSeconds(10));
        _log.Write($"session stopped reason={reason}");
    }

    /// <summary>ProcessTrace 返回了，而且不是自己停的：被外部停止（logman stop）或回调出错。</summary>
    private void OnPumpExit(NativeSession session)
    {
        bool giveUp;
        lock (_gate)
        {
            if (_session != session)
            {
                return; // 自己停的
            }

            _session = null;
            var shortLived = _sessionFromRestart && DateTime.UtcNow - _sessionStartUtc < ShortLived;
            _shortStops = shortLived ? _shortStops + 1 : 0;
            giveUp = _shortStops >= 3;
            ExternalStops++;
            State = giveUp ? "external-stopped" : "restarting";
        }

        session.Stop(TimeSpan.FromSeconds(5));
        var reason = session.CallbackError is not null ? "error" : "external";
        if (session.CallbackError is { } error)
        {
            LastError = error.GetType().Name + ": " + error.Message + (error.InnerException is { } inner ? " <- " + inner.GetType().Name + ": " + inner.Message : "");
        }

        _recorder.End(reason, TimeSpan.FromSeconds(10));
        _log.Write($"session ended reason={reason} external_stops={ExternalStops} give_up={giveUp}");
        if (!giveUp)
        {
            _retry.Change(_settings.RestartDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnStoreFailed()
    {
        _log.Write("store failed 3 times, stopping session");
        StopSession("store-error", "store-error");
        LastError ??= "store";
        _retry.Change(StoreRetry, Timeout.InfiniteTimeSpan);
    }

    private void OnRetry()
    {
        try
        {
            var fromRestart = State == "restarting";
            if (State is "restarting" or "store-error" or "error")
            {
                if (fromRestart)
                {
                    Restarts++;
                }

                if (!StartSession(fromRestart) && State == "error")
                {
                    _retry.Change(StoreRetry, Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (Exception ex)
        {
            // 计时器回调里的异常不能抛到线程池
            LastError = ex.Message;
            _log.Write("retry failed: " + ex.GetType().Name);
        }
    }

    /// <summary>界面的控制命令：stop / start（重新计数"连续被停"）。</summary>
    public void Control(string action)
    {
        if (action == "stop")
        {
            StopSession("user", "stopped");
        }
        else if (action == "start")
        {
            lock (_gate)
            {
                _shortStops = 0;
            }

            StartSession(fromRestart: false);
        }
    }

    /// <summary>configure：改模式（正在记录时用新模式重开会话）和保留天数，写 agent.json。</summary>
    public void Configure(string? mode, int? keepDays)
    {
        var modeChanged = mode is not null && mode != _config.Mode;
        if (mode is not null)
        {
            _config.Mode = mode;
        }

        if (keepDays is { } days)
        {
            _config.KeepDays = days;
            _recorder.KeepDays = days;
        }

        _config.Normalize();
        _config.Version = AgentHost.Version;
        _config.Save(_settings.ConfigPath);
        _log.Write($"configured mode={_config.Mode} keep_days={_config.KeepDays}");
        if (modeChanged && State == "recording")
        {
            StopSession("config", "starting");
            StartSession(fromRestart: false);
        }
    }

    /// <summary>服务停止：停会话、最后一次落盘、停计时器。</summary>
    public void Shutdown(string reason)
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _retry.Change(Timeout.Infinite, Timeout.Infinite);
        StopSession(reason, "stopped");
        _recorder.StopTimers();
    }

    public void Dispose()
    {
        Shutdown("service-stop");
        _retry.Dispose();
        _recorder.Dispose();
    }
}

/// <summary>映像的产品名和公司名（只读版本信息，和 2a 一样；不打开被写入的文件）。</summary>
internal static class AgentIdentity
{
    public static (string? Product, string? Company) Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return (null, null);
            }

            var info = FileVersionInfo.GetVersionInfo(path);
            return (Clean(info.ProductName), Clean(info.CompanyName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

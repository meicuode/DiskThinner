using System.Diagnostics;
using System.Reflection;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.Agent;

/// <summary>
/// 服务的启动、停止和请求处理（docs/phase2b-design.md 5.2、5.5 节）。启动顺序：
/// 检查数据目录的权限 → 读配置 → 打开库、标记上次没结束的行 → 停掉残留的同名会话、开会话、列举进程 → 开管道 → 报 RUNNING。
/// 任何一步失败都返回服务专用的错误码（SCM 记下，失败动作生效），见 <see cref="ExitCodes"/>。
/// </summary>
internal sealed class AgentHost : ServiceControl.IServiceBody, IRequestHandler
{
    /// <summary>服务专用的退出码（sc query 的 SERVICE_EXIT_CODE）。</summary>
    public static class ExitCodes
    {
        public const int DataDirectory = 10;
        public const int Store = 11;
        public const int Session = 12;
        public const int Pipe = 13;
        public const int Arguments = 2;
    }

    public static readonly long WorkingSetLimit = 128L << 20;

    private readonly AgentSettings _settings;
    private readonly ManualResetEventSlim _stop = new();
    private AgentLog? _log;
    private AgentConfig? _config;
    private AgentRecorder? _recorder;
    private PipeServer? _pipe;
    private Timer? _watch;
    private string _stopReason = "service-stop";
    private DateTime _startedUtc;
    private bool _warnedWorkingSet;

    public AgentHost(AgentSettings settings) => _settings = settings;

    public static string Version =>
        typeof(AgentHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";

    public AgentLog? Log => _log;

    public int Start(Action<uint> progress)
    {
        _startedUtc = DateTime.UtcNow;
        if (_settings.CheckAcl)
        {
            // 权限不对时不写进这个目录（日志也不写），只返回错误码；界面据此提示重新安装
            if (DataDirectoryAcl.Check(_settings.DataDir) is not null)
            {
                return ExitCodes.DataDirectory;
            }
        }
        else
        {
            Directory.CreateDirectory(_settings.DataDir);
        }

        _log = new AgentLog(_settings.LogPath);
        _log.Write($"starting version={Version} pid={Environment.ProcessId} console={_settings.Console}");
        _config = AgentConfig.Load(_settings.ConfigPath, _log.Write);
        _recorder = new AgentRecorder(_settings, _config, _log);
        progress(10_000);
        if (_recorder.Open() is { } storeError)
        {
            _log.Write("store open failed: " + storeError.ReplaceLineEndings(" "));
            return ExitCodes.Store;
        }

        progress(10_000);
        if (!_recorder.StartSession(fromRestart: false))
        {
            _log.Write("start failed: " + _recorder.State);
            _recorder.Shutdown("error");
            return ExitCodes.Session;
        }

        try
        {
            _pipe = new PipeServer(_settings.PipeName, this, _log);
            _pipe.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 名字已经被别的进程占了（FILE_FLAG_FIRST_PIPE_INSTANCE）：不和它共用名字
            _log.Write("pipe create failed: " + ex.GetType().Name);
            _recorder.Shutdown("error");
            return ExitCodes.Pipe;
        }

        _watch = new Timer(_ => Watch(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        _log.Write("running");
        return 0;
    }

    /// <summary>工作集超过 128 MB 时记一次警告并降到轻量（D9 的同一套降级，第 5.2 节）。</summary>
    private void Watch()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            if (self.WorkingSet64 > WorkingSetLimit && !_warnedWorkingSet)
            {
                _warnedWorkingSet = true;
                _log?.Write($"working set {self.WorkingSet64 >> 20} MB over limit");
                _recorder?.Recorder.ForceDowngrade();
            }
        }
        catch (Exception ex)
        {
            _log?.Write("watch failed: " + ex.GetType().Name);
        }
    }

    public void WaitForStop() => _stop.Wait();

    public void RequestStop(string reason)
    {
        _stopReason = reason;
        _stop.Set();
    }

    public int Stop(Action<uint> progress)
    {
        _watch?.Dispose();
        _pipe?.Dispose();
        progress(15_000);
        _recorder?.Shutdown(_stopReason);
        _log?.Write($"stopped reason={_stopReason}");
        return 0;
    }

    public void OnPowerEvent(uint eventType) => _log?.Write("power event " + eventType);

    // ---------------- 请求 ----------------

    public IEnumerable<AgentResponse> Handle(AgentRequest request, Caller caller, CancellationToken token)
    {
        switch (request.Cmd)
        {
            case "hello":
                return [new AgentResponse { Type = "hello", Protocol = AgentProtocol.Version, AgentVersion = Version, StartedUtc = _startedUtc }];
            case "status":
                return [new AgentResponse { Type = "status", Status = BuildStatus() }];
            case "query":
                return Query(request.FromUtc!.Value, request.ToUtc!.Value, caller, token);
            case "control" when !caller.Admin:
                _log?.Write("control denied");
                return [new AgentResponse { Type = "error", Code = "denied" }];
            case "control":
                if (request.Action == "configure")
                {
                    _recorder!.Configure(request.Mode, request.KeepDays);
                }
                else
                {
                    _recorder!.Control(request.Action!);
                }

                return [new AgentResponse { Type = "ok", Status = BuildStatus() }];
            default:
                return [new AgentResponse { Type = "error", Code = "bad-request" }];
        }
    }

    private IEnumerable<AgentResponse> Query(DateTime fromUtc, DateTime toUtc, Caller caller, CancellationToken token)
    {
        var store = _recorder!.Store!;
        var index = store.LoadMerged(Utc(fromUtc), Utc(toUtc));
        token.ThrowIfCancellationRequested();
        var filter = CallerFilter.For(caller.Admin, caller.Sid, CallerFilter.ReadProfiles());
        var result = AgentQuery.Build(index, filter);
        token.ThrowIfCancellationRequested();
        var list = new List<AgentResponse>
        {
            new() { Type = "sessions", Sessions = [.. result.Sessions] },
            new() { Type = "hours", Hours = [.. result.Hours] },
        };
        for (var i = 0; i < result.Rows.Count; i += AgentProtocol.RowsPerChunk)
        {
            list.Add(new AgentResponse { Type = "rows", Rows = [.. result.Rows.Skip(i).Take(AgentProtocol.RowsPerChunk)] });
        }

        list.Add(new AgentResponse
        {
            Type = "end", Count = result.Rows.Count, Folded = result.Folded, HiddenRows = result.HiddenRows,
            Hidden = result.HiddenRows > 0 ? CountersDto.From(result.Hidden) : null,
        });
        return list;
    }

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    public AgentStatus BuildStatus()
    {
        var r = _recorder!;
        var (received, lost, factor) = r.Recorder.Counts;
        using var self = Process.GetCurrentProcess();
        long dbBytes = 0;
        DateTime? since = null;
        try
        {
            dbBytes = r.Store?.FileBytes ?? 0;
            since = r.Store?.FirstSessionStartUtc();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
        }

        return new AgentStatus
        {
            State = r.State,
            Mode = r.Recorder.Mode == MonitorMode.Light ? "light" : "precise",
            KeepDays = _config!.KeepDays,
            AgentVersion = Version,
            ServiceStartedUtc = _startedUtc,
            SessionStartedUtc = r.SessionStartedUtc,
            RecordingSinceUtc = since,
            LastFlushUtc = r.Recorder.LastFlushUtc == default ? null : r.Recorder.LastFlushUtc,
            Received = received,
            Lost = lost,
            SampleFactor = factor,
            Cpu = Math.Round(r.Recorder.CpuPercent, 3),
            WorkingSet = self.WorkingSet64,
            DbBytes = dbBytes,
            LastError = r.LastError ?? r.Recorder.LastError,
            ExternalStops = r.ExternalStops,
            Restarts = r.Restarts,
        };
    }
}

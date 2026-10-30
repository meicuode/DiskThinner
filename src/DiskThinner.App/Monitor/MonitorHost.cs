using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Etw;

namespace DiskThinner.App.Monitor;

/// <summary>工具栏和状态栏显示的写入监控状态（docs/phase2a-design.md 3.3 节 P1）。</summary>
internal enum MonitorUiState
{
    Idle,
    Running,

    /// <summary>另一个 DiskThinner 窗口正在监控（第 5.1.2 节的 Live）。</summary>
    OtherInstance,

    /// <summary>上次的会话没有正常结束、还在运行，这次没有清理掉（普通权限下）。</summary>
    Residual,

    /// <summary>常驻服务在记录（阶段 2b，Q4）。</summary>
    Agent,

    /// <summary>装了常驻服务，但它没有在记录（停止了、出错了、需要更新）。</summary>
    AgentDown,
}

/// <summary>
/// 主窗口持有的写入监控（docs/phase2a-design.md 5.9.1 节）：包着 <see cref="MonitorRunner"/>，第一次开始时才创建（才加载 TraceEvent）。
/// 2 秒一次刷新状态；会话结束或落盘后防抖 500 毫秒发 <see cref="DataChanged"/>；启动时在后台判定残留会话和没有结束的会话记录。
/// 所有公开成员都在界面线程上调用，<see cref="Load"/> 可以在任何线程上调用（每次单独开连接）。
/// </summary>
internal sealed class MonitorHost : IDisposable
{
    private readonly Control _owner;
    private readonly string _snapshotDb;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _agentTick = new() { Interval = 10_000 };
    private MonitorRunner? _runner;
    private bool _otherLive;
    private bool _residual;
    private volatile AgentSnapshot _agent = AgentSnapshot.None;
    private bool _agentRefreshing;

    public MonitorHost(Control owner, string dbPath, string snapshotDb, IAgentSource? agentSource = null)
    {
        _owner = owner;
        DbPath = Path.GetFullPath(dbPath);
        _snapshotDb = snapshotDb;
        AgentSource = agentSource ?? new PipeAgentSource(DiskThinner.Monitor.Agent.AgentProtocol.PipeName, DiskThinner.Monitor.Agent.AgentProtocol.ServiceName);
        _agentTick.Tick += async (_, _) => await RefreshAgentAsync();
        _tick.Tick += (_, _) => OnTick();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DataChanged?.Invoke(this, EventArgs.Empty);
        };

        // 未处理异常和进程退出时只按名字停会话（第 5.1.3 节）；会话已经停了时什么都不做
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _runner?.EmergencyStop();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _runner?.EmergencyStop();
    }

    public string DbPath { get; }

    public static bool Elevated => DiagnosticSession.IsElevated();

    public MonitorStatus Status => _runner?.Status ?? MonitorStatus.Idle;

    public bool IsRunning => _runner is { Status.State: not MonitorState.Stopped } && _runner.SessionName is not null;

    public MonitorUiState State => IsRunning ? MonitorUiState.Running
        : _agent.Recording ? MonitorUiState.Agent
        : _otherLive ? MonitorUiState.OtherInstance
        : _residual ? MonitorUiState.Residual
        : _agent.Installed ? MonitorUiState.AgentDown
        : MonitorUiState.Idle;

    /// <summary>常驻服务的来源（测试换成假的）。</summary>
    public IAgentSource AgentSource { get; set; }

    /// <summary>常驻服务最近一次的状态（10 秒刷新一次）。</summary>
    public AgentSnapshot Agent => _agent;

    /// <summary>
    /// 查一次常驻服务的状态（后台线程上），状态变了就发 <see cref="StatusChanged"/>；服务落盘了（最后落盘时间变了）或者开始、停止记录时发 <see cref="DataChanged"/>。
    /// 上一次还没查完时不再查。
    /// </summary>
    public async Task RefreshAgentAsync()
    {
        if (_agentRefreshing)
        {
            return;
        }

        _agentRefreshing = true;
        try
        {
            var source = AgentSource;
            AgentSnapshot next;
            try
            {
                next = await Task.Run(source.Probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log("agent probe failed: " + ex.GetType().Name);
                next = AgentSnapshot.None;
            }

            var previous = _agent;
            _agent = next;
            if (next.Recording != previous.Recording || next.Installed != previous.Installed || next.Status?.LastFlushUtc != previous.Status?.LastFlushUtc)
            {
                ScheduleDataChanged();
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _agentRefreshing = false;
        }
    }

    /// <summary>上一次会话的结束原因（这个窗口里开始的；null 表示还没有结束过）。</summary>
    public StopReason? LastEndReason { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>正在监控的会话名（会话行的 name，用来认出索引里"进行中"的那一行）。</summary>
    public string? LiveSession => IsRunning ? _runner!.SessionName : null;

    /// <summary>状态变了（每 2 秒一次，以及开始、停止、启动时的判定之后）。</summary>
    public event EventHandler? StatusChanged;

    /// <summary>monitor.db 里的数据变了（落盘、会话结束、清理了孤儿行），界面重新查询。</summary>
    public event EventHandler? DataChanged;

    public long DbBytes
    {
        get
        {
            try
            {
                return File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    /// <summary>启动时（任何权限）：标出没有结束的会话记录，管理员下停掉残留会话（第 5.1.2 节）。在后台线程上做。</summary>
    public async Task CheckAsync()
    {
        // 常驻服务的状态和诊断库的清理同时查，之后每 10 秒查一次服务
        var agent = RefreshAgentAsync();
        _agentTick.Start();
        MonitorCleanup.Result r;
        try
        {
            r = await Task.Run(() => MonitorCleanup.Run(DbPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log("monitor check failed: " + ex.GetType().Name);
            await agent;
            return;
        }

        await agent;

        _otherLive = r.Live && !IsRunning;
        _residual = !r.Live && r.ActiveListed && r.ResidualFound > r.ResidualStopped;
        if (r.OrphansMarked > 0 || r.ResidualStopped > 0 || r.TaskDeleted)
        {
            Log($"monitor check: orphans={r.OrphansMarked} residual={r.ResidualFound} stopped={r.ResidualStopped} task_deleted={r.TaskDeleted}");
            ScheduleDataChanged();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>开始监控（管理员）。失败时返回原因，不抛异常。</summary>
    public StartResult Start(TimeSpan duration, MonitorMode mode, int keepDays)
    {
        if (IsRunning)
        {
            return new StartResult(StartStatus.AlreadyRunning);
        }

        if (!Elevated)
        {
            return new StartResult(StartStatus.NotElevated);
        }

        try
        {
            // 开始前先清理上次留下的（第 5.1.2 节第 1 条）
            var cleanup = MonitorCleanup.Run(DbPath);
            if (cleanup.Live)
            {
                _otherLive = true;
                StatusChanged?.Invoke(this, EventArgs.Empty);
                return new StartResult(StartStatus.AlreadyRunning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log("monitor cleanup before start failed: " + ex.GetType().Name);
        }

        _runner?.Dispose();
        var runner = new MonitorRunner(new RunnerOptions
        {
            DbPath = DbPath,
            Session = new SessionOptions { Duration = duration, Mode = mode },
            Anchors = new SnapshotAnchors(_snapshotDb),
            Identity = MonitorIdentity.Read,
            KeepDays = keepDays,
            Log = Log,
        });
        runner.Flushed += (_, _) =>
        {
            if (_owner.IsHandleCreated && !_owner.IsDisposed)
            {
                _owner.BeginInvoke(ScheduleDataChanged);
            }
        };

        StartResult result;
        try
        {
            result = runner.Start();
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result = new StartResult(StartStatus.Failed, ex.Message);
        }

        if (result.Status != StartStatus.Started)
        {
            runner.Dispose();
            if (result.Status == StartStatus.AlreadyRunning)
            {
                _otherLive = true;
            }

            StatusChanged?.Invoke(this, EventArgs.Empty);
            return result;
        }

        _runner = runner;
        _otherLive = false;
        _residual = false;
        LastEndReason = null;
        LastError = null;
        _tick.Start();
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>用户点了"停止监控"：在后台停（停会话、最后一次落盘、删兜底任务），完成后刷新。</summary>
    public async Task StopAsync(StopReason reason)
    {
        if (_runner is not { } runner)
        {
            return;
        }

        await Task.Run(() => runner.Stop(reason));
        OnTick();
    }

    /// <summary>关闭窗口时：停止会话、最后一次落盘，最多等 3 秒；超时就不等落盘（会话在第一步就停了）。</summary>
    public void StopForExit()
    {
        if (_runner is not { } runner || !IsRunning)
        {
            return;
        }

        var stop = Task.Run(() => runner.Stop(StopReason.AppExit));
        if (!stop.Wait(TimeSpan.FromSeconds(3)))
        {
            Log("monitor stop on exit took more than 3 s");
        }
    }

    /// <summary>监控期间保存了新快照时重新读锚点（第 5.7 节第 1 条）。</summary>
    public void ReloadAnchors() => _runner?.ReloadAnchors();

    /// <summary>
    /// 读 [fromUtc, toUtc) 的汇总（任何线程）。库不存在时返回空索引，不建库；打不开时抛出（调用方显示"写入监控数据读不了"）。
    /// 常驻服务在运行、协议一致时再经管道查服务的数据，两份合在一起（服务的会话号是负数，docs/phase2b-design.md 5.6 节）；服务查不到时只用诊断库的。
    /// </summary>
    public WriteIndex Load(DateTime fromUtc, DateTime toUtc)
    {
        var local = File.Exists(DbPath) ? MonitorStore.OpenExisting(DbPath)?.LoadIndex(fromUtc, toUtc) ?? WriteIndex.Empty : WriteIndex.Empty;
        var agent = _agent;
        if (!agent.Running || !agent.ProtocolOk)
        {
            return local;
        }

        WriteIndex? remote = null;
        try
        {
            remote = AgentSource.Query(fromUtc, toUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log("agent query failed: " + ex.GetType().Name);
        }

        return WriteIndex.Merge(local, remote);
    }

    private void OnTick()
    {
        if (_runner is { } runner && runner.Status.State == MonitorState.Stopped)
        {
            // 到时、被外部停止、出错，或者用户停止之后：收尾一次
            _tick.Stop();
            LastEndReason = runner.Status.EndReason;
            LastError = runner.Status.LastError;
            if (LastEndReason == StopReason.Error && runner.Failure is { } failure)
            {
                AppLog.Crash(failure, L.T("写入监控"));
            }

            Log($"monitor ended reason={LastEndReason} received={runner.Status.Received} lost={runner.Status.Lost}");
            runner.Dispose();
            _runner = null;
            ScheduleDataChanged();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ScheduleDataChanged()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private static void Log(string message) => AppLog.Append(Path.Combine(AppLog.Directory, "monitor.log"), message, 256 * 1024);

    public void Dispose()
    {
        _agentTick.Dispose();
        _tick.Dispose();
        _debounce.Dispose();
        _runner?.Dispose();
        _runner = null;
    }
}

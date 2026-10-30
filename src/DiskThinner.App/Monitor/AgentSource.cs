using DiskThinner.App.Agent;
using DiskThinner.Core;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.App.Monitor;

/// <summary>
/// 常驻服务在某一刻的状态（docs/phase2b-design.md 5.6 节）：SCM 里的服务、管道的应答和服务报的状态。
/// 先查 SCM（普通权限也能查），服务在运行时再查管道。
/// </summary>
internal sealed record AgentSnapshot(ServiceInfo Service, string? AgentVersion, bool ProtocolOk, AgentStatus? Status, string? ClientError)
{
    public static readonly AgentSnapshot None = new(ServiceInfo.Missing, null, false, null, null);

    public bool Installed => Service.Exists;

    public bool Running => Service.Running;

    /// <summary>服务在运行、协议一致、而且在记录。</summary>
    public bool Recording => Running && ProtocolOk && Status?.State == "recording";

    /// <summary>服务的版本和程序不同（含协议不同）。</summary>
    public bool NeedsUpdate => Running && AgentVersion is not null && (!ProtocolOk || !string.Equals(AgentVersion, AppVersion.Current, StringComparison.Ordinal));

    public bool Precise => Status?.Mode != "light";

    public double LostRate => Status is { } s && s.Received + s.Lost > 0 ? (double)s.Lost / (s.Received + s.Lost) : 0;

    /// <summary>已经记录了多久（胶囊用）："已记录 6 天"、"已记录 5 小时"、"刚开始记录"。</summary>
    public string RecordedFor(DateTime nowUtc)
    {
        if (Status?.RecordingSinceUtc is not { } since)
        {
            return L.T("刚开始记录");
        }

        var span = nowUtc - since;
        return span.TotalDays >= 1 ? L.F("已记录 {0} 天", (int)span.TotalDays) : span.TotalHours >= 1 ? L.F("已记录 {0} 小时", (int)span.TotalHours) : L.T("刚开始记录");
    }

    /// <summary>服务报的状态里的问题（没有在记录时），给设置页和胶囊用。</summary>
    public string? Problem => !Installed ? null
        : !Running ? (Service.State is "start-pending" ? L.T("正在启动") : L.T("已停止"))
        : !ProtocolOk && AgentVersion is not null ? L.F("需要更新（服务 {0}，程序 {1}）", AgentVersion, AppVersion.Current)
        : Status is null ? L.T("连不上服务")
        : Status.State switch
        {
            "recording" => null,
            "stopped" => L.T("服务在运行，记录已暂停"),
            "external-stopped" => L.T("记录被外部多次停止，没有再重开"),
            "store-error" => L.T("最近出错：写库失败，每 10 分钟重试"),
            "restarting" => L.T("记录被外部停止，1 分钟后重新开始"),
            _ => L.F("最近出错：{0}", Status.LastError ?? Status.State),
        };
}

/// <summary>常驻服务的数据来源（界面测试用假的）。所有方法在后台线程上调用。</summary>
internal interface IAgentSource
{
    AgentSnapshot Probe();

    /// <summary>[fromUtc, toUtc) 的数据（会话号已经取负数）；连不上时返回 null。</summary>
    WriteIndex? Query(DateTime fromUtc, DateTime toUtc);

    /// <summary>控制命令（管理员）。</summary>
    AgentResponse? Control(string action, string? mode = null, int? keepDays = null);

    /// <summary>SCM 里的服务名。</summary>
    string ServiceName { get; }

    /// <summary>经 SCM 启动或停止服务（管理员），等到 RUNNING / STOPPED（30 秒）。失败时返回原因。</summary>
    string? SetServiceRunning(bool run);
}

/// <summary>经 SCM 和命名管道的真实来源。</summary>
internal sealed class PipeAgentSource(string pipeName, string serviceName) : IAgentSource
{
    public string ServiceName => serviceName;

    private AgentClient Client() => new(pipeName, serviceName);

    public AgentSnapshot Probe()
    {
        var client = Client();
        var service = client.Service();
        if (!service.Exists || !service.Running)
        {
            return AgentSnapshot.None with { Service = service };
        }

        var hello = client.Hello(AppVersion.Current);
        if (hello is null)
        {
            return new AgentSnapshot(service, null, false, null, client.LastError);
        }

        var ok = hello.Protocol == AgentProtocol.Version;
        return new AgentSnapshot(service, hello.AgentVersion, ok, ok ? client.Status() : null, client.LastError);
    }

    public WriteIndex? Query(DateTime fromUtc, DateTime toUtc) => Client().Query(fromUtc, toUtc);

    public AgentResponse? Control(string action, string? mode = null, int? keepDays = null) => Client().Control(action, mode, keepDays);

    public string? SetServiceRunning(bool run)
    {
        try
        {
            var scm = ServiceManager.Instance;
            if (run)
            {
                scm.Start(serviceName);
                return scm.WaitRunning(serviceName, TimeSpan.FromSeconds(30)) ? null : L.T("服务没有启动起来");
            }

            return scm.Stop(serviceName, TimeSpan.FromSeconds(30)) ? null : L.T("服务 30 秒内没有停下来");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.Message;
        }
    }
}

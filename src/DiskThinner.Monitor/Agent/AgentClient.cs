using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace DiskThinner.Monitor.Agent;

/// <summary>
/// 界面这一侧的管道客户端（docs/phase2b-design.md 5.6 节）：连不上（服务没装、没运行、超时、协议不同）时返回 null 并记下原因，不抛异常。
/// 只请求读写数据的权限、用 Identification 级别连接（服务拿不到可以冒充用户的令牌）；连上后核对服务端进程号就是 SCM 里这个服务的进程，
/// 不和别人抢先建的同名管道说话。所有方法都是同步的，在后台线程上调用。
/// </summary>
public sealed partial class AgentClient(string pipeName = AgentProtocol.PipeName, string serviceName = AgentProtocol.ServiceName, IServiceManager? services = null)
{
    private readonly IServiceManager _services = services ?? ServiceManager.Instance;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>核对服务端进程号（测试连 --console 模式的服务时关掉）。</summary>
    public bool VerifyServer { get; init; } = true;

    public string PipeName => pipeName;

    public string ServiceName => serviceName;

    /// <summary>最近一次失败的原因（not-installed / not-running / connect / server-mismatch / protocol / 错误码）。</summary>
    public string? LastError { get; private set; }

    public ServiceInfo Service() => _services.Query(serviceName);

    public AgentResponse? Hello(string appVersion) =>
        Send(new AgentRequest { Cmd = "hello", Protocol = AgentProtocol.Version, AppVersion = appVersion })?.FirstOrDefault(r => r.Type == "hello");

    public AgentStatus? Status() => Send(new AgentRequest { Cmd = "status" })?.FirstOrDefault(r => r.Type == "status")?.Status;

    /// <summary>控制命令（服务只接受已提权的管理员）。返回 ok 或 error 那一行。</summary>
    public AgentResponse? Control(string action, string? mode = null, int? keepDays = null) =>
        Send(new AgentRequest { Cmd = "control", Action = action, Mode = mode, KeepDays = keepDays })?.LastOrDefault();

    /// <summary>[fromUtc, toUtc) 的数据，转成会话号为负数、来源是常驻服务的索引（和本机诊断库合并用）。</summary>
    public WriteIndex? Query(DateTime fromUtc, DateTime toUtc)
    {
        var lines = Send(new AgentRequest { Cmd = "query", FromUtc = fromUtc, ToUtc = toUtc });
        if (lines is null)
        {
            return null;
        }

        var end = lines.LastOrDefault();
        if (end?.Type != "end")
        {
            LastError = end?.Code ?? "protocol";
            return null;
        }

        var sessions = lines.Where(l => l.Sessions is not null).SelectMany(l => l.Sessions!).Select(s => s.ToRecord(fromAgent: true)).ToList();
        var hours = lines.Where(l => l.Hours is not null).SelectMany(l => l.Hours!).Select(h => h.ToCoverage(fromAgent: true)).ToList();
        var rows = lines.Where(l => l.Rows is not null).SelectMany(l => l.Rows!).Select(r => r.ToRow(fromAgent: true)).ToList();
        return new WriteIndex(sessions, rows, hours) { HiddenOtherUsers = end.Hidden?.ToCounters(), HiddenRows = end.HiddenRows ?? 0 };
    }

    /// <summary>发一条请求，读到最后一行。失败时返回 null。</summary>
    private List<AgentResponse>? Send(AgentRequest request)
    {
        LastError = null;
        ServiceInfo? service = null;
        if (VerifyServer)
        {
            service = _services.Query(serviceName);
            if (!service.Exists)
            {
                LastError = "not-installed";
                return null;
            }

            if (!service.Running)
            {
                LastError = "not-running";
                return null;
            }
        }

        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.None,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
            if (service is not null && (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != (uint)service.ProcessId))
            {
                LastError = "server-mismatch";
                return null;
            }

            pipe.Write(Encoding.UTF8.GetBytes(AgentProtocol.Serialize(request) + "\n"));
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024);
            var list = new List<AgentResponse>();
            long bytes = 0;
            while (reader.ReadLine() is { } line)
            {
                bytes += line.Length;
                if (bytes > AgentProtocol.MaxResponseBytes || AgentProtocol.ParseResponse(line) is not { } response)
                {
                    LastError = "protocol";
                    return null;
                }

                list.Add(response);
                if (request.Cmd != "query" || response.Type is "end" or "error")
                {
                    break;
                }
            }

            if (list.Count == 0)
            {
                LastError = "closed";
                return null;
            }

            if (list[^1].Type == "error" && request.Cmd != "control")
            {
                LastError = list[^1].Code;
                return null;
            }

            return list;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            LastError = "connect: " + ex.GetType().Name;
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
}

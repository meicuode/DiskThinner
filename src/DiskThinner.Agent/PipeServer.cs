using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.Agent;

/// <summary>调用者：SID 和是不是已提权的管理员（W5：从管道客户端的令牌取，读到第一条请求之后才能模拟）。</summary>
internal readonly record struct Caller(string Sid, bool Admin);

internal interface IRequestHandler
{
    /// <summary>处理一条请求，按顺序产出回应的各行（查询分块）。在线程池线程上调用。</summary>
    IEnumerable<AgentResponse> Handle(AgentRequest request, Caller caller, CancellationToken token);
}

/// <summary>
/// 管道服务端（docs/phase2b-design.md 5.5 节）：\\.\pipe\DiskThinner.Agent.v1，最多 4 个实例，只接受本机连接（.NET 总是带 PIPE_REJECT_REMOTE_CLIENTS）。
/// 访问权限：SYSTEM 和 Administrators 完全控制；INTERACTIVE 只能读写数据，不能新建管道实例（防止别人抢先建同名实例冒充服务）。
/// 第一个实例带 FILE_FLAG_FIRST_PIPE_INSTANCE：名字已经被别人占了时启动失败。
/// 每个连接 10 秒没有请求就断开；一条请求最多处理 30 秒；请求解析失败、超长、超时都直接断开，不回显内容。
/// </summary>
internal sealed class PipeServer : IDisposable
{
    public const int Instances = 4;
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly string _name;
    private readonly IRequestHandler _handler;
    private readonly AgentLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = [];

    public PipeServer(string name, IRequestHandler handler, AgentLog log)
    {
        _name = name;
        _handler = handler;
        _log = log;
    }

    public static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes | PipeAccessRights.ReadExtendedAttributes |
            PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    private NamedPipeServerStream Create(bool first) =>
        NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None), 64 * 1024, 64 * 1024, Security());

    /// <summary>建第一个实例（名字被占时抛出），再开 4 个接受连接的循环。</summary>
    public void Start()
    {
        var first = Create(first: true);
        _loops.Add(Task.Run(() => Loop(first)));
        for (var i = 1; i < Instances; i++)
        {
            _loops.Add(Task.Run(() => Loop(null)));
        }
    }

    private async Task Loop(NamedPipeServerStream? initial)
    {
        var token = _cts.Token;
        var pipe = initial;
        while (!token.IsCancellationRequested)
        {
            try
            {
                pipe ??= Create(first: false);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                await Serve(pipe, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                // 客户端断开、超时、管道出错：这个连接到此为止
                if (ex is UnauthorizedAccessException)
                {
                    _log.Write("pipe: " + ex.GetType().Name);
                }
            }
            finally
            {
                pipe?.Dispose();
                pipe = null;
            }

            if (!token.IsCancellationRequested)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false); // 出错时不空转
            }
        }
    }

    private async Task Serve(NamedPipeServerStream pipe, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                idle.CancelAfter(IdleTimeout);
                line = await ReadLine(pipe, idle.Token).ConfigureAwait(false);
            }

            if (line is null)
            {
                return; // 客户端关了、超长或者超时
            }

            var request = AgentProtocol.ParseRequest(line);
            if (request is null)
            {
                await Write(pipe, new AgentResponse { Type = "error", Code = "bad-request" }, token).ConfigureAwait(false);
                return;
            }

            var caller = Identify(pipe);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(RequestTimeout);
            List<AgentResponse> responses;
            try
            {
                responses = await Task.Run(() => _handler.Handle(request, caller, limit.Token).ToList(), limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // 查询出错或超过 30 秒：只回错误码，日志只记类型
                _log.Write($"request {request.Cmd} failed: {ex.GetType().Name}");
                await Write(pipe, new AgentResponse { Type = "error", Code = ex is OperationCanceledException ? "busy" : "internal" }, token).ConfigureAwait(false);
                return;
            }

            long bytes = 0;
            foreach (var r in responses)
            {
                bytes += await Write(pipe, r, limit.Token).ConfigureAwait(false);
                if (bytes > AgentProtocol.MaxResponseBytes)
                {
                    await Write(pipe, new AgentResponse { Type = "error", Code = "too-large" }, token).ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    /// <summary>W5：模拟管道客户端（客户端用 Identification 级别连接即可），取 SID 和"是否已提权的管理员"。取不到时当作没有身份的普通用户。</summary>
    internal static Caller Identify(NamedPipeServerStream pipe)
    {
        var caller = new Caller("", false);
        try
        {
            pipe.RunAsClient(() =>
            {
                using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                caller = new Caller(id.User?.Value ?? "", new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
        }

        return caller;
    }

    private static async Task<long> Write(Stream pipe, AgentResponse r, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(AgentProtocol.Serialize(r) + "\n");
        await pipe.WriteAsync(bytes, token).ConfigureAwait(false);
        await pipe.FlushAsync(token).ConfigureAwait(false);
        return bytes.Length;
    }

    /// <summary>读一行（不含换行）。超过 64 KB、连接关闭或取消时返回 null。</summary>
    private static async Task<string?> ReadLine(Stream pipe, CancellationToken token)
    {
        var buffer = new byte[AgentProtocol.MaxRequestBytes + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var n = await pipe.ReadAsync(buffer.AsMemory(length, 1), token).ConfigureAwait(false);
                if (n == 0)
                {
                    return null;
                }

                if (buffer[length] == (byte)'\n')
                {
                    return Encoding.UTF8.GetString(buffer, 0, length).TrimEnd('\r');
                }

                length++;
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        return null; // 超长
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            Task.WaitAll([.. _loops], TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}

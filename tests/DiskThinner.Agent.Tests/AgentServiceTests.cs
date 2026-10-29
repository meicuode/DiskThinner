using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using DiskThinner.Monitor.Agent;
using Microsoft.Win32.SafeHandles;

namespace DiskThinner.Agent.Tests;

/// <summary>要装一个真实的（测试用）服务：只在管理员下、设置了 DT_AGENT_TESTS=1 时运行（tools/verify/phase2b-elevated.ps1 -Part agent 会设置）。</summary>
public sealed class AgentFactAttribute : FactAttribute
{
    public const string Variable = "DT_AGENT_TESTS";

    public AgentFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"没有设置 {Variable}=1：要以管理员运行，见 tools/verify/phase2b-elevated.ps1 -Part agent";
        }
        else if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "装服务需要管理员权限";
        }
    }
}

/// <summary>
/// 服务的提权测试（docs/phase2b-design.md 8.2 节，W4–W6）：用测试专用的服务名、管道名、会话名和数据目录装一个服务，
/// 依次核对：启动和应答、记录和查询、普通用户的控制命令被拒绝、外部停止后重开、强制结束后 SCM 重启并标记"程序意外退出"、
/// 启动失败（非崩溃的错误退出）也触发失败重启、停止。最后删除服务和数据目录（只删这次自己建的）。
/// 服务程序用 DT_AGENT_EXE（AOT 发布版）；没有指定时用 Agent 项目的输出。
/// </summary>
public sealed class AgentServiceTests
{
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private string ServiceName => "DiskThinnerAgentTest";
    private string PipeName => "DiskThinner.AgentTest." + _id;
    private string SessionName => "DiskThinner-AgentTest-" + _id;
    private readonly string _data;
    private readonly string _load;
    private readonly List<string> _notes = [];

    public AgentServiceTests()
    {
        _data = Path.Combine(Path.GetTempPath(), "DiskThinner-2b-" + _id);
        _load = Path.Combine(Path.GetTempPath(), "DiskThinner-2b-load-" + _id);
    }

    private static string AgentExe()
    {
        var exe = Environment.GetEnvironmentVariable("DT_AGENT_EXE");
        if (string.IsNullOrEmpty(exe))
        {
            exe = Path.Combine(AppContext.BaseDirectory, "DiskThinner.Agent.exe");
        }

        Assert.True(File.Exists(exe), "找不到 " + exe);
        return exe;
    }

    [AgentFact]
    public void Service_EndToEnd()
    {
        var report = Path.Combine(Environment.GetEnvironmentVariable("DT_AGENT_REPORT_DIR") ?? Path.GetTempPath(), "agent-tests-" + _id + ".txt");
        try
        {
            Sc("stop", ServiceName);
            Sc("delete", ServiceName); // 上次没删掉的测试服务（名字固定，是测试自己的）
            WaitGone();

            Assert.True(DataDirectoryAcl.Apply(_data) == false, "新建的目录不应该报告权限被重设");
            Assert.Null(DataDirectoryAcl.Check(_data));
            File.WriteAllText(Path.Combine(_data, "agent.json"), """{"mode":"precise","keepDays":90,"flushSeconds":5}""");

            var bin = $"\"{AgentExe()}\" --service-name {ServiceName} --data-dir \"{_data}\" --pipe {PipeName} --session {SessionName} --restart-seconds 5";
            Assert.Equal(0, Sc("create", ServiceName, "binPath=", bin, "start=", "demand", "obj=", "LocalSystem", "DisplayName=", "DiskThinner Agent Test"));
            Assert.Equal(0, Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/2000/restart/5000//"));
            Assert.Equal(0, Sc("failureflag", ServiceName, "1"));

            // ---- 启动、应答（W4）----
            var sw = Stopwatch.StartNew();
            Assert.Equal(0, Sc("start", ServiceName));
            WaitState("RUNNING", TimeSpan.FromSeconds(30));
            _notes.Add($"start_ms={sw.ElapsedMilliseconds}");
            var hello = Single(Send(new AgentRequest { Cmd = "hello", Protocol = 1, AppVersion = "test" }));
            Assert.Equal("hello", hello.Type);
            Assert.Equal(AgentProtocol.Version, hello.Protocol);
            var status = Status();
            Assert.Equal("recording", status.State);
            Assert.Equal("precise", status.Mode);
            AssertOneSession();
            var pid1 = ServicePid();
            _notes.Add($"pid={pid1} ws_mb={status.WorkingSet >> 20}");

            // ---- 记录和查询（管理员看得到全部）----
            Directory.CreateDirectory(_load);
            for (var i = 0; i < 20; i++)
            {
                File.WriteAllBytes(Path.Combine(_load, $"f{i}.bin"), new byte[16384]);
            }

            var loadRow = WaitForRows(_load, minCreates: 20, TimeSpan.FromSeconds(40));
            Assert.True(loadRow.Counters.Growth >= 20 * 16384, "估算增长 " + loadRow.Counters.Growth);
            Assert.Contains("testhost", loadRow.Writer?.Path ?? "", StringComparison.OrdinalIgnoreCase);

            // ---- 控制命令：管理员可以，去掉 Administrators 的令牌和匿名连接被拒绝（W5）----
            var ok = Single(Send(new AgentRequest { Cmd = "control", Action = "configure", KeepDays = 30 }));
            Assert.Equal("ok", ok.Type);
            Assert.Equal(30, ok.Status!.KeepDays);
            var denied = Single(AsRestrictedUser(() => Send(new AgentRequest { Cmd = "control", Action = "stop" })));
            Assert.Equal("error", denied.Type);
            Assert.Equal("denied", denied.Code);
            var anonymous = Single(Send(new AgentRequest { Cmd = "control", Action = "stop" }, TokenImpersonationLevel.Anonymous));
            Assert.Equal("denied", anonymous.Code);
            // 受限令牌下的查询：调用者是同一个用户，自己的个人目录照样看得到
            var restrictedRows = AsRestrictedUser(() => Send(Query()));
            Assert.Contains(restrictedRows.SelectMany(r => r.Rows ?? []), r => r.Dir is not null && WriteIndexIsUnder(r.Dir, _load));
            Assert.Equal("recording", Status().State);

            // ---- 外部停止后重开（第 1.3 节第 4 条，测试服务 5 秒后重开）----
            RunTool("logman", "stop", SessionName, "-ets");
            WaitFor(() => Status() is { State: "recording", ExternalStops: 1 }, TimeSpan.FromSeconds(30), "外部停止后没有重开");
            Assert.Contains(Sessions(), s => s.EndReason == "external");
            AssertOneSession();

            // ---- taskkill /F 后 SCM 重启，被结束的那一行标"程序意外退出"（第 1.3 节第 5 条，W6）----
            Thread.Sleep(6000); // 等一次落盘，"意外退出"那一行有最后落盘时间
            RunTool("taskkill", "/F", "/PID", pid1.ToString(System.Globalization.CultureInfo.InvariantCulture));
            WaitFor(() => ServicePid() is var p && p != 0 && p != pid1 && QueryState() == "RUNNING", TimeSpan.FromSeconds(40), "强制结束后 SCM 没有重启服务");
            WaitFor(() => TryStatus()?.State == "recording", TimeSpan.FromSeconds(20), "重启后没有在记录");
            var crashed = Assert.Single(Sessions(), s => s.EndReason == "crashed");
            Assert.Equal(crashed.LastFlushUtc, crashed.EndedUtc);
            AssertOneSession();
            _notes.Add("restarted_after_kill=1");

            // ---- 停止（W4）：报 STOPPED、会话消失、最后一行结束原因 service-stop ----
            sw.Restart();
            Assert.Equal(0, Sc("stop", ServiceName));
            WaitState("STOPPED", TimeSpan.FromSeconds(30));
            _notes.Add($"stop_ms={sw.ElapsedMilliseconds}");
            Assert.DoesNotContain(EtwSessions(), n => n == SessionName);

            // ---- 非崩溃的错误退出也算失败（W6）：先占住管道名，服务启动时报错退出（13），SCM 2 秒后重启，这时已经放开了 ----
            using (var squat = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.FirstPipeInstance, 0, 0, null))
            {
                Sc("start", ServiceName);
                WaitFor(() => QueryState() == "STOPPED" && ExitCode() == 13, TimeSpan.FromSeconds(30), "占住管道名时服务没有以 13 退出");
            }

            WaitState("RUNNING", TimeSpan.FromSeconds(30));
            WaitFor(() => TryStatus()?.State == "recording", TimeSpan.FromSeconds(20), "失败重启后没有在记录");
            _notes.Add("restarted_after_error_exit=1");
            Assert.Equal(0, Sc("stop", ServiceName));
            WaitState("STOPPED", TimeSpan.FromSeconds(30));

            // ---- 数据目录的权限不对时拒绝启动（第 5.9 节）：加一条 Users 的读取权限，服务以 10 退出，不写进这个目录 ----
            var acl = new DirectoryInfo(_data).GetAccessControl();
            acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                System.Security.AccessControl.FileSystemRights.ReadAndExecute, System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(_data).SetAccessControl(acl);
            Assert.NotNull(DataDirectoryAcl.Check(_data));
            var logBytes = new FileInfo(Path.Combine(_data, "logs", "agent.log")).Length;
            Sc("start", ServiceName);
            WaitFor(() => QueryState() == "STOPPED" && ExitCode() == 10, TimeSpan.FromSeconds(30), "数据目录权限不对时服务没有以 10 退出");
            Assert.Equal(logBytes, new FileInfo(Path.Combine(_data, "logs", "agent.log")).Length);
            _notes.Add("refused_bad_acl=1");
            Assert.True(DataDirectoryAcl.Apply(_data), "重设权限时应该报告原来的权限不对");
            Assert.Null(DataDirectoryAcl.Check(_data));

            var sessions = SessionsFromDb();
            _notes.Add("rows=" + string.Join(",", sessions.Select(s => s.EndReason ?? "open")));
            Assert.All(sessions, s => Assert.NotNull(s.EndReason));
            Assert.Equal("service-stop", sessions[^1].EndReason);
            var log = File.ReadAllText(Path.Combine(_data, "logs", "agent.log"));
            Assert.DoesNotContain(_load, log, StringComparison.OrdinalIgnoreCase); // 日志不记路径
        }
        finally
        {
            if (_lastQuery is not null)
            {
                _notes.Add("last_query=" + _lastQuery);
            }

            File.WriteAllLines(report, _notes);
            Sc("stop", ServiceName);
            WaitState("STOPPED", TimeSpan.FromSeconds(20), throwOnTimeout: false);
            var agentLog = Path.Combine(_data, "logs", "agent.log");
            if (File.Exists(agentLog))
            {
                File.Copy(agentLog, Path.ChangeExtension(report, ".agent.log"), overwrite: true); // 服务日志不含路径
            }
            WaitState("STOPPED", TimeSpan.FromSeconds(20), throwOnTimeout: false);
            Sc("delete", ServiceName);
            if (EtwSessions().Contains(SessionName))
            {
                RunTool("logman", "stop", SessionName, "-ets");
            }

            foreach (var dir in new[] { _data, _load })
            {
                var full = Path.GetFullPath(dir);
                if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("DiskThinner-2b-", StringComparison.Ordinal)
                    && Directory.Exists(full))
                {
                    Directory.Delete(full, recursive: true);
                }
            }
        }
    }

    private static bool WriteIndexIsUnder(string path, string ancestor) => DiskThinner.Monitor.WriteIndex.IsUnder(path, ancestor);

    private AgentRequest Query() => new() { Cmd = "query", FromUtc = DateTime.UtcNow.AddHours(-2), ToUtc = DateTime.UtcNow.AddHours(1) };

    private RowDto WaitForRows(string dir, long minCreates, TimeSpan timeout)
    {
        try
        {
            return WaitForRowsCore(dir, minCreates, timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(ex.Message + " " + (_lastQuery ?? "（没有回应）"), ex);
        }
    }

    private RowDto WaitForRowsCore(string dir, long minCreates, TimeSpan timeout)
    {
        RowDto? found = null;
        WaitFor(() =>
        {
            var responses = Send(Query());
            var s = Status();
            _lastQuery = string.Join(",", responses.Select(r => r.Type + (r.Code is null ? "" : ":" + r.Code) + (r.Rows is null ? "" : "#" + r.Rows.Count)))
                         + $" received={s.Received} last_flush={s.LastFlushUtc:O} state={s.State} error={s.LastError}";
            var rows = responses.SelectMany(r => r.Rows ?? []).Where(r => r.Dir is not null && WriteIndexIsUnder(r.Dir, dir)).ToList();
            var creates = rows.Sum(r => r.Counters.Creates);
            found = rows.OrderByDescending(r => r.Counters.Creates).FirstOrDefault();
            if (found is not null)
            {
                found.Counters.Creates = creates;
                found.Counters.Growth = rows.Sum(r => r.Counters.Growth);
            }

            return creates >= minCreates;
        }, timeout, "查询里没有负载目录的写入");
        return found!;
    }

    private string? _lastQuery;

    private List<SessionDto> Sessions() => Send(Query()).Where(r => r.Type == "sessions").SelectMany(r => r.Sessions ?? []).ToList();

    private List<DiskThinner.Monitor.SessionRecord> SessionsFromDb() =>
        [.. DiskThinner.Monitor.MonitorStore.Open(Path.Combine(_data, "monitor.db")).Sessions(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1))];

    private AgentStatus Status() => Single(Send(new AgentRequest { Cmd = "status" })).Status!;

    private AgentStatus? TryStatus()
    {
        try
        {
            return Status();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AgentResponse Single(List<AgentResponse> responses) => Assert.Single(responses);

    /// <summary>发一条请求，读到最后一行（end / error / 单行回应）。客户端只要读写数据的权限，用 Identification 级别（服务不能拿它做别的）。</summary>
    private List<AgentResponse> Send(AgentRequest request, TokenImpersonationLevel level = TokenImpersonationLevel.Identification)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeAccessRights.ReadData | PipeAccessRights.WriteData, PipeOptions.None, level, HandleInheritability.None);
        pipe.Connect(5000);
        var bytes = Encoding.UTF8.GetBytes(AgentProtocol.Serialize(request) + "\n");
        pipe.Write(bytes);
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var list = new List<AgentResponse>();
        while (reader.ReadLine() is { } line)
        {
            var r = AgentProtocol.ParseResponse(line) ?? throw new InvalidOperationException("bad response");
            list.Add(r);
            if (request.Cmd != "query" || r.Type is "end" or "error")
            {
                break;
            }
        }

        return list;
    }

    // ---------------- 去掉 Administrators 的令牌（W5） ----------------

    private static T AsRestrictedUser<T>(Func<T> body)
    {
        using var self = WindowsIdentity.GetCurrent(TokenAccessLevels.Duplicate | TokenAccessLevels.Query | TokenAccessLevels.Impersonate | TokenAccessLevels.AssignPrimary);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sid = new byte[admins.BinaryLength];
        admins.GetBinaryForm(sid, 0);
        var pin = GCHandle.Alloc(sid, GCHandleType.Pinned);
        try
        {
            var disable = new SidAndAttributes { Sid = pin.AddrOfPinnedObject(), Attributes = 0 };
            if (!CreateRestrictedToken(self.AccessToken, 0x1 /* DISABLE_MAX_PRIVILEGE */, 1, ref disable, 0, 0, 0, 0, out var restricted))
            {
                throw new System.ComponentModel.Win32Exception();
            }

            using (restricted)
            {
                using var id = new WindowsIdentity(restricted.DangerousGetHandle());
                Assert.False(new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator));
                return WindowsIdentity.RunImpersonated(restricted, body);
            }
        }
        finally
        {
            pin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing, uint flags, uint disableSidCount, ref SidAndAttributes sidsToDisable,
        uint deletePrivilegeCount, nint privilegesToDelete, uint restrictedSidCount, nint sidsToRestrict, out SafeAccessTokenHandle newToken);

    // ---------------- SCM 和会话（经 sc.exe、logman.exe） ----------------

    private static int Sc(params string[] args) => RunTool("sc.exe", args).Code;

    private static (int Code, string Output) RunTool(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, output);
    }

    private string QueryState()
    {
        var m = Regex.Match(RunTool("sc.exe", "queryex", ServiceName).Output, @"STATE\s*:\s*\d+\s+(\w+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    private int ServicePid()
    {
        var m = Regex.Match(RunTool("sc.exe", "queryex", ServiceName).Output, @"PID\s*:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    private int ExitCode()
    {
        var m = Regex.Match(RunTool("sc.exe", "queryex", ServiceName).Output, @"SERVICE_EXIT_CODE\s*:\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
    }

    private void WaitState(string state, TimeSpan timeout, bool throwOnTimeout = true)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (QueryState() == state)
            {
                return;
            }

            Thread.Sleep(250);
        }

        if (throwOnTimeout)
        {
            throw new TimeoutException($"服务 {timeout.TotalSeconds} 秒内没有到 {state}（现在 {QueryState()}）");
        }
    }

    private void WaitGone()
    {
        for (var i = 0; i < 40 && RunTool("sc.exe", "query", ServiceName).Code == 0; i++)
        {
            Thread.Sleep(250);
        }
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout, string message)
    {
        var sw = Stopwatch.StartNew();
        Exception? last = null;
        while (sw.Elapsed < timeout)
        {
            try
            {
                if (condition())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
            {
                last = ex;
            }

            Thread.Sleep(500);
        }

        throw new TimeoutException(message + (last is null ? "" : "（最后一次出错：" + last.GetType().Name + " " + last.Message + "）"));
    }

    private void AssertOneSession()
    {
        var names = EtwSessions();
        Assert.True(names.Count(n => n == SessionName) == 1, $"期望一个 {SessionName}，实际：{string.Join(",", names)}");
    }

    /// <summary>活动的 DiskThinner 会话（QueryAllTracesW；logman 的输出解析不稳定，不用它）。</summary>
    private static List<string> EtwSessions() =>
        [.. (DiskThinner.Monitor.Etw.NativeSession.ActiveSessionNames() ?? throw new InvalidOperationException("列不出会话")).Where(n => n.StartsWith("DiskThinner", StringComparison.Ordinal))];
}

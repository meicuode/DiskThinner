// 阶段 2b.0：AOT 可行性验证（见 AotProbe.csproj）。
//   AotProbe.exe <临时目录> [--etw]   在目录里建 monitor.db、写一次会话、读回；开一个带访问权限的命名管道并自己连上去收发一行。
//     --etw（2b.1，需要管理员）：用 NativeSession 开一个精确模式的会话，在目录里写 20 个文件，看收到的事件和 TDH 核对。
// 输出 key=value，退出码 0 表示全部成功。
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DiskThinner.Monitor;

var dir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? Path.Combine(Path.GetTempPath(), "DiskThinner-aot-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var failures = 0;

// ---- SQLite（Microsoft.Data.Sqlite）经 MonitorStore ----
try
{
    var store = MonitorStore.Open(Path.Combine(dir, "monitor.db"));
    var start = new DateTime(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);
    var s = new SessionRecord { Name = SessionDeadline.NewSessionName(start), StartedUtc = start, PlannedEndUtc = start.AddHours(1), Mode = MonitorMode.Precise };
    store.BeginSession(s);
    var chain = new ProcessChain(ChainKind.Normal, @"C:\x\uv.exe", "", @"C:\x\Code.exe", "", "uv.exe ← Code.exe");
    store.Flush(s, [new HourData(start, MonitorMode.Precise, new MonitorStats { Received = 10 }, 0,
        [new StoredRow(@"C:\cache", chain, new Counters { Creates = 3, Growth = 4096, WriteBytes = 4096, WriteOps = 1 })])]);
    var index = store.LoadIndex(start, start.AddHours(1));
    var row = index.Under(@"C:\cache").Single();
    Console.WriteLine($"sqlite=ok sessions={index.Sessions.Count} creates={row.Counters.Creates} writer={row.Chain.Writer?.ExeName}");
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"sqlite=FAILED {ex.GetType().Name}: {ex.Message}");
}

// ---- 带访问权限的命名管道（服务给界面的只读查询） ----
try
{
    var name = "DiskThinner.AotProbe." + Environment.ProcessId;
    var security = new PipeSecurity();
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
    using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    var serve = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync();
        using var r = new StreamReader(server, leaveOpen: true);
        using var w = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
        await w.WriteLineAsync("pong " + await r.ReadLineAsync());
    });
    using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut);
    client.Connect(5000);
    using var cr = new StreamReader(client);
    using var cw = new StreamWriter(client) { AutoFlush = true };
    cw.WriteLine("ping");
    var reply = cr.ReadLine();
    serve.Wait(5000);
    Console.WriteLine($"pipe={(reply == "pong ping" ? "ok" : "FAILED")} reply={reply}");
    if (reply != "pong ping")
    {
        failures++;
    }
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine($"pipe=FAILED {ex.GetType().Name}: {ex.Message}");
}

// ---- 2b.1：NativeSession 在 AOT 下收事件、TDH 核对解析器（需要管理员；加 --etw 时才做） ----
if (args.Contains("--etw"))
{
    try
    {
        var received = 0L;
        var writes = 0L;
        var check = new DiskThinner.Monitor.Etw.TdhVerifier();
        var name = "DiskThinnerAotProbe-" + Environment.ProcessId;
        using var session = DiskThinner.Monitor.Etw.NativeSession.Start(name, 8,
            [(Providers.KernelFile, 0x1E10UL), (Providers.KernelProcess, 0x10UL)],
            (in RawEvent raw, DiskThinner.Monitor.Etw.EtwRecord record) =>
            {
                received++;
                if (raw.Provider == Providers.KernelFile && raw.Id == 16 && raw.ProcessId == Environment.ProcessId)
                {
                    writes++;
                }

                check.Check(raw, record);
            });
        session.StartPump("AotPump");
        Thread.Sleep(1000);
        for (var i = 0; i < 20; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"etw-{i}.bin"), new byte[8192]);
        }

        Thread.Sleep(3000);
        var lost = session.EventsLost;
        var stopped = session.Stop(TimeSpan.FromSeconds(10));
        var left = DiskThinner.Monitor.Etw.NativeSession.ActiveSessionNames()?.Contains(name) ?? true;
        var ok = stopped && !left && received > 0 && writes > 0 && check.Verified > 0 && check.Mismatches == 0 && session.CallbackError is null;
        Console.WriteLine($"etw={(ok ? "ok" : "FAILED")} received={received} own_writes={writes} parse_verified={check.Verified} parse_mismatch={check.Mismatches} first_mismatch={check.FirstMismatch} lost={lost} stopped={stopped} left={left}");
        if (!ok)
        {
            failures++;
        }
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"etw=FAILED {ex.GetType().Name}: {ex.Message}");
    }
}

Console.WriteLine($"failures={failures}");
return failures == 0 ? 0 : 1;

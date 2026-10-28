using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskThinner.Monitor.Etw;
using Microsoft.Diagnostics.Tracing.Session;

namespace DiskThinner.Monitor.Tests;

/// <summary>
/// 要开真实的 ETW 会话：只在管理员下、设置了 DT_ETW_TESTS=1 时运行（tools/verify/phase2a-elevated.ps1 -Part accept 会设置），
/// 平时和 CI 上跳过（docs/phase2a-design.md 8.2 节）。
/// </summary>
public sealed class EtwFactAttribute : FactAttribute
{
    public const string Variable = "DT_ETW_TESTS";

    public EtwFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"没有设置 {Variable}=1：要以管理员运行，见 tools/verify/phase2a-elevated.ps1 -Part accept";
        }
        else if (!DiagnosticSession.IsElevated())
        {
            Skip = "ETW 会话需要管理员权限";
        }
    }
}

/// <summary>
/// 会话的开关、到时停止、被外部停止、残留清理，以及 MonitorRunner 的停止和落盘（第 12.2 节第 1、17、19 条）。
/// 名字前缀用 DiskThinnerTest-Diag-、互斥量用 Local\DiskThinner.Test.&lt;guid&gt;、不注册兜底任务，不影响真实的监控。
/// </summary>
public sealed class EtwSessionTests : IDisposable
{
    private const string Prefix = "DiskThinnerTest-Diag-";
    private static readonly Regex TestPattern = new(@"^DiskThinnerTest-Diag-\d{14}$", RegexOptions.CultureInvariant);

    private readonly string _lock = @"Local\DiskThinner.Test." + Guid.NewGuid().ToString("N");
    private readonly string _work = Path.Combine(Path.GetTempPath(), "DiskThinnerTest-" + Guid.NewGuid().ToString("N"));

    public EtwSessionTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        // 测试失败时也不留会话：停掉这次测试前缀的所有会话
        DiagnosticSession.StopResidual(DiagnosticSession.ActiveSessionNames() ?? [], TestPattern);
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SessionOptions Options(TimeSpan duration, MonitorMode mode = MonitorMode.Light, bool verify = false) => new()
    {
        Duration = duration,
        Mode = mode,
        NamePrefix = Prefix,
        LockName = _lock,
        Watchdog = false,
        VerifyParser = verify,
    };

    private static bool IsActive(string name) => DiagnosticSession.ActiveSessionNames()?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false;

    [EtwFact]
    public void StartStop_SessionComesAndGoes_StopRunsOnce()
    {
        var events = 0L;
        var stopping = 0;
        var (session, result) = DiagnosticSession.Start(Options(TimeSpan.FromMinutes(2)), _ => Interlocked.Increment(ref events));
        Assert.Equal(StartStatus.Started, result.Status);
        Assert.NotNull(session);
        session.Stopping = _ => Interlocked.Increment(ref stopping);
        Assert.Matches(TestPattern, session.Name);
        Assert.True(IsActive(session.Name));

        // 持有互斥量时另一次开始返回"另一个实例在监控"
        var (second, secondResult) = DiagnosticSession.Start(Options(TimeSpan.FromMinutes(2)), _ => { });
        Assert.Null(second);
        Assert.Equal(StartStatus.AlreadyRunning, secondResult.Status);

        var lostBefore = session.EventsLost;
        var first = session.Stop(StopReason.User);
        var again = session.Stop(StopReason.Error);
        Assert.Same(first, again);
        Assert.True(first.Wait(TimeSpan.FromSeconds(15)));
        Assert.Equal(StopReason.User, session.StopReason);
        Assert.Equal(1, stopping);
        Assert.False(IsActive(session.Name));

        // 停止之后（会话已经不在了）别的线程再读丢失数：返回最后的值，不抛异常（2a.5 验收时计时器线程在这里抛出 0x80071069，进程退出）
        Assert.True(session.EventsLost >= lostBefore);

        // 互斥量已经释放
        using var l = MonitorLock.TryAcquire(_lock);
        Assert.NotNull(l);
    }

    [EtwFact]
    public void Timeout_StopsOnTimerThread_ThenStartsAgain()
    {
        var testThread = Environment.CurrentManagedThreadId;
        var stopThread = 0;
        var (session, result) = DiagnosticSession.Start(Options(TimeSpan.FromSeconds(10)), _ => { });
        Assert.Equal(StartStatus.Started, result.Status);
        session!.Stopping = _ => stopThread = Environment.CurrentManagedThreadId;
        Assert.True(session.Stopped.Wait(TimeSpan.FromSeconds(25)), "10 秒的会话 25 秒内没有停止");
        Assert.Equal(StopReason.Timeout, session.StopReason);
        Assert.NotEqual(0, stopThread);
        Assert.NotEqual(testThread, stopThread);
        Assert.False(IsActive(session.Name));

        // 同一个进程里能再开始一次
        var (next, nextResult) = DiagnosticSession.Start(Options(TimeSpan.FromMinutes(1)), _ => { });
        Assert.Equal(StartStatus.Started, nextResult.Status);
        Assert.True(next!.Stop(StopReason.User).Wait(TimeSpan.FromSeconds(15)));
        Assert.False(IsActive(next.Name));
    }

    [EtwFact]
    public void ExternalStop_IsReportedAsExternal()
    {
        var (session, result) = DiagnosticSession.Start(Options(TimeSpan.FromMinutes(2)), _ => { });
        Assert.Equal(StartStatus.Started, result.Status);
        var logman = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "logman.exe");
        var psi = new ProcessStartInfo(logman) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "stop", session!.Name, "-ets" })
        {
            psi.ArgumentList.Add(a);
        }

        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(15_000));
            Assert.Equal(0, p.ExitCode);
        }

        Assert.True(session.Stopped.Wait(TimeSpan.FromSeconds(15)), "被外部停止后会话对象没有结束");
        Assert.Equal(StopReason.External, session.StopReason);
        using var l = MonitorLock.TryAcquire(_lock);
        Assert.NotNull(l);
    }

    [EtwFact]
    public void Cleanup_StopsResidualAndMarksOrphan_LeavesOtherSessionsAlone()
    {
        var residual = Prefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var other = "DiskThinnerTest-Other-" + Guid.NewGuid().ToString("N")[..8];
        var db = Path.Combine(_work, "mon.db");
        try
        {
            // 模拟强制结束：会话建好、启用了提供程序，但进程"没来得及"停它；库里的会话行没有结束时间
            foreach (var name in new[] { residual, other })
            {
                using var s = new TraceEventSession(name) { StopOnDispose = false, BufferSizeMB = 1 };
                s.EnableProvider(Providers.KernelProcess, Microsoft.Diagnostics.Tracing.TraceEventLevel.Verbose, Providers.ProcessKeyword);
            }

            Assert.True(IsActive(residual));
            Assert.True(IsActive(other));
            var now = DateTime.UtcNow;
            MonitorStore.Open(db).BeginSession(new SessionRecord { Name = residual, StartedUtc = now, PlannedEndUtc = now.AddMinutes(10), Mode = MonitorMode.Light });

            var r = MonitorCleanup.Run(db, _lock, namePattern: TestPattern);
            Assert.False(r.Live);
            Assert.Equal(1, r.ResidualFound);
            Assert.Equal(1, r.ResidualStopped);
            Assert.Equal(1, r.OrphansMarked);
            Assert.False(r.TaskDeleted);
            Assert.False(IsActive(residual));
            Assert.True(IsActive(other), "名字不符合的会话被停掉了");
            var row = Assert.Single(MonitorStore.Open(db).Sessions(now.AddHours(-1), now.AddHours(1)));
            Assert.Equal("crashed", row.EndReason);

            // 默认的正则（真实会话的前缀）也不碰测试会话
            Assert.Equal(0, DiagnosticSession.StopResidual([other]));
            Assert.True(IsActive(other));
        }
        finally
        {
            foreach (var name in new[] { residual, other })
            {
                try
                {
                    using var s = new TraceEventSession(name, TraceEventSessionOptions.Attach);
                    s.Stop(noThrow: true);
                }
                catch (Exception ex) when (ex is FileNotFoundException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <summary>
    /// 到时停止发生在计时器线程上时，另一个线程再调用停止拿到同一个任务，结束原因仍是"到时"；
    /// 一直有定时落盘和它并发时也不交错，库里最后留下的是最新的计数（第 12.2 节第 1 条）。
    /// </summary>
    [EtwFact]
    public void Runner_TimeoutStopWithConcurrentFlushes_KeepsLatestCounts()
    {
        MonitorRunner? runner = null;
        Task? secondStop = null;
        var runnerOptions = new RunnerOptions
        {
            DbPath = Path.Combine(_work, "mon.db"),
            Session = Options(TimeSpan.FromSeconds(10)),
            Log = m =>
            {
                if (m.StartsWith("monitor stopped", StringComparison.Ordinal))
                {
                    // 在另一个线程上再停一次（这时计时器线程还在 Stop 里面）
                    var other = new Thread(() => secondStop = runner!.Stop(StopReason.User));
                    other.Start();
                    other.Join();
                }
            },
        };
        runner = new MonitorRunner(runnerOptions);
        try
        {
            Assert.Equal(StartStatus.Started, runner.Start().Status);
            var stopped = runner.Stopped;
            var flushes = 0;
            var flusher = Task.Run(() =>
            {
                while (!stopped.IsCompleted)
                {
                    runner.Flush(final: false);
                    flushes++;
                    Thread.Sleep(100);
                }
            });
            Assert.True(stopped.Wait(TimeSpan.FromSeconds(25)), "10 秒的监控 25 秒内没有停止");
            Assert.True(flusher.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(flushes > 10, $"并发落盘只跑了 {flushes} 次");
            Assert.True(runner.Failure is null, "停止过程中计时器出错：" + runner.Failure);
            Assert.Same(stopped, secondStop);
            Assert.Equal(StopReason.Timeout, runner.Status.EndReason);
            Assert.Equal(MonitorState.Stopped, runner.Status.State);

            var row = Assert.Single(MonitorStore.Open(runnerOptions.DbPath).Sessions(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1)));
            Assert.Equal("timeout", row.EndReason);
            Assert.NotNull(row.EndedUtc);
            Assert.True(row.Stats.Received > 0, "10 秒里一个进程事件也没收到");
            Assert.Equal(runner.Status.Received, row.Stats.Received);
            Assert.False(IsActive(runner.SessionName!));
        }
        finally
        {
            runner.Dispose();
        }
    }

    private static readonly string[] SmallLoad =
    [
        "--small", "20", "--small-size", "8192", "--rename", "5", "--delete", "5", "--big-size", "4194304", "--chunk", "65536",
        "--append", "2", "--reopen", "2", "--delete-on-close", "2", "--prealloc", "2", "--short-path", "2", "--move", "2",
    ];

    /// <summary>小负载的端到端汇总（精确模式、核对解析器）：会话 → 汇总 → 落盘 → 查询，和 WriteLoad 的报告逐项相等。</summary>
    [EtwFact]
    public void Runner_SmallWriteLoad_Precise() => RunLoad(MonitorMode.Precise, SmallLoad);

    /// <summary>默认参数的负载（第 1.3 节第 7 条）：精确模式逐项比对。</summary>
    [EtwFact]
    public void Runner_DefaultWriteLoad_Precise() => RunLoad(MonitorMode.Precise, []);

    /// <summary>轻量模式只比对新建、删除、重命名的个数。</summary>
    [EtwFact]
    public void Runner_DefaultWriteLoad_Light() => RunLoad(MonitorMode.Light, []);

    private void RunLoad(MonitorMode mode, string[] loadArgs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DiskThinner-2a-" + Guid.NewGuid().ToString("N"));
        var report = Path.Combine(_work, "load.json");
        var db = Path.Combine(_work, "mon.db");
        string[] subdirs = ["small", "big", "append", "temp", "prealloc", "LongDirectoryNameForShortPath", "move", @"move\tmp", @"move\dst", @"move\done"];
        using var runner = new MonitorRunner(new RunnerOptions
        {
            DbPath = db,
            Session = Options(TimeSpan.FromMinutes(5), mode, verify: true),
            Anchors = new FixedAnchors([dir, .. subdirs.Select(s => Path.Combine(dir, s))]),
        });
        try
        {
            Assert.Equal(StartStatus.Started, runner.Start().Status);
            Thread.Sleep(1000);
            var psi = new ProcessStartInfo(WriteLoadExe()) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "--dir", dir, "--report", report }.Concat(loadArgs))
            {
                psi.ArgumentList.Add(a);
            }

            using (var p = Process.Start(psi)!)
            {
                Assert.True(p.WaitForExit(180_000), "WriteLoad 3 分钟内没有结束");
                Assert.Equal(0, p.ExitCode);
            }

            Thread.Sleep(3000); // 重排窗口 1 秒，加上实时会话的缓冲刷新
            Assert.True(runner.Stop(StopReason.User).Wait(TimeSpan.FromSeconds(15)));
            Assert.True(runner.Failure is null, "会话因为异常停止：" + runner.Failure);
            Assert.True(runner.ParseMismatch == 0, $"解析不一致 {runner.ParseMismatch} 处，第一处 {runner.MismatchDetail}");
            Assert.True(runner.EventsById.Count > 0);

            using var json = JsonDocument.Parse(File.ReadAllText(report));
            var parts = json.RootElement.GetProperty("parts");
            var index = MonitorStore.Open(db).LoadIndex(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow.AddHours(1));
            var session = Assert.Single(index.Sessions);
            Assert.Equal("user", session.EndReason);
            Assert.Equal(0, session.Stats.Lost);
            Assert.Equal(0, session.Stats.ParseErrors);

            var failures = new List<string>();
            Counters Totals(string rel)
            {
                var c = new Counters();
                foreach (var r in index.Under(Path.Combine(dir, rel)))
                {
                    c.Add(r.Counters);
                }

                return c;
            }

            void Check(string rel, string what, long expected, long actual, long tolerance = 0)
            {
                if (Math.Abs(expected - actual) > tolerance)
                {
                    failures.Add($"{rel} {what}: 期望 {expected}，实际 {actual}");
                }
            }

            long P(string part, string field) => parts.GetProperty(part).GetProperty(field).GetInt64();

            var small = Totals("small");
            Check("small", "creates", P("small", "created_files"), small.Creates);
            Check("small", "deletes", P("small", "deleted"), small.Deletes);
            Check("small", "renames", P("small", "renamed"), small.Renames);
            var temp = Totals("temp");
            Check("temp", "creates", P("temp", "created_files"), temp.Creates);
            Assert.Contains(index.Under(Path.Combine(dir, "small")), c => c.Chain.Writer?.ExeName.Equals("DiskThinner.WriteLoad.exe", StringComparison.OrdinalIgnoreCase) == true);
            if (mode == MonitorMode.Precise)
            {
                Check("small", "write_bytes", P("small", "bytes_written"), small.WriteBytes);
                Check("small", "growth", P("small", "final_bytes"), small.Growth);
                var big = Totals("big");
                Check("big", "write_bytes", P("big", "bytes_written"), big.WriteBytes);
                Check("big", "growth", P("big", "final_bytes"), big.Growth);

                // append.cmd 脚本由 WriteLoad 写；c*.bin 的 >> 由 cmd.exe 写，按目录汇总时都算
                var append = Totals("append");
                Check("append", "write_bytes", P("append", "bytes_written") + P("append", "cmd_append_bytes"), append.WriteBytes, tolerance: 4096);
                Check("append", "growth", P("append", "final_bytes"), append.Growth, tolerance: 4096);
                // 关闭时删除没有 DeletePath（V12），靠 NameDelete 认出来；轻量模式不开文件名关键字，看不到这种删除
                Check("temp", "deletes", P("temp", "deleted"), temp.Deletes);
                Check("temp", "growth", 0, temp.Growth);
                Check("prealloc", "growth", 0, Totals("prealloc").Growth);
                if (json.RootElement.GetProperty("short_name").ValueKind == JsonValueKind.String)
                {
                    Check("short", "growth", P("short", "final_bytes"), Totals("LongDirectoryNameForShortPath").Growth);
                }

                Check(@"move\tmp", "growth", 0, Totals(@"move\tmp").Growth);
                Check(@"move\dst", "growth", P("move-dst", "final_bytes"), Totals(@"move\dst").Growth);
                Check(@"move\done", "growth", P("move-done", "final_bytes"), Totals(@"move\done").Growth);
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            if (Directory.Exists(dir) && Path.GetFileName(dir).StartsWith("DiskThinner-2a-", StringComparison.Ordinal))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private sealed class FixedAnchors(IReadOnlyCollection<string> paths) : IAnchorSource
    {
        public IReadOnlyCollection<string> Load() => paths;
    }

    private static string WriteLoadExe()
    {
        // tests/DiskThinner.Monitor.Tests/bin/<配置>/<框架>/ → tests/DiskThinner.WriteLoad/bin/<配置>/<框架>/
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var framework = dir.Name;
        var configuration = dir.Parent!.Name;
        var tests = dir.Parent.Parent!.Parent!.Parent!.FullName;
        var exe = Path.Combine(tests, "DiskThinner.WriteLoad", "bin", configuration, framework, "DiskThinner.WriteLoad.exe");
        Assert.True(File.Exists(exe), "找不到 " + exe + "（Monitor.Tests 引用了这个项目，构建时会一起生成）");
        return exe;
    }
}

/// <summary>不带 RID 的构建输出（这个测试项目的输出目录）里没有 TraceEvent 的原生 DLL 和互操作程序集：证明 Directory.Build.targets 生效（第 5.11 节，D10）。</summary>
public sealed class PackagingTests
{
    [Fact]
    public void OutputDirectory_HasTraceEventWithoutNatives()
    {
        var dir = AppContext.BaseDirectory;
        Assert.True(File.Exists(Path.Combine(dir, "Microsoft.Diagnostics.Tracing.TraceEvent.dll")), "输出目录里没有 TraceEvent：测试项目没有引用 Monitor.Etw");
        foreach (var sub in new[] { "x86", "arm64", "amd64" })
        {
            Assert.False(Directory.Exists(Path.Combine(dir, sub)), $"输出目录里有 {sub}\\");
        }

        foreach (var file in new[] { "Dia2Lib.dll", "TraceReloggerLib.dll", "KernelTraceControl.dll", "msdia140.dll" })
        {
            Assert.Empty(Directory.GetFiles(dir, file, SearchOption.AllDirectories));
        }
    }

    [Fact]
    public void Start_WhenNotElevated_ReturnsNotElevated()
    {
        if (DiagnosticSession.IsElevated())
        {
            return; // CI 的 runner 是管理员，这一条只在普通权限下有意义
        }

        var (session, result) = DiagnosticSession.Start(new SessionOptions
        {
            Duration = TimeSpan.FromSeconds(30),
            Mode = MonitorMode.Light,
            NamePrefix = "DiskThinnerTest-Diag-",
            LockName = @"Local\DiskThinner.Test." + Guid.NewGuid().ToString("N"),
            Watchdog = false,
        }, _ => { });
        Assert.Null(session);
        Assert.Equal(StartStatus.NotElevated, result.Status);
    }
}

using System.Diagnostics;
using DiskThinner.Rules;
using DiskThinner.Storage;
using Xunit.Abstractions;

namespace DiskThinner.Cleanup.Tests;

/// <summary>
/// "关闭并继续"（研发方案 3.6 节）：文件被另一个程序占用时，用 Restart Manager 查出是谁；用户同意后才用 RmShutdown
/// 请它正常关闭，不强制结束。占用文件的程序是 tests/DiskThinner.LockHolder（带一个普通的顶层窗口，可以同意或拒绝关闭）。
/// 它的窗口必须是任务栏里有按钮的普通窗口：只有工具窗口时，Restart Manager 只问进程里隐藏的 SystemEvents 窗口，
/// 那个窗口总是同意，程序自己的窗口收不到询问。
/// </summary>
public sealed class RestartManagerTests(ITestOutputHelper output) : IDisposable
{
    private const long Kb = 1024;
    private readonly Fixture _f = new();
    private readonly List<Process> _holders = [];

    private string Cache => Path.Combine(_f.Local, "Cache");

    public void Dispose()
    {
        // 测试自己启动的辅助程序：断言之后还在的（拒绝关闭的那个）在这里结束掉，这是测试的清理，不是被测的行为
        foreach (var p in _holders)
        {
            if (!p.HasExited)
            {
                p.Kill();
                p.WaitForExit(10_000);
            }

            p.Dispose();
        }

        _f.Dispose();
    }

    private static string HolderExe()
    {
        // tests/DiskThinner.Cleanup.Tests/bin/<配置>/<框架>/ → tests/DiskThinner.LockHolder/bin/<配置>/<框架>/
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\'));
        var framework = dir.Name;
        var configuration = dir.Parent!.Name;
        var tests = dir.Parent.Parent!.Parent!.Parent!.FullName;
        var exe = Path.Combine(tests, "DiskThinner.LockHolder", "bin", configuration, framework, "DiskThinner.LockHolder.exe");
        Assert.True(File.Exists(exe), "找不到 " + exe + "（Cleanup.Tests 引用了这个项目，构建时会一起生成）");
        return exe;
    }

    /// <summary>启动辅助程序占用 <paramref name="file"/>，等它的窗口显示出来。</summary>
    private (Process Process, string Log) Hold(string file, string mode)
    {
        var log = Path.Combine(_f.Root, $"holder-{_holders.Count}.log");
        var psi = new ProcessStartInfo(HolderExe()) { UseShellExecute = false };
        psi.ArgumentList.Add(file);
        psi.ArgumentList.Add(log);
        psi.ArgumentList.Add(mode);
        var p = Process.Start(psi)!;
        _holders.Add(p);
        var sw = Stopwatch.StartNew();
        while (!(File.Exists(log) && Lines(log).Contains("ready")))
        {
            Assert.False(p.HasExited, "辅助程序提前退出了，退出码 " + (p.HasExited ? p.ExitCode : 0));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "等辅助程序启动超时");
            Thread.Sleep(50);
        }

        return (p, log);
    }

    private List<string> Lines(string log)
    {
        try
        {
            var lines = File.ReadAllLines(log).Select(l => l.Trim()).ToList();
            output.WriteLine(Path.GetFileName(log) + "：" + string.Join(" | ", lines));
            return lines;
        }
        catch (IOException)
        {
            return []; // 辅助程序正在写
        }
    }

    private Env PlanCache() => _f.Plan([Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents))]);

    [Fact]
    public void HolderInAnotherProcessIsFound()
    {
        var busy = _f.File(Path.Combine(Cache, "busy.bin"), 64 * Kb);
        var (p, _) = Hold(busy, "close");

        var holder = Assert.Single(RestartManager.WhoLocks([busy]));
        Assert.Equal(p.Id, holder.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(holder.Name));
        Assert.False(holder.IsService);
    }

    [Fact]
    public void WithoutConsentTheProgramIsNotAsked()
    {
        var busy = _f.File(Path.Combine(Cache, "busy.bin"), 64 * Kb);
        _f.File(Path.Combine(Cache, "free.bin"), 64 * Kb);
        var env = PlanCache();
        var (p, log) = Hold(busy, "close");

        IReadOnlyList<FileHolder>? asked = null;
        var r = Assert.Single(env.Run(new ExecutorOptions { AskCloseApps = (_, holders) => { asked = holders; return false; } }, env.Item(Cache)).Results);

        Assert.NotNull(asked);
        Assert.Contains(asked, h => h.ProcessId == p.Id);
        Assert.Equal(AuditResult.Partial, r.Result);
        Assert.Contains(busy, r.Delete!.Locked);
        Assert.Contains(asked[0].Name, env.Audit.List().Single(e => e.Action == AuditAction.Delete).Detail); // 操作记录里写出是哪个程序占用的
        Assert.True(File.Exists(busy));
        Assert.False(p.HasExited);
        Assert.DoesNotContain(Lines(log), l => l.StartsWith("query", StringComparison.Ordinal));
    }

    [Fact]
    public void WithConsentTheProgramClosesNormallyAndTheFileIsDeleted()
    {
        var busy = _f.File(Path.Combine(Cache, "busy.bin"), 64 * Kb);
        _f.File(Path.Combine(Cache, "free.bin"), 64 * Kb);
        var env = PlanCache();
        var (p, log) = Hold(busy, "close");

        var r = Assert.Single(env.Run(new ExecutorOptions { AskCloseApps = (_, _) => true }, env.Item(Cache)).Results);

        Assert.True(p.WaitForExit(20_000), "辅助程序没有退出");
        Assert.Equal(0, p.ExitCode); // 自己正常退出（被强制结束时退出码不是 0）
        var lines = Lines(log);
        Assert.Contains(lines, l => l.StartsWith("query", StringComparison.Ordinal));
        Assert.Contains("end 1", lines);
        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.Equal(0, r.Delete!.LockedCount);
        Assert.False(File.Exists(busy));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Cache));
    }

    [Fact]
    public void ProgramThatRefusesIsNotKilled()
    {
        var busy = _f.File(Path.Combine(Cache, "busy.bin"), 64 * Kb);
        var env = PlanCache();
        var (p, log) = Hold(busy, "refuse");

        Assert.False(RestartManager.RequestClose([busy]));
        var r = Assert.Single(env.Run(new ExecutorOptions { AskCloseApps = (_, _) => true }, env.Item(Cache)).Results);

        Assert.Contains(Lines(log), l => l.StartsWith("query", StringComparison.Ordinal));
        Assert.False(p.HasExited); // 不带强制标志：程序拒绝就不关
        Assert.True(File.Exists(busy));
        Assert.NotEqual(AuditResult.Succeeded, r.Result);
        Assert.Contains(busy, r.Delete!.Locked);
    }
}

using DiskThinner.Rules;

namespace DiskThinner.Cleanup.Tests;

/// <summary>官方命令白名单：只运行白名单里的那一条、程序名必须对得上、有超时、输出被记录。</summary>
public sealed class CommandTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void LookalikeCommandsAreRefused()
    {
        // 同名同参数、但不是白名单里那个对象：拒绝
        var fake = new KnownCommand("uv.cache-clean", "uv.exe", ["cache", "clean"], "假的");
        var r = CommandRunner.Run(fake, @"C:\tools\uv.exe", TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Contains("白名单", r.Error);
    }

    [Fact]
    public void ExeMustBeTheOneTheCommandNames()
    {
        var uv = KnownCommands.Find("uv.cache-clean")!;
        var r = CommandRunner.Run(uv, Cmd, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(r.Succeeded);
        Assert.Contains("找不到", r.Error);
    }

    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    // 时间上的断言都留足余量：CI 的机器忙的时候，启动一个 cmd 或 PowerShell 也可能要十几秒

    [Fact]
    public void OutputIsCapturedAndExitCodeReported()
    {
        // 输出是异步送达的：进程退出后还要等输出读到结尾，否则最后几行（这里是唯一一行）会丢
        for (var i = 0; i < 5; i++)
        {
            var r = CommandRunner.Start(Cmd, ["/c", "echo", "cleaned-42"], TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.True(r.Succeeded, $"exit={r.ExitCode} timedOut={r.TimedOut} error={r.Error}");
            Assert.Contains("cleaned-42", r.Output);
        }
    }

    /// <summary>
    /// 命令自己结束了，但留下一个后台子进程还拿着输出管道（npm 的更新检查就会这样）：不能一直等管道关闭。
    /// cmd 用 start /b 启动 PowerShell 后立即退出，PowerShell 不在进程树里了，结束进程树也结束不到它。
    /// </summary>
    [Fact]
    public void BackgroundChildrenHoldingTheOutputDoNotBlock()
    {
        var marker = "dt-test-" + Guid.NewGuid().ToString("N");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = CommandRunner.Start(Cmd, ["/c", "echo", "done", "&", "start", "/b", "", PowerShell, "-NoProfile", "-NonInteractive", "-Command", $"Start-Sleep -Seconds 180 # {marker}"],
                TimeSpan.FromMinutes(5), CancellationToken.None);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), $"等了 {sw.Elapsed.TotalSeconds:F1} 秒（子进程要 180 秒才结束）");
            Assert.Equal(0, r.ExitCode);
            Assert.False(r.TimedOut);
            Assert.Contains("done", r.Output);
        }
        finally
        {
            KillByMarker(marker);
        }
    }

    [Fact]
    public void CommandsThatRunTooLongAreStopped()
    {
        var r = CommandRunner.Start(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 300"], TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.True(r.TimedOut, $"exit={r.ExitCode} error={r.Error} output={r.Output} duration={r.Duration}");
        Assert.False(r.Succeeded);
        Assert.True(r.Duration < TimeSpan.FromSeconds(90), $"用了 {r.Duration.TotalSeconds:F1} 秒（命令要 300 秒）");
    }

    /// <summary>结束测试留下的后台进程（命令行里带着这次测试的标记）。</summary>
    private static void KillByMarker(string marker)
    {
        var find = $"Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object {{ $_.CommandLine -like '*{marker}*' -and $_.ProcessId -ne $PID }} | ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force }}";
        CommandRunner.Start(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", find], TimeSpan.FromMinutes(1), CancellationToken.None);
    }

    [Fact]
    public void EveryRuleCommandIsInTheWhitelist()
    {
        Assert.All(KnownCommands.All, c => Assert.True(CommandRunner.IsWhitelisted(c)));
        Assert.All(KnownCommands.All, c => Assert.Matches(@"^[\w.-]+\.(exe|cmd)$", c.Exe));
    }
}

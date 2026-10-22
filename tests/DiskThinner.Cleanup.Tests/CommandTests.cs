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

    [Fact]
    public void OutputIsCapturedAndExitCodeReported()
    {
        var r = CommandRunner.Start(Cmd, ["/c", "echo", "cleaned-42"], TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.True(r.Succeeded, r.Error);
        Assert.Contains("cleaned-42", r.Output);
    }

    /// <summary>
    /// 命令自己结束了，但留下一个后台子进程还拿着输出管道（npm 的更新检查就会这样）：不能一直等管道关闭。
    /// cmd 用 start /b 启动 PowerShell 后立即退出，PowerShell 不在进程树里了，结束进程树也结束不到它。
    /// </summary>
    [Fact]
    public void BackgroundChildrenHoldingTheOutputDoNotBlock()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = CommandRunner.Start(Cmd, ["/c", "echo", "done", "&", "start", "/b", "", powershell, "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 25"],
            TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"等了 {sw.Elapsed.TotalSeconds:F1} 秒");
        Assert.Equal(0, r.ExitCode);
        Assert.False(r.TimedOut);
        Assert.Contains("done", r.Output);
    }

    [Fact]
    public void CommandsThatRunTooLongAreStopped()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var r = CommandRunner.Start(powershell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromSeconds(3), CancellationToken.None);
        Assert.True(r.TimedOut, $"exit={r.ExitCode} error={r.Error} output={r.Output} duration={r.Duration}");
        Assert.False(r.Succeeded);
        Assert.True(r.Duration < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void EveryRuleCommandIsInTheWhitelist()
    {
        Assert.All(KnownCommands.All, c => Assert.True(CommandRunner.IsWhitelisted(c)));
        Assert.All(KnownCommands.All, c => Assert.Matches(@"^[\w.-]+\.(exe|cmd)$", c.Exe));
    }
}

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DiskThinner.App.Shell;

/// <summary>
/// 每日快照的计划任务（研发方案 3.5 节）：用系统自带的 schtasks.exe 注册"每天定时运行 DiskThinner.exe snapshot"。
/// 以管理员身份注册时任务用最高权限运行（MFT 扫描，几秒）；否则用普通权限（遍历扫描，约 1 到 2 分钟）。
/// 参数逐个传给 schtasks，不经过命令行解释器；schtasks.exe 用 System32 下的完整路径。
/// </summary>
internal static partial class ScheduledSnapshot
{
    public const string TaskName = @"\DiskThinner\DailySnapshot";
    public const string DefaultTime = "12:00";

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public sealed record Status(bool Registered, string? Time, bool Highest);

    /// <summary>注册（或覆盖）任务的参数。</summary>
    internal static List<string> CreateArguments(string exePath, string time, bool highest, string? drive)
    {
        if (!TimePattern().IsMatch(time))
        {
            throw new ArgumentException("时间格式应为 HH:mm", nameof(time));
        }

        var action = $"\"{exePath}\" snapshot" + (drive is null ? "" : $" --drive {drive.TrimEnd('\\')}");
        var args = new List<string> { "/Create", "/TN", TaskName, "/TR", action, "/SC", "DAILY", "/ST", time, "/F" };
        if (highest)
        {
            args.AddRange(["/RL", "HIGHEST"]);
        }

        return args;
    }

    public static (bool Ok, string Message) Register(string exePath, string time, bool highest, string? drive) =>
        Run(CreateArguments(exePath, time, highest, drive));

    public static (bool Ok, string Message) Unregister() => Run(["/Delete", "/TN", TaskName, "/F"]);

    /// <summary>查询任务：是否存在、每天几点、是否用最高权限。</summary>
    public static Status Query()
    {
        var (ok, xml) = Run(["/Query", "/TN", TaskName, "/XML"]);
        if (!ok)
        {
            return new Status(false, null, false);
        }

        var start = StartPattern().Match(xml);
        return new Status(true, start.Success ? start.Groups[1].Value : null, xml.Contains("<RunLevel>HighestAvailable</RunLevel>", StringComparison.Ordinal));
    }

    private static (bool Ok, string Output) Run(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(Schtasks)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                p.Kill();
                return (false, "schtasks 没有响应");
            }

            return (p.ExitCode == 0, p.ExitCode == 0 ? output.Result : (error.Result + output.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$")]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"<StartBoundary>\d{4}-\d{2}-\d{2}T(\d{2}:\d{2})")]
    private static partial Regex StartPattern();
}

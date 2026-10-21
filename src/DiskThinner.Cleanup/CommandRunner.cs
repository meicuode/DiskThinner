using System.Diagnostics;
using System.Text;
using DiskThinner.Inventory;
using DiskThinner.Rules;

namespace DiskThinner.Cleanup;

/// <summary>一次官方命令的结果。</summary>
public sealed record CommandResult(int? ExitCode, bool TimedOut, bool Cancelled, string Output, TimeSpan Duration, string? Error)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled && Error is null;
}

/// <summary>
/// 运行官方清理命令（研发方案 3.6 节）：只运行 <see cref="KnownCommands"/> 白名单里的命令；
/// 程序必须是已装软件探测到的那个；参数是固定数组，不拼命令行字符串；有超时；输出截取一段写进操作记录。
/// </summary>
public static class CommandRunner
{
    public const int MaxOutput = 16 * 1024;

    /// <summary>是白名单里的那一条（同一个对象，不是同名的另一条）。</summary>
    public static bool IsWhitelisted(KnownCommand command) =>
        KnownCommands.Find(command.Id) is { } known && ReferenceEquals(known, command);

    public static CommandResult Run(KnownCommand command, string exePath, TimeSpan timeout, CancellationToken token)
    {
        if (!IsWhitelisted(command))
        {
            return new CommandResult(null, false, false, "", TimeSpan.Zero, "命令不在白名单里");
        }

        if (!Path.IsPathFullyQualified(exePath) || !string.Equals(Path.GetFileName(exePath), command.Exe, StringComparison.OrdinalIgnoreCase) || !File.Exists(exePath))
        {
            return new CommandResult(null, false, false, "", TimeSpan.Zero, $"找不到 {command.Exe}：{exePath}");
        }

        return Start(exePath, command.Arguments, timeout, token);
    }

    /// <summary>启动程序并等待结束。不经过 shell（.cmd 由系统交给 cmd.exe，参数是固定的常量）。</summary>
    internal static CommandResult Start(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (output)
            {
                if (output.Length < MaxOutput)
                {
                    output.AppendLine(line.Length > 1000 ? line[..1000] : line);
                }
            }
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(null, false, false, "", sw.Elapsed, ex.Message);
        }

        process.StandardInput.Close(); // 命令要求确认时直接得到 EOF，不会卡住
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timedOut = false;
        var cancelled = false;
        while (!process.WaitForExit(200))
        {
            if (token.IsCancellationRequested || sw.Elapsed > timeout)
            {
                cancelled = token.IsCancellationRequested;
                timedOut = !cancelled;
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // 已经退出
                }

                process.WaitForExit(5000);
                break;
            }
        }

        process.WaitForExit(); // 等输出读完
        string text;
        lock (output)
        {
            text = output.ToString().TrimEnd();
        }

        return new CommandResult(timedOut || cancelled ? null : process.ExitCode, timedOut, cancelled, text, sw.Elapsed, null);
    }
}

/// <summary>找到官方命令的程序：只认已装软件探测到的（命令行工具目录、正在运行的程序）。</summary>
public static class CommandResolver
{
    public static string? Resolve(KnownCommand command, InventorySnapshot inventory)
    {
        if (command.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return inventory.Apps
                .Where(a => a.Source is AppSource.CommandLine or AppSource.Process && a.ExePath is not null)
                .Select(a => a.ExePath!)
                .FirstOrDefault(p => string.Equals(Path.GetFileName(p), command.Exe, StringComparison.OrdinalIgnoreCase) && File.Exists(p));
        }

        // npm.cmd、yarn.cmd：在已装软件扫描的命令行工具目录里找
        return InventoryCollector.CommandLineDirs()
            .Select(d => Path.Combine(d, command.Exe))
            .FirstOrDefault(File.Exists);
    }
}

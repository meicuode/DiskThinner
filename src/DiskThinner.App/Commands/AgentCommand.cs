using System.Globalization;
using DiskThinner.App.Agent;
using DiskThinner.App.Shell;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.App.Commands;

/// <summary>
/// 常驻服务的子命令（docs/phase2b-design.md FR-9、5.3 节），给验收脚本、冒烟测试和高级用户用：
///   DiskThinner.exe agent --install [--mode light|precise] [--keep-days 30|90|180] [--flush-seconds N]
///   DiskThinner.exe agent --update
///   DiskThinner.exe agent --uninstall [--delete-data]
///   DiskThinner.exe agent --status [--dir 路径] [--hours 24]
/// 都可以加 --report 路径（key=value）和 --agent-source 目录（服务程序所在的目录，默认是 DiskThinner.exe 旁边的 Agent\）。
/// 返回值同 monitor 子命令：0 成功，1 出错，2 不是管理员，4 参数不对。主程序是 WinExe，结果只写 --report 和 logs\agent-install.log。
/// </summary>
internal static class AgentCommand
{
    public static string LogPath => Path.Combine(AppLog.Directory, "agent-install.log");

    public static int Run(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        var report = new KeyValueReport(Value("--report"), Log);
        var layout = AgentLayout.Default(Value("--agent-source") is { } src ? Path.GetFullPath(src) : null);
        var installer = new AgentInstaller(layout, ServiceManager.Instance) { Log = Log };
        try
        {
            if (Flag("--status"))
            {
                Status(report, layout, Value("--dir"), int.TryParse(Value("--hours"), NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? Math.Clamp(h, 1, 24 * 400) : 24);
                return Done(report, 0);
            }

            var action = Flag("--install") ? "install" : Flag("--update") ? "update" : Flag("--uninstall") ? "uninstall" : null;
            if (action is null)
            {
                Log("agent: 需要 --install、--update、--uninstall 或 --status");
                return Done(report, 4);
            }

            if (!DiskThinner.Monitor.Etw.DiagnosticSession.IsElevated())
            {
                report.Set("error", "not elevated");
                return Done(report, 2);
            }

            var mode = Value("--mode")?.ToLowerInvariant() ?? "precise";
            var keep = int.TryParse(Value("--keep-days"), NumberStyles.None, CultureInfo.InvariantCulture, out var k) ? k : 90;
            int? flush = int.TryParse(Value("--flush-seconds"), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : null;
            if (mode is not ("light" or "precise") || !AgentProtocol.KeepDaysChoices.Contains(keep) || flush is < 5 or > 300)
            {
                Log("agent: --mode / --keep-days / --flush-seconds 超出范围");
                return Done(report, 4);
            }

            var result = action switch
            {
                "install" => installer.Install(new AgentInstallOptions(mode, keep, flush)),
                "update" => installer.Update(),
                _ => installer.Uninstall(Flag("--delete-data")),
            };
            report.Set("action", action);
            report.Set("ok", result.Ok ? "1" : "0");
            report.Set("message", result.Message);
            report.Set("acl_reset", result.AclReset ? "1" : "0");
            report.Set("version", result.Version ?? "");
            report.Set("log_tail", string.Join(" | ", result.LogTail ?? []));
            return Done(report, result.Ok ? 0 : 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log("agent failed: " + ex.GetType().Name + ": " + ex.Message);
            report.Set("error", ex.Message);
            return Done(report, 1);
        }
    }

    private static int Done(KeyValueReport report, int code)
    {
        report.Set("exit_code", code.ToString(CultureInfo.InvariantCulture));
        report.Save();
        return code;
    }

    /// <summary>服务和管道的状态；给了 --dir 时再查一次最近 N 小时的数据，统计这个目录下的新建个数和估算增长（冒烟测试用）。</summary>
    private static void Status(KeyValueReport report, AgentLayout layout, string? dir, int hours)
    {
        var info = ServiceManager.Instance.Query(layout.ServiceName);
        var installer = new AgentInstaller(layout, ServiceManager.Instance);
        report.Set("installed", info.Exists ? "1" : "0");
        report.Set("service_state", info.State);
        report.Set("service_pid", info.ProcessId.ToString(CultureInfo.InvariantCulture));
        report.Set("service_exit", $"{info.Win32ExitCode}/{info.ServiceExitCode}");
        report.Set("ours", info.Exists && installer.IsOurs(info.BinaryPath) ? "1" : "0");
        report.Set("binary", AgentInstaller.ExePath(info.BinaryPath) ?? "");
        report.Set("app_version", AppVersion.Current);
        report.Set("source_available", layout.SourceAvailable ? "1" : "0");
        try
        {
            report.Set("data_acl", DataDirectoryAcl.Check(layout.DataDir) ?? "ok");
        }
        catch (UnauthorizedAccessException)
        {
            report.Set("data_acl", "no-access"); // 普通权限读不了数据目录的权限，这本身就说明权限是对的
        }
        report.Set("sessions_active", string.Join(',', (DiskThinner.Monitor.Etw.NativeSession.ActiveSessionNames() ?? []).Where(n => n.StartsWith("DiskThinner", StringComparison.Ordinal))));
        if (!info.Running)
        {
            return;
        }

        var client = new AgentClient(layout.PipeName, layout.ServiceName);
        var hello = client.Hello(AppVersion.Current);
        report.Set("agent_version", hello?.AgentVersion ?? "");
        report.Set("protocol", hello?.Protocol?.ToString(CultureInfo.InvariantCulture) ?? "");
        if (client.Status() is { } s)
        {
            report.Set("agent_state", s.State);
            report.Set("mode", s.Mode);
            report.Set("keep_days", s.KeepDays.ToString(CultureInfo.InvariantCulture));
            report.Set("received", s.Received.ToString(CultureInfo.InvariantCulture));
            report.Set("lost", s.Lost.ToString(CultureInfo.InvariantCulture));
            report.Set("cpu_percent", s.Cpu.ToString("0.###", CultureInfo.InvariantCulture));
            report.Set("working_set_bytes", s.WorkingSet.ToString(CultureInfo.InvariantCulture));
            report.Set("db_bytes", s.DbBytes.ToString(CultureInfo.InvariantCulture));
            report.Set("recording_since", s.RecordingSinceUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "");
            report.Set("last_flush", s.LastFlushUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "");
            report.Set("external_stops", s.ExternalStops.ToString(CultureInfo.InvariantCulture));
            report.Set("last_error", s.LastError ?? "");
        }

        if (dir is not null)
        {
            var now = DateTime.UtcNow;
            if (client.Query(now.AddHours(-hours), now.AddHours(1)) is { } index)
            {
                var full = Path.GetFullPath(dir);
                var total = new Counters();
                foreach (var row in index.Rows.Where(r => r.Dir is not null && WriteIndex.IsUnder(r.Dir, full)))
                {
                    total.Add(row.Counters);
                }

                report.Set("sessions", index.Sessions.Count.ToString(CultureInfo.InvariantCulture));
                report.Set("session_reasons", string.Join(',', index.Sessions.Select(s => s.EndReason ?? "open")));
                report.Set("hidden_rows", index.HiddenRows.ToString(CultureInfo.InvariantCulture));
                report.Set("rows", index.Rows.Count.ToString(CultureInfo.InvariantCulture));
                report.Set("dir_creates", total.Creates.ToString(CultureInfo.InvariantCulture));
                report.Set("dir_growth", total.Growth.ToString(CultureInfo.InvariantCulture));
                report.Set("dir_write_bytes", total.WriteBytes.ToString(CultureInfo.InvariantCulture));
            }
        }

        report.Set("client_error", client.LastError ?? "");
    }

    private static void Log(string message) => AppLog.Append(LogPath, message, 256 * 1024);
}

using System.Globalization;
using DiskThinner.App.Monitor;
using DiskThinner.App.Shell;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Etw;
using DiskThinner.Storage;

namespace DiskThinner.App.Commands;

/// <summary>
/// 无界面的 monitor 子命令（docs/phase2a-design.md 5.9.8 节，D14），诊断和验证用：
///   DiskThinner.exe monitor --seconds N [--mode light|precise] [--db 路径] [--snapshot-db 路径] [--report 路径] [--verify-parser]
///   DiskThinner.exe monitor --cleanup [--db 路径] [--report 路径]
///   DiskThinner.exe monitor --status [--report 路径]
/// 返回值：0 成功，1 出错，2 不是管理员，3 另一个实例正在监控，4 参数不对。主程序是 WinExe，结果只写 --report 和 logs\monitor.log。
/// </summary>
internal static class MonitorCommand
{
    public static string LogPath => Path.Combine(AppLog.Directory, "monitor.log");

    public static int Run(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        var report = new KeyValueReport(Value("--report"), Log);
        var db = Value("--db") ?? MonitorStore.DefaultPath;
        try
        {
            if (Flag("--status"))
            {
                var active = DiagnosticSession.ActiveSessionNames();
                report.Set("live", MonitorLock.ExistsElsewhere() ? "1" : "0");
                report.Set("residual", active is null ? "" : string.Join(',', active.Where(n => SessionLiveness.NamePattern.IsMatch(n))));
                report.Set("exit_code", "0");
                report.Save();
                return 0;
            }

            if (Flag("--cleanup"))
            {
                var r = MonitorCleanup.Run(db);
                report.Set("residual_found", r.ResidualFound.ToString(CultureInfo.InvariantCulture));
                report.Set("residual_stopped", r.ResidualStopped.ToString(CultureInfo.InvariantCulture));
                report.Set("orphans_marked", r.OrphansMarked.ToString(CultureInfo.InvariantCulture));
                report.Set("task_deleted", r.TaskDeleted ? "1" : "0");
                var code = r.ResidualFound > r.ResidualStopped && !DiagnosticSession.IsElevated() ? 2 : 0;
                report.Set("exit_code", code.ToString(CultureInfo.InvariantCulture));
                report.Save();
                Log($"cleanup residual={r.ResidualFound} stopped={r.ResidualStopped} orphans={r.OrphansMarked}");
                return code;
            }

            if (!int.TryParse(Value("--seconds"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 10 || seconds > 86_400)
            {
                report.Set("exit_code", "4");
                report.Save();
                Log("monitor: --seconds 要在 10 到 86400 之间");
                return 4;
            }

            var mode = string.Equals(Value("--mode"), "precise", StringComparison.OrdinalIgnoreCase) ? MonitorMode.Precise : MonitorMode.Light;
            if (!DiagnosticSession.IsElevated())
            {
                report.Set("exit_code", "2");
                report.Save();
                return 2;
            }

            MonitorCleanup.Run(db); // 先清理上次留下的
            var runner = new MonitorRunner(new RunnerOptions
            {
                DbPath = db,
                Session = new SessionOptions { Duration = TimeSpan.FromSeconds(seconds), Mode = mode, VerifyParser = Flag("--verify-parser") },
                Anchors = new SnapshotAnchors(Value("--snapshot-db") ?? SnapshotStore.DefaultPath),
                Identity = MonitorIdentity.Read,
                Log = Log,
            });

            // 这条路径不经过界面的异常处理：自己兜底，异常或退出时按名字停会话
            AppDomain.CurrentDomain.UnhandledException += (_, _) => runner.EmergencyStop();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => runner.EmergencyStop();
            report.Set("state", "starting");
            report.Set("started", "0");
            report.Save();
            var start = runner.Start();
            if (start.Status != StartStatus.Started)
            {
                var code = start.Status switch { StartStatus.AlreadyRunning => 3, StartStatus.NotElevated => 2, _ => 1 };
                report.Set("state", "stopped");
                report.Set("error", start.Message ?? start.Status.ToString());
                report.Set("exit_code", code.ToString(CultureInfo.InvariantCulture));
                report.Save();
                Log($"monitor start failed: {start.Status} {start.Message}");
                return code;
            }

            report.Set("state", "running");
            report.Set("started", "1");
            report.Set("session_name", runner.SessionName ?? "");
            report.Set("mode", mode == MonitorMode.Precise ? "precise" : "light");
            report.Save();
            while (!runner.Stopped.Wait(TimeSpan.FromSeconds(5)))
            {
                Fill(report, runner);
                report.Save();
            }

            Fill(report, runner);
            var name = runner.SessionName ?? "";
            var stillActive = DiagnosticSession.ActiveSessionNames()?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false;
            report.Set("state", "stopped");
            report.Set("session_stopped", stillActive ? "false" : "true");
            report.Set("end_reason", runner.Status.EndReason?.ToString().ToLowerInvariant() ?? "");

            // 会话因为异常或写库失败而提前停止：退出码 1（冒烟测试据此报错）
            var failed = runner.Status.EndReason is StopReason.Error or StopReason.StoreError;
            report.Set("exit_code", failed ? "1" : "0");
            report.Save();
            runner.Dispose();
            return failed ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            Log("monitor failed: " + ex.GetType().Name + ": " + ex.Message);
            report.Set("error", ex.Message);
            report.Set("exit_code", "1");
            report.Save();
            return 1;
        }
    }

    private static void Fill(KeyValueReport report, MonitorRunner runner)
    {
        var s = runner.Status;
        report.Set("received", s.Received.ToString(CultureInfo.InvariantCulture));
        report.Set("lost", s.Lost.ToString(CultureInfo.InvariantCulture));
        report.Set("sample_factor", s.SampleFactor.ToString(CultureInfo.InvariantCulture));
        report.Set("parse_mismatch", runner.ParseMismatch.ToString(CultureInfo.InvariantCulture));
        report.Set("parse_mismatch_first", runner.MismatchDetail ?? "");
        report.Set("parse_verified", runner.ParseVerified.ToString(CultureInfo.InvariantCulture));
        report.Set("cpu_percent", s.CpuPercent.ToString("0.###", CultureInfo.InvariantCulture));
        report.Set("working_set_bytes", s.WorkingSetBytes.ToString(CultureInfo.InvariantCulture));
        report.Set("events_by_id", string.Join(';', runner.EventsById.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}")));
        report.Set("last_error", s.LastError ?? "");
    }

    private static void Log(string message) => AppLog.Append(LogPath, message, 256 * 1024);
}

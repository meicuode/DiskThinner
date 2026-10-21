using System.Diagnostics;
using System.Globalization;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Scanner;
using DiskThinner.Storage;

namespace DiskThinner.App.Commands;

/// <summary>
/// 无界面的 snapshot 子命令（由计划任务调用）：
///   DiskThinner.exe snapshot [--drive C:] [--db 路径]     扫描一次、保存快照、按保留策略清理旧快照
///   DiskThinner.exe snapshot --register [--time 12:00]    注册每日快照任务（以管理员身份运行时用最高权限）
///   DiskThinner.exe snapshot --unregister                 移除任务
/// 结果写到 %LOCALAPPDATA%\DiskThinner\logs\snapshot.log；返回值 0 表示成功。
/// </summary>
internal static class SnapshotCommand
{
    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "logs", "snapshot.log");

    public static int Run(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        try
        {
            if (Flag("--register"))
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("取不到程序路径");
                var highest = VolumeScanner.IsElevated();
                var (ok, message) = ScheduledSnapshot.Register(exe, Value("--time") ?? ScheduledSnapshot.DefaultTime, highest, AppOptions.NormalizeDrive(Value("--drive")));
                Log(ok ? $"已注册每日快照任务{(highest ? "（最高权限）" : "")}" : "注册每日快照任务失败：" + message);
                return ok ? 0 : 1;
            }

            if (Flag("--unregister"))
            {
                var (ok, message) = ScheduledSnapshot.Unregister();
                Log(ok ? "已移除每日快照任务" : "移除每日快照任务失败：" + message);
                return ok ? 0 : 1;
            }

            // --drive 也可以是一个文件夹（用遍历扫描），便于在不需要管理员的情况下验证
            var target = Value("--drive");
            var drive = AppOptions.NormalizeDrive(target)
                ?? (target is not null && Directory.Exists(target) ? Path.GetFullPath(target) : null)
                ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var store = new SnapshotStore(Value("--db") ?? SnapshotStore.DefaultPath);
            var settings = Settings.AppSettings.Load(Settings.AppSettings.DefaultPath, out _);
            var info = Take(store, drive, DateTime.UtcNow, CancellationToken.None, settings.SnapshotOptions(), settings.Snapshot.KeepDays);
            Log($"{info.Volume} 快照完成：{(info.ScanMode == "mft" ? "MFT" : "遍历")}扫描，{info.Entries:N0} 项，已用 {ByteSize.Format(info.UsedBytes)}，可用 {ByteSize.Format(info.FreeBytes)}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException
                                       or ArgumentException or Microsoft.Data.Sqlite.SqliteException or System.ComponentModel.Win32Exception)
        {
            Log("快照失败：" + ex.Message);
            return 1;
        }
    }

    /// <summary>扫描并保存一次快照，再按保留策略清理旧快照。界面里的"立即创建快照"不重新扫描，直接用 <see cref="SnapshotStore.Save"/>。</summary>
    public static SnapshotInfo Take(SnapshotStore store, string drive, DateTime nowUtc, CancellationToken token, SnapshotOptions? options = null, int keepDays = 180)
    {
        var latency = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
        try
        {
            var result = VolumeScanner.Scan(drive, ScanPreference.Auto, null, token);
            var saved = store.Save(result.Tree, result.Stats, nowUtc, options);
            store.ApplyRetention(nowUtc, keepDays);
            return saved.Info;
        }
        finally
        {
            System.Runtime.GCSettings.LatencyMode = latency;
        }
    }

    /// <summary>追加一行日志；日志超过 256 KB 时只保留后一半。</summary>
    internal static void Log(string message) => AppLog.Append(LogPath, message, 256 * 1024);
}

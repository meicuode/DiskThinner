using System.Diagnostics;
using System.Globalization;

namespace DiskThinner.App.Shell;

/// <summary>
/// 程序日志：%LOCALAPPDATA%\DiskThinner\logs\app.log（启动、扫描、清理、出错）；未处理的异常另写一份 crash-时间.txt。
/// 日志只在本机，不上传。超过 512 KB 时只保留后一半。
/// </summary>
internal static class AppLog
{
    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "logs");

    public static string FilePath => Path.Combine(Directory, "app.log");

    private static readonly Lock Gate = new();

    public static void Write(string message) => Append(FilePath, message, 512 * 1024);

    /// <summary>追加一行；文件超过 <paramref name="maxBytes"/> 时只保留后一半。写不了就算了（日志不能影响程序本身）。</summary>
    internal static void Append(string path, string message, long maxBytes)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > maxBytes)
                {
                    var lines = File.ReadAllLines(path);
                    File.WriteAllLines(path, lines[(lines.Length / 2)..]);
                }

                File.AppendAllLines(path, [$"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {message}"]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(ex.Message);
            }
        }
    }

    /// <summary>写一份崩溃报告，返回文件路径（写不了时返回 null）。</summary>
    public static string? Crash(Exception ex, string where)
    {
        Write($"未处理的异常（{where}）：{ex.GetType().Name}: {ex.Message}");
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, $"crash-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");
            File.WriteAllText(path,
                $"DiskThinner {typeof(AppLog).Assembly.GetName().Version}\n" +
                $"时间：{DateTime.Now:O}\n位置：{where}\n系统：{Environment.OSVersion}，.NET {Environment.Version}，{(Environment.Is64BitProcess ? "64" : "32")} 位\n\n{ex}\n");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

using DiskThinner.App.Shell;
using DiskThinner.Core;

namespace DiskThinner.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 计划任务调用的无界面子命令
        if (args.Length > 0 && string.Equals(args[0], "snapshot", StringComparison.OrdinalIgnoreCase))
        {
            return Commands.SnapshotCommand.Run(args[1..]);
        }

        // 写入监控的诊断和验证子命令（D14）
        if (args.Length > 0 && string.Equals(args[0], "monitor", StringComparison.OrdinalIgnoreCase))
        {
            return Commands.MonitorCommand.Run(args[1..]);
        }

        // 未处理异常的兜底：界面线程上的异常记日志、提示后继续运行；其他线程上的只能记日志（进程随后会退出）
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportUiException(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AppLog.Crash(ex, "后台线程");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Crash(e.Exception, "未等待的任务");
            e.SetObserved();
        };

        // 高 DPI 模式由项目文件的 ApplicationHighDpiMode（PerMonitorV2）生成
        ApplicationConfiguration.Initialize();
        AppLog.Write($"启动 {typeof(Program).Assembly.GetName().Version}，参数：{string.Join(' ', args)}");
        Application.Run(new MainForm(AppOptions.Parse(args)));
        AppLog.Write("退出");
        return 0;
    }

    private static void ReportUiException(Exception ex)
    {
        var report = AppLog.Crash(ex, "界面");
        MessageBox.Show(
            L.F("DiskThinner 遇到了一个没有预料到的问题，这次操作没有完成。程序可以继续使用；如果反复出现，请重启程序。\n\n{0}: {1}{2}", ex.GetType().Name, ex.Message, (report is null ? "" : L.F("\n\n详细信息已保存到：\n{0}", report))),
            "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}

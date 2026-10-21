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

        // 高 DPI 模式由项目文件的 ApplicationHighDpiMode（PerMonitorV2）生成
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(AppOptions.Parse(args)));
        return 0;
    }
}

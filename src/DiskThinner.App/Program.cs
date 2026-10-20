namespace DiskThinner.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 高 DPI 模式由项目文件的 ApplicationHighDpiMode（PerMonitorV2）生成
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

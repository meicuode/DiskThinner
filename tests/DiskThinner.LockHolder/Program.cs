// 测试用的"占用文件的程序"：独占打开一个文件（不允许删除），显示一个不在任务栏、在屏幕外的顶层窗口，
// 等 Restart Manager 请它关闭。
//   DiskThinner.LockHolder.exe <要占用的文件> <日志文件> close|refuse
//   close：同意关闭（WM_QUERYENDSESSION 返回 TRUE），收到 WM_ENDSESSION 后释放文件、正常退出（退出码 0）
//   refuse：拒绝关闭（WM_QUERYENDSESSION 返回 FALSE），继续占用
// 日志文件里按顺序记下 ready、query <lParam>、end <wParam>、close、session-ending / session-ended（SystemEvents）、exit <退出码>，
// 测试据此判断收到了什么。窗口必须是普通的顶层窗口（任务栏里有按钮）：工具窗口不会被 Restart Manager 询问。
// 60 秒后自己退出（退出码 2），测试出错时也不会一直留着。
namespace DiskThinner.LockHolder;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[2] is not ("close" or "refuse"))
        {
            return 1;
        }

        using var file = new FileStream(args[0], FileMode.Open, FileAccess.Read, FileShare.Read);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var form = new HolderForm(file, args[1], args[2] == "refuse");
        Application.Run(form);
        File.AppendAllText(args[1], "exit " + form.ExitCode + Environment.NewLine);
        return form.ExitCode;
    }

    private sealed class HolderForm : Form
    {
        private const int WmClose = 0x0010;
        private const int WmQueryEndSession = 0x0011;
        private const int WmEndSession = 0x0016;
        private readonly FileStream _file;
        private readonly string _log;
        private readonly bool _refuse;
        private readonly System.Windows.Forms.Timer _timeout = new() { Interval = 60_000 };

        public HolderForm(FileStream file, string log, bool refuse)
        {
            _file = file;
            _log = log;
            _refuse = refuse;
            Text = "DiskThinner LockHolder";
            StartPosition = FormStartPosition.Manual;
            Microsoft.Win32.SystemEvents.SessionEnding += (_, e) => Log("session-ending " + e.Reason);
            Microsoft.Win32.SystemEvents.SessionEnded += (_, e) => Log("session-ended " + e.Reason);
            Bounds = new Rectangle(-32000, -32000, 120, 80);
            _timeout.Tick += (_, _) =>
            {
                ExitCode = 2;
                Close();
            };
        }

        public int ExitCode { get; private set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _timeout.Start();
            Log("ready");
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WmClose:
                    Log("close");
                    if (_refuse)
                    {
                        return; // 像有未保存内容、用户点了"取消"的程序一样不关
                    }

                    break;
                case WmQueryEndSession:
                    Log("query " + m.LParam);
                    m.Result = _refuse ? 0 : 1;
                    return;
                case WmEndSession:
                    Log("end " + m.WParam);
                    if (m.WParam != 0)
                    {
                        _file.Dispose();
                        _timeout.Stop();
                        ExitCode = 0;
                        BeginInvoke(Close);
                    }

                    m.Result = 0;
                    return;
            }

            base.WndProc(ref m);
        }

        private void Log(string line) => File.AppendAllText(_log, line + Environment.NewLine);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timeout.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

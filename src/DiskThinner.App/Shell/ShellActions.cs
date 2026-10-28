using System.ComponentModel;
using System.Diagnostics;
using DiskThinner.Core;

namespace DiskThinner.App.Shell;

/// <summary>与系统交互的操作：打开资源管理器、复制路径、以管理员身份重启。</summary>
internal static class ShellActions
{
    private const int ErrorCancelled = 1223; // 用户在 UAC 提示里点了"否"

    /// <summary>
    /// 用资源管理器打开目录；如果是文件，打开所在目录并选中它。只做显示，不修改任何东西。
    /// 参数按数组传递，不拼接命令行字符串。
    /// </summary>
    public static bool OpenLocation(string path, bool isDirectory, IWin32Window? owner)
    {
        if (isDirectory ? !Directory.Exists(path) : !File.Exists(path))
        {
            MessageBox.Show(owner, L.F("找不到：\n{0}\n\n可能已被删除或移动，请重新扫描。", path), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (isDirectory)
        {
            psi.ArgumentList.Add(path);
        }
        else
        {
            psi.ArgumentList.Add("/select," + path);
        }

        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(owner, L.F("无法打开资源管理器：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    public static void CopyPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // 剪贴板被其他程序占用时忽略
        }
    }

    /// <summary>
    /// 以管理员身份重新启动自己并退出当前进程。用户取消 UAC 时返回 false。
    /// <paramref name="extraArgs"/> 追加在后面（例如写入监控的 --start-monitor 4 --monitor-mode precise），逐个传参。
    /// </summary>
    public static bool RestartElevated(string? drive, IWin32Window? owner, IEnumerable<string>? extraArgs = null)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        if (!string.IsNullOrEmpty(drive))
        {
            psi.ArgumentList.Add("--scan");
            psi.ArgumentList.Add(drive);
        }

        foreach (var a in extraArgs ?? [])
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var _ = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(owner, L.F("无法以管理员身份启动：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        Application.Exit();
        return true;
    }
}

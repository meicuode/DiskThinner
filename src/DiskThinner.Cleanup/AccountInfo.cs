using System.Runtime.InteropServices;
using System.Security.Principal;
using DiskThinner.Cleanup.Native;

namespace DiskThinner.Cleanup;

/// <summary>
/// 当前进程的账户和登录会话的账户（研发方案 3.6 节"提权与账户"）。
/// 用另一个管理员账户提权时，提权后的 %USERPROFILE% 指向那个管理员账户：这时用户目录的清理和官方命令都会落到别人名下，必须禁用。
/// </summary>
public sealed record AccountInfo(string ProcessUser, string? SessionUser, bool Elevated)
{
    /// <summary>进程账户和登录会话的账户不是同一个。</summary>
    public bool Mismatch => SessionUser is not null && !string.Equals(ProcessUser, SessionUser, StringComparison.OrdinalIgnoreCase);

    /// <summary>界面上说明为什么禁用了用户目录的清理。</summary>
    public string? MismatchText => Mismatch
        ? $"DiskThinner 以 {ProcessUser} 的身份运行，而登录的是 {SessionUser}。为避免清到别人的目录，这次不清理用户目录、不运行官方命令，只清理系统位置。"
        : null;

    public static AccountInfo Current()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        return new AccountInfo(identity.Name, SessionUserName(), elevated);
    }

    /// <summary>当前登录会话的用户（DOMAIN\user）；查不到时返回 null（例如服务会话）。</summary>
    internal static string? SessionUserName()
    {
        var user = Query(Win32.WtsUserName);
        if (string.IsNullOrEmpty(user))
        {
            return null;
        }

        var domain = Query(Win32.WtsDomainName);
        return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
    }

    private static string? Query(int infoClass)
    {
        if (!Win32.WtsQuerySessionInformation(0, Win32.WtsCurrentSession, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Win32.WtsFreeMemory(buffer);
        }
    }
}

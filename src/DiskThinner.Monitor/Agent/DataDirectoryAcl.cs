using System.Security.AccessControl;
using System.Security.Principal;

namespace DiskThinner.Monitor.Agent;

/// <summary>
/// 服务数据目录的访问权限（docs/phase2b-design.md 5.9 节）：不继承，只有 SYSTEM 和 Administrators 完全控制，所有者是 Administrators（或 SYSTEM）。
/// ProgramData 默认允许普通用户新建目录，所以要防"预先放好的目录"：安装时 <see cref="Apply"/>，服务每次启动时 <see cref="Check"/>。
/// </summary>
public static class DataDirectoryAcl
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>服务的数据目录：%ProgramData%\DiskThinner\Agent。</summary>
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DiskThinner", "Agent");

    /// <summary>权限是不是上面说的样子；不是时返回原因（不含路径）。目录不存在时返回 "missing"。</summary>
    public static string? Check(string dir)
    {
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
        {
            return "missing";
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return "reparse point";
        }

        var security = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (owner != Administrators && owner != System))
        {
            return "owner";
        }

        if (!security.AreAccessRulesProtected)
        {
            return "inherited";
        }

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid || (sid != System && sid != Administrators))
            {
                return "rule " + rule.IdentityReference.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// 建目录（不存在时）并设成上面的权限：所有者 Administrators，不继承，SYSTEM 和 Administrators 完全控制（对子目录和文件生效），
    /// 已有的子项改成继承这个目录的权限。需要管理员。返回 true 表示原来的权限不对、被重设了（安装日志和界面写明）。
    /// </summary>
    public static bool Apply(string dir)
    {
        var existed = Directory.Exists(dir);
        var wasBad = existed && Check(dir) is not null;
        var info = Directory.CreateDirectory(dir);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("data directory is a reparse point");
        }

        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        // 已有的子项：所有者改成 Administrators，去掉显式规则，只继承上面的（不跟随链接）
        foreach (var child in info.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            if (child is DirectoryInfo d)
            {
                var s = new DirectorySecurity();
                s.SetOwner(Administrators);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                d.SetAccessControl(s);
            }
            else if (child is FileInfo f)
            {
                var s = new FileSecurity();
                s.SetOwner(Administrators);
                s.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                f.SetAccessControl(s);
            }
        }

        return wasBad;
    }
}

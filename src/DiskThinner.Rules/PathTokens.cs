namespace DiskThinner.Rules;

/// <summary>
/// 展开路径令牌时用到的系统位置。用户相关的令牌（%USERPROFILE%、%LOCALAPPDATA% 等）展开成
/// "用户目录根\*\相对路径"，对盘上所有用户的目录都生效（扫描的是整个盘，不只是当前用户）。
/// </summary>
public sealed record PathContext(
    string SystemDrive,
    string ProfilesRoot,
    string WindowsDir,
    string ProgramData,
    string ProgramFiles,
    string ProgramFilesX86)
{
    /// <summary>典型的 Windows 布局，测试和规则校验用。</summary>
    public static readonly PathContext Default = new(@"C:", @"C:\Users", @"C:\Windows", @"C:\ProgramData", @"C:\Program Files", @"C:\Program Files (x86)");

    /// <summary>从当前系统读取。</summary>
    public static PathContext FromSystem()
    {
        static string Folder(Environment.SpecialFolder f, string fallback)
        {
            var v = Environment.GetFolderPath(f);
            return string.IsNullOrEmpty(v) ? fallback : v.TrimEnd('\\');
        }

        var windows = Folder(Environment.SpecialFolder.Windows, @"C:\Windows");
        var drive = Path.GetPathRoot(windows)?.TrimEnd('\\') ?? "C:";
        var profile = Folder(Environment.SpecialFolder.UserProfile, drive + @"\Users\Default");
        return new PathContext(
            drive,
            Path.GetDirectoryName(profile) ?? drive + @"\Users",
            windows,
            Folder(Environment.SpecialFolder.CommonApplicationData, drive + @"\ProgramData"),
            Folder(Environment.SpecialFolder.ProgramFiles, drive + @"\Program Files"),
            Folder(Environment.SpecialFolder.ProgramFilesX86, drive + @"\Program Files (x86)"));
    }
}

public static class PathTokens
{
    /// <summary>规则里允许使用的令牌。路径必须以其中一个开头。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "%USERPROFILE%", "%LOCALAPPDATA%", "%APPDATA%", "%LOCALLOW%",
        "%PROGRAMDATA%", "%WINDIR%", "%SYSTEMDRIVE%", "%PROGRAMFILES%", "%PROGRAMFILES(X86)%",
    ];

    public static string? LeadingToken(string pattern)
    {
        foreach (var t in All)
        {
            if (pattern.StartsWith(t, StringComparison.OrdinalIgnoreCase) &&
                (pattern.Length == t.Length || pattern[t.Length] == '\\'))
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>
    /// 保护名单的模式：内置项和规则一样以令牌开头；用户在设置里追加的保护目录可以是任意盘上的绝对路径（不含通配符）。
    /// </summary>
    public static string ExpandProtection(string pattern, PathContext ctx) =>
        LeadingToken(pattern) is null && Path.IsPathFullyQualified(pattern) && !pattern.StartsWith(@"\\", StringComparison.Ordinal)
            ? pattern.TrimEnd('\\')
            : Expand(pattern, ctx);

    /// <summary>把开头的令牌展开成绝对路径模式；没有已知令牌时抛出异常。</summary>
    public static string Expand(string pattern, PathContext ctx)
    {
        var token = LeadingToken(pattern) ?? throw new FormatException($"路径必须以已知令牌开头：{pattern}");
        var rest = pattern[token.Length..];
        var users = ctx.ProfilesRoot + @"\*";
        var prefix = token.ToUpperInvariant() switch
        {
            "%USERPROFILE%" => users,
            "%LOCALAPPDATA%" => users + @"\AppData\Local",
            "%APPDATA%" => users + @"\AppData\Roaming",
            "%LOCALLOW%" => users + @"\AppData\LocalLow",
            "%PROGRAMDATA%" => ctx.ProgramData,
            "%WINDIR%" => ctx.WindowsDir,
            "%SYSTEMDRIVE%" => ctx.SystemDrive,
            "%PROGRAMFILES%" => ctx.ProgramFiles,
            _ => ctx.ProgramFilesX86,
        };
        return prefix + rest;
    }
}

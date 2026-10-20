namespace DiskThinner.Rules;

public enum ProtectionLevel : byte
{
    None = 0,

    /// <summary>软保护（文档、桌面、下载等用户目录）：风险至少按"丢用户数据"处理，只有实测过的规则才能给出清理建议。</summary>
    Soft = 1,

    /// <summary>只能用系统设置或官方工具处理（分页文件、WinSxS、还原点）。</summary>
    Guide = 2,

    /// <summary>硬保护：凭据、密钥、程序文件、系统目录。任何规则都不能让它变成可删。</summary>
    Hard = 3,
}

/// <summary>保护名单中的一项。<see cref="ProtectionLevel.None"/> 表示"例外"：在受保护的目录里放开一个子目录（如 Windows\Temp）。</summary>
public sealed record ProtectionEntry(string Pattern, ProtectionLevel Level, string Reason);

/// <summary>
/// 写死在程序里的保护名单，优先级高于任何规则；用户只能追加，不能删除。
/// 同一个节点被多项命中时，取最深（最具体）的一项，所以例外项能在受保护的目录里放开子目录。
/// </summary>
public static class ProtectionList
{
    public static readonly IReadOnlyList<ProtectionEntry> BuiltIn =
    [
        // ---- 凭据与密钥 ----
        new(@"%USERPROFILE%\.ssh", ProtectionLevel.Hard, "SSH 密钥"),
        new(@"%USERPROFILE%\.gnupg", ProtectionLevel.Hard, "GPG 密钥"),
        new(@"%USERPROFILE%\.aws", ProtectionLevel.Hard, "AWS 凭据"),
        new(@"%USERPROFILE%\.azure", ProtectionLevel.Hard, "Azure 凭据"),
        new(@"%USERPROFILE%\.kube", ProtectionLevel.Hard, "Kubernetes 凭据"),
        new(@"%USERPROFILE%\.docker\config.json", ProtectionLevel.Hard, "Docker 登录凭据"),
        new(@"%USERPROFILE%\.git-credentials", ProtectionLevel.Hard, "Git 凭据"),
        new(@"%USERPROFILE%\.netrc", ProtectionLevel.Hard, "网络登录凭据"),
        new(@"%USERPROFILE%\_netrc", ProtectionLevel.Hard, "网络登录凭据"),
        new(@"%USERPROFILE%\.npmrc", ProtectionLevel.Hard, "npm 配置（可能含访问令牌）"),
        new(@"%USERPROFILE%\.pypirc", ProtectionLevel.Hard, "PyPI 凭据"),
        new(@"%USERPROFILE%\.config\gh", ProtectionLevel.Hard, "GitHub CLI 登录凭据"),
        new(@"%USERPROFILE%\.claude\.credentials.json", ProtectionLevel.Hard, "Claude Code 登录凭据"),
        new(@"%USERPROFILE%\.codex\auth.json", ProtectionLevel.Hard, "Codex CLI 登录凭据"),
        new(@"%USERPROFILE%\.gemini\oauth_creds.json", ProtectionLevel.Hard, "Gemini CLI 登录凭据"),
        new(@"%APPDATA%\Microsoft\Credentials", ProtectionLevel.Hard, "Windows 凭据管理器"),
        new(@"%LOCALAPPDATA%\Microsoft\Credentials", ProtectionLevel.Hard, "Windows 凭据管理器"),
        new(@"%APPDATA%\Microsoft\Protect", ProtectionLevel.Hard, "DPAPI 主密钥，删除后加密数据无法解密"),
        new(@"%APPDATA%\Microsoft\Crypto", ProtectionLevel.Hard, "证书私钥"),
        new(@"%APPDATA%\Microsoft\SystemCertificates", ProtectionLevel.Hard, "个人证书"),
        new(@"%LOCALAPPDATA%\Microsoft\Vault", ProtectionLevel.Hard, "Windows 凭据保管库"),

        // ---- 浏览器登录数据（Chromium 系与 Firefox） ----
        new(@"%LOCALAPPDATA%\**\User Data\Local State", ProtectionLevel.Hard, "浏览器加密密钥"),
        new(@"%LOCALAPPDATA%\**\User Data\*\{Login Data,Login Data For Account,Web Data,Cookies}", ProtectionLevel.Hard, "浏览器保存的密码、Cookie 和自动填充"),
        new(@"%LOCALAPPDATA%\**\User Data\*\Network\Cookies", ProtectionLevel.Hard, "浏览器 Cookie"),
        new(@"%APPDATA%\Mozilla\Firefox\Profiles\*\{logins.json,key4.db,cookies.sqlite,cert9.db}", ProtectionLevel.Hard, "Firefox 保存的密码、Cookie 和证书"),

        // ---- 加密货币钱包 ----
        new(@"%APPDATA%\{Bitcoin,Ethereum,Electrum,Exodus,Monero,Litecoin}", ProtectionLevel.Hard, "加密货币钱包"),
        new(@"%USERPROFILE%\**\wallet.dat", ProtectionLevel.Hard, "加密货币钱包"),

        // ---- 用户配置单元 ----
        new(@"%USERPROFILE%\NTUSER.DAT*", ProtectionLevel.Hard, "用户注册表"),
        new(@"%USERPROFILE%\ntuser.ini", ProtectionLevel.Hard, "用户注册表"),
        new(@"%LOCALAPPDATA%\Microsoft\Windows\UsrClass.dat*", ProtectionLevel.Hard, "用户注册表"),

        // ---- 用户文件（软保护） ----
        new(@"%USERPROFILE%\{Documents,Desktop,Pictures,Videos,Music,Downloads,Favorites,Contacts,Links,Searches,Saved Games,3D Objects}", ProtectionLevel.Soft, "用户自己的文件"),
        new(@"%USERPROFILE%\OneDrive*", ProtectionLevel.Soft, "OneDrive 同步的文件"),
        new(@"%USERPROFILE%\source", ProtectionLevel.Soft, "代码仓库"),

        // ---- 系统 ----
        new(@"%WINDIR%", ProtectionLevel.Hard, "Windows 系统目录"),
        new(@"%PROGRAMFILES%", ProtectionLevel.Hard, "程序文件，请通过卸载程序删除"),
        new(@"%PROGRAMFILES(X86)%", ProtectionLevel.Hard, "程序文件，请通过卸载程序删除"),
        new(@"%SYSTEMDRIVE%\Recovery", ProtectionLevel.Hard, "系统恢复环境"),
        new(@"%SYSTEMDRIVE%\Boot", ProtectionLevel.Hard, "启动文件"),
        new(@"%SYSTEMDRIVE%\bootmgr", ProtectionLevel.Hard, "启动文件"),
        new(@"%SYSTEMDRIVE%\{pagefile.sys,hiberfil.sys,swapfile.sys}", ProtectionLevel.Guide, "在系统设置里调整"),
        new(@"%SYSTEMDRIVE%\System Volume Information", ProtectionLevel.Guide, "系统还原点，在\u201c系统保护\u201d里调整"),
        new(@"%WINDIR%\WinSxS", ProtectionLevel.Guide, "组件存储，只能用 DISM 清理"),
        new(@"%WINDIR%\Installer", ProtectionLevel.Guide, "安装程序缓存，删除后无法修复或卸载程序"),
        new(@"%PROGRAMDATA%\Microsoft\Windows Defender", ProtectionLevel.Hard, "Microsoft Defender"),
        new(@"%PROGRAMDATA%\Package Cache", ProtectionLevel.Guide, "安装包缓存，删除后可能无法修复或卸载程序"),

        // ---- Windows 目录里允许清理的位置（例外） ----
        new(@"%WINDIR%\Temp", ProtectionLevel.None, ""),
        new(@"%WINDIR%\LiveKernelReports", ProtectionLevel.None, ""),
        new(@"%WINDIR%\Minidump", ProtectionLevel.None, ""),
        new(@"%WINDIR%\MEMORY.DMP", ProtectionLevel.None, ""),
        new(@"%WINDIR%\ServiceProfiles\*\AppData\Local\Temp", ProtectionLevel.None, ""),
        new(@"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache", ProtectionLevel.None, ""),
        new(@"%WINDIR%\SoftwareDistribution\Download", ProtectionLevel.None, ""),
        new(@"%WINDIR%\Microsoft.NET\*\*\Temporary ASP.NET Files", ProtectionLevel.None, ""),
    ];

    public static string Display(ProtectionLevel level) => level switch
    {
        ProtectionLevel.Hard => "保护名单",
        ProtectionLevel.Guide => "系统设置",
        ProtectionLevel.Soft => "用户文件",
        _ => "",
    };
}

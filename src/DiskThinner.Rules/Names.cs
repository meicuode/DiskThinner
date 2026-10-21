namespace DiskThinner.Rules;

/// <summary>枚举在规则文件里的写法和界面上的中文名。</summary>
public static class Names
{
    public static string Code(Risk risk) => risk switch
    {
        Risk.Safe => "safe",
        Risk.Low => "low",
        Risk.Medium => "medium",
        Risk.High => "high",
        Risk.Guide => "guide",
        _ => "never",
    };

    public static string Display(Risk risk) => risk switch
    {
        Risk.Safe => "安全",
        Risk.Low => "需重新下载",
        Risk.Medium => "丢历史或登录态",
        Risk.High => "丢用户数据",
        Risk.Guide => "系统工具处理",
        _ => "不能删",
    };

    public static string Code(Kind kind) => kind switch
    {
        Kind.Cache => "cache",
        Kind.Temp => "temp",
        Kind.Log => "log",
        Kind.CrashDump => "crash-dump",
        Kind.UpdateCache => "update-cache",
        Kind.PackageCache => "package-cache",
        Kind.BuildOutput => "build-output",
        Kind.Model => "model",
        Kind.VmDisk => "vm-disk",
        Kind.SessionHistory => "session-history",
        Kind.BrowserData => "browser-data",
        Kind.AppData => "app-data",
        Kind.UserData => "user-data",
        Kind.Config => "config",
        Kind.Credential => "credential",
        Kind.Program => "program",
        Kind.System => "system",
        Kind.Backup => "backup",
        _ => "recycle-bin",
    };

    public static string Display(Kind kind) => kind switch
    {
        Kind.Cache => "缓存",
        Kind.Temp => "临时文件",
        Kind.Log => "日志",
        Kind.CrashDump => "崩溃转储",
        Kind.UpdateCache => "更新缓存",
        Kind.PackageCache => "包缓存",
        Kind.BuildOutput => "构建产物",
        Kind.Model => "模型文件",
        Kind.VmDisk => "虚拟磁盘",
        Kind.SessionHistory => "会话记录",
        Kind.BrowserData => "浏览器数据",
        Kind.AppData => "应用数据",
        Kind.UserData => "用户数据",
        Kind.Config => "配置",
        Kind.Credential => "凭据",
        Kind.Program => "程序",
        Kind.System => "系统",
        Kind.Backup => "备份",
        _ => "回收站",
    };

    /// <summary>
    /// 一个位置的简短标题：规则"作用"的第一句（如"npm 下载过的包""当前用户的临时文件"），比类别名更能说明是什么；
    /// 太长时退回"归属 + 类别"。概览、清理计划、操作记录共用。
    /// </summary>
    public static string Title(LocationRule rule, Owner? owner)
    {
        // 去掉括号里的补充说明，再取第一句："ASP.NET（.NET Framework）网站的编译缓存，……" → "ASP.NET网站的编译缓存"
        var text = System.Text.RegularExpressions.Regex.Replace(rule.Purpose, "（[^）]*）", "");
        var cut = text.IndexOfAny(['，', '。', '、']);
        var first = (cut > 0 ? text[..cut] : text).Trim();
        if (first.Length is > 0 and <= 30)
        {
            return first;
        }

        return (owner is { Key: not "f:user" and not "f:several" and not "f:projects" } ? owner.Display + " " : "") + Display(rule.Kind);
    }

    public static string Code(Tier tier) => tier switch
    {
        Tier.Draft => "draft",
        Tier.Reviewed => "reviewed",
        _ => "verified",
    };

    public static string Code(MatchMode mode) => mode switch
    {
        MatchMode.Dir => "dir",
        MatchMode.File => "file",
        _ => "under",
    };

    public static bool TryParse(string code, out Risk value) => TryParse(code, Enum.GetValues<Risk>(), Code, out value);

    public static bool TryParse(string code, out Kind value) => TryParse(code, Enum.GetValues<Kind>(), Code, out value);

    public static bool TryParse(string code, out Tier value) => TryParse(code, Enum.GetValues<Tier>(), Code, out value);

    public static bool TryParse(string code, out MatchMode value) => TryParse(code, Enum.GetValues<MatchMode>(), Code, out value);

    private static bool TryParse<T>(string code, T[] values, Func<T, string> toCode, out T value) where T : struct
    {
        foreach (var v in values)
        {
            if (string.Equals(toCode(v), code, StringComparison.Ordinal))
            {
                value = v;
                return true;
            }
        }

        value = default;
        return false;
    }
}

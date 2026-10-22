using DiskThinner.Cleanup.Native;
using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.Cleanup;

public enum VerifyStatus
{
    Ok,

    /// <summary>位置已经不在了（被别的程序或用户删掉了）。</summary>
    Missing,

    /// <summary>复核没通过，不执行。</summary>
    Refused,
}

public readonly record struct VerifyResult(VerifyStatus Status, string? Reason, ProtectionScope? Scope = null, bool AllowSoft = false)
{
    public static VerifyResult Refuse(string reason) => new(VerifyStatus.Refused, reason);
}

/// <summary>
/// 执行前逐项复核（研发方案 3.6 节），防止"确认之后、执行之前"路径被替换：
/// 1. 路径上任何一级都不是重解析点（junction、符号链接、挂载点、云文件占位符）；
/// 2. 再查一次保护名单；
/// 3. 这个位置仍然命中当初的规则（按磁盘上现在的内容判断条件）；
/// 另外：不碰盘的根目录和系统的顶层目录；处理方式必须是计划里给出的那几种之一；账户不一致时不碰用户目录、不运行命令。
/// </summary>
internal static class Verifier
{
    public static VerifyResult Verify(PlanItem item, RuleEngine engine, ProtectionCheck protection, ExecutorOptions options)
    {
        var path = item.Path;
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
        {
            return VerifyResult.Refuse(L.T("路径格式不对"));
        }

        if (IsStructural(path, engine.Context))
        {
            return VerifyResult.Refuse(L.T("不能清理盘的根目录或系统的顶层目录"));
        }

        if (options.RequiredRoot is { } required && !PlanBuilder.IsUnder(path, required))
        {
            return VerifyResult.Refuse(L.T("不在允许清理的目录里"));
        }

        if (item.Method is not { } method || !item.Methods.Contains(method) || method.Kind == MethodKind.Manual)
        {
            return VerifyResult.Refuse(L.T("处理方式不在计划给出的范围里"));
        }

        if (item.RuleIndex < 0 || item.RuleIndex >= engine.Rules.Locations.Count || !ReferenceEquals(engine.Rules.Locations[item.RuleIndex], item.Rule))
        {
            return VerifyResult.Refuse(L.T("规则已经变了，请重新扫描"));
        }

        var rule = item.Rule!;
        switch (method.Kind)
        {
            case MethodKind.Delete when method.Delete is null || !rule.Actions.Contains(method.Delete):
                return VerifyResult.Refuse(L.T("删除方式不是规则给出的"));
            case MethodKind.Delete when method.Delete.OlderThanDays is { } ruleDays && (method.MinAgeDays is not { } days || days < ruleDays):
                return VerifyResult.Refuse(L.T("时间条件比规则宽松"));
            case MethodKind.Command when method.Command is null || !CommandRunner.IsWhitelisted(method.Command) ||
                                         !rule.Actions.OfType<CommandAction>().Any(a => a.CommandId == method.Command.Id):
                return VerifyResult.Refuse(L.T("命令不在白名单里，或不是规则给出的"));
        }

        if (options.Account is { Mismatch: true } && (method.Kind == MethodKind.Command || PlanBuilder.IsUnder(path, engine.Context.ProfilesRoot)))
        {
            return VerifyResult.Refuse(L.T("提权账户和登录用户不一致"));
        }

        // 1. 从盘的根目录往下逐级看属性：任何一级是重解析点都不执行
        var root = Path.GetPathRoot(path)!;
        var parts = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var prefix = root.TrimEnd('\\');
        for (var i = 0; i < parts.Length; i++)
        {
            prefix += "\\" + parts[i];
            var attributes = Win32.GetFileAttributes(Win32.Extended(prefix));
            if (attributes == Win32.InvalidFileAttributes)
            {
                return new VerifyResult(VerifyStatus.Missing, L.T("已经不在了"));
            }

            if ((attributes & Win32.FileAttributeReparsePoint) != 0)
            {
                return VerifyResult.Refuse(L.F("路径上的 {0} 是链接或占位符（junction、符号链接、云文件），为防止删到别处，没有执行", prefix));
            }

            var isLast = i == parts.Length - 1;
            if (!isLast && (attributes & Win32.FileAttributeDirectory) == 0)
            {
                return VerifyResult.Refuse(L.T("路径结构变了"));
            }

            if (isLast && ((attributes & Win32.FileAttributeDirectory) != 0) != item.IsDirectory)
            {
                return VerifyResult.Refuse(item.IsDirectory ? L.T("原来是目录，现在变成了文件") : L.T("原来是文件，现在变成了目录"));
            }
        }

        // 2. 保护名单
        var allowSoft = rule.Tier == Tier.Verified;
        if (protection.EntryFor(path) is { } entry &&
            (entry.Level is ProtectionLevel.Hard or ProtectionLevel.Guide || (entry.Level == ProtectionLevel.Soft && !allowSoft)))
        {
            return VerifyResult.Refuse(L.F("保护名单：{0}", entry.Reason));
        }

        // 3. 仍然命中当初的规则
        if (!engine.StillMatches(item.RuleIndex, path, item.IsDirectory))
        {
            return VerifyResult.Refuse(L.F("规则 {0} 不再命中这个位置（可能被移动或替换），没有执行", rule.Id));
        }

        return new VerifyResult(VerifyStatus.Ok, null, protection.ScopeBelow(path), allowSoft);
    }

    /// <summary>盘的根目录、用户目录的根和每个用户的目录、Windows、ProgramData、Program Files：任何规则都不能清理它们本身。</summary>
    internal static bool IsStructural(string path, PathContext context)
    {
        var p = path.TrimEnd('\\');
        if (p.Length <= 2 || Path.GetPathRoot(p)?.TrimEnd('\\').Length == p.Length)
        {
            return true;
        }

        string[] tops = [context.SystemDrive, context.ProfilesRoot, context.WindowsDir, context.ProgramData, context.ProgramFiles, context.ProgramFilesX86];
        if (tops.Any(t => string.Equals(t.TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(Path.GetDirectoryName(p), context.ProfilesRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}

using System.Text.RegularExpressions;
using DiskThinner.Rules;

namespace DiskThinner.RuleTools;

/// <summary>
/// 规则的语义校验（研发方案 6.1 节）。错误会阻止编译；警告只提示。
/// 这些检查是"写规则时就拦住"的第一道关，客户端运行时的保护名单是第二道关。
/// </summary>
public static partial class RuleValidator
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvName();

    public static (List<RuleIssue> Errors, List<RuleIssue> Warnings) Validate(RuleSet rules, IEnumerable<RuleIssue> loadIssues)
    {
        var errors = new List<RuleIssue>(loadIssues);
        var warnings = new List<RuleIssue>();
        var ctx = PathContext.Default;
        var protection = new ProtectionCheck(ctx);

        foreach (var dup in rules.Apps.GroupBy(a => a.Id).Where(g => g.Count() > 1))
        {
            errors.Add(new RuleIssue("(多个文件)", dup.Key, "app id 重复"));
        }

        foreach (var dup in rules.Locations.GroupBy(r => r.Id).Where(g => g.Count() > 1))
        {
            errors.Add(new RuleIssue(string.Join(", ", dup.Select(r => r.SourceFile).Distinct()), dup.Key, "规则 id 重复"));
        }

        var appIds = rules.Apps.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var r in rules.Locations)
        {
            var file = r.SourceFile ?? "?";
            void Error(string m) => errors.Add(new RuleIssue(file, r.Id, m));
            void Warn(string m) => warnings.Add(new RuleIssue(file, r.Id, m));

            if (r.AppId is { } app && !appIds.Contains(app))
            {
                Error($"app 引用了没有定义的应用：{app}");
            }

            // ---- 路径 ----
            var token = PathTokens.LeadingToken(r.Pattern);
            if (token is null)
            {
                Error($"路径必须以已知令牌开头（{string.Join(" ", PathTokens.All)}）：{r.Pattern}");
                continue;
            }

            var rest = r.Pattern[token.Length..].Trim('\\');
            var segments = rest.Length == 0 ? [] : rest.Split('\\');
            // 可清理的规则（或带删除、命令动作的规则）不能直接覆盖整个系统位置；只做说明的 never / guide / high 规则可以
            var actsOnFiles = r.Risk <= Risk.Medium || r.Actions.Any(a => a is DeleteAction or CommandAction);
            if (actsOnFiles && (segments.Length == 0 || segments.All(s => s == "**")))
            {
                Error("可清理的规则在令牌后面至少还要有一层具体的目录或文件，不能直接匹配整个系统位置");
            }

            if (segments.Any(s => s is ".." or "." || s.Length == 0))
            {
                Error("路径里不能有 .. 、. 或空的一段");
            }

            if (segments.Count(s => s == "**") > 1)
            {
                Error("** 最多出现一次");
            }

            if (segments.Any(s => s.Contains("**", StringComparison.Ordinal) && s != "**"))
            {
                Error("** 必须单独占一段");
            }

            string expanded;
            try
            {
                expanded = PathTokens.Expand(r.Pattern, ctx);
                CompiledPattern.Compile(expanded);
            }
            catch (FormatException ex)
            {
                Error(ex.Message);
                continue;
            }

            // ---- 文字 ----
            if (string.IsNullOrWhiteSpace(r.Purpose))
            {
                Error("缺少 purpose（作用）");
            }

            if (string.IsNullOrWhiteSpace(r.Impact))
            {
                Error("缺少 impact（删除后的影响）");
            }

            // ---- 风险与类别 ----
            if (r.Kind == Kind.Credential && r.Risk != Risk.Never)
            {
                Error("凭据（credential）的风险必须是 never");
            }

            if (r.Kind is Kind.Program or Kind.System && r.Risk is not (Risk.Never or Risk.Guide or Risk.Low))
            {
                Error("程序和系统文件（program / system）的风险只能是 never、guide 或 low（可以重新下载的工具链）");
            }

            if (r.Risk is Risk.Safe or Risk.Low && r.Evidence.Count == 0 && r.Tier != Tier.Verified)
            {
                Error("风险为 safe 或 low 的规则必须有 evidence（链接或说明），或者已实测（tier: verified）");
            }

            if (r.Tier == Tier.Draft)
            {
                if (r.Risk == Risk.Safe)
                {
                    Error("草稿（draft）不能标为 safe");
                }

                if (r.Actions.OfType<CommandAction>().Any())
                {
                    Error("草稿（draft）不能有 command 动作");
                }
            }

            // ---- 动作 ----
            foreach (var c in r.Actions.OfType<CommandAction>())
            {
                if (KnownCommands.Find(c.CommandId) is null)
                {
                    Error($"command 只能引用内置白名单里的编号：{c.CommandId}（可选：{string.Join("、", KnownCommands.All.Select(k => k.Id))}）");
                }
            }

            if (r.Actions.OfType<DeleteAction>().Any() && r.Risk > Risk.Medium)
            {
                Error("风险高于 medium 的规则不能有 delete 动作");
            }

            if (r.Actions.OfType<CommandAction>().Any() && r.Risk > Risk.Medium)
            {
                Error("风险高于 medium 的规则不能有 command 动作");
            }

            if (r.Relocate?.Env is { } env && !EnvName().IsMatch(env))
            {
                Error($"relocate.env 不是合法的环境变量名：{env}");
            }

            foreach (var c in r.Conditions.Where(c => c.Type == ConditionType.NotUnder).SelectMany(c => c.Patterns))
            {
                if (PathTokens.LeadingToken(c) is null)
                {
                    Error($"not_under 的路径必须以已知令牌开头：{c}");
                }
            }

            // ---- 与保护名单的冲突 ----
            if (r.Risk <= Risk.Medium)
            {
                foreach (var sample in ProtectionCheck.SamplePaths(expanded))
                {
                    var entry = protection.EntryFor(sample);
                    switch (entry?.Level)
                    {
                        case ProtectionLevel.Hard:
                            Error($"可清理的规则落在保护名单里（{entry.Reason}）：{sample}");
                            break;
                        case ProtectionLevel.Guide:
                            Error($"可清理的规则落在只能用系统工具处理的位置（{entry.Reason}）：{sample}");
                            break;
                        case ProtectionLevel.Soft when r.Tier != Tier.Verified:
                            Warn($"位于用户文件目录（{entry.Reason}），未实测的规则在这里最多只能按\u201c丢用户数据\u201d显示：{sample}");
                            break;
                    }
                }
            }

            if (r.Tier == Tier.Draft)
            {
                Warn("草稿规则，发布前需要人工审阅");
            }
        }

        return (errors, warnings);
    }
}

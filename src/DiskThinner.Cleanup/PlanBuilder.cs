using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.Cleanup;

/// <summary>生成清理计划时的环境。</summary>
public sealed record PlanOptions
{
    /// <summary>可清理部分小于这个值的位置不列出。</summary>
    public long MinBytes { get; init; } = 1L << 20;

    /// <summary>只能用系统工具处理的位置（分页文件、WinSxS），至少这么大才列进"已拦截"。</summary>
    public long MinGuideBytes { get; init; } = 1L << 30;

    /// <summary>当前登录用户的目录（C:\Users\me）。其他用户目录下的位置列为已拦截；null 表示不按用户区分（测试用）。</summary>
    public string? CurrentProfile { get; init; }

    public AccountInfo? Account { get; init; }

    /// <summary>官方命令的程序路径（只认已装软件探测到的）；找不到时返回 null。</summary>
    public Func<KnownCommand, string?> ResolveCommand { get; init; } = static _ => null;

    /// <summary>正在运行的程序的文件名（小写，如 "devenv.exe"）。</summary>
    public IReadOnlySet<string> RunningExes { get; init; } = new HashSet<string>();

    /// <summary>扫描能不能识别硬链接（MFT 扫描能，遍历扫描不能）。</summary>
    public bool HardLinksKnown { get; init; } = true;

    public Func<int, Owner?>? OwnerOf { get; init; }

    public DateTime NowUtc { get; init; } = DateTime.UtcNow;

    /// <summary>"已拦截"最多列出多少项。</summary>
    public int MaxBlocked { get; init; } = 30;
}

/// <summary>
/// 从规则匹配结果生成清理计划（研发方案 3.6 节）：每个可执行规则命中的位置一项，按风险分组，给出默认处理方式和预计释放。
/// 预计释放按处理方式在扫描树上模拟：扣掉硬链接共享、受保护、按时间和 keep_latest 保留的文件。
/// </summary>
public static class PlanBuilder
{
    public static CleanupPlan Build(NodeTree tree, Classification c, PlanOptions o)
    {
        var rules = c.Engine.Rules;
        var profiles = c.Engine.Context.ProfilesRoot;
        var items = new List<PlanItem>();
        var blocked = new List<PlanItem>();

        foreach (var root in c.Roots)
        {
            var rule = rules.Locations[root.Rule];
            if (rule.Mode == MatchMode.Under)
            {
                continue;
            }

            var node = root.Node;
            var path = tree.GetPath(node);
            var owner = o.OwnerOf?.Invoke(node);
            var title = Names.Title(rule, owner);
            var risk = c.EffectiveRisk(node) ?? rule.Risk;
            var actionable = Classification.IsActionable(rule);

            if (rule.Risk == Risk.Guide)
            {
                if (root.Bytes >= o.MinGuideBytes)
                {
                    blocked.Add(Blocked(tree, node, root.Rule, rule, title, root.Bytes, "系统设置中处理", "系统设置",
                        rule.Actions.OfType<GuideAction>().FirstOrDefault()?.Text));
                }

                continue;
            }

            if (!actionable || !Classification.IsCleanable(rule.Risk))
            {
                continue; // 只有说明的规则、本身就会丢用户数据的位置：不进计划
            }

            if (!Classification.IsCleanable(risk))
            {
                // 规则说可以删，但保护名单把它拦下了
                if (c.ProtectionOf(node) is { } p && root.Bytes >= o.MinBytes)
                {
                    blocked.Add(Blocked(tree, node, root.Rule, rule, title, root.Bytes, p.Reason, ProtectionList.Display(p.Level), null));
                }

                continue;
            }

            if (root.CleanableBytes < o.MinBytes)
            {
                continue;
            }

            if (UserBlock(path, profiles, o) is { } userBlock)
            {
                blocked.Add(Blocked(tree, node, root.Rule, rule, title, root.CleanableBytes, userBlock, "规则", null));
                continue;
            }

            var allowSoft = rule.Tier == Tier.Verified;
            var protectedNodes = new List<(int Node, ProtectionEntry Entry)>();
            var methods = new List<PlanMethod>();
            var hints = new List<string>();
            foreach (var action in rule.Actions)
            {
                switch (action)
                {
                    case DeleteAction d:
                        var est = Simulate(tree, c, node, d, allowSoft, o.NowUtc, methods.Any(m => m.Kind == MethodKind.Delete) ? null : protectedNodes);
                        methods.Add(new PlanMethod(MethodKind.Delete, "直接删除")
                        {
                            Delete = d,
                            EstimatedBytes = est.Bytes,
                            Files = est.Files,
                            OldestUtc = est.Oldest,
                            NewestUtc = est.Newest,
                        });
                        if (CleanupText.Filters(d) is { } filters)
                        {
                            hints.Add(filters);
                        }

                        break;
                    case CommandAction cmd when KnownCommands.Find(cmd.CommandId) is { } known:
                        if (CommandBlock(path, o) is { } why)
                        {
                            hints.Add(why);
                        }
                        else if (o.ResolveCommand(known) is { } exe)
                        {
                            methods.Add(new PlanMethod(MethodKind.Command, known.Display)
                            {
                                Command = known,
                                CommandPath = exe,
                                EstimatedBytes = c.Cleanable(node),
                            });
                        }
                        else
                        {
                            hints.Add($"没有找到 {known.Exe}，不能运行 {known.Display}");
                        }

                        break;
                }
            }

            methods.Add(PlanMethod.Manual);
            var group = risk switch
            {
                Risk.Safe => PlanGroup.Safe,
                Risk.Low => PlanGroup.Redownload,
                _ => PlanGroup.Irreversible,
            };
            var first = group == PlanGroup.Redownload
                ? methods.FirstOrDefault(m => m.Kind == MethodKind.Command) ?? methods[0]
                : methods.FirstOrDefault(m => m.Kind == MethodKind.Delete) ?? methods[0];

            if (root.SharedBytes > 0)
            {
                hints.Add($"{ByteSize.Format(root.SharedBytes)} 与其他位置用硬链接共享，删掉这里不释放");
            }

            var running = RunningApps(rules, rule, o.RunningExes);
            if (running.Count > 0)
            {
                hints.Add($"{string.Join("、", running)} 正在运行，被占用的文件会跳过");
            }

            if (protectedNodes.Count > 0)
            {
                hints.Add($"其中 {protectedNodes.Count} 处受保护，执行时跳过");
            }

            // 项目里的依赖（node_modules、虚拟环境）：规则用 ** 在用户目录里到处匹配，属于用户自己的项目，默认不勾选
            var projectDeps = rule.Kind == Kind.BuildOutput && rule.Pattern.Contains(@"\**\", StringComparison.Ordinal);
            if (projectDeps)
            {
                hints.Add("你的项目的依赖，默认不勾选");
            }

            items.Add(new PlanItem
            {
                Path = path,
                IsDirectory = tree[node].IsDirectory,
                Title = title,
                Group = group,
                Node = node,
                RuleIndex = root.Rule,
                Rule = rule,
                Risk = risk,
                Bytes = root.Bytes,
                SharedBytes = root.SharedBytes,
                Owner = owner?.Display,
                Methods = methods,
                Method = first,
                Selected = first.Kind != MethodKind.Manual && !projectDeps,
                Hint = hints.Count == 0 ? null : string.Join("；", hints),
                RunningApps = running,
            });

            foreach (var (pn, entry) in protectedNodes)
            {
                blocked.Add(new PlanItem
                {
                    Path = tree.GetPath(pn),
                    IsDirectory = tree[pn].IsDirectory,
                    Title = entry.Reason,
                    Group = PlanGroup.Blocked,
                    Node = pn,
                    Bytes = tree[pn].AllocSize,
                    BlockReason = entry.Reason,
                    Source = ProtectionList.Display(entry.Level),
                    Hint = $"在\u201c{title}\u201d里，执行时跳过",
                });
            }
        }

        var ordered = items
            .OrderBy(i => i.Group)
            .ThenByDescending(i => i.Bytes)
            .Concat(blocked.OrderByDescending(b => b.Bytes).Take(o.MaxBlocked))
            .ToList();
        string?[] warnings =
        [
            o.Account?.MismatchText,
            o.HardLinksKnown ? null : "这次是普通权限的遍历扫描，识别不了硬链接：和别处共享的文件也算进了预计释放，实际释放可能更少。以管理员身份扫描更准确。",
        ];
        var warning = string.Join("\n", warnings.OfType<string>());
        return new CleanupPlan(ordered, warning.Length == 0 ? null : warning);
    }

    private static PlanItem Blocked(NodeTree tree, int node, int ruleIndex, LocationRule rule, string title, long bytes, string reason, string source, string? hint) => new()
    {
        Path = tree.GetPath(node),
        IsDirectory = tree[node].IsDirectory,
        Title = title,
        Group = PlanGroup.Blocked,
        Node = node,
        RuleIndex = ruleIndex,
        Rule = rule,
        Risk = rule.Risk,
        Bytes = bytes,
        BlockReason = reason,
        Source = source,
        Hint = hint ?? reason,
    };

    /// <summary>用户目录下的位置：提权账户不一致时一律不清理；不是当前用户的目录也不清理。</summary>
    internal static string? UserBlock(string path, string profilesRoot, PlanOptions o)
    {
        if (!IsUnder(path, profilesRoot))
        {
            return null;
        }

        if (o.Account is { Mismatch: true })
        {
            return "提权账户和登录用户不一致，不清理用户目录";
        }

        return o.CurrentProfile is { } me && !IsUnder(path, me) ? "其他用户的目录" : null;
    }

    /// <summary>官方命令清的是当前用户自己的缓存：账户不一致、或者这个位置不在当前用户目录下时不能用。</summary>
    private static string? CommandBlock(string path, PlanOptions o)
    {
        if (o.Account is { Mismatch: true })
        {
            return "提权账户和登录用户不一致，不运行官方命令";
        }

        return o.CurrentProfile is { } me && !IsUnder(path, me) ? "官方命令只清理当前用户自己的缓存" : null;
    }

    internal static bool IsUnder(string path, string dir) =>
        path.Length > dir.Length && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && path[dir.TrimEnd('\\').Length] == '\\';

    private static List<string> RunningApps(RuleSet rules, LocationRule rule, IReadOnlySet<string> running)
    {
        if (!rule.RequiresClosed || rules.App(rule.AppId) is not { } app || running.Count == 0)
        {
            return [];
        }

        return app.Processes.Any(p => running.Contains(p.ToLowerInvariant())) ? [app.Name] : [];
    }

    internal readonly record struct Estimate(long Bytes, int Files, DateTime? Oldest, DateTime? Newest);

    /// <summary>
    /// 在扫描树上模拟一次删除，口径和 <see cref="Deleter"/> 一致：只算可清理的文件（不含硬链接共享和受保护的），
    /// older_than_days 按文件的修改时间，keep_latest 按直接子项（目录取子树里最新的修改时间）保留最新的几个。
    /// <paramref name="protectedOut"/> 收集里面受保护的最上层位置。
    /// </summary>
    internal static Estimate Simulate(NodeTree tree, Classification c, int root, DeleteAction d, bool allowSoft, DateTime nowUtc,
        List<(int Node, ProtectionEntry Entry)>? protectedOut)
    {
        var cutoff = d.OlderThanDays is { } days ? nowUtc.AddDays(-days).ToFileTimeUtc() : long.MaxValue;
        HashSet<int>? keep = null;
        if (d.KeepLatest is > 0 && tree[root].IsDirectory)
        {
            var children = new List<int>();
            for (var ch = tree[root].FirstChild; ch >= 0; ch = tree[ch].NextSibling)
            {
                children.Add(ch);
            }

            keep = children.OrderByDescending(ch => tree[ch].LastWriteUtc).Take(d.KeepLatest.Value).ToHashSet();
        }

        long bytes = 0;
        var files = 0;
        long oldest = long.MaxValue, newest = 0;
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n != root && Blocking(c.ProtectionOf(n), allowSoft) is { } entry)
            {
                // 不往受保护的位置里面走，所以到这里的都是最上层的那个
                protectedOut?.Add((n, entry));
                continue;
            }

            ref var node = ref tree[n];
            if (node.IsDirectory)
            {
                for (var ch = node.FirstChild; ch >= 0; ch = tree[ch].NextSibling)
                {
                    if (n != root || keep is null || !keep.Contains(ch))
                    {
                        stack.Push(ch);
                    }
                }
            }
            else if ((node.Flags & NodeFlags.ReparsePoint) == 0 && node.LastWriteUtc < cutoff && c.Cleanable(n) is var b && b > 0)
            {
                bytes += b;
                files++;
                oldest = Math.Min(oldest, node.LastWriteUtc);
                newest = Math.Max(newest, node.LastWriteUtc);
            }
        }

        return new Estimate(bytes, files,
            files > 0 ? DateTime.FromFileTimeUtc(oldest) : null,
            files > 0 ? DateTime.FromFileTimeUtc(newest) : null);
    }

    /// <summary>这一项保护是否会让删除跳过：硬保护、只能用系统工具处理的；软保护除非规则实测过。返回 null 表示不拦。</summary>
    private static ProtectionEntry? Blocking(ProtectionEntry? entry, bool allowSoft) =>
        entry is { Level: ProtectionLevel.Hard or ProtectionLevel.Guide } || entry is { Level: ProtectionLevel.Soft } && !allowSoft ? entry : null;
}

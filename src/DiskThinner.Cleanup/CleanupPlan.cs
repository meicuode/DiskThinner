using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.Cleanup;

/// <summary>清理计划里的分组（原型 .p-cleanup），按风险决定默认处理方式。</summary>
public enum PlanGroup
{
    /// <summary>会自动重建 · 默认直接删除。</summary>
    Safe,

    /// <summary>需重新下载 · 默认运行官方清理命令。</summary>
    Redownload,

    /// <summary>删除后不可恢复（会话记录、登录状态等）· 确认时列出明细。</summary>
    Irreversible,

    /// <summary>AI 建议 · 默认不勾选（阶段 3）。</summary>
    Ai,

    /// <summary>已拦截 · 显示但不能勾选。</summary>
    Blocked,
}

/// <summary>计划里一项的来源（"来源"列）。</summary>
public enum PlanSource
{
    /// <summary>规则给出的位置。</summary>
    Rule,

    /// <summary>保护名单拦下的（凭据、程序和系统目录、你添加的保护目录）。</summary>
    Protection,

    /// <summary>只能在系统设置里处理（分页文件、组件存储、还原点）。</summary>
    SystemSettings,

    /// <summary>软保护的用户文件（文档、桌面等）。</summary>
    UserFiles,
}

public enum MethodKind
{
    Delete,
    Command,

    /// <summary>手动清理：只用资源管理器打开，工具不删除任何东西。</summary>
    Manual,
}

/// <summary>一项的处理方式和按这种方式预计的释放量。</summary>
public sealed record PlanMethod(MethodKind Kind, string Display)
{
    public static readonly PlanMethod Manual = new(MethodKind.Manual, L.T("手动清理（打开所在位置）"));

    public DeleteAction? Delete { get; init; }

    /// <summary>实际使用的"只删 N 天前的"：规则的 older_than_days，或者设置里更严格的值。只能比规则更严格。</summary>
    public int? MinAgeDays { get; init; }

    public KnownCommand? Command { get; init; }

    /// <summary>官方命令的程序路径：已装软件探测到的那个。</summary>
    public string? CommandPath { get; init; }

    /// <summary>预计释放：扣掉硬链接共享、受保护和按过滤条件保留的部分。手动清理为 0（工具不删除）。</summary>
    public long EstimatedBytes { get; init; }

    /// <summary>预计删除的文件数（直接删除时）。</summary>
    public int Files { get; init; }

    /// <summary>预计删除的文件里最早和最晚的修改时间（UTC），"无法恢复"的明细里显示。</summary>
    public DateTime? OldestUtc { get; init; }

    public DateTime? NewestUtc { get; init; }
}

public enum PlanItemState
{
    Pending,
    Done,
    Partial,
    Failed,

    /// <summary>执行前复核没通过，或被取消。</summary>
    NotRun,

    /// <summary>手动清理后重新测量过。</summary>
    Measured,
}

/// <summary>清理计划里的一项：一个规则命中的位置。</summary>
public sealed class PlanItem
{
    public required string Path { get; init; }

    public required bool IsDirectory { get; init; }

    public required string Title { get; init; }

    public required PlanGroup Group { get; init; }

    /// <summary>扫描树里的节点（重新扫描后无效）。</summary>
    public int Node { get; init; } = -1;

    /// <summary>规则在规则集里的编号，执行前用来复核"仍然命中当初的规则"。</summary>
    public int RuleIndex { get; init; } = -1;

    public LocationRule? Rule { get; init; }

    public Risk Risk { get; init; }

    /// <summary>规则下的占用。</summary>
    public long Bytes { get; set; }

    /// <summary>和别的位置用硬链接共享的部分（删掉这里不释放）。</summary>
    public long SharedBytes { get; init; }

    /// <summary>按当前处理方式预计能释放的空间。</summary>
    public long EstimatedBytes => Method?.EstimatedBytes ?? 0;

    public string? Owner { get; init; }

    public IReadOnlyList<PlanMethod> Methods { get; init; } = [];

    public PlanMethod? Method { get; set; }

    public bool Selected { get; set; }

    public bool Selectable => Group != PlanGroup.Blocked && Methods.Count > 0;

    /// <summary>提示列：过滤条件、硬链接共享、需要先关闭的程序等。</summary>
    public string? Hint { get; set; }

    /// <summary>已拦截的原因（保护名单、系统设置、账户不一致、其他用户的目录）。</summary>
    public string? BlockReason { get; init; }

    /// <summary>"来源"列：规则、保护名单、系统设置、用户文件。</summary>
    public PlanSource Source { get; init; } = PlanSource.Rule;

    /// <summary>"来源"列的文字。</summary>
    public string SourceText => CleanupText.SourceTitle(Source);

    /// <summary>规则要求先关闭、而现在正在运行的程序。</summary>
    public IReadOnlyList<string> RunningApps { get; init; } = [];

    public PlanItemState State { get; set; }

    /// <summary>执行或重新测量后的结果说明。</summary>
    public string? ResultText { get; set; }

    /// <summary>执行或重新测量后实际释放的空间。</summary>
    public long? FreedBytes { get; set; }

    /// <summary>勾选并且会由工具执行（手动清理的不算）。</summary>
    public bool WillRun => Selected && Selectable && Method is { Kind: not MethodKind.Manual } && State == PlanItemState.Pending;
}

/// <summary>一份清理计划。</summary>
public sealed class CleanupPlan(IReadOnlyList<PlanItem> items, string? warning)
{
    public static readonly CleanupPlan Empty = new([], null);

    public IReadOnlyList<PlanItem> Items { get; } = items;

    /// <summary>整份计划的提示（账户不一致、普通权限扫描识别不了硬链接等）。</summary>
    public string? Warning { get; } = warning;

    public IEnumerable<PlanItem> ToRun => Items.Where(i => i.WillRun);
}

/// <summary>界面和操作记录共用的文字。</summary>
public static class CleanupText
{
    public static string Describe(DeleteAction d)
    {
        var filters = Filters(d);
        var what = d.Scope switch
        {
            DeleteScope.Children => L.T("逐个删除子项"),
            DeleteScope.Self => L.T("删除"),
            _ => L.T("清空内容"),
        };
        return filters is null ? what : L.F("{0}（{1}）", what, filters);
    }

    /// <summary>过滤条件："只删 1 天前的文件，保留最新的 2 个"；没有时返回 null。</summary>
    public static string? Filters(DeleteAction d)
    {
        var parts = new List<string>();
        if (d.OlderThanDays is { } days)
        {
            parts.Add(days == 1 ? L.T("只删 24 小时前的文件") : L.F("只删 {0} 天前的文件", days));
        }

        if (d.KeepLatest is { } keep)
        {
            parts.Add(L.F("保留最新的 {0} 个", keep));
        }

        return parts.Count == 0 ? null : string.Join(L.T("，"), parts);
    }

    public static string GroupTitle(PlanGroup g) => g switch
    {
        PlanGroup.Safe => L.T("安全"),
        PlanGroup.Redownload => L.T("需重新下载"),
        PlanGroup.Irreversible => L.T("无法恢复"),
        PlanGroup.Ai => L.T("AI 建议"),
        _ => L.T("已拦截"),
    };

    public static string GroupNote(PlanGroup g) => g switch
    {
        PlanGroup.Safe => L.T("会自动重建 · 默认直接删除"),
        PlanGroup.Redownload => L.T("默认运行官方清理命令"),
        PlanGroup.Irreversible => L.T("删除后不可恢复，确认时列出明细"),
        PlanGroup.Ai => L.T("默认不勾选，确认时逐项勾选"),
        _ => L.T("不会执行"),
    };

    public static string SourceTitle(PlanSource s) => s switch
    {
        PlanSource.Rule => L.T("规则"),
        PlanSource.Protection => L.T("保护名单"),
        PlanSource.SystemSettings => L.T("系统设置"),
        _ => L.T("用户文件"),
    };

    /// <summary>保护名单的级别对应的来源。</summary>
    public static PlanSource SourceOf(Rules.ProtectionLevel level) => level switch
    {
        Rules.ProtectionLevel.Hard => PlanSource.Protection,
        Rules.ProtectionLevel.Guide => PlanSource.SystemSettings,
        Rules.ProtectionLevel.Soft => PlanSource.UserFiles,
        _ => PlanSource.Rule,
    };
}

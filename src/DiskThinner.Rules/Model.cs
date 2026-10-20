namespace DiskThinner.Rules;

/// <summary>删除的风险等级，从低到高。数值越大越保守，合并多个来源时取最大值。</summary>
public enum Risk
{
    /// <summary>会自动重建（缓存、临时文件、崩溃转储）。</summary>
    Safe = 0,

    /// <summary>删了需要重新下载（包缓存、模型、工具链）。</summary>
    Low = 1,

    /// <summary>会丢失历史记录、登录状态等非关键数据。</summary>
    Medium = 2,

    /// <summary>会丢失用户数据。</summary>
    High = 3,

    /// <summary>只能用系统设置或官方工具处理（分页文件、WinSxS、还原点）。</summary>
    Guide = 4,

    /// <summary>禁止删除（凭据、程序文件、系统文件）。</summary>
    Never = 5,
}

/// <summary>目录或文件的类别。</summary>
public enum Kind
{
    Cache,
    Temp,
    Log,
    CrashDump,
    UpdateCache,
    PackageCache,
    BuildOutput,
    Model,
    VmDisk,
    SessionHistory,
    BrowserData,
    AppData,
    UserData,
    Config,
    Credential,
    Program,
    System,
    Backup,
    RecycleBin,
}

/// <summary>规则可信度。</summary>
public enum Tier
{
    /// <summary>AI 或人起草，没有审阅过。</summary>
    Draft,

    /// <summary>人工审阅过，有依据，但没有在真机上实测删除。</summary>
    Reviewed,

    /// <summary>在真机上实测过。</summary>
    Verified,
}

/// <summary>路径模式怎么匹配。</summary>
public enum MatchMode
{
    /// <summary>匹配目录，整棵子树都归这条规则。</summary>
    Dir,

    /// <summary>匹配文件。</summary>
    File,

    /// <summary>兜底：目录下没被更具体的规则认领的部分归这条规则，匹配继续往下找。</summary>
    Under,
}

public enum DeleteScope
{
    /// <summary>删除目录里的全部内容，保留目录本身。</summary>
    Contents,

    /// <summary>逐个删除直接子项（配合 older_than_days、keep_latest）。</summary>
    Children,

    /// <summary>删除这个文件或目录本身。</summary>
    Self,
}

public abstract record RuleAction;

public sealed record DeleteAction(DeleteScope Scope, int? OlderThanDays = null, int? KeepLatest = null) : RuleAction;

/// <summary>运行官方清理命令。只能引用 <see cref="KnownCommands"/> 里的编号。</summary>
public sealed record CommandAction(string CommandId) : RuleAction;

/// <summary>只展示说明（和链接），不执行任何操作。</summary>
public sealed record GuideAction(string Text, string? Url = null) : RuleAction;

public sealed record Evidence(string? Url, string? Note);

/// <summary>"从根上解决"：用环境变量或程序设置把数据放到别的盘。</summary>
public sealed record Relocation(string? Env, string? Note);

public sealed record Condition(ConditionType Type, IReadOnlyList<string> Patterns);

public enum ConditionType
{
    /// <summary>目录里有名称匹配任意一项的子项。</summary>
    HasAny,

    /// <summary>同级（父目录里）有名称匹配任意一项的其他子项。</summary>
    SiblingAny,

    /// <summary>完整路径不能匹配这个模式。</summary>
    NotUnder,
}

public sealed record AppInfo(
    string Id,
    string Name,
    string? Vendor,
    IReadOnlyList<string> DetectExes,
    IReadOnlyList<string> Processes);

/// <summary>一条位置规则：路径模式 + 这个位置是什么、风险多大、怎么处理。</summary>
public sealed record LocationRule
{
    public required string Id { get; init; }
    public string? AppId { get; init; }
    public required MatchMode Mode { get; init; }

    /// <summary>带路径令牌的原始模式，例如 <c>%LOCALAPPDATA%\uv\cache</c>。</summary>
    public required string Pattern { get; init; }

    public IReadOnlyList<Condition> Conditions { get; init; } = [];
    public required Kind Kind { get; init; }
    public required Risk Risk { get; init; }
    public required Tier Tier { get; init; }

    /// <summary>界面上的风险标签（覆盖默认文字），例如"用 DISM 清理"。</summary>
    public string? Label { get; init; }

    public bool Regenerates { get; init; }
    public bool RequiresClosed { get; init; }
    public required string Purpose { get; init; }
    public required string Impact { get; init; }
    public IReadOnlyList<RuleAction> Actions { get; init; } = [];
    public Relocation? Relocate { get; init; }
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    /// <summary>规则来自哪个源文件（只用于报错和调试，不写入规则库）。</summary>
    public string? SourceFile { get; init; }
}

public sealed record RuleSet(IReadOnlyList<AppInfo> Apps, IReadOnlyList<LocationRule> Locations)
{
    public const int SchemaVersion = 1;

    public string Version { get; init; } = "";

    public static readonly RuleSet Empty = new([], []);

    public AppInfo? App(string? id) => id is null ? null : Apps.FirstOrDefault(a => a.Id == id);
}

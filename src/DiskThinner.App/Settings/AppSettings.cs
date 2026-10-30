using System.Text.Json;
using System.Text.Json.Serialization;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Storage;

namespace DiskThinner.App.Settings;

/// <summary>快照相关的设置。</summary>
internal sealed record SnapshotSettings
{
    /// <summary>扫描整个盘后，距离上次快照超过 6 小时就自动保存一次。</summary>
    public bool AutoAfterScan { get; init; } = true;

    /// <summary>每日快照（计划任务）的时间，HH:mm。任务本身是否存在以计划任务为准。</summary>
    public string DailyTime { get; init; } = "12:00";

    /// <summary>只单独记录不小于这个大小的目录（MiB）。</summary>
    public int MinDirMb { get; init; } = 50;

    /// <summary>这么多天内的快照全部保留，更早的每周只留一个。</summary>
    public int KeepDays { get; init; } = 180;
}

/// <summary>清理相关的设置。</summary>
internal sealed record CleanupSettings
{
    /// <summary>临时文件只删这么多天前的（规则本身更严格时以规则为准）。</summary>
    public int TempMinAgeDays { get; init; } = 1;

    /// <summary>有文件被占用时问用户要不要请程序关闭（确认框里那个选项的默认值）。</summary>
    public bool AskWhenLocked { get; init; }

    /// <summary>用户追加的保护目录（绝对路径）。只能追加和删除自己加的，内置名单不能改。</summary>
    public List<string> ProtectedPaths { get; init; } = [];
}

/// <summary>写入监控的设置（docs/phase2a-design.md 5.9.7 节）。旧的 settings.json 没有这一组时用默认值。</summary>
internal sealed record MonitorSettings
{
    public const string Off = "off";
    public const string Diagnostic = "diagnostic";
    public const string Agent = "agent";
    public const string Light = "light";
    public const string Precise = "precise";

    /// <summary>"off"：不提示开启诊断，也不出现入口；"diagnostic"：需要时手动开启（D12）；"agent"：常驻服务（阶段 2b，服务装没装以 SCM 为准）。</summary>
    public string Enabled { get; init; } = Diagnostic;

    /// <summary>对话框里默认的记录模式（D2：2a.1 实测后定为精确）。</summary>
    public string Mode { get; init; } = Precise;

    /// <summary>对话框里默认的时长（小时）。</summary>
    public int DefaultHours { get; init; } = 4;

    /// <summary>monitor.db 里的会话保留多少天（D5，另有 64 MB 的上限）。</summary>
    public int KeepDays { get; init; } = 90;

    [JsonIgnore]
    public bool IsOff => Enabled == Off;

    [JsonIgnore]
    public bool IsPrecise => Mode == Precise;

    [JsonIgnore]
    public bool IsAgent => Enabled == Agent;
}

/// <summary>
/// %LOCALAPPDATA%\DiskThinner\settings.json。读不了或格式不对时用默认值，坏文件改名留着（不删除）。
/// </summary>
internal sealed record AppSettings
{
    public const int CurrentVersion = 1;
    public static readonly int[] MinDirChoices = [20, 50, 100, 200];
    public static readonly int[] KeepDayChoices = [90, 180, 365];
    public static readonly int[] TempAgeChoices = [1, 7];
    public static readonly int[] MonitorHourChoices = [1, 4, 8, 24];
    public static readonly int[] MonitorKeepChoices = [30, 90, 180];

    public int Version { get; init; } = CurrentVersion;

    public SnapshotSettings Snapshot { get; init; } = new();

    public CleanupSettings Cleanup { get; init; } = new();

    public MonitorSettings Monitor { get; init; } = new();

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "settings.json");

    /// <summary>快照剪枝的参数（文件阈值固定为 100 MiB）。</summary>
    public SnapshotOptions SnapshotOptions() =>
        new(MinDirBytes: Math.Clamp(Snapshot.MinDirMb, 1, 4096) * (1L << 20));

    /// <summary>用户追加的保护目录，作为硬保护交给规则引擎和执行器。</summary>
    public IReadOnlyList<ProtectionEntry> ProtectionEntries() =>
        Cleanup.ProtectedPaths.Where(IsValidProtectedPath).Select(p => new ProtectionEntry(p.TrimEnd('\\'), ProtectionLevel.Hard, L.T("你添加的保护目录"))).ToList();

    /// <summary>可以作为保护目录的路径：本地盘上的绝对路径，不含通配符和花括号（会被当成模式）。</summary>
    public static bool IsValidProtectedPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && path.IndexOfAny(['*', '?', '{', '}']) < 0 && path.TrimEnd('\\').Length > 2;

    /// <summary>
    /// 把超出范围的值收回到界面能选的值。源生成的 JSON 读取对 init 属性的处理：文件里没有的一组读出来是 null、没有的数字是 0，
    /// 不会用属性的初始值，所以这里按默认值补上（旧的 settings.json 没有"monitor"这一组）。
    /// </summary>
    public AppSettings Normalized()
    {
        var snapshot = Snapshot ?? new SnapshotSettings();
        var cleanup = Cleanup ?? new CleanupSettings();
        var monitor = Monitor ?? new MonitorSettings();
        return this with
        {
            Snapshot = snapshot with
            {
                MinDirMb = Nearest(MinDirChoices, snapshot.MinDirMb),
                KeepDays = Nearest(KeepDayChoices, snapshot.KeepDays),
                DailyTime = TimeOnly.TryParse(snapshot.DailyTime, out var t) ? t.ToString("HH:mm") : "12:00",
            },
            Cleanup = cleanup with
            {
                TempMinAgeDays = Nearest(TempAgeChoices, cleanup.TempMinAgeDays),
                ProtectedPaths = (cleanup.ProtectedPaths ?? []).Where(IsValidProtectedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            },
            Monitor = monitor with
            {
                Enabled = monitor.Enabled is MonitorSettings.Off or MonitorSettings.Agent ? monitor.Enabled : MonitorSettings.Diagnostic,
                Mode = monitor.Mode is MonitorSettings.Light ? MonitorSettings.Light : MonitorSettings.Precise,
                DefaultHours = monitor.DefaultHours <= 0 ? 4 : Nearest(MonitorHourChoices, monitor.DefaultHours),
                KeepDays = monitor.KeepDays <= 0 ? 90 : Nearest(MonitorKeepChoices, monitor.KeepDays),
            },
        };
    }

    private static int Nearest(int[] choices, int value) => choices.MinBy(c => Math.Abs(c - value));

    public static AppSettings Load(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), SettingsJson.Default.AppSettings) ?? new AppSettings();
            return settings.Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            try
            {
                File.Move(path, path + ".bad-" + DateTime.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
            }

            return new AppSettings();
        }
    }

    /// <summary>先写临时文件再替换，写到一半断电也不会留下半个文件。</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

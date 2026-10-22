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

/// <summary>
/// %LOCALAPPDATA%\DiskThinner\settings.json。读不了或格式不对时用默认值，坏文件改名留着（不删除）。
/// </summary>
internal sealed record AppSettings
{
    public const int CurrentVersion = 1;
    public static readonly int[] MinDirChoices = [20, 50, 100, 200];
    public static readonly int[] KeepDayChoices = [90, 180, 365];
    public static readonly int[] TempAgeChoices = [1, 7];

    public int Version { get; init; } = CurrentVersion;

    public SnapshotSettings Snapshot { get; init; } = new();

    public CleanupSettings Cleanup { get; init; } = new();

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

    /// <summary>把超出范围的值收回到界面能选的值。</summary>
    public AppSettings Normalized() => this with
    {
        Snapshot = Snapshot with
        {
            MinDirMb = Nearest(MinDirChoices, Snapshot.MinDirMb),
            KeepDays = Nearest(KeepDayChoices, Snapshot.KeepDays),
            DailyTime = TimeOnly.TryParse(Snapshot.DailyTime, out var t) ? t.ToString("HH:mm") : "12:00",
        },
        Cleanup = Cleanup with
        {
            TempMinAgeDays = Nearest(TempAgeChoices, Cleanup.TempMinAgeDays),
            ProtectedPaths = Cleanup.ProtectedPaths.Where(IsValidProtectedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        },
    };

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

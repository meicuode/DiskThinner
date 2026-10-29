using System.Text.Json;
using System.Text.Json.Serialization;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.Agent;

/// <summary>
/// 服务配置 agent.json（docs/phase2b-design.md 5.2 节）：安装时由界面写一次，之后只由服务写（界面通过管道的 configure 命令改）。
/// 读不了时用默认值；坏文件改名留着（agent.json.bad-时间）。
/// </summary>
internal sealed class AgentConfig
{
    public const long DefaultMaxBytes = 256L << 20;

    public string Mode { get; set; } = "precise";
    public int KeepDays { get; set; } = 90;
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>写这个文件的版本（服务或安装器的版本）。</summary>
    public string? Version { get; set; }

    /// <summary>验收专用：定时落盘的间隔（秒），只接受 5 到 300；正式安装不写。</summary>
    public int? FlushSeconds { get; set; }

    [JsonIgnore]
    public MonitorMode MonitorMode => Mode == "light" ? MonitorMode.Light : MonitorMode.Precise;

    /// <summary>超出范围的值改回默认（手改坏了也不影响服务）。</summary>
    public AgentConfig Normalize()
    {
        if (Mode is not ("light" or "precise"))
        {
            Mode = "precise";
        }

        if (!AgentProtocol.KeepDaysChoices.Contains(KeepDays))
        {
            KeepDays = 90;
        }

        if (MaxBytes is < 16L << 20 or > 4L << 30)
        {
            MaxBytes = DefaultMaxBytes;
        }

        if (FlushSeconds is < 5 or > 300)
        {
            FlushSeconds = null;
        }

        return this;
    }

    public static AgentConfig Load(string path, Action<string> log)
    {
        if (!File.Exists(path))
        {
            return new AgentConfig().Normalize();
        }

        try
        {
            var text = File.ReadAllText(path);
            return (JsonSerializer.Deserialize(text, AgentConfigJson.Default.AgentConfig) ?? throw new JsonException("null")).Normalize();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            log("config unreadable: " + ex.GetType().Name);
            if (ex is JsonException)
            {
                try
                {
                    File.Move(path, path + ".bad-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture));
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    log("config rename failed: " + moveError.GetType().Name);
                }
            }

            return new AgentConfig().Normalize();
        }
    }

    /// <summary>先写临时文件再替换（写到一半断电也不会留下半个文件）。</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, AgentConfigJson.Default.AgentConfig));
        File.Move(tmp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentConfig))]
internal sealed partial class AgentConfigJson : JsonSerializerContext;

/// <summary>
/// 服务日志 logs\agent.log（和 2a 一样不记文件名和路径）：一行一条，带 UTC 时间；超过 512 KB 时只保留后一半。
/// </summary>
internal sealed class AgentLog(string path)
{
    public const long MaxBytes = 512 * 1024;
    private readonly Lock _gate = new();

    public string FilePath { get; } = path;

    public void Write(string message)
    {
        var line = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture) + " " + message.ReplaceLineEndings(" ") + Environment.NewLine;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    var text = File.ReadAllText(FilePath);
                    var half = text[(text.Length / 2)..];
                    var cut = half.IndexOf('\n');
                    File.WriteAllText(FilePath, cut >= 0 ? half[(cut + 1)..] : half);
                }

                File.AppendAllText(FilePath, line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不了日志不影响记录
            }
        }
    }

    /// <summary>最后几行（安装失败时界面显示，日志里本来就不含路径）。</summary>
    public IReadOnlyList<string> Tail(int lines)
    {
        lock (_gate)
        {
            try
            {
                return File.Exists(FilePath) ? [.. File.ReadAllLines(FilePath).TakeLast(lines)] : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskThinner.Monitor.Agent;

/// <summary>
/// 常驻服务和界面之间的管道协议（docs/phase2b-design.md 5.5 节）：一行一个 UTF-8 JSON。请求不超过 64 KB；
/// 回应可以有多行（查询分块），最后一行的 <see cref="AgentResponse.Type"/> 是 end、error 或单行回应本身。服务和界面共用这一份。
/// </summary>
public static class AgentProtocol
{
    public const int Version = 1;
    public const string PipeName = "DiskThinner.Agent.v1";
    public const string ServiceName = "DiskThinnerAgent";

    /// <summary>服务的 ETW 会话名（固定，2a 的清理只处理 DiskThinner-Diag- 前缀，不会碰它）。</summary>
    public const string SessionName = "DiskThinner-Agent";

    /// <summary>会话行的名字前缀（monitor.db 里的 session.name）。</summary>
    public const string RowPrefix = "DiskThinner-Agent-";

    public const int MaxRequestBytes = 64 * 1024;
    public const long MaxResponseBytes = 32L << 20;
    public const int MaxRows = 50_000;
    public const int RowsPerChunk = 2_000;
    public const int MaxQueryDays = 400;

    /// <summary>普通用户查询时，其他用户个人目录下的映像路径换成这个（界面显示为"其他用户的程序"）。</summary>
    public const string OtherUserImage = "<other-user>";

    public static readonly int[] KeepDaysChoices = [30, 90, 180];

    public static string Serialize(AgentRequest r) => JsonSerializer.Serialize(r, AgentJson.Default.AgentRequest);

    public static string Serialize(AgentResponse r) => JsonSerializer.Serialize(r, AgentJson.Default.AgentResponse);

    /// <summary>解析请求；格式不对、未知命令、字段超出范围都返回 null（服务回 error 并断开）。</summary>
    public static AgentRequest? ParseRequest(string line)
    {
        if (line.Length == 0 || line.Length > MaxRequestBytes)
        {
            return null;
        }

        AgentRequest? r;
        try
        {
            r = JsonSerializer.Deserialize(line, AgentJson.Default.AgentRequest);
        }
        catch (JsonException)
        {
            return null;
        }

        return r is not null && Valid(r) ? r : null;
    }

    public static AgentResponse? ParseResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, AgentJson.Default.AgentResponse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(AgentRequest r)
    {
        switch (r.Cmd)
        {
            case "hello":
            case "status":
                return true;
            case "query":
                return r.FromUtc is { } from && r.ToUtc is { } to && from < to && to - from <= TimeSpan.FromDays(MaxQueryDays)
                       && from.Year >= 2000 && to.Year <= 9000;
            case "control":
                return r.Action switch
                {
                    "stop" or "start" => true,
                    "configure" => (r.Mode is null || r.Mode is "light" or "precise") && (r.KeepDays is null || KeepDaysChoices.Contains(r.KeepDays.Value))
                                   && (r.Mode is not null || r.KeepDays is not null),
                    _ => false,
                };
            default:
                return false;
        }
    }
}

/// <summary>请求：cmd 是 hello / status / query / control。</summary>
public sealed class AgentRequest
{
    public string Cmd { get; set; } = "";
    public int? Protocol { get; set; }
    public string? AppVersion { get; set; }
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    /// <summary>control：stop / start / configure。</summary>
    public string? Action { get; set; }

    public string? Mode { get; set; }
    public int? KeepDays { get; set; }
}

/// <summary>回应的一行。type：hello / status / sessions / hours / rows / end / ok / error。</summary>
public sealed class AgentResponse
{
    public string Type { get; set; } = "";
    public int? Protocol { get; set; }
    public string? AgentVersion { get; set; }
    public DateTime? StartedUtc { get; set; }
    public AgentStatus? Status { get; set; }
    public List<SessionDto>? Sessions { get; set; }
    public List<HourDto>? Hours { get; set; }
    public List<RowDto>? Rows { get; set; }

    /// <summary>end：行数超过上限、按权重并到了上级目录。</summary>
    public bool? Folded { get; set; }

    /// <summary>end：一共多少行。</summary>
    public int? Count { get; set; }

    /// <summary>end：普通用户看不到的其他用户个人目录下的写入，合成一份计数（没有路径）。</summary>
    public CountersDto? Hidden { get; set; }

    public int? HiddenRows { get; set; }

    /// <summary>error：denied / bad-request / busy / internal / too-large。</summary>
    public string? Code { get; set; }
}

public sealed class AgentStatus
{
    /// <summary>recording / stopped（被控制命令停止）/ external-stopped（被外部停止多次，不再重开）/ store-error / error / starting。</summary>
    public string State { get; set; } = "";
    public string Mode { get; set; } = "precise";
    public int KeepDays { get; set; }
    public string? AgentVersion { get; set; }
    public DateTime? ServiceStartedUtc { get; set; }
    public DateTime? SessionStartedUtc { get; set; }

    /// <summary>库里最早的会话的开始时间（"从 10-20 开始记录"）。</summary>
    public DateTime? RecordingSinceUtc { get; set; }

    public DateTime? LastFlushUtc { get; set; }
    public long Received { get; set; }
    public long Lost { get; set; }
    public int SampleFactor { get; set; } = 1;
    public double Cpu { get; set; }
    public long WorkingSet { get; set; }
    public long DbBytes { get; set; }
    public string? LastError { get; set; }
    public int ExternalStops { get; set; }
    public int Restarts { get; set; }
}

public sealed class SessionDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime StartedUtc { get; set; }
    public DateTime PlannedEndUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public DateTime LastFlushUtc { get; set; }
    public string Mode { get; set; } = "precise";
    public string? EndReason { get; set; }
    public DateTime? DowngradedUtc { get; set; }
    public long Received { get; set; }
    public long Lost { get; set; }
    public long SampledOut { get; set; }
    public long ParseErrors { get; set; }
    public long Filtered { get; set; }
    public long UnresolvedWrites { get; set; }
    public long UnresolvedBytes { get; set; }
    public long BadOffsets { get; set; }
    public long PeakEventsPerSecond { get; set; }
    public int AvgCpuPermille { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public int Flags { get; set; }

    public static SessionDto From(SessionRecord s) => new()
    {
        Id = s.Id, Name = s.Name, StartedUtc = s.StartedUtc, PlannedEndUtc = s.PlannedEndUtc, EndedUtc = s.EndedUtc, LastFlushUtc = s.LastFlushUtc,
        Mode = s.Mode == MonitorMode.Light ? "light" : "precise", EndReason = s.EndReason, DowngradedUtc = s.DowngradedUtc,
        Received = s.Stats.Received, Lost = s.Stats.Lost, SampledOut = s.Stats.SampledOut, ParseErrors = s.Stats.ParseErrors, Filtered = s.Stats.Filtered,
        UnresolvedWrites = s.Stats.UnresolvedWrites, UnresolvedBytes = s.Stats.UnresolvedBytes, BadOffsets = s.Stats.BadOffsets,
        PeakEventsPerSecond = s.PeakEventsPerSecond, AvgCpuPermille = s.AvgCpuPermille, PeakWorkingSetBytes = s.PeakWorkingSetBytes, Flags = (int)s.Flags,
    };

    /// <summary>转成会话行；<paramref name="fromAgent"/> 时会话号取负数、来源标成常驻服务（和本机诊断库合并时不冲突）。</summary>
    public SessionRecord ToRecord(bool fromAgent = false) => new()
    {
        Id = fromAgent ? -Id : Id, Source = fromAgent ? SessionSource.Agent : SessionSource.Diagnostic, Name = Name, StartedUtc = Utc(StartedUtc), PlannedEndUtc = Utc(PlannedEndUtc), EndedUtc = EndedUtc is { } e ? Utc(e) : null, LastFlushUtc = Utc(LastFlushUtc),
        Mode = Mode == "light" ? MonitorMode.Light : MonitorMode.Precise, EndReason = EndReason, DowngradedUtc = DowngradedUtc is { } d ? Utc(d) : null,
        Stats = new MonitorStats
        {
            Received = Received, Lost = Lost, SampledOut = SampledOut, ParseErrors = ParseErrors, Filtered = Filtered,
            UnresolvedWrites = UnresolvedWrites, UnresolvedBytes = UnresolvedBytes, BadOffsets = BadOffsets,
        },
        PeakEventsPerSecond = PeakEventsPerSecond, AvgCpuPermille = AvgCpuPermille, PeakWorkingSetBytes = PeakWorkingSetBytes, Flags = (SessionFlags)Flags,
    };

    internal static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d, DateTimeKind.Utc);
}

public sealed class HourDto
{
    public long SessionId { get; set; }
    public DateTime HourUtc { get; set; }
    public string Mode { get; set; } = "precise";
    public long Received { get; set; }
    public long Lost { get; set; }
    public long SampledOut { get; set; }
    public long UnresolvedBytes { get; set; }
    public long WriteBytes { get; set; }
    public int Flags { get; set; }

    public static HourDto From(HourCoverage h) => new()
    {
        SessionId = h.SessionId, HourUtc = h.HourUtc, Mode = h.Mode == MonitorMode.Light ? "light" : "precise", Received = h.Received, Lost = h.Lost,
        SampledOut = h.SampledOut, UnresolvedBytes = h.UnresolvedBytes, WriteBytes = h.WriteBytes, Flags = h.Flags,
    };

    public HourCoverage ToCoverage(bool fromAgent = false) =>
        new(fromAgent ? -SessionId : SessionId, SessionDto.Utc(HourUtc), Mode == "light" ? MonitorMode.Light : MonitorMode.Precise, Received, Lost, SampledOut, UnresolvedBytes, WriteBytes, Flags);
}

public sealed class ImageDto
{
    public string Path { get; set; } = "";
    public string Services { get; set; } = "";
    public string? Product { get; set; }
    public string? Company { get; set; }

    public static ImageDto? From(StoredImage? i) => i is null ? null : new() { Path = i.Path, Services = i.Services, Product = i.Product, Company = i.Company };

    public StoredImage ToImage() => new(Path, Services, Product, Company);
}

public sealed class CountersDto
{
    public long Creates { get; set; }
    public long CreatedDirs { get; set; }
    public long Deletes { get; set; }
    public long Renames { get; set; }
    public long WriteOps { get; set; }
    public long WriteBytes { get; set; }
    public long Growth { get; set; }
    public bool Sampled { get; set; }

    public static CountersDto From(in Counters c) => new()
    {
        Creates = c.Creates, CreatedDirs = c.CreatedDirs, Deletes = c.Deletes, Renames = c.Renames, WriteOps = c.WriteOps, WriteBytes = c.WriteBytes,
        Growth = c.Growth, Sampled = c.Sampled,
    };

    public Counters ToCounters() => new()
    {
        Creates = Creates, CreatedDirs = CreatedDirs, Deletes = Deletes, Renames = Renames, WriteOps = WriteOps, WriteBytes = WriteBytes, Growth = Growth, Sampled = Sampled,
    };
}

/// <summary>一行：会话 × 目录 × 进程链，这段时间内各小时的合计（hourUtc 是最早的一个小时）。</summary>
public sealed class RowDto
{
    public long SessionId { get; set; }
    public DateTime HourUtc { get; set; }
    public string? Dir { get; set; }
    public int Kind { get; set; }
    public ImageDto? Writer { get; set; }
    public ImageDto? Top { get; set; }
    public string Links { get; set; } = "";
    public int UnknownPid { get; set; }
    public CountersDto Counters { get; set; } = new();

    public IndexRow ToRow(bool fromAgent = false) => new(fromAgent ? -SessionId : SessionId, SessionDto.Utc(HourUtc), Dir,
        new StoredChain((ChainKind)Kind, Writer?.ToImage(), Top?.ToImage(), Links, UnknownPid), Counters.ToCounters());
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentResponse))]
internal sealed partial class AgentJson : JsonSerializerContext;

namespace DiskThinner.Monitor;

/// <summary>一个"目录 × 进程链 × 小时"的计数（docs/phase2a-design.md 5.6、5.7 节）。</summary>
public struct Counters
{
    public long Creates;
    public long CreatedDirs;
    public long Deletes;
    public long Renames;

    /// <summary>非分页写入的次数和字节（抽样时已乘以倍数）。</summary>
    public long WriteOps;
    public long WriteBytes;

    /// <summary>估算增长：监控期间新建的文件最大写入位置的增量，删除时减回，可以是负数。</summary>
    public long Growth;

    /// <summary>有抽样（写入量是估算）。</summary>
    public bool Sampled;

    public readonly bool IsEmpty => Creates == 0 && CreatedDirs == 0 && Deletes == 0 && Renames == 0 && WriteOps == 0 && WriteBytes == 0 && Growth == 0;

    /// <summary>合并用的权重：|估算增长| + 写入量 + 1 MiB ×（新建 + 删除 + 重命名个数）。</summary>
    public readonly long Weight => Math.Abs(Growth) + WriteBytes + (1L << 20) * (Creates + CreatedDirs + Deletes + Renames);

    public void Add(in Counters o)
    {
        Creates += o.Creates;
        CreatedDirs += o.CreatedDirs;
        Deletes += o.Deletes;
        Renames += o.Renames;
        WriteOps += o.WriteOps;
        WriteBytes += o.WriteBytes;
        Growth += o.Growth;
        Sampled |= o.Sampled;
    }
}

/// <summary>会话（或一个小时）的统计（docs/phase2a-design.md 5.3 节）。</summary>
public sealed class MonitorStats
{
    /// <summary>送到回调的事件数。</summary>
    public long Received;

    /// <summary>内核侧丢的事件（EventsLost 的差值）。</summary>
    public long Lost;

    public long SampledOut;
    public long ParseErrors;

    /// <summary>自己的进程、非本地卷、不认识的事件号。</summary>
    public long Filtered;

    public long UnresolvedWrites;
    public long UnresolvedBytes;
    public long BadOffsets;
    public long DuplicateWrites;
    public long NameMapEvicted;
    public long FileStateEvicted;
    public long ReorderOverflow;
    public long LateEvents;

    public double LostRate => Received + Lost == 0 ? 0 : (double)Lost / (Received + Lost);

    public void Add(MonitorStats o)
    {
        Received += o.Received;
        Lost += o.Lost;
        SampledOut += o.SampledOut;
        ParseErrors += o.ParseErrors;
        Filtered += o.Filtered;
        UnresolvedWrites += o.UnresolvedWrites;
        UnresolvedBytes += o.UnresolvedBytes;
        BadOffsets += o.BadOffsets;
        DuplicateWrites += o.DuplicateWrites;
        NameMapEvicted += o.NameMapEvicted;
        FileStateEvicted += o.FileStateEvicted;
        ReorderOverflow += o.ReorderOverflow;
        LateEvents += o.LateEvents;
    }

    /// <summary>这次减去 <paramref name="o"/> 的差（服务按天轮换时，新会话行的统计从轮换那一刻算起）。</summary>
    public MonitorStats Minus(MonitorStats o) => new()
    {
        Received = Received - o.Received,
        Lost = Lost - o.Lost,
        SampledOut = SampledOut - o.SampledOut,
        ParseErrors = ParseErrors - o.ParseErrors,
        Filtered = Filtered - o.Filtered,
        UnresolvedWrites = UnresolvedWrites - o.UnresolvedWrites,
        UnresolvedBytes = UnresolvedBytes - o.UnresolvedBytes,
        BadOffsets = BadOffsets - o.BadOffsets,
        DuplicateWrites = DuplicateWrites - o.DuplicateWrites,
        NameMapEvicted = NameMapEvicted - o.NameMapEvicted,
        FileStateEvicted = FileStateEvicted - o.FileStateEvicted,
        ReorderOverflow = ReorderOverflow - o.ReorderOverflow,
        LateEvents = LateEvents - o.LateEvents,
    };
}

/// <summary>会话的标记（monitor.db 的 session.flags，按位）。</summary>
[Flags]
public enum SessionFlags
{
    None = 0,
    Sampled = 1,
    Downgraded = 2,
    DirectoryTableFull = 4,
    ChainTableFull = 8,
    FileStateEvicted = 16,
    BudgetExceeded = 32,
    RenameIncomplete = 64,
    NameMapEvicted = 128,
    AnchorsFolded = 256,
    WatchdogFailed = 512,
}

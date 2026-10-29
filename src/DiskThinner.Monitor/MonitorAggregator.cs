namespace DiskThinner.Monitor;

/// <summary>
/// 按"目录 × 进程链 × 小时"汇总事件（docs/phase2a-design.md 5.5–5.7 节，第 12.2 节）。事件要按时间顺序送进来（先经过 <see cref="ReorderBuffer"/>）。
/// 只在一个线程上调用；落盘线程取数据时由调用方加锁。
/// </summary>
public sealed class MonitorAggregator
{
    public const int MaxFileStates = 300_000;
    public const int DefaultNameCapacity = 400_000;

    /// <summary>每秒写事件超过这么多就开始抽样：2a.1 回放测得单线程处理约 96,000 个事件 / 秒，取一半（设计 5.3 节）。</summary>
    public const double DefaultSampleThreshold = 48_000;
    private static readonly long Hour = TimeSpan.TicksPerHour;
    private static readonly long SealDelay = TimeSpan.FromSeconds(60).Ticks;

    private readonly IDeviceMap _devices;
    private readonly IServiceNames? _services;
    private readonly int _selfPid;
    private readonly FileNameMap _names;
    private readonly Dictionary<ulong, FileName> _recentlyDeletedNames = [];
    private readonly Dictionary<(int Dir, ulong Name), FileState> _files = [];
    private readonly Dictionary<long, Dictionary<(int Dir, int Chain), Counters>> _hours = [];
    private readonly Dictionary<long, MonitorStats> _hourStats = [];
    private readonly Dictionary<int, (ulong Key, ulong Offset, uint Size)> _lastFastIo = [];
    private readonly WriteSampler _sampler;
    private readonly List<(string Old, string New)> _movedDirs = [];
    private long _sealedBefore;

    public MonitorAggregator(MonitorMode mode, IDeviceMap devices, IServiceNames? services = null, int selfPid = 0,
        int nameCapacity = DefaultNameCapacity, int maxDirectories = 200_000, double sampleThreshold = DefaultSampleThreshold)
    {
        Mode = mode;
        _devices = devices;
        _services = services;
        _selfPid = selfPid;
        _names = new FileNameMap(nameCapacity);
        Directories = new DirectoryTable(maxDirectories);
        _sampler = new WriteSampler(sampleThreshold);
    }

    public MonitorMode Mode { get; set; }
    public ProcessTable Processes { get; } = new();
    public ChainTable Chains { get; } = new();
    public DirectoryTable Directories { get; }
    public MonitorStats Stats { get; } = new();
    public SessionFlags Flags { get; private set; }
    public int NameMapPeak => _names.Peak;
    public int FileStates => _files.Count;

    /// <summary>当前的抽样倍数（1 表示不抽样）。</summary>
    public int SampleFactor => _sampler.Factor;

    /// <summary>开始监控时列举到的进程（事件里的 ProcessStart 以后到的以事件为准）。</summary>
    public void Seed(int pid, long createTicks, int parentPid, string image)
    {
        var p = Processes.Add(pid, createTicks, parentPid, _devices.ToDosPath(image) ?? image);
        p.Services = _services?.For(pid) ?? [];
    }

    public void Process(MonitorEvent e)
    {
        Stats.Received++;
        var hourStats = StatsFor(e.TimestampUtcTicks);
        hourStats.Received++;
        switch (e.Kind)
        {
            case MonitorEventKind.ProcessStart:
            case MonitorEventKind.ProcessRundown:
            {
                var p = Processes.Add(e.TargetProcessId, e.CreateTimeUtcTicks, e.ParentProcessId, _devices.ToDosPath(e.ImageName ?? "") ?? e.ImageName ?? "", e.Sequence, e.ParentSequence);
                if (string.Equals(p.ExeName, "svchost.exe", StringComparison.OrdinalIgnoreCase))
                {
                    p.Services = _services?.For(p.Pid) ?? [];
                }

                return;
            }

            case MonitorEventKind.ProcessStop:
                Processes.Exit(e.TargetProcessId, e.CreateTimeUtcTicks, e.ExitTimeUtcTicks);
                return;
        }

        if (e.ProcessId == _selfPid && _selfPid != 0)
        {
            Stats.Filtered++;
            return;
        }

        string? path = null;
        if (e.Kind != MonitorEventKind.Write && (e.Path is null || (path = _devices.ToDosPath(e.Path)) is null))
        {
            Stats.Filtered++;
            return;
        }

        var process = Processes.Find(e.ProcessId, e.TimestampUtcTicks);
        var chain = Chains.For(e.ProcessId, process, Processes);
        var hour = HourOf(e.TimestampUtcTicks);
        switch (e.Kind)
        {
            case MonitorEventKind.NameCreate:
                if (Mode == MonitorMode.Precise)
                {
                    var (dir, name) = DirectoryTable.Split(path!);
                    if (dir is not null)
                    {
                        _names.Set(e.FileKey, dir, name);
                        _recentlyDeletedNames.Remove(e.FileKey);
                    }
                }

                break;

            case MonitorEventKind.NameDelete:
                OnNameDelete(e, hour);
                break;

            case MonitorEventKind.Write:
                if (Mode == MonitorMode.Precise)
                {
                    OnWrite(e, chain, hour, hourStats);
                }

                break;

            case MonitorEventKind.CreateNewFile:
                OnCreate(path!, e.CreateOptions, chain, hour);
                break;

            case MonitorEventKind.DeletePath:
                OnDelete(path!, chain, hour);
                break;

            case MonitorEventKind.RenamePath:
                OnRename(e.FileKey, path!, chain, hour);
                break;

            case MonitorEventKind.SetLinkPath:
                // 建硬链接：计入新建文件数（不占新空间，估算增长不变）
                Bucket(hour, DirOf(path!), chain).Creates++;
                break;
        }
    }

    // ---------------- 各类事件 ----------------

    private void OnCreate(string path, uint options, int chain, long hour)
    {
        var (parent, name) = DirectoryTable.Split(path);
        if (parent is null)
        {
            return;
        }

        var dir = Directories.GetOrAdd(parent);
        ref var c = ref Bucket(hour, dir, chain);
        if ((options & 0x1) != 0)
        {
            c.CreatedDirs++; // FILE_DIRECTORY_FILE
            return;
        }

        c.Creates++;
        if (Mode != MonitorMode.Precise)
        {
            return;
        }

        var key = (dir, Hash(name));
        if (_files.Remove(key, out var old) && old.MaxPos != 0)
        {
            // 覆盖已有的（本次新建过的）文件：旧内容被截断，减回旧的估算增长（不计删除数）
            Bucket(hour, dir, old.CreatorChain).Growth -= old.MaxPos;
        }

        if (_files.Count >= MaxFileStates)
        {
            EvictFileStates();
        }

        _files[key] = new FileState { CreatorChain = chain, DeleteOnClose = (options & 0x1000) != 0 };
    }

    private void OnDelete(string path, int chain, long hour)
    {
        var (parent, name) = DirectoryTable.Split(path);
        if (parent is null)
        {
            return;
        }

        var dir = Directories.GetOrAdd(parent);
        Bucket(hour, dir, chain).Deletes++;
        if (_files.Remove((dir, Hash(name)), out var state) && state.MaxPos != 0)
        {
            Bucket(hour, dir, state.CreatorChain).Growth -= state.MaxPos;
        }
    }

    /// <summary>NameDelete：名字缓存拆除和改名时都会来（V15）。关闭时删除的文件没有 DeletePath（V12），它的 NameDelete 按删除处理。</summary>
    private void OnNameDelete(MonitorEvent e, long hour)
    {
        if (Mode != MonitorMode.Precise || !_names.Remove(e.FileKey, out var fn))
        {
            return;
        }

        if (_recentlyDeletedNames.Count > 10_000)
        {
            _recentlyDeletedNames.Clear();
        }

        _recentlyDeletedNames[e.FileKey] = fn; // 改名事件可能晚一点到，留着给它用
        var dir = Directories.Find(FixDir(fn.Dir));
        if (dir > 0 && _files.TryGetValue((dir, Hash(fn.Name)), out var state) && state.DeleteOnClose)
        {
            _files.Remove((dir, Hash(fn.Name)));
            ref var c = ref Bucket(hour, dir, state.CreatorChain);
            c.Deletes++;
            c.Growth -= state.MaxPos;
        }
    }

    private void OnRename(ulong fileKey, string newPath, int chain, long hour)
    {
        var (newParent, newName) = DirectoryTable.Split(newPath);
        if (newParent is null)
        {
            return;
        }

        var newDir = Directories.GetOrAdd(newParent);
        Bucket(hour, newDir, chain).Renames++;
        if (Mode != MonitorMode.Precise)
        {
            return;
        }

        // FilePath 是新名字（V4），旧名字取同一 FileKey 的 NameCreate
        if (!_names.TryGet(fileKey, out var old) && !_recentlyDeletedNames.Remove(fileKey, out old))
        {
            Flags |= SessionFlags.RenameIncomplete;
            return;
        }

        _names.Set(fileKey, newParent, newName);
        old = new FileName(FixDir(old.Dir), old.Name);
        var oldDirNode = Directories.Find(old.FullPath);
        if (oldDirNode > 0)
        {
            // 目录改名：节点原地改名，下面的文件状态跟着走；名字表里还是旧路径的项按前缀换成新路径
            var merged = Directories.Move(oldDirNode, newPath);
            if (merged != oldDirNode)
            {
                // 新路径已经有节点（例如锚点），旧节点并了进去：直接在这个目录下的文件状态换成新编号（并目录很少见，逐项扫一遍）
                foreach (var key in _files.Keys.Where(k => k.Dir == oldDirNode).ToList())
                {
                    _files.Remove(key, out var moved);
                    _files[(merged, key.Name)] = moved;
                }
            }
            if (_movedDirs.Count >= 1000)
            {
                _movedDirs.RemoveAt(0);
            }

            _movedDirs.Add((old.FullPath.TrimEnd('\\'), newPath.TrimEnd('\\')));
            return;
        }

        var oldDir = Directories.Find(old.Dir);
        if (oldDir <= 0 || !_files.Remove((oldDir, Hash(old.Name)), out var state))
        {
            return;
        }

        var newKey = (newDir, Hash(newName));
        if (_files.Remove(newKey, out var target) && target.MaxPos != 0)
        {
            // 改名覆盖已有文件：先把目标按删除处理（减回累计值，不计删除数）
            Bucket(hour, newDir, target.CreatorChain).Growth -= target.MaxPos;
        }

        if (oldDir != newDir && state.MaxPos != 0)
        {
            // 跨目录移动：已累计的值从旧目录移到新目录（记在当前小时）
            Bucket(hour, oldDir, state.CreatorChain).Growth -= state.MaxPos;
            Bucket(hour, newDir, state.CreatorChain).Growth += state.MaxPos;
        }

        _files[newKey] = state;
    }

    private void OnWrite(MonitorEvent e, int chain, long hour, MonitorStats hourStats)
    {
        // 快速 I/O 失败后会再发一个同样的 IRP 写入（V2）：只算一次（写入量和估算增长都不再算；追加写的哨兵偏移重复算会多一倍）
        if (e.IsFastIoWrite)
        {
            if (_lastFastIo.Count > 4096)
            {
                _lastFastIo.Clear();
            }

            _lastFastIo[e.ThreadId] = (e.FileKey, e.ByteOffset, e.IoSize);
        }
        else if (_lastFastIo.Remove(e.ThreadId, out var last) && last == (e.FileKey, e.ByteOffset, e.IoSize))
        {
            Stats.DuplicateWrites++;
            return;
        }

        var factor = _sampler.Next(e.TimestampUtcTicks);
        if (factor == 0)
        {
            Stats.SampledOut++;
            hourStats.SampledOut++;
            return;
        }

        if (factor > 1)
        {
            Flags |= SessionFlags.Sampled;
        }

        var paging = e.IsPagingWrite;
        if (!_names.TryGet(e.FileKey, out var fn))
        {
            if (!paging)
            {
                Stats.UnresolvedWrites++;
                Stats.UnresolvedBytes += e.IoSize;
                hourStats.UnresolvedWrites++;
                hourStats.UnresolvedBytes += e.IoSize;
                ref var u = ref Bucket(hour, DirectoryTable.Unresolved, chain);
                u.WriteOps += factor;
                u.WriteBytes += (long)e.IoSize * factor;
                u.Sampled |= factor > 1;
            }

            return;
        }

        var dir = Directories.GetOrAdd(FixDir(fn.Dir));
        if (!paging)
        {
            ref var c = ref Bucket(hour, dir, chain);
            c.WriteOps += factor;
            c.WriteBytes += (long)e.IoSize * factor;
            c.Sampled |= factor > 1;
        }

        if (!_files.TryGetValue((dir, Hash(fn.Name)), out var state))
        {
            return; // 监控开始前就有的文件：只计写入量
        }

        // 估算增长：分页写入只在这个文件没有过非分页写入时算（只有映射写入的文件）
        if (paging && state.SawNonPaging)
        {
            return;
        }

        ulong end;
        if (e.ByteOffset == ulong.MaxValue)
        {
            end = (ulong)state.MaxPos + e.IoSize; // 写到文件末尾（FILE_APPEND_DATA，V16）
        }
        else if (e.ByteOffset >= 1UL << 63 || e.ByteOffset > ulong.MaxValue - e.IoSize)
        {
            Stats.BadOffsets++;
            return;
        }
        else
        {
            end = e.ByteOffset + e.IoSize;
        }

        var delta = (long)end - state.MaxPos;
        if (delta > 1L << 40)
        {
            Stats.BadOffsets++;
            return;
        }

        if (!paging)
        {
            state.SawNonPaging = true;
        }

        state.LastWriteTicks = e.TimestampUtcTicks;
        if (delta > 0)
        {
            state.MaxPos += delta;
            Bucket(hour, dir, state.CreatorChain).Growth += delta;
        }

        _files[(dir, Hash(fn.Name))] = state;
    }

    // ---------------- 小时 ----------------

    private long HourOf(long ticks)
    {
        var hour = ticks - ticks % Hour;
        if (hour < _sealedBefore)
        {
            // 上一个小时已经封存之后才到的事件：记到当前小时
            Stats.LateEvents++;
            return _sealedBefore;
        }

        return hour;
    }

    private MonitorStats StatsFor(long ticks)
    {
        var hour = Math.Max(ticks - ticks % Hour, _sealedBefore);
        if (!_hourStats.TryGetValue(hour, out var s))
        {
            _hourStats[hour] = s = new MonitorStats();
        }

        return s;
    }

    private ref Counters Bucket(long hour, int dir, int chain)
    {
        if (!_hours.TryGetValue(hour, out var map))
        {
            _hours[hour] = map = [];
        }

        return ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(map, (dir, chain), out _);
    }

    /// <summary>改过名的目录：旧路径前缀换成新路径（后改的优先）。</summary>
    private string FixDir(string dir)
    {
        for (var i = _movedDirs.Count - 1; i >= 0; i--)
        {
            var (old, now) = _movedDirs[i];
            if (dir.StartsWith(old, StringComparison.OrdinalIgnoreCase) && (dir.Length == old.Length || dir[old.Length] == '\\'))
            {
                dir = now + dir[old.Length..];
            }
        }

        return dir;
    }

    private int DirOf(string path)
    {
        var (parent, _) = DirectoryTable.Split(path);
        return parent is null ? DirectoryTable.Unresolved : Directories.GetOrAdd(parent);
    }

    /// <summary>整点后 60 秒内到达的上一个小时的事件仍记到上一个小时，之后封存。返回已封存、可以落盘后丢弃的小时。</summary>
    public IReadOnlyList<long> SealBefore(long nowTicks)
    {
        var limit = nowTicks - SealDelay;
        limit -= limit % Hour;
        if (limit > _sealedBefore)
        {
            _sealedBefore = limit;
        }

        return [.. _hours.Keys.Where(h => h < _sealedBefore).Order()];
    }

    public IEnumerable<long> Hours => _hours.Keys.Order();

    /// <summary>某个小时的计数器副本（落盘用，第 5.7 节的合并在副本上做）。</summary>
    public Dictionary<(int Dir, int Chain), Counters> HourCounters(long hour) =>
        _hours.TryGetValue(hour, out var map) ? new Dictionary<(int, int), Counters>(map) : [];

    public MonitorStats HourStats(long hour) => _hourStats.GetValueOrDefault(hour) ?? new MonitorStats();

    /// <summary>落盘后不再需要的小时。</summary>
    public void Drop(long hour)
    {
        _hours.Remove(hour);
        _hourStats.Remove(hour);
    }

    /// <summary>停止时：还在表里的关闭时删除的文件按删除处理（V12）；记下名字表的淘汰数。</summary>
    public void Finish(long nowTicks)
    {
        var hour = HourOf(nowTicks);
        foreach (var (key, state) in _files.Where(f => f.Value.DeleteOnClose).ToList())
        {
            _files.Remove(key);
            ref var c = ref Bucket(hour, key.Dir, state.CreatorChain);
            c.Deletes++;
            c.Growth -= state.MaxPos;
        }

        Stats.NameMapEvicted = _names.Evicted;
        if (_names.Evicted > 0)
        {
            Flags |= SessionFlags.NameMapEvicted;
        }

        if (Directories.Full)
        {
            Flags |= SessionFlags.DirectoryTableFull;
        }

        if (Chains.Full)
        {
            Flags |= SessionFlags.ChainTableFull;
        }
    }

    public void MarkDowngraded() => Flags |= SessionFlags.Downgraded;

    /// <summary>清掉会话标记（服务开新的会话行时：标记按行记，不跟着汇总器一直留着）。</summary>
    public void ClearFlags() => Flags = SessionFlags.None;

    private void EvictFileStates()
    {
        // 淘汰最久没写的 10%（满 300,000 项才会走到这里，很少发生）
        foreach (var key in _files.OrderBy(f => f.Value.LastWriteTicks).Take(MaxFileStates / 10).Select(f => f.Key).ToList())
        {
            _files.Remove(key);
            Stats.FileStateEvicted++;
        }

        Flags |= SessionFlags.FileStateEvicted;
    }

    /// <summary>文件名的 64 位 FNV-1a 哈希（大写后的 UTF-16）。</summary>
    internal static ulong Hash(string name)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in name)
        {
            var c = char.ToUpperInvariant(ch);
            h = (h ^ (byte)c) * 1099511628211UL;
            h = (h ^ (byte)(c >> 8)) * 1099511628211UL;
        }

        return h;
    }

    private struct FileState
    {
        public long MaxPos;
        public int CreatorChain;
        public bool SawNonPaging;
        public bool DeleteOnClose;
        public long LastWriteTicks;
    }
}

/// <summary>
/// 写事件的抽样（docs/phase2a-design.md 5.3 节）：每秒的写事件数超过 R 时倍数翻倍（最大 64），连续 10 秒低于 R/4 时减半；
/// 按到达顺序每 k 个处理一个。只抽样写事件。
/// </summary>
internal sealed class WriteSampler(double threshold)
{
    private long _second = -1;
    private long _countThisSecond;
    private int _quietSeconds;
    private long _seq;

    public int Factor { get; private set; } = 1;

    /// <summary>返回这个写事件要乘的倍数；0 表示跳过。</summary>
    public int Next(long ticks)
    {
        var second = ticks / TimeSpan.TicksPerSecond;
        if (second != _second)
        {
            if (_second >= 0)
            {
                if (_countThisSecond > threshold && Factor < 64)
                {
                    Factor *= 2;
                    _quietSeconds = 0;
                }
                else if (_countThisSecond < threshold / 4 && Factor > 1 && ++_quietSeconds >= 10)
                {
                    Factor /= 2;
                    _quietSeconds = 0;
                }
            }

            _second = second;
            _countThisSecond = 0;
        }

        _countThisSecond++;
        return _seq++ % Factor == 0 ? Factor : 0;
    }
}

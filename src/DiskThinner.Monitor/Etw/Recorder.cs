using System.Diagnostics;

namespace DiskThinner.Monitor.Etw;

public sealed class RecorderOptions
{
    public required string DbPath { get; init; }
    public required MonitorMode Mode { get; init; }
    public IAnchorSource? Anchors { get; init; }

    /// <summary>映像的产品名和公司名（落盘线程上调用）。</summary>
    public Func<string, (string? Product, string? Company)>? Identity { get; init; }

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>结束时（服务是每次轮换后）按这个天数和 <see cref="MaxBytes"/> 删掉最早的会话（2a 的 D5，2b 的 E9）。</summary>
    public int KeepDays { get; init; } = 90;

    public long MaxBytes { get; init; } = 64L << 20;

    /// <summary>日志（不记路径）。</summary>
    public Action<string>? Log { get; init; }

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>
    /// 按天轮换会话行（服务，docs/phase2b-design.md 5.2 节）：本地日期变了之后的第一次定时落盘时，在 UTC 整点结束当前行（结束原因 rotate），
    /// 用同一个 ETW 会话开一条新行。参数是新行的开始时间，返回新行的名字。为 null 时不轮换（诊断）。
    /// </summary>
    public Func<DateTime, string>? RotateName { get; init; }

    /// <summary>UTC → 本地时间（测试注入时区）。</summary>
    public Func<DateTime, DateTime> ToLocal { get; init; } = u => u.ToLocalTime();

    /// <summary>测试注入：设备路径映射、服务名；为 null 时用本机的。</summary>
    public IDeviceMap? Devices { get; init; }

    public IServiceNames? Services { get; init; }

    /// <summary>开始时列举现有进程（测试里关掉）。</summary>
    public bool SeedProcesses { get; init; } = true;

    /// <summary>轮换时目录表或进程链表超过这么多，就换一个新的汇总器（长期运行的服务不让表一直涨到上限）。</summary>
    public int RebuildDirectories { get; init; } = 100_000;

    public int RebuildChains { get; init; } = ChainTable.MaxChains / 2;
}

/// <summary>
/// 和会话无关的记录部分（docs/phase2a-design.md 5.9.1 节；2b 从 MonitorRunner 拆出来，诊断和服务共用，docs/phase2b-design.md 5.2 节）：
/// 解析 → 重排 → 汇总 → 定时落盘、降级判断、统计，服务还有按天轮换。
/// 泵线程每个事件拿一次 <see cref="_gate"/>；落盘线程拿锁后合并、复制出要写的行，写 SQLite 在锁外；所有落盘共用一个信号量，不会交错。
/// </summary>
public sealed class Recorder : IDisposable
{
    private readonly RecorderOptions _options;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly ReorderBuffer _reorder = new(TimeSpan.FromSeconds(1));
    private readonly Dictionary<string, long> _byId = new(StringComparer.Ordinal);
    private readonly Queue<(long Received, long Lost)> _window = new();

    /// <summary>换掉的汇总器还没落盘的小时（按目录路径和进程链合并进下一次落盘）。</summary>
    private readonly Dictionary<long, (List<StoredRow> Rows, MonitorStats Stats, int Flags)> _carry = [];

    private readonly Stopwatch _wall = new();
    private MonitorStore? _store;
    private MonitorAggregator? _aggregator;
    private IDeviceMap? _devices;
    private IServiceNames? _services;
    private SessionRecord? _record;
    private MonitorStats _base = new();
    private Timer? _statusTimer;
    private Timer? _flushTimer;
    private Func<long>? _eventsLost;
    private Action? _downgrade;
    private HashSet<int> _anchors = [];
    private DateTime _lastFlushUtc;
    private int _flushFailures;
    private long _lastLost;
    private TimeSpan _cpuStart;
    private long _peakWs;
    private long _peakEventsPerSecond;
    private long _lastReceived;
    private int _foldFlags;

    public Recorder(RecorderOptions options)
    {
        _options = options;
        KeepDays = options.KeepDays;
    }

    /// <summary>保留天数（服务的 configure 命令可以改，下一次删除时生效）。</summary>
    public int KeepDays { get; set; }

    /// <summary>当前的会话行（轮换后是新的一行）。</summary>
    public SessionRecord? Record => _record;

    public MonitorStore? Store => _store;

    public bool Downgraded { get; private set; }

    public string? LastError { get; set; }

    /// <summary>落盘时加到会话行上的标记（诊断的"兜底任务注册失败"）。</summary>
    public SessionFlags ExtraFlags { get; set; }

    /// <summary>最近一次成功落盘的时间。</summary>
    public DateTime LastFlushUtc => _lastFlushUtc;

    /// <summary>连续 3 次定时落盘失败时调用（任何线程上）：诊断停会话，服务停会话后每 10 分钟重试。</summary>
    public Action? StoreFailed { get; set; }

    /// <summary>每秒的统计刷新之后调用（计时器线程上）。</summary>
    public Action? Ticked { get; set; }

    /// <summary>定时落盘成功之后调用（任何线程上）。</summary>
    public event EventHandler? Flushed;

    /// <summary>轮换之后调用：（结束的行, 新行）。</summary>
    public Action<SessionRecord, SessionRecord>? Rotated { get; set; }

    public MonitorMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _aggregator?.Mode ?? _options.Mode;
            }
        }
    }

    public (long Received, long Lost, int SampleFactor) Counts
    {
        get
        {
            lock (_gate)
            {
                return _aggregator is null ? (0, 0, 1) : (_aggregator.Stats.Received - _base.Received, _aggregator.Stats.Lost - _base.Lost, _aggregator.SampleFactor);
            }
        }
    }

    /// <summary>这个进程从当前会话行开始以来的 CPU（整机百分比）。</summary>
    public double CpuPercent
    {
        get
        {
            using var self = Process.GetCurrentProcess();
            var wall = _wall.Elapsed.TotalMilliseconds;
            return wall <= 0 ? 0 : (self.TotalProcessorTime - _cpuStart).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
        }
    }

    /// <summary>每个（提供程序/事件号/版本）收到的个数（monitor 子命令的 events_by_id，V5）。</summary>
    public IReadOnlyDictionary<string, long> EventsById
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, long>(_byId);
            }
        }
    }

    /// <summary>打开（或新建）库，建汇总器。失败时返回原因。</summary>
    public string? Open()
    {
        try
        {
            _store = MonitorStore.Open(_options.DbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            return ex.Message;
        }

        _services = _options.Services ?? new WindowsSystem.Services();
        _devices = _options.Devices ?? WindowsSystem.DeviceMap();
        _aggregator = new MonitorAggregator(_options.Mode, _devices, _services, Environment.ProcessId);
        ReloadAnchors();
        return null;
    }

    /// <summary>列举现有进程（会话开始之后做：之后启动的进程有事件，之前的靠列举，同一个进程两边都看到时以先到的为准）。</summary>
    private void Seed(MonitorAggregator a)
    {
        if (!_options.SeedProcesses)
        {
            return;
        }

        foreach (var (pid, ppid, create, image) in WindowsSystem.Processes())
        {
            a.Seed(pid, create, ppid, image);
        }
    }

    /// <summary>
    /// 会话已建、提供程序已启用之后、泵线程开始之前：立即插入会话行（刚开始就被强制结束也能在清理时找到），再列举现有进程。写库失败时抛出。
    /// 服务在外部停止后重开会话时再调用一次，开一条新行（汇总器接着用）。
    /// </summary>
    public void Begin(SessionRecord record)
    {
        _store!.BeginSession(record);
        lock (_gate)
        {
            _record = record;
            _aggregator!.Mode = record.Mode;
            _aggregator.ClearFlags();
            Downgraded = false;
            _window.Clear();
            _base = Clone(_aggregator.Stats);
            _lastFlushUtc = record.StartedUtc;
            _foldFlags = 0;
            _peakEventsPerSecond = 0;
            _peakWs = 0;
            Seed(_aggregator);
        }
    }

    /// <summary>精确模式降到轻量（服务的工作集超过 128 MB 时，和 D9 同一套）。已经是轻量时什么都不做。</summary>
    public void ForceDowngrade()
    {
        lock (_gate)
        {
            if (_aggregator is null || _record is null || _aggregator.Mode != MonitorMode.Precise)
            {
                return;
            }

            _aggregator.Mode = MonitorMode.Light;
            _aggregator.MarkDowngraded();
            Downgraded = true;
            _record.DowngradedUtc = _options.UtcNow();
            _record.Mode = MonitorMode.Light;
        }

        _downgrade?.Invoke();
        _options.Log?.Invoke("monitor downgraded to light (working set)");
    }

    /// <summary>开始每秒的统计和每 30 秒一次的定时落盘检查。</summary>
    public void Run(Func<long> eventsLost, Action downgrade)
    {
        _eventsLost = eventsLost;
        _downgrade = downgrade;
        _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        _wall.Restart();
        _statusTimer = new Timer(_ => SafeTick(), null, 1000, 1000);
        var check = _options.FlushInterval < TimeSpan.FromSeconds(30) ? _options.FlushInterval : TimeSpan.FromSeconds(30); // 验收用的短间隔
        _flushTimer = new Timer(_ => FlushIfDue(), null, check, check);
    }

    /// <summary>监控期间保存了新快照时重新读锚点。</summary>
    public void ReloadAnchors()
    {
        var paths = _options.Anchors?.Load() ?? [];
        if (_aggregator is null)
        {
            return;
        }

        lock (_gate)
        {
            _anchors = Anchors(_aggregator, paths);
        }
    }

    private static HashSet<int> Anchors(MonitorAggregator a, IReadOnlyCollection<string> paths)
    {
        var set = new HashSet<int>();
        foreach (var root in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            set.Add(a.Directories.GetOrAdd(root.Name));
        }

        foreach (var p in paths)
        {
            var id = a.Directories.GetOrAdd(p);
            if (id > 0)
            {
                set.Add(id);
            }
        }

        return set;
    }

    public void OnEvent(RawEvent raw)
    {
        var e = new MonitorEvent();
        var ok = PayloadParser.TryParse(raw, e);
        lock (_gate)
        {
            var key = $"{(raw.Provider == Providers.KernelFile ? "Kernel-File" : "Kernel-Process")}/{raw.Id}/v{raw.Version}";
            _byId[key] = _byId.GetValueOrDefault(key) + 1;
            if (!ok)
            {
                _aggregator!.Stats.ParseErrors++;
                return;
            }

            _reorder.Add(e, _aggregator!.Process);
        }
    }

    /// <summary>测试：送一个已经解析好的事件（经过重排）。</summary>
    internal void OnParsed(MonitorEvent e)
    {
        lock (_gate)
        {
            _reorder.Add(e, _aggregator!.Process);
        }
    }

    /// <summary>每秒的统计（线程池计时器）：异常不能抛到线程池（会让进程退出），整体捕获、记下最后一个（2a 第 5.1.3 节）。</summary>
    private void SafeTick()
    {
        try
        {
            Tick();
            Ticked?.Invoke();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void Tick()
    {
        if (_eventsLost is null || _record is null || _record.EndedUtc is not null)
        {
            return;
        }

        var lost = _eventsLost();
        long received;
        var downgrade = false;
        lock (_gate)
        {
            var delta = lost - _lastLost;
            _lastLost = lost;
            _aggregator!.Stats.Lost += Math.Max(0, delta);
            received = _aggregator.Stats.Received;
            _window.Enqueue((received, lost));
            while (_window.Count > 60)
            {
                _window.Dequeue();
            }

            // D9：抽样已经到 64 倍、最近 60 秒丢失率仍 > 5% 时降到轻量
            var (r0, l0) = _window.Peek();
            var dr = received - r0;
            var dl = lost - l0;
            if (!Downgraded && _aggregator.Mode == MonitorMode.Precise && _aggregator.SampleFactor >= 64 && dr + dl > 0 && (double)dl / (dr + dl) > 0.05)
            {
                downgrade = true;
                _aggregator.Mode = MonitorMode.Light;
                _aggregator.MarkDowngraded();
                Downgraded = true;
                _record.DowngradedUtc = _options.UtcNow();
                _record.Mode = MonitorMode.Light;
            }
        }

        if (downgrade)
        {
            _downgrade?.Invoke();
            _options.Log?.Invoke("monitor downgraded to light");
        }

        _peakEventsPerSecond = Math.Max(_peakEventsPerSecond, received - _lastReceived);
        _lastReceived = received;
        using var self = Process.GetCurrentProcess();
        _peakWs = Math.Max(_peakWs, self.WorkingSet64);
    }

    /// <summary>定时落盘的检查（每 30 秒，线程池计时器；测试直接调用）：每 5 分钟、整点加 60 秒；服务在本地日期变了之后先轮换。</summary>
    public void FlushIfDue()
    {
        try
        {
            if (_record is null || _record.EndedUtc is not null)
            {
                return; // 还没开始，或者这一行已经结束（服务在两次会话之间）
            }

            var now = _options.UtcNow();
            var boundary = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
            if (_options.RotateName is not null && now - boundary >= TimeSpan.FromSeconds(60)
                && _options.ToLocal(boundary).Date > _options.ToLocal(_record.StartedUtc).Date && boundary > _record.StartedUtc)
            {
                Rotate(boundary);
                return;
            }

            var dueByInterval = now - _lastFlushUtc >= _options.FlushInterval;
            var dueByHour = now.Hour != _lastFlushUtc.Hour && now.Minute >= 1;
            if (dueByInterval || dueByHour)
            {
                Flush(final: false);
            }
        }
        catch (Exception ex)
        {
            // 定时落盘也在线程池计时器上：同样整体捕获（写库失败本身在 Flush 里按"连续 3 次"处理）
            LastError = ex.Message;
            _options.Log?.Invoke("monitor timed flush failed: " + ex.GetType().Name);
        }
    }

    /// <summary>停掉两个计时器并等它们的回调退出（最多 3 秒）。</summary>
    public void StopTimers()
    {
        using (var done = new ManualResetEvent(false))
        {
            if (_flushTimer?.Dispose(done) == true)
            {
                done.WaitOne(TimeSpan.FromSeconds(3));
            }
        }

        _flushTimer = null;
        _statusTimer?.Dispose();
        _statusTimer = null;
    }

    /// <summary>结束当前会话行：最后一次落盘（最多等 <paramref name="wait"/>），再按保留策略删最早的会话。会话已经停了之后调用。</summary>
    public void End(string reason, TimeSpan wait)
    {
        if (_record is null)
        {
            return;
        }

        _record.EndedUtc = _options.UtcNow();
        _record.EndReason = reason;
        var final = Task.Run(() => Flush(final: true));
        if (!final.Wait(wait))
        {
            _options.Log?.Invoke("monitor final flush timed out");
        }

        Prune();
    }

    private void Prune()
    {
        try
        {
            _store!.Prune(_options.UtcNow(), TimeSpan.FromDays(Math.Clamp(KeepDays, 1, 3650)), _options.MaxBytes);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            _options.Log?.Invoke("monitor prune failed: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// 落盘：锁里封存小时、合并、复制出要写的行；锁外写 SQLite。成功后丢掉已封存的小时；连续 3 次失败时调用 <see cref="StoreFailed"/>（数据还在内存里，下次再写）。
    /// </summary>
    public void Flush(bool final)
    {
        _flushGate.Wait();
        bool ok;
        try
        {
            var now = _options.UtcNow();
            List<HourData> hours;
            IReadOnlyList<long> sealedHours;
            SessionRecord rec;
            lock (_gate)
            {
                var a = _aggregator!;
                if (final)
                {
                    _reorder.Flush(a.Process);
                    a.Finish(now.Ticks);
                }

                sealedHours = final ? [.. a.Hours.Union(_carry.Keys).Order()] : [.. a.SealBefore(now.Ticks).Union(_carry.Keys.Where(h => h < SealedLimit(now))).Order()];
                hours = Collect(a, a.Hours.Union(_carry.Keys).Order());
                rec = _record!;
                Stamp(rec, a, now);
            }

            ok = Write(rec, hours, final, now);
            if (ok)
            {
                lock (_gate)
                {
                    foreach (var h in sealedHours)
                    {
                        _aggregator!.Drop(h);
                        _carry.Remove(h);
                    }
                }
            }
        }
        finally
        {
            _flushGate.Release();
        }

        if (!final && ok)
        {
            Flushed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>和 <see cref="MonitorAggregator.SealBefore"/> 同样的口径：整点后 60 秒之前的小时算封存。</summary>
    private static long SealedLimit(DateTime now)
    {
        var limit = now.Ticks - TimeSpan.FromSeconds(60).Ticks;
        return limit - limit % TimeSpan.TicksPerHour;
    }

    /// <summary>在锁里调用：把这些小时的计数合并成要落盘的行（含换掉的汇总器留下的）。</summary>
    private List<HourData> Collect(MonitorAggregator a, IEnumerable<long> hourKeys)
    {
        var hours = new List<HourData>();
        foreach (var hour in hourKeys)
        {
            var (rows, flags) = WriteTreeFolder.Fold(a.HourCounters(hour), a.Directories, _anchors);
            _foldFlags |= (int)flags;
            var stored = rows.Select(r => new StoredRow(r.Dir == DirectoryTable.Unresolved ? null : a.Directories.PathOf(r.Dir), a.Chains[r.Chain], r.Counters)).ToList();
            var stats = Clone(a.HourStats(hour));
            var allFlags = (int)flags;
            if (_carry.TryGetValue(hour, out var carried))
            {
                stored = Merge(stored, carried.Rows);
                stats.Add(carried.Stats);
                allFlags |= carried.Flags;
            }

            hours.Add(new HourData(new DateTime(hour, DateTimeKind.Utc), a.Mode, stats, allFlags, stored));
        }

        return hours;
    }

    private static List<StoredRow> Merge(List<StoredRow> a, List<StoredRow> b)
    {
        var map = new Dictionary<(string, ProcessChain), Counters>();
        foreach (var r in a.Concat(b))
        {
            ref var c = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(map, (r.Dir ?? "", r.Chain), out _);
            c.Add(r.Counters);
        }

        return [.. map.Select(kv => new StoredRow(kv.Key.Item1.Length == 0 ? null : kv.Key.Item1, kv.Key.Item2, kv.Value))];
    }

    /// <summary>在锁里调用：更新会话行的统计。</summary>
    private void Stamp(SessionRecord rec, MonitorAggregator a, DateTime now)
    {
        rec.Stats = a.Stats.Minus(_base);
        rec.LastFlushUtc = now;
        rec.PeakEventsPerSecond = _peakEventsPerSecond;
        rec.PeakWorkingSetBytes = _peakWs;
        rec.AvgCpuPermille = (int)Math.Round(CpuPercent * 10);
        rec.Flags = a.Flags | ExtraFlags
            | ((_foldFlags & (int)FoldFlags.BudgetExceeded) != 0 ? SessionFlags.BudgetExceeded : 0)
            | ((_foldFlags & (int)FoldFlags.AnchorsFolded) != 0 ? SessionFlags.AnchorsFolded : 0);
    }

    private bool Write(SessionRecord rec, List<HourData> hours, bool final, DateTime now)
    {
        try
        {
            _store!.Flush(rec, hours, _options.Identity);
            _flushFailures = 0;
            _lastFlushUtc = now;
            return true;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            LastError = ex.Message;
            _options.Log?.Invoke("monitor flush failed: " + ex.GetType().Name);
            if (!final && ++_flushFailures >= 3)
            {
                StoreFailed?.Invoke();
            }

            return false;
        }
    }

    /// <summary>
    /// 按天轮换（在 UTC 整点 <paramref name="boundary"/>）：当前行写入整点之前的小时、结束原因 rotate；新行从整点开始，整点之后的数据都记到新行。
    /// 汇总器的文件状态不清空（跨天的估算增长不断）；目录表或进程链表太大时换一个新的汇总器，还没落盘的小时留给新行合并。
    /// 写库失败时不轮换（数据留在内存里，下次再试）。
    /// </summary>
    private void Rotate(DateTime boundary)
    {
        _flushGate.Wait();
        SessionRecord? next = null;
        SessionRecord old;
        try
        {
            var now = _options.UtcNow();
            List<HourData> hours;
            IReadOnlyList<long> sealedHours;
            lock (_gate)
            {
                var a = _aggregator!;
                a.SettleDeleteOnClose(boundary.Ticks - 1); // 记在旧行的最后一个小时
                sealedHours = [.. a.SealBefore(now.Ticks).Union(_carry.Keys.Where(h => h < boundary.Ticks)).Where(h => h < boundary.Ticks).Order()];
                hours = Collect(a, sealedHours);
                old = _record!;
                Stamp(old, a, now);
                old.EndedUtc = boundary;
                old.EndReason = "rotate";
            }

            if (!Write(old, hours, final: false, now))
            {
                old.EndedUtc = null;
                old.EndReason = null;
                return;
            }

            next = new SessionRecord { Name = _options.RotateName!(boundary), StartedUtc = boundary, PlannedEndUtc = boundary.AddDays(1), Mode = Mode };
            try
            {
                _store!.BeginSession(next);
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                // 旧行已经结束了：重新打开它，下次再轮换
                LastError = ex.Message;
                _options.Log?.Invoke("monitor rotate failed: " + ex.GetType().Name);
                old.EndedUtc = null;
                old.EndReason = null;
                Write(old, [], final: false, now);
                return;
            }

            lock (_gate)
            {
                foreach (var h in sealedHours)
                {
                    _aggregator!.Drop(h);
                    _carry.Remove(h);
                }

                RebuildIfLarge(now);
                _record = next;
                _aggregator!.ClearFlags();
                if (Downgraded)
                {
                    _aggregator.MarkDowngraded();
                }

                _base = Clone(_aggregator.Stats);
                _foldFlags = 0;
                _peakEventsPerSecond = 0;
                _peakWs = 0;
            }

            _cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            _wall.Restart();
            _options.Log?.Invoke($"monitor rotated {old.Name} -> {next.Name}");
        }
        finally
        {
            _flushGate.Release();
        }

        Prune();
        Rotated?.Invoke(old, next);
        Flushed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>在锁里调用：目录表或进程链表太大时换新的汇总器，还没落盘的小时（整点之后的这一点）按路径和进程链留给下一次落盘合并。</summary>
    private void RebuildIfLarge(DateTime now)
    {
        var a = _aggregator!;
        if (a.Directories.Count <= _options.RebuildDirectories && a.Chains.Count <= _options.RebuildChains)
        {
            return;
        }

        _reorder.Flush(a.Process);
        foreach (var h in Collect(a, [.. a.Hours]))
        {
            _carry[h.HourUtc.Ticks] = ([.. h.Rows], h.Stats, h.Flags);
        }

        var fresh = new MonitorAggregator(a.Mode, _devices!, _services, Environment.ProcessId);
        fresh.SealBefore(now.Ticks); // 整点之前的晚到事件记到当前小时，不再出现在新行开始之前
        Seed(fresh);
        fresh.Stats.Add(a.Stats); // 累计的统计接着算（会话行按 _base 取差）
        _aggregator = fresh;
        _anchors = Anchors(fresh, _options.Anchors?.Load() ?? []);
        _options.Log?.Invoke($"monitor aggregator rebuilt dirs={a.Directories.Count} chains={a.Chains.Count}");
    }

    private static MonitorStats Clone(MonitorStats s)
    {
        var c = new MonitorStats();
        c.Add(s);
        return c;
    }

    public void Dispose() => StopTimers();
}

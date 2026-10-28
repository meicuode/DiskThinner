namespace DiskThinner.Monitor.Tests;

/// <summary>
/// 汇总的口径（docs/phase2a-design.md 5.6 节和第 12.2 节第 3–7 条，2a.1 实测得到的规则 V2、V12、V16）：
/// 用合成事件按时间顺序送进 <see cref="MonitorAggregator"/>，看"目录 × 进程链"的计数。
/// </summary>
public class AggregatorTests
{
    private const int Load = 500;
    private const string Dev = @"\Device\HarddiskVolume3";
    private const string Root = @"C:\Temp\w";
    private long _ticks = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc).Ticks;
    private ulong _nextKey = 0x1000;
    private readonly MonitorAggregator _a;

    public AggregatorTests() : this(MonitorMode.Precise)
    {
    }

    private AggregatorTests(MonitorMode mode)
    {
        _a = new MonitorAggregator(mode, new PrefixDeviceMap(new Dictionary<string, string> { [Dev] = "C:" }), selfPid: 999);
        _a.Seed(Load, 1, 0, @"C:\tools\load.exe");
    }

    private static string D(string dosPath) => Dev + dosPath[2..];

    private MonitorEvent E(MonitorEventKind kind, int pid = Load, int tid = 1)
    {
        _ticks += TimeSpan.TicksPerMillisecond;
        return new MonitorEvent { Kind = kind, ProcessId = pid, ThreadId = tid, TimestampUtcTicks = _ticks };
    }

    private void Feed(MonitorEvent e) => _a.Process(e);

    /// <summary>新建文件（CreateNewFile + NameCreate），返回 FileKey。</summary>
    private ulong Create(string path, uint options = 0, int pid = Load)
    {
        var create = E(MonitorEventKind.CreateNewFile, pid);
        create.Path = D(path);
        create.CreateOptions = options;
        Feed(create);
        return Name(path, pid);
    }

    private ulong Name(string path, int pid = Load)
    {
        var key = _nextKey++;
        var name = E(MonitorEventKind.NameCreate, pid);
        name.Path = D(path);
        name.FileKey = key;
        Feed(name);
        return key;
    }

    private void Write(ulong key, ulong offset, uint size, int pid = Load, uint flags = 0x60A00, ulong irp = 1, int tid = 1)
    {
        var w = E(MonitorEventKind.Write, pid, tid);
        w.FileKey = key;
        w.ByteOffset = offset;
        w.IoSize = size;
        w.IoFlags = flags;
        w.Irp = irp;
        Feed(w);
    }

    private void PathEvent(MonitorEventKind kind, string path, ulong key = 0, int pid = Load)
    {
        var e = E(kind, pid);
        e.Path = D(path);
        e.FileKey = key;
        Feed(e);
    }

    /// <summary>某个目录下（含子目录）所有进程链的计数之和。</summary>
    private Counters Under(string dir)
    {
        var total = new Counters();
        var id = _a.Directories.Find(dir);
        foreach (var hour in _a.Hours)
        {
            foreach (var ((d, _), c) in _a.HourCounters(hour))
            {
                if (id > 0 && d > 0 && (_a.Directories.Resolve(d) == id || _a.Directories.IsUnder(d, id)))
                {
                    total.Add(c);
                }
            }
        }

        return total;
    }

    private Counters At(string dir)
    {
        var total = new Counters();
        var id = _a.Directories.Find(dir);
        foreach (var hour in _a.Hours)
        {
            foreach (var ((d, _), c) in _a.HourCounters(hour))
            {
                if (id > 0 && _a.Directories.Resolve(d) == id)
                {
                    total.Add(c);
                }
            }
        }

        return total;
    }

    [Fact]
    public void CreateWriteDeleteNetsToZero()
    {
        var key = Create($@"{Root}\a\f.bin");
        Write(key, 0, 4096);
        Write(key, 4096, 4096);
        var c = At($@"{Root}\a");
        Assert.Equal((1, 2, 8192L, 8192L), (c.Creates, c.WriteOps, c.WriteBytes, c.Growth));

        PathEvent(MonitorEventKind.DeletePath, $@"{Root}\a\f.bin", key);
        c = At($@"{Root}\a");
        Assert.Equal((1L, 0L), (c.Deletes, c.Growth));
        Assert.Equal(8192, c.WriteBytes); // 写入量不减
    }

    [Fact]
    public void ExistingFilesCountWritesButNoGrowth()
    {
        var key = Name($@"{Root}\db\data.db");
        Write(key, 1 << 20, 4096);
        var c = At($@"{Root}\db");
        Assert.Equal((4096L, 0L, 0L), (c.WriteBytes, c.Growth, c.Creates));
    }

    [Fact]
    public void WritesWithoutAKnownNameGoToUnresolved()
    {
        Write(0xDEAD, 0, 65536);
        Assert.Equal(1, _a.Stats.UnresolvedWrites);
        Assert.Equal(65536, _a.Stats.UnresolvedBytes);
        var counters = _a.HourCounters(_a.Hours.Single());
        Assert.Equal(65536, counters.Where(kv => kv.Key.Dir == DirectoryTable.Unresolved).Sum(kv => kv.Value.WriteBytes));
    }

    [Fact]
    public void PagingWritesAreNotCountedTwice()
    {
        // 缓存写入之后 System 的回写（IOFlags 0x60003）：写入量和估算增长都不再算（V2）
        var key = Create($@"{Root}\p\f.bin");
        Write(key, 0, 10_000);
        Write(key, 0, 12_288, pid: 4, flags: 0x60003);
        var c = At($@"{Root}\p");
        Assert.Equal((10_000L, 10_000L), (c.WriteBytes, c.Growth));

        // 只有映射写入（分页）的新文件：估算增长按分页写入算，记到新建者头上
        var mapped = Create($@"{Root}\p\mapped.dll");
        Write(mapped, 0, 8192, pid: 4, flags: 0x60003);
        c = At($@"{Root}\p");
        Assert.Equal((10_000L, 18_192L), (c.WriteBytes, c.Growth));
    }

    [Fact]
    public void SystemNonPagingWritesCountUnderSystem()
    {
        var key = Create($@"{Root}\copy\x.dll");
        Write(key, 0, 65536, pid: 4, flags: 0x60200);
        var system = _a.HourCounters(_a.Hours.Single()).Where(kv => _a.Chains[kv.Key.Chain].Kind == ChainKind.System).Sum(kv => kv.Value.WriteBytes);
        Assert.Equal(65536, system);
    }

    [Fact]
    public void FastIoRetriedAsIrpIsCountedOnce()
    {
        var key = Create($@"{Root}\big\big.bin");
        Write(key, 0, 1 << 20);                                   // IRP（第一次写）
        Write(key, 1 << 20, 1 << 20, flags: 0, irp: 0);            // 快速 I/O
        Write(key, 1 << 20, 1 << 20);                              // 快速 I/O 失败后同样的 IRP 重试
        Write(key, 2 << 20, 1 << 20, flags: 0, irp: 0);            // 快速 I/O 成功
        var c = At($@"{Root}\big");
        Assert.Equal(3L << 20, c.WriteBytes);
        Assert.Equal(3L << 20, c.Growth);
        Assert.Equal(1, _a.Stats.DuplicateWrites);
    }

    [Fact]
    public void AppendSentinelOffsetGrowsByTheWriteSize()
    {
        var key = Create($@"{Root}\append\a.bin");
        Write(key, 0, 4096);
        Write(key, ulong.MaxValue, 4096);
        Write(key, ulong.MaxValue, 4096);
        Assert.Equal(12_288, At($@"{Root}\append").Growth);
        Write(key, 0xFFFF_FFFF_FFFF_FFF0, 16); // 其他异常偏移：不更新
        Assert.Equal(1, _a.Stats.BadOffsets);
    }

    [Fact]
    public void DeleteOnCloseFilesAreCountedAsDeletedOnNameDelete()
    {
        var key = Create($@"{Root}\temp\t.tmp", options: 0x1000);
        Write(key, 0, 65536);
        PathEvent(MonitorEventKind.NameDelete, $@"{Root}\temp\t.tmp", key);
        var c = At($@"{Root}\temp");
        Assert.Equal((1L, 1L, 0L, 65536L), (c.Creates, c.Deletes, c.Growth, c.WriteBytes));
    }

    [Fact]
    public void MovedFileTakesItsGrowthToTheNewDirectory()
    {
        var key = Create($@"{Root}\move\tmp\m.bin");
        Write(key, 0, 65536);
        PathEvent(MonitorEventKind.RenamePath, $@"{Root}\move\dst\m.bin", key);
        Assert.Equal(0, At($@"{Root}\move\tmp").Growth);
        Assert.Equal(65536, At($@"{Root}\move\dst").Growth);
        Assert.Equal(1, At($@"{Root}\move\dst").Renames);

        PathEvent(MonitorEventKind.DeletePath, $@"{Root}\move\dst\m.bin", key);
        Assert.Equal(0, Under($@"{Root}\move").Growth);
    }

    [Fact]
    public void RenameOverAnExistingFileRemovesTheReplacedGrowth()
    {
        var target = Create($@"{Root}\r\target.bin");
        Write(target, 0, 1000);
        var tmp = Create($@"{Root}\r\target.bin.tmp");
        Write(tmp, 0, 3000);
        PathEvent(MonitorEventKind.RenamePath, $@"{Root}\r\target.bin", tmp);
        Assert.Equal(3000, At($@"{Root}\r").Growth);
    }

    [Fact]
    public void RenamedDirectoryOntoExistingNode_KeepsFileStates()
    {
        // 目标目录已经有节点（锚点：上次快照里有、之后被删掉，又有别的目录改名成它）：旧节点并进去，计数和文件状态都跟过去
        _a.Directories.GetOrAdd($@"{Root}\move\done");
        var stageKey = Create($@"{Root}\move\stage", options: 0x1);
        var f = Create($@"{Root}\move\stage\m.bin");
        Write(f, 0, 4096);
        PathEvent(MonitorEventKind.RenamePath, $@"{Root}\move\done", stageKey);
        Assert.Equal(4096, At($@"{Root}\move\done").Growth);

        Write(f, 4096, 4096);
        Assert.Equal(8192, At($@"{Root}\move\done").Growth);
        PathEvent(MonitorEventKind.DeletePath, $@"{Root}\move\done\m.bin", f);
        Assert.Equal(0, At($@"{Root}\move\done").Growth);
    }

    [Fact]
    public void RenamedDirectoryKeepsFileStates()
    {
        var stageKey = Create($@"{Root}\move\stage", options: 0x1); // 目录本身的新建（FILE_DIRECTORY_FILE）
        var f = Create($@"{Root}\move\stage\m.bin");
        Write(f, 0, 4096);
        PathEvent(MonitorEventKind.RenamePath, $@"{Root}\move\done", stageKey);
        Assert.Equal(4096, At($@"{Root}\move\done").Growth);
        Assert.Equal(-1, _a.Directories.Find($@"{Root}\move\stage"));

        // 改名后继续写、再删除：都对得上
        Write(f, 4096, 4096);
        Assert.Equal(8192, At($@"{Root}\move\done").Growth);
        PathEvent(MonitorEventKind.DeletePath, $@"{Root}\move\done\m.bin", f);
        Assert.Equal(0, At($@"{Root}\move\done").Growth);
    }

    [Fact]
    public void OverwritingANewFileRestartsItsGrowth()
    {
        var key = Create($@"{Root}\o\f.bin");
        Write(key, 0, 5000);
        var again = Create($@"{Root}\o\f.bin");
        Write(again, 0, 2000);
        var c = At($@"{Root}\o");
        Assert.Equal((2L, 2000L), (c.Creates, c.Growth));
    }

    [Fact]
    public void DirectoriesCountSeparately()
    {
        Create($@"{Root}\d\sub", options: 0x1);
        var c = At($@"{Root}\d");
        Assert.Equal((0L, 1L), (c.Creates, c.CreatedDirs));
    }

    [Fact]
    public void LightModeIgnoresWrites()
    {
        var light = new AggregatorTests(MonitorMode.Light);
        var key = light.Create($@"{Root}\l\f.bin");
        light.Write(key, 0, 4096);
        var c = light.At($@"{Root}\l");
        Assert.Equal((1L, 0L, 0L), (c.Creates, c.WriteBytes, c.Growth));
    }

    [Fact]
    public void OwnProcessAndUnknownVolumesAreFiltered()
    {
        Create($@"{Root}\self\f.bin", pid: 999);
        var e = E(MonitorEventKind.CreateNewFile);
        e.Path = @"\Device\Mup\server\share\f.bin";
        Feed(e);
        Assert.Equal(-1, _a.Directories.Find($@"{Root}\self"));
        Assert.True(_a.Stats.Filtered >= 3);
    }

    [Fact]
    public void ProcessChainOfTheCreatorGetsTheGrowth()
    {
        // 新建者是 load.exe，之后由 System 映射写入：估算增长记在新建者的进程链上
        var key = Create($@"{Root}\c\f.dll");
        Write(key, 0, 8192, pid: 4, flags: 0x60003);
        var rows = _a.HourCounters(_a.Hours.Single()).Where(kv => kv.Value.Growth != 0).ToList();
        var row = Assert.Single(rows);
        Assert.Equal("load.exe", _a.Chains[row.Key.Chain].WriterExe);
    }

    [Fact]
    public void SamplingScalesWriteBytesButNotGrowth()
    {
        var a = new MonitorAggregator(MonitorMode.Precise, new PrefixDeviceMap(new Dictionary<string, string> { [Dev] = "C:" }), sampleThreshold: 100);
        a.Seed(Load, 1, 0, @"C:\load.exe");
        var t0 = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc).Ticks;
        a.Process(new MonitorEvent { Kind = MonitorEventKind.NameCreate, ProcessId = Load, TimestampUtcTicks = t0, FileKey = 7, Path = D($@"{Root}\s\db") });
        for (var i = 0; i < 1000; i++)
        {
            // 第 1 秒 500 个、第 2 秒 500 个：第 2 秒倍数变成 2
            a.Process(new MonitorEvent { Kind = MonitorEventKind.Write, ProcessId = Load, ThreadId = 1, Irp = 1, IoFlags = 0x60A00, TimestampUtcTicks = t0 + (i < 500 ? 0 : TimeSpan.TicksPerSecond) + i, FileKey = 7, ByteOffset = 0, IoSize = 100 });
        }

        Assert.Equal(2, a.SampleFactor);
        Assert.Equal(250, a.Stats.SampledOut);
        var bytes = a.HourCounters(a.Hours.Single()).Sum(kv => kv.Value.WriteBytes);
        Assert.Equal(100_000, bytes);
        Assert.Contains(a.HourCounters(a.Hours.Single()), kv => kv.Value.Sampled);
    }

    [Fact]
    public void LateEventsAfterTheHourIsSealedGoToTheCurrentHour()
    {
        var key = Create($@"{Root}\h\f.bin");
        var hour0 = _a.Hours.Single();
        _a.SealBefore(hour0 + TimeSpan.TicksPerHour + TimeSpan.FromSeconds(61).Ticks);
        var late = new MonitorEvent { Kind = MonitorEventKind.Write, ProcessId = Load, ThreadId = 1, Irp = 1, IoFlags = 0x60A00, TimestampUtcTicks = hour0 + 10, FileKey = key, IoSize = 10 };
        _a.Process(late);
        Assert.Equal(1, _a.Stats.LateEvents);
        Assert.Contains(hour0 + TimeSpan.TicksPerHour, _a.Hours);
    }

    [Fact]
    public void FinishDeletesRemainingDeleteOnCloseFiles()
    {
        var key = Create($@"{Root}\doc\t.tmp", options: 0x1000);
        Write(key, 0, 100);
        _a.Finish(_ticks);
        Assert.Equal(0, At($@"{Root}\doc").Growth);
        Assert.Equal(1, At($@"{Root}\doc").Deletes);
    }
}

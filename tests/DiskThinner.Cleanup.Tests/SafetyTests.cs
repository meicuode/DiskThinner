using DiskThinner.Rules;
using DiskThinner.Storage;

namespace DiskThinner.Cleanup.Tests;

/// <summary>
/// M4 验收的安全用例（研发方案 M4）：指向保护目录的 junction、执行前路径被替换、超长路径、只读文件、被占用文件、硬链接；
/// 以及保护名单在目录里面的拦截、实际释放和预计一致。
/// </summary>
public sealed class SafetyTests : IDisposable
{
    private const long Kb = 1024;
    private const long Mb = 1024 * 1024;
    private readonly Fixture _f = new();

    public void Dispose() => _f.Dispose();

    private string Cache => Path.Combine(_f.Local, "Cache");

    private static readonly LocationRule CacheRule = Fixture.Rule("t.cache", @"%LOCALAPPDATA%\Cache", Risk.Safe, new DeleteAction(DeleteScope.Contents));

    private Env PlanCache() => _f.Plan([CacheRule]);

    [Fact]
    public void JunctionToAProtectedDirectoryIsRemovedWithoutTouchingTheTarget()
    {
        var key = _f.File(Path.Combine(_f.Profile, ".ssh", "id_ed25519"), 4 * Kb);
        _f.File(Path.Combine(Cache, "a.bin"), 256 * Kb);
        _f.Dir(Cache);
        Fixture.Junction(Path.Combine(Cache, "keys"), Path.Combine(_f.Profile, ".ssh"));

        var env = PlanCache();
        var run = env.Run(env.Item(Cache));

        var r = Assert.Single(run.Results);
        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.Equal(1, r.Delete!.LinksRemoved);
        Assert.False(Directory.Exists(Path.Combine(Cache, "keys")));
        Assert.True(File.Exists(key), "junction 指向的保护目录里的文件不能被删");
        Assert.True(Directory.Exists(Cache), "contents 只清空内容，保留目录本身");
    }

    [Fact]
    public void DirectoryReplacedByAJunctionAfterPlanningIsNotTouched()
    {
        var key = _f.File(Path.Combine(_f.Profile, ".ssh", "id_ed25519"), 4 * Kb);
        _f.File(Path.Combine(Cache, "a.bin"), 256 * Kb);
        var env = PlanCache();
        var item = env.Item(Cache);

        // 确认之后、执行之前：缓存目录被换成指向密钥目录的 junction
        Directory.Move(Cache, Cache + ".old");
        Fixture.Junction(Cache, Path.Combine(_f.Profile, ".ssh"));

        var r = Assert.Single(env.Run(item).Results);
        Assert.Equal(AuditResult.NotRun, r.Result);
        Assert.Contains("链接", r.Text);
        Assert.True(File.Exists(key));
        var log = env.Audit.List();
        Assert.Contains(log, e => e.Action == AuditAction.Blocked && e.Target == Cache && e.Basis.Contains("复核"));
    }

    [Fact]
    public void ParentReplacedByAJunctionIsAlsoRefused()
    {
        _f.File(Path.Combine(Cache, "a.bin"), 256 * Kb);
        var env = PlanCache();
        var item = env.Item(Cache);

        // 上一级（AppData\Local）被换成 junction：路径上任何一级是重解析点都不执行
        Directory.Move(_f.Local, _f.Local + ".real");
        Fixture.Junction(_f.Local, _f.Local + ".real");

        var r = Assert.Single(env.Run(item).Results);
        Assert.Equal(AuditResult.NotRun, r.Result);
        Assert.True(File.Exists(Path.Combine(_f.Local + ".real", "Cache", "a.bin")));
    }

    [Fact]
    public void PathsLongerThanMaxPathAreDeleted()
    {
        var deep = Cache;
        while (deep.Length < 400)
        {
            deep = Path.Combine(deep, "level-" + new string('x', 20));
        }

        var file = _f.File(Path.Combine(deep, "deep.bin"), 128 * Kb);
        Assert.True(file.Length > 400);

        var env = PlanCache();
        var r = Assert.Single(env.Run(env.Item(Cache)).Results);

        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.False(File.Exists(file));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Cache));
    }

    [Fact]
    public void ReadOnlyFilesAndDirectoriesAreDeleted()
    {
        var file = _f.File(Path.Combine(Cache, "ro", "locked-by-attribute.bin"), 128 * Kb, attributes: FileAttributes.ReadOnly);
        File.SetAttributes(Path.Combine(Cache, "ro"), FileAttributes.Directory | FileAttributes.ReadOnly);

        var env = PlanCache();
        var r = Assert.Single(env.Run(env.Item(Cache)).Results);

        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(Path.Combine(Cache, "ro")));
    }

    [Fact]
    public void FilesInUseAreSkippedAndTheHolderIsReported()
    {
        var busy = _f.File(Path.Combine(Cache, "busy.bin"), 128 * Kb);
        var free = _f.File(Path.Combine(Cache, "free.bin"), 128 * Kb);
        var env = PlanCache();

        DeleteOutcome outcome;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = Assert.Single(env.Run(env.Item(Cache)).Results);
            Assert.Equal(AuditResult.Partial, r.Result);
            outcome = r.Delete!;
            Assert.Contains(busy, outcome.Locked);

            // Restart Manager 能查出是谁占用的（这里就是测试进程自己）
            Assert.Contains(RestartManager.WhoLocks([busy]), h => h.ProcessId == Environment.ProcessId);
        }

        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(free));
        Assert.Equal(1, outcome.LockedCount);
        var entry = env.Audit.List().Single(e => e.Action == AuditAction.Delete);
        Assert.Equal(AuditResult.Partial, entry.Result);
        Assert.Contains("被占用", entry.Detail);
    }

    [Fact]
    public void DeletingOneHardLinkKeepsTheOtherNameAndIsNotCountedAsFreed()
    {
        var shared = _f.File(Path.Combine(Cache, "shared.bin"), 1 * Mb);
        _f.File(Path.Combine(Cache, "own.bin"), 512 * Kb);
        var outside = Path.Combine(_f.Root, "elsewhere", "keep.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Fixture.HardLink(outside, shared);

        var env = PlanCache();
        var r = Assert.Single(env.Run(env.Item(Cache)).Results);

        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.False(File.Exists(shared));
        Assert.True(File.Exists(outside));
        Assert.Equal(1 * Mb, new FileInfo(outside).Length);
        Assert.Equal(1 * Mb, r.Delete!.SharedBytes);
        Assert.Equal(512 * Kb, r.Delete.FreedEstimate);
        if (_f.OnTestVolume)
        {
            // 测试盘上没有别的写入：实际释放只有没共享的那个文件
            Assert.InRange(r.FreedBytes, 512 * Kb, 512 * Kb + 256 * Kb);
        }
    }

    /// <summary>
    /// 管理员在测试盘上：用 MFT 扫描整个测试盘，硬链接能被识别，预计释放里不含共享的文件，实际释放和预计一致。
    /// 普通权限或不在测试盘上时不适用（遍历扫描识别不了硬链接），直接返回。
    /// </summary>
    [Fact]
    public void WithMftTheEstimateLeavesOutHardLinkedFiles()
    {
        if (!_f.OnTestVolume || !DiskThinner.Scanner.VolumeScanner.IsElevated())
        {
            return;
        }

        var shared = _f.File(Path.Combine(Cache, "shared.bin"), 2 * Mb);
        _f.File(Path.Combine(Cache, "own.bin"), 3 * Mb);
        var outside = Path.Combine(_f.Root, "elsewhere", "keep.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        Fixture.HardLink(outside, shared);

        var volume = Path.GetPathRoot(_f.Root)!;
        var scan = DiskThinner.Scanner.VolumeScanner.Scan(volume);
        Assert.Equal(DiskThinner.Scanner.ScanMode.Mft, scan.Stats.Mode);
        var engine = new RuleEngine(new RuleSet([], [CacheRule]), _f.Context);
        var c = engine.Classify(scan.Tree);
        var plan = PlanBuilder.Build(scan.Tree, c, new PlanOptions { CurrentProfile = _f.Profile, MinBytes = 1 });
        var item = plan.Items.Single(i => string.Equals(i.Path, Cache, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3 * Mb, item.EstimatedBytes);
        Assert.Equal(2 * Mb, item.SharedBytes);
        Assert.Contains("硬链接", item.Hint);

        var audit = new AuditLog(_f.Db);
        var run = new CleanupExecutor(engine, audit, new ExecutorOptions { RequiredRoot = _f.Root }).Execute([item], null, CancellationToken.None);
        var r = Assert.Single(run.Results);
        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.True(File.Exists(outside));
        Assert.InRange(r.FreedBytes, 3 * Mb - 64 * Kb, 3 * Mb + 256 * Kb);
    }

    [Fact]
    public void ProtectedFilesInsideALocationAreSkippedAndListedAsBlocked()
    {
        var login = _f.File(Path.Combine(Cache, "User Data", "Default", "Login Data"), 64 * Kb);
        _f.File(Path.Combine(Cache, "User Data", "Default", "Cache", "data_1"), 256 * Kb);

        var env = PlanCache();
        var blocked = env.Plan.Items.Single(i => i.Group == PlanGroup.Blocked);
        Assert.Equal(login, blocked.Path, ignoreCase: true);
        Assert.Equal(PlanSource.Protection, blocked.Source);
        Assert.Equal("保护名单", blocked.SourceText);
        Assert.Equal(256 * Kb, env.Item(Cache).EstimatedBytes);

        var r = Assert.Single(env.Run(env.Item(Cache)).Results);
        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.True(File.Exists(login));
        Assert.Equal(1, r.Delete!.ProtectedCount);
        Assert.Contains(env.Audit.List(), e => e.Action == AuditAction.Blocked && e.Basis.StartsWith("保护名单", StringComparison.Ordinal));
    }

    [Fact]
    public void FreedSpaceMatchesThePlan()
    {
        for (var i = 0; i < 10; i++)
        {
            _f.File(Path.Combine(Cache, "pkg" + (i % 3), $"f{i}.bin"), (i + 1) * 256 * Kb);
        }

        _f.File(Path.Combine(Cache, "big.bin"), 8 * Mb);
        var env = PlanCache();
        var item = env.Item(Cache);
        var r = Assert.Single(env.Run(item).Results);

        Assert.Equal(AuditResult.Succeeded, r.Result);
        Assert.Equal(item.EstimatedBytes, r.Delete!.FreedEstimate);
        if (_f.OnTestVolume)
        {
            // 可用空间的实际变化：允许目录索引等元数据带来的少量差别
            Assert.InRange(r.FreedBytes, item.EstimatedBytes - 64 * Kb, item.EstimatedBytes + item.EstimatedBytes / 100 + 256 * Kb);
        }

        var entry = env.Audit.List().Single(e => e.Action == AuditAction.Delete);
        Assert.Equal(r.FreedBytes, entry.FreedBytes);
        Assert.Equal("规则 t.cache", entry.Basis);
    }
}

namespace DiskThinner.Monitor.Tests;

/// <summary>落盘时的目录合并（docs/phase2a-design.md 5.7 节，D6；第 12.2 节第 10 条）。</summary>
public class FoldTests
{
    private readonly DirectoryTable _dirs = new();
    private readonly Dictionary<(int Dir, int Chain), Counters> _counters = [];

    private void Put(string dir, int chain, long bytes, long creates = 0)
    {
        var id = _dirs.GetOrAdd(dir);
        var c = _counters.GetValueOrDefault((id, chain));
        c.WriteBytes += bytes;
        c.WriteOps++;
        c.Creates += creates;
        _counters[(id, chain)] = c;
    }

    private HashSet<int> Anchors(params string[] paths) => [.. paths.Select(_dirs.GetOrAdd)];

    private static Counters Total(IEnumerable<WriteRow> rows)
    {
        var t = new Counters();
        foreach (var r in rows)
        {
            t.Add(r.Counters);
        }

        return t;
    }

    [Fact]
    public void UnderBudgetNothingIsMerged()
    {
        Put(@"C:\a\b\c", 1, 100);
        Put(@"C:\a\x", 2, 200);
        var (rows, flags) = WriteTreeFolder.Fold(_counters, _dirs, Anchors(@"C:\"), budget: 10);
        Assert.Equal(2, rows.Count);
        Assert.Equal(FoldFlags.None, flags);
    }

    [Fact]
    public void SmallLeavesAreMergedUpToTheAnchorsAndTotalsArePreserved()
    {
        var anchors = Anchors(@"C:\", @"C:\Users\demo\AppData\Local");
        for (var i = 0; i < 50; i++)
        {
            Put($@"C:\Users\demo\AppData\Local\app{i}\cache", 1, 1000 + i);
        }

        Put(@"C:\Users\demo\AppData\Local\big\data", 1, 10_000_000);
        var before = Total(_counters.Select(kv => new WriteRow(kv.Key.Dir, kv.Key.Chain, kv.Value)));
        var (rows, flags) = WriteTreeFolder.Fold(_counters, _dirs, anchors, budget: 5);
        Assert.True(rows.Count <= 5, "行数 " + rows.Count);
        Assert.Equal(FoldFlags.None, flags);
        var after = Total(rows);
        Assert.Equal((before.WriteBytes, before.WriteOps), (after.WriteBytes, after.WriteOps));

        // 最大的保留得最细；没有并到锚点以上
        Assert.Contains(rows, r => _dirs.PathOf(r.Dir) == @"C:\Users\demo\AppData\Local\big\data");
        var local = _dirs.Find(@"C:\Users\demo\AppData\Local");
        Assert.All(rows, r => Assert.True(r.Dir == local || _dirs.IsUnder(r.Dir, local), _dirs.PathOf(r.Dir)));
    }

    [Fact]
    public void ManyChainsInOneDirectoryKeepTheHeaviest()
    {
        var anchors = Anchors(@"C:\", @"C:\data");
        for (var chain = 1; chain <= 20; chain++)
        {
            Put(@"C:\data", chain, chain * 1000);
        }

        var (rows, _) = WriteTreeFolder.Fold(_counters, _dirs, anchors, budget: 5);
        Assert.True(rows.Count <= 5);
        Assert.Contains(rows, r => r.Chain == 20);
        Assert.Contains(rows, r => r.Chain == 0); // 其他进程
        Assert.Equal(Enumerable.Range(1, 20).Sum(c => c * 1000L), Total(rows).WriteBytes);
    }

    [Fact]
    public void TooManyAnchorsAreFoldedUnderTheHardLimit()
    {
        var anchorPaths = Enumerable.Range(0, 300).Select(i => $@"C:\a{i % 10}\b{i}").ToList();
        var anchors = Anchors([@"C:\", .. anchorPaths]);
        foreach (var p in anchorPaths)
        {
            Put(p, 1, 1000);
            Put(p, 2, 500);
        }

        var (budgetRows, budgetFlags) = WriteTreeFolder.Fold(_counters, _dirs, anchors, budget: 100, hardLimit: 1000);
        Assert.Equal(FoldFlags.BudgetExceeded, budgetFlags); // 锚点本身就多：不超过硬上限时照样输出
        Assert.True(budgetRows.Count <= 1000);

        var (rows, flags) = WriteTreeFolder.Fold(_counters, _dirs, anchors, budget: 20, hardLimit: 40);
        Assert.True(flags.HasFlag(FoldFlags.AnchorsFolded));
        Assert.True(rows.Count <= 40, "行数 " + rows.Count);
        Assert.Equal(300 * 1500L, Total(rows).WriteBytes);
    }

    [Fact]
    public void UnresolvedRowsArePassedThrough()
    {
        _counters[(DirectoryTable.Unresolved, 3)] = new Counters { WriteBytes = 777, WriteOps = 1 };
        Put(@"C:\x\y", 1, 5);
        var (rows, _) = WriteTreeFolder.Fold(_counters, _dirs, Anchors(@"C:\"), budget: 1);
        Assert.Contains(rows, r => r.Dir == DirectoryTable.Unresolved && r.Counters.WriteBytes == 777);
    }

    [Fact]
    public void FoldingDoesNotChangeTheInput()
    {
        for (var i = 0; i < 30; i++)
        {
            Put($@"C:\p\q{i}", 1, i);
        }

        var copy = new Dictionary<(int, int), Counters>(_counters);
        WriteTreeFolder.Fold(_counters, _dirs, Anchors(@"C:\"), budget: 3);
        Assert.Equal(copy, _counters);
    }
}

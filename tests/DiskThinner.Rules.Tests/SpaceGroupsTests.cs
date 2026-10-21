using DiskThinner.Inventory;
using DiskThinner.Rules;
using static DiskThinner.Rules.Tests.Helpers;
using static DiskThinner.Rules.Tests.OwnerTests;

namespace DiskThinner.Rules.Tests;

/// <summary>"按归属汇总""按类别汇总"：不重叠的划分、同一应用的多处位置合并、未识别按热点列出。</summary>
public class SpaceGroupsTests
{
    private const string Local = @"C:\Users\demo\AppData\Local";
    private const long Mb = 1L << 20;

    private static OwnerMap Sample()
    {
        var tree = Tree(
            ($@"{Local}\uv\cache\a.whl", 300 * Mb),
            ($@"{Local}\Perplexity\Comet\Application\comet.exe", 100 * Mb),
            ($@"{Local}\Perplexity\Comet\User Data\Default\Cache\data_1", 50 * Mb),
            (@"C:\Users\demo\AppData\Roaming\Perplexity\settings.json", 1 * Mb),
            ($@"{Local}\Temp\a.tmp", 20 * Mb),
            (@"C:\Windows\System32\kernel32.dll", 400 * Mb),
            (@"C:\Data\blob.bin", 200 * Mb),
            (@"C:\small.txt", 1));
        var c = ClassifyWith(tree, [new AppInfo("uv", "uv", "Astral", [], [])],
            Rule("dev.uv.cache", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low, Kind.PackageCache) with { AppId = "uv" },
            Rule("gen.cache", MatchMode.Dir, @"%LOCALAPPDATA%\**\Cache", Risk.Safe, Kind.Cache),
            Rule("gen.user-temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp", Risk.Safe, Kind.Temp),
            Rule("sys.windows", MatchMode.Under, @"%WINDIR%", Risk.Never, Kind.System));
        return Build(c, [App("Perplexity Comet", "Perplexity", $@"{Local}\Perplexity\Comet\Application")]);
    }

    [Fact]
    public void ByOwnerIsAPartitionAndMergesLocationsOfOneApp()
    {
        var owners = Sample();
        var tree = owners.Classification.Tree;
        var groups = SpaceGroups.ByOwner(owners);
        Assert.Equal(tree[tree.Root].AllocSize, groups.Sum(g => g.Bytes));

        var perplexity = groups.Single(g => g.Name == "Perplexity Comet");
        Assert.Equal(OwnerSource.Inferred, perplexity.Owner?.Source);
        Assert.Equal(151 * Mb, perplexity.Bytes);

        // 应用目录（没有规则）、里面的缓存（通用规则）、Roaming 里的同名目录（按目录名）是三个位置
        var locations = perplexity.Locations.Select(l => tree.GetPath(l.Node)).ToList();
        Assert.Contains($@"{Local}\Perplexity", locations);
        Assert.Contains($@"{Local}\Perplexity\Comet\User Data\Default\Cache", locations);
        Assert.Contains(@"C:\Users\demo\AppData\Roaming\Perplexity", locations);
        Assert.Equal(50 * Mb, perplexity.CleanableBytes);

        Assert.Equal(300 * Mb, groups.Single(g => g.Name == "uv（Astral）").Bytes);
        Assert.Equal(400 * Mb, groups.Single(g => g.Name == "Windows").Bytes);
        Assert.Equal(20 * Mb, groups.Single(g => g.Name == "多个程序").Bytes);

        // 没有规则也不知道归谁：按热点列出，剩下的零星空间合并成一项
        var unknown = groups.Single(g => g.IsUnknown);
        Assert.Equal(@"C:\Data\blob.bin", tree.GetPath(unknown.Locations[0].Node));
        Assert.Equal(200 * Mb + 1, unknown.Bytes);
        Assert.True(unknown.Locations[^1].IsRemainder);

        // 组按大小排
        Assert.Equal(groups.OrderByDescending(g => g.Bytes).Select(g => g.Key), groups.Select(g => g.Key));
    }

    [Fact]
    public void ByKindGroupsRuleLocationsAndListsUnknownHotspots()
    {
        var owners = Sample();
        var tree = owners.Classification.Tree;
        var groups = SpaceGroups.ByKind(owners);
        Assert.Equal(tree[tree.Root].AllocSize, groups.Sum(g => g.Bytes));

        var cache = groups.Single(g => g.Kind == Kind.Cache);
        Assert.Equal("Perplexity Comet", cache.Locations.Single().Owner?.Name);
        Assert.Equal(300 * Mb, groups.Single(g => g.Kind == Kind.PackageCache).Bytes);

        var unknown = groups.Single(g => g.IsUnknown);
        Assert.Equal("未识别", unknown.Name);
        Assert.Contains(unknown.Locations, l => !l.IsRemainder && tree.GetPath(l.Node).StartsWith($@"{Local}\Perplexity", StringComparison.Ordinal));
        Assert.Contains(unknown.Locations, l => !l.IsRemainder && tree.GetPath(l.Node) == @"C:\Data\blob.bin");
    }

    [Fact]
    public void ManyLocationsAreMergedIntoOneRow()
    {
        var items = Enumerable.Range(0, SpaceGroups.MaxLocationsPerGroup + 5)
            .Select(i => ($@"C:\Users\demo\src\p{i}\__pycache__\m.pyc", (long)(i + 1)))
            .ToArray();
        var c = Classify(Tree(items), Rule("gen.pycache", MatchMode.Dir, @"%USERPROFILE%\**\__pycache__", Risk.Safe, Kind.BuildOutput));
        var group = SpaceGroups.ByKind(OwnerMap.RulesOnly(c)).Single(g => g.Kind == Kind.BuildOutput);

        Assert.Equal(SpaceGroups.MaxLocationsPerGroup + 5, group.LocationCount);
        Assert.Equal(SpaceGroups.MaxLocationsPerGroup + 1, group.Locations.Count);
        var rest = group.Locations[^1];
        Assert.True(rest.IsRemainder);
        Assert.Equal(5, rest.MergedCount);
        Assert.Equal(1 + 2 + 3 + 4 + 5, rest.Bytes); // 最小的 5 个
        Assert.Equal(items.Sum(i => i.Item2), group.Bytes);
    }
}

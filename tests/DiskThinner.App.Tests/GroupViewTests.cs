using DiskThinner.App.Explorer;
using DiskThinner.Inventory;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.App.Tests;

/// <summary>推断出的归属在界面上的表现，以及"按归属汇总""按类别汇总"两个视图的联动。</summary>
[Collection(UiTestCollection.Name)]
public class GroupViewTests
{
    private const string Local = @"Users\demo\AppData\Local";

    private static LocationRule Rule(string id, MatchMode mode, string pattern, Risk risk, Kind kind) => new()
    {
        Id = id,
        Mode = mode,
        Pattern = pattern,
        Kind = kind,
        Risk = risk,
        Tier = Tier.Reviewed,
        Purpose = id + " 的作用",
        Impact = id + " 删除后的影响",
        Actions = risk <= Risk.Medium ? [new DeleteAction(DeleteScope.Contents)] : [],
        Evidence = [new Evidence("https://example.com/doc", null)],
    };

    /// <summary>uv 缓存（规则写明应用）、FooTool（按程序版本信息推断）、临时文件（通用规则）、一个没人认识的大文件。</summary>
    private static (ScanResult Result, Classification C, OwnerMap Owners, NodeTree Tree) Sample()
    {
        var tree = TestTrees.Build(
            ($@"{Local}\uv\cache\a.whl", 4L << 30),
            ($@"{Local}\FooTool\bin\foo.exe", 2L << 20),
            ($@"{Local}\FooTool\data.bin", 300L << 20),
            ($@"{Local}\Temp\t.tmp", 1L << 30),
            (@"Unknown\blob.bin", 2L << 30));
        var rules = new RuleSet(
            [new AppInfo("uv", "uv", "Astral", ["uv.exe"], [])],
            [
                Rule("dev.uv.cache", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low, Kind.PackageCache) with { AppId = "uv" },
                Rule("gen.user-temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp", Risk.Safe, Kind.Temp),
            ]);
        var c = new RuleEngine(rules, PathContext.Default).Classify(tree);
        var exe = $@"C:\{Local}\FooTool\bin\foo.exe";
        var owners = OwnerMap.Build(c, InventorySnapshot.Empty, new OwnerInferenceOptions
        {
            ReadIdentity = p => p == exe ? new ExeIdentity(p, "Foo Studio", "Foo", null, "1.0") : null,
            ResolveIndirect = _ => null,
        });
        var stats = new ScanStats { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = 50L << 30 };
        return (new ScanResult(tree, stats), c, owners, tree);
    }

    [Fact]
    public void ExplorerShowsInferredOwnerWithEvidence() => UiThread.Run(() =>
    {
        var (result, c, owners, tree) = Sample();
        var page = new ExplorerPage();
        using var form = UiThread.Host(page);
        page.SetResult(result, c, owners);

        var foo = tree.FindPath($@"C:\{Local}\FooTool");
        page.Reveal(foo);
        Assert.Equal(("Foo Studio", "未识别"), page.List.Describe(foo));
        Assert.Equal(("uv（Astral）", "包缓存 需重新下载"), page.List.Describe(tree.FindPath($@"C:\{Local}\uv\cache")));
        Assert.Equal(("多个程序", "临时文件 安全"), page.List.Describe(tree.FindPath($@"C:\{Local}\Temp")));

        var texts = page.Detail.Texts.ToList();
        Assert.Contains("Foo Studio", texts);
        Assert.Contains("推断", texts);
        Assert.Contains(texts, t => t.Contains(@"bin\foo.exe：产品名 Foo Studio", StringComparison.Ordinal));
    });

    [Fact]
    public void GroupedViewsSelectGroupsAndLocations() => UiThread.Run(() =>
    {
        var (result, c, owners, tree) = Sample();
        var page = new ExplorerPage();
        using var form = UiThread.Host(page);
        page.SetResult(result, c, owners);
        var cache = tree.FindPath($@"C:\{Local}\uv\cache");

        // 按归属汇总：选中组显示汇总，选中位置联动详情
        page.ViewIndex = 1;
        var list = page.GroupList;
        Assert.True(list.ByOwner);
        var uvRow = list.RowOfGroup("n:uv");
        Assert.True(uvRow >= 0);
        list.SelectRowAsUser(uvRow);
        Assert.Equal("uv（Astral）", page.Detail.ShownGroup?.Name);
        Assert.Contains("按归属汇总", page.Crumb.Segments);
        Assert.Contains("处", list.Describe(uvRow)[5], StringComparison.Ordinal);

        var cacheRow = list.RowOfNode(cache);
        Assert.True(cacheRow > uvRow); // 最大的一组默认展开
        list.SelectRowAsUser(cacheRow);
        Assert.Equal(cache, page.SelectedNode);
        Assert.Equal(cache, page.Detail.ShownNode);

        // 从方块图等处选中位置里的文件：汇总列表选中它所在的位置
        page.Reveal(tree.FindPath($@"C:\{Local}\uv\cache\a.whl"));
        Assert.Equal(cache, list.SelectedNode);

        // 双击位置：回到按目录视图并定位
        list.Activate(list.RowOfNode(cache));
        Assert.Equal(0, page.ViewIndex);
        Assert.Equal(cache, page.List.SelectedNode);

        // 按类别汇总 + 只看未识别
        page.ViewIndex = 2;
        Assert.False(list.ByOwner);
        Assert.True(list.RowOfGroup("k:" + Names.Code(Kind.PackageCache)) >= 0);
        page.OnlyUnknown.Checked = true;
        Assert.Equal(-1, list.RowOfGroup("k:" + Names.Code(Kind.PackageCache)));
        Assert.True(list.RowOfGroup("unknown") >= 0);
    });
}

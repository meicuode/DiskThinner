using DiskThinner.App.Explorer;
using DiskThinner.App.Pages;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.App.Tests;

/// <summary>规则结果在界面上的表现：主窗口能正常创建，概览和空间浏览显示规则给出的信息，过滤器工作正常。</summary>
[Collection(UiTestCollection.Name)]
public class RulesUiTests
{
    private static LocationRule Rule(string id, MatchMode mode, string pattern, Risk risk, Kind kind, params RuleAction[] actions) => new()
    {
        Id = id,
        Mode = mode,
        Pattern = pattern,
        Kind = kind,
        Risk = risk,
        Tier = Tier.Reviewed,
        Purpose = id + " 的作用，后面是补充说明",
        Impact = id + " 删除后的影响",
        Actions = actions,
        Evidence = [new Evidence("https://example.com/doc", null)],
    };

    /// <summary>示例：uv 缓存（可清理）、分页文件（只能系统处理）、一个没人认识的大文件。</summary>
    private static (ScanResult Result, Classification C, NodeTree Tree) Sample()
    {
        var tree = TestTrees.Build(
            (@"Users\demo\AppData\Local\uv\cache\a.whl", 4L << 30),
            (@"Users\demo\AppData\Local\Temp\t.tmp", 1L << 30),
            (@"Users\demo\Documents\thesis.docx", 900),
            (@"Unknown\blob.bin", 2L << 30),
            (@"pagefile.sys", 3L << 30));
        var rules = new RuleSet(
            [new AppInfo("astral.uv", "uv", "Astral", ["uv.exe"], [])],
            [
                Rule("dev.uv.cache", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low, Kind.PackageCache, new CommandAction("uv.cache-clean")) with
                {
                    AppId = "astral.uv",
                    Relocate = new Relocation("UV_CACHE_DIR", null),
                },
                Rule("gen.user-temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp", Risk.Safe, Kind.Temp, new DeleteAction(DeleteScope.Contents, 1)),
                Rule("sys.pagefile", MatchMode.File, @"%SYSTEMDRIVE%\pagefile.sys", Risk.Guide, Kind.System, new GuideAction("在系统设置里调整")),
            ]);
        var c = new RuleEngine(rules, PathContext.Default).Classify(tree);
        var stats = new ScanStats { Mode = ScanMode.Mft, Root = @"C:\", VolumeTotalBytes = 200L << 30, VolumeFreeBytes = 50L << 30 };
        return (new ScanResult(tree, stats), c, tree);
    }

    [Fact]
    public void MainFormCanBeCreated() => UiThread.Run(() =>
    {
        using var form = new MainForm(new AppOptions());
        Assert.Equal("DiskThinner", form.Text);
    });

    [Fact]
    public void OverviewShowsCleanableSuggestionsAndLargest() => UiThread.Run(() =>
    {
        var (result, c, tree) = Sample();
        var page = new OverviewPage();
        using var form = UiThread.Host(page);
        page.SetResult(result, c);

        Assert.Equal("5.00 GB", page.CleanableCard.ValueText);

        // 建议先处理：可清理的按大小排在前面，然后是系统设置类的大项，最后是未识别
        var suggestions = page.Suggestions.Items;
        Assert.Equal(tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache"), suggestions[0].Node);
        Assert.Contains("uv", suggestions[0].Cells[0], StringComparison.Ordinal);
        Assert.Contains(suggestions, s => s.Node == tree.FindPath(@"C:\pagefile.sys") && s.Tag == "系统工具处理");
        Assert.Equal(tree.FindPath(@"C:\Unknown\blob.bin"), suggestions[^1].Node);
        Assert.Equal("未识别", suggestions[^1].Tag);

        // 占用最多：规则位置和未识别位置混在一起按大小排
        var largest = page.Largest.Items.Select(i => i.Node).ToList();
        Assert.Equal(tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache"), largest[0]);
        Assert.Equal(tree.FindPath(@"C:\pagefile.sys"), largest[1]);
        Assert.Equal(tree.FindPath(@"C:\Unknown\blob.bin"), largest[2]);

        var navigated = -1;
        page.NavigateRequested += (_, n) => navigated = n;
        page.Largest.ActivateAt(1);
        Assert.Equal(tree.FindPath(@"C:\pagefile.sys"), navigated);
    });

    [Fact]
    public void ExplorerShowsRuleColumnsAndDetail() => UiThread.Run(() =>
    {
        var (result, c, tree) = Sample();
        var page = new ExplorerPage();
        using var form = UiThread.Host(page);
        page.SetResult(result, c);

        var cache = tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache");
        page.Reveal(cache);
        Assert.Equal(("uv（Astral）", "包缓存 需重新下载"), page.List.Describe(cache));
        Assert.Equal(("未识别", ""), page.List.Describe(tree.FindPath(@"C:\Unknown\blob.bin")));

        var texts = page.Detail.Texts.ToList();
        Assert.Contains(texts, t => t.Contains("dev.uv.cache 的作用", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("运行 uv cache clean", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("UV_CACHE_DIR", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("打开所在位置", StringComparison.Ordinal));
    });

    [Fact]
    public void FiltersShowOnlyCleanableOrUnknown() => UiThread.Run(() =>
    {
        var (result, c, tree) = Sample();
        var page = new ExplorerPage();
        using var form = UiThread.Host(page);
        page.SetResult(result, c);

        page.OnlyCleanable.Checked = true;
        page.List.SelectNode(tree.FindPath(@"C:\Users\demo\AppData\Local\Temp\t.tmp"));
        Assert.True(page.List.RowOf(tree.FindPath(@"C:\Users")) >= 0);
        Assert.Equal(-1, page.List.RowOf(tree.FindPath(@"C:\Unknown")));
        Assert.Equal(-1, page.List.RowOf(tree.FindPath(@"C:\pagefile.sys")));

        page.OnlyUnknown.Checked = true;
        Assert.False(page.OnlyCleanable.Checked); // 两个过滤互斥
        Assert.True(page.List.RowOf(tree.FindPath(@"C:\Unknown")) >= 0);
        Assert.Equal(-1, page.List.RowOf(tree.FindPath(@"C:\pagefile.sys")));

        // 从别处要求选中一个被过滤掉的节点：自动取消过滤
        page.Reveal(tree.FindPath(@"C:\pagefile.sys"));
        Assert.False(page.OnlyUnknown.Checked);
        Assert.Equal(tree.FindPath(@"C:\pagefile.sys"), page.List.SelectedNode);
    });
}

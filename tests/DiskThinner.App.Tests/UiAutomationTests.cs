using System.Diagnostics;
using System.Text;
using System.Windows.Automation;
using Xunit.Abstractions;

namespace DiskThinner.App.Tests;

/// <summary>
/// 读屏软件看到的界面（研发方案 4 节可访问性）：在另一个进程里启动主程序扫描一个测试文件夹，用 UI Automation
/// 像"讲述人"一样遍历每个页面的控件树（控件视图），检查能拿到焦点的和可以操作的控件都有名字。
/// 读出来是否通顺、焦点顺序是否合理，还是要用讲述人实际听一遍（docs/known-issues.md 里的人工检查清单）。
/// </summary>
[Collection(UiTestCollection.Name)]
public sealed class UiAutomationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-uia-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ITestOutputHelper _output;
    private Process? _app;

    /// <summary>这些类型的控件是拿来操作或者读内容的，一定要有名字。</summary>
    private static readonly ControlType[] NeedsName =
    [
        ControlType.Button, ControlType.CheckBox, ControlType.RadioButton, ControlType.Hyperlink, ControlType.MenuItem,
        ControlType.TabItem, ControlType.ListItem, ControlType.TreeItem, ControlType.DataItem, ControlType.ComboBox,
        ControlType.Edit, ControlType.Spinner, ControlType.Slider, ControlType.List, ControlType.Tree, ControlType.DataGrid,
        ControlType.Table, ControlType.Image,
    ];

    public UiAutomationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_dir, "data", "Users", "demo", "AppData", "Local", "Temp"));
        Directory.CreateDirectory(Path.Combine(_dir, "data", "Program Files", "App"));
        File.WriteAllBytes(Path.Combine(_dir, "data", "Users", "demo", "AppData", "Local", "Temp", "a.tmp"), new byte[300_000]);
        File.WriteAllBytes(Path.Combine(_dir, "data", "Users", "demo", "big.bin"), new byte[900_000]);
        File.WriteAllBytes(Path.Combine(_dir, "data", "Program Files", "App", "app.exe"), new byte[500_000]);
        File.WriteAllBytes(Path.Combine(_dir, "data", "readme.txt"), new byte[1000]);
    }

    public void Dispose()
    {
        if (_app is { HasExited: false })
        {
            _app.CloseMainWindow();
            if (!_app.WaitForExit(10_000))
            {
                _app.Kill(); // 测试自己启动的程序，关不掉时结束它
                _app.WaitForExit(10_000);
            }
        }

        _app?.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 程序刚退出时数据库文件可能还没释放；临时目录，留着也无妨
        }
    }

    /// <summary>启动主程序扫描测试文件夹并打开 <paramref name="page"/>，等扫描完、写完报告，返回主窗口。</summary>
    private AutomationElement Launch(string page)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "DiskThinner.exe");
        var report = Path.Combine(_dir, $"report-{page}.txt");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[]
        {
            "--scan", Path.Combine(_dir, "data"), "--page", page, "--report", report,
            "--settings", Path.Combine(_dir, "settings.json"), "--snapshot-db", Path.Combine(_dir, "snap.db"),
            "--user-db", Path.Combine(_dir, "user.db"), "--no-auto-snapshot", "--primary-screen",
        })
        {
            psi.ArgumentList.Add(a);
        }

        _app = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();
        while (!File.Exists(report))
        {
            Assert.False(_app.HasExited, "主程序提前退出了，退出码 " + (_app.HasExited ? _app.ExitCode : 0));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "等扫描完成超时");
            Thread.Sleep(100);
        }

        _app.Refresh();
        Assert.NotEqual(0, _app.MainWindowHandle);
        return AutomationElement.FromHandle(_app.MainWindowHandle);
    }

    private sealed record Item(int Depth, AutomationElement Element, ControlType Type, string Name, bool Focusable, bool Enabled, bool Offscreen, string Id, string Path);

    /// <summary>按控件视图（讲述人默认的浏览方式）遍历，最多 3000 个元素。</summary>
    private static List<Item> Walk(AutomationElement root)
    {
        var items = new List<Item>();
        var walker = TreeWalker.ControlViewWalker;
        var stack = new Stack<(AutomationElement Element, int Depth, string Path)>([(root, 0, "")]);
        while (stack.Count > 0 && items.Count < 3000)
        {
            var (e, depth, parentPath) = stack.Pop();
            Item item;
            try
            {
                var c = e.Current;
                var name = c.Name ?? "";
                var label = string.IsNullOrEmpty(name) ? $"<{c.ControlType.ProgrammaticName}>" : name;
                item = new Item(depth, e, c.ControlType, name, c.IsKeyboardFocusable, c.IsEnabled, c.IsOffscreen, c.AutomationId ?? "", parentPath + " / " + label);
            }
            catch (ElementNotAvailableException)
            {
                continue;
            }

            items.Add(item);
            var children = new List<AutomationElement>();
            try
            {
                for (var child = walker.GetFirstChild(e); child is not null && children.Count < 500; child = walker.GetNextSibling(child))
                {
                    children.Add(child);
                }
            }
            catch (ElementNotAvailableException)
            {
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], depth + 1, item.Path));
            }
        }

        return items;
    }

    private void Dump(string page, List<Item> items)
    {
        var sb = new StringBuilder($"== {page}：{items.Count} 个元素\n");
        foreach (var i in items)
        {
            sb.Append(' ', i.Depth * 2).Append(i.Type.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal))
                .Append(" \"").Append(i.Name).Append('"')
                .Append(i.Focusable ? " 可聚焦" : "").Append(i.Enabled ? "" : " 禁用").Append(i.Offscreen ? " 屏幕外" : "")
                .Append(i.Id.Length > 0 ? " #" + i.Id : "").Append('\n');
        }

        _output.WriteLine(sb.ToString());
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("explorer")]
    [InlineData("cleanup")]
    [InlineData("growth")]
    [InlineData("log")]
    [InlineData("settings")]
    public void EveryInteractiveElementHasAName(string page)
    {
        var items = Walk(Launch(page));
        Dump(page, items);

        // 列表里内容为空的单元格（例如没有类别的行）本来就是空的，不算问题
        static bool EmptyCell(Item i) => i.Type == ControlType.Text && i.Id.StartsWith("ListViewSubItem", StringComparison.Ordinal);
        var problems = items
            .Where(i => !i.Offscreen && i.Enabled && string.IsNullOrWhiteSpace(i.Name) && (i.Focusable || NeedsName.Contains(i.Type)) && !EmptyCell(i))
            .Select(i => $"{i.Type.ProgrammaticName}{(i.Focusable ? "（可聚焦）" : "")} 没有名字：{i.Path}")
            .ToList();
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(30)));

        // 导航的六个页面都读得出来
        foreach (var nav in new[] { "概览", "空间浏览", "增长分析", "清理计划", "操作记录", "设置" })
        {
            Assert.Contains(items, i => i.Type == ControlType.RadioButton && i.Name == nav);
        }
    }

    [Fact]
    public void TreemapIsReachableAndDescribed()
    {
        var items = Walk(Launch("explorer"));
        var map = Assert.Single(items, i => i.Name == "方块图");
        Assert.True(map.Focusable, "方块图要能用 Tab 到达");
        var help = (string)map.Element.GetCurrentPropertyValue(AutomationElement.HelpTextProperty);
        Assert.Contains("方向键", help);
        Assert.Contains(items, i => i.Name == "文件树" && i.Focusable);
        Assert.Contains(items, i => i.Name == "当前位置");
    }
}

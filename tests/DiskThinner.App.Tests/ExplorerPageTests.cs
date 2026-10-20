using System.Runtime.ExceptionServices;
using DiskThinner.App.Explorer;
using DiskThinner.Scanner;

namespace DiskThinner.App.Tests;

/// <summary>
/// 空间浏览页的联动和键盘操作：在 STA 线程上创建真实的窗体和控件（放在屏幕外），直接驱动各部分，
/// 检查树形列表、方块图、面包屑、详情是否同步。
/// </summary>
public class ExplorerPageTests
{
    private static void RunOnSta(Action<ExplorerPage, NodeTree> test)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new Form
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(-4000, -4000),
                    Size = new Size(1300, 850),
                    ShowInTaskbar = false,
                };
                var page = new ExplorerPage { Dock = DockStyle.Fill };
                form.Controls.Add(page);
                form.Show();
                Application.DoEvents();

                var tree = TestTrees.Sample();
                page.SetResult(new ScanResult(tree, new ScanStats { Mode = ScanMode.Mft, Root = @"C:\" }));
                Application.DoEvents();
                test(page, tree);
                form.Close();
            }
            catch (Exception ex)
            {
                error = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    [Fact]
    public void SelectingInTreeUpdatesMapDetailAndBreadcrumb() => RunOnSta((page, tree) =>
    {
        var users = tree.FindPath(@"C:\Users");
        page.List.SelectRowAsUser(page.List.RowOf(users));

        Assert.Equal(users, page.SelectedNode);
        Assert.Equal(users, page.Map.SelectedNode);
        Assert.Equal(users, page.Detail.ShownNode);
        Assert.Equal("Users", page.Crumb.Segments.Last());
    });

    [Fact]
    public void ClickingMapRevealsNodeInTree() => RunOnSta((page, tree) =>
    {
        var iso = tree.FindPath(@"C:\Users\demo\big.iso");
        var bounds = page.Map.BoundsOf(iso);
        Assert.False(bounds.IsEmpty, "big.iso 应该在方块图里有自己的块");

        page.Map.ClickAt(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));

        Assert.Equal(iso, page.SelectedNode);
        Assert.Equal(iso, page.List.SelectedNode);
        Assert.True(page.List.IsExpanded(tree.FindPath(@"C:\Users\demo")));
        Assert.Equal(iso, page.Detail.ShownNode);
    });

    [Fact]
    public void TreeKeyboardNavigation() => RunOnSta((page, tree) =>
    {
        var windows = tree.FindPath(@"C:\Windows");
        var system32 = tree.FindPath(@"C:\Windows\System32");

        // 根已展开：→ 进入第一个子项
        Assert.True(page.List.HandleKey(Keys.Right));
        Assert.Equal(windows, page.SelectedNode);

        // → 展开 Windows，再 → 进入 System32
        page.List.HandleKey(Keys.Right);
        Assert.True(page.List.IsExpanded(windows));
        page.List.HandleKey(Keys.Right);
        Assert.Equal(system32, page.SelectedNode);
        Assert.Equal(system32, page.Map.SelectedNode);

        // ← 未展开时回到上级；再 ← 折叠
        page.List.HandleKey(Keys.Left);
        Assert.Equal(windows, page.SelectedNode);
        page.List.HandleKey(Keys.Left);
        Assert.False(page.List.IsExpanded(windows));

        // Backspace 回到上级
        page.List.HandleKey(Keys.Back);
        Assert.Equal(tree.Root, page.SelectedNode);
    });

    [Fact]
    public void MapKeyboardNavigationAndZoom() => RunOnSta((page, tree) =>
    {
        var windows = tree.FindPath(@"C:\Windows");
        var users = tree.FindPath(@"C:\Users");

        page.Map.HandleKey(Keys.Down);           // 根 → 最大的子项
        Assert.Equal(windows, page.SelectedNode);
        page.Map.HandleKey(Keys.Right);          // 下一个同级
        Assert.Equal(users, page.SelectedNode);
        Assert.Equal(users, page.List.SelectedNode);

        page.Map.HandleKey(Keys.Enter);          // 进入 Users
        Assert.Equal(users, page.Map.MapRoot);
        page.Map.HandleKey(Keys.Back);           // 返回上一级
        Assert.Equal(tree.Root, page.Map.MapRoot);

        page.Map.HandleKey(Keys.Up);             // 上级
        Assert.Equal(tree.Root, page.SelectedNode);
    });

    [Fact]
    public void SelectingOutsideMapScopeResetsScope() => RunOnSta((page, tree) =>
    {
        var windows = tree.FindPath(@"C:\Windows");
        var users = tree.FindPath(@"C:\Users");
        page.Map.SetMapRoot(windows);
        Assert.Equal(windows, page.Map.MapRoot);

        page.List.SelectRowAsUser(page.List.RowOf(users));
        Assert.Equal(tree.Root, page.Map.MapRoot);
        Assert.Equal(users, page.Map.SelectedNode);
    });

    [Fact]
    public void BreadcrumbJumpsToAncestor() => RunOnSta((page, tree) =>
    {
        var file = tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache\x.whl");
        page.SelectPath(@"C:\Users\demo\AppData\Local\uv\cache\x.whl");
        Assert.Equal(file, page.SelectedNode);
        // 8 级超过上限 7 级，最前面的 C:\ 折叠成"…"
        Assert.Equal(["…", "›", "Users", "›", "demo", "›", "AppData", "›", "Local", "›", "uv", "›", "cache", "›", "x.whl"], page.Crumb.Segments);

        page.Crumb.ClickSegment("AppData");
        var appData = tree.FindPath(@"C:\Users\demo\AppData");
        Assert.Equal(appData, page.SelectedNode);
        Assert.Equal(appData, page.List.SelectedNode);
    });
}

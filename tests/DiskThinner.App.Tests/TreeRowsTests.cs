using DiskThinner.App.Explorer;

namespace DiskThinner.App.Tests;

public class TreeRowsTests
{
    [Fact]
    public void StartsWithRootOnly()
    {
        var rows = new TreeRows(TestTrees.Sample());
        Assert.Equal(1, rows.Count);
        Assert.Equal(rows.Tree.Root, rows.NodeAt(0));
    }

    [Fact]
    public void ExpandInsertsChildrenSortedBySize()
    {
        var tree = TestTrees.Sample();
        var rows = new TreeRows(tree);
        Assert.Equal(3, rows.Expand(0)); // Windows 900、Users 750、pagefile.sys 350
        Assert.Equal(["Windows", "Users", "pagefile.sys"], Enumerable.Range(1, 3).Select(r => tree.GetNameString(rows.NodeAt(r))));
        Assert.All(Enumerable.Range(1, 3), r => Assert.Equal(1, rows.DepthAt(r)));
    }

    [Fact]
    public void CollapseRemovesWholeSubtree()
    {
        var tree = TestTrees.Sample();
        var rows = new TreeRows(tree);
        var uvCache = tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache");
        rows.Reveal(uvCache);
        var before = rows.Count;
        var usersRow = rows.FindVisibleRow(tree.FindPath(@"C:\Users"));
        var removed = rows.Collapse(usersRow);
        Assert.Equal(before - removed, rows.Count);
        Assert.Equal(-1, rows.FindVisibleRow(uvCache));
    }

    [Fact]
    public void ReExpandRestoresPreviouslyExpandedDescendants()
    {
        var tree = TestTrees.Sample();
        var rows = new TreeRows(tree);
        var uv = tree.FindPath(@"C:\Users\demo\AppData\Local\uv");
        rows.Reveal(uv);
        var usersRow = rows.FindVisibleRow(tree.FindPath(@"C:\Users"));
        rows.Collapse(usersRow);
        rows.Expand(usersRow);
        Assert.True(rows.FindVisibleRow(uv) > usersRow);
    }

    [Fact]
    public void RevealExpandsAncestorsAndReturnsRow()
    {
        var tree = TestTrees.Sample();
        var rows = new TreeRows(tree);
        var file = tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache\x.whl");
        var row = rows.Reveal(file);
        Assert.True(row > 0);
        Assert.Equal(file, rows.NodeAt(row));
        Assert.Equal(7, rows.DepthAt(row)); // Users、demo、AppData、Local、uv、cache、x.whl
        Assert.True(rows.IsExpanded(tree.FindPath(@"C:\Users\demo\AppData")));
    }

    [Fact]
    public void ParentRowAndSubtreeEnd()
    {
        var tree = TestTrees.Sample();
        var rows = new TreeRows(tree);
        var row = rows.Reveal(tree.FindPath(@"C:\Windows\System32\b.dll"));
        var parent = rows.ParentRow(row);
        Assert.Equal(tree.FindPath(@"C:\Windows\System32"), rows.NodeAt(parent));
        Assert.Equal(-1, rows.ParentRow(0));

        var windowsRow = rows.FindVisibleRow(tree.FindPath(@"C:\Windows"));
        var end = rows.SubtreeEnd(windowsRow);
        Assert.Equal(tree.FindPath(@"C:\Users"), rows.NodeAt(end));
    }

    [Fact]
    public void ExpandingFileDoesNothing()
    {
        var tree = TestTrees.Sample();
        var rows = new TreeRows(tree);
        rows.Expand(0);
        var pagefileRow = rows.FindVisibleRow(tree.FindPath(@"C:\pagefile.sys"));
        Assert.Equal(0, rows.Expand(pagefileRow));
    }
}

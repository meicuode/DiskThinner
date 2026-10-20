using DiskThinner.App.Explorer;
using DiskThinner.Scanner;

namespace DiskThinner.App.Tests;

public class TreemapLayoutTests
{
    private static readonly RectangleF Bounds = new(0, 0, 800, 400);

    [Fact]
    public void RootFillsBounds()
    {
        var tree = TestTrees.Sample();
        var rects = TreemapLayout.Compute(tree, tree.Root, Bounds);
        Assert.Equal(tree.Root, rects[0].Node);
        Assert.Equal(Bounds, rects[0].Bounds);
        Assert.False(rects[0].Leaf);
    }

    [Fact]
    public void ChildrenStayInsideParentAndDoNotOverlap()
    {
        var tree = TestTrees.Sample();
        var rects = TreemapLayout.Compute(tree, tree.Root, Bounds);
        var byNode = rects.ToDictionary(r => r.Node);
        foreach (var r in rects.Skip(1))
        {
            var parent = byNode[tree[r.Node].Parent].Bounds;
            Assert.True(RectangleF.Inflate(parent, 0.01f, 0.01f).Contains(r.Bounds), $"{tree.GetPath(r.Node)} 超出父目录");
        }

        foreach (var group in rects.Skip(1).GroupBy(r => tree[r.Node].Parent))
        {
            var list = group.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    var overlap = RectangleF.Intersect(list[i].Bounds, list[j].Bounds);
                    Assert.True(overlap.Width * overlap.Height < 0.01f, $"{tree.GetPath(list[i].Node)} 与 {tree.GetPath(list[j].Node)} 重叠");
                }
            }
        }
    }

    [Fact]
    public void AreaIsProportionalToSize()
    {
        var tree = TestTrees.Sample();
        var rects = TreemapLayout.Compute(tree, tree.Root, Bounds);
        var inner = RectangleF.Inflate(Bounds, -TreemapLayout.DirectoryInset, -TreemapLayout.DirectoryInset);
        var scale = inner.Width * inner.Height / tree[tree.Root].AllocSize;
        foreach (var r in rects.Where(r => tree[r.Node].Parent == tree.Root && r.Node != tree.Root))
        {
            var expected = tree[r.Node].AllocSize * scale;
            Assert.InRange(r.Bounds.Width * r.Bounds.Height, expected * 0.999, expected * 1.001);
        }
    }

    [Fact]
    public void SquarifiedKeepsAspectRatiosReasonable()
    {
        // 10 个大小相近的文件：正方形化之后每块的长宽比都不应太离谱
        var tree = TestTrees.Build(Enumerable.Range(0, 10).Select(i => ($"f{i}.bin", 1000L + i)).ToArray());
        var rects = TreemapLayout.Compute(tree, tree.Root, new RectangleF(0, 0, 1000, 500));
        foreach (var r in rects.Skip(1))
        {
            var ratio = Math.Max(r.Bounds.Width / r.Bounds.Height, r.Bounds.Height / r.Bounds.Width);
            Assert.True(ratio < 3, $"长宽比 {ratio:F1}");
        }
    }

    [Fact]
    public void TinyItemsAreSkipped()
    {
        var tree = TestTrees.Build(("big.bin", 1_000_000_000), ("tiny.txt", 1));
        var rects = TreemapLayout.Compute(tree, tree.Root, new RectangleF(0, 0, 100, 100));
        Assert.DoesNotContain(rects, r => tree.GetNameString(r.Node) == "tiny.txt");
    }

    [Fact]
    public void HardLinkSecondaryTakesNoArea()
    {
        var tree = new NodeTree();
        tree.Root = tree.Add(-1, @"C:\", NodeFlags.Directory, 0, 0, 0);
        tree.Add(tree.Root, "a.dll", NodeFlags.None, 500, 500, 0);
        tree.Add(tree.Root, "a-link.dll", NodeFlags.HardLinkSecondary, 500, 500, 0);
        tree.Add(tree.Root, "b.dll", NodeFlags.None, 500, 500, 0);
        tree.Finalize();
        var rects = TreemapLayout.Compute(tree, tree.Root, Bounds);
        Assert.DoesNotContain(rects, r => tree.GetNameString(r.Node) == "a-link.dll");
        Assert.Equal(3, rects.Count);
    }

    [Fact]
    public void HitTestReturnsDeepestBlock()
    {
        var tree = TestTrees.Sample();
        var rects = TreemapLayout.Compute(tree, tree.Root, Bounds);
        var file = rects.First(r => !tree[r.Node].IsDirectory);
        var center = new PointF(file.Bounds.X + file.Bounds.Width / 2, file.Bounds.Y + file.Bounds.Height / 2);
        Assert.Equal(file.Node, TreemapLayout.HitTest(rects, center));
        Assert.Equal(-1, TreemapLayout.HitTest(rects, new PointF(-5, -5)));
    }

    [Fact]
    public void LayoutOfSubdirectoryUsesItAsRoot()
    {
        var tree = TestTrees.Sample();
        var windows = tree.FindPath(@"C:\Windows");
        var rects = TreemapLayout.Compute(tree, windows, Bounds);
        Assert.Equal(windows, rects[0].Node);
        Assert.DoesNotContain(rects, r => r.Node == tree.FindPath(@"C:\Users"));
    }
}

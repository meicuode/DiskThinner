using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>方块图中的一块。<see cref="Leaf"/> 表示没有再往下拆（文件，或者太小 / 到深度上限的目录）。</summary>
internal readonly record struct TreemapRect(RectangleF Bounds, int Node, int Depth, bool Leaf);

/// <summary>
/// Squarified 方块图布局（Bruls、Huizing、van Wijk，2000）：逐行放置子项，每加一项都检查行内最差长宽比，
/// 变差就另起一行。子项必须按大小降序排列（NodeTree.Finalize 已保证）。纯计算，不依赖界面。
/// </summary>
internal static class TreemapLayout
{
    public const float MinArea = 9f;  // 小于 3×3 像素的项不再单独放置，留给父目录的底色
    public const float DirectoryInset = 1f;

    public static List<TreemapRect> Compute(NodeTree tree, int root, RectangleF bounds, int maxDepth = 12)
    {
        var output = new List<TreemapRect>(4096);
        if (bounds.Width >= 1 && bounds.Height >= 1 && tree[root].AllocSize > 0)
        {
            LayoutNode(tree, root, bounds, 0, maxDepth, output);
        }

        return output;
    }

    private static void LayoutNode(NodeTree tree, int node, RectangleF rect, int depth, int maxDepth, List<TreemapRect> output)
    {
        var index = output.Count;
        output.Add(new TreemapRect(rect, node, depth, true));
        if (!tree[node].IsDirectory || depth >= maxDepth || rect.Width < 6 || rect.Height < 6 || tree[node].AllocSize <= 0)
        {
            return;
        }

        var inner = RectangleF.Inflate(rect, -DirectoryInset, -DirectoryInset);
        var scale = inner.Width * (double)inner.Height / tree[node].AllocSize;
        var items = new List<int>();
        for (var c = tree[node].FirstChild; c >= 0; c = tree[c].NextSibling)
        {
            if (tree[c].AllocSize * scale < MinArea)
            {
                break; // 已按大小降序排列，后面的只会更小
            }

            if (CountsTowardParent(tree, c))
            {
                items.Add(c);
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        output[index] = new TreemapRect(rect, node, depth, false);
        Squarify(tree, items, inner, scale, depth + 1, maxDepth, output);
    }

    /// <summary>只算一次的硬链接副本不占父目录的面积，否则面积会超出父目录。</summary>
    private static bool CountsTowardParent(NodeTree tree, int node) =>
        !tree.CountHardLinksOnce || (tree[node].Flags & NodeFlags.HardLinkSecondary) == 0;

    private static void Squarify(NodeTree tree, List<int> items, RectangleF rect, double scale, int depth, int maxDepth, List<TreemapRect> output)
    {
        var i = 0;
        while (i < items.Count && rect.Width > 0.5f && rect.Height > 0.5f)
        {
            double side = Math.Min(rect.Width, rect.Height);
            var start = i;
            double rowSum = 0;
            var worst = double.MaxValue;
            var max = tree[items[start]].AllocSize * scale;
            while (i < items.Count)
            {
                var area = tree[items[i]].AllocSize * scale;
                var sum = rowSum + area;
                var w = Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * area));
                if (rowSum > 0 && w > worst)
                {
                    break;
                }

                rowSum = sum;
                worst = w;
                i++;
            }

            var thickness = (float)(rowSum / side);
            var horizontalRow = rect.Width < rect.Height; // 沿较短的一边排一行
            var offset = 0f;
            for (var k = start; k < i; k++)
            {
                var length = (float)(tree[items[k]].AllocSize * scale / thickness);
                var r = horizontalRow
                    ? new RectangleF(rect.X + offset, rect.Y, length, thickness)
                    : new RectangleF(rect.X, rect.Y + offset, thickness, length);
                offset += length;
                LayoutNode(tree, items[k], r, depth, maxDepth, output);
            }

            rect = horizontalRow
                ? new RectangleF(rect.X, rect.Y + thickness, rect.Width, rect.Height - thickness)
                : new RectangleF(rect.X + thickness, rect.Y, rect.Width - thickness, rect.Height);
        }
    }

    /// <summary>命中测试：返回包含该点的最深一块（布局按先序输出，越靠后越深）。</summary>
    public static int HitTest(List<TreemapRect> rects, PointF point)
    {
        for (var i = rects.Count - 1; i >= 0; i--)
        {
            if (rects[i].Bounds.Contains(point))
            {
                return rects[i].Node;
            }
        }

        return -1;
    }
}

using System.Drawing.Drawing2D;
using DiskThinner.Scanner;

namespace UiBench;

/// <summary>
/// Squarified 方块图（Bruls 等人的算法）：每一行尽量让矩形接近正方形。
/// 布局结果画到离屏位图上缓存，窗口重绘时只贴图；尺寸或根节点变化时才重新布局。
/// </summary>
internal sealed class TreemapControl : Control
{
    private const float MinArea = 9f;     // 小于 3×3 像素的项不再单独绘制
    private const int MaxDepth = 12;

    // Leaf：没有再往下拆的目录（太小或到了深度上限），整块填色，否则只会剩下一堆边框
    private readonly List<(RectangleF Rect, int Node, int Depth, bool Leaf)> _rects = [];
    private NodeTree? _tree;
    private int _root = -1;
    private Bitmap? _cache;

    public TreemapControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        AccessibleName = "方块图";
        AccessibleRole = AccessibleRole.Graphic;
        TabStop = true;
    }

    public event EventHandler<int>? NodeClicked;

    public int RectCount => _rects.Count;

    public void SetRoot(NodeTree tree, int root)
    {
        _tree = tree;
        _root = root;
        Rebuild();
    }

    public void Rebuild()
    {
        _cache?.Dispose();
        _cache = null;
        if (_tree is null || _root < 0 || Width < 4 || Height < 4)
        {
            Invalidate();
            return;
        }

        LayoutRects();
        _cache = Render(Width, Height);
        Invalidate();
    }

    /// <summary>只做布局，返回矩形数（测量用）。</summary>
    public int LayoutRects()
    {
        _rects.Clear();
        var bounds = new RectangleF(0, 0, Width, Height);
        LayoutNode(_root, bounds, 0);
        return _rects.Count;
    }

    public Bitmap Render(int width, int height)
    {
        var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.FromArgb(250, 250, 250));
        using var border = new Pen(Color.FromArgb(60, 0, 0, 0));
        using var dirFill = new SolidBrush(Color.FromArgb(207, 216, 220));
        var brushes = new Dictionary<int, SolidBrush>();
        foreach (var (rect, node, _, leaf) in _rects)
        {
            var isDir = _tree![node].IsDirectory;
            if (!isDir)
            {
                var key = ColorKey(node);
                if (!brushes.TryGetValue(key, out var brush))
                {
                    brush = new SolidBrush(Palette(key));
                    brushes[key] = brush;
                }

                g.FillRectangle(brush, rect);
            }
            else if (leaf)
            {
                g.FillRectangle(dirFill, rect);
            }

            // 太小的块不画边框，否则整片都是边框线
            if (rect.Width >= 6 && rect.Height >= 6)
            {
                g.DrawRectangle(border, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
        }

        foreach (var b in brushes.Values)
        {
            b.Dispose();
        }

        return bmp;
    }

    private void LayoutNode(int node, RectangleF rect, int depth)
    {
        var index = _rects.Count;
        _rects.Add((rect, node, depth, true));
        var tree = _tree!;
        if (!tree[node].IsDirectory || depth >= MaxDepth || rect.Width < 6 || rect.Height < 6 || tree[node].AllocSize <= 0)
        {
            return;
        }

        // 目录内缩 1 像素，露出边框，层级关系更清楚
        var inner = RectangleF.Inflate(rect, -1, -1);
        var scale = inner.Width * inner.Height / tree[node].AllocSize;
        var items = new List<int>();
        foreach (var c in tree.Children(node))
        {
            if (tree[c].AllocSize * scale < MinArea)
            {
                break; // 子项已按大小降序排列，后面的只会更小
            }

            items.Add(c);
        }

        if (items.Count > 0)
        {
            _rects[index] = (rect, node, depth, false);
            Squarify(items, inner, scale, depth + 1);
        }
    }

    private void Squarify(List<int> items, RectangleF rect, double scale, int depth)
    {
        var tree = _tree!;
        var i = 0;
        while (i < items.Count && rect.Width > 0.5f && rect.Height > 0.5f)
        {
            var side = Math.Min(rect.Width, rect.Height);
            var start = i;
            double rowSum = 0;
            var worst = double.MaxValue;
            while (i < items.Count)
            {
                var area = tree[items[i]].AllocSize * scale;
                var sum = rowSum + area;
                var max = tree[items[start]].AllocSize * scale;
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
            var offset = 0f;
            for (var k = start; k < i; k++)
            {
                var length = (float)(tree[items[k]].AllocSize * scale / thickness);
                var r = rect.Width >= rect.Height
                    ? new RectangleF(rect.X, rect.Y + offset, thickness, length)
                    : new RectangleF(rect.X + offset, rect.Y, length, thickness);
                offset += length;
                LayoutNode(items[k], r, depth);
            }

            rect = rect.Width >= rect.Height
                ? new RectangleF(rect.X + thickness, rect.Y, rect.Width - thickness, rect.Height)
                : new RectangleF(rect.X, rect.Y + thickness, rect.Width, rect.Height - thickness);
        }
    }

    private int ColorKey(int node)
    {
        var name = _tree!.GetName(node);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? 0 : string.GetHashCode(name[dot..], StringComparison.OrdinalIgnoreCase) & 0x7;
    }

    private static Color Palette(int key) => key switch
    {
        0 => Color.FromArgb(144, 164, 174),
        1 => Color.FromArgb(100, 181, 246),
        2 => Color.FromArgb(129, 199, 132),
        3 => Color.FromArgb(255, 183, 77),
        4 => Color.FromArgb(240, 98, 146),
        5 => Color.FromArgb(149, 117, 205),
        6 => Color.FromArgb(77, 208, 225),
        _ => Color.FromArgb(220, 231, 117),
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_cache is not null)
        {
            e.Graphics.DrawImageUnscaled(_cache, 0, 0);
        }
        else
        {
            e.Graphics.Clear(BackColor);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        // 取命中的最深一层
        for (var i = _rects.Count - 1; i >= 0; i--)
        {
            if (_rects[i].Rect.Contains(e.Location))
            {
                NodeClicked?.Invoke(this, _rects[i].Node);
                return;
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cache?.Dispose();
        }

        base.Dispose(disposing);
    }
}

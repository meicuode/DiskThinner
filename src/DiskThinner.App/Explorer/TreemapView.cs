using System.Drawing.Drawing2D;
using DiskThinner.App.Controls;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 方块图（原型 .tm）。布局结果画到离屏位图上缓存，重绘时只贴图再叠加选中框；尺寸变化时延迟重新布局。
/// 单击选中，双击进入下一级。键盘：↑ 上级，↓ 最大的子项，← → 同级上一个 / 下一个，Enter 进入，Backspace 返回上一级。
/// </summary>
internal sealed class TreemapView : Control
{
    private readonly DerivedFonts _fonts = new();
    private readonly System.Windows.Forms.Timer _resizeTimer = new() { Interval = 120 };
    private readonly ToolTip _tip = new() { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 8000 };
    private readonly Dictionary<int, int> _rectIndex = [];
    private List<TreemapRect> _rects = [];
    private NodeTree? _tree;
    private Bitmap? _cache;
    private int _mapRoot = -1;
    private int _selected = -1;
    private int _hover = -1;

    public TreemapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Color.White;
        Font = Theme.Ui;
        AccessibleName = "方块图";
        AccessibleRole = AccessibleRole.Graphic;
        _resizeTimer.Tick += (_, _) =>
        {
            _resizeTimer.Stop();
            Rebuild();
        };
    }

    /// <summary>用户在方块图里选中了一个节点（单击或键盘）。</summary>
    public event EventHandler<int>? NodeSelected;

    /// <summary>方块图的显示范围变了（双击进入、返回上一级）。</summary>
    public event EventHandler? MapRootChanged;

    public int MapRoot => _mapRoot;

    public int BlockCount => _rects.Count;

    public TimeSpan LastRenderTime { get; private set; }

    public void SetTree(NodeTree? tree)
    {
        _tree = tree;
        _mapRoot = tree?.Root ?? -1;
        _selected = -1;
        Rebuild();
        MapRootChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetMapRoot(int node)
    {
        if (_tree is null || node < 0 || !_tree[node].IsDirectory || node == _mapRoot)
        {
            return;
        }

        _mapRoot = node;
        Rebuild();
        MapRootChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>选中节点；不在当前显示范围内时，把范围放大到整个磁盘。</summary>
    public void SelectNode(int node)
    {
        if (_tree is null)
        {
            return;
        }

        if (node >= 0 && !IsWithin(node, _mapRoot))
        {
            _mapRoot = _tree.Root;
            Rebuild();
            MapRootChanged?.Invoke(this, EventArgs.Empty);
        }

        _selected = node;
        AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
        Invalidate();
    }

    private bool IsWithin(int node, int ancestor)
    {
        for (var n = node; n >= 0; n = n == _tree!.Root ? -1 : _tree[n].Parent)
        {
            if (n == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    public void Rebuild()
    {
        _cache?.Dispose();
        _cache = null;
        _rects = [];
        _rectIndex.Clear();
        if (_tree is null || _mapRoot < 0 || ClientSize.Width < 8 || ClientSize.Height < 8)
        {
            Invalidate();
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _rects = TreemapLayout.Compute(_tree, _mapRoot, new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
        for (var i = 0; i < _rects.Count; i++)
        {
            _rectIndex[_rects[i].Node] = i;
        }

        _cache = Render(ClientSize.Width, ClientSize.Height);
        LastRenderTime = sw.Elapsed;
        Invalidate();
    }

    private Bitmap Render(int width, int height)
    {
        var tree = _tree!;
        var bmp = new Bitmap(width, height);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.None;
        g.Clear(Color.White);

        var brushes = new Dictionary<FileKind, SolidBrush>();
        using var dirBack = new SolidBrush(ColorTranslator.FromHtml("#eceef1"));
        using var leafDir = new SolidBrush(FileKinds.ColorOf(FileKind.Other));
        using var border = new Pen(Color.FromArgb(45, 0, 0, 0));
        var minBorder = 8 * DeviceDpi / 96f;
        try
        {
            foreach (var r in _rects)
            {
                var isDir = tree[r.Node].IsDirectory;
                if (!isDir)
                {
                    var kind = FileKinds.Classify(tree, r.Node);
                    if (!brushes.TryGetValue(kind, out var brush))
                    {
                        brushes[kind] = brush = new SolidBrush(FileKinds.ColorOf(kind));
                    }

                    g.FillRectangle(brush, r.Bounds);
                }
                else
                {
                    g.FillRectangle(r.Leaf ? leafDir : dirBack, r.Bounds);
                }

                // 太小的块不画边框，否则整片都是线
                if (r.Bounds.Width >= minBorder && r.Bounds.Height >= minBorder)
                {
                    g.DrawRectangle(border, r.Bounds.X, r.Bounds.Y, r.Bounds.Width - 1, r.Bounds.Height - 1);
                }
            }

            DrawLabels(g);
        }
        finally
        {
            foreach (var b in brushes.Values)
            {
                b.Dispose();
            }
        }

        return bmp;
    }

    /// <summary>在足够大的块上写名称和大小（原型里的 Windows 60.2 GB 这类标签），只标前两层，避免满屏文字。</summary>
    private void DrawLabels(Graphics g)
    {
        var tree = _tree!;
        _fonts.Update(Font);
        var s = DeviceDpi / 96f;
        var line = _fonts.SmallBold.Height;
        var minW = 64 * s;
        var minH = line * 2 + 6 * s;
        var flags = TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var placed = new List<Rectangle>();
        using var labelBack = new SolidBrush(Color.FromArgb(200, 255, 255, 255));

        // 先标第一层再标第二层；子块的标签和已经画过的标签重叠就跳过（父目录和最大的子项共用左上角）
        foreach (var depth in new[] { 1, 2 })
        {
            foreach (var r in _rects)
            {
                if (r.Depth != depth || r.Bounds.Width < minW || r.Bounds.Height < minH)
                {
                    continue;
                }

                var name = tree.GetNameString(r.Node);
                var size = ByteSize.Format(tree[r.Node].AllocSize);
                var maxW = (int)(r.Bounds.Width - 10 * s);
                var textW = Math.Min(maxW, Math.Max(TextRenderer.MeasureText(name, _fonts.SmallBold).Width, TextRenderer.MeasureText(size, _fonts.Small).Width));
                var box = new Rectangle((int)(r.Bounds.X + 3 * s), (int)(r.Bounds.Y + 2 * s), textW + (int)(4 * s), line * 2 + (int)(2 * s));
                if (placed.Any(p => p.IntersectsWith(box)))
                {
                    continue;
                }

                placed.Add(box);
                g.FillRectangle(labelBack, box);
                var x = box.X + (int)(2 * s);
                var y = box.Y + (int)(1 * s);
                TextRenderer.DrawText(g, name, _fonts.SmallBold, new Rectangle(x, y, textW, line), Theme.Text, flags);
                TextRenderer.DrawText(g, size, _fonts.Small, new Rectangle(x, y + line, textW, line), Theme.Text, flags);
            }
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_cache is null)
        {
            g.Clear(BackColor);
            return;
        }

        g.DrawImageUnscaled(_cache, 0, 0);

        // 选中的节点太小没有单独的块时，框出它最近的有块的祖先
        for (var n = _selected; n >= 0 && _tree is not null; n = n == _tree.Root ? -1 : _tree[n].Parent)
        {
            if (_rectIndex.TryGetValue(n, out var i))
            {
                var b = Rectangle.Round(_rects[i].Bounds);
                using var pen = new Pen(Color.FromArgb(17, 17, 17), 2) { Alignment = PenAlignment.Inset };
                g.DrawRectangle(pen, b.X, b.Y, Math.Max(1, b.Width - 1), Math.Max(1, b.Height - 1));
                break;
            }
        }

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Theme.Accent, 2) { Alignment = PenAlignment.Inset };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        Rebuild();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = TreemapLayout.HitTest(_rects, e.Location);
        if (hit == _hover)
        {
            return;
        }

        _hover = hit;
        if (hit < 0 || _tree is null)
        {
            _tip.SetToolTip(this, null);
            return;
        }

        _tip.SetToolTip(this, $"{_tree.GetPath(hit)}\n{ByteSize.Format(_tree[hit].AllocSize)}");
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        Focus();
        var hit = TreemapLayout.HitTest(_rects, e.Location);
        if (hit >= 0)
        {
            SelectAndNotify(hit);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var hit = TreemapLayout.HitTest(_rects, e.Location);
        var next = ChildOfMapRootContaining(hit);
        if (next >= 0 && _tree![next].IsDirectory)
        {
            SetMapRoot(next);
            SelectAndNotify(next);
        }
    }

    /// <summary>当前范围下、包含该节点的那个直接子项（"进入下一级"）。</summary>
    private int ChildOfMapRootContaining(int node)
    {
        if (_tree is null || node < 0 || node == _mapRoot)
        {
            return -1;
        }

        var n = node;
        while (n >= 0 && _tree[n].Parent != _mapRoot)
        {
            n = n == _tree.Root ? -1 : _tree[n].Parent;
        }

        return n;
    }

    public void ZoomOut()
    {
        if (_tree is not null && _mapRoot != _tree.Root && _mapRoot >= 0)
        {
            SetMapRoot(_tree[_mapRoot].Parent);
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Enter or Keys.Back || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        e.Handled = HandleKey(e.KeyCode);
    }

    public int SelectedNode => _selected;

    /// <summary>模拟在某一点单击（测试用）。</summary>
    internal void ClickAt(PointF point)
    {
        var hit = TreemapLayout.HitTest(_rects, point);
        if (hit >= 0)
        {
            SelectAndNotify(hit);
        }
    }

    /// <summary>某个节点的块（找不到返回空矩形，测试用）。</summary>
    internal RectangleF BoundsOf(int node) => _rectIndex.TryGetValue(node, out var i) ? _rects[i].Bounds : RectangleF.Empty;

    /// <summary>处理方块图的快捷键，返回是否已处理。单独拆出来便于测试。</summary>
    internal bool HandleKey(Keys key)
    {
        if (_tree is null)
        {
            return false;
        }

        var current = _selected >= 0 ? _selected : _mapRoot;
        var target = -1;
        switch (key)
        {
            case Keys.Up when current != _tree.Root:
                target = _tree[current].Parent;
                break;
            case Keys.Down:
                target = _tree[current].FirstChild;
                break;
            case Keys.Left:
                target = Sibling(current, -1);
                break;
            case Keys.Right:
                target = Sibling(current, 1);
                break;
            case Keys.Enter when _tree[current].IsDirectory:
                SetMapRoot(current);
                break;
            case Keys.Back:
                ZoomOut();
                break;
            default:
                return false;
        }

        if (target >= 0)
        {
            SelectAndNotify(target);
        }

        return true;
    }

    private int Sibling(int node, int delta)
    {
        if (node == _tree!.Root)
        {
            return -1;
        }

        var siblings = _tree.Children(_tree[node].Parent).ToList();
        var i = siblings.IndexOf(node) + delta;
        return i >= 0 && i < siblings.Count ? siblings[i] : -1;
    }

    private void SelectAndNotify(int node)
    {
        SelectNode(node);
        NodeSelected?.Invoke(this, node);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new TreemapAccessible(this);

    private sealed class TreemapAccessible(TreemapView owner) : ControlAccessibleObject(owner)
    {
        public override string? Value
        {
            get
            {
                var tree = owner._tree;
                if (tree is null || owner._selected < 0)
                {
                    return "未选中";
                }

                return $"{tree.GetPath(owner._selected)}，{ByteSize.Format(tree[owner._selected].AllocSize)}";
            }
        }

        public override string? Description => "方向键在方块之间移动，Enter 进入目录，Backspace 返回上一级";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cache?.Dispose();
            _resizeTimer.Dispose();
            _tip.Dispose();
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }
}

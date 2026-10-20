using System.Globalization;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace UiBench;

/// <summary>
/// 用虚拟模式 ListView 模拟树形列表：维护一个"当前可见行"列表，展开时插入子节点，折叠时移除子树。
/// ListView 只为屏幕上可见的几十行请求数据，所以节点总数对绘制开销没有影响。
/// </summary>
internal sealed class TreeListView : ListView
{
    private const int IndentWidth = 16;

    private readonly List<(int Node, int Depth)> _rows = [];
    private NodeTree? _tree;
    private bool[] _expanded = [];

    public TreeListView()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        OwnerDraw = true;
        DoubleBuffered = true;
        AccessibleName = "文件树";

        Columns.Add("名称", 360);
        Columns.Add("大小", 90, HorizontalAlignment.Right);
        Columns.Add("占上级", 120);
        Columns.Add("最后写入", 130);
        Columns.Add("文件数", 90, HorizontalAlignment.Right);

        RetrieveVirtualItem += OnRetrieveVirtualItem;
        DrawColumnHeader += (_, e) => e.DrawDefault = true;
        DrawSubItem += OnDrawSubItem;
        MouseDown += OnMouseDown;
        KeyDown += OnKeyDown;
    }

    public event EventHandler<int>? NodeSelected;

    public int RowCount => _rows.Count;

    public void SetTree(NodeTree tree)
    {
        _tree = tree;
        _expanded = new bool[tree.Count];
        _rows.Clear();
        _rows.Add((tree.Root, 0));
        VirtualListSize = 0;
        Expand(0);
    }

    public int NodeAt(int row) => _rows[row].Node;

    public void Toggle(int row)
    {
        if (_tree is null || row < 0 || row >= _rows.Count)
        {
            return;
        }

        if (_expanded[_rows[row].Node])
        {
            Collapse(row);
        }
        else
        {
            Expand(row);
        }
    }

    public void Expand(int row)
    {
        var tree = _tree!;
        var (node, depth) = _rows[row];
        if (_expanded[node] || tree[node].FirstChild < 0)
        {
            return;
        }

        _expanded[node] = true;
        var insert = new List<(int, int)>();
        AppendVisibleChildren(node, depth + 1, insert);
        _rows.InsertRange(row + 1, insert);
        VirtualListSize = _rows.Count;
        Invalidate();
    }

    public void Collapse(int row)
    {
        var (node, depth) = _rows[row];
        if (!_expanded[node])
        {
            return;
        }

        _expanded[node] = false;
        var end = row + 1;
        while (end < _rows.Count && _rows[end].Depth > depth)
        {
            end++;
        }

        _rows.RemoveRange(row + 1, end - row - 1);
        VirtualListSize = _rows.Count;
        Invalidate();
    }

    /// <summary>全部展开（压力测试用）：可见行数等于节点总数。</summary>
    public void ExpandAll()
    {
        var tree = _tree!;
        Array.Fill(_expanded, true);
        _rows.Clear();
        _rows.Capacity = Math.Max(_rows.Capacity, tree.Count);
        var stack = new Stack<(int, int)>();
        stack.Push((tree.Root, 0));
        var children = new List<int>();
        while (stack.Count > 0)
        {
            var (n, d) = stack.Pop();
            _rows.Add((n, d));
            children.Clear();
            children.AddRange(tree.Children(n));
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], d + 1));
            }
        }

        VirtualListSize = _rows.Count;
        Invalidate();
    }

    /// <summary>展开祖先并选中节点，返回所在行；用于方块图点击后同步到列表。</summary>
    public int Reveal(int node)
    {
        var tree = _tree!;
        var chain = new List<int>();
        for (var n = node; n >= 0 && n != tree.Root; n = tree[n].Parent)
        {
            chain.Add(n);
        }

        var row = 0; // 根
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            Expand(row);
            var target = chain[i];
            var depth = _rows[row].Depth + 1;
            var r = row + 1;
            while (r < _rows.Count && _rows[r].Depth >= depth && _rows[r].Node != target)
            {
                r++;
            }

            if (r >= _rows.Count || _rows[r].Node != target)
            {
                return -1;
            }

            row = r;
        }

        SelectedIndices.Clear();
        SelectedIndices.Add(row);
        EnsureVisible(row);
        return row;
    }

    private void AppendVisibleChildren(int node, int depth, List<(int, int)> output)
    {
        foreach (var c in _tree!.Children(node))
        {
            output.Add((c, depth));
            if (_expanded[c])
            {
                AppendVisibleChildren(c, depth + 1, output);
            }
        }
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var tree = _tree!;
        var (n, _) = _rows[e.ItemIndex];
        ref var node = ref tree[n];
        var item = new ListViewItem(tree.GetNameString(n));
        item.SubItems.Add(ByteSize.Format(node.AllocSize));
        item.SubItems.Add(PercentOfParent(n).ToString("0.0", CultureInfo.InvariantCulture) + "%");
        item.SubItems.Add(node.LastWriteUtc > 0 ? DateTime.FromFileTimeUtc(node.LastWriteUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "");
        item.SubItems.Add(node.IsDirectory ? node.FileCount.ToString("N0", CultureInfo.InvariantCulture) : "");
        e.Item = item;
    }

    private double PercentOfParent(int n)
    {
        var tree = _tree!;
        var parent = tree[n].Parent;
        if (n == tree.Root || parent < 0 || tree[parent].AllocSize == 0)
        {
            return 100;
        }

        return tree[n].AllocSize * 100.0 / tree[parent].AllocSize;
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null)
        {
            return;
        }

        var g = e.Graphics;
        var selected = e.Item.Selected;
        using var back = new SolidBrush(selected ? Color.FromArgb(204, 228, 247) : BackColor);
        g.FillRectangle(back, e.Bounds);

        var (n, depth) = _rows[e.ItemIndex];
        var tree = _tree!;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        switch (e.ColumnIndex)
        {
            case 0:
            {
                var x = e.Bounds.X + 4 + depth * IndentWidth;
                if (tree[n].FirstChild >= 0)
                {
                    TextRenderer.DrawText(g, _expanded[n] ? "▼" : "▶", Font, new Rectangle(x, e.Bounds.Y, IndentWidth, e.Bounds.Height), Color.DimGray, flags);
                }

                var textBounds = Rectangle.FromLTRB(x + IndentWidth, e.Bounds.Y, e.Bounds.Right, e.Bounds.Bottom);
                TextRenderer.DrawText(g, e.SubItem.Text, Font, textBounds, ForeColor, flags);
                break;
            }

            case 2:
            {
                var pct = PercentOfParent(n);
                var bar = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 4, (int)((e.Bounds.Width - 48) * pct / 100), e.Bounds.Height - 8);
                using var barBrush = new SolidBrush(Color.FromArgb(0, 120, 212));
                g.FillRectangle(barBrush, bar);
                TextRenderer.DrawText(g, e.SubItem.Text, Font, Rectangle.FromLTRB(e.Bounds.Right - 44, e.Bounds.Y, e.Bounds.Right, e.Bounds.Bottom), ForeColor, flags | TextFormatFlags.Right);
                break;
            }

            default:
            {
                var align = Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                TextRenderer.DrawText(g, e.SubItem.Text, Font, Rectangle.Inflate(e.Bounds, -4, 0), ForeColor, flags | align);
                break;
            }
        }
    }

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        var hit = HitTest(e.Location);
        if (hit.Item is null)
        {
            return;
        }

        var row = hit.Item.Index;
        var depth = _rows[row].Depth;
        var glyphLeft = Columns[0].Width > 0 ? 4 + depth * IndentWidth : 0;
        if (e.X >= glyphLeft && e.X < glyphLeft + IndentWidth)
        {
            Toggle(row);
        }

        NodeSelected?.Invoke(this, _rows[row].Node);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedIndices.Count == 0)
        {
            return;
        }

        var row = SelectedIndices[0];
        switch (e.KeyCode)
        {
            case Keys.Right:
                Expand(row);
                e.Handled = true;
                break;
            case Keys.Left:
                Collapse(row);
                e.Handled = true;
                break;
            case Keys.Enter:
                Toggle(row);
                e.Handled = true;
                break;
        }

        NodeSelected?.Invoke(this, _rows[row].Node);
    }
}

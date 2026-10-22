using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Formatting;
using DiskThinner.App.Rules;
using DiskThinner.Rules;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 树形列表（原型 .tbl.tree）：虚拟模式 ListView + 自绘。只为屏幕上可见的行取数据，百万级节点也不卡。
/// 键盘：→ 展开 / 进入第一个子项，← 折叠 / 回到上级，Enter 展开或折叠，Backspace 回到上级，Ctrl+C 复制路径。
/// </summary>
internal sealed class TreeListView : ListView
{
    private const int IndentWidth = 16;

    // 96 DPI 下的列宽；句柄创建和 DPI 变化时按 DeviceDpi 换算
    // 默认窗口在 150% 缩放下，列表区约 880 个 96 DPI 像素宽：六列加起来不能超过它，否则会出现横向滚动条
    private static readonly int[] BaseColumnWidths = [300, 80, 110, 88, 150, 200];

    private readonly DerivedFonts _fonts = new();
    private TreeRows? _rows;
    private Classification? _classification;
    private OwnerMap? _owners;
    private bool _suppressSelection;
    private DateTime _now = DateTime.Now;
    private int _hoverRow = -1;

    public TreeListView()
    {
        View = View.Details;
        VirtualMode = true;
        FullRowSelect = true;
        HideSelection = false;
        MultiSelect = false;
        OwnerDraw = true;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.None;
        HeaderStyle = ColumnHeaderStyle.Nonclickable;
        Font = Theme.Ui;
        AccessibleName = L.T("文件树");

        Columns.Add(L.T("名称"), BaseColumnWidths[0]);
        Columns.Add(L.T("大小"), BaseColumnWidths[1], HorizontalAlignment.Right);
        Columns.Add(L.T("占上级"), BaseColumnWidths[2]);
        Columns.Add(L.T("最后写入"), BaseColumnWidths[3]);
        Columns.Add(L.T("归属"), BaseColumnWidths[4]);
        Columns.Add(L.T("类别 / 风险"), BaseColumnWidths[5]);

        RetrieveVirtualItem += OnRetrieveVirtualItem;
        DrawColumnHeader += (_, e) => e.DrawDefault = true;
        DrawItem += (_, e) => e.DrawDefault = false;
        DrawSubItem += OnDrawSubItem;
        SelectedIndexChanged += OnSelectedIndexChanged;
    }

    /// <summary>用户在列表里选中了一个节点。</summary>
    public event EventHandler<int>? NodeSelected;

    public event EventHandler<int>? CopyPathRequested;

    public NodeTree? Tree => _rows?.Tree;

    public int SelectedNode => _rows is not null && SelectedIndices.Count > 0 ? _rows.NodeAt(SelectedIndices[0]) : -1;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRowHeight();
        ApplyColumnWidths();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyRowHeight();
        ApplyColumnWidths();
    }

    private void ApplyColumnWidths()
    {
        for (var i = 1; i < Columns.Count && i < BaseColumnWidths.Length; i++)
        {
            Columns[i].Width = Ui.Scale(this, BaseColumnWidths[i]);
        }

        FitNameColumn();
    }

    private const int GwlStyle = -16;
    private const int WsVScroll = 0x00200000;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    /// <summary>
    /// 名称列占满剩余宽度（不小于默认宽度），其他列保持固定。
    /// 竖直滚动条出现时 ListView 不一定触发尺寸变化，所以滚动条还没出现时先把它的宽度留出来，避免出现横向滚动条。
    /// </summary>
    private void FitNameColumn()
    {
        if (Columns.Count == 0 || !IsHandleCreated)
        {
            return;
        }

        var others = 0;
        for (var i = 1; i < Columns.Count; i++)
        {
            others += Columns[i].Width;
        }

        var hasVScroll = (GetWindowLong(Handle, GwlStyle) & WsVScroll) != 0;
        var reserve = hasVScroll ? 0 : SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        var width = Math.Max(Ui.Scale(this, 220), ClientSize.Width - others - reserve - 1);
        if (Math.Abs(Columns[0].Width - width) > 1)
        {
            Columns[0].Width = width;
        }
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (IsHandleCreated)
        {
            FitNameColumn();
        }
    }

    /// <summary>ListView 的行高由小图标列表决定：用一个 1 像素宽的空图标把行高撑到 24。</summary>
    private void ApplyRowHeight()
    {
        SmallImageList?.Dispose();
        SmallImageList = new ImageList { ImageSize = new Size(1, Ui.Scale(this, 24)) };
    }

    public void SetTree(NodeTree? tree, Classification? classification = null, OwnerMap? owners = null)
    {
        _suppressSelection = true;
        try
        {
            VirtualListSize = 0;
            _rows = tree is null ? null : new TreeRows(tree);
            _classification = classification;
            _owners = classification is null ? null : owners ?? OwnerMap.RulesOnly(classification);
            _now = DateTime.Now;
            if (_rows is not null)
            {
                _rows.Expand(0);
                VirtualListSize = _rows.Count;
                FitNameColumn();
                SelectRow(0);
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        Invalidate();
    }

    /// <summary>只显示满足条件的节点；null 取消过滤。保持当前选中项（如果它仍然可见）。</summary>
    public void SetFilter(Func<int, bool>? filter)
    {
        if (_rows is null)
        {
            return;
        }

        var selected = SelectedNode;
        _suppressSelection = true;
        try
        {
            _rows.SetFilter(filter);
            VirtualListSize = _rows.Count;
            FitNameColumn();
            var row = selected >= 0 ? _rows.FindVisibleRow(selected) : -1;
            SelectRow(row >= 0 ? row : 0);
        }
        finally
        {
            _suppressSelection = false;
        }

        Invalidate();
    }

    public bool HasFilter => _rows?.HasFilter ?? false;

    /// <summary>展开祖先并选中节点（不触发 NodeSelected）。节点被过滤掉时返回 false。</summary>
    public bool SelectNode(int node)
    {
        if (_rows is null || node < 0)
        {
            return false;
        }

        _suppressSelection = true;
        try
        {
            var row = _rows.Reveal(node);
            VirtualListSize = _rows.Count;
            FitNameColumn();
            if (row >= 0)
            {
                SelectRow(row);
            }

            return row >= 0;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void SelectRow(int row)
    {
        SelectedIndices.Clear();
        SelectedIndices.Add(row);
        FocusedItem = Items[row];
        EnsureVisible(row);
        Invalidate();
    }

    private void Toggle(int row)
    {
        if (_rows is null)
        {
            return;
        }

        _rows.Toggle(row);
        VirtualListSize = _rows.Count;
        FitNameColumn();
        Invalidate();
    }

    private void OnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_suppressSelection && SelectedNode >= 0)
        {
            NodeSelected?.Invoke(this, SelectedNode);
        }
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var rows = _rows!;
        var tree = rows.Tree;
        var n = rows.NodeAt(e.ItemIndex);
        ref var node = ref tree[n];
        var item = new ListViewItem(DisplayName(tree, n));
        item.SubItems.Add(ByteSize.Format(node.AllocSize));
        item.SubItems.Add(PercentOfParent(tree, n).ToString("0.0", CultureInfo.InvariantCulture) + "%");
        item.SubItems.Add(TimeText.Relative(node.LastWriteUtc, _now));
        var (owner, kind) = Describe(n);
        item.SubItems.Add(owner);
        item.SubItems.Add(kind);
        e.Item = item;
    }

    /// <summary>归属列和类别列的文字（给读屏软件和测试用；绘制时另外画标签）。</summary>
    internal (string Owner, string Kind) Describe(int n)
    {
        var c = _classification;
        if (c is null)
        {
            return ("", "");
        }

        var owner = _owners?.OwnerOf(n)?.Display;
        if (c.RuleOf(n) is { } rule)
        {
            var risk = c.EffectiveRisk(n) ?? rule.Risk;
            return (owner ?? "", $"{Names.Display(rule.Kind)} {RuleView.RiskLabel(risk, rule)}");
        }

        if (c.ProtectionOf(n) is { Level: ProtectionLevel.Hard or ProtectionLevel.Guide } p)
        {
            return (owner ?? "", ProtectionList.Display(p.Level));
        }

        if (IsUnrecognized(n))
        {
            // 知道归谁但没有规则说明是什么：归属列写应用，类别列标"未识别"
            return owner is null ? (L.T("未识别"), "") : (owner, L.T("未识别"));
        }

        return (owner ?? "", "");
    }

    /// <summary>整个节点都没有被任何规则解释（目录要求整棵子树都没有规则）。</summary>
    private bool IsUnrecognized(int n) =>
        _classification is { } c && c.RuleOf(n) is null && Tree![n].AllocSize > 0 && c.Unexplained(n) >= Tree[n].AllocSize;

    internal static string DisplayName(NodeTree tree, int n)
    {
        var name = tree.GetNameString(n);
        var flags = tree[n].Flags;
        if ((flags & NodeFlags.AccessDenied) != 0)
        {
            return L.F("{0}（无法列出）", name);
        }

        if ((flags & NodeFlags.ReparsePoint) != 0 && tree[n].IsDirectory)
        {
            return L.F("{0}（链接，未展开）", name);
        }

        return name;
    }

    internal static double PercentOfParent(NodeTree tree, int n)
    {
        var parent = tree[n].Parent;
        if (n == tree.Root || parent < 0 || tree[parent].AllocSize <= 0)
        {
            return 100;
        }

        return Math.Min(100, tree[n].AllocSize * 100.0 / tree[parent].AllocSize);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null || _rows is null)
        {
            return;
        }

        var g = e.Graphics;
        var row = e.ItemIndex;
        var selected = SelectedIndices.Count > 0 && SelectedIndices[0] == row;
        var back = selected ? Theme.RowSelected : row == _hoverRow ? Theme.RowHover : Color.White;
        using (var b = new SolidBrush(back))
        {
            g.FillRectangle(b, e.Bounds);
        }

        using (var line = new Pen(Theme.RowLine))
        {
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        var tree = _rows.Tree;
        var n = _rows.NodeAt(row);
        var depth = _rows.DepthAt(row);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var s = (float)DeviceDpi / 96;
        var muted = (tree[n].Flags & (NodeFlags.AccessDenied | NodeFlags.ReparsePoint | NodeFlags.Metadata)) != 0;
        var textColor = muted ? Theme.Muted : Theme.Text;
        _fonts.Update(Font);

        switch (e.ColumnIndex)
        {
            case 0:
            {
                var x = e.Bounds.X + (int)((6 + depth * IndentWidth) * s);
                if (_rows.HasChildren(n))
                {
                    var chevron = new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - (int)(14 * s)) / 2, (int)(14 * s), (int)(14 * s));
                    Ui.DrawChevron(g, chevron, _rows.IsExpanded(n), ColorTranslator.FromHtml("#666666"));
                }

                x += (int)(14 * s);
                var isDir = tree[n].IsDirectory;
                var iconW = (int)((isDir ? 14 : 10) * s);
                var iconH = (int)((isDir ? 11 : 13) * s);
                var icon = new Rectangle(x + (isDir ? 0 : (int)(2 * s)), e.Bounds.Y + (e.Bounds.Height - iconH) / 2, iconW, iconH);
                using (var ib = new SolidBrush(isDir ? Theme.FolderIcon : Theme.FileIcon))
                {
                    g.FillRectangle(ib, icon);
                }

                x += (int)(20 * s);
                var font = n == tree.Root ? _fonts.Bold : _fonts.Regular;
                TextRenderer.DrawText(g, e.SubItem.Text, font, Rectangle.FromLTRB(x, e.Bounds.Y, e.Bounds.Right - 2, e.Bounds.Bottom), textColor, flags);
                break;
            }

            case 2:
            {
                var pct = PercentOfParent(tree, n);
                var barW = (int)(44 * s);
                var barH = (int)(8 * s);
                var bar = new Rectangle(e.Bounds.X + (int)(6 * s), e.Bounds.Y + (e.Bounds.Height - barH) / 2, barW, barH);
                using (var bb = new SolidBrush(Theme.BarBack))
                {
                    g.FillRectangle(bb, bar);
                }

                using (var fb = new SolidBrush(Theme.BarFill))
                {
                    g.FillRectangle(fb, bar.X, bar.Y, (int)Math.Round(barW * pct / 100), barH);
                }

                TextRenderer.DrawText(g, e.SubItem.Text, _fonts.Regular, Rectangle.FromLTRB(bar.Right + (int)(6 * s), e.Bounds.Y, e.Bounds.Right - 2, e.Bounds.Bottom), textColor, flags);
                break;
            }

            case 4:
                DrawOwner(g, e.Bounds, n, s, flags);
                break;

            case 5:
                DrawKind(g, e.Bounds, n, s, flags);
                break;

            default:
            {
                var align = Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                var font = e.ColumnIndex == 1 && n == tree.Root ? _fonts.Bold : _fonts.Regular;
                TextRenderer.DrawText(g, e.SubItem.Text, font, Rectangle.Inflate(e.Bounds, -(int)(6 * s), 0), textColor, flags | align);
                break;
            }
        }
    }

    /// <summary>
    /// 归属列：应用名 + 来源标签（规则 / 推断）；标签只画在归属确定的那一级，下面的子项用浅色。
    /// 不知道归谁、也没有规则认识时显示"未识别"。
    /// </summary>
    private void DrawOwner(Graphics g, Rectangle bounds, int n, float s, TextFormatFlags flags)
    {
        var c = _classification;
        if (c is null)
        {
            return;
        }

        var x = bounds.X + (int)(6 * s);
        var tagH = (int)(17 * s);
        var tagY = bounds.Y + (bounds.Height - tagH) / 2;
        if (_owners?.Resolve(n) is { } resolved)
        {
            var text = resolved.Owner.Display;
            var atRoot = resolved.Root == n;
            var w = Math.Min(TextRenderer.MeasureText(g, text, _fonts.Regular).Width, bounds.Right - x - (int)(44 * s));
            TextRenderer.DrawText(g, text, _fonts.Regular, new Rectangle(x, bounds.Y, w, bounds.Height), atRoot ? Theme.Text : Theme.Muted, flags);
            x += w + (int)(4 * s);
            if (atRoot && x + (int)(36 * s) < bounds.Right)
            {
                var (tag, style) = RuleView.SourceTag(resolved.Owner.Source);
                RuleView.DrawTag(g, tag, _fonts.Small, new Point(x, tagY), tagH, style);
            }

            return;
        }

        if (IsUnrecognized(n))
        {
            RuleView.DrawTag(g, L.T("未识别"), _fonts.Small, new Point(x, tagY), tagH, RuleView.Unknown);
        }
    }

    /// <summary>类别列：类别文字 + 风险标签（颜色取自原型）。</summary>
    private void DrawKind(Graphics g, Rectangle bounds, int n, float s, TextFormatFlags flags)
    {
        var c = _classification;
        if (c is null)
        {
            return;
        }

        var x = bounds.X + (int)(6 * s);
        var tagH = (int)(17 * s);
        var tagY = bounds.Y + (bounds.Height - tagH) / 2;
        var rule = c.RuleOf(n);
        var risk = c.EffectiveRisk(n);
        if (rule is not null)
        {
            var kind = Names.Display(rule.Kind);
            var w = TextRenderer.MeasureText(g, kind, _fonts.Regular).Width;
            TextRenderer.DrawText(g, kind, _fonts.Regular, new Rectangle(x, bounds.Y, w, bounds.Height), c.IsRoot(n) ? Theme.Text : Theme.Muted, flags);
            x += w + (int)(4 * s);
        }

        if (risk is { } r && x < bounds.Right - (int)(20 * s))
        {
            RuleView.DrawTag(g, RuleView.RiskLabel(r, rule), _fonts.Small, new Point(x, tagY), tagH, RuleView.RiskStyle(r));
        }
        else if (rule is null && risk is null && IsUnrecognized(n) && _owners?.OwnerOf(n) is not null)
        {
            RuleView.DrawTag(g, L.T("未识别"), _fonts.Small, new Point(x, tagY), tagH, RuleView.Unknown);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_rows is not null && e.Button == MouseButtons.Left)
        {
            var hit = HitTest(e.Location);
            if (hit.Item is not null && hit.SubItem == hit.Item.SubItems[0])
            {
                var row = hit.Item.Index;
                var s = (float)DeviceDpi / 96;
                var chevronLeft = hit.Item.Bounds.X + (int)((6 + _rows.DepthAt(row) * IndentWidth) * s);
                if (e.X >= chevronLeft && e.X < chevronLeft + (int)(16 * s) && _rows.HasChildren(_rows.NodeAt(row)))
                {
                    Toggle(row);
                }
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var hit = HitTest(e.Location);
        if (hit.Item is not null)
        {
            Toggle(hit.Item.Index);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var row = HitTest(e.Location).Item?.Index ?? -1;
        if (row != _hoverRow)
        {
            var old = _hoverRow;
            _hoverRow = row;
            if (old >= 0 && old < VirtualListSize)
            {
                RedrawItems(old, old, true);
            }

            if (row >= 0)
            {
                RedrawItems(row, row, true);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        var old = _hoverRow;
        _hoverRow = -1;
        if (old >= 0 && old < VirtualListSize)
        {
            RedrawItems(old, old, true);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleKey(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>处理树形列表的快捷键，返回是否已处理。单独拆出来便于测试。</summary>
    internal bool HandleKey(Keys keyData)
    {
        if (_rows is null || SelectedIndices.Count == 0)
        {
            return false;
        }

        var row = SelectedIndices[0];
        var node = _rows.NodeAt(row);
        var handled = true;
        switch (keyData & Keys.KeyCode)
        {
            case Keys.Right when _rows.HasChildren(node) && !_rows.IsExpanded(node):
                Toggle(row);
                break;
            case Keys.Right when _rows.HasChildren(node):
                SelectAndNotify(row + 1);
                break;
            case Keys.Left when _rows.IsExpanded(node):
                Toggle(row);
                break;
            case Keys.Left:
            case Keys.Back:
                var parent = _rows.ParentRow(row);
                if (parent >= 0)
                {
                    SelectAndNotify(parent);
                }

                break;
            case Keys.Enter:
                Toggle(row);
                break;
            case Keys.C when (keyData & Keys.Control) != 0:
                CopyPathRequested?.Invoke(this, node);
                break;
            default:
                handled = false;
                break;
        }

        return handled;
    }

    /// <summary>模拟用户点选一行（测试用）。</summary>
    internal void SelectRowAsUser(int row) => SelectAndNotify(row);

    internal int RowCount => _rows?.Count ?? 0;

    internal int RowOf(int node) => _rows?.FindVisibleRow(node) ?? -1;

    internal bool IsExpanded(int node) => _rows?.IsExpanded(node) ?? false;

    private void SelectAndNotify(int row)
    {
        SelectRow(row);
        NodeSelected?.Invoke(this, _rows!.NodeAt(row));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SmallImageList?.Dispose();
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }
}

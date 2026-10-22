using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Formatting;
using DiskThinner.App.Rules;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// "按归属汇总""按类别汇总"的列表：第一层是组（应用或类别），展开后是组里的各个位置。和树形列表一样用虚拟模式 ListView 自绘。
/// 选中位置时联动方块图和详情；双击位置或按回车在目录视图里定位。
/// </summary>
internal sealed class GroupListView : ListView
{
    // 96 DPI 下的列宽。按归属汇总时归属列只放来源标签，收窄后把空间让给路径
    private static readonly int[] BaseColumnWidths = [320, 80, 110, 88, 160, 210];
    private const int NarrowOwnerColumn = 64;

    private readonly DerivedFonts _fonts = new();
    private readonly List<(int Group, int Location)> _rows = [];
    private readonly HashSet<string> _expanded = [];
    private readonly Dictionary<int, (int Group, int Location)> _byNode = [];
    private List<SpaceGroup> _groups = [];
    private NodeTree? _tree;
    private Classification? _classification;
    private Func<SpaceGroup, SpaceLocation, bool>? _filter;
    private long _total = 1;
    private bool _suppressSelection;
    private DateTime _now = DateTime.Now;
    private int _hoverRow = -1;

    public GroupListView()
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
        AccessibleName = L.T("汇总列表");

        Columns.Add(L.T("名称"), BaseColumnWidths[0]);
        Columns.Add(L.T("大小"), BaseColumnWidths[1], HorizontalAlignment.Right);
        Columns.Add(L.T("占比"), BaseColumnWidths[2]);
        Columns.Add(L.T("最后写入"), BaseColumnWidths[3]);
        Columns.Add(L.T("归属"), BaseColumnWidths[4]);
        Columns.Add(L.T("类别 / 风险"), BaseColumnWidths[5]);

        RetrieveVirtualItem += OnRetrieveVirtualItem;
        DrawColumnHeader += (_, e) => e.DrawDefault = true;
        DrawItem += (_, e) => e.DrawDefault = false;
        DrawSubItem += OnDrawSubItem;
        SelectedIndexChanged += OnSelectedIndexChanged;
    }

    /// <summary>选中了一个位置。</summary>
    public event EventHandler<int>? NodeSelected;

    /// <summary>选中了一个组。</summary>
    public event EventHandler<SpaceGroup>? GroupSelected;

    /// <summary>要求在目录视图里定位这个位置（双击、回车、右键菜单）。</summary>
    public event EventHandler<int>? RevealRequested;

    public event EventHandler<int>? CopyPathRequested;

    /// <summary>true 为按归属汇总，false 为按类别汇总。</summary>
    public bool ByOwner { get; private set; }

    internal IReadOnlyList<SpaceGroup> AllGroups => _groups;

    public int SelectedNode => SelectedRow is var (g, l) && l >= 0 ? _groups[g].Locations[l].Node : -1;

    public SpaceGroup? SelectedGroup => SelectedRow is var (g, _) && g >= 0 ? _groups[g] : null;

    private (int Group, int Location) SelectedRow => SelectedIndices.Count > 0 && SelectedIndices[0] < _rows.Count ? _rows[SelectedIndices[0]] : (-1, -1);

    internal int RowCount => _rows.Count;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyMetrics();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyMetrics();
    }

    private void ApplyMetrics()
    {
        SmallImageList?.Dispose();
        SmallImageList = new ImageList { ImageSize = new Size(1, Ui.Scale(this, 24)) };
        ApplyColumnWidths();
    }

    private void ApplyColumnWidths()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        for (var i = 1; i < Columns.Count && i < BaseColumnWidths.Length; i++)
        {
            Columns[i].Width = Ui.Scale(this, i == 4 && ByOwner ? NarrowOwnerColumn : BaseColumnWidths[i]);
        }

        FitNameColumn();
    }

    private const int GwlStyle = -16;
    private const int WsVScroll = 0x00200000;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    /// <summary>名称列占满剩余宽度；竖直滚动条还没出现时先把它的宽度留出来（和树形列表一样）。</summary>
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

        var reserve = (GetWindowLong(Handle, GwlStyle) & WsVScroll) != 0 ? 0 : SystemInformation.GetVerticalScrollBarWidthForDpi(DeviceDpi);
        var width = Math.Max(Ui.Scale(this, 220), ClientSize.Width - others - reserve - 1);
        if (Math.Abs(Columns[0].Width - width) > 1)
        {
            Columns[0].Width = width;
        }
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        FitNameColumn();
    }

    public void SetGroups(NodeTree? tree, Classification? classification, List<SpaceGroup> groups, bool byOwner)
    {
        _tree = tree;
        _classification = classification;
        _groups = groups;
        ByOwner = byOwner;
        _total = Math.Max(1, tree is null ? 1 : tree[tree.Root].AllocSize);
        _now = DateTime.Now;
        _expanded.Clear();
        _byNode.Clear();
        for (var g = 0; g < groups.Count; g++)
        {
            for (var l = 0; l < groups[g].Locations.Count; l++)
            {
                var node = groups[g].Locations[l].Node;
                if (node >= 0)
                {
                    _byNode.TryAdd(node, (g, l));
                }
            }
        }

        if (groups.Count > 0)
        {
            _expanded.Add(groups[0].Key); // 默认展开最大的一组
        }

        ApplyColumnWidths();
        Rebuild(selectFirst: true);
    }

    /// <summary>只显示满足条件的位置（组里没有满足条件的位置时整组隐藏）；null 取消过滤。</summary>
    public void SetFilter(Func<SpaceGroup, SpaceLocation, bool>? filter)
    {
        var keep = SelectedRow;
        _filter = filter;
        Rebuild(selectFirst: false);
        var row = _rows.IndexOf(keep);
        SelectRowQuietly(row >= 0 ? row : 0);
    }

    public bool HasFilter => _filter is not null;

    private bool Passes(SpaceGroup g, SpaceLocation l) => _filter is null || _filter(g, l);

    private void Rebuild(bool selectFirst)
    {
        _suppressSelection = true;
        try
        {
            _rows.Clear();
            for (var g = 0; g < _groups.Count; g++)
            {
                var group = _groups[g];
                var visible = Enumerable.Range(0, group.Locations.Count).Where(l => Passes(group, group.Locations[l])).ToList();
                if (visible.Count == 0)
                {
                    continue;
                }

                _rows.Add((g, -1));
                if (_expanded.Contains(group.Key))
                {
                    _rows.AddRange(visible.Select(l => (g, l)));
                }
            }

            VirtualListSize = _rows.Count;
            FitNameColumn();
            if (selectFirst && _rows.Count > 0)
            {
                SelectRowQuietly(0);
            }
        }
        finally
        {
            _suppressSelection = false;
        }

        Invalidate();
    }

    private void SelectRowQuietly(int row)
    {
        var old = _suppressSelection;
        _suppressSelection = true;
        try
        {
            SelectedIndices.Clear();
            if (row >= 0 && row < _rows.Count)
            {
                SelectedIndices.Add(row);
                FocusedItem = Items[row];
                EnsureVisible(row);
            }
        }
        finally
        {
            _suppressSelection = old;
        }

        Invalidate();
    }

    /// <summary>选中这个节点所在的位置（节点本身或最近的上级是某个位置），必要时展开组。不触发事件。找不到返回 false。</summary>
    public bool SelectNode(int node)
    {
        if (_tree is null || node < 0)
        {
            return false;
        }

        for (var n = node; n >= 0; n = n == _tree.Root ? -1 : _tree[n].Parent)
        {
            if (!_byNode.TryGetValue(n, out var hit) || !Passes(_groups[hit.Group], _groups[hit.Group].Locations[hit.Location]))
            {
                continue;
            }

            if (_expanded.Add(_groups[hit.Group].Key))
            {
                Rebuild(selectFirst: false);
            }

            SelectRowQuietly(_rows.IndexOf(hit));
            return true;
        }

        SelectRowQuietly(-1);
        return false;
    }

    /// <summary>展开或折叠一个组。</summary>
    public void ToggleGroup(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return;
        }

        var (g, _) = _rows[row];
        var key = _groups[g].Key;
        if (!_expanded.Remove(key))
        {
            _expanded.Add(key);
        }

        Rebuild(selectFirst: false);
        SelectRowQuietly(_rows.IndexOf((g, -1)));
    }

    private void OnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressSelection || SelectedIndices.Count == 0)
        {
            return;
        }

        Notify(SelectedRow);
    }

    private void Notify((int Group, int Location) row)
    {
        if (row.Group < 0)
        {
            return;
        }

        if (row.Location < 0)
        {
            GroupSelected?.Invoke(this, _groups[row.Group]);
        }
        else if (_groups[row.Group].Locations[row.Location] is { IsRemainder: false } loc)
        {
            NodeSelected?.Invoke(this, loc.Node);
        }
        else
        {
            GroupSelected?.Invoke(this, _groups[row.Group]); // "其他 N 处"没有对应的目录，显示整组
        }
    }

    /// <summary>模拟用户点选一行（测试用）。</summary>
    internal void SelectRowAsUser(int row)
    {
        SelectRowQuietly(row);
        Notify(_rows[row]);
    }

    internal int RowOfNode(int node) => _byNode.TryGetValue(node, out var hit) ? _rows.IndexOf(hit) : -1;

    internal int RowOfGroup(string key) => _rows.FindIndex(r => r.Location < 0 && _groups[r.Group].Key == key);

    // ---------------- 显示 ----------------

    /// <summary>一行的各列文字（读屏软件和测试用；绘制时另外画标签和条形）。</summary>
    internal string[] Describe(int row)
    {
        var (g, l) = _rows[row];
        var group = _groups[g];
        if (l < 0)
        {
            return [GroupTitle(group), ByteSize.Format(group.Bytes), Percent(group.Bytes, _total), TimeText.Relative(GroupLastWrite(group), _now), GroupOwnerText(group), GroupSummary(group)];
        }

        var loc = group.Locations[l];
        var kind = loc.Rule >= 0 && _classification is { } c
            ? $"{Names.Display(c.Engine.Rules.Locations[loc.Rule].Kind)} {RiskText(loc)}"
            : loc.IsRemainder ? "" : L.T("未识别");
        var owner = ByOwner || loc.Owner is null ? "" : loc.Owner.Display;
        var lastWrite = loc.Node >= 0 && _tree is not null ? TimeText.Relative(_tree[loc.Node].LastWriteUtc, _now) : "";
        return [LocationTitle(loc), ByteSize.Format(loc.Bytes), Percent(loc.Bytes, Math.Max(1, group.Bytes)), lastWrite, owner, kind];
    }

    private static string GroupTitle(SpaceGroup g) => g.Name;

    private static string Percent(long part, long whole) => (Math.Min(100, part * 100.0 / whole)).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private long GroupLastWrite(SpaceGroup g) => _tree is null ? 0 : g.Locations.Where(l => l.Node >= 0).Select(l => _tree[l.Node].LastWriteUtc).DefaultIfEmpty(0).Max();

    /// <summary>组的归属列：按归属汇总时是来源（规则 / 推断）；按类别汇总时列出最大的几个应用。</summary>
    private string GroupOwnerText(SpaceGroup g)
    {
        if (ByOwner)
        {
            return g.IsUnknown ? L.T("未识别") : g.Owner is { } o ? RuleView.SourceTag(o.Source).Text : "";
        }

        var owners = g.Locations.Where(l => l.Owner is not null).GroupBy(l => l.Owner!.Key)
            .Select(x => (Name: x.First().Owner!.Display, Bytes: x.Sum(l => l.Bytes)))
            .OrderByDescending(x => x.Bytes).Select(x => x.Name).ToList();
        return owners.Count switch
        {
            0 => "",
            <= 2 => string.Join(L.T("、"), owners),
            _ => L.F("{0}、{1} 等 {2} 个", owners[0], owners[1], owners.Count),
        };
    }

    private static string GroupSummary(SpaceGroup g)
    {
        var count = L.F("{0} 处", g.LocationCount.ToString("N0", CultureInfo.InvariantCulture));
        return g.CleanableBytes > 0 ? L.F("{0} · 可释放 {1}", count, ByteSize.Format(g.CleanableBytes)) : count;
    }

    private string LocationTitle(SpaceLocation loc)
    {
        if (loc.IsRemainder)
        {
            return loc.MergedCount > 0
                ? L.F("其他 {0} 处", loc.MergedCount.ToString("N0", CultureInfo.InvariantCulture))
                : L.F("零散的小位置（各不到 {0}）", ByteSize.Format(SpaceGroups.DefaultUnknownThreshold));
        }

        var path = _tree is null ? "" : PathDisplay.Shorten(_tree.GetPath(loc.Node));
        return loc.IsParentRemainder ? L.F("{0}（其余部分）", path) : path;
    }

    private string RiskText(SpaceLocation loc)
    {
        if (_classification is not { } c || loc.Rule < 0)
        {
            return "";
        }

        var rule = c.Engine.Rules.Locations[loc.Rule];
        var risk = loc.Node >= 0 ? c.EffectiveRisk(loc.Node) ?? rule.Risk : rule.Risk;
        return RuleView.RiskLabel(risk, rule);
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        var cells = Describe(e.ItemIndex);
        var item = new ListViewItem(cells[0]);
        for (var i = 1; i < cells.Length; i++)
        {
            item.SubItems.Add(cells[i]);
        }

        e.Item = item;
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.SubItem is null || e.ItemIndex >= _rows.Count)
        {
            return;
        }

        var g = e.Graphics;
        var row = e.ItemIndex;
        var (gi, li) = _rows[row];
        var group = _groups[gi];
        var isGroup = li < 0;
        var loc = isGroup ? null : group.Locations[li];
        var selected = SelectedIndices.Count > 0 && SelectedIndices[0] == row;
        var back = selected ? Theme.RowSelected : row == _hoverRow ? Theme.RowHover : isGroup ? Theme.Background : Color.White;
        using (var b = new SolidBrush(back))
        {
            g.FillRectangle(b, e.Bounds);
        }

        using (var line = new Pen(Theme.RowLine))
        {
            g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        }

        _fonts.Update(Font);
        var s = (float)DeviceDpi / 96;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var tagH = (int)(17 * s);
        var tagY = e.Bounds.Y + (e.Bounds.Height - tagH) / 2;
        var textColor = loc is { IsRemainder: true } ? Theme.Muted : Theme.Text;
        switch (e.ColumnIndex)
        {
            case 0:
            {
                var x = e.Bounds.X + (int)(6 * s);
                if (isGroup)
                {
                    var chevron = new Rectangle(x, e.Bounds.Y + (e.Bounds.Height - (int)(14 * s)) / 2, (int)(14 * s), (int)(14 * s));
                    Ui.DrawChevron(g, chevron, _expanded.Contains(group.Key), ColorTranslator.FromHtml("#666666"));
                    x += (int)(20 * s);
                    TextRenderer.DrawText(g, e.SubItem.Text, _fonts.Bold, Rectangle.FromLTRB(x, e.Bounds.Y, e.Bounds.Right - 2, e.Bounds.Bottom), Theme.Text, flags);
                }
                else
                {
                    x += (int)(30 * s);
                    if (loc is { IsRemainder: false } && _tree is not null)
                    {
                        var isDir = _tree[loc.Node].IsDirectory;
                        var iconW = (int)((isDir ? 14 : 10) * s);
                        var iconH = (int)((isDir ? 11 : 13) * s);
                        var icon = new Rectangle(x + (isDir ? 0 : (int)(2 * s)), e.Bounds.Y + (e.Bounds.Height - iconH) / 2, iconW, iconH);
                        using var ib = new SolidBrush(isDir ? Theme.FolderIcon : Theme.FileIcon);
                        g.FillRectangle(ib, icon);
                    }

                    x += (int)(20 * s);
                    TextRenderer.DrawText(g, e.SubItem.Text, _fonts.Regular, Rectangle.FromLTRB(x, e.Bounds.Y, e.Bounds.Right - 2, e.Bounds.Bottom), textColor,
                        flags | TextFormatFlags.PathEllipsis);
                }

                break;
            }

            case 2:
            {
                var part = isGroup ? group.Bytes : loc!.Bytes;
                var whole = isGroup ? _total : Math.Max(1, group.Bytes);
                var pct = Math.Min(100, part * 100.0 / whole);
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
            {
                var x = e.Bounds.X + (int)(6 * s);
                if (isGroup && ByOwner)
                {
                    if (group.IsUnknown)
                    {
                        RuleView.DrawTag(g, L.T("未识别"), _fonts.Small, new Point(x, tagY), tagH, RuleView.Unknown);
                    }
                    else if (group.Owner is { } o)
                    {
                        var (tag, style) = RuleView.SourceTag(o.Source);
                        RuleView.DrawTag(g, tag, _fonts.Small, new Point(x, tagY), tagH, style);
                    }
                }
                else if (!isGroup && loc!.Owner is { } owner && !ByOwner)
                {
                    var w = Math.Min(TextRenderer.MeasureText(g, owner.Display, _fonts.Regular).Width, e.Bounds.Right - x - (int)(44 * s));
                    TextRenderer.DrawText(g, owner.Display, _fonts.Regular, new Rectangle(x, e.Bounds.Y, w, e.Bounds.Height), Theme.Text, flags);
                    x += w + (int)(4 * s);
                    if (x + (int)(36 * s) < e.Bounds.Right)
                    {
                        var (tag, style) = RuleView.SourceTag(owner.Source);
                        RuleView.DrawTag(g, tag, _fonts.Small, new Point(x, tagY), tagH, style);
                    }
                }
                else
                {
                    TextRenderer.DrawText(g, e.SubItem.Text, _fonts.Regular, Rectangle.Inflate(e.Bounds, -(int)(6 * s), 0), Theme.Muted, flags);
                }

                break;
            }

            case 5:
            {
                var x = e.Bounds.X + (int)(6 * s);
                if (isGroup)
                {
                    TextRenderer.DrawText(g, e.SubItem.Text, _fonts.Regular, Rectangle.Inflate(e.Bounds, -(int)(6 * s), 0), Theme.Muted, flags);
                }
                else if (loc!.Rule >= 0 && _classification is { } c)
                {
                    var rule = c.Engine.Rules.Locations[loc.Rule];
                    var kind = Names.Display(rule.Kind);
                    var w = TextRenderer.MeasureText(g, kind, _fonts.Regular).Width;
                    TextRenderer.DrawText(g, kind, _fonts.Regular, new Rectangle(x, e.Bounds.Y, w, e.Bounds.Height), Theme.Text, flags);
                    x += w + (int)(4 * s);
                    var risk = loc.Node >= 0 ? c.EffectiveRisk(loc.Node) ?? rule.Risk : rule.Risk;
                    if (x < e.Bounds.Right - (int)(20 * s))
                    {
                        RuleView.DrawTag(g, RuleView.RiskLabel(risk, rule), _fonts.Small, new Point(x, tagY), tagH, RuleView.RiskStyle(risk));
                    }
                }
                else if (!loc.IsRemainder)
                {
                    RuleView.DrawTag(g, L.T("未识别"), _fonts.Small, new Point(x, tagY), tagH, RuleView.Unknown);
                }

                break;
            }

            default:
            {
                var align = Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
                var font = e.ColumnIndex == 1 && isGroup ? _fonts.Bold : _fonts.Regular;
                TextRenderer.DrawText(g, e.SubItem.Text, font, Rectangle.Inflate(e.Bounds, -(int)(6 * s), 0), textColor, flags | align);
                break;
            }
        }
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && HitTest(e.Location).Item is { } item && item.Index < _rows.Count && _rows[item.Index].Location < 0)
        {
            var s = (float)DeviceDpi / 96;
            if (e.X < item.Bounds.X + (int)(24 * s))
            {
                ToggleGroup(item.Index); // 点小三角
                return;
            }
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (HitTest(e.Location).Item is { } item)
        {
            Activate(item.Index);
        }
    }

    /// <summary>双击或回车：组展开 / 折叠，位置在目录视图里定位。</summary>
    internal void Activate(int row)
    {
        if (row < 0 || row >= _rows.Count)
        {
            return;
        }

        var (g, l) = _rows[row];
        if (l < 0)
        {
            ToggleGroup(row);
        }
        else if (_groups[g].Locations[l] is { IsRemainder: false } loc)
        {
            RevealRequested?.Invoke(this, loc.Node);
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
        if (SelectedIndices.Count > 0)
        {
            var row = SelectedIndices[0];
            var (g, l) = _rows[row];
            var handled = true;
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    Activate(row);
                    break;
                case Keys.Right when l < 0 && !_expanded.Contains(_groups[g].Key):
                case Keys.Left when l < 0 && _expanded.Contains(_groups[g].Key):
                    ToggleGroup(row);
                    break;
                case Keys.Left or Keys.Back when l >= 0:
                    SelectRowQuietly(_rows.IndexOf((g, -1)));
                    Notify((g, -1));
                    break;
                case Keys.C when e.Control && l >= 0 && _groups[g].Locations[l] is { IsRemainder: false } loc:
                    CopyPathRequested?.Invoke(this, loc.Node);
                    break;
                default:
                    handled = false;
                    break;
            }

            if (handled)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        base.OnKeyDown(e);
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

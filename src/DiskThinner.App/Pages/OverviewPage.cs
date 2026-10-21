using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Rules;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.App.Pages;

/// <summary>
/// 概览（原型 .p-overview）：四张卡片回答"还剩多少、涨得多快、能清多少、还有多少不认识"，
/// 下面是"建议先处理"和"占用最多"。增长相关的内容要等 M3 有了快照才有数据。
/// </summary>
internal sealed class OverviewPage : UserControl
{
    private readonly Label _sub = Ui.Label("", Theme.Ui, Theme.Muted);
    private readonly StatCard _used = new("C 盘已用");
    private readonly StatCard _growth = new("近 7 天净增长");
    private readonly StatCard _cleanable = new("可释放（规则判定）");
    private readonly StatCard _unknown = new("未识别");
    private readonly ItemList _suggest = new("建议先处理", ["项目", "大小", "风险", ""]);
    private readonly ItemList _largest = new("占用最多", ["位置", "大小", "归属", "风险"]);
    private readonly Label _empty = Ui.Label("正在扫描…", Theme.Ui, Theme.Muted);

    public OverviewPage()
    {
        BackColor = Theme.Background;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 24);
        AccessibleName = "概览";

        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 12) };
        var title = Ui.Label("概览", new Font(Theme.UiFontName, 15F, FontStyle.Bold));
        title.Margin = new Padding(0, 0, 12, 0);
        _sub.Margin = new Padding(0, 8, 0, 0);
        head.Controls.Add(title);
        head.Controls.Add(_sub);

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1, Height = 132, BackColor = Color.Transparent, Margin = new Padding(0) };
        for (var i = 0; i < 4; i++)
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        cards.Controls.Add(_used, 0, 0);
        cards.Controls.Add(_growth, 1, 0);
        cards.Controls.Add(_cleanable, 2, 0);
        cards.Controls.Add(_unknown, 3, 0);
        foreach (Control c in cards.Controls)
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 0, 12, 0);
        }

        var lists = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 400, BackColor = Color.Transparent, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(0, 12, 0, 0) };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.Controls.Add(_suggest, 0, 0);
        lists.Controls.Add(_largest, 1, 0);
        _suggest.Dock = _largest.Dock = DockStyle.Fill;
        _suggest.Margin = new Padding(0, 0, 12, 0);
        _largest.Margin = new Padding(0, 0, 12, 0);

        _empty.Dock = DockStyle.Top;
        _empty.Padding = new Padding(0, 16, 0, 0);

        Controls.Add(lists);
        Controls.Add(cards);
        Controls.Add(_empty);
        Controls.Add(head);

        _growth.Set("—", "需要至少两次快照，增长分析在 M3 加入", null);
        _suggest.Activated += (_, node) => NavigateRequested?.Invoke(this, node);
        _largest.Activated += (_, node) => NavigateRequested?.Invoke(this, node);
    }

    /// <summary>用户要在空间浏览里查看某个节点。</summary>
    public event EventHandler<int>? NavigateRequested;

    internal ItemList Suggestions => _suggest;

    internal ItemList Largest => _largest;

    internal StatCard CleanableCard => _cleanable;

    public void ShowScanning() => _empty.Text = "正在扫描…";

    public void SetResult(ScanResult? result, Classification? c, OwnerMap? owners = null)
    {
        _owners = c is null ? null : owners ?? OwnerMap.RulesOnly(c);
        _empty.Visible = result is null;
        if (result is null)
        {
            _empty.Text = "没有扫描结果。";
            return;
        }

        var (tree, stats) = result;
        var drive = stats.Root.TrimEnd('\\');
        var mode = stats.Mode == ScanMode.Mft ? "MFT" : "遍历，估算";
        _sub.Text = $"上次扫描：{DateTime.Now:HH:mm}（{mode}，{stats.TotalDuration.TotalSeconds:F1} 秒）";

        // ---- 卡片 ----
        var used = stats.VolumeTotalBytes - stats.VolumeFreeBytes;
        var isVolume = tree.GetPath(tree.Root).Length <= 3;
        _used.Title = isVolume ? $"{drive} 已用" : "扫描范围";
        if (isVolume && stats.VolumeTotalBytes > 0)
        {
            _used.Set($"{ByteSize.Format(used)} / {ByteSize.Format(stats.VolumeTotalBytes)}",
                $"可用 {ByteSize.Format(stats.VolumeFreeBytes)}", used / (double)stats.VolumeTotalBytes);
        }
        else
        {
            _used.Set(ByteSize.Format(tree[tree.Root].AllocSize), tree.GetPath(tree.Root), null);
        }

        if (c is null)
        {
            _cleanable.Set("—", "规则没有加载", null);
            _unknown.Set("—", "", null);
            _suggest.SetItems([]);
            _largest.SetItems([]);
            return;
        }

        _cleanable.Set(ByteSize.Format(c.TotalCleanable),
            $"安全 {ByteSize.Format(c.CleanableBytes(Risk.Safe))} · 需重新下载 {ByteSize.Format(c.CleanableBytes(Risk.Low))} · 丢历史 {ByteSize.Format(c.CleanableBytes(Risk.Medium))}", null);

        var hotspots = c.UnexplainedHotspots(100L << 20);
        _unknown.Set(ByteSize.Format(c.Unexplained(tree.Root)), $"其中 {hotspots.Count} 处超过 100 MB", null);

        _suggest.SetItems(BuildSuggestions(tree, c, hotspots, _owners!));
        _largest.SetItems(BuildLargest(tree, c, _owners!));
    }

    private OwnerMap? _owners;

    /// <summary>
    /// 建议先处理：可清理的按规则汇总取最大的几项；再加上只能用系统工具处理的大项（如分页文件、WinSxS）和最大的未识别位置。
    /// </summary>
    internal static List<ListItem> BuildSuggestions(NodeTree tree, Classification c, List<(int Node, long Bytes, bool IsRemainder)> hotspots, OwnerMap owners)
    {
        var rules = c.Engine.Rules;
        string TitleAt(LocationRule rule, int node) => Title(rule, owners.OwnerOf(node));
        var items = new List<ListItem>();
        foreach (var g in c.Roots.Where(r => r.CleanableBytes > 0).GroupBy(r => r.Rule)
                     .Select(g => (Rule: rules.Locations[g.Key], Roots: g.ToList(), Bytes: g.Sum(r => r.CleanableBytes)))
                     .OrderByDescending(x => x.Bytes).Take(6))
        {
            var largest = g.Roots.MaxBy(r => r.CleanableBytes)!;
            var count = g.Roots.Count > 1 ? $"（{g.Roots.Count} 处）" : "";
            items.Add(new ListItem(largest.Node, [TitleAt(g.Rule, largest.Node) + count, ByteSize.Format(g.Bytes), "", "查看"], g.Rule.Purpose,
                RuleView.RiskStyle(g.Rule.Risk), RuleView.RiskLabel(g.Rule.Risk, g.Rule)));
        }

        foreach (var root in c.Roots.Where(r => rules.Locations[r.Rule].Risk == Risk.Guide && r.Bytes >= 1L << 30)
                     .OrderByDescending(r => r.Bytes).Take(2))
        {
            var rule = rules.Locations[root.Rule];
            items.Add(new ListItem(root.Node, [TitleAt(rule, root.Node), ByteSize.Format(root.Bytes), "", "查看步骤"], rule.Purpose,
                RuleView.RiskStyle(Risk.Guide), RuleView.RiskLabel(Risk.Guide, rule)));
        }

        if (hotspots.FirstOrDefault(h => !h.IsRemainder) is { Bytes: > 0 } spot)
        {
            items.Add(new ListItem(spot.Node, [Redact(tree.GetPath(spot.Node)), ByteSize.Format(spot.Bytes), "", "查看"], "没有规则认识这里",
                RuleView.Unknown, "未识别"));
        }

        return items;
    }

    /// <summary>占用最多：规则命中的位置按大小排列（不含兜底规则），再混入最大的未识别位置。</summary>
    internal static List<ListItem> BuildLargest(NodeTree tree, Classification c, OwnerMap owners)
    {
        var rules = c.Engine.Rules;
        var candidates = c.Roots.Where(r => rules.Locations[r.Rule].Mode != MatchMode.Under && r.Bytes > 0)
            .Select(r => (r.Node, r.Bytes, Rule: (LocationRule?)rules.Locations[r.Rule]))
            .Concat(c.UnexplainedHotspots(100L << 20).Where(h => !h.IsRemainder).Select(h => (h.Node, h.Bytes, Rule: (LocationRule?)null)))
            .OrderByDescending(x => x.Bytes)
            .Take(8);

        var items = new List<ListItem>();
        foreach (var (node, bytes, rule) in candidates)
        {
            var path = Redact(tree.GetPath(node));
            var owner = owners.OwnerOf(node)?.Display;
            if (rule is null)
            {
                // 没有规则认识，但可能推断出了归属
                items.Add(new ListItem(node, [path, ByteSize.Format(bytes), owner ?? "未识别", ""], null, RuleView.Unknown, "未识别"));
                continue;
            }

            var risk = c.EffectiveRisk(node) ?? rule.Risk;
            items.Add(new ListItem(node, [path, ByteSize.Format(bytes), owner ?? Names.Display(rule.Kind), ""], null,
                RuleView.RiskStyle(risk), RuleView.RiskLabel(risk, rule)));
        }

        return items;
    }

    /// <summary>
    /// 一行的标题：规则"作用"的第一句（如"npm 下载过的包""当前用户的临时文件"），比类别名更能说明是什么；
    /// 太长时退回"归属 + 类别"。完整的作用放在提示里。
    /// </summary>
    private static string Title(LocationRule rule, Owner? owner)
    {
        // 去掉括号里的补充说明，再取第一句："ASP.NET（.NET Framework）网站的编译缓存，……" → "ASP.NET网站的编译缓存"
        var text = System.Text.RegularExpressions.Regex.Replace(rule.Purpose, "（[^）]*）", "");
        var cut = text.IndexOfAny(['，', '。', '、']);
        var first = (cut > 0 ? text[..cut] : text).Trim();
        if (first.Length is > 0 and <= 30)
        {
            return first;
        }

        return (owner is { Key: not "f:user" and not "f:several" and not "f:projects" } ? owner.Display + " " : "") + Names.Display(rule.Kind);
    }

    /// <summary>把当前用户的目录显示成 %LOCALAPPDATA%、%USERPROFILE% 等，路径更短，截图里也不暴露用户名。</summary>
    internal static string Redact(string path) => PathDisplay.Shorten(path);

    /// <summary>一行数据：点击后跳到 <see cref="Node"/>。</summary>
    internal sealed record ListItem(int Node, string[] Cells, string? Note, RuleView.TagStyle TagStyle, string Tag);

    /// <summary>原型 .stat 卡片：标题、大数字、说明和可选的进度条。</summary>
    internal sealed class StatCard : Panel
    {
        private readonly Label _title;
        private readonly Label _value = new() { AutoSize = true, Font = new Font(Theme.UiFontName, 16F, FontStyle.Bold), ForeColor = Theme.Text, UseMnemonic = false };
        private readonly Label _foot = new() { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false };
        private double? _meter;

        public StatCard(string title)
        {
            // 先建子控件再设 Padding：设置 Padding 会立即触发 OnLayout
            _title = new Label { Text = title, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false };
            BackColor = Theme.Card;
            Padding = new Padding(16, 12, 16, 12);
            Controls.AddRange([_title, _value, _foot]);
            SetStyle(ControlStyles.ResizeRedraw, true);
            AccessibleName = title;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Title
        {
            get => _title.Text;
            set
            {
                _title.Text = value;
                AccessibleName = value;
            }
        }

        public string ValueText => _value.Text;

        public void Set(string value, string foot, double? meter)
        {
            _value.Text = value;
            _foot.Text = foot;
            _meter = meter;
            AccessibleDescription = $"{value}，{foot}";
            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (_title is null)
            {
                return;
            }

            _title.Location = new Point(Padding.Left, Padding.Top);
            _value.Location = new Point(Padding.Left - 2, _title.Bottom + 2);
            _foot.MaximumSize = new Size(Math.Max(60, Width - Padding.Horizontal), 0);
            _foot.Location = new Point(Padding.Left, _value.Bottom + (_meter is null ? 4 : 18));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            if (_meter is { } m)
            {
                var y = _value.Bottom + 6;
                var h = Ui.Scale(this, 8);
                var w = Width - Padding.Horizontal;
                using var back = new SolidBrush(ColorTranslator.FromHtml("#e6e6e6"));
                using var fill = new SolidBrush(m > 0.9 ? Theme.Up : Theme.Accent);
                e.Graphics.FillRectangle(back, Padding.Left, y, w, h);
                e.Graphics.FillRectangle(fill, Padding.Left, y, (int)(w * Math.Clamp(m, 0, 1)), h);
            }
        }
    }

    /// <summary>卡片里的列表：ListView 详细视图，最后一列画标签。双击或回车跳到空间浏览。</summary>
    internal sealed class ItemList : Panel
    {
        private readonly ListView _view;
        private readonly DerivedFonts _fonts = new();
        private List<ListItem> _items = [];

        public ItemList(string title, string[] columns)
        {
            BackColor = Theme.Card;
            Padding = new Padding(14, 10, 14, 10);
            var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = Theme.UiBold, ForeColor = Theme.Text, UseMnemonic = false };
            var hint = new Label { Text = "双击或按回车在空间浏览中查看", Dock = DockStyle.Bottom, Height = 20, Font = Theme.Small, ForeColor = Theme.Subtle, UseMnemonic = false };
            _view = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                OwnerDraw = true,
                Font = Theme.Ui,
                AccessibleName = title,
            };
            foreach (var c in columns)
            {
                _view.Columns.Add(c);
            }

            _view.DrawColumnHeader += (_, e) => e.DrawDefault = true;
            _view.DrawItem += (_, e) => e.DrawDefault = false;
            _view.DrawSubItem += DrawSubItem;
            _view.ItemActivate += (_, _) =>
            {
                if (_view.SelectedIndices.Count > 0)
                {
                    Activated?.Invoke(this, _items[_view.SelectedIndices[0]].Node);
                }
            };
            _view.ClientSizeChanged += (_, _) => FitColumns();
            _view.HandleCreated += (_, _) =>
            {
                _view.SmallImageList = new ImageList { ImageSize = new Size(1, Ui.Scale(_view, 30)) };
                FitColumns();
            };
            Controls.Add(_view);
            Controls.Add(hint);
            Controls.Add(heading);
            AccessibleName = title;
        }

        public event EventHandler<int>? Activated;

        public IReadOnlyList<ListItem> Items => _items;

        /// <summary>模拟用户激活第 i 行（测试用）。</summary>
        internal void ActivateAt(int i) => Activated?.Invoke(this, _items[i].Node);

        public void SetItems(List<ListItem> items)
        {
            _items = items;
            _view.BeginUpdate();
            _view.Items.Clear();
            foreach (var it in items)
            {
                var row = new ListViewItem(it.Cells[0]) { ToolTipText = it.Note };
                for (var i = 1; i < _view.Columns.Count; i++)
                {
                    row.SubItems.Add(i < it.Cells.Length ? it.Cells[i] : "");
                }

                // 风险列的文字给读屏软件用（绘制时画成标签）
                var tagColumn = Array.FindIndex(it.Cells, string.IsNullOrEmpty);
                if (tagColumn > 0 && tagColumn < row.SubItems.Count)
                {
                    row.SubItems[tagColumn].Text = it.Tag;
                }

                _view.Items.Add(row);
            }

            _view.EndUpdate();
            _view.ShowItemToolTips = true;
            FitColumns();
        }

        private void FitColumns()
        {
            if (_view.Columns.Count < 2 || !_view.IsHandleCreated)
            {
                return;
            }

            var w = _view.ClientSize.Width;
            int[] fixedWidths = _view.Columns.Count == 4 ? [80, 110, 90] : [80, 110];
            var rest = w - fixedWidths.Sum(x => Ui.Scale(_view, x)) - 2;
            _view.Columns[0].Width = Math.Max(Ui.Scale(_view, 120), rest);
            for (var i = 1; i < _view.Columns.Count; i++)
            {
                _view.Columns[i].Width = Ui.Scale(_view, fixedWidths[i - 1]);
            }
        }

        private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item is null || e.SubItem is null || e.ItemIndex >= _items.Count)
            {
                return;
            }

            _fonts.Update(_view.Font);
            var g = e.Graphics;
            var item = _items[e.ItemIndex];
            using (var back = new SolidBrush(e.Item.Selected ? Theme.RowSelected : Color.White))
            {
                g.FillRectangle(back, e.Bounds);
            }

            using (var line = new Pen(Theme.RowLine))
            {
                g.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            var pad = Ui.Scale(_view, 6);
            var cell = Rectangle.FromLTRB(e.Bounds.Left + pad, e.Bounds.Top, e.Bounds.Right - pad, e.Bounds.Bottom);
            var text = e.ColumnIndex < item.Cells.Length ? item.Cells[e.ColumnIndex] : "";
            var isTag = e.ColumnIndex > 0 && string.IsNullOrEmpty(text);
            if (isTag)
            {
                var h = Ui.Scale(_view, 17);
                RuleView.DrawTag(g, item.Tag, _fonts.Small, new Point(cell.X, cell.Y + (cell.Height - h) / 2), h, item.TagStyle);
                return;
            }

            if (e.ColumnIndex == item.Cells.Length - 1 && text is "查看" or "查看步骤")
            {
                TextRenderer.DrawText(g, text, _fonts.Regular, cell, Theme.Accent, flags);
                return;
            }

            var align = e.ColumnIndex == 1 ? TextFormatFlags.Right : TextFormatFlags.Left;
            TextRenderer.DrawText(g, text, e.ColumnIndex == 0 ? _fonts.Regular : _fonts.Regular, cell, Theme.Text, flags | align);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fonts.Dispose();
                _view.SmallImageList?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

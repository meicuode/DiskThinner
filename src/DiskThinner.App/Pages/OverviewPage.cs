using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Growth;
using DiskThinner.App.Rules;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;

namespace DiskThinner.App.Pages;

/// <summary>
/// 概览（原型 .p-overview）：四张卡片回答"还剩多少、涨得多快、能清多少、还有多少不认识"，
/// 下面是"建议先处理"和"占用最多"。增长相关的内容要等 M3 有了快照才有数据。
/// </summary>
internal sealed class OverviewPage : UserControl
{
    private readonly Label _sub = Ui.Label("", Theme.Ui, Theme.Muted);
    private readonly StatCard _used = new(L.T("C 盘已用"));
    private readonly StatCard _growth = new(L.T("近 7 天净增长"));
    private readonly StatCard _cleanable = new(L.T("可释放（规则判定）"));
    private readonly StatCard _unknown = new(L.T("未识别"));
    private readonly ItemList _suggest = new(L.T("建议先处理"), [L.T("项目"), L.T("大小"), L.T("风险"), ""]);
    private readonly ItemList _largest = new(L.T("占用最多"), [L.T("位置"), L.T("大小"), L.T("归属"), L.T("风险")]);
    private readonly Label _empty = Ui.Label(L.T("正在扫描…"), Theme.Ui, Theme.Muted);

    public OverviewPage()
    {
        BackColor = Theme.Background;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 24);
        AccessibleName = L.T("概览");

        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 12) };
        var title = Ui.Label(L.T("概览"), new Font(Theme.UiFontName, 15F, FontStyle.Bold));
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

        var trendCard = new ItemCard(L.T("可用空间（近 30 天）"), _trend, L.T("数据来自快照"));
        var row2 = Row(390, 57.5f, trendCard, _suggest);
        var row3 = Row(350, 50, _largest, _fastest);

        _empty.Dock = DockStyle.Top;
        _empty.Padding = new Padding(0, 16, 0, 0);

        Controls.Add(row3);
        Controls.Add(row2);
        Controls.Add(cards);
        Controls.Add(_empty);
        Controls.Add(head);

        _growth.Set("—", L.T("需要相隔半天以上的两次快照"), null);
        _trend.SetData([], L.T("快照少于两个，还画不出趋势"));
        _fastest.SetPlaceholder(L.T("需要相隔半天以上的两次快照"));
        _fastest.HintLinkClicked += (_, _) => StartMonitorRequested?.Invoke(this, EventArgs.Empty);
        _suggest.Activated += (_, node) => NavigateRequested?.Invoke(this, node);
        _largest.Activated += (_, node) => NavigateRequested?.Invoke(this, node);
        _fastest.Activated += (_, node) =>
        {
            if (node >= 0)
            {
                NavigateRequested?.Invoke(this, node);
            }
        };
    }

    private static TableLayoutPanel Row(int height, float leftPercent, Control left, Control right)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = height, BackColor = Color.Transparent, Padding = new Padding(0, 12, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPercent));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - leftPercent));
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(right, 1, 0);
        left.Dock = right.Dock = DockStyle.Fill;
        left.Margin = right.Margin = new Padding(0, 0, 12, 0);
        return row;
    }

    private readonly TrendChart _trend = new() { AccessibleName = L.T("可用空间趋势") };
    private readonly ItemList _fastest = new(L.T("增长最快（近 7 天）"), [L.T("目录"), L.T("变化"), L.T("写入者")], [86, 190]);

    // 增长最快的写入者列用到的数据：快照对比（SetGrowth）和写入监控（SetWrites）分别到达
    private GrowthModel? _growthModel;
    private NodeTree? _growthTree;
    private Classification? _growthC;
    private OwnerMap? _growthOwners;
    private DiskThinner.Monitor.WriteIndex? _writes;
    private double _writesHours;
    private double _writesRange;
    private bool _monitorEnabled = true;

    /// <summary>用户点了"开启 4 小时诊断"。</summary>
    public event EventHandler? StartMonitorRequested;

    /// <summary>
    /// 近 7 天（<see cref="GrowthModel.WeekBaseline"/> 到现在）的写入监控数据（docs/phase2a-design.md 5.9.5 节）。<paramref name="index"/> 为 null 表示读不了或还没读；
    /// <paramref name="enabled"/> 为 false（设置为"关闭"）时不显示开启诊断的提示。
    /// </summary>
    public void SetWrites(DiskThinner.Monitor.WriteIndex? index, double coveredHours, double rangeHours, bool enabled)
    {
        _writes = index;
        _writesHours = coveredHours;
        _writesRange = rangeHours;
        _monitorEnabled = enabled;
        RenderFastest();
    }

    internal ItemList Fastest => _fastest;

    internal StatCard GrowthCard => _growth;

    internal TrendChart Trend => _trend;

    /// <summary>
    /// 增长相关的部分：近 7 天净增长和"约 N 天后写满"、近 30 天可用空间趋势、增长最快的目录。
    /// 快照历史不够时显示说明。
    /// </summary>
    public void SetGrowth(GrowthModel? model, NodeTree? tree, Classification? c, OwnerMap? owners)
    {
        var now = model?.NowPoint;
        if (model is null || now is null)
        {
            _growth.Set("—", L.T("快照库不可用"), null);
            return;
        }

        var points = model.Snapshots.Where(s => s.TakenAtUtc >= model.NowUtc.AddDays(-30)).Select(GrowthStats.Of).ToList();
        if (!model.CurrentSaved)
        {
            points.Add(now.Value);
        }

        _trend.SetData(points.Select(p => (p.TakenAtUtc.ToLocalTime(), p.FreeBytes)).ToList(), L.T("快照少于两个，还画不出趋势"));

        if (model.WeekBaseline is not { } baseline || model.WeekDiff is null)
        {
            _growth.Title = L.T("近 7 天净增长");
            _growth.Set("—", model.Snapshots.Count == 0 ? L.T("还没有快照，扫描整个盘后会自动保存") : L.T("需要相隔半天以上的两次快照"), null);
            _fastest.SetItems([]);
            _fastest.SetPlaceholder(L.T("需要相隔半天以上的两次快照"));
            return;
        }

        var (change, days, toFull) = GrowthStats.Rate(GrowthStats.Of(baseline), now.Value);
        var label = days >= 6.5 ? L.T("近 7 天") : L.F("近 {0} 天", Math.Max(1, (int)Math.Round(days)));
        _growth.Title = L.F("{0}净增长", label);
        _fastest.Title = L.F("增长最快（{0}）", label);
        var foot = toFull is { } d && d < 3650
            ? L.F("按此速度约 {0} 天后写满", Math.Max(1, (int)Math.Round(d)))
            : change <= 0 ? L.T("没有在增长") : L.T("按此速度短期内不会写满");
        _growth.Set(BarChart.Signed(change), foot, null);
        (_growthModel, _growthTree, _growthC, _growthOwners) = (model, tree, c, owners);
        RenderFastest();
    }

    /// <summary>
    /// 增长最快：第三列"写入者"按三种情况显示（第 5.9.5 节）：近 7 天有监控写入时是"写入进程 ← 顶层应用"加"实测"；
    /// 没有监控写入、归属已知时是归属（范围内没有监控时加"未监控"）；都没有时是"未识别"或"已不存在"。
    /// </summary>
    private void RenderFastest()
    {
        _fastest.SetHint(_monitorEnabled && _writesHours <= 0
            ? L.T("近 7 天没有开过写入监控，增长只能按快照对比，看不到是哪个程序写的。")
            : null, _monitorEnabled && _writesHours <= 0 ? L.T("开启 4 小时诊断") : null);
        if (_growthModel is not { } model || model.WeekBaseline is null)
        {
            return;
        }

        var (tree, c, owners) = (_growthTree, _growthC, _growthOwners);
        var items = model.WeekHotspots.Take(8).Select(h =>
        {
            var node = tree?.FindPath(h.Path) ?? -1;
            var rule = node >= 0 ? c?.RuleOf(node) : null;
            var owner = node >= 0 ? owners?.OwnerOf(node)?.Display ?? (rule is { } r ? Names.Display(r.Kind) : null) : null;
            var path = Redact(h.Path) + (h.IsRemainder ? L.T("（其余部分）") : "");
            var note = L.F("之前 {0}，现在 {1}", ByteSize.Format(h.Previous), ByteSize.Format(h.Current));
            var color = h.Change > 0 ? Theme.Up : h.Change < 0 ? Theme.Down : (Color?)null;
            var cell = Monitor.MonitorText.Cell(_writes, _writesHours, _writesRange, h.Path, h.Change, !h.IsDirectory, rule?.Kind == Kind.VmDisk, shortChain: true, remainder: h.IsRemainder);
            if (cell is { Observed: true })
            {
                return new ListItem(node, [path, BarChart.Signed(h.Change), cell.Text], note + "\n" + cell.Note, RuleView.ObservedSource, cell.Tag, color, TagColumn: 2);
            }

            if (owner is not null)
            {
                // 有监控覆盖、只是这里没写入：只写归属，原因放在提示里；范围内没有监控：加"未监控"
                return cell is null
                    ? new ListItem(node, [path, BarChart.Signed(h.Change), owner], note, RuleView.NotMonitored, L.T("未监控"), color, TagColumn: 2)
                    : new ListItem(node, [path, BarChart.Signed(h.Change), owner], note + "\n" + cell.Text, RuleView.NotMonitored, "", color, TagColumn: 2);
            }

            return new ListItem(node, [path, BarChart.Signed(h.Change), ""], cell is null ? note : note + "\n" + cell.Text, RuleView.Unknown,
                node < 0 ? L.T("已不存在") : L.T("未识别"), color);
        }).ToList();
        _fastest.SetItems(items);
        _fastest.SetPlaceholder(items.Count == 0 ? L.T("这段时间里没有超过 100 MB 的变化") : null);
    }

    /// <summary>带标题和边框的卡片，里面放一个控件（趋势图）。</summary>
    private sealed class ItemCard : Panel
    {
        public ItemCard(string title, Control body, string? note)
        {
            BackColor = Theme.Card;
            Padding = new Padding(14, 10, 14, 10);
            var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = Theme.UiBold, ForeColor = Theme.Text, UseMnemonic = false };
            body.Dock = DockStyle.Fill;
            Controls.Add(body);
            if (note is not null)
            {
                Controls.Add(new Label { Text = note, Dock = DockStyle.Bottom, Height = 20, Font = Theme.Small, ForeColor = Theme.Subtle, UseMnemonic = false });
            }

            Controls.Add(heading);
            AccessibleName = title;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>用户要在空间浏览里查看某个节点。</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>焦点移到下面的列表时不要自动滚动页面。</summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    internal ItemList Suggestions => _suggest;

    internal ItemList Largest => _largest;

    internal StatCard CleanableCard => _cleanable;

    public void ShowScanning() => _empty.Text = L.T("正在扫描…");

    public void SetResult(ScanResult? result, Classification? c, OwnerMap? owners = null)
    {
        _owners = c is null ? null : owners ?? OwnerMap.RulesOnly(c);
        _empty.Visible = result is null;
        if (result is null)
        {
            _empty.Text = L.T("没有扫描结果。");
            return;
        }

        var (tree, stats) = result;
        var drive = stats.Root.TrimEnd('\\');
        var mode = stats.Mode == ScanMode.Mft ? "MFT" : L.T("遍历，估算");
        _sub.Text = L.F("上次扫描：{0:HH:mm}（{1}，{2:F1} 秒）", DateTime.Now, mode, stats.TotalDuration.TotalSeconds);

        // ---- 卡片 ----
        var used = stats.VolumeTotalBytes - stats.VolumeFreeBytes;
        var isVolume = tree.GetPath(tree.Root).Length <= 3;
        _used.Title = isVolume ? L.F("{0} 已用", drive) : L.T("扫描范围");
        if (isVolume && stats.VolumeTotalBytes > 0)
        {
            _used.Set($"{ByteSize.Format(used)} / {ByteSize.Format(stats.VolumeTotalBytes)}",
                L.F("可用 {0}", ByteSize.Format(stats.VolumeFreeBytes)), used / (double)stats.VolumeTotalBytes);
        }
        else
        {
            _used.Set(ByteSize.Format(tree[tree.Root].AllocSize), tree.GetPath(tree.Root), null);
        }

        if (c is null)
        {
            _cleanable.Set("—", L.T("规则没有加载"), null);
            _unknown.Set("—", "", null);
            _suggest.SetItems([]);
            _largest.SetItems([]);
            return;
        }

        _cleanable.Set(ByteSize.Format(c.TotalCleanable),
            L.F("安全 {0} · 需重新下载 {1} · 丢历史 {2}", ByteSize.Format(c.CleanableBytes(Risk.Safe)), ByteSize.Format(c.CleanableBytes(Risk.Low)), ByteSize.Format(c.CleanableBytes(Risk.Medium))), null);

        var hotspots = c.UnexplainedHotspots(100L << 20);
        _unknown.Set(ByteSize.Format(c.Unexplained(tree.Root)), L.F("其中 {0} 处超过 100 MB", hotspots.Count), null);

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
            var count = g.Roots.Count > 1 ? L.F("（{0} 处）", g.Roots.Count) : "";
            items.Add(new ListItem(largest.Node, [TitleAt(g.Rule, largest.Node) + count, ByteSize.Format(g.Bytes), "", L.T("查看")], g.Rule.Purpose,
                RuleView.RiskStyle(g.Rule.Risk), RuleView.RiskLabel(g.Rule.Risk, g.Rule), LinkLast: true));
        }

        foreach (var root in c.Roots.Where(r => rules.Locations[r.Rule].Risk == Risk.Guide && r.Bytes >= 1L << 30)
                     .OrderByDescending(r => r.Bytes).Take(2))
        {
            var rule = rules.Locations[root.Rule];
            items.Add(new ListItem(root.Node, [TitleAt(rule, root.Node), ByteSize.Format(root.Bytes), "", L.T("查看步骤")], rule.Purpose,
                RuleView.RiskStyle(Risk.Guide), RuleView.RiskLabel(Risk.Guide, rule), LinkLast: true));
        }

        if (hotspots.FirstOrDefault(h => !h.IsRemainder) is { Bytes: > 0 } spot)
        {
            items.Add(new ListItem(spot.Node, [Redact(tree.GetPath(spot.Node)), ByteSize.Format(spot.Bytes), "", L.T("查看")], L.T("没有规则认识这里"),
                RuleView.Unknown, L.T("未识别"), LinkLast: true));
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
                items.Add(new ListItem(node, [path, ByteSize.Format(bytes), owner ?? L.T("未识别"), ""], null, RuleView.Unknown, L.T("未识别")));
                continue;
            }

            var risk = c.EffectiveRisk(node) ?? rule.Risk;
            items.Add(new ListItem(node, [path, ByteSize.Format(bytes), owner ?? Names.Display(rule.Kind), ""], null,
                RuleView.RiskStyle(risk), RuleView.RiskLabel(risk, rule)));
        }

        return items;
    }

    /// <summary>一行的标题，见 <see cref="Names.Title"/>；完整的作用放在提示里。</summary>
    private static string Title(LocationRule rule, Owner? owner) => Names.Title(rule, owner);

    /// <summary>把当前用户的目录显示成 %LOCALAPPDATA%、%USERPROFILE% 等，路径更短，截图里也不暴露用户名。</summary>
    internal static string Redact(string path) => PathDisplay.Shorten(path);

    /// <summary>一行数据：点击后跳到 <see cref="Node"/>。</summary>
    /// <param name="LinkLast">最后一列是链接（"查看""查看步骤"），画成链接色。</param>
    /// <param name="TagColumn">
    /// 标签画在这一列的文字后面（"uv.exe ← Code.exe 实测"），<see cref="Tag"/> 为空时只有文字；读屏读到的是"文字 标签"。
    /// 不设时标签画在第一个空单元格里（docs/phase2a-design.md 5.9.4 节）。
    /// </param>
    internal sealed record ListItem(int Node, string[] Cells, string? Note, RuleView.TagStyle TagStyle, string Tag, Color? ValueColor = null, bool LinkLast = false,
        int? TagColumn = null);

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
            AccessibleDescription = L.F("{0}，{1}", value, foot);
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
        private readonly int[] _fixedWidths;
        private readonly int _numericColumns;
        private readonly HashSet<int>? _rightAligned;
        private readonly int _fillColumn;
        private readonly Label _placeholder;
        private readonly LinkLabel _hintLink = new() { Dock = DockStyle.Bottom, AutoSize = false, Font = Theme.Small, ForeColor = Theme.Text, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover, UseMnemonic = false, Visible = false, Padding = new Padding(0, 4, 0, 2) };
        private List<ListItem> _items = [];

        /// <param name="fixedWidths">除了占满剩余宽度的那一列，其余各列的宽度（96 DPI，按列的顺序）；不给时按列数取默认值。</param>
        /// <param name="numericColumns">第 2 列起有几列是数字（右对齐，第 2 列可以带颜色）。</param>
        /// <param name="rightAligned">右对齐的列号（给了就不按 <paramref name="numericColumns"/> 算）。</param>
        /// <param name="fillColumn">占满剩余宽度的列（默认第一列）。</param>
        public ItemList(string title, string[] columns, int[]? fixedWidths = null, int numericColumns = 1, int[]? rightAligned = null, int fillColumn = 0)
        {
            _fixedWidths = fixedWidths ?? (columns.Length == 4 ? [80, 110, 90] : [80, 110]);
            _numericColumns = numericColumns;
            _rightAligned = rightAligned is null ? null : [.. rightAligned];
            _fillColumn = Math.Clamp(fillColumn, 0, columns.Length - 1);
            _placeholder = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Muted, Font = Theme.Ui, TextAlign = ContentAlignment.MiddleCenter, Visible = false, UseMnemonic = false, BackColor = Theme.Card };
            BackColor = Theme.Card;
            Padding = new Padding(14, 10, 14, 10);
            var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = Theme.UiBold, ForeColor = Theme.Text, UseMnemonic = false, AutoEllipsis = true };
            _heading = heading;
            var hint = new Label { Text = L.T("双击或按回车在空间浏览中查看"), Dock = DockStyle.Bottom, Height = 20, Font = Theme.Small, ForeColor = Theme.Subtle, UseMnemonic = false };
            _hintLink.LinkClicked += (_, _) => HintLinkClicked?.Invoke(this, EventArgs.Empty);
            _hintLink.Resize += (_, _) => FitHint();
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
            Controls.Add(_placeholder);
            Controls.Add(_view);
            Controls.Add(_hintLink); // 在"双击或按回车……"的上面
            Controls.Add(hint);
            Controls.Add(heading);
            AccessibleName = title;
        }

        private readonly Label _heading;

        /// <summary>列表下面的提示行（例如"近 7 天没有开过写入监控……开启 4 小时诊断"）；<paramref name="link"/> 是可以点的部分，接在文字后面。</summary>
        public void SetHint(string? text, string? link = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                _hintLink.Visible = false;
                return;
            }

            _hintLink.Text = link is null ? text : text + " " + link;
            _hintLink.LinkArea = link is null ? new LinkArea(0, 0) : new LinkArea(text.Length + 1, link.Length);
            _hintLink.AccessibleName = _hintLink.Text;
            _hintLink.Visible = true;
            FitHint();
        }

        internal string? HintText => _hintLink.Visible ? _hintLink.Text : null;

        /// <summary>点了提示行里的链接。</summary>
        public event EventHandler? HintLinkClicked;

        private void FitHint()
        {
            if (!_hintLink.Visible || _hintLink.Width <= 0)
            {
                return;
            }

            var h = TextRenderer.MeasureText(_hintLink.Text, _hintLink.Font, new Size(Math.Max(60, _hintLink.Width), 0), TextFormatFlags.WordBreak).Height + _hintLink.Padding.Vertical + 2;
            if (_hintLink.Height != h)
            {
                _hintLink.Height = h;
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Title
        {
            get => _heading.Text;
            set
            {
                _heading.Text = value;
                _view.AccessibleName = value;
            }
        }

        /// <summary>列表为空时显示的说明（为 null 时不显示）。</summary>
        public void SetPlaceholder(string? text)
        {
            _placeholder.Text = text ?? "";
            _placeholder.Visible = text is not null;
            _placeholder.BringToFront();
        }

        internal string? PlaceholderText => _placeholder.Visible ? _placeholder.Text : null;

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

                // 标签的文字给读屏软件用（绘制时画成标签）：指定了标签列时是"文字 标签"，否则写进第一个空单元格
                if (it.TagColumn is { } tc && tc > 0 && tc < row.SubItems.Count)
                {
                    row.SubItems[tc].Text = (row.SubItems[tc].Text + " " + it.Tag).Trim();
                }
                else if (it.TagColumn is null)
                {
                    var tagColumn = Array.FindIndex(it.Cells, string.IsNullOrEmpty);
                    if (tagColumn > 0 && tagColumn < row.SubItems.Count)
                    {
                        row.SubItems[tagColumn].Text = it.Tag;
                    }
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
            var fixedWidths = _fixedWidths;
            var rest = w - fixedWidths.Take(_view.Columns.Count - 1).Sum(x => Ui.Scale(_view, x)) - 2;
            var k = 0;
            for (var i = 0; i < _view.Columns.Count; i++)
            {
                if (i == _fillColumn)
                {
                    _view.Columns[i].Width = Math.Max(Ui.Scale(_view, 120), rest);
                    continue;
                }

                _view.Columns[i].Width = Ui.Scale(_view, k < fixedWidths.Length ? fixedWidths[k] : 90);
                k++;
            }
        }

        private bool RightAligned(int column) => _rightAligned is { } set ? set.Contains(column) : column >= 1 && column <= _numericColumns;

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
            var tagHeight = Ui.Scale(_view, 17);
            if (item.TagColumn is { } tc && tc == e.ColumnIndex)
            {
                // 文字在前，标签紧跟在后面；文字太长时用省略号，给标签留出位置
                var tagWidth = item.Tag.Length == 0 ? 0 : TextRenderer.MeasureText(g, item.Tag, _fonts.Small, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width
                    + Math.Max(4, tagHeight / 4) * 2 + Ui.Scale(_view, 6);
                var textRect = cell with { Width = Math.Max(0, cell.Width - tagWidth) };
                // 量宽度时不能带 EndEllipsis（没有给宽度时会按 0 宽截断，量出来几乎是 0，标签就压在文字上了）
                var drawn = text.Length == 0 ? 0 : Math.Min(textRect.Width, TextRenderer.MeasureText(g, text, _fonts.Regular, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width);
                if (text.Length > 0)
                {
                    TextRenderer.DrawText(g, text, _fonts.Regular, textRect, Theme.Text, flags);
                }

                if (item.Tag.Length > 0)
                {
                    var x = cell.X + drawn + (drawn > 0 ? Ui.Scale(_view, 6) : 0);
                    RuleView.DrawTag(g, item.Tag, _fonts.Small, new Point(x, cell.Y + (cell.Height - tagHeight) / 2), tagHeight, item.TagStyle);
                }

                return;
            }

            var isTag = item.TagColumn is null && e.ColumnIndex > 0 && string.IsNullOrEmpty(text);
            if (isTag)
            {
                RuleView.DrawTag(g, item.Tag, _fonts.Small, new Point(cell.X, cell.Y + (cell.Height - tagHeight) / 2), tagHeight, item.TagStyle);
                return;
            }

            if (e.ColumnIndex == item.Cells.Length - 1 && item.LinkLast)
            {
                TextRenderer.DrawText(g, text, _fonts.Regular, cell, Theme.Accent, flags);
                return;
            }

            var color = e.ColumnIndex == 1 && item.ValueColor is { } vc ? vc : Theme.Text;
            TextRenderer.DrawText(g, text, _fonts.Regular, cell, color, flags | (RightAligned(e.ColumnIndex) ? TextFormatFlags.Right : TextFormatFlags.Left));
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

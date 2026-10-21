using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Growth;
using DiskThinner.App.Rules;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;
using static DiskThinner.App.Pages.OverviewPage;

namespace DiskThinner.App.Pages;

/// <summary>
/// 增长分析（原型 .p-growth）：选两个快照（或"当前扫描"）对比，显示净增长、每日净增长和变化最大的目录（热点分解）。
/// 写入监控（是谁写的）在阶段 2 加入，相关的卡片先显示"未开启"。
/// </summary>
internal sealed class GrowthPage : UserControl
{
    private readonly Label _sub = Ui.Label("对比两个快照，找出哪些目录在变大", Theme.Ui, Theme.Muted);
    private readonly SegmentedControl _range = new("时间范围", "24 小时", "7 天", "30 天");
    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = "起始快照" };
    private readonly ComboBox _to = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = "结束快照" };
    private readonly Label _status = new() { AutoSize = true, Font = Theme.Ui, ForeColor = Theme.Muted, UseMnemonic = false, Margin = new Padding(0, 7, 12, 0) };
    private readonly Button _take = Ui.Button("立即创建快照");
    private readonly Button _schedule = Ui.Button("开启每日快照");
    private readonly StatCard _net = new("净增长");
    private readonly StatCard _files = new("文件数变化");
    private readonly StatCard _topOwner = new("增长最多的应用");
    private readonly StatCard _monitor = new("写入监控覆盖");
    private readonly BarChart _daily = new() { Dock = DockStyle.Fill, AccessibleName = "每日净增长" };
    private readonly ItemList _changes = new("变化最大的目录", ["目录", "变化", "当前", "归属", "写入进程"], [96, 96, 190, 92], numericColumns: 2);
    private readonly Label _message = new() { Dock = DockStyle.Top, AutoSize = false, Height = 0, Font = Theme.Ui, ForeColor = Theme.Text, BackColor = Theme.AccentWeak, Padding = new Padding(12, 8, 12, 8), UseMnemonic = false, Visible = false };
    private GrowthModel? _model;
    private ScanResult? _result;
    private Classification? _classification;
    private OwnerMap? _owners;
    private bool _suppress;
    private int _version;

    public GrowthPage()
    {
        BackColor = Theme.Background;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 24);
        AccessibleName = "增长分析";

        // ---- 标题行：时间范围 + 两个快照 ----
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        var title = Ui.Label("增长分析", new Font(Theme.UiFontName, 15F, FontStyle.Bold));
        title.Margin = new Padding(0, 0, 12, 0);
        _sub.Margin = new Padding(0, 8, 24, 0);
        _range.Margin = new Padding(0, 2, 12, 0);
        _range.SelectedIndex = 1;
        _from.Margin = new Padding(0, 3, 6, 0);
        _to.Margin = new Padding(0, 3, 0, 0);
        var arrow = Ui.Label("→", Theme.Ui, Theme.Muted);
        arrow.Margin = new Padding(0, 7, 6, 0);
        head.Controls.AddRange([title, _sub, _range, _from, arrow, _to]);

        // ---- 快照状态和操作 ----
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 6) };
        _take.Margin = _schedule.Margin = new Padding(0, 0, 8, 0);
        bar.Controls.AddRange([_status, _take, _schedule]);

        // ---- 四张卡片 ----
        var cards = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1, Height = 132, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0, 6, 0, 0) };
        foreach (var (card, i) in new[] { _net, _files, _topOwner, _monitor }.Select((c, i) => (c, i)))
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 12, 0);
            cards.Controls.Add(card, i, 0);
        }

        _monitor.Set("未开启", "写入监控在后续版本加入：现在能看到哪里变了，看不到是哪个程序写的", null);

        // ---- 每日净增长 + 写入监控覆盖时段 ----
        var charts = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 236, BackColor = Color.Transparent, Padding = new Padding(0, 12, 0, 0) };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        charts.Controls.Add(Card("每日净增长", _daily), 0, 0);
        var monitorText = Ui.Label("写入监控（ETW 诊断）还没有开启，这一版也还没有这个功能。快照对比只知道哪里变了、变了多少，不知道是谁写的；" +
            "开启后这里会显示有监控记录的时段。", Theme.Ui, Theme.Muted);
        monitorText.Dock = DockStyle.Fill; // 不自动大小，按卡片宽度换行
        monitorText.AutoSize = false;
        var diagnose = Ui.Button("开启诊断（后续版本）");
        diagnose.Enabled = false;
        diagnose.Dock = DockStyle.Bottom;
        var monitorBody = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        monitorBody.Controls.Add(monitorText);
        monitorBody.Controls.Add(diagnose);
        charts.Controls.Add(Card("写入监控覆盖时段", monitorBody), 1, 0);
        foreach (Control c in charts.Controls)
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 0, 12, 0);
        }

        // ---- 变化最大的目录 ----
        var list = new Panel { Dock = DockStyle.Top, Height = 420, BackColor = Color.Transparent, Padding = new Padding(0, 12, 12, 0) };
        _changes.Dock = DockStyle.Fill;
        list.Controls.Add(_changes);

        Controls.Add(list);
        Controls.Add(charts);
        Controls.Add(cards);
        Controls.Add(bar);
        Controls.Add(_message);
        Controls.Add(head);

        _range.SelectedIndexChanged += (_, _) => ApplyRange();
        _from.SelectedIndexChanged += (_, _) => SelectionChanged();
        _to.SelectedIndexChanged += (_, _) => SelectionChanged();
        _take.Click += (_, _) => SaveSnapshotRequested?.Invoke(this, EventArgs.Empty);
        _schedule.Click += (_, _) => ToggleScheduleRequested?.Invoke(this, EventArgs.Empty);
        _changes.Activated += (_, node) =>
        {
            if (node >= 0)
            {
                NavigateRequested?.Invoke(this, node);
            }
        };
        ShowEmpty("正在扫描…");
    }

    public event EventHandler<int>? NavigateRequested;
    public event EventHandler? SaveSnapshotRequested;
    public event EventHandler? ToggleScheduleRequested;

    /// <summary>焦点移到下面的列表时不要自动滚动页面（否则一打开页面标题和快照选择就被滚出去）。</summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    internal ItemList Changes => _changes;

    internal StatCard NetCard => _net;

    internal BarChart Daily => _daily;

    internal ComboBox FromBox => _from;

    internal ComboBox ToBox => _to;

    /// <summary>等待中的对比任务（测试用）。</summary>
    internal Task? Pending { get; private set; }

    private static Panel Card(string title, Control body)
    {
        var card = new BorderedPanel { BackColor = Theme.Card, Padding = new Padding(14, 10, 14, 10) };
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = Theme.UiBold, ForeColor = Theme.Text, UseMnemonic = false };
        body.Dock = DockStyle.Fill;
        card.Controls.Add(body);
        card.Controls.Add(heading);
        return card;
    }

    private sealed class BorderedPanel : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    private sealed record Choice(SnapshotInfo Info, bool IsCurrent, string Text)
    {
        public override string ToString() => Text;
    }

    public void ShowScanning() => ShowEmpty("正在扫描…");

    /// <summary>每日快照任务的状态（null 表示还没查询到）。</summary>
    public void SetSchedule(Shell.ScheduledSnapshot.Status? status)
    {
        _schedule.Text = status is { Registered: true } ? "关闭每日快照" : "开启每日快照";
        _schedule.AccessibleName = _schedule.Text;
        _scheduleText = status is null ? "" : status.Registered
            ? $"每日快照：已开启，每天 {status.Time ?? "定时"}{(status.Highest ? "（管理员权限，MFT 扫描）" : "（普通权限，遍历扫描）")}"
            : "每日快照：未开启";
        UpdateStatus();
    }

    private string _scheduleText = "";

    public void SetModel(GrowthModel? model, ScanResult? result, Classification? classification, OwnerMap? owners, string? error)
    {
        _model = model;
        _result = result;
        _classification = classification;
        _owners = owners;
        _take.Enabled = model is not null && result is not null && !model.CurrentSaved;
        _take.Text = model is { CurrentSaved: true } ? "当前扫描已保存为快照" : "立即创建快照";
        UpdateStatus();
        if (model is null)
        {
            ShowEmpty(error ?? "没有扫描结果。");
            return;
        }

        _suppress = true;
        _from.Items.Clear();
        _to.Items.Clear();
        var newest = model.Snapshots.Reverse().ToList();
        foreach (var s in newest)
        {
            var isCurrent = model.CurrentSaved && model.Current?.Info.Id == s.Id;
            _from.Items.Add(new Choice(s, false, Describe(s)));
            _to.Items.Add(new Choice(s, isCurrent, isCurrent ? "当前扫描（已保存）" : Describe(s)));
        }

        if (!model.CurrentSaved && model.Current is { } current)
        {
            _to.Items.Insert(0, new Choice(current.Info, true, "当前扫描"));
        }

        _suppress = false;
        if (_from.Items.Count == 0 || _to.Items.Count == 0 || model.Snapshots.Count + (model.CurrentSaved ? 0 : 1) < 2)
        {
            ShowEmpty(model.Snapshots.Count == 0
                ? "还没有快照。快照记录各个目录当时的大小，至少要两次才能对比。可以现在创建第一个快照，或者开启每日快照。"
                : "只有一个快照，还不能对比。下次扫描（距离上次超过 6 小时会自动保存）或者每日快照之后就能看到变化。");
            return;
        }

        ApplyRange();
    }

    private static string Describe(SnapshotInfo s) =>
        $"{s.TakenAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)} 快照（{(s.ScanMode == "mft" ? "MFT" : "遍历")}）";

    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_model is { } m)
        {
            parts.Add(m.Snapshots.Count == 0 ? "还没有快照" : $"共 {m.Snapshots.Count} 个快照，最早 {m.Snapshots[0].TakenAtUtc.ToLocalTime():yyyy-MM-dd}，快照库 {ByteSize.Format(m.Store.FileBytes)}");
        }

        if (_scheduleText.Length > 0)
        {
            parts.Add(_scheduleText);
        }

        _status.Text = string.Join(" · ", parts);
    }

    /// <summary>按时间范围选起点：范围起点之前最后一个快照（没有那么早的就用最早的）；终点是当前扫描。</summary>
    private void ApplyRange()
    {
        if (_model is not { } m || _from.Items.Count == 0 || _to.Items.Count == 0)
        {
            return;
        }

        var days = _range.SelectedIndex switch { 0 => 1, 2 => 30, _ => 7 };
        var end = (Choice)_to.Items[0]!;
        var earlier = m.Snapshots.Where(s => s.Id != end.Info.Id && s.TakenAtUtc < end.Info.TakenAtUtc).ToList();
        var start = earlier.Where(s => s.TakenAtUtc <= m.NowUtc.AddDays(-days)).MaxBy(s => s.TakenAtUtc) ?? earlier.MinBy(s => s.TakenAtUtc);
        _suppress = true;
        _to.SelectedIndex = 0;
        var fromIndex = start is null ? -1 : _from.Items.Cast<Choice>().ToList().FindIndex(c => c.Info.Id == start.Id);
        _from.SelectedIndex = fromIndex >= 0 ? fromIndex : Math.Min(1, _from.Items.Count - 1);
        _suppress = false;
        SelectionChanged();
    }

    private void SelectionChanged()
    {
        if (_suppress || _model is not { } m || _from.SelectedItem is not Choice from || _to.SelectedItem is not Choice to)
        {
            return;
        }

        if (!to.IsCurrent && from.Info.Id == to.Info.Id)
        {
            ShowEmpty("起点和终点是同一个快照，请换一个。");
            return;
        }

        var version = ++_version;
        var result = _result;
        var tree = result?.Tree;
        var stopAt = tree is null ? null : GrowthModel.StopAt(tree, _classification);
        var current = m.Current;
        Pending = Compute();

        async Task Compute()
        {
            try
            {
                var data = await Task.Run(() =>
                {
                    var a = m.Store.Load(from.Info.Id);

                    // "当前扫描"：用完整的扫描结果按起点快照记录过的路径重新剪一次，两边的值都是实测的
                    var b = to.IsCurrent && result is not null ? m.Store.Preview(result.Tree, result.Stats, m.NowUtc, compareWith: a)
                        : to.IsCurrent && current is not null ? current
                        : m.Store.Load(to.Info.Id);
                    var diff = SnapshotDiff.Compute(a, b);
                    var points = m.Snapshots.Select(GrowthStats.Of).ToList();
                    if (to.IsCurrent && !m.CurrentSaved && m.NowPoint is { } now)
                    {
                        points.Add(now);
                    }

                    var daily = GrowthStats.Daily(points, a.Info.TakenAtUtc, to.IsCurrent ? m.NowUtc : b.Info.TakenAtUtc, TimeZoneInfo.Local);
                    return (Diff: diff, Hot: diff.Hotspots(stopAt: stopAt), Daily: daily);
                });

                if (version == _version && !IsDisposed)
                {
                    ShowDiff(data.Diff, data.Hot, data.Daily);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                if (version == _version && !IsDisposed)
                {
                    ShowEmpty("读取快照失败：" + ex.Message);
                }
            }
        }
    }

    private void ShowEmpty(string text)
    {
        _message.Text = text;
        _message.Height = TextRenderer.MeasureText(text, _message.Font, new Size(Math.Max(300, Width - 80), 0), TextFormatFlags.WordBreak).Height + 18;
        _message.Visible = true;
        _net.Set("—", "", null);
        _files.Set("—", "", null);
        _topOwner.Set("—", "", null);
        _daily.SetData([], "没有可以对比的快照");
        _changes.SetItems([]);
        _changes.SetPlaceholder("没有可以对比的快照");
    }

    private void ShowDiff(SnapshotDiff diff, List<GrowthHotspot> hot, List<(DateOnly Day, long Change)> daily)
    {
        _message.Visible = false;
        _net.Set(BarChart.Signed(diff.NetChange), $"增加 {ByteSize.Format(diff.Increase)} · 减少 {ByteSize.Format(diff.Decrease)}（按快照记录的目录汇总）", null);
        var files = diff.FileCountChange;
        _files.Set((files > 0 ? "+" : files < 0 ? "−" : "") + Math.Abs(files).ToString("N0", CultureInfo.InvariantCulture),
            "快照只记总数，分不出新建和删除", null);

        var growing = hot.Where(h => h.Change > 0).Select(h => (Hot: h, Owner: OwnerText(h.Path))).ToList();
        var byOwner = growing.Where(x => x.Owner.Known)
            .GroupBy(x => x.Owner.Text)
            .Select(g => (Name: g.Key, Bytes: g.Sum(x => x.Hot.Change)))
            .OrderByDescending(x => x.Bytes)
            .ToList();
        if (byOwner.Count == 0)
        {
            _topOwner.Set("—", growing.Count == 0 ? "没有超过 100 MB 的增长" : "增长的位置都还没认出是谁的", null);
        }
        else
        {
            var second = byOwner.Count > 1 ? $" · 其次 {byOwner[1].Name} {BarChart.Signed(byOwner[1].Bytes)}" : "";
            _topOwner.Set(byOwner[0].Name, BarChart.Signed(byOwner[0].Bytes) + second, null);
        }

        _daily.SetData(daily.Select(d => (d.Day.ToString("MM-dd", CultureInfo.InvariantCulture), d.Change)).ToList(),
            "每天至少要有一个快照才能画出每日变化");

        var notMonitored = new RuleView.TagStyle(ColorTranslator.FromHtml("#f3f3f3"), Theme.Subtle);
        var tree = _result?.Tree;
        var items = hot.Select(h =>
        {
            var node = tree?.FindPath(h.Path) ?? -1;
            var path = PathDisplay.Shorten(h.Path) + (h.IsRemainder ? "（其余部分）" : "");
            var (owner, _) = OwnerText(h.Path);
            var note = $"增加 {ByteSize.Format(h.Increase)}，减少 {ByteSize.Format(h.Decrease)}；之前 {ByteSize.Format(h.Previous)}" + (node < 0 ? "；当前扫描里已经没有这个位置" : "");
            return new ListItem(node, [path, BarChart.Signed(h.Change), ByteSize.Format(h.Current), owner, ""], note, notMonitored, "未监控",
                h.Change > 0 ? Theme.Up : h.Change < 0 ? Theme.Down : null);
        }).ToList();
        _changes.SetItems(items);
        _changes.SetPlaceholder(items.Count == 0 ? "这段时间里没有超过 100 MB 的变化" : null);
    }

    /// <summary>归属判断：当前扫描里的归属；没有归属时用规则类别；都没有时是"未识别"。</summary>
    private (string Text, bool Known) OwnerText(string path)
    {
        var tree = _result?.Tree;
        var node = tree?.FindPath(path) ?? -1;
        if (node < 0)
        {
            return ("当前已不存在", false);
        }

        if (_owners?.OwnerOf(node) is { } owner)
        {
            return (owner.Display, true);
        }

        return _classification?.RuleOf(node) is { } rule ? (Names.Display(rule.Kind), true) : ("未识别", false);
    }
}

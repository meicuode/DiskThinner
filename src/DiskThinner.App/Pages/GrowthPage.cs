using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Growth;
using DiskThinner.App.Monitor;
using DiskThinner.App.Rules;
using DiskThinner.Core;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Etw;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;
using static DiskThinner.App.Pages.OverviewPage;

namespace DiskThinner.App.Pages;

/// <summary>
/// 增长分析（原型 .p-growth）：选两个快照（或"当前扫描"）对比，显示净增长、每日净增长和变化最大的目录（热点分解）；
/// 有写入监控数据时再显示覆盖时段、每个目录的写入进程和写入进程排行（docs/phase2a-design.md 5.9.4 节）。
/// </summary>
internal sealed class GrowthPage : UserControl
{
    private const int MaxSessionRows = 4;

    private readonly Label _sub = Ui.Label(L.T("对比两个快照，结合写入监控数据，找出谁在让磁盘变大"), Theme.Ui, Theme.Muted);
    private readonly SegmentedControl _range = new(L.T("时间范围"), L.T("24 小时"), L.T("7 天"), L.T("30 天"));
    private readonly ComboBox _from = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = L.T("起始快照") };
    private readonly ComboBox _to = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230, AccessibleName = L.T("结束快照") };
    private readonly Label _status = new() { AutoSize = true, Font = Theme.Ui, ForeColor = Theme.Muted, UseMnemonic = false, Margin = new Padding(0, 7, 12, 0) };
    private readonly Button _take = Ui.Button(L.T("立即创建快照"));
    private readonly Button _schedule = Ui.Button(L.T("开启每日快照"));
    private readonly StatCard _net = new(L.T("净增长"));
    private readonly StatCard _files = new(L.T("文件数变化"));
    private readonly StatCard _topOwner = new(L.T("增长最多的应用"));
    private readonly StatCard _monitor = new(L.T("写入监控覆盖"));
    private readonly BarChart _daily = new() { Dock = DockStyle.Fill, AccessibleName = L.T("每日净增长") };
    private readonly CoverageTimeline _timeline = new() { Dock = DockStyle.Fill, Height = 40 };
    private readonly TableLayoutPanel _sessions = new() { ColumnCount = 4, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _coverageNote = new() { Dock = DockStyle.Fill, AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false };
    private readonly Button _diagnose = Ui.Button(L.T("开启诊断"));
    private readonly Label _coverageTitle = new() { Text = L.T("写入监控覆盖时段"), Dock = DockStyle.Top, Height = 26, Font = Theme.UiBold, ForeColor = Theme.Text, UseMnemonic = false };
    private readonly ItemList _changes = new(L.T("变化最大的目录"), [L.T("目录"), L.T("变化"), L.T("当前"), L.T("归属"), L.T("写入进程")], [96, 96, 170, 260], numericColumns: 2);
    private readonly ItemList _writers = new(L.T("写入进程排行"), [L.T("进程"), L.T("产品 · 公司"), L.T("上级应用"), L.T("新建文件"), L.T("写入量"), L.T("主要目录")],
        [170, 200, 190, 80, 90], rightAligned: [3, 4], fillColumn: 5);
    private readonly Label _message = new() { Dock = DockStyle.Top, AutoSize = false, Height = 0, Font = Theme.Ui, ForeColor = Theme.Text, BackColor = Theme.AccentWeak, Padding = new Padding(12, 8, 12, 8), UseMnemonic = false, Visible = false };
    private GrowthModel? _model;
    private ScanResult? _result;
    private Classification? _classification;
    private OwnerMap? _owners;
    private bool _suppress;
    private int _version;

    // 写入监控
    private Func<DateTime, DateTime, WriteIndex>? _loadWrites;
    private bool _monitorEnabled = true;
    private MonitorStatus _live = MonitorStatus.Idle;
    private string? _liveSession;
    private bool _agentLive;
    private WriteIndex? _index;
    private string? _indexError;
    private DateTime _rangeFrom;
    private DateTime _rangeTo;
    private bool _hasDiff;

    public GrowthPage()
    {
        BackColor = Theme.Background;
        AutoScroll = true;
        Padding = new Padding(20, 16, 20, 24);
        AccessibleName = L.T("增长分析");

        // ---- 标题行：时间范围 + 两个快照 ----
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        var title = Ui.Label(L.T("增长分析"), new Font(Theme.UiFontName, 15F, FontStyle.Bold));
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

        // ---- 每日净增长 + 写入监控覆盖时段 ----
        var charts = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Height = 330, BackColor = Color.Transparent, Padding = new Padding(0, 12, 0, 0) };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.Controls.Add(Card(L.T("每日净增长"), _daily), 0, 0);
        charts.Controls.Add(CoverageCard(), 1, 0);
        foreach (Control c in charts.Controls)
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 0, 12, 0);
        }

        // ---- 变化最大的目录、写入进程排行 ----
        var list = new Panel { Dock = DockStyle.Top, Height = 420, BackColor = Color.Transparent, Padding = new Padding(0, 12, 12, 0) };
        _changes.Dock = DockStyle.Fill;
        list.Controls.Add(_changes);
        var ranking = new Panel { Dock = DockStyle.Top, Height = 380, BackColor = Color.Transparent, Padding = new Padding(0, 12, 12, 0) };
        _writers.Dock = DockStyle.Fill;
        ranking.Controls.Add(_writers);

        Controls.Add(ranking);
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
        _diagnose.Click += (_, _) =>
        {
            if (_live.State == MonitorState.Running)
            {
                StopMonitorRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StartMonitorRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _changes.Activated += (_, node) =>
        {
            if (node >= 0)
            {
                NavigateRequested?.Invoke(this, node);
            }
        };
        _writers.Activated += (_, node) =>
        {
            if (node >= 0)
            {
                NavigateRequested?.Invoke(this, node);
            }
        };
        ShowEmpty(L.T("正在扫描…"));
        RenderMonitor();
    }

    public event EventHandler<int>? NavigateRequested;
    public event EventHandler? SaveSnapshotRequested;
    public event EventHandler? ToggleScheduleRequested;

    /// <summary>点了"开启诊断"。</summary>
    public event EventHandler? StartMonitorRequested;

    /// <summary>点了"停止监控"。</summary>
    public event EventHandler? StopMonitorRequested;

    /// <summary>焦点移到下面的列表时不要自动滚动页面（否则一打开页面标题和快照选择就被滚出去）。</summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    internal ItemList Changes => _changes;

    internal ItemList Writers => _writers;

    internal StatCard NetCard => _net;

    internal StatCard MonitorCard => _monitor;

    internal CoverageTimeline Timeline => _timeline;

    internal Button DiagnoseButton => _diagnose;

    internal BarChart Daily => _daily;

    internal ComboBox FromBox => _from;

    internal ComboBox ToBox => _to;

    /// <summary>页面顶部的说明（没有可以对比的快照、读取失败时显示；测试用）。</summary>
    internal string? MessageText => _message.Visible ? _message.Text : null;

    /// <summary>覆盖时段卡片里会话表的文字（测试用）。</summary>
    internal IEnumerable<string> SessionTexts => _sessions.Controls.Cast<Control>().Select(c => c.Text);

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

    /// <summary>"写入监控覆盖时段"卡片：时间轴、最多 4 个会话、说明、开启诊断 / 停止监控。</summary>
    private Panel CoverageCard()
    {
        var card = new BorderedPanel { BackColor = Theme.Card, Padding = new Padding(14, 10, 14, 10), AccessibleName = L.T("写入监控覆盖时段") };
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Card, Margin = new Padding(0) };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _timeline.Margin = new Padding(0);
        _sessions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        _sessions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        _sessions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 13));
        _sessions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        _coverageNote.Margin = new Padding(0, 8, 0, 0);
        _diagnose.Margin = new Padding(0, 6, 0, 0);
        _diagnose.Anchor = AnchorStyles.Left;
        body.Controls.Add(_timeline, 0, 0);
        body.Controls.Add(_sessions, 0, 1);
        body.Controls.Add(_coverageNote, 0, 2);
        body.Controls.Add(_diagnose, 0, 3);
        card.Controls.Add(body);
        card.Controls.Add(_coverageTitle);
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

    public void ShowScanning() => ShowEmpty(L.T("正在扫描…"));

    /// <summary>
    /// 写入监控的数据来源（<paramref name="load"/> 在后台线程上调用，读 [起点, 终点) 的汇总；为 null 表示没有写入监控）。
    /// <paramref name="enabled"/> 为 false（设置为"关闭"）时不显示"开启诊断"。
    /// </summary>
    public void SetMonitor(Func<DateTime, DateTime, WriteIndex>? load, bool enabled)
    {
        var reload = !ReferenceEquals(load, _loadWrites);
        _loadWrites = load;
        _monitorEnabled = enabled;
        RenderMonitor();
        if (reload)
        {
            RefreshWrites();
        }
    }

    /// <summary>监控状态（2 秒一次）：只更新覆盖卡片的脚注、进行中的那一行和按钮，不重新查询。</summary>
    public void SetLive(MonitorStatus status, string? liveSession, bool agentLive = false)
    {
        var changed = status.State != _live.State || liveSession != _liveSession || agentLive != _agentLive;
        _live = status;
        _liveSession = liveSession;
        _agentLive = agentLive;
        if (changed || status.State == MonitorState.Running)
        {
            RenderMonitor();
        }
    }

    /// <summary>monitor.db 变了（落盘、会话结束）：按当前选择重新对比和查询。</summary>
    public void RefreshWrites()
    {
        if (_hasDiff)
        {
            SelectionChanged();
        }
    }

    /// <summary>每日快照任务的状态（null 表示还没查询到）。</summary>
    public void SetSchedule(Shell.ScheduledSnapshot.Status? status)
    {
        _schedule.Text = status is { Registered: true } ? L.T("关闭每日快照") : L.T("开启每日快照");
        _schedule.AccessibleName = _schedule.Text;
        _scheduleText = status is null ? "" : status.Registered
            ? L.F("每日快照：已开启，每天 {0}{1}", status.Time ?? L.T("定时"), (status.Highest ? L.T("（管理员权限，MFT 扫描）") : L.T("（普通权限，遍历扫描）")))
            : L.T("每日快照：未开启");
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
        _take.Text = model is { CurrentSaved: true } ? L.T("当前扫描已保存为快照") : L.T("立即创建快照");
        UpdateStatus();
        if (model is null)
        {
            ShowEmpty(error ?? L.T("没有扫描结果。"));
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
            _to.Items.Add(new Choice(s, isCurrent, isCurrent ? L.T("当前扫描（已保存）") : Describe(s)));
        }

        if (!model.CurrentSaved && model.Current is { } current)
        {
            _to.Items.Insert(0, new Choice(current.Info, true, L.T("当前扫描")));
        }

        _suppress = false;
        if (_from.Items.Count == 0 || _to.Items.Count == 0 || model.Snapshots.Count + (model.CurrentSaved ? 0 : 1) < 2)
        {
            ShowEmpty(model.Snapshots.Count == 0
                ? L.T("还没有快照。快照记录各个目录当时的大小，至少要两次才能对比。可以现在创建第一个快照，或者开启每日快照。")
                : L.T("只有一个快照，还不能对比。下次扫描（距离上次超过 6 小时会自动保存）或者每日快照之后就能看到变化。"));
            return;
        }

        ApplyRange();
    }

    private static string Describe(SnapshotInfo s) =>
        L.F("{0} 快照（{1}）", s.TakenAtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), (s.ScanMode == "mft" ? "MFT" : L.T("遍历")));

    private void UpdateStatus()
    {
        var parts = new List<string>();
        if (_model is { } m)
        {
            parts.Add(m.Snapshots.Count == 0 ? L.T("还没有快照") : L.F("共 {0} 个快照，最早 {1:yyyy-MM-dd}，快照库 {2}", m.Snapshots.Count, m.Snapshots[0].TakenAtUtc.ToLocalTime(), ByteSize.Format(m.Store.FileBytes)));
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
            ShowEmpty(L.T("起点和终点是同一个快照，请换一个。"));
            return;
        }

        var version = ++_version;
        var result = _result;
        var tree = result?.Tree;
        var stopAt = tree is null ? null : GrowthModel.StopAt(tree, _classification);
        var current = m.Current;
        var load = _loadWrites;
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

                    // 写入监控：比较范围是 [起点快照时间, 终点快照时间或现在]
                    var rangeFrom = a.Info.TakenAtUtc;
                    var rangeTo = to.IsCurrent ? DateTime.UtcNow : b.Info.TakenAtUtc;
                    WriteIndex? index = null;
                    string? indexError = null;
                    if (load is not null)
                    {
                        try
                        {
                            index = load(rangeFrom, rangeTo);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
                        {
                            indexError = ex.Message;
                        }
                    }

                    return (Diff: diff, Hot: diff.Hotspots(stopAt: stopAt), Daily: daily, Index: index, IndexError: indexError, From: rangeFrom, To: rangeTo);
                });

                if (version == _version && !IsDisposed)
                {
                    (_index, _indexError, _rangeFrom, _rangeTo) = (data.Index, data.IndexError, data.From, data.To);
                    ShowDiff(data.Diff, data.Hot, data.Daily);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
            {
                if (version == _version && !IsDisposed)
                {
                    ShowEmpty(L.F("读取快照失败：{0}", ex.Message));
                }
            }
        }
    }

    private void ShowEmpty(string text)
    {
        _hasDiff = false;
        _message.Text = text;
        _message.Height = TextRenderer.MeasureText(text, _message.Font, new Size(Math.Max(300, Width - 80), 0), TextFormatFlags.WordBreak).Height + 18;
        _message.Visible = true;
        _net.Set("—", "", null);
        _files.Set("—", "", null);
        _topOwner.Set("—", "", null);
        _daily.SetData([], L.T("没有可以对比的快照"));
        _changes.SetItems([]);
        _changes.SetPlaceholder(L.T("没有可以对比的快照"));
        _writers.SetItems([]);
        _writers.SetPlaceholder(L.T("没有可以对比的快照"));
        _index = null;
        _indexError = null;
        RenderMonitor();
    }

    private double CoveredHours => _index is { } index ? MonitorText.CoveredHours(index.Sessions, _rangeFrom, _rangeTo, _liveSession, agentLive: _agentLive) : 0;

    private double RangeHours => Math.Max(0, (_rangeTo - _rangeFrom).TotalHours);

    private void ShowDiff(SnapshotDiff diff, List<GrowthHotspot> hot, List<(DateOnly Day, long Change)> daily)
    {
        _hasDiff = true;
        _message.Visible = false;
        _net.Set(BarChart.Signed(diff.NetChange), L.F("增加 {0} · 减少 {1}（按快照记录的目录汇总）", ByteSize.Format(diff.Increase), ByteSize.Format(diff.Decrease)), null);
        var files = diff.FileCountChange;
        _files.Set((files > 0 ? "+" : files < 0 ? "−" : "") + Math.Abs(files).ToString("N0", CultureInfo.InvariantCulture),
            L.T("快照只记总数，分不出新建和删除"), null);

        var covered = CoveredHours;
        var index = covered > 0 ? _index : null;
        var writers = index?.Writers() ?? [];
        var growing = hot.Where(h => h.Change > 0).Select(h => (Hot: h, Owner: OwnerText(h.Path))).ToList();
        var byOwner = growing.Where(x => x.Owner.Known)
            .GroupBy(x => x.Owner.Text)
            .Select(g => (Name: g.Key, Bytes: g.Sum(x => x.Hot.Change)))
            .OrderByDescending(x => x.Bytes)
            .ToList();
        var topWriter = writers.FirstOrDefault() is { } w
            ? L.F("监控时段写入最多：{0} {1}", WriterName(w), index!.PreciseHours > 0 ? ByteSize.Format(w.Counters.WriteBytes) : L.F("新建 {0} 个文件", w.Counters.Creates.ToString("N0", CultureInfo.InvariantCulture)))
            : null;
        if (byOwner.Count == 0)
        {
            _topOwner.Set("—", topWriter ?? (growing.Count == 0 ? L.T("没有超过 100 MB 的增长") : L.T("增长的位置都还没认出是谁的")), null);
        }
        else
        {
            var second = byOwner.Count > 1 ? L.F(" · 其次 {0} {1}", byOwner[1].Name, BarChart.Signed(byOwner[1].Bytes)) : "";
            _topOwner.Set(byOwner[0].Name, BarChart.Signed(byOwner[0].Bytes) + second + (topWriter is null ? "" : "\n" + topWriter), null);
        }

        _daily.SetData(daily.Select(d => (d.Day.ToString("MM-dd", CultureInfo.InvariantCulture), d.Change)).ToList(),
            L.T("每天至少要有一个快照才能画出每日变化"));

        var tree = _result?.Tree;
        var items = hot.Select(h =>
        {
            var node = tree?.FindPath(h.Path) ?? -1;
            var path = PathDisplay.Shorten(h.Path) + (h.IsRemainder ? L.T("（其余部分）") : "");
            var (owner, _) = OwnerText(h.Path);
            var note = L.F("增加 {0}，减少 {1}；之前 {2}{3}", ByteSize.Format(h.Increase), ByteSize.Format(h.Decrease), ByteSize.Format(h.Previous), (node < 0 ? L.T("；当前扫描里已经没有这个位置") : ""));
            var color = h.Change > 0 ? Theme.Up : h.Change < 0 ? Theme.Down : (Color?)null;
            var vmDisk = node >= 0 && _classification?.RuleOf(node)?.Kind == Kind.VmDisk;
            var cell = MonitorText.Cell(index, covered, RangeHours, h.Path, h.Change, !h.IsDirectory, vmDisk, remainder: h.IsRemainder);
            return cell switch
            {
                null => new ListItem(node, [path, BarChart.Signed(h.Change), ByteSize.Format(h.Current), owner, ""], note, RuleView.NotMonitored, L.T("未监控"), color, TagColumn: 4),
                { Observed: true } => new ListItem(node, [path, BarChart.Signed(h.Change), ByteSize.Format(h.Current), owner, cell.Text], note + "\n" + cell.Note,
                    RuleView.ObservedSource, cell.Tag, color, TagColumn: 4),
                _ => new ListItem(node, [path, BarChart.Signed(h.Change), ByteSize.Format(h.Current), owner, cell.Text], cell.Note is null ? note : note + "\n" + cell.Note,
                    RuleView.ObservedSource, "", color, TagColumn: 4),
            };
        }).ToList();
        _changes.SetItems(items);
        _changes.SetPlaceholder(items.Count == 0 ? L.T("这段时间里没有超过 100 MB 的变化") : null);
        ShowWriters(index, writers, covered);
        RenderMonitor();
    }

    /// <summary>写入进程排行（第 5.9.4 节）：精确按写入量排，范围内只有轻量数据时按新建文件排，写入量列为"—"；双击跳到主要目录。</summary>
    private void ShowWriters(WriteIndex? index, IReadOnlyList<WriterRank> writers, double covered)
    {
        if (index is null)
        {
            _writers.Title = L.T("写入进程排行");
            _writers.SetItems([]);
            _writers.SetPlaceholder(_indexError is not null ? L.F("写入监控数据读不了：{0}", _indexError) : L.T("这段时间里没有写入监控记录"));
            return;
        }

        var light = Math.Max(0, index.MonitoredHours - index.PreciseHours);
        var span = covered < 1 ? L.F("{0} 分钟", (int)Math.Floor(covered * 60)) : L.F("{0} 小时", MonitorText.Hours(covered));
        _writers.Title = L.F("写入进程排行 · 仅统计有监控的 {0}", span) + (light > 0 ? L.F(" · 轻量模式的 {0} 小时没有写入量", light) : "");
        var precise = index.PreciseHours > 0;
        var tree = _result?.Tree;
        var items = writers.Select(w =>
        {
            var dirs = w.MainDirs;
            var mainDir = dirs.Count == 0 ? "—"
                : w.Kind == ChainKind.System && dirs.Count > 1 ? L.T("多个目录")
                : string.Join(L.T("、"), dirs.Select(d => d is null ? L.T("对不上目录（监控前已打开的文件）") : PathDisplay.Shorten(d)));
            var node = dirs.FirstOrDefault(d => d is not null) is { } first ? tree?.FindPath(first) ?? -1 : -1;
            var writeBytes = !precise ? "—" : (w.Counters.Sampled || index.LostRate > 0.01 ? "≈ " : "") + ByteSize.Format(w.Counters.WriteBytes);
            return new ListItem(node, [WriterName(w), Product(w), TopApp(w), w.Counters.Creates.ToString("N0", CultureInfo.InvariantCulture), writeBytes, mainDir],
                w.Writer is { } img ? PathDisplay.Shorten(img.Path) : null, RuleView.ObservedSource, "");
        }).ToList();

        // 常驻服务给普通用户的查询里，其他用户个人目录下的写入合成一行，只有计数没有路径（Q7）
        if (index?.HiddenOtherUsers is { } hidden)
        {
            var bytes = !precise ? "—" : ByteSize.Format(hidden.WriteBytes);
            items.Add(new ListItem(-1, [L.T("其他用户的目录（需要管理员）"), "—", "—", hidden.Creates.ToString("N0", CultureInfo.InvariantCulture), bytes, "—"],
                L.T("其他用户个人目录下的写入，以管理员身份打开 DiskThinner 才能看到是谁写的、写在哪里"), RuleView.ObservedSource, ""));
        }

        _writers.SetItems(items);
        _writers.SetPlaceholder(items.Count == 0 ? L.F("监控的 {0} 小时里没有记录到写入", MonitorText.Hours(covered)) : null);
    }

    private static string WriterName(WriterRank w) => w.Kind switch
    {
        ChainKind.System => MonitorText.SystemWriter,
        ChainKind.Unknown => L.T("未知进程"),
        _ => w.Writer?.Path == DiskThinner.Monitor.Agent.AgentProtocol.OtherUserImage ? MonitorText.OtherUserProgram : w.Writer?.ExeName ?? "—",
    };

    /// <summary>"产品 · 公司"；服务宿主写"服务 wuauserv · Microsoft"。</summary>
    private static string Product(WriterRank w)
    {
        if (w.Kind != ChainKind.Normal || w.Writer is not { } img)
        {
            return "—";
        }

        var first = img.Services.Length > 0 ? MonitorText.Services(img.Services) : img.Product;
        var parts = new[] { first, img.Company }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    /// <summary>上级应用：写得最多的那条链的顶层应用；顶层就是自己时写"—"。</summary>
    private static string TopApp(WriterRank w)
    {
        if (w.Kind != ChainKind.Normal || w.Top is not { } top || w.Writer is null || string.Equals(top.Path, w.Writer.Path, StringComparison.OrdinalIgnoreCase))
        {
            return "—";
        }

        var name = top.Services.Length > 0 ? MonitorText.Image(top) : top.ExeName;
        return string.IsNullOrWhiteSpace(top.Product) ? name : L.F("{0}（{1}）", name, top.Product);
    }

    /// <summary>"写入监控覆盖"卡片和"写入监控覆盖时段"卡片（时间轴、会话、说明、按钮）。</summary>
    private void RenderMonitor()
    {
        var running = _live.State == MonitorState.Running;
        _diagnose.Text = running ? L.T("停止监控") : L.T("开启诊断");
        _diagnose.AccessibleName = _diagnose.Text;
        _diagnose.Visible = running || (_monitorEnabled && !_agentLive); // 常驻服务在记录时不再开诊断（E12）
        var rangeHours = RangeHours;
        var covered = CoveredHours;

        // ---- 覆盖卡片 ----
        if (_loadWrites is null)
        {
            _monitor.Set(L.T("未开启"), L.T("写入监控不可用"), null);
        }
        else if (_indexError is not null)
        {
            _monitor.Set("—", L.F("写入监控数据读不了：{0}", _indexError), null);
        }
        else if (!_hasDiff)
        {
            _monitor.Set("—", running ? L.F("诊断中，剩余 {0}", MonitorText.Remaining(_live.Remaining)) : "", null);
        }
        else
        {
            var foot = running ? L.F("诊断中，剩余 {0}", MonitorText.Remaining(_live.Remaining))
                : _agentLive ? L.T("常驻服务记录中 · ") + MonitorText.Lost(_index?.LostRate ?? 0)
                : covered > 0 ? MonitorText.Lost(_index?.LostRate ?? 0) + (_index?.LostRate > 0.01 ? L.T("，部分数据不全") : "")
                : _monitorEnabled ? L.T("这段时间没有监控，可以开启诊断") : L.T("写入监控已在设置里关闭");
            _monitor.Set(MonitorText.Coverage(covered, rangeHours), foot, null);
        }

        // ---- 覆盖时段卡片 ----
        var sessions = _index?.Sessions ?? [];
        _coverageTitle.Text = _hasDiff && _index is not null
            ? L.F("写入监控覆盖时段（{0}）", MonitorText.Coverage(covered, rangeHours))
            : L.T("写入监控覆盖时段");
        var merged = MonitorText.Spans(sessions, _liveSession, running, _agentLive, DateTime.UtcNow);
        var spans = merged.Select(s => (s.StartUtc, s.EndUtc, Running: s.Live)).ToList();
        _timeline.SetData(_hasDiff ? _rangeFrom : DateTime.UtcNow.AddDays(-7), _hasDiff ? _rangeTo : DateTime.UtcNow, spans);

        _sessions.SuspendLayout();
        foreach (Control c in _sessions.Controls)
        {
            c.Dispose();
        }

        _sessions.Controls.Clear();
        _sessions.RowStyles.Clear();
        _sessions.RowCount = 0;
        var shown = merged.TakeLast(MaxSessionRows).ToList();
        foreach (var s in shown)
        {
            var live = s.Live;
            var start = s.StartUtc.ToLocalTime();
            var endUtc = s.EndUtc;
            var end = endUtc.ToLocalTime();
            var when = live ? L.F("{0} – 进行中", start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture))
                : start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " – " + end.ToString(start.Date == end.Date ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
            var what = s.Source == SessionSource.Agent ? L.F("常驻 · {0}模式", MonitorText.Mode(s.Mode)) : L.F("诊断 · {0}模式", MonitorText.Mode(s.Mode));
            if (s.Downgraded)
            {
                what += L.T(" · 已降为轻量");
            }

            if (live && s.Source == SessionSource.Diagnostic)
            {
                what += L.F(" · 剩余 {0}", MonitorText.Remaining(_live.Remaining));
            }
            else if (!live && s.EndReason is "crashed" or "external" or "error" or "store-error")
            {
                what += " · " + MonitorText.EndReason(s.EndReason);
            }

            var lost = s.LostRate;
            var row = _sessions.RowCount++;
            _sessions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _sessions.Controls.Add(SessionCell(when, false, Theme.Text), 0, row);
            _sessions.Controls.Add(SessionCell(what, false, Theme.Text), 1, row);
            _sessions.Controls.Add(SessionCell(MonitorText.Duration(endUtc - s.StartUtc), true, Theme.Text), 2, row);
            _sessions.Controls.Add(SessionCell(MonitorText.Lost(lost), true, lost > 0.01 ? Theme.WarnText : Theme.Muted), 3, row);
        }

        if (merged.Count > shown.Count)
        {
            var row = _sessions.RowCount++;
            _sessions.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var more = SessionCell(L.F("另有 {0} 段", merged.Count - shown.Count), false, Theme.Muted);
            _sessions.Controls.Add(more, 0, row);
            _sessions.SetColumnSpan(more, 4);
        }

        _sessions.ResumeLayout();
        _coverageNote.Text = _indexError is not null ? L.F("写入监控数据读不了：{0}", _indexError)
            : _loadWrites is null ? L.T("写入监控不可用。")
            : !_monitorEnabled && !running ? L.T("写入监控已在设置里关闭。快照对比只知道哪里变了、变了多少，不知道是谁写的。")
            : sessions.Count == 0 ? L.T("这段时间里没有写入监控记录。快照对比只知道哪里变了、变了多少，不知道是谁写的；开启诊断后，这里会显示有写入进程记录的时段。")
            : (_agentLive ? L.T("常驻服务正在记录。") : "") + L.T("蓝色时段有写入进程记录。其余时段只有快照对比，只知道哪里变了，不知道是谁写的。");
    }

    private static Label SessionCell(string text, bool right, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = right ? AnchorStyles.Right : AnchorStyles.Left,
        Font = Theme.Small,
        ForeColor = color,
        UseMnemonic = false,
        Margin = new Padding(0, 3, 4, 3),
        AutoEllipsis = true,
    };

    /// <summary>归属判断：当前扫描里的归属；没有归属时用规则类别；都没有时是"未识别"。</summary>
    private (string Text, bool Known) OwnerText(string path)
    {
        var tree = _result?.Tree;
        var node = tree?.FindPath(path) ?? -1;
        if (node < 0)
        {
            return (L.T("当前已不存在"), false);
        }

        if (_owners?.OwnerOf(node) is { } owner)
        {
            return (owner.Display, true);
        }

        return _classification?.RuleOf(node) is { } rule ? (Names.Display(rule.Kind), true) : (L.T("未识别"), false);
    }
}

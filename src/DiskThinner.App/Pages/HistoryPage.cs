using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.Storage;

namespace DiskThinner.App.Pages;

/// <summary>
/// 操作记录（原型 .p-quarantine）：四张统计卡、类型筛选、只读列表、导出 CSV。
/// 记录来自 user.db 的 audit_log，只追加不修改；这个页面也只读。
/// </summary>
internal sealed class HistoryPage : UserControl
{
    private static readonly AuditAction?[] Filters = [null, AuditAction.Delete, AuditAction.Command, AuditAction.Manual, AuditAction.Blocked];

    private readonly SegmentedControl _filter = new("筛选", "全部", "删除", "运行命令", "手动清理", "拦截");
    private readonly Button _export = Ui.Button("导出");
    private readonly OverviewPage.StatCard _freed = new("累计释放");
    private readonly OverviewPage.StatCard _last = new("最近一次");
    private readonly OverviewPage.StatCard _blocked = new("被拦截");
    private readonly OverviewPage.StatCard _ai = new("AI 建议被采纳");
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        BorderStyle = BorderStyle.None,
        ShowItemToolTips = true,
        AccessibleName = "操作记录",
        Font = Theme.Ui,
    };

    private readonly Label _empty = Ui.Label("还没有操作记录。执行清理、手动清理后重新测量、被拦截的操作都会记在这里。", Theme.Ui, Theme.Muted);
    private AuditLog? _log;
    private List<AuditEntry> _entries = [];

    public HistoryPage()
    {
        BackColor = Theme.Background;
        Padding = new Padding(20, 16, 20, 16);
        AccessibleName = "操作记录";

        var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(0, 0, 0, 12) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = Ui.Label("操作记录", new Font(Theme.UiFontName, 15F, FontStyle.Bold));
        title.Margin = new Padding(0, 0, 12, 0);
        var sub = Ui.Label("每次删除、运行命令、手动清理、拦截都会记录，只追加不修改", Theme.Ui, Theme.Muted);
        sub.Margin = new Padding(0, 8, 0, 0);
        _filter.Margin = new Padding(0, 3, 8, 0);
        _export.Margin = new Padding(0, 2, 0, 0);
        head.Controls.Add(title, 0, 0);
        head.Controls.Add(sub, 1, 0);
        head.Controls.Add(_filter, 2, 0);
        head.Controls.Add(_export, 3, 0);

        var cards = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, RowCount = 1, Height = 120, BackColor = Color.Transparent };
        foreach (var (card, i) in new[] { _freed, _last, _blocked, _ai }.Select((c, i) => (c, i)))
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 12, 0);
            cards.Controls.Add(card, i, 0);
        }

        _list.Columns.Add("时间", 110);
        _list.Columns.Add("操作", 80);
        _list.Columns.Add("对象", 380);
        _list.Columns.Add("大小", 90, HorizontalAlignment.Right);
        _list.Columns.Add("依据", 300);
        _list.Columns.Add("结果", 220);
        var listCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(1) };
        _empty.Dock = DockStyle.Fill;
        _empty.AutoSize = false;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        listCard.Controls.Add(_list);
        listCard.Controls.Add(_empty);
        var gap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Color.Transparent };

        Controls.Add(listCard);
        Controls.Add(gap);
        Controls.Add(cards);
        Controls.Add(head);

        _filter.SelectedIndexChanged += (_, _) => Fill();
        _export.Click += (_, _) => Export();
        _list.ItemActivate += (_, _) =>
        {
            if (_list.SelectedItems.Count == 1 && _list.SelectedItems[0].Tag is AuditEntry e)
            {
                MessageBox.Show(FindForm(), Describe(e), "操作记录", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _ai.Set("0 项", "AI 分析在后续版本提供", null);
        Reload();
    }

    internal ListView List => _list;

    internal OverviewPage.StatCard FreedCard => _freed;

    public void SetLog(AuditLog? log, string? error)
    {
        _log = log;
        if (error is not null)
        {
            _empty.Text = "操作记录打不开：" + error;
        }

        Reload();
    }

    public void Reload()
    {
        try
        {
            _entries = _log?.List() ?? [];
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _entries = [];
            _empty.Text = "操作记录打不开：" + ex.Message;
        }

        UpdateCards();
        Fill();
    }

    private void UpdateCards()
    {
        var done = _entries.Where(e => e.Action is AuditAction.Delete or AuditAction.Command or AuditAction.Manual && e.FreedBytes is not null).ToList();
        var runs = done.Select(e => e.RunId ?? "m" + e.Id).Distinct().Count();
        _freed.Set(ByteSize.Format(done.Sum(e => e.FreedBytes ?? 0)), runs == 0 ? "还没有清理过" : $"共 {runs} 次清理", null);

        if (done.Count == 0)
        {
            _last.Set("—", "", null);
        }
        else
        {
            var latest = done.MaxBy(e => e.AtUtc)!;
            var same = done.Where(e => e.RunId is not null ? e.RunId == latest.RunId : e.Id == latest.Id);
            _last.Set(latest.AtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture), $"释放 {ByteSize.Format(same.Sum(e => e.FreedBytes ?? 0))}", null);
        }

        var blocked = _entries.Where(e => e.Action == AuditAction.Blocked).ToList();
        var top = blocked.GroupBy(e => e.Basis.StartsWith("保护名单", StringComparison.Ordinal) ? "保护名单" : "执行前复核").MaxBy(g => g.Count());
        _blocked.Set($"{blocked.Count} 次", top is null ? "" : $"大多是{top.Key}", null);
    }

    private void Fill()
    {
        var filter = Filters[Math.Clamp(_filter.SelectedIndex, 0, Filters.Length - 1)];
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _entries.Where(x => filter is null || x.Action == filter))
        {
            var result = AuditLog.Display(e.Result) + (e.FreedBytes is { } f && e.Action != AuditAction.Blocked ? $"，释放 {ByteSize.Format(f)}" : "");
            var item = new ListViewItem([
                e.AtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                AuditLog.Display(e.Action),
                e.Action == AuditAction.Command ? e.Target : PathDisplay.Shorten(e.Target),
                e.Bytes > 0 ? ByteSize.Format(e.Bytes) : "",
                e.Basis,
                result,
            ])
            {
                Tag = e,
                ToolTipText = e.Detail ?? e.Title ?? "",
                ForeColor = e.Result is AuditResult.Failed or AuditResult.NotRun ? Theme.WarnText : Theme.Text,
            };
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        _list.Visible = _list.Items.Count > 0;
        _empty.Visible = !_list.Visible;
        _export.Enabled = _entries.Count > 0;
    }

    private static string Describe(AuditEntry e) =>
        $"{e.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}　{AuditLog.Display(e.Action)}　{AuditLog.Display(e.Result)}\n\n" +
        $"{e.Title}\n{e.Target}\n\n依据：{e.Basis}\n" +
        (e.Bytes > 0 ? $"大小：{ByteSize.Format(e.Bytes)}\n" : "") +
        (e.FreedBytes is { } f ? $"实际释放：{ByteSize.Format(f)}\n" : "") +
        (string.IsNullOrEmpty(e.Detail) ? "" : "\n" + e.Detail);

    private void Export()
    {
        using var dlg = new SaveFileDialog
        {
            Title = "导出操作记录",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"DiskThinner-操作记录-{DateTime.Now:yyyyMMdd}.csv",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        try
        {
            AuditLog.ExportCsv(_entries, dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(FindForm(), "导出失败：" + ex.Message, "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDpi();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyDpi();
    }

    private void ApplyDpi()
    {
        int[] widths = [110, 80, 380, 90, 300, 220];
        for (var i = 0; i < widths.Length && i < _list.Columns.Count; i++)
        {
            _list.Columns[i].Width = Ui.Scale(this, widths[i]);
        }
    }
}

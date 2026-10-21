using System.ComponentModel;
using DiskThinner.App.Controls;
using DiskThinner.App.Rules;
using DiskThinner.Cleanup;
using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.App.Pages;

/// <summary>
/// 清理计划（原型 .p-cleanup）：按风险分组的清单，每组有默认处理方式，可以逐项修改（包括"手动清理"）；
/// 已拦截的项目显示但不能勾选。底部是已选和预计释放，"执行清理"先弹出确认框。
/// </summary>
internal sealed class CleanupPage : UserControl
{
    private const int ColSelect = 0, ColTitle = 1, ColPath = 2, ColSize = 3, ColSource = 4, ColMethod = 5, ColHint = 6, ColAction = 7;

    private readonly DataGridView _grid = new();
    private readonly Label _sub = Ui.Label("按风险分组，每组有默认的处理方式，可以逐项修改", Theme.Ui, Theme.Muted);
    private readonly Label _banner = new() { Dock = DockStyle.Top, AutoSize = false, Height = 0, Visible = false, Font = Theme.Ui, ForeColor = Theme.WarnText, BackColor = Theme.WarnBack, Padding = new Padding(12, 8, 12, 8), UseMnemonic = false };
    private readonly Label _summary = Ui.Label("", Theme.UiBold);
    private readonly Label _note = Ui.Label("删除是永久的，不经过回收站（C 盘的回收站不释放空间）。每一项都会写入操作记录。", Theme.Small, Theme.Muted);
    private readonly Button _clear = Ui.Button("清空计划", accessibleDescription: "取消勾选全部项目");
    private readonly Button _export = Ui.Button("导出清单");
    private readonly Button _run = Ui.Button("执行清理", primary: true, accessibleDescription: "先显示确认框，确认后才会删除");
    private readonly Label _empty = Ui.Label("正在扫描…", Theme.Ui, Theme.Muted);
    private readonly HashSet<PlanItem> _opened = [];
    private CleanupPlan _plan = CleanupPlan.Empty;
    private bool _binding;

    public CleanupPage()
    {
        BackColor = Theme.Background;
        Padding = new Padding(20, 16, 20, 16);
        AccessibleName = "清理计划";

        var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0, 0, 0, 12) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = Ui.Label("清理计划", new Font(Theme.UiFontName, 15F, FontStyle.Bold));
        title.Margin = new Padding(0, 0, 12, 0);
        _sub.Margin = new Padding(0, 8, 0, 0);
        _clear.Margin = new Padding(0, 2, 0, 0);
        head.Controls.Add(title, 0, 0);
        head.Controls.Add(_sub, 1, 0);
        head.Controls.Add(_clear, 2, 0);

        ConfigureGrid();
        var card = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(1) };
        card.Controls.Add(_grid);
        _empty.Dock = DockStyle.Fill;
        _empty.AutoSize = false;
        _empty.TextAlign = ContentAlignment.MiddleCenter;
        _empty.BackColor = Theme.Card;
        card.Controls.Add(_empty);

        var bar = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 2, RowCount = 1, BackColor = Theme.Card, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0), Dock = DockStyle.Fill };
        _note.Margin = new Padding(0, 4, 0, 0);
        text.Controls.Add(_summary);
        text.Controls.Add(_note);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0), Anchor = AnchorStyles.Right };
        _export.Margin = new Padding(0, 0, 8, 0);
        _run.Margin = new Padding(0);
        buttons.Controls.Add(_export);
        buttons.Controls.Add(_run);
        bar.Controls.Add(text, 0, 0);
        bar.Controls.Add(buttons, 1, 0);
        var gap = new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Color.Transparent };

        Controls.Add(card);
        Controls.Add(gap);
        Controls.Add(bar);
        Controls.Add(_banner);
        Controls.Add(head);

        _clear.Click += (_, _) => SelectAll(false);
        _export.Click += (_, _) => ExportRequested?.Invoke(this, EventArgs.Empty);
        _run.Click += (_, _) => ExecuteRequested?.Invoke(this, EventArgs.Empty);
        ShowScanning();
    }

    /// <summary>点"执行清理"（由主窗口弹出确认框）。</summary>
    public event EventHandler? ExecuteRequested;

    public event EventHandler? ExportRequested;

    /// <summary>在空间浏览中查看这一项。</summary>
    public event EventHandler<int>? NavigateRequested;

    /// <summary>手动清理：用资源管理器打开。</summary>
    public event EventHandler<PlanItem>? OpenRequested;

    /// <summary>手动清理后重新测量。</summary>
    public event EventHandler<PlanItem>? MeasureRequested;

    public CleanupPlan Plan => _plan;

    internal DataGridView Grid => _grid;

    internal Button RunButton => _run;

    internal string SummaryText => _summary.Text;

    public void ShowScanning()
    {
        _plan = CleanupPlan.Empty;
        _grid.Rows.Clear();
        _grid.Visible = false;
        _empty.Text = "正在扫描…";
        _empty.Visible = true;
        SetBanner(null);
        UpdateSummary();
    }

    public void SetPlan(CleanupPlan plan, string? message)
    {
        _plan = plan;
        _opened.Clear();
        _binding = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear();
        foreach (var group in plan.Items.GroupBy(i => i.Group))
        {
            AddGroupRow(group.Key, group.ToList());
            foreach (var item in group)
            {
                AddItemRow(item);
            }
        }

        _grid.ResumeLayout();
        _binding = false;
        _grid.Visible = plan.Items.Count > 0;
        _empty.Visible = plan.Items.Count == 0;
        _empty.Text = message ?? "没有可以清理的位置：规则认识的位置都不到 1 MB，或者都受保护。";
        SetBanner(plan.Warning);
        UpdateSummary();
    }

    /// <summary>执行或重新测量之后刷新各行的状态和底部汇总。</summary>
    public void RefreshItems(string? banner = null)
    {
        _binding = true;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is PlanItem item)
            {
                FillItemRow(row, item);
            }
        }

        _binding = false;
        if (banner is not null)
        {
            SetBanner(banner);
        }

        UpdateSummary();
        _grid.Invalidate();
    }

    /// <summary>选中并滚动到这一项（从空间浏览"加入清理计划"过来）。</summary>
    public void Reveal(PlanItem item)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (ReferenceEquals(row.Tag, item))
            {
                _grid.ClearSelection();
                row.Selected = true;
                _grid.CurrentCell = row.Cells[ColTitle];
                _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, row.Index - 3);
                break;
            }
        }

        RefreshItems();
    }

    private void SetBanner(string? text)
    {
        _banner.Text = text ?? "";
        _banner.Visible = text is not null;
        _banner.Height = text is null ? 0 : TextRenderer.MeasureText(text, _banner.Font, new Size(Math.Max(200, Width - Padding.Horizontal - 24), 0), TextFormatFlags.WordBreak).Height + _banner.Padding.Vertical;
    }

    private void UpdateSummary()
    {
        var run = _plan.ToRun.ToList();
        var manual = _plan.Items.Count(i => i.Selectable && i.Selected && i.Method?.Kind == MethodKind.Manual);
        _summary.Text = run.Count == 0
            ? "没有选择要执行的项目"
            : $"已选 {run.Count} 项 · {ByteSize.Format(run.Sum(i => i.Bytes))}　预计释放 {ByteSize.Format(run.Sum(i => i.EstimatedBytes))}";
        _note.Text = "删除是永久的，不经过回收站（C 盘的回收站不释放空间）。每一项都会写入操作记录。" +
            (manual > 0 ? $"另有 {manual} 项是手动清理，工具不会删除，处理完后点\u201c重新测量\u201d。" : "");
        _run.Enabled = run.Count > 0;
        _export.Enabled = _plan.Items.Count > 0;
        _clear.Enabled = _plan.Items.Any(i => i.Selected);
    }

    private void SelectAll(bool selected)
    {
        foreach (var item in _plan.Items.Where(i => i.Selectable && i.State == PlanItemState.Pending))
        {
            item.Selected = selected;
        }

        RefreshItems();
    }

    private void ConfigureGrid()
    {
        var g = _grid;
        g.Dock = DockStyle.Fill;
        g.BackgroundColor = Theme.Card;
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = Theme.RowLine;
        g.RowHeadersVisible = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.AllowUserToOrderColumns = false;
        g.MultiSelect = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.EditMode = DataGridViewEditMode.EditOnEnter;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 32;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.HeaderBack,
            ForeColor = Theme.Muted,
            Font = Theme.Ui,
            SelectionBackColor = Theme.HeaderBack,
            SelectionForeColor = Theme.Muted,
            Padding = new Padding(6, 0, 6, 0),
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            SelectionBackColor = Theme.RowSelected,
            SelectionForeColor = Theme.Text,
            Padding = new Padding(6, 0, 6, 0),
            WrapMode = DataGridViewTriState.False,
        };
        g.RowTemplate.Height = 34;
        g.AccessibleName = "清理计划";

        g.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "选", Resizable = DataGridViewTriState.False });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "项目", ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "路径", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "大小", ReadOnly = true, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "来源", ReadOnly = true });
        g.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "处理方式", FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
        g.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "提示", ReadOnly = true });
        g.Columns.Add(new DataGridViewLinkColumn { HeaderText = "", ReadOnly = true, TrackVisitedState = false, LinkColor = Theme.Accent, ActiveLinkColor = Theme.AccentHover });
        foreach (DataGridViewColumn c in g.Columns)
        {
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
        }

        g.Columns[ColSize].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;

        g.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell or DataGridViewComboBoxCell)
            {
                g.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        g.CellValueChanged += OnCellValueChanged;
        g.CellContentClick += OnCellContentClick;
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex is ColTitle or ColPath && g.Rows[e.RowIndex].Tag is PlanItem { Node: >= 0 } item)
            {
                NavigateRequested?.Invoke(this, item.Node);
            }
        };
        g.CellPainting += OnCellPainting;
        g.RowPostPaint += (_, e) =>
        {
            if (g.Rows[e.RowIndex].Tag is not PlanGroup group)
            {
                return;
            }

            // 分组行：整行画成一条（原型 tr.grp），风险标签 + 说明
            var rect = e.RowBounds;
            var pad = Ui.Scale(this, 8);
            var tagHeight = Math.Min(rect.Height - Ui.Scale(this, 10), Ui.Scale(this, 20));
            var x = rect.Left + pad - g.HorizontalScrollingOffset;
            var y = rect.Top + (rect.Height - tagHeight) / 2;
            x += RuleView.DrawTag(e.Graphics, CleanupText.GroupTitle(group), Theme.Small, new Point(x, y), tagHeight, GroupStyle(group)) + pad;
            TextRenderer.DrawText(e.Graphics, g.Rows[e.RowIndex].Cells[ColPath].Value as string, Theme.Ui, new Rectangle(x, rect.Top, Math.Max(0, rect.Right - x), rect.Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };
        g.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && g.Rows[e.RowIndex].Tag is PlanItem item)
            {
                e.ToolTipText = e.ColumnIndex switch
                {
                    ColPath => item.Path,
                    ColTitle => item.Rule?.Purpose ?? item.Title,
                    ColHint => item.ResultText ?? item.Hint ?? "",
                    _ => "",
                };
            }
        };
        g.DataError += (_, e) => e.ThrowException = false;
        g.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && g.CurrentRow?.Tag is PlanItem { Node: >= 0 } item)
            {
                e.Handled = true;
                NavigateRequested?.Invoke(this, item.Node);
            }
        };

        var menu = new ContextMenuStrip();
        var view = menu.Items.Add("在空间浏览中查看", null, (_, _) => { if (CurrentItem is { Node: >= 0 } i) NavigateRequested?.Invoke(this, i.Node); });
        var open = menu.Items.Add("打开所在位置，自己清理", null, (_, _) => { if (CurrentItem is { } i) Open(i); });
        var measure = menu.Items.Add("重新测量", null, (_, _) => { if (CurrentItem is { } i) MeasureRequested?.Invoke(this, i); });
        menu.Opening += (_, e) =>
        {
            var item = CurrentItem;
            e.Cancel = item is null;
            view.Enabled = item is { Node: >= 0 };
            open.Enabled = measure.Enabled = item is not null;
        };
        g.ContextMenuStrip = menu;
        g.CellMouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
            {
                g.CurrentCell = g.Rows[e.RowIndex].Cells[ColTitle];
            }
        };
    }

    private PlanItem? CurrentItem => _grid.CurrentRow?.Tag as PlanItem;

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

    /// <summary>各列在 96 DPI 下的宽度（原型 colgroup）；路径列填满剩下的宽度。DataGridView 不会自动缩放列宽，按显示器 DPI 换算。</summary>
    private static readonly int[] BaseWidths = [36, 200, 200, 80, 70, 220, 200, 64];

    private void ApplyDpi()
    {
        var h = Ui.Scale(this, 34);
        _grid.RowTemplate.Height = h;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            row.Height = h;
        }

        _grid.ColumnHeadersHeight = Ui.Scale(this, 32);
        for (var i = 0; i < BaseWidths.Length; i++)
        {
            if (i == ColPath)
            {
                _grid.Columns[i].MinimumWidth = Ui.Scale(this, BaseWidths[i]);
            }
            else
            {
                _grid.Columns[i].Width = Ui.Scale(this, BaseWidths[i]);
            }
        }
    }

    private void AddGroupRow(PlanGroup group, List<PlanItem> items)
    {
        var index = _grid.Rows.Add();
        var row = _grid.Rows[index];
        row.Tag = group;
        row.ReadOnly = true;
        row.Cells[ColSelect] = new DataGridViewTextBoxCell();
        row.Cells[ColMethod] = new DataGridViewTextBoxCell();
        row.Cells[ColAction] = new DataGridViewTextBoxCell();
        var bytes = items.Sum(i => i.Bytes);
        row.Cells[ColTitle].Value = CleanupText.GroupTitle(group);
        row.Cells[ColPath].Value = group == PlanGroup.Blocked
            ? $"{CleanupText.GroupNote(group)} · {items.Count} 项"
            : $"{CleanupText.GroupNote(group)} · {items.Count} 项 · {ByteSize.Format(bytes)}";
        row.DefaultCellStyle.BackColor = Theme.HeaderBack;
        row.DefaultCellStyle.SelectionBackColor = Theme.HeaderBack;
    }

    private void AddItemRow(PlanItem item)
    {
        var index = _grid.Rows.Add();
        var row = _grid.Rows[index];
        row.Tag = item;
        if (!item.Selectable)
        {
            row.Cells[ColSelect] = new DataGridViewTextBoxCell();
            row.Cells[ColMethod] = new DataGridViewTextBoxCell();
        }
        else
        {
            var combo = (DataGridViewComboBoxCell)row.Cells[ColMethod];
            combo.Items.AddRange(item.Methods.Select(m => (object)m.Display).ToArray());
        }

        FillItemRow(row, item);
    }

    private void FillItemRow(DataGridViewRow row, PlanItem item)
    {
        var done = item.State != PlanItemState.Pending;
        if (item.Selectable)
        {
            row.Cells[ColSelect].Value = item.Selected;
            row.Cells[ColSelect].ReadOnly = done;
            row.Cells[ColMethod].Value = item.Method?.Display;
            row.Cells[ColMethod].ReadOnly = done;
        }
        else
        {
            row.Cells[ColSelect].Value = "";
            row.Cells[ColMethod].Value = item.Source == "系统设置" ? "系统设置中处理" : "不可删除";
            row.ReadOnly = true;
        }

        row.Cells[ColTitle].Value = item.Title;
        row.Cells[ColPath].Value = PathDisplay.Shorten(item.Path);
        row.Cells[ColSize].Value = ByteSize.Format(item.Bytes);
        row.Cells[ColSource].Value = item.Source;
        row.Cells[ColHint].Value = item.ResultText ?? item.Hint ?? "";
        row.Cells[ColHint].Style.ForeColor = item.State switch
        {
            PlanItemState.Done or PlanItemState.Measured => Theme.Down,
            PlanItemState.Failed or PlanItemState.NotRun or PlanItemState.Partial => Theme.WarnText,
            _ => Theme.Muted,
        };
        row.Cells[ColAction].Value = ActionText(item);
        var muted = !item.Selectable || done;
        row.DefaultCellStyle.ForeColor = muted ? Theme.Subtle : Theme.Text;
    }

    private string ActionText(PlanItem item)
    {
        if (item.Group == PlanGroup.Blocked)
        {
            return item.Source == "系统设置" ? "查看步骤" : "";
        }

        if (item.Method?.Kind == MethodKind.Manual || item.State == PlanItemState.Measured)
        {
            return _opened.Contains(item) || item.State == PlanItemState.Measured ? "重新测量" : "打开位置";
        }

        return item.Node >= 0 ? "查看" : "";
    }

    private void Open(PlanItem item)
    {
        OpenRequested?.Invoke(this, item);
        _opened.Add(item);
        RefreshItems();
    }

    private void OnCellContentClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex != ColAction || _grid.Rows[e.RowIndex].Tag is not PlanItem item)
        {
            return;
        }

        switch (ActionText(item))
        {
            case "查看":
                NavigateRequested?.Invoke(this, item.Node);
                break;
            case "打开位置":
                Open(item);
                break;
            case "重新测量":
                MeasureRequested?.Invoke(this, item);
                break;
            case "查看步骤":
                MessageBox.Show(FindForm(), item.Hint ?? item.BlockReason, item.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
        }
    }

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_binding || e.RowIndex < 0 || _grid.Rows[e.RowIndex].Tag is not PlanItem item || !item.Selectable)
        {
            return;
        }

        var row = _grid.Rows[e.RowIndex];
        if (e.ColumnIndex == ColSelect)
        {
            item.Selected = row.Cells[ColSelect].Value is true;
        }
        else if (e.ColumnIndex == ColMethod && row.Cells[ColMethod].Value is string display)
        {
            item.Method = item.Methods.FirstOrDefault(m => m.Display == display) ?? item.Method;
            if (item.Method?.Kind == MethodKind.Manual && !item.Selected)
            {
                item.Selected = true; // 选了手动清理，说明打算处理它
            }
        }

        _binding = true;
        FillItemRow(row, item);
        _binding = false;
        UpdateSummary();
    }

    private void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.Graphics is null)
        {
            return;
        }

        var row = _grid.Rows[e.RowIndex];
        if (row.Tag is PlanGroup)
        {
            // 分组行只画底色和下边线
            e.Handled = true;
            using (var back = new SolidBrush(Theme.HeaderBack))
            {
                e.Graphics.FillRectangle(back, e.CellBounds);
            }

            using (var line = new Pen(Theme.Border))
            {
                e.Graphics.DrawLine(line, e.CellBounds.Left, e.CellBounds.Bottom - 1, e.CellBounds.Right, e.CellBounds.Bottom - 1);
            }

            return; // 文字在 RowPostPaint 里跨列画
        }

        if (e.ColumnIndex == ColSource && row.Tag is PlanItem item)
        {
            // 来源列画成原型的标签（.s-rule、.r-never 等）
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var tagHeight = Math.Min(e.CellBounds.Height - Ui.Scale(this, 10), Ui.Scale(this, 20));
            var style = item.Source switch
            {
                "规则" => RuleView.RuleSource,
                "系统设置" => RuleView.RiskStyle(Risk.Guide),
                "用户文件" => RuleView.RiskStyle(Risk.High),
                _ => RuleView.RiskStyle(Risk.Never),
            };
            RuleView.DrawTag(e.Graphics, item.Source, Theme.Small, new Point(e.CellBounds.Left + Ui.Scale(this, 6), e.CellBounds.Top + (e.CellBounds.Height - tagHeight) / 2), tagHeight, style);
            e.Handled = true;
        }
    }

    private static RuleView.TagStyle GroupStyle(PlanGroup g) => g switch
    {
        PlanGroup.Safe => RuleView.RiskStyle(Risk.Safe),
        PlanGroup.Redownload => RuleView.RiskStyle(Risk.Low),
        PlanGroup.Irreversible => RuleView.RiskStyle(Risk.Medium),
        PlanGroup.Ai => RuleView.Unknown,
        _ => RuleView.RiskStyle(Risk.Never),
    };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal IReadOnlyList<PlanItem> SelectedToRun => _plan.ToRun.ToList();
}

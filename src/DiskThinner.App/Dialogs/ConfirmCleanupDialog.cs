using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.Cleanup;
using DiskThinner.Core;

namespace DiskThinner.App.Dialogs;

/// <summary>
/// 确认清理（原型 .p-dlg-clean）：按处理方式汇总、列出删除后无法恢复的明细、提示正在运行的程序，
/// 说明删除是永久的。默认焦点在"返回修改"上，回车不会直接开始。
/// 尺寸在 OnLoad 里按窗口所在显示器的 DPI 计算（自动缩放之后），内容太多时可以滚动。
/// </summary>
internal sealed class ConfirmCleanupDialog : Form
{
    private const int ContentWidth = 560;
    private const int MaxListed = 8;

    private readonly CheckBox _ask = new()
    {
        Text = "有文件被占用时，问我要不要请占用它的程序正常关闭（不会强制结束）",
        AutoSize = true,
        Font = Theme.Ui,
        Margin = new Padding(0, 10, 0, 0),
    };

    private readonly FlowLayoutPanel _body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 8), BackColor = Theme.Card, Location = Point.Empty };
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card };
    private readonly FlowLayoutPanel _footer = new() { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(20, 10, 20, 14), BackColor = Theme.Surface };
    private readonly TableLayoutPanel _table = new() { ColumnCount = 3, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    private readonly List<Control> _fullWidth = [];
    private readonly List<Label> _wrapped = [];

    public ConfirmCleanupDialog(IReadOnlyList<PlanItem> toRun, int manualCount)
    {
        Items = toRun;
        var total = toRun.Sum(i => i.Bytes);
        var estimate = toRun.Sum(i => i.EstimatedBytes);
        Text = "确认清理";
        Font = Theme.Ui;
        BackColor = Theme.Card;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);

        _body.Controls.Add(Wrapped(Ui.Label($"确认清理 {toRun.Count} 项，共 {ByteSize.Format(total)}", Theme.Title)));

        // ---- 按处理方式汇总 ----
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 14));
        _table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        var irreversible = toRun.Where(i => i.Group == PlanGroup.Irreversible).ToList();
        var commands = toRun.Where(i => i.Group != PlanGroup.Irreversible && i.Method?.Kind == MethodKind.Command).ToList();
        var deletes = toRun.Where(i => i.Group != PlanGroup.Irreversible && i.Method?.Kind == MethodKind.Delete).ToList();
        var names = string.Join("、", commands.Select(c => Path.GetFileNameWithoutExtension(c.Method!.Command!.Exe)).Distinct());
        Line("直接删除（会自动重建的缓存、临时文件、崩溃转储等）", deletes);
        Line($"运行官方清理命令（{names}）", commands);
        Line("删除后无法恢复", irreversible);
        _fullWidth.Add(_table);
        _body.Controls.Add(_table);

        // ---- 无法恢复的明细 ----
        if (irreversible.Count > 0)
        {
            var lines = irreversible.Take(MaxListed).Select(Describe).ToList();
            if (irreversible.Count > MaxListed)
            {
                lines.Add($"等 {irreversible.Count} 项");
            }

            var details = Ui.Button("查看明细");
            details.Margin = new Padding(0, 8, 0, 0);
            details.Click += (_, _) => ShowDetails(irreversible);
            _body.Controls.Add(Callout("以下内容删除后无法恢复", string.Join("\n", lines), Theme.WarnBack, Theme.WarnText, details));
        }

        // ---- 正在运行的程序 ----
        var running = toRun.Where(i => i.RunningApps.Count > 0).ToList();
        if (running.Count > 0)
        {
            var text = string.Join("\n", running.Take(MaxListed).Select(i => $"{string.Join("、", i.RunningApps)} 正在运行，{i.Title}中被占用的文件会跳过。"));
            _body.Controls.Add(Callout(null, text, Theme.AccentWeak, Theme.Text, null));
        }

        _body.Controls.Add(_ask);
        var note = Ui.Label($"删除是永久的，不经过回收站。预计释放 {ByteSize.Format(estimate)}，所有操作都会写入操作记录。" +
            (manualCount > 0 ? $"另有 {manualCount} 项是手动清理，不会执行。" : ""), Theme.Small, Theme.Muted);
        note.Margin = new Padding(0, 10, 0, 4);
        _body.Controls.Add(Wrapped(note));

        // ---- 按钮 ----
        var start = Ui.Button("开始清理", primary: true, accessibleDescription: "按上面的清单删除，删除是永久的");
        var back = Ui.Button("返回修改");
        start.DialogResult = DialogResult.OK;
        back.DialogResult = DialogResult.Cancel;
        start.Margin = new Padding(8, 0, 0, 0);
        back.Margin = new Padding(0);
        _footer.Controls.Add(start);
        _footer.Controls.Add(back);
        CancelButton = back;

        _scroll.Controls.Add(_body);
        Controls.Add(_scroll);
        Controls.Add(_footer);
        ActiveControl = back;
        ClientSize = new Size(ContentWidth + 40, 400);
    }

    public IReadOnlyList<PlanItem> Items { get; }

    /// <summary>用户选择了"有文件被占用时问我"。</summary>
    public bool AskWhenLocked => _ask.Checked;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 自动缩放之后再按实际 DPI 排版：文字换行宽度、表格宽度、窗口大小
        var width = Ui.Scale(this, ContentWidth);
        foreach (var label in _wrapped)
        {
            label.MaximumSize = new Size(width, 0);
        }

        foreach (var c in _fullWidth)
        {
            c.MinimumSize = new Size(width, 0);
            c.MaximumSize = new Size(width, 0);
        }

        _ask.MaximumSize = new Size(width, 0);
        _body.PerformLayout();
        var screen = Screen.FromControl(Owner ?? this).WorkingArea;
        var height = Math.Min(_body.PreferredSize.Height + _footer.PreferredSize.Height, screen.Height * 85 / 100);
        var scrollbar = _body.PreferredSize.Height + _footer.PreferredSize.Height > height ? SystemInformation.VerticalScrollBarWidth : 0;
        ClientSize = new Size(width + _body.Padding.Horizontal + scrollbar, height);
        if (Owner is { } owner)
        {
            Location = new Point(owner.Left + (owner.Width - Width) / 2, Math.Max(screen.Top, owner.Top + (owner.Height - Height) / 2));
        }
    }

    private static string Describe(PlanItem i)
    {
        var m = i.Method!;
        var range = m.OldestUtc is { } a && m.NewestUtc is { } b
            ? $"，最早 {a.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture)}、最晚 {b.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture)}"
            : "";
        return m.Kind == MethodKind.Delete
            ? $"{i.Title}：{m.Files:N0} 个文件{range}（{ByteSize.Format(m.EstimatedBytes)}）"
            : $"{i.Title}：运行 {m.Display}（{ByteSize.Format(i.Bytes)}）";
    }

    private void Line(string what, List<PlanItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var r = _table.RowCount++;
        _table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _table.Controls.Add(Cell(what, ContentAlignment.MiddleLeft), 0, r);
        _table.Controls.Add(Cell($"{items.Count} 项", ContentAlignment.MiddleRight), 1, r);
        _table.Controls.Add(Cell(ByteSize.Format(items.Sum(i => i.Bytes)), ContentAlignment.MiddleRight), 2, r);
    }

    private static Label Cell(string text, ContentAlignment align) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = align == ContentAlignment.MiddleLeft ? AnchorStyles.Left : AnchorStyles.Right,
        TextAlign = align,
        Font = Theme.Ui,
        ForeColor = Theme.Text,
        UseMnemonic = false,
        Margin = new Padding(0, 6, 0, 6),
    };

    private Label Wrapped(Label label)
    {
        _wrapped.Add(label);
        return label;
    }

    /// <summary>原型的 .callout：带底色的说明块。</summary>
    private FlowLayoutPanel Callout(string? title, string text, Color back, Color fore, Control? extra)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = back, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(12, 10, 12, 10) };
        if (title is not null)
        {
            flow.Controls.Add(Ui.Label(title, Theme.UiBold, fore));
        }

        var body = Ui.Label(text, Theme.Ui, fore);
        body.Margin = new Padding(0, title is null ? 0 : 4, 0, 0);
        flow.Controls.Add(body);
        if (extra is not null)
        {
            flow.Controls.Add(extra);
        }

        // 换行宽度要扣掉说明块自己的内边距
        flow.Layout += (_, _) => body.MaximumSize = new Size(Math.Max(100, flow.MinimumSize.Width - flow.Padding.Horizontal), 0);
        _fullWidth.Add(flow);
        return flow;
    }

    private void ShowDetails(IReadOnlyList<PlanItem> items)
    {
        using var dlg = new Form
        {
            Text = "删除后无法恢复的内容",
            Font = Theme.Ui,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96F, 96F),
            ClientSize = new Size(760, 360),
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, AccessibleName = "明细" };
        list.Columns.Add("项目", 200);
        list.Columns.Add("路径", 300);
        list.Columns.Add("文件数", 70, HorizontalAlignment.Right);
        list.Columns.Add("最早", 80);
        list.Columns.Add("最晚", 80);
        foreach (var i in items)
        {
            var m = i.Method!;
            list.Items.Add(new ListViewItem([
                i.Title, i.Path, m.Files.ToString("N0", CultureInfo.InvariantCulture),
                m.OldestUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                m.NewestUtc?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            ]));
        }

        dlg.Controls.Add(list);
        dlg.Load += (_, _) =>
        {
            int[] widths = [200, 300, 70, 80, 80];
            for (var c = 0; c < widths.Length; c++)
            {
                list.Columns[c].Width = Ui.Scale(dlg, widths[c]);
            }
        };
        dlg.ShowDialog(this);
    }
}

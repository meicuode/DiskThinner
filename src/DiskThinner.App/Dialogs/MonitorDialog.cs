using DiskThinner.App.Controls;
using DiskThinner.App.Settings;
using DiskThinner.Core;

namespace DiskThinner.App.Dialogs;

/// <summary>
/// 开启写入监控（临时诊断）（原型 .p-dlg-etw，docs/phase2a-design.md 3.3 节 P7、5.9.2 节）：选时长和模式，说明注意事项。
/// 几种状态：管理员（"开始监控"）；普通权限（"以管理员身份重启并开始"）；另一个窗口正在监控（开始按钮置灰）；
/// 用另一个管理员账户提权（黄色提示，允许继续）。只返回选择，不自己开始。Esc 取消、Enter 开始。
/// </summary>
internal sealed class MonitorDialog : Form
{
    private const int ContentWidth = 540;

    private readonly SegmentedControl _hours = new(L.T("监控时长"), [.. AppSettings.MonitorHourChoices.Select(h => L.F("{0} 小时", h))]);
    private readonly SegmentedControl _mode = new(L.T("记录模式"), L.T("轻量"), L.T("精确"));
    private readonly FlowLayoutPanel _body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 8), BackColor = Theme.Card, Location = Point.Empty };
    private readonly FlowLayoutPanel _footer = new() { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(20, 10, 20, 14), BackColor = Theme.Surface };
    private readonly List<Label> _wrapped = [];
    private readonly List<Label> _hints = [];
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card };
    private readonly List<Control> _fullWidth = [];

    /// <param name="hours">预选的时长（1 / 4 / 8 / 24）。</param>
    /// <param name="precise">预选的模式。</param>
    /// <param name="elevated">当前是管理员（按钮"开始监控"），否则按钮是"以管理员身份重启并开始"。</param>
    /// <param name="otherInstance">另一个 DiskThinner 窗口正在监控：开始按钮置灰。</param>
    /// <param name="processUser">账户不一致时提权用的账户（显示黄色提示），一致时为 null。</param>
    public MonitorDialog(int hours, bool precise, bool elevated, bool otherInstance, string? processUser)
    {
        SuspendLayout();
        Text = L.T("开启写入监控（临时诊断）");
        Font = Theme.Ui;
        BackColor = Theme.Card;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        KeyPreview = true;

        _body.Controls.Add(Wrapped(Ui.Label(L.T("开启写入监控（临时诊断）"), Theme.Title)));
        var intro = Ui.Label(L.T("监控期间会记录哪个程序在本机磁盘上新建、删除、重命名了文件（精确模式还记录写了多少），结束后在\u201c增长分析\u201d和\u201c空间浏览\u201d里查看。"), Theme.Ui, Theme.Text);
        intro.Margin = new Padding(0, 8, 0, 4);
        _body.Controls.Add(Wrapped(intro));

        if (otherInstance)
        {
            _body.Controls.Add(Callout(L.T("另一个 DiskThinner 窗口正在监控，结束后再开启。"), Theme.AccentWeak, Theme.Text));
        }

        if (processUser is not null)
        {
            _body.Controls.Add(Callout(L.F("你用 {0} 账户提权，监控数据会保存在 {0} 的数据目录下，用自己的账户打开 DiskThinner 时看不到。", processUser), Theme.WarnBack, Theme.WarnText));
        }

        _hours.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MonitorHourChoices, hours));
        _mode.SelectedIndex = precise ? 1 : 0;
        _hours.Margin = _mode.Margin = new Padding(0);
        _body.Controls.Add(FormRow(L.T("时长"), _hours, null));
        _body.Controls.Add(FormRow(L.T("模式"), _mode,
            L.T("轻量只记录新建、删除、重命名。精确额外记录每次写入，能估算每个程序让目录涨了多少，编译、npm install 时开销更大。")));

        var notes = new[]
        {
            elevated
                ? L.T("需要管理员权限（当前已是管理员）。")
                : L.T("需要管理员权限。现在是普通权限：点下面的按钮后 DiskThinner 会以管理员身份重新打开、重新扫描，然后开始监控。"),
            L.T("监控期间请保持 DiskThinner 打开（可以最小化）；到时间或关闭窗口都会停止。"),
            L.T("数据只保存在本机，按目录和程序汇总，不记录文件名和文件内容，不联网，不修改任何文件。"),
            L.T("为了在程序被强制结束时也能停下来，会临时添加一个计划任务，监控结束后删除。"),
        };
        var note = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Background, Margin = new Padding(0, 12, 0, 4), Padding = new Padding(12, 10, 12, 10) };
        foreach (var n in notes)
        {
            var l = Ui.Label("· " + n, Theme.Small, Theme.Text);
            l.Margin = new Padding(0, 2, 0, 2);
            note.Controls.Add(l);
            _wrapped.Add(l);
        }

        note.AccessibleName = L.T("注意事项");
        _fullWidth.Add(note);
        _body.Controls.Add(note);

        // ---- 按钮 ----
        var start = Ui.Button(elevated ? L.T("开始监控") : L.T("以管理员身份重启并开始"), primary: true,
            accessibleDescription: elevated ? L.T("按选择的时长和模式开始写入监控") : L.T("以管理员身份重新打开 DiskThinner，然后开始写入监控"));
        var cancel = Ui.Button(L.T("取消"));
        start.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        start.Enabled = !otherInstance;
        start.Margin = new Padding(8, 0, 0, 0);
        cancel.Margin = new Padding(0);
        _footer.Controls.Add(start);
        _footer.Controls.Add(cancel);
        AcceptButton = start.Enabled ? start : null;
        CancelButton = cancel;
        StartButton = start;

        _scroll.Controls.Add(_body);
        Controls.Add(_scroll);
        Controls.Add(_footer);
        ActiveControl = start.Enabled ? _hours : cancel;
        ClientSize = new Size(ContentWidth + 40, 420);
        ResumeLayout(false);
    }

    /// <summary>选择的时长（小时）。</summary>
    public int Hours => AppSettings.MonitorHourChoices[_hours.SelectedIndex];

    public bool Precise => _mode.SelectedIndex == 1;

    internal Button StartButton { get; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 自动缩放之后再按实际 DPI 排版（和确认清理对话框一样）
        var width = Ui.Scale(this, ContentWidth);
        foreach (var label in _wrapped)
        {
            label.MaximumSize = new Size(Math.Max(100, width - (label.Parent == _body ? 0 : Ui.Scale(this, 24))), 0);
        }

        foreach (var c in _fullWidth)
        {
            c.MinimumSize = new Size(width, 0);
            c.MaximumSize = new Size(width, 0);
        }

        // 表单行里的说明：扣掉左边标签列（64）和间距
        foreach (var h in _hints)
        {
            h.MaximumSize = new Size(Math.Max(100, width - Ui.Scale(this, 80)), 0);
        }

        // 正文固定为内容宽度：嵌套的自动大小面板会按换行之前的宽度报首选大小，不固定的话会出现横向滚动条
        _body.MinimumSize = new Size(width + _body.Padding.Horizontal, 0);
        _body.MaximumSize = new Size(width + _body.Padding.Horizontal, 0);
        _body.PerformLayout();
        var screen = Screen.FromControl(Owner ?? this).WorkingArea;
        var maxHeight = screen.Height * 85 / 100;
        ClientSize = new Size(width + _body.Padding.Horizontal, Math.Min(_body.PreferredSize.Height + _footer.PreferredSize.Height + Ui.Scale(this, 12), maxHeight));

        // 嵌套的自动大小面板在换行宽度改了之后才算出真正的高度：排版后内容还比可见区域高，就把窗口加高（最多到屏幕的 85%）
        for (var i = 0; i < 3; i++)
        {
            PerformLayout();
            var missing = _body.Height - _scroll.ClientSize.Height;
            if (missing <= 0 || ClientSize.Height >= maxHeight)
            {
                break;
            }

            ClientSize = new Size(ClientSize.Width, Math.Min(ClientSize.Height + missing + Ui.Scale(this, 4), maxHeight));
        }

        if (_body.Height > _scroll.ClientSize.Height)
        {
            ClientSize = new Size(ClientSize.Width + SystemInformation.VerticalScrollBarWidth, ClientSize.Height); // 还是放不下：留出竖向滚动条，不出现横向的
        }
        if (Owner is { } owner)
        {
            Location = new Point(owner.Left + (owner.Width - Width) / 2, Math.Max(screen.Top, owner.Top + (owner.Height - Height) / 2));
        }
    }

    private Label Wrapped(Label label)
    {
        _wrapped.Add(label);
        return label;
    }

    /// <summary>原型的 .form-row：左边标签，右边控件和说明。</summary>
    private TableLayoutPanel FormRow(string label, Control control, string? hint)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lbl = Ui.Label(label, Theme.Ui, Theme.Muted);
        lbl.Margin = new Padding(0, 6, 8, 0);
        row.Controls.Add(lbl, 0, 0);
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        right.Controls.Add(control);
        if (hint is not null)
        {
            var h = Ui.Label(hint, Theme.Small, Theme.Muted);
            h.Margin = new Padding(0, 6, 0, 0);
            _hints.Add(h);
            right.Controls.Add(h);
        }

        row.Controls.Add(right, 1, 0);
        return row;
    }

    /// <summary>原型的 .callout：带底色的提示块。</summary>
    private FlowLayoutPanel Callout(string text, Color back, Color fore)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = back, Margin = new Padding(0, 10, 0, 0), Padding = new Padding(12, 8, 12, 8) };
        var body = Ui.Label(text, Theme.Ui, fore);
        body.Margin = new Padding(0);
        flow.Controls.Add(body);
        _wrapped.Add(body);
        _fullWidth.Add(flow);
        flow.AccessibleName = text;
        return flow;
    }
}

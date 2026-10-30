using DiskThinner.App.Controls;
using DiskThinner.App.Settings;
using DiskThinner.Core;

namespace DiskThinner.App.Dialogs;

/// <summary>
/// 安装常驻监控服务（原型 .p-dlg-agent，docs/phase2b-design.md 3.2 节 Q2）：说明服务做什么、装在哪、数据在哪、开销和身份，选模式和保留天数。
/// 管理员下按钮是"安装并启动"，普通权限下是"以管理员身份重启并安装"。只返回选择，不自己安装。Esc 取消、Enter 确认。
/// </summary>
internal sealed class AgentInstallDialog : Form
{
    private const int ContentWidth = 560;

    private readonly SegmentedControl _mode = new(L.T("记录模式"), L.T("轻量"), L.T("精确"));
    private readonly ComboBox _keep = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120, Font = Theme.Ui, AccessibleName = L.T("常驻服务数据保留时长"), Margin = new Padding(0) };
    private readonly FlowLayoutPanel _body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 8), BackColor = Theme.Card, Location = Point.Empty };
    private readonly FlowLayoutPanel _footer = new() { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(20, 10, 20, 14), BackColor = Theme.Surface };
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Card };
    private readonly List<Label> _wrapped = [];
    private readonly List<Label> _hints = [];
    private readonly List<Control> _fullWidth = [];

    public AgentInstallDialog(bool precise, int keepDays, bool elevated, string programDir, string dataDir)
    {
        SuspendLayout();
        Text = L.T("安装常驻监控服务");
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

        _body.Controls.Add(Wrapped(Ui.Label(L.T("安装常驻监控服务"), Theme.Title)));
        var intro = Ui.Label(L.T("服务开机后自动记录哪个程序在本机磁盘上新建、删除、重命名、写入了文件，按目录和程序汇总。只在本机，不联网，不记录文件名和文件内容，不修改、不删除任何文件。"), Theme.Ui, Theme.Text);
        intro.Margin = new Padding(0, 8, 0, 4);
        _body.Controls.Add(Wrapped(intro));

        var facts = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
        facts.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (label, value) in new[]
                 {
                     (L.T("程序"), programDir.TrimEnd('\\') + "\\"),
                     (L.T("数据"), dataDir.TrimEnd('\\') + "\\" + L.T("（只有管理员能访问）")),
                     (L.T("开销"), L.T("空闲时 CPU 约 0.1%，内存不超过约 100 MB")),
                     (L.T("身份"), L.T("以 SYSTEM 运行；开机自动启动（延迟），出错时自动重启两次")),
                 })
        {
            var l = Ui.Label(label, Theme.Ui, Theme.Muted);
            l.Margin = new Padding(0, 3, 8, 3);
            var v = Ui.Label(value, Theme.Ui, Theme.Text);
            v.Margin = new Padding(0, 3, 0, 3);
            _hints.Add(v);
            facts.Controls.Add(l);
            facts.Controls.Add(v);
        }

        facts.AccessibleName = L.T("服务的位置和开销");
        _body.Controls.Add(facts);

        _mode.SelectedIndex = precise ? 1 : 0;
        _mode.Margin = new Padding(0);
        _keep.Items.AddRange([.. AppSettings.MonitorKeepChoices.Select(d => L.F("{0} 天", d))]);
        _keep.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MonitorKeepChoices, keepDays));
        _body.Controls.Add(FormRow(L.T("模式"), _mode, L.T("和临时诊断一样：轻量只记录新建、删除、重命名；精确还记录写了多少。")));
        _body.Controls.Add(FormRow(L.T("保留"), _keep, L.T("另有 256 MB 的上限，超出时先删最早的一天。")));

        var notes = new[]
        {
            elevated
                ? L.T("需要管理员权限（当前已是管理员）。")
                : L.T("需要管理员权限。现在是普通权限：点下面的按钮后 DiskThinner 会以管理员身份重新打开，然后安装。"),
            L.T("随时可以在\u201c设置 → 写入监控\u201d里停止或卸载；卸载时可以选择是否删除已经记录的数据。"),
            L.T("其他用户个人目录下的记录，普通用户的界面看不到。"),
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

        var ok = Ui.Button(elevated ? L.T("安装并启动") : L.T("以管理员身份重启并安装"), primary: true,
            accessibleDescription: elevated ? L.T("复制服务程序、注册并启动常驻监控服务") : L.T("以管理员身份重新打开 DiskThinner，然后安装常驻监控服务"));
        var cancel = Ui.Button(L.T("取消"));
        ok.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        ok.Margin = new Padding(8, 0, 0, 0);
        cancel.Margin = new Padding(0);
        _footer.Controls.Add(ok);
        _footer.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        OkButton = ok;

        _scroll.Controls.Add(_body);
        Controls.Add(_scroll);
        Controls.Add(_footer);
        ActiveControl = _mode;
        ClientSize = new Size(ContentWidth + 40, 460);
        ResumeLayout(false);
    }

    public bool Precise => _mode.SelectedIndex == 1;

    public int KeepDays => AppSettings.MonitorKeepChoices[Math.Max(0, _keep.SelectedIndex)];

    internal Button OkButton { get; }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
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

        foreach (var h in _hints)
        {
            h.MaximumSize = new Size(Math.Max(100, width - Ui.Scale(this, 80)), 0);
        }

        _body.MinimumSize = new Size(width + _body.Padding.Horizontal, 0);
        _body.MaximumSize = new Size(width + _body.Padding.Horizontal, 0);
        _body.PerformLayout();
        var screen = Screen.FromControl(Owner ?? this).WorkingArea;
        var maxHeight = screen.Height * 85 / 100;
        ClientSize = new Size(width + _body.Padding.Horizontal, Math.Min(_body.PreferredSize.Height + _footer.PreferredSize.Height + Ui.Scale(this, 12), maxHeight));
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
            ClientSize = new Size(ClientSize.Width + SystemInformation.VerticalScrollBarWidth, ClientSize.Height);
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
}

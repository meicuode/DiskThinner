using DiskThinner.App.Controls;
using DiskThinner.Cleanup;
using DiskThinner.Core;

namespace DiskThinner.App.Dialogs;

/// <summary>执行清理时的进度：第几项、删了多少；可以取消（当前文件处理完就停）。</summary>
internal sealed class CleanupProgressDialog : Form
{
    private readonly Label _title = Ui.Label("正在准备…", Theme.UiBold);
    private readonly Label _detail = Ui.Label("", Theme.Ui, Theme.Muted);
    private readonly ProgressBar _bar = new() { Width = 440, Height = 10, Style = ProgressBarStyle.Continuous, Minimum = 0 };
    private readonly Button _cancel = Ui.Button("停止");
    private readonly CancellationTokenSource _cts = new();

    public CleanupProgressDialog(int count)
    {
        Text = "正在清理";
        Font = Theme.Ui;
        BackColor = Theme.Card;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _bar.Maximum = Math.Max(1, count);
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16), BackColor = Theme.Card };
        _title.MaximumSize = new Size(440, 0);
        _detail.MaximumSize = new Size(440, 0);
        _detail.Margin = new Padding(0, 6, 0, 10);
        _cancel.Margin = new Padding(0, 12, 0, 0);
        _cancel.Click += (_, _) =>
        {
            _cts.Cancel();
            _cancel.Enabled = false;
            _cancel.Text = "正在停止…";
        };
        flow.Controls.AddRange([_title, _detail, _bar, _cancel]);
        Controls.Add(flow);
    }

    public CancellationToken Token => _cts.Token;

    public void Report(ExecProgress p)
    {
        _title.Text = $"正在清理 {p.Index + 1} / {p.Count}：{p.Title}";
        _detail.Text = p.Files > 0 ? $"这一项已删除 {p.Files:N0} 个文件 · {ByteSize.Format(p.Bytes)}" : "";
        _bar.Value = Math.Min(_bar.Maximum, p.Index);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }

        base.Dispose(disposing);
    }
}

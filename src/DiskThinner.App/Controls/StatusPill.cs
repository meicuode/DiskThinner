using System.Drawing.Drawing2D;

namespace DiskThinner.App.Controls;

/// <summary>
/// 原型的 .pill（工具栏右侧的"写入监控：关闭 · 开启诊断"）：圆点 + 状态文字 + 链接色的操作文字。是一个按钮，
/// 读屏软件读出的名字和显示的文字一致（docs/phase2a-design.md 5.9.3 节）。
/// </summary>
internal sealed class StatusPill : Button
{
    public static readonly Color Idle = ColorTranslator.FromHtml("#767676");
    public static readonly Color Running = ColorTranslator.FromHtml("#107c10");
    // 原型的 #c19c00 在胶囊底色上只有 2.4:1，加深到 3:1 以上（ContrastTests）
    public static readonly Color Warning = ColorTranslator.FromHtml("#9a7b00");

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
    private string _main = "";
    private string _action = "";
    private Color _dot = Idle;
    private bool _hover;

    public StatusPill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        Cursor = Cursors.Hand;
        Font = Theme.Small;
        UseVisualStyleBackColor = false;
        AccessibleRole = AccessibleRole.PushButton;
    }

    internal string MainText => _main;

    internal string ActionText => _action;

    internal Color Dot => _dot;

    /// <summary>设置文字：<paramref name="action"/> 为空时只有状态文字（例如"写入监控：另一个窗口正在监控"）。</summary>
    public void Set(string main, string? action, Color dot, string? description = null)
    {
        _main = main;
        _action = action ?? "";
        _dot = dot;
        Text = _action.Length == 0 ? main : main + " · " + _action;
        AccessibleName = Text;
        AccessibleDescription = description;
        PerformLayout();
        Parent?.PerformLayout();
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, Size.Empty, Flags);
        return new Size(text.Width + Ui.Scale(this, 34), Math.Max(text.Height + Ui.Scale(this, 10), Ui.Scale(this, 26)));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // 圆角外面用最近的不透明上级的底色（直接的父控件是透明的 FlowLayoutPanel，Clear 透明色会画成黑色）
        var back = Theme.Surface;
        for (var p = Parent; p is not null; p = p.Parent)
        {
            if (p.BackColor.A == 255)
            {
                back = p.BackColor;
                break;
            }
        }

        g.Clear(back);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = rect.Height;
        using (var path = new GraphicsPath())
        {
            path.AddArc(rect.X, rect.Y, radius, radius, 90, 180);
            path.AddArc(rect.Right - radius, rect.Y, radius, radius, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(_hover ? Theme.NavHover : Theme.Background);
            g.FillPath(fill, path);
            using var border = new Pen(Focused && ShowFocusCues ? Theme.Accent : Theme.Border, Focused && ShowFocusCues ? 2 : 1);
            g.DrawPath(border, path);
        }

        var d = Ui.Scale(this, 8);
        var x = Ui.Scale(this, 12);
        using (var dot = new SolidBrush(_dot))
        {
            g.FillEllipse(dot, x, (Height - d) / 2, d, d);
        }

        g.SmoothingMode = SmoothingMode.Default;
        x += d + Ui.Scale(this, 6);
        var main = TextRenderer.MeasureText(g, _main, Font, Size.Empty, Flags).Width;
        TextRenderer.DrawText(g, _main, Font, new Rectangle(x, 0, main + 2, Height), Theme.Text, Flags);
        if (_action.Length > 0)
        {
            x += main;
            const string sep = " · ";
            var sepWidth = TextRenderer.MeasureText(g, sep, Font, Size.Empty, Flags).Width;
            TextRenderer.DrawText(g, sep, Font, new Rectangle(x, 0, sepWidth + 2, Height), Theme.Subtle, Flags);
            x += sepWidth;
            TextRenderer.DrawText(g, _action, Font, new Rectangle(x, 0, Width - x, Height), Theme.Accent, Flags);
        }
    }
}

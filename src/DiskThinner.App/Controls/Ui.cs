namespace DiskThinner.App.Controls;

/// <summary>按原型样式创建常用控件（.btn、.btn.primary、标签等）。</summary>
internal static class Ui
{
    public static Button Button(string text, bool primary = false, string? accessibleDescription = null)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 28),
            Padding = new Padding(8, 0, 8, 0),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Ui,
            BackColor = primary ? Theme.Accent : Color.White,
            ForeColor = primary ? Color.White : Theme.Text,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            AccessibleName = text,
            AccessibleDescription = accessibleDescription,
        };
        b.FlatAppearance.BorderColor = primary ? Theme.Accent : Theme.BorderStrong;
        b.FlatAppearance.MouseOverBackColor = primary ? Theme.AccentHover : Theme.Background;
        b.FlatAppearance.MouseDownBackColor = primary ? Theme.AccentHover : Theme.NavHover;
        return b;
    }

    public static Label Label(string text, Font? font = null, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Theme.Ui,
        ForeColor = color ?? Theme.Text,
        BackColor = Color.Transparent,
        UseMnemonic = false,
    };

    /// <summary>原型的 .chip：可以切换的小按钮（CheckBox 的按钮外观，读屏软件读作复选框）。</summary>
    public static CheckBox Chip(string text)
    {
        // 按钮外观的 CheckBox 自动大小时会按复选框的布局算宽度，文字会被截掉；这里按 96 DPI 量好固定尺寸，交给窗体统一缩放
        var textWidth = TextRenderer.MeasureText(text, Theme.Small).Width;
        var c = new CheckBox
        {
            Text = text,
            Appearance = Appearance.Button,
            AutoSize = false,
            Size = new Size(textWidth + 24, 26),
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Small,
            BackColor = Color.White,
            ForeColor = Theme.Text,
            Margin = new Padding(6, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            AccessibleName = text,
        };
        c.FlatAppearance.BorderColor = Theme.BorderStrong;
        c.FlatAppearance.CheckedBackColor = Theme.AccentWeak;
        c.FlatAppearance.MouseOverBackColor = Theme.Background;
        c.CheckedChanged += (_, _) => c.ForeColor = c.Checked ? Theme.Accent : Theme.Text;
        return c;
    }

    /// <summary>原型的 .tag：小号圆角底色文字。</summary>
    public static Label Tag(string text, Color back, Color fore) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Theme.Small,
        ForeColor = fore,
        BackColor = back,
        Padding = new Padding(4, 1, 4, 1),
        Margin = new Padding(6, 3, 0, 0),
        UseMnemonic = false,
    };

    /// <summary>
    /// 把 96 DPI 下的像素换算到控件所在显示器。只能在句柄创建之后用（之前 DeviceDpi 是系统 DPI，不一定是窗口所在显示器的）；
    /// 构造函数里设置的尺寸交给窗体的 AutoScaleMode.Dpi 统一缩放。
    /// </summary>
    public static int Scale(Control c, int value) => (int)Math.Round(value * c.DeviceDpi / 96.0);

    /// <summary>绘制 ▶ / ▼ 小三角（原型 .chev），不用字符，避免受字体影响。</summary>
    public static void DrawChevron(Graphics g, Rectangle box, bool expanded, Color color)
    {
        var size = Math.Max(5, Math.Min(box.Width, box.Height) / 2);
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        PointF[] points = expanded
            ? [new(cx - size / 2f, cy - size / 4f), new(cx + size / 2f, cy - size / 4f), new(cx, cy + size / 3f)]
            : [new(cx - size / 4f, cy - size / 2f), new(cx + size / 3f, cy), new(cx - size / 4f, cy + size / 2f)];
        using var brush = new SolidBrush(color);
        var mode = g.SmoothingMode;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.FillPolygon(brush, points);
        g.SmoothingMode = mode;
    }
}

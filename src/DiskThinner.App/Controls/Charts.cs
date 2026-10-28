using System.Drawing.Drawing2D;
using System.Globalization;
using DiskThinner.Core;

namespace DiskThinner.App.Controls;

/// <summary>
/// 柱状图（原型"每日净增长"）：正值向上（蓝），负值向下（绿），明显高出其他日子的一天标红。
/// 数据很少，直接在 OnPaint 里画；读屏软件读 AccessibleDescription 里的文字版本。
/// </summary>
internal sealed class BarChart : Control
{
    private static readonly Color Positive = ColorTranslator.FromHtml("#6fa0d6");
    private static readonly Color Spike = ColorTranslator.FromHtml("#e07b6a");
    private static readonly Color Negative = ColorTranslator.FromHtml("#8cc084");
    private readonly DerivedFonts _fonts = new();
    private IReadOnlyList<(string Label, long Value)> _bars = [];
    private string _empty = "";

    public BarChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Chart;
    }

    internal IReadOnlyList<(string Label, long Value)> Bars => _bars;

    public void SetData(IReadOnlyList<(string Label, long Value)> bars, string emptyText)
    {
        _bars = bars;
        _empty = emptyText;
        AccessibleDescription = bars.Count == 0 ? emptyText : string.Join(L.T("，"), bars.Select(b => $"{b.Label} {Signed(b.Value)}"));
        Invalidate();
    }

    internal static string Signed(long bytes) => ByteSize.FormatDelta(bytes);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        _fonts.Update(Font);
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        if (_bars.Count == 0)
        {
            TextRenderer.DrawText(g, _empty, _fonts.Regular, ClientRectangle, Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var textH = TextRenderer.MeasureText("0", _fonts.Small).Height;
        var top = textH + 4;
        var bottom = Height - textH - 6;
        var maxUp = Math.Max(1, _bars.Max(b => b.Value));
        var maxDown = Math.Max(0, -_bars.Min(b => b.Value));
        var span = (double)maxUp + maxDown;
        var zero = top + (int)((bottom - top) * (maxUp / span));
        var slot = (double)(Width - 8) / _bars.Count;
        var barW = Math.Max(2, (int)(slot * 0.6));
        var sorted = _bars.Select(b => b.Value).Where(v => v > 0).OrderDescending().ToList();
        var spikeAt = sorted.Count >= 3 && sorted[0] > 3 * sorted[1] ? sorted[0] : long.MinValue;

        using (var axis = new Pen(Theme.BorderStrong))
        {
            g.DrawLine(axis, 4, zero, Width - 4, zero);
        }

        var labelEvery = Math.Max(1, (int)Math.Ceiling(_bars.Count / Math.Max(1.0, (Width - 8) / (double)TextRenderer.MeasureText("10-30", _fonts.Small).Width / 1.4)));
        for (var i = 0; i < _bars.Count; i++)
        {
            var (label, value) = _bars[i];
            var cx = 4 + (int)(slot * (i + 0.5));
            var h = (int)((bottom - top) * (Math.Abs(value) / span));
            var rect = value >= 0 ? new Rectangle(cx - barW / 2, zero - h, barW, Math.Max(h, 1)) : new Rectangle(cx - barW / 2, zero, barW, Math.Max(h, 1));
            using (var b = new SolidBrush(value < 0 ? Negative : value == spikeAt ? Spike : Positive))
            {
                g.FillRectangle(b, rect);
            }

            if (_bars.Count <= 14 || value == spikeAt)
            {
                var text = ByteSize.Format(Math.Abs(value));
                var y = value >= 0 ? rect.Top - textH - 1 : rect.Bottom + 1;
                TextRenderer.DrawText(g, value < 0 ? "−" + text : text, _fonts.Small, new Rectangle(cx - (int)slot, Math.Max(0, y), (int)slot * 2, textH), Theme.Text, flags);
            }

            if (i % labelEvery == 0 || i == _bars.Count - 1)
            {
                TextRenderer.DrawText(g, label, _fonts.Small, new Rectangle(cx - (int)slot, Height - textH - 2, (int)slot * 2, textH), Theme.Subtle, flags);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>折线图（原型"C 盘可用空间（近 30 天）"）：纵轴从 0 开始，按 GB 取整的刻度。</summary>
internal sealed class TrendChart : Control
{
    private readonly DerivedFonts _fonts = new();
    private IReadOnlyList<(DateTime Local, long Value)> _points = [];
    private string _empty = "";

    public TrendChart()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Chart;
    }

    internal int PointCount => _points.Count;

    public void SetData(IReadOnlyList<(DateTime Local, long Value)> points, string emptyText)
    {
        _points = points;
        _empty = emptyText;
        AccessibleDescription = points.Count < 2
            ? emptyText
            : L.F("从 {0:MM-dd} 的 {1} 到 {2:MM-dd} 的 {3}，最低 {4}", points[0].Local, ByteSize.Format(points[0].Value), points[^1].Local, ByteSize.Format(points[^1].Value), ByteSize.Format(points.Min(p => p.Value)));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        _fonts.Update(Font);
        if (_points.Count < 2)
        {
            TextRenderer.DrawText(g, _empty, _fonts.Regular, ClientRectangle, Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
            return;
        }

        const double Gb = 1L << 30;
        var maxGb = _points.Max(p => p.Value) / Gb;
        var step = maxGb switch { <= 4 => 1, <= 20 => 5, <= 40 => 10, <= 100 => 25, <= 200 => 50, _ => 100 };
        var topGb = Math.Max(step, Math.Ceiling(maxGb / step) * step);
        var textH = TextRenderer.MeasureText("0", _fonts.Small).Height;
        var labelW = TextRenderer.MeasureText(topGb.ToString(CultureInfo.InvariantCulture) + " GB", _fonts.Small).Width + 6;
        var plot = Rectangle.FromLTRB(labelW, textH / 2 + 2, Width - 8, Height - textH - 6);
        if (plot.Width < 20 || plot.Height < 20)
        {
            return;
        }

        using (var grid = new Pen(ColorTranslator.FromHtml("#ececec")))
        {
            for (var v = 0.0; v <= topGb + 0.001; v += step)
            {
                var y = plot.Bottom - (int)(plot.Height * v / topGb);
                g.DrawLine(grid, plot.Left, y, plot.Right, y);
                if (v < 0.001)
                {
                    // 0 刻度与横轴日期挤在同一高度，只画线不写字。
                    continue;
                }

                var text = v.ToString(CultureInfo.InvariantCulture) + (v + step > topGb ? " GB" : "");
                TextRenderer.DrawText(g, text, _fonts.Small, new Rectangle(0, y - textH / 2, labelW - 4, textH), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
        }

        var t0 = _points[0].Local;
        var span = Math.Max(1, (_points[^1].Local - t0).TotalSeconds);
        var pts = _points.Select(p => new PointF(plot.Left + (float)(plot.Width * (p.Local - t0).TotalSeconds / span),
            plot.Bottom - (float)(plot.Height * (p.Value / Gb) / topGb))).ToArray();

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var fill = new SolidBrush(Color.FromArgb(20, Theme.Accent)))
        {
            g.FillPolygon(fill, [new PointF(pts[0].X, plot.Bottom), .. pts, new PointF(pts[^1].X, plot.Bottom)]);
        }

        using (var line = new Pen(Theme.Accent, Math.Max(1.5f, DeviceDpi / 64f)))
        {
            g.DrawLines(line, pts);
        }

        if (pts.Length <= 40)
        {
            using var dot = new SolidBrush(Theme.Accent);
            var r = DeviceDpi / 48f;
            foreach (var p in pts)
            {
                g.FillEllipse(dot, p.X - r, p.Y - r, r * 2, r * 2);
            }
        }

        g.SmoothingMode = SmoothingMode.Default;
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        // 跨度不到两天时只写日期会看到几个相同的 "10-20"，改为带时刻。
        var format = (_points[^1].Local - t0).TotalDays < 2 ? "MM-dd HH:mm" : "MM-dd";
        var gap = TextRenderer.MeasureText(" ", _fonts.Small).Width * 2;
        var labels = new List<(int Left, int Width, string Text)>();
        foreach (var i in new[] { 0, _points.Count - 1, _points.Count / 2 }.Distinct())
        {
            var text = _points[i].Local.ToString(format, CultureInfo.InvariantCulture);
            var w = TextRenderer.MeasureText(text, _fonts.Small).Width;
            var left = Math.Clamp((int)pts[i].X - w / 2, 2, Math.Max(2, Width - w - 2));
            // 首尾优先；中间那个与已有标签重叠或文字相同就不画。
            if (labels.Any(l => l.Text == text || (left < l.Left + l.Width + gap && l.Left < left + w + gap)))
            {
                continue;
            }

            labels.Add((left, w, text));
        }

        foreach (var (left, w, text) in labels)
        {
            TextRenderer.DrawText(g, text, _fonts.Small, new Rectangle(left, Height - textH - 2, w, textH), Theme.Subtle, flags);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// 写入监控覆盖时段（原型 .timeline，docs/phase2a-design.md 5.9.4 节）：TitleBar 色的底条上用 Accent 色画出有监控的时段，
/// 进行中的时段右端加一个深色标记；下面是日期刻度。读屏软件读 AccessibleDescription 里列出的时段。
/// </summary>
internal sealed class CoverageTimeline : Control
{
    private static readonly Color RunningEdge = ColorTranslator.FromHtml("#063d73");
    private readonly DerivedFonts _fonts = new();
    private DateTime _from;
    private DateTime _to;
    private IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, bool Running)> _spans = [];

    public CoverageTimeline()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = L.T("写入监控覆盖时段");
    }

    internal IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, bool Running)> Spans => _spans;

    public void SetData(DateTime fromUtc, DateTime toUtc, IReadOnlyList<(DateTime StartUtc, DateTime EndUtc, bool Running)> spans)
    {
        _from = fromUtc;
        _to = toUtc > fromUtc ? toUtc : fromUtc.AddHours(1);
        _spans = spans;
        AccessibleDescription = spans.Count == 0
            ? L.T("这段时间里没有写入监控记录")
            : L.F("写入监控覆盖：{0}", string.Join(L.T("，"), spans.Select(s => L.F("{0} 到 {1}{2}",
                s.StartUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture),
                s.EndUtc.ToLocalTime().ToString(s.StartUtc.ToLocalTime().Date == s.EndUtc.ToLocalTime().Date ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture),
                s.Running ? L.T("（进行中）") : ""))));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        _fonts.Update(Font);
        var textH = TextRenderer.MeasureText("0", _fonts.Small).Height;
        var bar = new Rectangle(2, 2, Math.Max(10, Width - 4), Math.Max(6, Height - textH - 8));
        using (var back = new SolidBrush(Theme.TitleBar))
        {
            g.FillRectangle(back, bar);
        }

        var span = Math.Max(1, (_to - _from).TotalSeconds);
        int X(DateTime t) => bar.Left + (int)Math.Round(bar.Width * Math.Clamp((t - _from).TotalSeconds / span, 0, 1));
        using (var fill = new SolidBrush(Theme.Accent))
        using (var edge = new SolidBrush(RunningEdge))
        {
            foreach (var (start, end, running) in _spans)
            {
                var left = X(start);
                var right = Math.Max(left + Math.Max(2, Ui.Scale(this, 2)), X(end));
                g.FillRectangle(fill, left, bar.Top, right - left, bar.Height);
                if (running)
                {
                    var w = Math.Max(2, Ui.Scale(this, 3));
                    g.FillRectangle(edge, right - w, bar.Top - 1, w, bar.Height + 2);
                }
            }
        }

        // 日期刻度：首、尾和中间两个，跨度不到两天时带时刻；和已经画的重叠或者文字相同就不画（跨度很短时只剩首尾）
        var format = (_to - _from).TotalDays < 2 ? "MM-dd HH:mm" : "MM-dd";
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var gap = TextRenderer.MeasureText(" ", _fonts.Small).Width * 2;
        var drawn = new List<(int Left, int Width, string Text)>();
        foreach (var i in new[] { 0, 3, 1, 2 })
        {
            var t = _from + TimeSpan.FromSeconds(span * i / 3);
            var text = t.ToLocalTime().ToString(format, CultureInfo.InvariantCulture);
            var w = TextRenderer.MeasureText(text, _fonts.Small, Size.Empty, flags).Width;
            var x = Math.Clamp(X(t) - w / 2, 0, Math.Max(0, Width - w));
            if (drawn.Any(d => d.Text == text || (x < d.Left + d.Width + gap && d.Left < x + w + gap)))
            {
                continue;
            }

            drawn.Add((x, w, text));
            TextRenderer.DrawText(g, text, _fonts.Small, new Rectangle(x, bar.Bottom + 4, w + 2, textH), Theme.Subtle, flags);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }
}

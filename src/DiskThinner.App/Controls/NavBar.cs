using DiskThinner.Core;

namespace DiskThinner.App.Controls;

/// <summary>左侧导航（原型 .nav）：图标 + 文字，选中项浅蓝底色并带左侧竖条。支持 Tab、方向键、回车和空格。</summary>
internal sealed class NavBar : Panel
{
    private readonly List<NavItem> _items = [];

    public NavBar()
    {
        Dock = DockStyle.Left;
        Width = 188;
        BackColor = Theme.Surface;
        Padding = new Padding(8);
        AccessibleName = L.T("主导航");
        AccessibleRole = AccessibleRole.List;
    }

    public event EventHandler<string>? Selected;

    public string? Current { get; private set; }

    public void AddItem(string key, string text, string glyph)
    {
        // 尺寸按 96 DPI 给出，由窗体的 AutoScaleMode.Dpi 统一缩放
        var item = new NavItem(key, text, glyph)
        {
            Dock = DockStyle.Top,
            Height = 34,
        };
        item.Activated += (_, _) => Select(key);
        item.Navigate += (_, delta) => MoveFocus(item, delta);
        _items.Add(item);
        Controls.Add(item);
        item.BringToFront(); // Dock=Top 时后加的在下面
    }

    public void Select(string key)
    {
        Current = key;
        foreach (var i in _items)
        {
            i.Checked = i.Key == key;
        }

        Selected?.Invoke(this, key);
    }

    private void MoveFocus(NavItem from, int delta)
    {
        var index = _items.IndexOf(from) + delta;
        if (index >= 0 && index < _items.Count)
        {
            _items[index].Focus();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, Width - 1, 0, Width - 1, Height);
    }

    private sealed class NavItem : Control
    {
        private bool _checked;
        private bool _hover;

        private readonly DerivedFonts _fonts = new();
        private Font? _iconFont;

        public NavItem(string key, string text, string glyph)
        {
            Key = key;
            Text = text;
            Glyph = glyph;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Font = Theme.Ui;
            AccessibleName = text;
            AccessibleRole = AccessibleRole.RadioButton;
        }

        /// <summary>图标字体跟随控件字体（已按显示器 DPI 缩放）大小。</summary>
        private Font IconFont
        {
            get
            {
                if (_iconFont is null || Math.Abs(_iconFont.SizeInPoints - Font.SizeInPoints * 10 / 9) > 0.01f)
                {
                    _iconFont?.Dispose();
                    _iconFont = new Font(Theme.Icons.FontFamily, Font.SizeInPoints * 10 / 9, FontStyle.Regular, GraphicsUnit.Point);
                }

                return _iconFont;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _fonts.Dispose();
                _iconFont?.Dispose();
            }

            base.Dispose(disposing);
        }

        public string Key { get; }
        public string Glyph { get; }

        public event EventHandler? Activated;
        public event EventHandler<int>? Navigate;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Checked
        {
            get => _checked;
            set
            {
                _checked = value;
                AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                Invalidate();
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance() => new NavItemAccessible(this);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Theme.Surface);
            var r = new Rectangle(0, 0, Width, Height);
            var back = _checked ? Theme.NavSelected : _hover ? Theme.NavHover : Color.Empty;
            if (back != Color.Empty)
            {
                using var b = new SolidBrush(back);
                g.FillRectangle(b, r);
            }

            if (_checked)
            {
                using var bar = new SolidBrush(Theme.Accent);
                g.FillRectangle(bar, 0, 0, Ui.Scale(this, 3), Height);
            }

            _fonts.Update(Font);
            var color = _checked ? Theme.Accent : ColorTranslator.FromHtml("#333333");
            var pad = Ui.Scale(this, 10);
            var iconBox = new Rectangle(pad, 0, Ui.Scale(this, 18), Height);
            TextRenderer.DrawText(g, Glyph, IconFont, iconBox, color, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            var textBox = Rectangle.FromLTRB(iconBox.Right + Ui.Scale(this, 10), 0, Width - pad, Height);
            TextRenderer.DrawText(g, Text, _checked ? _fonts.Bold : _fonts.Regular, textBox, color, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Focused && ShowFocusCues)
            {
                using var focus = new Pen(Theme.Accent, 2) { Alignment = System.Drawing.Drawing2D.PenAlignment.Inset };
                g.DrawRectangle(focus, 1, 1, Width - 2, Height - 2);
            }
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

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Focus();
            Activated?.Invoke(this, EventArgs.Empty);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Enter:
                case Keys.Space:
                    Activated?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
                case Keys.Up:
                    Navigate?.Invoke(this, -1);
                    e.Handled = true;
                    break;
                case Keys.Down:
                    Navigate?.Invoke(this, 1);
                    e.Handled = true;
                    break;
            }
        }

        private sealed class NavItemAccessible(NavItem owner) : ControlAccessibleObject(owner)
        {
            public override AccessibleStates State =>
                base.State | AccessibleStates.Focusable | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

            public override string DefaultAction => L.T("切换");

            public override void DoDefaultAction() => owner.Activated?.Invoke(owner, EventArgs.Empty);
        }
    }
}

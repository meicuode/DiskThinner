using System.ComponentModel;

namespace DiskThinner.App.Controls;

/// <summary>原型的 .seg：一组互斥的按钮（单选按钮的按钮外观，读屏软件读作单选按钮）。</summary>
internal sealed class SegmentedControl : FlowLayoutPanel
{
    private readonly List<RadioButton> _items = [];
    private int _selected;

    public SegmentedControl(string accessibleName, params string[] items)
    {
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        Padding = new Padding(0, 2, 0, 0);
        Margin = new Padding(12, 0, 0, 0);
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.Grouping;

        for (var i = 0; i < items.Length; i++)
        {
            var index = i;
            // 和 Ui.Chip 一样：按钮外观自动大小时宽度算不准，按 96 DPI 量好固定尺寸，交给窗体统一缩放
            var textWidth = TextRenderer.MeasureText(items[i], Theme.Small).Width;
            var b = new RadioButton
            {
                Text = items[i],
                Appearance = Appearance.Button,
                AutoSize = false,
                Size = new Size(textWidth + 22, 26),
                FlatStyle = FlatStyle.Flat,
                Font = Theme.Small,
                BackColor = Color.White,
                ForeColor = Theme.Text,
                Margin = new Padding(0),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                UseVisualStyleBackColor = false,
                AccessibleName = items[i],
                Checked = i == 0,
                TabStop = i == 0,
            };
            b.FlatAppearance.BorderColor = Theme.BorderStrong;
            b.FlatAppearance.CheckedBackColor = Theme.AccentWeak;
            b.FlatAppearance.MouseOverBackColor = Theme.Background;
            b.CheckedChanged += (_, _) =>
            {
                b.ForeColor = !Enabled ? Theme.Subtle : b.Checked ? Theme.Accent : Theme.Text;
                b.Font = b.Checked ? Theme.SmallBold : Theme.Small;
                if (b.Checked && _selected != index)
                {
                    _selected = index;
                    SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
                }
            };
            _items.Add(b);
            Controls.Add(b);
        }

        if (_items.Count > 0)
        {
            _items[0].ForeColor = Theme.Accent;
            _items[0].Font = Theme.SmallBold;
        }
    }

    public event EventHandler? SelectedIndexChanged;

    /// <summary>平面按钮在禁用时文字不变灰（文字颜色是自己设的）：禁用时自己换成浅色。</summary>
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        foreach (var b in _items)
        {
            b.ForeColor = !Enabled ? Theme.Subtle : b.Checked ? Theme.Accent : Theme.Text;
            b.Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value >= 0 && value < _items.Count)
            {
                _items[value].Checked = true;
            }
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ItemsEnabled
    {
        get => _items.All(i => i.Enabled);
        set
        {
            foreach (var i in _items)
            {
                i.Enabled = value;
            }
        }
    }
}

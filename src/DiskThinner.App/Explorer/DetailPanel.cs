using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Formatting;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 右侧详情（原型 .ex-detail）。M1 显示大小、文件数、时间、属性和"打开所在位置"；
/// 归属、类别、作用、证据和清理建议在 M2 规则上线后补上。
/// </summary>
internal sealed class DetailPanel : Panel
{
    private readonly FlowLayoutPanel _content;
    private NodeTree? _tree;
    private int _node = -1;
    private ScanStats? _stats;

    public DetailPanel()
    {
        AutoScroll = true;
        BackColor = Theme.Surface;
        Padding = new Padding(16, 14, 16, 14);
        AccessibleName = "详情";
        _content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(Padding.Left, Padding.Top),
            BackColor = Color.Transparent,
        };
        Controls.Add(_content);
    }

    public event EventHandler<int>? OpenLocationRequested;
    public event EventHandler<int>? CopyPathRequested;
    public event EventHandler<int>? ShowInMapRequested;

    private int ContentWidth => Math.Max(120, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

    /// <summary>当前显示的节点（测试用）。</summary>
    internal int ShownNode => _node;

    public void ShowNode(NodeTree? tree, int node, ScanStats? stats)
    {
        _tree = tree;
        _node = node;
        _stats = stats;
        Rebuild();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (_content.Controls.Count > 0 && Math.Abs(_content.Controls[0].Width - ContentWidth) > 8)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        SuspendLayout();
        _content.SuspendLayout();
        foreach (Control c in _content.Controls)
        {
            c.Dispose();
        }

        _content.Controls.Clear();
        AutoScrollPosition = Point.Empty;
        _content.Location = new Point(Padding.Left, Padding.Top);

        if (_tree is null || _node < 0)
        {
            _content.Controls.Add(Wrap(Ui.Label("选中左侧的目录或文件查看详情。", Theme.Ui, Theme.Muted)));
        }
        else
        {
            BuildFor(_tree, _node);
        }

        _content.ResumeLayout();
        ResumeLayout();
    }

    private void BuildFor(NodeTree tree, int n)
    {
        ref var node = ref tree[n];
        var isDir = node.IsDirectory;
        var path = tree.GetPath(n);
        var width = ContentWidth;

        // 标题 + 属性标签
        var title = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(width, 0), Width = width, Margin = new Padding(0), BackColor = Color.Transparent };
        title.Controls.Add(new Label
        {
            Text = TreeListView.DisplayName(tree, n),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Font = Theme.Title,
            ForeColor = Theme.Text,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 2),
        });
        foreach (var tag in FlagTags(node))
        {
            title.Controls.Add(Ui.Tag(tag, Theme.TagBack, Theme.TagText));
        }

        _content.Controls.Add(title);

        // 路径：可以选中复制
        var pathBox = new TextBox
        {
            Text = path,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Muted,
            Font = Theme.Mono,
            Multiline = true,
            WordWrap = true,
            Width = width,
            TabStop = true,
            AccessibleName = "完整路径",
            Margin = new Padding(0, 2, 0, 8),
        };
        _content.Controls.Add(pathBox); // 先加入，拿到按 DPI 缩放后的字体再量高度
        var textSize = TextRenderer.MeasureText(path, pathBox.Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        pathBox.Height = textSize.Height + 4;

        // 键值表
        var kv = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = width, Margin = new Padding(0), BackColor = Color.Transparent };
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Scale(this, 76)));
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var valueWidth = width - Ui.Scale(this, 80);
        void Row(string key, string value, Font? font = null)
        {
            kv.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui, Margin = new Padding(0, 3, 4, 3), UseMnemonic = false });
            kv.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(valueWidth, 0), ForeColor = Theme.Text, Font = font ?? Theme.Ui, Margin = new Padding(0, 3, 0, 3), UseMnemonic = false });
        }

        var size = ByteSize.Format(node.AllocSize);
        Row("大小", isDir ? $"{size} · {node.FileCount.ToString("N0", CultureInfo.InvariantCulture)} 个文件" : size, Theme.UiBold);
        if (isDir)
        {
            Row("子目录", node.DirCount.ToString("N0", CultureInfo.InvariantCulture) + " 个");
        }

        var diff = node.LogicalSize - node.AllocSize;
        if (Math.Abs(diff) > 1 << 20 && Math.Abs(diff) > node.AllocSize / 100)
        {
            Row("逻辑大小", ByteSize.Format(node.LogicalSize) + (diff > 0 ? "（压缩、稀疏或未下载到本地，实际占用更小）" : "（按簇分配，实际占用更大）"));
        }

        if (n != tree.Root)
        {
            Row("占上级", TreeListView.PercentOfParent(tree, n).ToString("0.0", CultureInfo.InvariantCulture) + "%");
        }

        Row(isDir ? "最后写入" : "修改时间", TimeText.Full(node.LastWriteUtc) + (isDir ? "（含子项）" : ""));
        _content.Controls.Add(kv);

        // 说明
        foreach (var note in Notes(node, isDir))
        {
            _content.Controls.Add(Callout(note, width));
        }

        _content.Controls.Add(Wrap(Ui.Label("归属、类别、作用和清理建议会在规则上线（M2）后显示在这里。", Theme.Small, Theme.Subtle), new Padding(0, 10, 0, 0)));

        // 操作
        _content.Controls.Add(SectionTitle("操作"));
        var open = Ui.Button(isDir ? "打开所在位置，自己清理" : "在资源管理器中显示，自己清理",
            accessibleDescription: "用资源管理器打开，DiskThinner 不会删除任何东西");
        open.Click += (_, _) => OpenLocationRequested?.Invoke(this, n);
        _content.Controls.Add(Stretch(open, width));

        var copy = Ui.Button("复制路径");
        copy.Click += (_, _) => CopyPathRequested?.Invoke(this, n);
        _content.Controls.Add(Stretch(copy, width));

        if (isDir && node.FirstChild >= 0)
        {
            var map = Ui.Button("在方块图中只显示这个目录");
            map.Click += (_, _) => ShowInMapRequested?.Invoke(this, n);
            _content.Controls.Add(Stretch(map, width));
        }
    }

    private IEnumerable<string> Notes(Node node, bool isDir)
    {
        var f = node.Flags;
        if ((f & NodeFlags.Metadata) != 0)
        {
            yield return "NTFS 的内部文件，由文件系统自己管理，不能删除。";
        }

        if ((f & NodeFlags.HardLinkSecondary) != 0)
        {
            yield return "这是同一个文件的另一个硬链接名，大小已经算在别处；只删这个名字不会释放空间。";
        }
        else if ((f & NodeFlags.HardLinked) != 0)
        {
            yield return "这个文件在别的目录里还有硬链接名（大小算在这里）。只删其中一个名字不会释放空间。";
        }

        if ((f & NodeFlags.AccessDenied) != 0)
        {
            yield return "没有权限列出这个目录的内容，显示的大小偏小。以管理员身份重启后用 MFT 扫描可以看到。";
        }
        else if ((f & NodeFlags.Incomplete) != 0)
        {
            yield return "这个目录下有没权限列出的子目录，显示的大小偏小。";
        }

        if ((f & NodeFlags.ReparsePoint) != 0 && isDir)
        {
            yield return "这是 junction 或符号链接，指向别的位置，没有展开计算，大小为 0。";
        }

        if ((f & NodeFlags.CloudPlaceholder) != 0)
        {
            yield return "云文件占位符（如 OneDrive 按需下载），内容不在本地，不占空间。";
        }

        if (_stats?.Mode == ScanMode.Walk && (isDir || node.AllocSize > 0))
        {
            yield return "当前是遍历扫描：占用按簇估算，正在写入的文件可能偏小，硬链接会重复计算。";
        }
    }

    private static IEnumerable<string> FlagTags(Node node)
    {
        var f = node.Flags;
        if ((f & NodeFlags.Metadata) != 0) yield return "NTFS 元数据";
        if ((f & NodeFlags.System) != 0) yield return "系统";
        if ((f & NodeFlags.Hidden) != 0) yield return "隐藏";
        if ((f & NodeFlags.ReparsePoint) != 0) yield return "链接";
        if ((f & NodeFlags.HardLinked) != 0) yield return "硬链接";
        if ((f & NodeFlags.Compressed) != 0) yield return "压缩";
        if ((f & NodeFlags.Sparse) != 0) yield return "稀疏";
        if ((f & NodeFlags.CloudPlaceholder) != 0) yield return "云文件";
    }

    private Control SectionTitle(string text)
    {
        var l = Ui.Label(text, Theme.SmallBold, Theme.Muted);
        l.Margin = new Padding(0, 16, 0, 6);
        return l;
    }

    private Control Wrap(Label label, Padding? margin = null)
    {
        label.MaximumSize = new Size(ContentWidth, 0);
        label.Margin = margin ?? new Padding(0);
        return label;
    }

    private Control Callout(string text, int width)
    {
        var panel = new CalloutPanel
        {
            Width = width,
            Padding = new Padding(Ui.Scale(this, 10), 6, 8, 6),
            Margin = new Padding(0, 8, 0, 0),
            BackColor = Theme.WarnBack,
        };
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(width - panel.Padding.Horizontal, 0),
            Font = Theme.Small,
            ForeColor = Theme.WarnText,
            Location = new Point(panel.Padding.Left, panel.Padding.Top),
            UseMnemonic = false,
        };
        panel.Controls.Add(label);
        panel.Height = label.PreferredHeight + panel.Padding.Vertical;
        return panel;
    }

    private static Control Stretch(Button b, int width)
    {
        b.AutoSize = false;
        b.Width = width;
        b.Height = Math.Max(28, b.PreferredSize.Height);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Margin = new Padding(0, 0, 0, 6);
        return b;
    }

    /// <summary>原型 .callout.warn：左侧 3 像素黄色竖条。</summary>
    private sealed class CalloutPanel : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var bar = new SolidBrush(Theme.WarnBorder);
            e.Graphics.FillRectangle(bar, 0, 0, Ui.Scale(this, 3), Height);
        }
    }
}

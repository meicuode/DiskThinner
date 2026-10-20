using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 空间浏览页（原型 .p-explorer）：顶部标题和面包屑，左上树形列表，左下方块图，右侧详情。三者联动选中。
/// 扫描期间显示进度遮罩；遍历扫描时在顶部提示结果为估算，并提供"以管理员身份重启"。
/// </summary>
internal sealed class ExplorerPage : UserControl
{
    private readonly Breadcrumb _crumb = new() { Dock = DockStyle.Fill };
    private readonly TreeListView _list = new() { Dock = DockStyle.Fill };
    private readonly TreemapView _map = new() { Dock = DockStyle.Fill };
    private readonly DetailPanel _detail = new() { Dock = DockStyle.Fill };
    private readonly Label _mapScope;
    private readonly Button _zoomOut;
    private readonly Panel _banner;
    private readonly Label _bannerText;
    private readonly ToolTip _bannerTip = new();
    private readonly ProgressOverlay _overlay = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly SplitContainer _outer;
    private readonly SplitContainer _left;
    private readonly FlowLayoutPanel _legend;
    private readonly CheckBox _onlyCleanable = Ui.Chip("只看可清理");
    private readonly CheckBox _onlyUnknown = Ui.Chip("只看未识别");
    private ScanResult? _result;
    private DiskThinner.Rules.Classification? _classification;
    private int _selected = -1;

    public ExplorerPage()
    {
        BackColor = Theme.Background;
        Font = Theme.Ui;

        // 顶部：标题 + 面包屑
        var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Surface, Padding = new Padding(12, 6, 12, 6) };
        var title = Ui.Label("空间浏览", Theme.PageTitle);
        title.Dock = DockStyle.Left;
        title.Padding = new Padding(0, 4, 12, 0);
        var chips = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 0) };
        chips.Controls.Add(_onlyCleanable);
        chips.Controls.Add(_onlyUnknown);
        _onlyCleanable.Enabled = _onlyUnknown.Enabled = false;
        _onlyCleanable.CheckedChanged += (_, _) => OnFilterChanged(_onlyCleanable);
        _onlyUnknown.CheckedChanged += (_, _) => OnFilterChanged(_onlyUnknown);
        bar.Controls.Add(_crumb);
        bar.Controls.Add(chips);
        bar.Controls.Add(title);
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        // 遍历扫描提示条（原型 .callout.warn）
        _banner = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.WarnBack, Visible = false, Padding = new Padding(14, 0, 10, 0) };
        _bannerText = new Label { Dock = DockStyle.Fill, ForeColor = Theme.WarnText, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true };
        var restart = Ui.Button("以管理员身份重启");
        restart.Dock = DockStyle.Right;
        restart.Image = SystemIcons.GetStockIcon(StockIconId.Shield, 16).ToBitmap();
        restart.ImageAlign = ContentAlignment.MiddleLeft;
        restart.TextImageRelation = TextImageRelation.ImageBeforeText;
        restart.Click += (_, _) => RestartRequested?.Invoke(this, EventArgs.Empty);
        _banner.Controls.Add(_bannerText);
        _banner.Controls.Add(restart);
        _banner.Paint += (_, e) =>
        {
            using var b = new SolidBrush(Theme.WarnBorder);
            e.Graphics.FillRectangle(b, 0, 0, Ui.Scale(this, 3), _banner.Height);
        };

        // 方块图上方的图例和范围
        var legend = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = false, BackColor = Color.White, Padding = new Padding(8, 4, 8, 0) };
        _legend = legend;
        BuildLegend(byRule: false);

        var mapHead = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.White, Padding = new Padding(8, 2, 8, 2) };
        _zoomOut = Ui.Button("返回上一级");
        _zoomOut.Dock = DockStyle.Right;
        _zoomOut.Enabled = false;
        _zoomOut.Click += (_, _) => _map.ZoomOut();
        _mapScope = new Label { Dock = DockStyle.Fill, Font = Theme.Small, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false };
        mapHead.Controls.Add(_mapScope);
        mapHead.Controls.Add(_zoomOut);

        var mapPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(8, 0, 8, 8) };
        mapPanel.Controls.Add(_map);
        mapPanel.Controls.Add(mapHead);
        mapPanel.Controls.Add(legend);

        _left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2, BackColor = Theme.Border, SplitterWidth = 4 };
        _left.Panel1.Controls.Add(_list);
        _left.Panel2.Controls.Add(mapPanel);

        _outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel2, BackColor = Theme.Border, SplitterWidth = 4 };
        _outer.Panel1.Controls.Add(_left);
        _outer.Panel2.Controls.Add(_detail);

        Controls.Add(_outer);
        Controls.Add(_overlay);
        Controls.Add(_banner);
        Controls.Add(bar);
        _overlay.BringToFront();

        _list.NodeSelected += (_, n) => SelectFrom(n, _list);
        _list.CopyPathRequested += (_, n) => CopyPath(n);
        _map.NodeSelected += (_, n) => SelectFrom(n, _map);
        _map.MapRootChanged += (_, _) => UpdateMapScope();
        _crumb.NodeClicked += (_, n) => SelectFrom(n, _crumb);
        _detail.OpenLocationRequested += (_, n) => OpenLocation(n);
        _detail.CopyPathRequested += (_, n) => CopyPath(n);
        _detail.ShowInMapRequested += (_, n) => _map.SetMapRoot(n);
        _detail.SelectRequested += (_, n) => SelectFrom(n, null);
        _overlay.CancelRequested += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开所在位置，自己清理", null, (_, _) => OpenLocation(_list.SelectedNode));
        menu.Items.Add("复制路径", null, (_, _) => CopyPath(_list.SelectedNode));
        menu.Items.Add("在方块图中只显示这个目录", null, (_, _) => _map.SetMapRoot(_list.SelectedNode));
        _list.ContextMenuStrip = menu;
    }

    public event EventHandler? RestartRequested;
    public event EventHandler? CancelRequested;

    public int SelectedNode => _selected;

    // 测试用：直接驱动各部分
    internal TreeListView List => _list;

    internal Breadcrumb Crumb => _crumb;

    internal DetailPanel Detail => _detail;

    public TreemapView Map => _map;

    public ScanResult? Result => _result;

    private bool _splitInitialized;

    /// <summary>第一次拿到实际尺寸时，按原型设置：详情栏 340 像素宽，方块图区域约 270 像素高。之后由用户拖动。</summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_splitInitialized || _outer.Width < 700 || _left.Height < 400)
        {
            return;
        }

        _splitInitialized = true;
        _outer.SplitterDistance = Math.Max(300, _outer.Width - Ui.Scale(this, 340) - _outer.SplitterWidth);
        _left.SplitterDistance = Math.Max(150, _left.Height - Ui.Scale(this, 270) - _left.SplitterWidth);
    }

    public void ShowProgress(string title, string detail, int percent, bool cancellable)
    {
        _overlay.SetProgress(title, detail, percent, cancellable);
        _overlay.Visible = true;
        _overlay.BringToFront();
    }

    public void HideProgress() => _overlay.Visible = false;

    public void SetResult(ScanResult? result, DiskThinner.Rules.Classification? classification = null)
    {
        _result = result;
        _classification = result is null ? null : classification;
        _overlay.Visible = false;
        _onlyCleanable.Checked = _onlyUnknown.Checked = false;
        _onlyCleanable.Enabled = _onlyUnknown.Enabled = _classification is not null;
        _list.SetTree(result?.Tree, _classification);
        _map.SetTree(result?.Tree, _classification);
        BuildLegend(byRule: _classification is not null);
        UpdateBanner();
        var first = result is null ? -1 : result.Tree.Root;
        SelectFrom(first, null);
    }

    public DiskThinner.Rules.Classification? Classification => _classification;

    internal CheckBox OnlyCleanable => _onlyCleanable;

    internal CheckBox OnlyUnknown => _onlyUnknown;

    /// <summary>"只看可清理"和"只看未识别"互斥：勾选一个时取消另一个。</summary>
    private void OnFilterChanged(CheckBox changed)
    {
        if (changed.Checked)
        {
            var other = changed == _onlyCleanable ? _onlyUnknown : _onlyCleanable;
            if (other.Checked)
            {
                other.Checked = false; // 会再次进入这里，但 Checked 为 false 时只重新应用过滤
            }
        }

        var c = _classification;
        Func<int, bool>? filter = c is null ? null
            : _onlyCleanable.Checked ? n => c.Cleanable(n) > 0
            : _onlyUnknown.Checked ? n => c.Unexplained(n) > 0
            : null;
        _list.SetFilter(filter);
        if (_list.SelectedNode >= 0 && _list.SelectedNode != _selected)
        {
            SelectFrom(_list.SelectedNode, _list);
        }
    }

    private void BuildLegend(bool byRule)
    {
        _legend.SuspendLayout();
        _legend.Controls.Clear();
        void Item(Color color, string text)
        {
            _legend.Controls.Add(new LegendSwatch(color));
            _legend.Controls.Add(new Label { Text = text, AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, Margin = new Padding(0, 2, 10, 0) });
        }

        if (byRule)
        {
            foreach (var g in DiskThinner.App.Rules.RuleView.LegendOrder)
            {
                Item(DiskThinner.App.Rules.RuleView.ColorOf(g), DiskThinner.App.Rules.RuleView.NameOf(g));
            }
        }
        else
        {
            _legend.Controls.Add(new Label { Text = "按文件类型：", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Subtle, Margin = new Padding(0, 2, 6, 0) });
            foreach (var kind in FileKinds.LegendOrder)
            {
                Item(FileKinds.ColorOf(kind), FileKinds.NameOf(kind));
            }
        }

        _legend.ResumeLayout();
    }

    /// <summary>按路径选中（命令行 --select 用）。</summary>
    public bool SelectPath(string path)
    {
        if (_result is null)
        {
            return false;
        }

        var n = _result.Tree.FindPath(path);
        if (n >= 0)
        {
            SelectFrom(n, null);
        }

        return n >= 0;
    }

    /// <summary>从概览等页面跳过来时用：选中节点，被过滤掉时先取消过滤。</summary>
    public void Reveal(int node) => SelectFrom(node, null);

    private void SelectFrom(int node, object? source)
    {
        _selected = node;
        var tree = _result?.Tree;
        if (!ReferenceEquals(source, _list) && !_list.SelectNode(node) && node >= 0 && _list.HasFilter)
        {
            // 要选中的节点被"只看可清理/未识别"过滤掉了：取消过滤再选
            _onlyCleanable.Checked = _onlyUnknown.Checked = false;
            _selected = node;
            _list.SelectNode(node);
        }

        if (!ReferenceEquals(source, _map))
        {
            _map.SelectNode(node);
        }

        _crumb.Show(tree, node);
        _detail.ShowNode(tree, node, _result?.Stats, _classification);
    }

    private void UpdateMapScope()
    {
        var tree = _result?.Tree;
        if (tree is null || _map.MapRoot < 0)
        {
            _mapScope.Text = "";
            _zoomOut.Enabled = false;
            return;
        }

        var root = _map.MapRoot;
        _mapScope.Text = $"方块图范围：{tree.GetPath(root)}（{ByteSize.Format(tree[root].AllocSize)}）· 点击定位，双击进入下一级";
        _zoomOut.Enabled = root != tree.Root;
    }

    private void UpdateBanner()
    {
        var stats = _result?.Stats;
        if (stats is null || stats.Mode == ScanMode.Mft)
        {
            _banner.Visible = false;
            return;
        }

        var reason = string.IsNullOrEmpty(stats.MftUnavailableReason) ? "" : $"（{stats.MftUnavailableReason}）";
        _bannerText.Text = $"遍历扫描{reason}：结果为估算。以管理员身份运行可改用 MFT 扫描，几秒完成。";
        _bannerTip.SetToolTip(_bannerText, "遍历扫描较慢；占用按簇估算；正在写入的文件大小可能偏小；硬链接会重复计算。");
        _banner.Visible = true;
    }

    private void OpenLocation(int node)
    {
        var tree = _result?.Tree;
        if (tree is null || node < 0)
        {
            return;
        }

        ShellActions.OpenLocation(tree.GetPath(node), tree[node].IsDirectory, FindForm());
    }

    private void CopyPath(int node)
    {
        var tree = _result?.Tree;
        if (tree is not null && node >= 0)
        {
            ShellActions.CopyPath(tree.GetPath(node));
        }
    }

    private sealed class LegendSwatch : Control
    {
        public LegendSwatch(Color color)
        {
            BackColor = color;
            Size = new Size(10, 10);
            Margin = new Padding(0, 5, 4, 0);
            AccessibleRole = AccessibleRole.Graphic;
        }
    }

    /// <summary>扫描进度遮罩：标题、说明、进度条、取消按钮。</summary>
    private sealed class ProgressOverlay : Panel
    {
        private readonly Label _title = new() { AutoSize = true, Font = Theme.Title, ForeColor = Theme.Text, UseMnemonic = false };
        private readonly Label _detail = new() { AutoSize = true, Font = Theme.Ui, ForeColor = Theme.Muted, UseMnemonic = false };
        private readonly ProgressBar _bar = new() { Width = 420, Height = 8, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
        private readonly Button _cancel = Ui.Button("取消扫描");
        private readonly Panel _box = new() { BackColor = Color.White, Size = new Size(480, 150) };

        public ProgressOverlay()
        {
            BackColor = Theme.Background;
            _box.Controls.AddRange([_title, _detail, _bar, _cancel]);
            Controls.Add(_box);
            _cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
            _box.Paint += (_, e) =>
            {
                using var pen = new Pen(Theme.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, _box.Width - 1, _box.Height - 1);
            };
            AccessibleName = "扫描进度";
            _bar.AccessibleName = "扫描进度";
        }

        public event EventHandler? CancelRequested;

        public void SetProgress(string title, string detail, int percent, bool cancellable)
        {
            _title.Text = title;
            _detail.Text = detail;
            _bar.Value = Math.Clamp(percent, 0, 100);
            _cancel.Visible = cancellable;
            LayoutBox();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutBox();
        }

        private void LayoutBox()
        {
            var s = DeviceDpi / 96f;
            _box.Size = new Size((int)(480 * s), (int)(150 * s));
            _box.Location = new Point(Math.Max(0, (Width - _box.Width) / 2), Math.Max(0, (Height - _box.Height) / 3));
            var pad = (int)(20 * s);
            _title.Location = new Point(pad, pad);
            _detail.Location = new Point(pad, _title.Bottom + (int)(6 * s));
            _bar.Location = new Point(pad, _detail.Bottom + (int)(12 * s));
            _bar.Width = _box.Width - pad * 2;
            _cancel.Location = new Point(_box.Width - pad - _cancel.Width, _box.Height - pad - _cancel.Height + (int)(6 * s));
        }
    }

    internal static string Describe(ScanStats stats)
    {
        var files = stats.Nodes.ToString("N0", CultureInfo.InvariantCulture);
        var mode = stats.Mode == ScanMode.Mft ? "MFT 扫描" : "遍历扫描（估算）";
        return $"{stats.Root.TrimEnd('\\')} · {mode} {files} 项，用时 {stats.TotalDuration.TotalSeconds:F1} 秒";
    }
}

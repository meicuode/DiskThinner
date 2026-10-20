using System.Diagnostics;
using System.Globalization;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace UiBench;

/// <summary>三栏布局的简化版：上方树形列表、下方方块图、右侧详情。--bench 时自动执行测量脚本并退出。</summary>
internal sealed class BenchForm : Form
{
    private readonly NodeTree _tree;
    private readonly TreeListView _list = new() { Dock = DockStyle.Fill };
    private readonly TreemapControl _map = new() { Dock = DockStyle.Fill };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, Padding = new Padding(8), AccessibleName = "详情" };
    private readonly string? _benchOut;
    private readonly List<string> _results;

    public BenchForm(NodeTree tree, string? benchOut, List<string> results)
    {
        _tree = tree;
        _benchOut = benchOut;
        _results = results;

        Text = $"UiBench：{tree.Count:N0} 个节点";
        Font = new Font("Microsoft YaHei UI", 9F);
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 1060 };
        var left = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 470 };
        left.Panel1.Controls.Add(_list);
        left.Panel2.Controls.Add(_map);
        right.Panel1.Controls.Add(left);
        right.Panel2.Controls.Add(_detail);
        Controls.Add(right);

        _list.NodeSelected += (_, n) => ShowDetail(n);
        _map.NodeClicked += (_, n) =>
        {
            _list.Reveal(n);
            ShowDetail(n);
        };

        Shown += OnShown;
    }

    private void ShowDetail(int n)
    {
        ref var node = ref _tree[n];
        _detail.Text = $"{_tree.GetPath(n)}\n\n大小 {ByteSize.Format(node.AllocSize)}\n文件 {node.FileCount:N0} 个\n目录 {node.DirCount:N0} 个";
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        var sw = Stopwatch.StartNew();
        _list.SetTree(_tree);
        _map.SetRoot(_tree, _tree.Root);
        Record("initial_bind_ms", sw.Elapsed.TotalMilliseconds);

        if (_benchOut is null)
        {
            return;
        }

        await Task.Delay(500);
        RunBench();

        // 截图前回到"只展开根目录、选中最大子项"的日常状态
        _list.Collapse(0);
        _list.Expand(0);
        _map.Rebuild();
        if (_tree[_tree.Root].FirstChild >= 0)
        {
            _list.Reveal(_tree[_tree.Root].FirstChild);
            ShowDetail(_tree[_tree.Root].FirstChild);
        }

        _list.EnsureVisible(0);
        Refresh();
        await Task.Delay(300);
        SaveScreenshot(Path.GetFileNameWithoutExtension(_benchOut) + ".png");
        File.WriteAllLines(_benchOut, _results);
        Close();
    }

    private void RunBench()
    {
        var random = new Random(7);

        // 1. 全部展开：可见行数 = 节点总数
        var sw = Stopwatch.StartNew();
        _list.ExpandAll();
        _list.Update();
        Record("expand_all_ms", sw.Elapsed.TotalMilliseconds);
        Record("rows_after_expand_all", _list.RowCount);

        // 2. 随机跳转并同步重绘
        var samples = new List<double>();
        for (var i = 0; i < 200; i++)
        {
            var row = random.Next(_list.RowCount);
            sw.Restart();
            _list.EnsureVisible(row);
            _list.Update();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        RecordSamples("random_jump_paint", samples);

        // 3. 连续翻页
        samples.Clear();
        var top = 0;
        for (var i = 0; i < 200; i++)
        {
            top = Math.Min(_list.RowCount - 1, top + 40);
            sw.Restart();
            _list.EnsureVisible(top);
            _list.Update();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        RecordSamples("page_down_paint", samples);

        // 4. 折叠根（移除全部行）再展开根
        sw.Restart();
        _list.Collapse(0);
        _list.Update();
        Record("collapse_root_ms", sw.Elapsed.TotalMilliseconds);
        sw.Restart();
        _list.Expand(0);
        _list.Update();
        Record("expand_root_ms", sw.Elapsed.TotalMilliseconds);

        // 5. 方块图：布局与绘制
        samples.Clear();
        var renderSamples = new List<double>();
        var rects = 0;
        for (var i = 0; i < 10; i++)
        {
            sw.Restart();
            rects = _map.LayoutRects();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            sw.Restart();
            using var bmp = _map.Render(_map.Width, _map.Height);
            renderSamples.Add(sw.Elapsed.TotalMilliseconds);
        }

        Record("treemap_size", $"{_map.Width}x{_map.Height}");
        Record("treemap_rects", rects);
        RecordSamples("treemap_layout", samples);
        RecordSamples("treemap_render", renderSamples);
        _map.Rebuild();

        // 6. 方块图点击 → 列表定位（展开祖先 + 找行）
        samples.Clear();
        _list.Collapse(0);
        _list.Expand(0);
        for (var i = 0; i < 50; i++)
        {
            var node = random.Next(_tree.Count);
            sw.Restart();
            _list.Reveal(node);
            _list.Update();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        RecordSamples("reveal_node", samples);
        Record("rows_after_reveal", _list.RowCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var proc = Process.GetCurrentProcess();
        proc.Refresh();
        Record("managed_heap_bytes", GC.GetTotalMemory(false));
        Record("working_set_bytes", proc.WorkingSet64);
        Record("peak_working_set_bytes", proc.PeakWorkingSet64);
        Record("tree_bytes", _tree.ApproximateBytes);
    }

    private void SaveScreenshot(string fileName)
    {
        var dir = Path.GetDirectoryName(_benchOut!)!;
        var bounds = RectangleToScreen(ClientRectangle);
        using var bmp = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        bmp.Save(Path.Combine(dir, fileName));
    }

    private void Record(string key, object value) =>
        _results.Add($"{key}={Convert.ToString(value, CultureInfo.InvariantCulture)}");

    private void RecordSamples(string key, List<double> samples)
    {
        var sorted = samples.Order().ToList();
        Record(key + "_avg_ms", sorted.Average().ToString("F2", CultureInfo.InvariantCulture));
        Record(key + "_p95_ms", sorted[(int)(sorted.Count * 0.95) - 1].ToString("F2", CultureInfo.InvariantCulture));
        Record(key + "_max_ms", sorted[^1].ToString("F2", CultureInfo.InvariantCulture));
    }
}

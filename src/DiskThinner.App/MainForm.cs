using System.Diagnostics;
using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Explorer;
using DiskThinner.App.Pages;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App;

/// <summary>主窗口（原型 .shell）：工具栏、左侧导航、页面区、状态栏；负责启动和取消扫描。</summary>
internal sealed class MainForm : Form
{
    private readonly AppOptions _options;
    private readonly NavBar _nav = new();
    private readonly Panel _pageHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Control> _pages = [];
    private readonly ExplorerPage _explorer = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _drives = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, AccessibleName = "选择磁盘" };
    private readonly Button _rescan = Ui.Button("重新扫描", primary: true);
    private readonly ToolStripStatusLabel _statusScan = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusMode = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly bool _elevated = VolumeScanner.IsElevated();
    private CancellationTokenSource? _scanCts;
    private bool _suppressDriveChange;
    private string? _currentDrive;

    public MainForm(AppOptions options)
    {
        _options = options;

        // 所有在构造函数里给出的尺寸都按 96 DPI 写，由 WinForms 按窗口所在显示器的 DPI 统一缩放。
        // 和设计器生成的代码一样，必须包在 SuspendLayout / ResumeLayout 里，自动缩放才会在布局恢复时执行。
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "DiskThinner";
        Font = Theme.Ui;
        BackColor = Theme.Background;
        MinimumSize = new Size(1100, 680);
        Size = new Size(1440, 900);
        StartPosition = FormStartPosition.CenterScreen;
        if (options.PrimaryScreen && Screen.PrimaryScreen is { } primary)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(primary.WorkingArea.Left + 40, primary.WorkingArea.Top + 40);
        }

        KeyPreview = true;

        Controls.Add(_pageHost);
        Controls.Add(_nav);
        Controls.Add(BuildToolbar());
        Controls.Add(BuildStatusBar());

        _nav.AddItem("overview", "概览", "\uE80A");
        _nav.AddItem("explorer", "空间浏览", "\uE8B7");
        _nav.AddItem("growth", "增长分析", "\uE9D2");
        _nav.AddItem("cleanup", "清理计划", "\uE74D");
        _nav.AddItem("log", "操作记录", "\uE81C");
        _nav.AddItem("settings", "设置", "\uE713");
        _nav.Selected += (_, key) => ShowPage(key);

        _pages["explorer"] = _explorer;
        _pages["overview"] = new PlaceholderPage("概览", "M2 和 M3", "还剩多少空间、近 7 天涨了多少、能清多少、还有多少不认识，以及建议先处理的项目。");
        _pages["growth"] = new PlaceholderPage("增长分析", "M3", "对比两次快照，找出哪些目录在变大。");
        _pages["cleanup"] = new PlaceholderPage("清理计划", "M4", "按风险分组的清理清单，确认后执行。");
        _pages["log"] = new PlaceholderPage("操作记录", "M4", "每次删除、运行命令、手动清理和拦截的记录。");
        _pages["settings"] = new PlaceholderPage("设置", "M5", "快照、清理、保护名单和规则设置。");
        foreach (var page in _pages.Values)
        {
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _pageHost.Controls.Add(page);
        }

        _explorer.RestartRequested += (_, _) => RestartElevated();
        _explorer.CancelRequested += (_, _) => _scanCts?.Cancel();
        _rescan.Click += (_, _) => StartScan(SelectedDrive());
        _drives.SelectedIndexChanged += (_, _) =>
        {
            if (!_suppressDriveChange && SelectedDrive() is { } d && d != _currentDrive)
            {
                StartScan(d);
            }
        };

        // M1 只有空间浏览可用，默认打开它
        _nav.Select("explorer");

        ResumeLayout(false);
        PerformLayout();
    }

    private Control BuildToolbar()
    {
        // "以管理员身份重启"放在空间浏览页的遍历扫描提示条里，那里能说清楚为什么需要
        var bar = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Surface, Padding = new Padding(12, 0, 12, 0) };
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 9, 0, 0) };
        _drives.Font = Theme.Ui;
        _drives.Margin = new Padding(0, 1, 8, 0);
        _rescan.Margin = new Padding(0, 0, 8, 0);
        left.Controls.Add(_drives);
        left.Controls.Add(_rescan);
        bar.Controls.Add(left);
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };
        return bar;
    }

    private StatusStrip BuildStatusBar()
    {
        var status = new StatusStrip { BackColor = Theme.TitleBar, SizingGrip = true, Font = Theme.Small };
        _statusMode.Text = _elevated ? "管理员模式" : "普通权限（MFT 快速扫描需要管理员）";
        status.Items.Add(_statusScan);
        status.Items.Add(_statusMode);
        return status;
    }

    private void ShowPage(string key)
    {
        foreach (var (k, page) in _pages)
        {
            page.Visible = k == key;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LoadDrives();
        var drive = _options.Drive ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        SelectDriveInCombo(drive);
        StartScan(drive);
    }

    private sealed record DriveItem(string Root, string Text)
    {
        public override string ToString() => Text;
    }

    private void LoadDrives()
    {
        _suppressDriveChange = true;
        _drives.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel;
                _drives.Items.Add(new DriveItem(d.RootDirectory.FullName,
                    $"{d.Name.TrimEnd('\\')} {label} · 可用 {ByteSize.Format(d.AvailableFreeSpace)} / {ByteSize.Format(d.TotalSize)}"));
            }
            catch (IOException)
            {
                // 读取磁盘信息失败（例如设备刚拔出）时跳过
            }
        }

        _suppressDriveChange = false;
    }

    private void SelectDriveInCombo(string target)
    {
        _suppressDriveChange = true;
        var index = -1;
        for (var i = 0; i < _drives.Items.Count; i++)
        {
            if (string.Equals(((DriveItem)_drives.Items[i]!).Root, target, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            // 扫描的是文件夹（命令行 --scan 指定），单独列一项
            _drives.Items.Insert(0, new DriveItem(target, $"{target.TrimEnd('\\')}（文件夹）"));
            index = 0;
        }

        _drives.SelectedIndex = index;
        _suppressDriveChange = false;
    }

    private string SelectedDrive() =>
        (_drives.SelectedItem as DriveItem)?.Root ?? _currentDrive ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    private async void StartScan(string drive)
    {
        _scanCts?.Cancel();
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        _currentDrive = drive;
        var previous = _explorer.Result;

        var preference = _options.ForceWalk ? ScanPreference.Walk : ScanPreference.Auto;
        var (mode, reason) = VolumeScanner.Plan(drive, preference);
        long usedBytes = 1;
        try
        {
            var info = new DriveInfo(drive);
            usedBytes = Math.Max(1, info.TotalSize - info.TotalFreeSpace);
        }
        catch (IOException)
        {
        }

        var title = mode == ScanMode.Mft ? $"正在读取 {drive.TrimEnd('\\')} 的主文件表（MFT）" : $"正在逐个目录扫描 {drive.TrimEnd('\\')}";
        var hint = mode == ScanMode.Mft ? "通常只需要几秒。" : $"遍历扫描较慢，整个系统盘约需 1 到 2 分钟。{(reason is null ? "" : "原因：" + reason + "。")}";
        _explorer.ShowProgress(title, hint, 0, cancellable: true);
        _rescan.Enabled = false;
        _statusScan.Text = "正在扫描…";

        var progress = new Progress<ScanProgress>(p =>
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            int percent;
            string detail;
            if (p.Total > 0)
            {
                percent = (int)(p.Done * 100 / p.Total);
                detail = $"已读取 {p.Done.ToString("N0", CultureInfo.InvariantCulture)} / {p.Total.ToString("N0", CultureInfo.InvariantCulture)} 条记录";
            }
            else
            {
                percent = (int)Math.Min(99, p.Bytes * 100 / usedBytes);
                detail = $"已列出 {p.Done.ToString("N0", CultureInfo.InvariantCulture)} 个目录，统计到 {ByteSize.Format(p.Bytes)}（已用 {ByteSize.Format(usedBytes)}）";
            }

            _explorer.ShowProgress(title, detail, percent, cancellable: true);
        });

        ScanResult? result = null;
        long peakAfterScan = 0;
        try
        {
            result = await Task.Run(() =>
            {
                var r = VolumeScanner.Scan(drive, preference, progress, cts.Token);
                using (var proc = Process.GetCurrentProcess())
                {
                    peakAfterScan = proc.PeakWorkingSet64;
                }

                // 扫描是一次性的重活，结束后把空闲的 GC 内存还给系统
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                return r;
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (_scanCts == cts)
            {
                _explorer.SetResult(previous);
                _statusScan.Text = previous is null ? "扫描已取消" : "扫描已取消，显示的是上一次的结果 · " + ExplorerPage.Describe(previous.Stats);
                _rescan.Enabled = true;
            }

            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (_scanCts == cts)
            {
                _explorer.SetResult(previous);
                _rescan.Enabled = true;
                _statusScan.Text = "扫描失败";
                MessageBox.Show(this, $"扫描 {drive} 失败：{ex.Message}", "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return;
        }

        if (_scanCts != cts)
        {
            return; // 期间又开始了新的扫描
        }

        _rescan.Enabled = true;
        var renderWatch = Stopwatch.StartNew();
        _explorer.SetResult(result);
        _statusScan.Text = ExplorerPage.Describe(result.Stats);
        LoadDrives();
        SelectDriveInCombo(drive);
        await AfterScanAutomation(result, peakAfterScan, renderWatch);
    }

    /// <summary>自动化验证：选中路径、截图、写报告、退出（只在带了对应参数时执行）。</summary>
    private async Task AfterScanAutomation(ScanResult result, long peakAfterScan, Stopwatch renderWatch)
    {
        if (_options.SelectPath is { } path && !_explorer.SelectPath(path))
        {
            _statusScan.Text += $" · 找不到 {path}";
        }

        if (_options.Screenshot is null && _options.Report is null && !_options.ExitAfterScan)
        {
            return;
        }

        var bindMs = renderWatch.Elapsed.TotalMilliseconds;
        string? automationError = null;
        try
        {
            // 窗口可能被最小化或被其他窗口挡住：先恢复并置顶，再等方块图的延迟重排和首次绘制完成
            if (WindowState == FormWindowState.Minimized)
            {
                WindowState = FormWindowState.Normal;
            }

            TopMost = true;
            Activate();
            await Task.Delay(1000);
            Refresh();

            if (_options.Screenshot is { } shot && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                var bounds = RectangleToScreen(ClientRectangle);
                using var bmp = new Bitmap(bounds.Width, bounds.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot))!);
                bmp.Save(shot);
            }

            TopMost = false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            automationError = ex.Message;
        }

        if (_options.Report is { } report)
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            var s = result.Stats;
            var root = result.Tree[result.Tree.Root];
            string[] lines =
            [
                $"mode={s.Mode}",
                $"elevated={_elevated}",
                $"automation_error={automationError}",
                $"device_dpi={DeviceDpi}",
                $"form_size={Width}x{Height}",
                $"toolbar_height={Controls.OfType<Panel>().FirstOrDefault(c => c.Dock == DockStyle.Top)?.Height}",
                $"mft_unavailable_reason={s.MftUnavailableReason}",
                $"scan_ms={s.TotalDuration.TotalMilliseconds:F0}",
                $"bind_and_first_paint_ms={bindMs:F0}",
                $"treemap_render_ms={_explorer.Map.LastRenderTime.TotalMilliseconds:F0}",
                $"treemap_blocks={_explorer.Map.BlockCount}",
                $"nodes={s.Nodes}",
                $"files={root.FileCount}",
                $"dirs={root.DirCount}",
                $"alloc_bytes={root.AllocSize}",
                $"name_chars={s.NameChars}",
                $"name_chars_capacity={s.NameCharsCapacity}",
                $"peak_working_set_after_scan_bytes={peakAfterScan}",
                $"peak_working_set_bytes={proc.PeakWorkingSet64}",
                $"working_set_bytes={proc.WorkingSet64}",
                $"managed_heap_bytes={GC.GetTotalMemory(false)}",
            ];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
            await File.WriteAllLinesAsync(report, lines);
        }

        if (_options.ExitAfterScan)
        {
            Close();
        }
    }

    private void RestartElevated()
    {
        _scanCts?.Cancel();
        if (!ShellActions.RestartElevated(_currentDrive, this))
        {
            _statusScan.Text = "已取消以管理员身份重启";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        base.OnFormClosing(e);
    }
}

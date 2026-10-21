using System.Diagnostics;
using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Explorer;
using DiskThinner.App.Pages;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Inventory;
using DiskThinner.Rules;
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
    private readonly OverviewPage _overview = new() { Dock = DockStyle.Fill };
    private readonly ToolStripStatusLabel _statusRules = new() { TextAlign = ContentAlignment.MiddleRight };
    private DiskThinner.Rules.RuleEngine? _engine;
    private string? _rulesError;
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
        var screen = options.SecondaryScreen ? Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen
            : options.PrimaryScreen ? Screen.PrimaryScreen
            : null;
        if (screen is not null)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40);
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
        _pages["overview"] = _overview;
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

        _overview.NavigateRequested += (_, node) =>
        {
            _nav.Select("explorer");
            _explorer.Reveal(node);
        };
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

        _nav.Select(options.StartPage ?? "overview");

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
        status.Items.Add(_statusRules);
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
        _overview.ShowScanning();
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
        Analysis? analysis = null;
        long peakAfterScan = 0;
        try
        {
            (result, analysis) = await Task.Run(() =>
            {
                // 扫描和匹配期间不用后台 GC：后台回收进行时分配还在继续，堆会涨得更高；这几秒里界面没有要求低延迟的操作
                var latency = System.Runtime.GCSettings.LatencyMode;
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
                try
                {
                    var r = VolumeScanner.Scan(drive, preference, progress, cts.Token);
                    using (var proc = Process.GetCurrentProcess())
                    {
                        peakAfterScan = proc.PeakWorkingSet64;
                    }

                    // 扫描是一次性的重活，结束后把空闲的 GC 内存还给系统。
                    // 规则和已装软件放在这之后再加载，扫描时的临时数组和它们不同时占着内存。
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    return (r, _options.NoRules ? null : Analyze(r, cts.Token));
                }
                finally
                {
                    System.Runtime.GCSettings.LatencyMode = latency;
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (_scanCts == cts)
            {
                _explorer.SetResult(previous, _explorer.Classification, _explorer.Owners);
                _statusScan.Text = previous is null ? "扫描已取消" : "扫描已取消，显示的是上一次的结果 · " + ExplorerPage.Describe(previous.Stats);
                _rescan.Enabled = true;
            }

            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (_scanCts == cts)
            {
                _explorer.SetResult(previous, _explorer.Classification, _explorer.Owners);
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
        _analysis = analysis;
        _explorer.SetResult(result, analysis?.C, analysis?.Owners, analysis?.ByOwner, analysis?.ByKind);
        _overview.SetResult(result, analysis?.C, analysis?.Owners);
        if (_options.StartView > 0 && !_startViewApplied)
        {
            _startViewApplied = true;
            _explorer.ViewIndex = _options.StartView;
        }

        _wsAfterBind = Environment.WorkingSet;
        _statusScan.Text = ExplorerPage.Describe(result.Stats);
        _statusRules.Text = _options.NoRules ? "没有加载规则（--no-rules）"
            : _engine is { } eng && analysis is not null
                ? $"规则 {eng.Rules.Locations.Count} 条 · 已装软件 {analysis.Owners.Inventory.Apps.Count} 项 · 匹配和归属 {(analysis.C.Duration + analysis.Owners.Duration).TotalSeconds:F1} 秒"
                : "规则没有加载：" + _rulesError;
        LoadDrives();
        SelectDriveInCombo(drive);
        await AfterScanAutomation(result, peakAfterScan, renderWatch);
    }

    /// <summary>一次扫描的规则匹配、归属推断和两种汇总；<see cref="Memory"/> 是每一步之后的工作集（找内存峰值用）。</summary>
    private sealed record Analysis(Classification C, OwnerMap Owners, List<SpaceGroup> ByOwner, List<SpaceGroup> ByKind, TimeSpan InventoryWait, TimeSpan GroupsDuration,
        IReadOnlyList<(string Step, long Bytes)> Memory);

    private Analysis? _analysis;
    private long _wsAfterBind;
    private bool _startViewApplied;
    private string? _inventoryError;

    /// <summary>
    /// 在后台线程上：加载规则并匹配，同时收集已装软件（两者互不依赖，并行做），然后推断归属、计算汇总。
    /// 规则加载失败时返回 null；已装软件收集失败时照样给出规则结果，只是没有推断。
    /// </summary>
    private Analysis? Analyze(ScanResult r, CancellationToken token)
    {
        var memory = new List<(string, long)> { ("analyze_start", Environment.WorkingSet) };
        var inventoryTask = Task.Run(() => InventoryCollector.Collect(token), token);
        var engine = EnsureRules();
        memory.Add(("rules_loaded", Environment.WorkingSet));
        var c = engine?.Classify(r.Tree, token);
        memory.Add(("classified", Environment.WorkingSet));
        var wait = Stopwatch.StartNew();
        InventorySnapshot inventory;
        try
        {
            inventory = inventoryTask.GetAwaiter().GetResult();
            _inventoryError = inventory.Errors.Count > 0 ? string.Join("；", inventory.Errors) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            inventory = InventorySnapshot.Empty;
            _inventoryError = ex.Message;
        }

        wait.Stop();
        if (c is null)
        {
            return null;
        }

        memory.Add(("inventory", Environment.WorkingSet));
        var owners = OwnerMap.Build(c, inventory, cancellationToken: token);
        memory.Add(("owners", Environment.WorkingSet));
        var sw = Stopwatch.StartNew();
        var byOwner = SpaceGroups.ByOwner(owners, cancellationToken: token);
        var byKind = SpaceGroups.ByKind(owners);
        sw.Stop();
        memory.Add(("groups", Environment.WorkingSet));

        // 匹配和推断过程中的临时对象（路径字符串、注册表和进程信息等）在绑定界面之前回收掉
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        memory.Add(("after_gc", Environment.WorkingSet));
        return new Analysis(c, owners, byOwner, byKind, wait.Elapsed, sw.Elapsed, memory);
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
                using var bmp = CaptureClientArea();
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
                $"rules={_engine?.Rules.Locations.Count}",
                $"rules_apps={_engine?.Rules.Apps.Count}",
                $"rules_error={_rulesError}",
                $"no_rules={_options.NoRules}",
                $"classify_ms={_explorer.Classification?.Duration.TotalMilliseconds:F0}",
                $"cleanable_bytes={_explorer.Classification?.TotalCleanable}",
                $"unexplained_bytes={_explorer.Classification?.Unexplained(result.Tree.Root)}",
                .. AnalysisReport(),
                $"view={_explorer.ViewIndex}",
                $"nodes={s.Nodes}",
                $"files={root.FileCount}",
                $"dirs={root.DirCount}",
                $"alloc_bytes={root.AllocSize}",
                $"name_chars={s.NameChars}",
                $"name_chars_capacity={s.NameCharsCapacity}",
                $"mft_records={s.MftRecords}",
                $"mft_names={s.MftNames}",
                $"hardlink_secondary_names={s.HardLinkSecondaryNames}",
                $"ws_before_scan_bytes={s.WorkingSetBeforeScan}",
                $"ws_after_read_bytes={s.WorkingSetAfterRead}",
                $"ws_after_build_bytes={s.WorkingSetAfterBuild}",
                $"ws_after_finalize_bytes={s.WorkingSetAfterFinalize}",
                $"peak_working_set_after_scan_bytes={peakAfterScan}",
                $"peak_working_set_bytes={proc.PeakWorkingSet64}",
                $"working_set_bytes={proc.WorkingSet64}",
                $"peak_private_bytes={proc.PeakPagedMemorySize64}",
                $"private_bytes={proc.PrivateMemorySize64}",
                .. (_analysis?.Memory ?? []).Select(m => $"ws_{m.Step}_bytes={m.Bytes}"),
                $"ws_after_bind_bytes={_wsAfterBind}",
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    /// <summary>
    /// 截取本窗口的客户区。用 PrintWindow 让窗口把自己画到位图上：窗口被别的程序挡住时也能截到，
    /// 而且不会截到屏幕上的其他内容。
    /// </summary>
    private Bitmap CaptureClientArea()
    {
        const uint PwRenderFullContent = 2;
        using var full = new Bitmap(Bounds.Width, Bounds.Height);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try
            {
                PrintWindow(Handle, hdc, PwRenderFullContent);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var client = RectangleToScreen(ClientRectangle);
        var area = new Rectangle(client.X - Bounds.X, client.Y - Bounds.Y, client.Width, client.Height);
        area.Intersect(new Rectangle(Point.Empty, full.Size));
        return full.Clone(area, full.PixelFormat);
    }

    /// <summary>报告里的已装软件、归属推断和汇总的统计。</summary>
    private IEnumerable<string> AnalysisReport()
    {
        if (_analysis is not { } a)
        {
            yield break;
        }

        var inv = a.Owners.Inventory;
        yield return $"inventory_apps={inv.Apps.Count}";
        yield return $"inventory_by_source={string.Join(",", Enum.GetValues<AppSource>().Select(s => $"{s}:{inv.Count(s)}"))}";
        yield return $"inventory_ms={inv.Duration.TotalMilliseconds:F0}";
        yield return $"inventory_wait_ms={a.InventoryWait.TotalMilliseconds:F0}";
        yield return $"inventory_errors={_inventoryError}";
        yield return $"owners_ms={a.Owners.Duration.TotalMilliseconds:F0}";
        yield return $"inferred_roots={a.Owners.InferredCount}";
        yield return $"groups_ms={a.GroupsDuration.TotalMilliseconds:F0}";
        yield return $"groups_by_owner={a.ByOwner.Count}";
        yield return $"groups_by_kind={a.ByKind.Count}";
        yield return $"owner_bytes_rule={a.ByOwner.Where(g => g.Owner?.Source == OwnerSource.Rule).Sum(g => g.Bytes)}";
        yield return $"owner_bytes_inferred={a.ByOwner.Where(g => g.Owner?.Source == OwnerSource.Inferred).Sum(g => g.Bytes)}";
        yield return $"owner_bytes_unknown={a.ByOwner.Where(g => g.Owner is null).Sum(g => g.Bytes)}";
        foreach (var (g, i) in a.ByOwner.Where(g => g.Owner?.Source == OwnerSource.Inferred).Take(15).Select((g, i) => (g, i)))
        {
            yield return $"inferred_top{i}={g.Name}|{g.Bytes}|{g.LocationCount}|{g.Owner!.Evidence.FirstOrDefault()?.Kind}";
        }
    }

    /// <summary>第一次扫描时加载内置规则库（在后台线程）。加载失败时继续扫描，只是没有规则结果。</summary>
    private DiskThinner.Rules.RuleEngine? EnsureRules()
    {
        if (_engine is not null || _rulesError is not null)
        {
            return _engine;
        }

        try
        {
            _engine = new DiskThinner.Rules.RuleEngine(Rules.RuleStore.LoadBuiltIn(), DiskThinner.Rules.PathContext.FromSystem());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            _rulesError = ex.Message;
        }

        return _engine;
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

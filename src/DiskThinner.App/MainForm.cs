using System.Diagnostics;
using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Dialogs;
using DiskThinner.App.Explorer;
using DiskThinner.App.Pages;
using DiskThinner.App.Shell;
using DiskThinner.App.Formatting;
using DiskThinner.App.Growth;
using DiskThinner.Cleanup;
using DiskThinner.Core;
using DiskThinner.Inventory;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Scanner.Walk;
using DiskThinner.Storage;

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
    private readonly ToolStripStatusLabel _statusSnapshot = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly GrowthPage _growthPage = new() { Dock = DockStyle.Fill };
    private readonly CleanupPage _cleanupPage = new() { Dock = DockStyle.Fill };
    private readonly HistoryPage _historyPage = new() { Dock = DockStyle.Fill };
    private readonly SettingsPage _settingsPage = new() { Dock = DockStyle.Fill };
    private readonly string _settingsPath;
    private Settings.AppSettings _settings;
    private readonly AccountInfo _account = AccountInfo.Current();
    private AuditLog? _audit;
    private string? _auditError;
    private readonly Button _snapshot = Ui.Button(L.T("创建快照"), accessibleDescription: L.T("把当前扫描结果保存为快照，以后和它对比"));
    private SnapshotStore? _store;
    private string? _storeError;
    private GrowthModel? _growth;
    private ScanResult? _lastResult;
    private DiskThinner.Rules.RuleEngine? _engine;
    private string? _rulesError;
    private readonly ComboBox _drives = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300, AccessibleName = L.T("选择磁盘") };
    private readonly Button _rescan = Ui.Button(L.T("重新扫描"), primary: true);
    private readonly ToolStripStatusLabel _statusScan = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _statusMode = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly ToolStripStatusLabel _statusMonitor = new() { TextAlign = ContentAlignment.MiddleRight };
    private readonly StatusPill _pill = new() { Margin = new Padding(0) };
    private readonly Monitor.MonitorHost _monitorHost;
    private readonly Agent.AgentLayout _agentLayout;
    private bool _agentBusy;
    private DiskThinner.Monitor.WriteIndex? _weekWrites;
    private DiskThinner.Monitor.WriteIndex? _monthWrites;
    private string? _monitorLoadError;
    private int _writesVersion;
    private Task? _writesLoad;
    private readonly bool _elevated = VolumeScanner.IsElevated();
    private CancellationTokenSource? _scanCts;
    private bool _suppressDriveChange;
    private string? _currentDrive;

    public MainForm(AppOptions options)
    {
        _options = options;
        _settingsPath = options.SettingsFile ?? Settings.AppSettings.DefaultPath;
        _settings = Settings.AppSettings.Load(_settingsPath, out var settingsError);
        if (settingsError is not null)
        {
            AppLog.Write("设置文件读不了，用默认值：" + settingsError);
        }

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

        // 写入监控：这里只建宿主，第一次开始时才加载 TraceEvent（docs/phase2a-design.md 第 12.2 节第 21 条）
        _monitorHost = new Monitor.MonitorHost(this, options.MonitorDb ?? DiskThinner.Monitor.MonitorStore.DefaultPath, options.SnapshotDb ?? SnapshotStore.DefaultPath,
            new Monitor.PipeAgentSource(options.AgentPipe ?? DiskThinner.Monitor.Agent.AgentProtocol.PipeName, options.AgentService ?? DiskThinner.Monitor.Agent.AgentProtocol.ServiceName));
        _agentLayout = Agent.AgentLayout.Default(options.AgentSource) with
        {
            ServiceName = options.AgentService ?? DiskThinner.Monitor.Agent.AgentProtocol.ServiceName,
            PipeName = options.AgentPipe ?? DiskThinner.Monitor.Agent.AgentProtocol.PipeName,
        };
        _monitorHost.StatusChanged += (_, _) => UpdateMonitorUi();
        _monitorHost.DataChanged += (_, _) => _writesLoad = ReloadWrites();

        Controls.Add(_pageHost);
        Controls.Add(_nav);
        Controls.Add(BuildToolbar());
        Controls.Add(BuildStatusBar());

        (string Key, string Text, string Glyph, Control Page)[] pages =
        [
            ("overview", L.T("概览"), "\uE80A", _overview),
            ("explorer", L.T("空间浏览"), "\uE8B7", _explorer),
            ("growth", L.T("增长分析"), "\uE9D2", _growthPage),
            ("cleanup", L.T("清理计划"), "\uE74D", _cleanupPage),
            ("log", L.T("操作记录"), "\uE81C", _historyPage),
            ("settings", L.T("设置"), "\uE713", _settingsPage),
        ];
        foreach (var (key, text, glyph, page) in pages)
        {
            _nav.AddItem(key, text, glyph);
            _pages[key] = page;
            page.Dock = DockStyle.Fill;
            page.Visible = false;
            page.AccessibleName = text; // 读屏软件进入页面时读出页面名，而不是"窗格"
            _pageHost.Controls.Add(page);
        }

        _nav.Selected += (_, key) => ShowPage(key);

        _growthPage.NavigateRequested += (_, node) =>
        {
            _nav.Select("explorer");
            _explorer.Reveal(node);
        };
        _growthPage.SaveSnapshotRequested += (_, _) => SaveSnapshot();
        _growthPage.ToggleScheduleRequested += (_, _) => ToggleSchedule();
        _growthPage.StartMonitorRequested += (_, _) => OpenMonitorDialog();
        _growthPage.StopMonitorRequested += (_, _) => ConfirmStopMonitor();
        _growthPage.SetMonitor(_monitorHost.Load, !_settings.Monitor.IsOff);
        _overview.StartMonitorRequested += (_, _) => OpenMonitorDialog(hours: 4);
        _pill.Click += (_, _) => PillClicked();
        _snapshot.Click += (_, _) => SaveSnapshot();
        _overview.NavigateRequested += (_, node) =>
        {
            _nav.Select("explorer");
            _explorer.Reveal(node);
        };
        _explorer.RestartRequested += (_, _) => RestartElevated();
        _explorer.CancelRequested += (_, _) => _scanCts?.Cancel();
        _explorer.AddToPlanRequested += (_, r) => AddToPlan(r.Node, r.CommandId);
        _cleanupPage.NavigateRequested += (_, node) =>
        {
            _nav.Select("explorer");
            _explorer.Reveal(node);
        };
        _cleanupPage.OpenRequested += (_, item) => ShellActions.OpenLocation(item.Path, item.IsDirectory, this);
        _cleanupPage.MeasureRequested += (_, item) => Measure(item);
        _cleanupPage.ExecuteRequested += (_, _) => ExecuteCleanup();
        _cleanupPage.ExportRequested += (_, _) => ExportPlan();
        _historyPage.SetLog(EnsureAudit(), _auditError);
        _settingsPage.SetSettings(_settings);
        _settingsPage.SetInfo(0, null, 0);
        _settingsPage.SetMonitorInfo(_monitorHost.DbPath, _monitorHost.DbBytes);
        _settingsPage.SettingsChanged += (_, s) => ApplySettings(s);
        _settingsPage.ScheduleChanged += (_, e) => SetSchedule(e.Enabled, e.Time);
        _settingsPage.AgentActionRequested += (_, action) => AgentAction(action);
        _settingsPage.AgentConfigureRequested += (_, c) => ConfigureAgent(c.Mode, c.KeepDays);
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
        _snapshot.Enabled = false;
        _snapshot.Margin = new Padding(0, 0, 8, 0);
        left.Controls.Add(_snapshot);
        bar.Controls.Add(left);

        // 右侧：写入监控胶囊（四种状态，docs/phase2a-design.md 3.3 节 P1）
        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        right.Controls.Add(_pill);
        bar.Controls.Add(right);
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
        _statusMode.Text = _elevated ? L.T("管理员模式") : L.T("普通权限（MFT 快速扫描需要管理员）");
        status.Items.Add(_statusScan);
        status.Items.Add(_statusSnapshot);
        status.Items.Add(_statusRules);
        status.Items.Add(_statusMonitor);
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
        // 研发方案 1.3 的冷启动目标：记下进程启动到窗口显示的时间，以及这时 TraceEvent 有没有被加载（第 5.9.1 节：不应该）
        using (var self = Process.GetCurrentProcess())
        {
            _shownMs = (DateTime.Now - self.StartTime).TotalMilliseconds;
        }

        _traceEventAtShown = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Microsoft.Diagnostics.Tracing.TraceEvent");
        LoadDrives();
        RefreshSchedule();
        UpdateMonitorUi();
        _monitorInit = InitMonitorAsync();
        var drive = _options.Drive ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        SelectDriveInCombo(drive);
        StartScan(drive);
    }

    private Task? _monitorInit;
    private double _shownMs;
    private bool _traceEventAtShown;

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

                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? L.T("本地磁盘") : d.VolumeLabel;
                _drives.Items.Add(new DriveItem(d.RootDirectory.FullName,
                    L.F("{0} {1} · 可用 {2} / {3}", d.Name.TrimEnd('\\'), label, ByteSize.Format(d.AvailableFreeSpace), ByteSize.Format(d.TotalSize))));
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
            _drives.Items.Insert(0, new DriveItem(target, L.F("{0}（文件夹）", target.TrimEnd('\\'))));
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

        var title = mode == ScanMode.Mft ? L.F("正在读取 {0} 的主文件表（MFT）", drive.TrimEnd('\\')) : L.F("正在逐个目录扫描 {0}", drive.TrimEnd('\\'));
        var hint = mode == ScanMode.Mft ? L.T("通常只需要几秒。") : L.F("遍历扫描较慢，整个系统盘约需 1 到 2 分钟。{0}", (reason is null ? "" : L.F("原因：{0}。", reason)));
        _explorer.ShowProgress(title, hint, 0, cancellable: true);
        _overview.ShowScanning();
        _growthPage.ShowScanning();
        _cleanupPage.ShowScanning();
        _snapshot.Enabled = false;
        _rescan.Enabled = false;
        _statusScan.Text = L.T("正在扫描…");

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
                detail = L.F("已读取 {0} / {1} 条记录", p.Done.ToString("N0", CultureInfo.InvariantCulture), p.Total.ToString("N0", CultureInfo.InvariantCulture));
            }
            else
            {
                percent = (int)Math.Min(99, p.Bytes * 100 / usedBytes);
                detail = L.F("已列出 {0} 个目录，统计到 {1}（已用 {2}）", p.Done.ToString("N0", CultureInfo.InvariantCulture), ByteSize.Format(p.Bytes), ByteSize.Format(usedBytes));
            }

            _explorer.ShowProgress(title, detail, percent, cancellable: true);
        });

        ScanResult? result = null;
        Analysis? analysis = null;
        GrowthModel? growth = null;
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
                    var a = _options.NoRules ? null : Analyze(r, cts.Token);
                    growth = BuildGrowth(r, a?.C, DateTime.UtcNow);
                    if (a?.Memory is List<(string, long)> steps)
                    {
                        steps.Add(("growth", Environment.WorkingSet));
                    }

                    // 匹配、推断和快照过程中的临时对象（路径字符串、注册表和进程信息等）在绑定界面之前回收掉
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    if (a?.Memory is List<(string, long)> after)
                    {
                        after.Add(("after_gc", Environment.WorkingSet));
                    }

                    return (r, a);
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
                _statusScan.Text = previous is null ? L.T("扫描已取消") : L.F("扫描已取消，显示的是上一次的结果 · {0}", ExplorerPage.Describe(previous.Stats));
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
                _statusScan.Text = L.T("扫描失败");
                MessageBox.Show(this, L.F("扫描 {0} 失败：{1}", drive, ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        _lastResult = result;
        _explorer.SetResult(result, analysis?.C, analysis?.Owners, analysis?.ByOwner, analysis?.ByKind);
        _overview.SetResult(result, analysis?.C, analysis?.Owners);
        _cleanupPage.SetPlan(analysis?.Plan ?? CleanupPlan.Empty, analysis is null ? L.T("规则没有加载，不能生成清理计划。") : null);
        ApplyGrowth(growth);
        if (_options.StartView > 0 && !_startViewApplied)
        {
            _startViewApplied = true;
            _explorer.ViewIndex = _options.StartView;
        }

        _wsAfterBind = Environment.WorkingSet;
        _statusScan.Text = ExplorerPage.Describe(result.Stats);
        _statusRules.Text = _options.NoRules ? L.T("没有加载规则（--no-rules）")
            : _engine is { } eng && analysis is not null
                ? L.F("规则 {0} 条 · 已装软件 {1} 项 · 匹配和归属 {2:F1} 秒", eng.Rules.Locations.Count, analysis.Owners.Inventory.Apps.Count, (analysis.C.Duration + analysis.Owners.Duration).TotalSeconds)
                : L.F("规则没有加载：{0}", _rulesError);
        _settingsPage.SetInfo(_engine?.Rules.Locations.Count ?? 0, _rulesError, _store?.FileBytes ?? 0);
        AppLog.Write($"扫描完成：{drive}，{result.Stats.Mode}，{result.Tree.Count} 项，{result.Stats.TotalDuration.TotalSeconds:F1} 秒；清理计划 {analysis?.Plan.Items.Count ?? 0} 项");
        LoadDrives();
        SelectDriveInCombo(drive);
        await AfterScanAutomation(result, peakAfterScan, renderWatch);
    }

    /// <summary>一次扫描的规则匹配、归属推断和两种汇总；<see cref="Memory"/> 是每一步之后的工作集（找内存峰值用）。</summary>
    private sealed record Analysis(Classification C, OwnerMap Owners, List<SpaceGroup> ByOwner, List<SpaceGroup> ByKind, TimeSpan InventoryWait, TimeSpan GroupsDuration,
        IReadOnlyList<(string Step, long Bytes)> Memory, CleanupPlan Plan, TimeSpan PlanDuration);

    private Analysis? _analysis;
    private double _growthMs;
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
            _inventoryError = inventory.Errors.Count > 0 ? string.Join(L.T("；"), inventory.Errors) : null;
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
        ApplyObservedOwners(owners, r.Tree);
        memory.Add(("owners", Environment.WorkingSet));
        var sw = Stopwatch.StartNew();
        var byOwner = SpaceGroups.ByOwner(owners, cancellationToken: token);
        var byKind = SpaceGroups.ByKind(owners);
        sw.Stop();
        memory.Add(("groups", Environment.WorkingSet));
        var planWatch = Stopwatch.StartNew();
        var plan = BuildPlan(r, c, owners, inventory);
        planWatch.Stop();
        memory.Add(("plan", Environment.WorkingSet));
        return new Analysis(c, owners, byOwner, byKind, wait.Elapsed, sw.Elapsed, memory, plan, planWatch.Elapsed);
    }

    /// <summary>
    /// 实测的归属（E11，后台线程上）：常驻服务在运行时，取近 30 天服务的数据，给规则和推断都不认识的目录补上"实测：写入进程"。
    /// 服务没装、连不上时什么都不做。
    /// </summary>
    private void ApplyObservedOwners(OwnerMap owners, NodeTree tree)
    {
        try
        {
            var snap = _monitorHost.Agent;
            if (!snap.Installed)
            {
                snap = _monitorHost.AgentSource.Probe(); // 启动时的第一次查询可能还没完成
            }

            if (!snap.Running || !snap.ProtocolOk)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (_monitorHost.AgentSource.Query(now.AddDays(-30), now.AddHours(1)) is { } index)
            {
                var observed = Monitor.ObservedOwners.Compute(index, tree, owners, now);
                owners.SetObserved(observed);
                if (observed.Count > 0)
                {
                    AppLog.Write($"实测归属：{observed.Count} 处");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write("实测归属没有算出来：" + ex.GetType().Name);
        }
    }

    /// <summary>清理计划：官方命令只认已装软件探测到的程序；正在运行的程序用来提示"会跳过被占用的文件"。</summary>
    private CleanupPlan BuildPlan(ScanResult r, Classification c, OwnerMap owners, InventorySnapshot inventory)
    {
        var running = inventory.Apps.Where(a => a.Source == AppSource.Process && a.Id is not null).Select(a => a.Id!.ToLowerInvariant()).ToHashSet();
        var resolved = new Dictionary<string, string?>();
        return PlanBuilder.Build(r.Tree, c, new PlanOptions
        {
            CurrentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Account = _account,
            ResolveCommand = cmd => resolved.TryGetValue(cmd.Id, out var p) ? p : resolved[cmd.Id] = CommandResolver.Resolve(cmd, inventory),
            RunningExes = running,
            HardLinksKnown = r.Stats.Mode == ScanMode.Mft,
            OwnerOf = owners.OwnerOf,
            MinTempAgeDays = _settings.Cleanup.TempMinAgeDays,
        });
    }

    /// <summary>设置页改了设置：保存，再把能立即生效的部分应用上。</summary>
    private async void ApplySettings(Settings.AppSettings next)
    {
        var previous = _settings;

        // 改成"关闭"时如果正在监控，先确认再停止；不停就恢复原来的设置（第 5.9.7 节）
        if (next.Monitor.IsOff && !previous.Monitor.IsOff && _monitorHost.IsRunning)
        {
            var answer = MessageBox.Show(this, L.T("写入监控正在进行。改成\u201c关闭\u201d会停止这次监控，已经记录的数据会保留。\n\n要停止吗？"), L.T("写入监控"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                _settingsPage.SetSettings(previous);
                return;
            }

            await StopMonitorAsync();
        }

        _settings = next;
        try
        {
            next.Save(_settingsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.F("保存设置失败：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        AppLog.Write("设置已更新");
        if (previous.Monitor.IsOff != next.Monitor.IsOff)
        {
            // "关闭"时不出现开启诊断的入口（D12）
            _growthPage.SetMonitor(_monitorHost.Load, !next.Monitor.IsOff);
            UpdateMonitorUi();
            _writesLoad = ReloadWrites();
        }

        if (!previous.Cleanup.ProtectedPaths.SequenceEqual(next.Cleanup.ProtectedPaths, StringComparer.OrdinalIgnoreCase))
        {
            // 执行清理时立即按新名单复核；规则引擎重新加载后，下次扫描的计划和标签也会更新
            _engine = null;
            _cleanupPage.RefreshItems(L.T("保护名单已更新：执行清理时立即生效；重新扫描后清理计划会更新。"));
        }
        else if (previous.Cleanup.TempMinAgeDays != next.Cleanup.TempMinAgeDays && _analysis is { } a && _lastResult is { } r
                 && _cleanupPage.Plan.Items.All(i => i.State == PlanItemState.Pending))
        {
            _cleanupPage.SetPlan(BuildPlan(r, a.C, a.Owners, a.Owners.Inventory), null);
        }
    }

    /// <summary>操作记录库（user.db）。打不开时返回 null：这时不允许执行清理（没有记录就不删）。</summary>
    private AuditLog? EnsureAudit()
    {
        if (_audit is not null)
        {
            return _audit;
        }

        try
        {
            _audit = new AuditLog(_options.UserDb ?? AuditLog.DefaultPath);
            _auditError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _auditError = ex.Message;
        }

        return _audit;
    }

    /// <summary>空间浏览详情里的"加入清理计划""运行 X"：在计划里选中这一项（必要时换成指定的命令），切到清理计划页。</summary>
    private void AddToPlan(int node, string? commandId)
    {
        var item = _cleanupPage.Plan.Items.FirstOrDefault(i => i.Node == node && i.Group != PlanGroup.Blocked);
        if (item is null)
        {
            var blocked = _cleanupPage.Plan.Items.FirstOrDefault(i => i.Node == node);
            MessageBox.Show(this, blocked?.BlockReason is { } why ? L.F("这个位置不能由工具清理：{0}。", why) : L.T("这个位置不在清理计划里：可以清理的部分不到 1 MB，或者受保护。"),
                L.T("清理计划"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (item.State == PlanItemState.Pending)
        {
            var method = commandId is null
                ? item.Method is { Kind: not MethodKind.Manual } current ? current : item.Methods[0]
                : item.Methods.FirstOrDefault(m => m.Command?.Id == commandId);
            if (method is null)
            {
                MessageBox.Show(this, L.T("没有找到这个命令的程序（已装软件里没有探测到），可以改用直接删除或手动清理。"), L.T("清理计划"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                item.Method = method;
            }

            item.Selected = true;
        }

        _nav.Select("cleanup");
        _cleanupPage.Reveal(item);
    }

    /// <summary>执行清理：确认框 → 进度框（后台执行）→ 汇总 → 切到操作记录。</summary>
    private void ExecuteCleanup()
    {
        var plan = _cleanupPage.Plan;
        var toRun = plan.ToRun.ToList();
        if (toRun.Count == 0 || _explorer.Classification?.Engine is not { } engine)
        {
            return;
        }

        if (EnsureAudit() is not { } audit)
        {
            MessageBox.Show(this, L.F("操作记录打不开（{0}）。为了每次删除都有记录可查，这次不执行清理。", _auditError), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var manual = plan.Items.Count(i => i.Selectable && i.Selected && i.Method?.Kind == MethodKind.Manual && i.State == PlanItemState.Pending);
        bool askWhenLocked;
        using (var confirm = new ConfirmCleanupDialog(toRun, manual, _settings.Cleanup.AskWhenLocked))
        {
            if (confirm.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            askWhenLocked = confirm.AskWhenLocked;
        }

        CleanupRun? run = null;
        Exception? error = null;
        using (var progressDialog = new CleanupProgressDialog(toRun.Count))
        {
            var progress = new Progress<ExecProgress>(progressDialog.Report);
            Func<PlanItem, IReadOnlyList<FileHolder>, bool>? ask = askWhenLocked
                ? (item, holders) => progressDialog.Invoke(() => MessageBox.Show(progressDialog,
                    L.F("{0}里有文件被这些程序占用：\n\n{1}\n\n要请它们正常关闭后再试一次吗？不会强制结束，程序可能会提示保存。选“否”则跳过这些文件。", item.Title, string.Join("\n", holders.Select(h => h.Name).Distinct().Take(8))),
                    L.T("有文件被占用"), MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                : null;
            var executor = new CleanupExecutor(engine, audit, new ExecutorOptions { Account = _account, AskCloseApps = ask, ExtraProtection = _settings.ProtectionEntries() });
            AppLog.Write($"开始清理 {toRun.Count} 项，预计释放 {toRun.Sum(i => i.EstimatedBytes)} 字节");
            progressDialog.Shown += async (_, _) =>
            {
                try
                {
                    run = await Task.Run(() => executor.Execute(toRun, progress, progressDialog.Token));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
                {
                    error = ex;
                }

                progressDialog.Close();
            };
            progressDialog.ShowDialog(this);
        }

        AppLog.Write(run is null ? "清理出错：" + error?.Message : $"清理结束：释放 {run.FreedBytes} 字节，{run.Results.Count} 项，用时 {run.Duration.TotalSeconds:F1} 秒{(run.Cancelled ? "（被停止）" : "")}");
        _cleanupPage.RefreshItems(L.T("已执行清理，结果见操作记录。磁盘上的内容已经变了，重新扫描后会生成新的计划。"));
        _historyPage.Reload();
        if (error is not null)
        {
            MessageBox.Show(this, L.F("清理中途出错：{0}\n\n已经完成的部分记在操作记录里。", error.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (run is not null)
        {
            var ok = run.Results.Count(r => r.Result == AuditResult.Succeeded);
            var partial = run.Results.Count(r => r.Result == AuditResult.Partial);
            var notRun = run.Results.Count(r => r.Result is AuditResult.NotRun or AuditResult.Failed);
            MessageBox.Show(this,
                L.F("释放 {0}（预计 {1}），用时 {2:F1} 秒。\n\n成功 {3} 项{4}{5}。{6}\n\n每一项的结果都记在操作记录里。建议重新扫描，更新空间占用。", ByteSize.Format(run.FreedBytes), ByteSize.Format(run.EstimatedBytes), run.Duration.TotalSeconds, ok, (partial > 0 ? L.F("，部分完成 {0} 项", partial) : ""), (notRun > 0 ? L.F("，没有完成 {0} 项", notRun) : ""), (run.Cancelled ? L.T("\n清理被停止了。") : "")),
                L.T("清理完成"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        _nav.Select("log");
    }

    /// <summary>手动清理后重新测量这个位置，操作记录里写一条"手动清理"和实际释放量。</summary>
    private async void Measure(PlanItem item)
    {
        if (EnsureAudit() is not { } audit)
        {
            MessageBox.Show(this, L.F("操作记录打不开：{0}", _auditError), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var before = item.Bytes;
        long now;
        try
        {
            now = await Task.Run(() => MeasureNow(item.Path, item.IsDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.F("重新测量失败：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var freed = Math.Max(0, before - now);
        audit.Append(new AuditEntry
        {
            AtUtc = DateTime.UtcNow,
            Action = AuditAction.Manual,
            Target = item.Path,
            Title = item.Title,
            Bytes = before,
            FreedBytes = freed,
            Basis = L.T("用户自行处理 · 打开所在位置后重新测量"),
            Result = AuditResult.Succeeded,
            Detail = L.F("扫描时 {0}，现在 {1}", ByteSize.Format(before), ByteSize.Format(now)),
        });
        item.State = PlanItemState.Measured;
        item.FreedBytes = freed;
        item.ResultText = L.F("重新测量：现在 {0}，释放 {1}", ByteSize.Format(now), ByteSize.Format(freed));
        _cleanupPage.RefreshItems();
        _historyPage.Reload();
    }

    private static long MeasureNow(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            return Directory.Exists(path) ? WalkScanner.Scan(path).Tree is var t ? t[t.Root].AllocSize : 0 : 0;
        }

        return File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    /// <summary>导出清理清单（CSV）。</summary>
    private void ExportPlan()
    {
        using var dlg = new SaveFileDialog
        {
            Title = L.T("导出清理清单"),
            Filter = L.T("CSV 文件 (*.csv)|*.csv"),
            FileName = L.F("DiskThinner-清理计划-{0:yyyyMMdd}.csv", DateTime.Now),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var lines = new List<string> { L.T("分组,项目,路径,大小（字节）,来源,处理方式,预计释放（字节）,已选,提示") };
        foreach (var i in _cleanupPage.Plan.Items)
        {
            lines.Add(string.Join(',',
                AuditLog.Csv(CleanupText.GroupTitle(i.Group)), AuditLog.Csv(i.Title), AuditLog.Csv(i.Path),
                i.Bytes.ToString(CultureInfo.InvariantCulture), AuditLog.Csv(i.SourceText), AuditLog.Csv(i.Method?.Display ?? i.BlockReason),
                i.EstimatedBytes.ToString(CultureInfo.InvariantCulture), i.Selected && i.Selectable ? L.T("是") : L.T("否"), AuditLog.Csv(i.ResultText ?? i.Hint)));
        }

        try
        {
            File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, L.F("导出失败：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 后台线程上：读快照，整个盘扫描完、离上次快照超过 6 小时就自动保存，再和 7 天前对比。快照库打不开时返回 null（不影响其他功能）。
    /// </summary>
    private GrowthModel? BuildGrowth(ScanResult r, Classification? c, DateTime nowUtc)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _store ??= new SnapshotStore(_options.SnapshotDb ?? SnapshotStore.DefaultPath);
            _storeError = null;
            var s = _settings;
            return GrowthModel.Build(_store, r, c, autoSave: !_options.NoAutoSnapshot && s.Snapshot.AutoAfterScan, nowUtc,
                options: s.SnapshotOptions(), keepDays: s.Snapshot.KeepDays);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException
                                       or ArgumentException or KeyNotFoundException or Microsoft.Data.Sqlite.SqliteException)
        {
            _storeError = ex.Message;
            return null;
        }
        finally
        {
            _growthMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>把当前扫描结果保存为快照（工具栏和增长分析页的"创建快照"）。</summary>
    private async void SaveSnapshot()
    {
        if (_store is not { } store || _lastResult is not { } result || _growth is { CurrentSaved: true })
        {
            return;
        }

        _snapshot.Enabled = false;
        var classification = _explorer.Classification;
        try
        {
            var s = _settings;
            var model = await Task.Run(() => GrowthModel.SaveCurrent(store, result, classification, DateTime.UtcNow, s.SnapshotOptions(), s.Snapshot.KeepDays));
            if (ReferenceEquals(result, _lastResult))
            {
                ApplyGrowth(model);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            _snapshot.Enabled = true;
            MessageBox.Show(this, L.F("保存快照失败：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ApplyGrowth(GrowthModel? model)
    {
        _growth = model;
        _growthPage.SetModel(model, _lastResult, _explorer.Classification, _explorer.Owners, _storeError is null ? null : L.F("快照库打不开：{0}", _storeError));
        _overview.SetGrowth(model, _lastResult?.Tree, _explorer.Classification, _explorer.Owners);
        _snapshot.Enabled = model is { CurrentSaved: false };
        _snapshot.Text = model is { CurrentSaved: true } ? L.T("已保存快照") : L.T("创建快照");
        var latest = model?.Snapshots.LastOrDefault();
        _statusSnapshot.Text = latest is null ? L.T("还没有快照") : L.F("最近快照：{0}", TimeText.Relative(new DateTimeOffset(latest.TakenAtUtc).ToFileTime(), DateTime.Now));

        // 新快照的目录是写入监控合并的锚点（第 5.7 节第 1 条）；概览的写入者按新的 WeekBaseline 重新查询
        if (model is { CurrentSaved: true })
        {
            _monitorHost.ReloadAnchors();
        }

        _writesLoad = ReloadWrites();
    }

    /// <summary>查询每日快照任务的状态（schtasks 要几十毫秒，放到后台）。</summary>
    private async void RefreshSchedule()
    {
        var status = await Task.Run(ScheduledSnapshot.Query);
        if (!IsDisposed)
        {
            _growthPage.SetSchedule(status);
            _settingsPage.SetSchedule(status);
        }
    }

    /// <summary>增长分析页的"开启 / 关闭每日快照"。</summary>
    private async void ToggleSchedule()
    {
        var registered = await Task.Run(() => ScheduledSnapshot.Query().Registered);
        SetSchedule(!registered, _settings.Snapshot.DailyTime);
    }

    /// <summary>开启（或改时间）、关闭每日快照。管理员模式下注册的任务用最高权限（MFT 扫描）；普通权限下注册的任务用遍历扫描。</summary>
    private async void SetSchedule(bool enabled, string time)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return;
        }

        var drive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\").TrimEnd('\\');
        var (ok, message) = await Task.Run(() =>
        {
            var status = ScheduledSnapshot.Query();
            return enabled ? ScheduledSnapshot.Register(exe, time, _elevated, drive)
                : status.Registered ? ScheduledSnapshot.Unregister() : (true, "");
        });
        AppLog.Write(ok ? (enabled ? $"每日快照已开启（{time}）" : "每日快照已关闭") : "设置每日快照失败：" + message);
        if (!ok)
        {
            MessageBox.Show(this, L.F("设置每日快照失败：{0}", message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshSchedule();
    }

    /// <summary>自动化验证：选中路径、截图、写报告、退出（只在带了对应参数时执行）。</summary>
    private async Task AfterScanAutomation(ScanResult result, long peakAfterScan, Stopwatch renderWatch)
    {
        if (_options.SelectPath is { } path && !_explorer.SelectPath(path))
        {
            _statusScan.Text += L.F(" · 找不到 {0}", path);
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

            // 截图前等写入监控的查询和增长对比完成（都在后台），不然截到的是"正在加载"的样子
            foreach (var pending in new[] { _monitorInit, _writesLoad, _growthPage.Pending })
            {
                if (pending is not null)
                {
                    await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
                }
            }

            if (_growthPage.Pending is { } again)
            {
                await Task.WhenAny(again, Task.Delay(TimeSpan.FromSeconds(10)));
            }

            await Task.Delay(1000);
            Refresh();

            if (_options.Screenshot is { } shot && ClientSize.Width > 0 && ClientSize.Height > 0)
            {
                using var bmp = CaptureClientArea();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(shot))!);
                bmp.Save(shot);

                if (_options.ShowConfirm && _cleanupPage.Plan.ToRun.ToList() is { Count: > 0 } toRun)
                {
                    // 只显示确认框截图，不执行：截完直接关掉，DialogResult 不看
                    using var confirm = new ConfirmCleanupDialog(toRun, 0);
                    confirm.Show(this);
                    await Task.Delay(800);
                    confirm.Refresh();
                    using var dlgShot = CaptureForm(confirm, confirm.ClientRectangle);
                    dlgShot.Save(Path.ChangeExtension(shot, null) + "-confirm.png");
                    confirm.Close();
                }

                if (_options.ShowMonitorDialog)
                {
                    // 只显示"开启写入监控"对话框截图，不开始：截完直接关掉
                    using var monitorDialog = new MonitorDialog(_settings.Monitor.DefaultHours, _settings.Monitor.IsPrecise, _elevated,
                        _monitorHost.State == Monitor.MonitorUiState.OtherInstance, _account.Mismatch ? _account.ProcessUser : null);
                    monitorDialog.Show(this);
                    await Task.Delay(800);
                    monitorDialog.Refresh();
                    using var monitorShot = CaptureForm(monitorDialog, monitorDialog.ClientRectangle);
                    monitorShot.Save(Path.ChangeExtension(shot, null) + "-monitor.png");
                    monitorDialog.Close();
                }

                if (_options.ShowAgentDialog)
                {
                    // 只显示"安装常驻监控服务"对话框截图，不安装：截完直接关掉
                    using var agentDialog = new AgentInstallDialog(_settings.Monitor.IsPrecise, _settings.Monitor.KeepDays, _elevated, _agentLayout.ProgramRoot, _agentLayout.DataDir);
                    agentDialog.Show(this);
                    await Task.Delay(800);
                    agentDialog.Refresh();
                    using var agentShot = CaptureForm(agentDialog, agentDialog.ClientRectangle);
                    agentShot.Save(Path.ChangeExtension(shot, null) + "-agent.png");
                    agentDialog.Close();
                }
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
                $"snapshots={_growth?.Snapshots.Count}",
                $"snapshot_saved={_growth?.CurrentSaved}",
                $"snapshot_entries={_growth?.Current?.Count}",
                $"snapshot_db_bytes={_growth?.Store.FileBytes}",
                $"growth_ms={_growthMs:F0}",
                $"week_baseline={_growth?.WeekBaseline?.TakenAtUtc.ToString("o", CultureInfo.InvariantCulture)}",
                $"week_change_bytes={_growth?.WeekDiff?.NetChange}",
                $"week_hotspots={_growth?.WeekHotspots.Count}",
                $"snapshot_error={_storeError}",
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
                $"plan_items={_cleanupPage.Plan.Items.Count}",
                $"plan_selected={_cleanupPage.Plan.ToRun.Count()}",
                $"plan_bytes={_cleanupPage.Plan.ToRun.Sum(i => i.Bytes)}",
                $"plan_estimated_bytes={_cleanupPage.Plan.ToRun.Sum(i => i.EstimatedBytes)}",
                $"plan_blocked={_cleanupPage.Plan.Items.Count(i => i.Group == PlanGroup.Blocked)}",
                $"plan_commands={_cleanupPage.Plan.Items.Count(i => i.Methods.Any(m => m.Kind == MethodKind.Command))}",
                $"plan_ms={_analysis?.PlanDuration.TotalMilliseconds:F0}",
                $"account={_account.ProcessUser.Length > 0}",
                $"account_mismatch={_account.Mismatch}",
                $"audit_entries={_audit?.List().Count}",
                $"audit_error={_auditError}",
                $"managed_heap_bytes={GC.GetTotalMemory(false)}",
                .. MonitorReport(),
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
    private Bitmap CaptureClientArea() => CaptureForm(this, ClientRectangle);

    private static Bitmap CaptureForm(Form form, Rectangle clientRect)
    {
        const uint PwRenderFullContent = 2;
        var bounds = form.Bounds;
        using var full = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try
            {
                PrintWindow(form.Handle, hdc, PwRenderFullContent);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        var client = form.RectangleToScreen(clientRect);
        var area = new Rectangle(client.X - bounds.X, client.Y - bounds.Y, client.Width, client.Height);
        area.Intersect(new Rectangle(Point.Empty, full.Size));
        return full.Clone(area, full.PixelFormat);
    }

    /// <summary>报告里的写入监控（第 5.9.8 节）：状态、会话数、行数、库大小、收到 / 丢失 / 抽样、错误。</summary>
    private IEnumerable<string> MonitorReport()
    {
        var host = _monitorHost;
        var s = host.Status;
        yield return $"startup_shown_ms={_shownMs:F0}";
        yield return $"traceevent_loaded_at_shown={_traceEventAtShown}";
        yield return $"assemblies_at_report={AppDomain.CurrentDomain.GetAssemblies().Length}";
        yield return $"monitor_state={host.State}";
        yield return $"monitor_session={s.SessionName}";
        yield return $"monitor_mode={(s.SessionName is null ? "" : s.Mode.ToString())}";
        yield return $"monitor_received={s.Received}";
        yield return $"monitor_lost={s.Lost}";
        yield return $"monitor_sample_factor={s.SampleFactor}";
        yield return $"monitor_end_reason={host.LastEndReason}";
        yield return $"monitor_error={host.LastError ?? _monitorLoadError}";
        yield return $"monitor_db_bytes={host.DbBytes}";
        yield return $"monitor_sessions_30d={_monthWrites?.Sessions.Count}";
        yield return $"monitor_rows_30d={_monthWrites?.Rows.Count}";
        yield return $"monitor_week_sessions={_weekWrites?.Sessions.Count}";
        yield return $"monitor_agent_sessions_30d={_monthWrites?.Sessions.Count(x => x.Source == DiskThinner.Monitor.SessionSource.Agent)}";
        yield return $"agent_installed={host.Agent.Installed}";
        yield return $"agent_service_state={host.Agent.Service.State}";
        yield return $"agent_recording={host.Agent.Recording}";
        yield return $"agent_version={host.Agent.AgentVersion}";
        yield return $"agent_settings_status={_settingsPage.AgentStatusText}";
        yield return $"status_monitor={_statusMonitor.Text}";
        yield return $"growth_writer_cells={string.Join('|', _growthPage.Changes.Items.Select(i => i.Cells.Length > 4 ? i.Cells[4] + (i.Tag.Length > 0 ? " " + i.Tag : "") : ""))}";
        yield return $"growth_writers={_growthPage.Writers.Items.Count}";
        yield return $"overview_hint={_overview.Fastest.HintText}";
        yield return $"overview_writer_cells={string.Join('|', _overview.Fastest.Items.Select(i => i.Cells.Length > 2 ? i.Cells[2] + (i.Tag.Length > 0 ? " " + i.Tag : "") : ""))}";
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
            _engine = new DiskThinner.Rules.RuleEngine(Rules.RuleStore.LoadBuiltIn(), DiskThinner.Rules.PathContext.FromSystem(), _settings.ProtectionEntries());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or InvalidOperationException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            _rulesError = ex.Message;
        }

        return _engine;
    }

    private void RestartElevated(IEnumerable<string>? extraArgs = null)
    {
        _scanCts?.Cancel();
        if (!ShellActions.RestartElevated(_currentDrive, this, extraArgs))
        {
            _statusScan.Text = L.T("已取消以管理员身份重启");
        }
    }

    // ---------------- 写入监控（docs/phase2a-design.md 5.9 节） ----------------

    /// <summary>
    /// 启动时：后台判定残留会话和没有结束的会话记录（第 5.1.2 节）；带了 --start-monitor / --monitor-seconds 时在管理员下开始监控，
    /// 账户不一致时先打开对话框（第 5.1.5 节）。
    /// </summary>
    private async Task InitMonitorAsync()
    {
        foreach (var ignored in _options.Ignored)
        {
            AppLog.Write("忽略了不合法的参数：" + ignored);
        }

        await _monitorHost.CheckAsync();
        if (_options.InstallAgent is { } install && !IsDisposed)
        {
            // 以管理员身份重启后安装（用户已经在对话框里确认过）
            if (_elevated)
            {
                _nav.Select("settings");
                _settingsPage.ShowSection(L.T("写入监控"));
                await InstallAgentAsync(install.Precise, install.KeepDays);
            }
            else
            {
                AppLog.Write("--install-agent 需要管理员权限，没有安装");
            }
        }

        if (_options.StartMonitor is not { } duration || IsDisposed)
        {
            return;
        }

        var precise = _options.MonitorPrecise ?? _settings.Monitor.IsPrecise;
        if (!_elevated)
        {
            AppLog.Write("--start-monitor 需要管理员权限，没有开始写入监控");
            _statusMonitor.Text = L.T("写入监控：需要管理员权限，没有开始");
            return;
        }

        if (_account.Mismatch && !_options.MonitorForAutomation)
        {
            OpenMonitorDialog(hours: Math.Clamp((int)Math.Round(duration.TotalHours), 1, 24), precise: precise, fromAutoStart: true);
            return;
        }

        StartMonitor(duration, precise);
    }

    /// <summary>工具栏胶囊和状态栏（2 秒一次，以及状态变化时）。</summary>
    private void UpdateMonitorUi()
    {
        var host = _monitorHost;
        var s = host.Status;
        var off = _settings.Monitor.IsOff;
        switch (host.State)
        {
            case Monitor.MonitorUiState.Running:
            {
                var mode = Monitor.MonitorText.Mode(s.Mode);
                _pill.Set(L.F("写入监控：诊断中（{0}）· 剩余 {1}", mode, Monitor.MonitorText.Remaining(s.Remaining)), L.T("停止"), StatusPill.Running,
                    L.T("点击停止写入监控，已经记录的数据会保留"));
                _statusMonitor.Text = L.F("写入监控：诊断中（{0}）· 剩余 {1} · {2}", mode, Monitor.MonitorText.RemainingShort(s.Remaining), Monitor.MonitorText.Lost(s.LostRate));
                break;
            }

            case Monitor.MonitorUiState.Agent:
            {
                var a = host.Agent;
                var mode = Monitor.MonitorText.Mode(a.Precise ? DiskThinner.Monitor.MonitorMode.Precise : DiskThinner.Monitor.MonitorMode.Light);
                _pill.Set(L.F("写入监控：常驻（{0}）· {1}", mode, a.RecordedFor(DateTime.UtcNow)), null, StatusPill.Running, L.T("常驻服务正在记录，点击打开设置里的写入监控一组"));
                _statusMonitor.Text = L.F("写入监控：常驻（{0}）· {1}", mode, Monitor.MonitorText.Lost(a.LostRate));
                break;
            }

            case Monitor.MonitorUiState.AgentDown:
            {
                var a = host.Agent;
                var action = a.NeedsUpdate ? L.T("更新") : !a.Running ? L.T("启动") : null;
                _pill.Set(a.NeedsUpdate ? L.T("写入监控：常驻服务需要更新") : L.T("写入监控：常驻服务没有运行"), action, StatusPill.Warning,
                    _elevated ? a.Problem ?? L.T("点击处理") : L.T("以管理员身份打开 DiskThinner 后可以启动或更新服务"));
                _statusMonitor.Text = L.F("写入监控：常驻服务{0}", a.Problem is { } problem ? L.F("（{0}）", problem) : L.T("没有运行"));
                break;
            }

            case Monitor.MonitorUiState.OtherInstance:
                _pill.Set(L.T("写入监控：另一个窗口正在监控"), null, StatusPill.Running);
                _statusMonitor.Text = L.T("写入监控：另一个 DiskThinner 窗口正在监控");
                break;
            case Monitor.MonitorUiState.Residual:
                _pill.Set(L.T("写入监控：上次没有正常结束"), L.T("清理"), StatusPill.Warning,
                    _elevated ? L.T("点击再清理一次") : L.T("以管理员身份打开 DiskThinner 后会自动清理"));
                _statusMonitor.Text = _elevated ? L.T("写入监控：上次没有正常结束，残留的会话没有清理掉") : L.T("写入监控：上次没有正常结束，以管理员身份打开后会自动清理");
                break;
            default:
                _pill.Set(L.T("写入监控：关闭"), off ? null : L.T("开启诊断"), StatusPill.Idle,
                    off ? L.T("设置里关闭了写入监控，点击打开设置") : L.T("开启写入监控后，可以看到是哪个程序在写磁盘"));
                _statusMonitor.Text = host.LastEndReason switch
                {
                    DiskThinner.Monitor.Etw.StopReason.External => L.T("写入监控被外部停止"),
                    DiskThinner.Monitor.Etw.StopReason.Error => L.T("写入监控因错误停止"),
                    DiskThinner.Monitor.Etw.StopReason.StoreError => L.T("写入监控：写库失败，已停止"),
                    _ => L.T("写入监控：关闭"),
                };
                break;
        }

        _growthPage.SetLive(s, host.LiveSession, host.Agent.Recording);
        _settingsPage.SetAgent(host.Agent, _elevated, _agentLayout.SourceAvailable);
    }

    private async void PillClicked()
    {
        var host = _monitorHost;
        if (_settings.Monitor.IsOff && !host.IsRunning)
        {
            _nav.Select("settings");
            _settingsPage.ShowSection(L.T("写入监控"));
            return;
        }

        switch (host.State)
        {
            case Monitor.MonitorUiState.Running:
                ConfirmStopMonitor();
                break;
            case Monitor.MonitorUiState.Agent:
                _nav.Select("settings");
                _settingsPage.ShowSection(L.T("写入监控"));
                break;
            case Monitor.MonitorUiState.AgentDown when !_elevated:
                if (MessageBox.Show(this, L.T("启动或更新常驻服务需要管理员权限。\n\n现在以管理员身份重新打开 DiskThinner 吗？"), L.T("写入监控"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    RestartElevated(["--page", "settings"]);
                }

                break;
            case Monitor.MonitorUiState.AgentDown:
                AgentAction(host.Agent.NeedsUpdate ? "update" : !host.Agent.Running ? "start" : "settings");
                break;
            case Monitor.MonitorUiState.Residual when _elevated:
                await host.CheckAsync();
                break;
            case Monitor.MonitorUiState.Residual:
                if (MessageBox.Show(this, L.T("上次的写入监控没有正常结束，会话可能还在运行。以管理员身份打开 DiskThinner 后会自动清理。\n\n现在以管理员身份重新打开吗？"),
                        L.T("写入监控"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    RestartElevated();
                }

                break;
            default:
                OpenMonitorDialog();
                break;
        }
    }

    /// <summary>开启写入监控对话框：管理员下直接开始；普通权限下以管理员身份重启，新实例带参数自动开始（第 5.1.5 节）。</summary>
    private void OpenMonitorDialog(int? hours = null, bool? precise = null, bool fromAutoStart = false)
    {
        if (_monitorHost.IsRunning)
        {
            return;
        }

        // 常驻服务在记录时不再开第二个会话（E12）
        if (_monitorHost.Agent.Recording)
        {
            MessageBox.Show(this, L.T("常驻服务正在记录，不需要再开启诊断。"), L.T("写入监控"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int chosenHours;
        bool chosenPrecise;
        using (var dlg = new MonitorDialog(hours ?? _settings.Monitor.DefaultHours, precise ?? _settings.Monitor.IsPrecise, _elevated,
                   _monitorHost.State == Monitor.MonitorUiState.OtherInstance, _account.Mismatch ? _account.ProcessUser : null))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                if (fromAutoStart)
                {
                    _statusMonitor.Text = L.T("没有开始写入监控");
                }

                return;
            }

            (chosenHours, chosenPrecise) = (dlg.Hours, dlg.Precise);
        }

        if (!_elevated)
        {
            var args = new List<string> { "--start-monitor", chosenHours.ToString(CultureInfo.InvariantCulture), "--monitor-mode", chosenPrecise ? "precise" : "light" };
            if (_options.MonitorDb is { } db)
            {
                args.AddRange(["--monitor-db", db]);
            }

            RestartElevated(args);
            return;
        }

        StartMonitor(TimeSpan.FromHours(chosenHours), chosenPrecise);
    }

    private void StartMonitor(TimeSpan duration, bool precise)
    {
        var mode = precise ? DiskThinner.Monitor.MonitorMode.Precise : DiskThinner.Monitor.MonitorMode.Light;
        var r = _monitorHost.Start(duration, mode, _settings.Monitor.KeepDays);
        AppLog.Write(r.Status == DiskThinner.Monitor.Etw.StartStatus.Started
            ? $"写入监控已开始：{duration.TotalSeconds:F0} 秒，{(precise ? "精确" : "轻量")}"
            : $"写入监控没有开始：{r.Status} {r.Message}");
        var message = r.Status switch
        {
            DiskThinner.Monitor.Etw.StartStatus.Started => null,
            DiskThinner.Monitor.Etw.StartStatus.AlreadyRunning => L.T("另一个 DiskThinner 窗口正在监控，结束后再开启。"),
            DiskThinner.Monitor.Etw.StartStatus.NotElevated => L.T("写入监控需要管理员权限。"),
            _ => L.F("开始写入监控失败：{0}", r.Message),
        };
        if (message is not null && !_options.MonitorForAutomation)
        {
            MessageBox.Show(this, message, L.T("写入监控"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateMonitorUi();
    }

    // ---------------- 常驻服务（docs/phase2b-design.md 5.3、5.6 节） ----------------

    /// <summary>自动化验证时不弹结果框。</summary>
    private bool Automation => _options.Screenshot is not null || _options.ExitAfterScan || _options.MonitorForAutomation;

    private Agent.AgentInstaller NewAgentInstaller() => new(_agentLayout, DiskThinner.Monitor.Agent.ServiceManager.Instance);

    /// <summary>以管理员身份重启并回到设置页（带上测试用的位置参数）。</summary>
    private void RestartElevatedToSettings(IEnumerable<string>? extra = null)
    {
        var args = new List<string> { "--page", "settings" };
        if (_options.AgentSource is { } src)
        {
            args.AddRange(["--agent-source", src]);
        }

        if (_options.SettingsFile is { } settings)
        {
            args.AddRange(["--settings", settings]);
        }

        args.AddRange(extra ?? []);
        RestartElevated(args);
    }

    /// <summary>设置页服务一组的按钮、胶囊的"启动 / 更新"。</summary>
    private async void AgentAction(string action)
    {
        if (_agentBusy)
        {
            return;
        }

        if (action is "start" or "stop" or "update" or "uninstall" && !_elevated)
        {
            RestartElevatedToSettings();
            return;
        }

        switch (action)
        {
            case "settings":
                _nav.Select("settings");
                _settingsPage.ShowSection(L.T("写入监控"));
                break;
            case "elevate":
                RestartElevatedToSettings();
                break;
            case "install":
                OpenAgentInstallDialog();
                break;
            case "start":
                await RunAgentAsync(L.T("正在启动常驻服务…"), () => SetAgentRunning(true), showSuccess: false);
                break;
            case "stop":
                if (MessageBox.Show(this, L.T("停止常驻服务？停止后不再记录，已经记录的数据会保留。下次开机时服务会自动启动。"), L.T("常驻监控服务"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                {
                    await RunAgentAsync(L.T("正在停止常驻服务…"), () => SetAgentRunning(false), showSuccess: false);
                }

                break;
            case "update":
                await RunAgentAsync(L.T("正在更新常驻服务…"), () => NewAgentInstaller().Update(), showSuccess: true);
                break;
            case "uninstall":
                if (ConfirmUninstallAgent() is { } deleteData)
                {
                    var r = await RunAgentAsync(L.T("正在卸载常驻服务…"), () => NewAgentInstaller().Uninstall(deleteData), showSuccess: true);
                    if (r.Ok && _settings.Monitor.IsAgent)
                    {
                        // 卸载后回到"临时诊断"
                        var next = _settings with { Monitor = _settings.Monitor with { Enabled = Settings.MonitorSettings.Diagnostic } };
                        _settingsPage.SetSettings(next);
                        ApplySettings(next);
                    }
                }

                break;
        }
    }

    private Agent.AgentOpResult SetAgentRunning(bool run)
    {
        var error = _monitorHost.AgentSource.SetServiceRunning(run);
        return new Agent.AgentOpResult(error is null, error ?? (run ? L.T("已启动。") : L.T("已停止。")));
    }

    private async void OpenAgentInstallDialog()
    {
        bool precise;
        int keep;
        using (var dlg = new AgentInstallDialog(_settings.Monitor.IsPrecise, _settings.Monitor.KeepDays, _elevated, _agentLayout.ProgramRoot, _agentLayout.DataDir))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            (precise, keep) = (dlg.Precise, dlg.KeepDays);
        }

        if (!_elevated)
        {
            RestartElevatedToSettings(["--install-agent", (precise ? "precise" : "light") + ":" + keep.ToString(CultureInfo.InvariantCulture)]);
            return;
        }

        await InstallAgentAsync(precise, keep);
    }

    private async Task InstallAgentAsync(bool precise, int keep)
    {
        var r = await RunAgentAsync(L.T("正在安装常驻服务…"), () => NewAgentInstaller().Install(new Agent.AgentInstallOptions(precise ? "precise" : "light", keep)), showSuccess: true);
        if (r.Ok)
        {
            var next = _settings with
            {
                Monitor = _settings.Monitor with
                {
                    Enabled = Settings.MonitorSettings.Agent,
                    Mode = precise ? Settings.MonitorSettings.Precise : Settings.MonitorSettings.Light,
                    KeepDays = keep,
                },
            };
            _settingsPage.SetSettings(next);
            ApplySettings(next);
        }
    }

    /// <summary>在后台做一次服务的操作：期间显示进度、不接受别的操作；完成后刷新服务状态，失败时（或要求时）弹出结果。</summary>
    private async Task<Agent.AgentOpResult> RunAgentAsync(string progress, Func<Agent.AgentOpResult> op, bool showSuccess)
    {
        _agentBusy = true;
        UseWaitCursor = true;
        _statusMonitor.Text = progress;
        Agent.AgentOpResult r;
        try
        {
            r = await Task.Run(op);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            r = new Agent.AgentOpResult(false, ex.Message);
        }
        finally
        {
            _agentBusy = false;
            UseWaitCursor = false;
        }

        AppLog.Write($"常驻服务：{progress} {(r.Ok ? "完成" : "失败")}：{r.Message}");
        await _monitorHost.RefreshAgentAsync();
        if ((!r.Ok || showSuccess) && !Automation)
        {
            var text = r.Message + (r.LogTail is { Count: > 0 } tail ? "\n\n" + L.T("服务日志的最后几行：") + "\n" + string.Join("\n", tail) : "");
            MessageBox.Show(this, text, L.T("常驻监控服务"), MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        _writesLoad = ReloadWrites();
        return r;
    }

    /// <summary>卸载确认（Q3）："卸载常驻监控服务？"，勾选框"同时删除监控数据"默认不勾选。返回是否删除数据；取消时返回 null。</summary>
    private bool? ConfirmUninstallAgent()
    {
        var size = _monitorHost.Agent.Status?.DbBytes ?? 0;
        var uninstall = new TaskDialogButton(L.T("卸载"));
        var cancel = new TaskDialogButton(L.T("取消"));
        var page = new TaskDialogPage
        {
            Caption = L.T("常驻监控服务"),
            Heading = L.T("卸载常驻监控服务？"),
            Text = L.T("服务会停止并删除，程序目录也一起删除。不勾选下面这一项时，已经记录的数据保留着，以后重新安装可以接着用。"),
            Verification = new TaskDialogVerificationCheckBox(size > 0 ? L.F("同时删除监控数据（{0}）", ByteSize.Format(size)) : L.T("同时删除监控数据")),
            Buttons = { uninstall, cancel },
            DefaultButton = cancel,
            Icon = TaskDialogIcon.Warning,
        };
        return TaskDialog.ShowDialog(this, page) == uninstall ? page.Verification.Checked : null;
    }

    /// <summary>选了常驻服务时改了记录模式或保留天数：发给服务（管理员）。</summary>
    private async void ConfigureAgent(string? mode, int? keepDays)
    {
        if (!_elevated || _agentBusy)
        {
            return;
        }

        var source = _monitorHost.AgentSource;
        var r = await Task.Run(() => source.Control("configure", mode, keepDays));
        if (r?.Type != "ok" && !Automation)
        {
            MessageBox.Show(this, L.F("服务没有接受新的设置：{0}", r?.Code ?? L.T("连不上服务")), L.T("常驻监控服务"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        await _monitorHost.RefreshAgentAsync();
    }

    /// <summary>停止监控先确认（第 3.3 节 P8）："停止写入监控？已经记录的数据会保留。"</summary>
    private async void ConfirmStopMonitor()
    {
        if (!_monitorHost.IsRunning)
        {
            return;
        }

        var stop = new TaskDialogButton(L.T("停止"));
        var keep = new TaskDialogButton(L.T("继续监控"));
        var page = new TaskDialogPage
        {
            Caption = L.T("写入监控"),
            Heading = L.T("停止写入监控？"),
            Text = L.T("已经记录的数据会保留。"),
            Buttons = { stop, keep },
            DefaultButton = keep,
        };
        if (TaskDialog.ShowDialog(this, page) != stop)
        {
            return;
        }

        await StopMonitorAsync();
    }

    private async Task StopMonitorAsync()
    {
        _pill.Enabled = false;
        try
        {
            await _monitorHost.StopAsync(DiskThinner.Monitor.Etw.StopReason.User);
        }
        finally
        {
            _pill.Enabled = true;
        }

        UpdateMonitorUi();
    }

    /// <summary>
    /// 后台读写入监控的汇总（扫描完成和 <see cref="Monitor.MonitorHost.DataChanged"/> 之后）：概览用 [WeekBaseline, 现在]，空间浏览用近 30 天；
    /// 增长分析页按它自己的范围重新查询。
    /// </summary>
    private async Task ReloadWrites()
    {
        var version = ++_writesVersion;
        var host = _monitorHost;
        var now = DateTime.UtcNow;
        var weekFrom = _growth?.WeekBaseline?.TakenAtUtc ?? now.AddDays(-7);
        var enabled = !_settings.Monitor.IsOff;
        try
        {
            var (week, month) = await Task.Run(() => (host.Load(weekFrom, now), host.Load(now.AddDays(-30), now)));
            if (version != _writesVersion || IsDisposed)
            {
                return;
            }

            (_weekWrites, _monthWrites, _monitorLoadError) = (week, month, null);
            var covered = Monitor.MonitorText.CoveredHours(week.Sessions, weekFrom, now, host.LiveSession, now, host.Agent.Recording);
            _overview.SetWrites(week, covered, (now - weekFrom).TotalHours, enabled && !host.Agent.Recording);
            _explorer.SetWriteIndex(month.Sessions.Count == 0 ? null : month);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (version != _writesVersion || IsDisposed)
            {
                return;
            }

            (_weekWrites, _monthWrites, _monitorLoadError) = (null, null, ex.Message);
            AppLog.Write("写入监控数据读不了：" + ex.Message);
            _overview.SetWrites(null, 0, 0, enabled: false); // 读不了时不提示"没有开过"
            _explorer.SetWriteIndex(null);
        }

        _growthPage.RefreshWrites();
        _settingsPage.SetMonitorInfo(host.DbPath, host.DbBytes);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        // 关闭窗口（含注销、关机、--exit-after-scan）时停止监控：会话先停，最后一次落盘最多等 3 秒（第 5.1.3 节）
        _monitorHost.StopForExit();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _monitorHost.Dispose();
        base.OnFormClosed(e);
    }
}

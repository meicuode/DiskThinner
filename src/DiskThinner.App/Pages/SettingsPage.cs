using DiskThinner.App.Controls;
using DiskThinner.App.Settings;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.App.Pages;

/// <summary>
/// 设置（原型 .p-settings）：左边是分类，右边是各组卡片。写入监控（含阶段 2b 的常驻服务）、快照、清理、规则四组可用；
/// AI（阶段 3）显示为后续版本。改动立即保存到 settings.json。
/// </summary>
internal sealed class SettingsPage : UserControl
{
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background };
    private readonly TableLayoutPanel _stack = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = Point.Empty, BackColor = Color.Transparent };
    private readonly Dictionary<string, Control> _sections = [];

    private readonly CheckBox _daily = new() { Text = L.T("每天"), AutoSize = true, Font = Theme.Ui, AccessibleName = L.T("每日快照") };
    private readonly ComboBox _dailyTime = Combo(L.T("每日快照的时间"), Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToArray(), width: 100);
    private readonly Label _dailyHint = Hint("");
    private readonly CheckBox _auto = new() { Text = L.T("扫描整个盘后，距离上次快照超过 6 小时就自动保存一次"), AutoSize = true, Font = Theme.Ui };
    private readonly ComboBox _minDir = Combo(L.T("只记录大于"), AppSettings.MinDirChoices.Select(m => L.F("{0} MB 的目录", m)).ToArray());
    private readonly ComboBox _keep = Combo(L.T("快照保留时长"), AppSettings.KeepDayChoices.Select(d => L.F("{0} 天", d)).ToArray());
    private readonly Label _dbSize = Ui.Label("", Theme.Small, Theme.Muted);
    private readonly ComboBox _tempAge = Combo(L.T("临时文件最小年龄"), [L.T("只删 24 小时前的"), L.T("只删 7 天前的")]);
    private readonly ComboBox _locked = Combo(L.T("被占用的文件"), [L.T("跳过并记录"), L.T("询问是否关闭程序")]);
    private readonly ListBox _protected = new() { Height = 96, Width = 420, Font = Theme.Ui, IntegralHeight = false, AccessibleName = L.T("我添加的保护目录") };
    private readonly Button _remove = Ui.Button(L.T("移除"));
    private readonly Label _rules = Ui.Label("", Theme.Ui);
    private readonly Label _paths = Ui.Label("", Theme.Small, Theme.Muted);
    private readonly RadioButton _monOff = new() { Text = L.T("关闭"), AutoSize = true, Font = Theme.Ui, Margin = new Padding(0) };
    private readonly RadioButton _monDiag = new() { Text = L.T("临时诊断（推荐）"), AutoSize = true, Font = Theme.Ui, Margin = new Padding(0, 6, 0, 0) };
    private readonly RadioButton _monAgent = new() { Text = L.T("常驻服务"), AutoSize = true, Font = Theme.Ui, Margin = new Padding(0, 6, 0, 0) };
    private readonly FlowLayoutPanel _agentGroup = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Background, Margin = new Padding(0, 8, 0, 0), Padding = new Padding(12, 10, 12, 10), AccessibleName = L.T("常驻监控服务") };
    private readonly Label _agentStatus = Ui.Label("", Theme.Ui);
    private readonly Label _agentHint = Hint("");
    private readonly Button _agentInstall = Ui.Button(L.T("安装…"));
    private readonly Button _agentStartStop = Ui.Button(L.T("停止服务"));
    private readonly Button _agentUpdate = Ui.Button(L.T("更新服务"));
    private readonly Button _agentUninstall = Ui.Button(L.T("卸载…"));
    private readonly Button _agentElevate = Ui.Button(L.T("以管理员身份重启"));
    private readonly Label _monModeHint = Hint("");
    private readonly Label _monKeepHint = Hint("");
    private Monitor.AgentSnapshot _agent = Monitor.AgentSnapshot.None;
    private bool _agentBound;
    private readonly SegmentedControl _monMode = new(L.T("记录模式"), L.T("轻量"), L.T("精确"));
    private readonly ComboBox _monHours = Combo(L.T("写入监控默认时长"), AppSettings.MonitorHourChoices.Select(h => L.F("{0} 小时", h)).ToArray(), width: 120);
    private readonly ComboBox _monKeep = Combo(L.T("写入监控数据保留时长"), AppSettings.MonitorKeepChoices.Select(d => L.F("{0} 天", d)).ToArray(), width: 120);
    private readonly Label _monDbSize = Ui.Label("", Theme.Small, Theme.Muted);
    private string _monitorDbPath = "";
    private (int Rules, string? Error, long SnapshotBytes) _info;
    private AppSettings _settings = new();
    private bool _binding;

    public SettingsPage()
    {
        BackColor = Theme.Background;
        Padding = new Padding(20, 16, 20, 16);
        AccessibleName = L.T("设置");

        var title = Ui.Label(L.T("设置"), new Font(Theme.UiFontName, 15F, FontStyle.Bold));
        title.Dock = DockStyle.Top;
        title.Padding = new Padding(0, 0, 0, 12);

        var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 150, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 4, 12, 0) };
        nav.AccessibleName = L.T("设置分类");

        // 写入监控（docs/phase2a-design.md 3.3 节 P6、5.9.7 节）：三个选项放在同一个容器里，单选按钮才互斥
        var monOptions = Stack(
            _monOff, Hint(L.T("不提示开启诊断。看得到哪里变了，看不到是谁写的。")),
            _monDiag, Hint(L.T("需要时手动开启 1 到 24 小时，到时自动停止。需要管理员权限，不安装后台服务。")),
            _monAgent, Hint(L.T("开机自动记录，不需要打开 DiskThinner。以 SYSTEM 身份运行，只监控、不联网、不删除文件，可以随时卸载。")),
            _agentGroup);
        var agentButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 8, 0, 0) };
        foreach (var b in new[] { _agentInstall, _agentStartStop, _agentUpdate, _agentUninstall, _agentElevate })
        {
            b.Margin = new Padding(0, 0, 8, 0);
        }

        agentButtons.Controls.AddRange([_agentInstall, _agentStartStop, _agentUpdate, _agentUninstall, _agentElevate]);
        _agentStatus.Margin = new Padding(0);
        _agentStatus.MaximumSize = new Size(600, 0);
        _agentGroup.Controls.AddRange([_agentStatus, agentButtons, _agentHint]);
        _agentInstall.Click += (_, _) => AgentActionRequested?.Invoke(this, "install");
        _agentStartStop.Click += (_, _) => AgentActionRequested?.Invoke(this, _agent.Running ? "stop" : "start");
        _agentUpdate.Click += (_, _) => AgentActionRequested?.Invoke(this, "update");
        _agentUninstall.Click += (_, _) => AgentActionRequested?.Invoke(this, "uninstall");
        _agentElevate.Click += (_, _) => AgentActionRequested?.Invoke(this, "elevate");
        monOptions.AccessibleName = L.T("写入监控");
        _monMode.Margin = new Padding(0);
        var monKeepRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _monDbSize.Margin = new Padding(8, 6, 0, 0);
        monKeepRow.Controls.AddRange([_monKeep, _monDbSize]);
        AddSection(nav, L.T("写入监控"), Card(L.T("写入监控（ETW）"),
            Row("", monOptions),
            Row(L.T("记录模式"), Stack(_monMode, Hint(L.T("轻量：只记录新建、删除、重命名，看不到写了多少。精确：额外记录每次写入，能估算每个程序让目录涨了多少；编译、npm install 时开销更大。")), _monModeHint)),
            Row(L.T("默认时长"), Stack(_monHours, Hint(L.T("只对临时诊断有效。")))),
            Row(L.T("保留"), Stack(monKeepRow, Hint(L.T("这么多天内的监控记录全部保留。诊断数据另有 64 MB 的上限，常驻服务的数据另有 256 MB 的上限，超出时先删最早的。")), _monKeepHint))));

        var dailyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _daily.Margin = new Padding(0, 4, 8, 0);
        dailyRow.Controls.AddRange([_daily, _dailyTime]);
        var keepRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _dbSize.Margin = new Padding(8, 6, 0, 0);
        keepRow.Controls.AddRange([_keep, _dbSize]);
        AddSection(nav, L.T("快照"), Card(L.T("快照"),
            Row(L.T("每日快照"), Stack(dailyRow, _dailyHint)),
            Row(L.T("扫描后保存"), _auto),
            Row(L.T("只记录大于"), Stack(_minDir, Hint(L.T("更小的目录并入上级。改小会让快照库变大；文件固定为 100 MB 以上才单独记录。")))),
            Row(L.T("保留"), Stack(keepRow, Hint(L.T("这么多天内的快照全部保留，更早的每周只留一个。"))))));

        AddSection(nav, "AI", Card("AI",
            Row("", Hint(L.T("AI 分析在后续版本（阶段 3）提供：接口配置、发送前预览、MCP 服务都会放在这里。DiskThinner 现在不联网。")))));

        var protectButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        var showBuiltIn = Ui.Button(L.T("查看内置保护名单"));
        var add = Ui.Button(L.T("添加我的保护目录"));
        showBuiltIn.Margin = new Padding(0, 0, 8, 0);
        add.Margin = new Padding(0, 0, 8, 0);
        _remove.Margin = new Padding(0);
        protectButtons.Controls.AddRange([showBuiltIn, add, _remove]);
        _protected.Margin = new Padding(0, 8, 0, 0);
        AddSection(nav, L.T("清理"), Card(L.T("清理"),
            Row(L.T("临时文件"), Stack(_tempAge, Hint(L.T("避免删掉正在安装或运行的程序刚写的临时文件。规则本身更严格时以规则为准。")))),
            Row(L.T("被占用的文件"), Stack(_locked, Hint(L.T("询问时，同意了才请程序正常关闭，不会强制结束。确认清理时还可以再改。")))),
            Row(L.T("保护名单"), Stack(protectButtons, _protected,
                Hint(L.T("内置名单不能删除，只能追加。添加的目录和里面的所有内容都不会被清理：执行清理时立即生效，清理计划在重新扫描后更新。"))))));

        var openData = Ui.Button(L.T("打开数据目录"));
        openData.Margin = new Padding(0, 8, 0, 0);
        AddSection(nav, L.T("规则"), Card(L.T("规则"),
            Row(L.T("内置规则"), _rules),
            Row(L.T("本地规则"), Hint(L.T("后续版本提供（保存 AI 结论为本地规则、导入、导出）。"))),
            Row(L.T("数据位置"), Stack(_paths, openData))));

        _scroll.Controls.Add(_stack);
        _scroll.Resize += (_, _) => FitWidth();
        Controls.Add(_scroll);
        Controls.Add(nav);
        Controls.Add(title);

        _daily.CheckedChanged += (_, _) =>
        {
            if (!_binding)
            {
                ScheduleChanged?.Invoke(this, (_daily.Checked, (string)_dailyTime.SelectedItem!));
            }
        };
        _dailyTime.SelectedIndexChanged += (_, _) =>
        {
            if (_binding)
            {
                return;
            }

            Change(_settings with { Snapshot = _settings.Snapshot with { DailyTime = (string)_dailyTime.SelectedItem! } });
            if (_daily.Checked)
            {
                ScheduleChanged?.Invoke(this, (true, (string)_dailyTime.SelectedItem!));
            }
        };
        _monOff.CheckedChanged += (_, _) =>
        {
            if (_monOff.Checked)
            {
                Change(_settings with { Monitor = _settings.Monitor with { Enabled = MonitorSettings.Off } });
            }
        };
        _monDiag.CheckedChanged += (_, _) =>
        {
            if (_monDiag.Checked)
            {
                Change(_settings with { Monitor = _settings.Monitor with { Enabled = MonitorSettings.Diagnostic } });
            }
        };
        _monAgent.CheckedChanged += (_, _) =>
        {
            if (_monAgent.Checked)
            {
                Change(_settings with { Monitor = _settings.Monitor with { Enabled = MonitorSettings.Agent } });
            }

            RenderAgent();
        };
        _monMode.SelectedIndexChanged += (_, _) =>
        {
            Change(_settings with { Monitor = _settings.Monitor with { Mode = _monMode.SelectedIndex == 1 ? MonitorSettings.Precise : MonitorSettings.Light } });
            if (!_binding && _agentBound)
            {
                AgentConfigureRequested?.Invoke(this, (_monMode.SelectedIndex == 1 ? "precise" : "light", null));
            }
        };
        _monHours.SelectedIndexChanged += (_, _) => Change(_settings with { Monitor = _settings.Monitor with { DefaultHours = AppSettings.MonitorHourChoices[_monHours.SelectedIndex] } });
        _monKeep.SelectedIndexChanged += (_, _) =>
        {
            Change(_settings with { Monitor = _settings.Monitor with { KeepDays = AppSettings.MonitorKeepChoices[_monKeep.SelectedIndex] } });
            if (!_binding && _agentBound)
            {
                AgentConfigureRequested?.Invoke(this, (null, AppSettings.MonitorKeepChoices[_monKeep.SelectedIndex]));
            }
        };
        _auto.CheckedChanged += (_, _) => Change(_settings with { Snapshot = _settings.Snapshot with { AutoAfterScan = _auto.Checked } });
        _minDir.SelectedIndexChanged += (_, _) => Change(_settings with { Snapshot = _settings.Snapshot with { MinDirMb = AppSettings.MinDirChoices[_minDir.SelectedIndex] } });
        _keep.SelectedIndexChanged += (_, _) => Change(_settings with { Snapshot = _settings.Snapshot with { KeepDays = AppSettings.KeepDayChoices[_keep.SelectedIndex] } });
        _tempAge.SelectedIndexChanged += (_, _) => Change(_settings with { Cleanup = _settings.Cleanup with { TempMinAgeDays = AppSettings.TempAgeChoices[_tempAge.SelectedIndex] } });
        _locked.SelectedIndexChanged += (_, _) => Change(_settings with { Cleanup = _settings.Cleanup with { AskWhenLocked = _locked.SelectedIndex == 1 } });
        showBuiltIn.Click += (_, _) => ShowBuiltInProtection();
        add.Click += (_, _) => AddProtected();
        _remove.Click += (_, _) => RemoveProtected();
        _protected.SelectedIndexChanged += (_, _) => _remove.Enabled = _protected.SelectedIndex >= 0;
        openData.Click += (_, _) =>
        {
            var dir = Path.GetDirectoryName(AppSettings.DefaultPath)!;
            Directory.CreateDirectory(dir);
            ShellActions.OpenLocation(dir, isDirectory: true, FindForm());
        };
        Bind(_settings);
    }

    /// <summary>设置改了（已经是规范化的值），由主窗口保存并应用。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>常驻服务的按钮：install / start / stop / update / uninstall / elevate。</summary>
    public event EventHandler<string>? AgentActionRequested;

    /// <summary>选了常驻服务、服务在运行时改了记录模式或保留天数（管理员）：发给服务。</summary>
    public event EventHandler<(string? Mode, int? KeepDays)>? AgentConfigureRequested;

    /// <summary>开启或关闭每日快照（注册或移除计划任务）。</summary>
    public event EventHandler<(bool Enabled, string Time)>? ScheduleChanged;

    public AppSettings Settings => _settings;

    internal ListBox ProtectedList => _protected;

    internal ComboBox TempAgeBox => _tempAge;

    public void SetSettings(AppSettings settings) => Bind(settings);

    public void SetSchedule(ScheduledSnapshot.Status status)
    {
        _binding = true;
        _daily.Checked = status.Registered;
        if (status.Registered && status.Time is { } t && !_dailyTime.Items.Contains(t))
        {
            _dailyTime.Items.Add(t);
        }

        if (status.Registered && status.Time is { } time)
        {
            _dailyTime.SelectedItem = time;
        }

        _binding = false;
        _dailyHint.Text = status.Registered
            ? status.Highest ? L.T("已开启，以最高权限运行（MFT 扫描，几秒完成）。") : L.T("已开启，以普通权限运行（遍历扫描，较慢）。以管理员身份打开 DiskThinner 再开启可以改用 MFT。")
            : L.T("通过 Windows 计划任务运行 DiskThinner.exe snapshot，不需要打开界面。");
    }

    public void SetInfo(int rules, string? rulesError, long snapshotDbBytes)
    {
        _info = (rules, rulesError, snapshotDbBytes);
        _rules.Text = rulesError is null ? L.F("{0} 条 · 随程序更新", rules) : L.F("没有加载：{0}", rulesError);
        _dbSize.Text = snapshotDbBytes > 0 ? L.F("当前占用 {0}", ByteSize.Format(snapshotDbBytes)) : "";
        var root = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        var monitor = _monitorDbPath.Length > 0 ? _monitorDbPath : Path.Combine(root, "monitor.db");
        _paths.Text = L.F("设置：{0}\n快照：{1}\n写入监控：{2}\n操作记录：{3}\n日志：{4}", PathDisplay.Shorten(AppSettings.DefaultPath), PathDisplay.Shorten(Path.Combine(root, "snapshots.db")),
            PathDisplay.Shorten(monitor), PathDisplay.Shorten(Path.Combine(root, "user.db")), PathDisplay.Shorten(AppLog.Directory));
    }

    /// <summary>monitor.db 的位置和占用（"保留"一行的"当前占用"）。</summary>
    public void SetMonitorInfo(string dbPath, long bytes)
    {
        _monitorDbPath = dbPath;
        _diagDbBytes = bytes;
        RenderDbSize();
        SetInfo(_info.Rules, _info.Error, _info.SnapshotBytes);
    }

    private long _diagDbBytes;

    /// <summary>"当前占用 1.2 MB"；装了常驻服务时是"诊断 1.2 MB · 常驻服务 12.4 MB"（原型的设置页）。</summary>
    private void RenderDbSize()
    {
        if (_agent.Status is { } s)
        {
            _monDbSize.Text = L.F("诊断 {0} · 常驻服务 {1}", _diagDbBytes > 0 ? ByteSize.Format(_diagDbBytes) : L.T("没有记录"), ByteSize.Format(s.DbBytes));
            return;
        }

        _monDbSize.Text = _diagDbBytes > 0 ? L.F("当前占用 {0}", ByteSize.Format(_diagDbBytes)) : L.T("还没有记录");
    }

    internal string MonitorDbSizeText => _monDbSize.Text;

    private bool _elevated;
    private bool _agentSource = true;

    /// <summary>常驻服务的状态（主窗口每次查完服务后调用，docs/phase2b-design.md 3.2 节 Q1）。</summary>
    public void SetAgent(Monitor.AgentSnapshot agent, bool elevated, bool sourceAvailable)
    {
        _agent = agent;
        _elevated = elevated;
        _agentSource = sourceAvailable;
        RenderAgent();
    }

    /// <summary>
    /// 服务一组：选了常驻服务或者已经装了时显示。没有安装时只有"安装…"；装了时显示状态和按钮（普通权限下只有"以管理员身份重启"）。
    /// 选了常驻服务、服务在记录时，记录模式和保留显示服务的值，修改发给服务；普通权限下这两项不能改。
    /// </summary>
    private void RenderAgent()
    {
        var a = _agent;
        RenderDbSize();
        var bound = _monAgent.Checked && a.Running && a.ProtocolOk && a.Status is not null;
        _agentBound = bound;
        if (bound)
        {
            var was = _binding;
            _binding = true;
            _monMode.SelectedIndex = a.Precise ? 1 : 0;
            _monKeep.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MonitorKeepChoices, a.Status!.KeepDays));
            _binding = was;
        }

        _monModeHint.Text = !bound ? "" : _elevated
            ? L.T("选了常驻服务：记录模式和下面的\u201c保留\u201d作用于服务，所有用户共用。")
            : L.T("选了常驻服务：记录模式和保留天数是服务的设置，以管理员身份打开 DiskThinner 才能修改。");
        _monModeHint.Visible = bound;
        _monKeepHint.Text = bound ? L.T("改了之后服务在下一次删除旧数据时按新的天数（每天一次）。") : "";
        _monKeepHint.Visible = bound;
        _monMode.Enabled = !bound || _elevated;
        _monKeep.Enabled = !bound || _elevated;

        var show = _monAgent.Checked || a.Installed;
        _agentGroup.Visible = show;
        if (!show)
        {
            return;
        }

        foreach (var b in new[] { _agentInstall, _agentStartStop, _agentUpdate, _agentUninstall, _agentElevate })
        {
            b.Visible = false;
        }

        if (!a.Installed)
        {
            _agentStatus.Text = L.T("没有安装");
            _agentStatus.ForeColor = Theme.Muted;
            _agentInstall.Visible = true;
            _agentInstall.Text = _elevated ? L.T("安装…") : L.T("以管理员身份重启并安装");
            _agentInstall.AccessibleName = _agentInstall.Text;
            _agentInstall.Enabled = _agentSource;
            _agentHint.Text = _agentSource ? L.T("安装前会说明服务做什么、装在哪、数据在哪。没有安装时，临时诊断照常可用。")
                : L.T("这个发布包里没有服务程序（Agent\\ 目录），请下载完整的发布包。");
            return;
        }

        var problem = a.Problem;
        if (problem is null)
        {
            var s = a.Status!;
            var since = s.RecordingSinceUtc is { } r ? r.ToLocalTime().ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "—";
            _agentStatus.Text = L.F("运行中 · {0} · 版本 {1} · 从 {2} 开始记录 · 数据 {3}", Monitor.MonitorText.Mode(a.Precise ? DiskThinner.Monitor.MonitorMode.Precise : DiskThinner.Monitor.MonitorMode.Light),
                a.AgentVersion ?? "—", since, ByteSize.Format(s.DbBytes)) + (a.NeedsUpdate ? L.F(" · 可以更新到 {0}", Agent.AppVersion.Current) : "");
            _agentStatus.ForeColor = Theme.Text;
        }
        else
        {
            _agentStatus.Text = problem;
            _agentStatus.ForeColor = a.Service.State == "stopped" ? Theme.Muted : Theme.WarnText;
        }

        if (_elevated)
        {
            _agentStartStop.Visible = true;
            _agentStartStop.Text = a.Running ? L.T("停止服务") : L.T("启动服务");
            _agentStartStop.AccessibleName = _agentStartStop.Text;
            _agentUpdate.Visible = a.NeedsUpdate || !a.Running;
            _agentUninstall.Visible = true;
            _agentHint.Text = L.T("服务的记录模式和保留天数是所有用户共用的，选了常驻服务时在下面修改。停止服务后不再记录，直到再启动或重启电脑。");
        }
        else
        {
            _agentElevate.Visible = true;
            _agentHint.Text = L.T("启动、停止、更新和卸载服务需要管理员权限。");
        }
    }

    internal RadioButton MonitorOff => _monOff;

    internal RadioButton MonitorDiagnostic => _monDiag;

    internal RadioButton MonitorAgent => _monAgent;

    internal string AgentStatusText => _agentGroup.Visible ? _agentStatus.Text : "";

    /// <summary>服务一组里看得见的按钮（测试用）。</summary>
    internal IEnumerable<Button> AgentButtons => new[] { _agentInstall, _agentStartStop, _agentUpdate, _agentUninstall, _agentElevate }.Where(b => _agentGroup.Visible && b.Visible);

    internal SegmentedControl MonitorMode => _monMode;

    /// <summary>滚动到某一组（例如工具栏的写入监控胶囊在设置为"关闭"时跳到"写入监控"）。</summary>
    public void ShowSection(string name)
    {
        if (_sections.TryGetValue(name, out var card))
        {
            _scroll.ScrollControlIntoView(card);
        }
    }

    private void Bind(AppSettings s)
    {
        _binding = true;
        _settings = s;
        _dailyTime.SelectedItem = _dailyTime.Items.Contains(s.Snapshot.DailyTime) ? s.Snapshot.DailyTime : "12:00";
        _auto.Checked = s.Snapshot.AutoAfterScan;
        _minDir.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MinDirChoices, s.Snapshot.MinDirMb));
        _keep.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.KeepDayChoices, s.Snapshot.KeepDays));
        _tempAge.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.TempAgeChoices, s.Cleanup.TempMinAgeDays));
        _locked.SelectedIndex = s.Cleanup.AskWhenLocked ? 1 : 0;
        _monOff.Checked = s.Monitor.IsOff;
        _monDiag.Checked = s.Monitor.Enabled == MonitorSettings.Diagnostic;
        _monAgent.Checked = s.Monitor.IsAgent;
        _monMode.SelectedIndex = s.Monitor.IsPrecise ? 1 : 0;
        _monHours.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MonitorHourChoices, s.Monitor.DefaultHours));
        _monKeep.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MonitorKeepChoices, s.Monitor.KeepDays));
        _protected.Items.Clear();
        foreach (var p in s.Cleanup.ProtectedPaths)
        {
            _protected.Items.Add(p);
        }

        _remove.Enabled = false;
        _binding = false;
        RenderAgent();
    }

    private void Change(AppSettings next)
    {
        if (_binding)
        {
            return;
        }

        _settings = next.Normalized();
        SettingsChanged?.Invoke(this, _settings);
    }

    private void AddProtected()
    {
        using var dlg = new FolderBrowserDialog { Description = L.T("选择不希望被清理的目录（里面的所有内容都会受保护）"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var path = Path.GetFullPath(dlg.SelectedPath).TrimEnd('\\');
        if (!AppSettings.IsValidProtectedPath(path))
        {
            MessageBox.Show(FindForm(), L.T("这个位置不能作为保护目录：需要是本机磁盘上的目录，路径里不能有 * ? { }，也不能是整个盘。"), L.T("保护名单"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_settings.Cleanup.ProtectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var next = _settings with { Cleanup = _settings.Cleanup with { ProtectedPaths = [.. _settings.Cleanup.ProtectedPaths, path] } };
        Bind(next.Normalized());
        SettingsChanged?.Invoke(this, _settings);
    }

    private void RemoveProtected()
    {
        if (_protected.SelectedItem is not string path)
        {
            return;
        }

        var next = _settings with { Cleanup = _settings.Cleanup with { ProtectedPaths = _settings.Cleanup.ProtectedPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() } };
        Bind(next.Normalized());
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ShowBuiltInProtection()
    {
        using var dlg = new Form
        {
            Text = L.T("内置保护名单"),
            Font = Theme.Ui,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96F, 96F),
            ClientSize = new Size(820, 480),
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, AccessibleName = L.T("内置保护名单") };
        list.Columns.Add(L.T("位置"), 420);
        list.Columns.Add(L.T("级别"), 90);
        list.Columns.Add(L.T("原因"), 280);
        foreach (var e in ProtectionList.BuiltIn)
        {
            list.Items.Add(new ListViewItem([e.Pattern, e.Level == ProtectionLevel.None ? L.T("例外（可清理）") : ProtectionList.Display(e.Level), e.Reason]));
        }

        var note = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(10, 8, 10, 4), Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false,
            Text = L.T("保护名单优先于任何规则：硬保护的位置任何规则都不能清理；\u201c系统设置\u201d只给引导；\u201c用户文件\u201d只有实测过的规则可以提出建议。\u201c例外\u201d是在受保护的目录里放开的子目录（如 Windows\\Temp）。") };
        dlg.Controls.Add(list);
        dlg.Controls.Add(note);
        dlg.Load += (_, _) =>
        {
            int[] widths = [420, 90, 280];
            for (var c = 0; c < widths.Length; c++)
            {
                list.Columns[c].Width = Ui.Scale(dlg, widths[c]);
            }

            note.Height = Ui.Scale(dlg, 44);
        };
        dlg.ShowDialog(FindForm());
    }

    private void AddSection(FlowLayoutPanel nav, string name, Control card)
    {
        _sections[name] = card;
        card.Margin = new Padding(0, 0, 0, 12);
        _stack.Controls.Add(card);
        var link = new LinkLabel { Text = name, AutoSize = true, Font = Theme.Ui, LinkColor = Theme.Text, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 4, 0, 8) };
        link.LinkClicked += (_, _) => _scroll.ScrollControlIntoView(card);
        nav.Controls.Add(link);
    }

    /// <summary>卡片自动长高，宽度跟着页面：用最小 / 最大宽度把自动大小的宽度定住。</summary>
    private void FitWidth()
    {
        var width = Math.Max(400, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (Control card in _stack.Controls)
        {
            if (card.MinimumSize.Width != width)
            {
                card.MinimumSize = new Size(width, 0);
                card.MaximumSize = new Size(width, 0);
            }
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        FitWidth();
    }

    /// <summary>卡片里滚动时不要跳回顶部（和概览一样）。</summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    private static Panel Card(string title, params Control[] rows)
    {
        var card = new Panel { BackColor = Theme.Card, Padding = new Padding(18, 14, 18, 10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var table = new TableLayoutPanel { Location = new Point(18, 14), ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0) };
        var heading = Ui.Label(title, Theme.PageTitle);
        heading.Margin = new Padding(0, 0, 0, 8);
        table.Controls.Add(heading);
        foreach (var r in rows)
        {
            table.Controls.Add(r);
        }

        card.Controls.Add(table);
        card.AccessibleName = title;
        return card;
    }

    /// <summary>原型的 .form-row：左边标签，右边内容。</summary>
    private static TableLayoutPanel Row(string label, Control content)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lbl = Ui.Label(label, Theme.Ui, Theme.Muted);
        lbl.Margin = new Padding(0, 5, 8, 0);
        row.Controls.Add(lbl, 0, 0);
        row.Controls.Add(content, 1, 0);
        if (!string.IsNullOrEmpty(label))
        {
            content.AccessibleName ??= label;
        }

        return row;
    }

    private static FlowLayoutPanel Stack(params Control[] items)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        flow.Controls.AddRange(items);
        return flow;
    }

    private static Label Hint(string text)
    {
        var l = Ui.Label(text, Theme.Small, Theme.Muted);
        l.MaximumSize = new Size(620, 0);
        l.Margin = new Padding(0, 4, 0, 0);
        return l;
    }

    private static ComboBox Combo(string name, string[] items, int width = 220)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = Theme.Ui, AccessibleName = name, Margin = new Padding(0) };
        c.Items.AddRange(items);
        return c;
    }
}

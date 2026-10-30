namespace DiskThinner.App;

/// <summary>
/// 命令行参数。
///   --scan C:            启动后扫描指定磁盘（以管理员身份重启时会带上）
///   --walk               强制使用遍历扫描
/// 下面几项用于自动化验证：
///   --select C:\Users    扫描完成后选中这个路径
///   --screenshot a.png   扫描完成后截取窗口
///   --report r.txt       扫描完成后写入耗时和内存
///   --exit-after-scan    完成上面几步后退出
///   --primary-screen     窗口放在主显示器上（--secondary-screen 放在另一个显示器上）
///   --page explorer      启动后打开的页面（overview / explorer / growth / cleanup / log）
///   --snapshot-db a.db   快照库的位置
///   --no-auto-snapshot   扫描完整个盘后不自动保存快照
///   --user-db u.db       操作记录库的位置
///   --settings s.json    设置文件的位置
///   --show-confirm       截图后再截一张"确认清理"对话框（只显示，不执行）
///   --view owner         空间浏览的视图（dir / owner / kind，对应按目录、按归属汇总、按类别汇总）
///   --no-rules           不加载规则和已装软件（对比内存用）
/// 写入监控（docs/phase2a-design.md 5.9.8 节）：
///   --monitor-db m.db    写入监控库的位置
///   --start-monitor 4    启动后开始写入监控（小时，1 到 24；以管理员身份重启时带上，只在管理员下执行）
///   --monitor-mode m     light / precise，和 --start-monitor、--monitor-seconds 一起用
///   --monitor-seconds 20 启动后开始写入监控（秒，10 到 86400，自动化验证用，优先于 --start-monitor）
///   --show-monitor-dialog 截图后再截一张"开启写入监控"对话框（只显示，不开始）
/// 常驻服务（docs/phase2b-design.md 5.6 节）：
///   --install-agent precise:90  启动后安装常驻服务（以管理员身份重启时带上，用户已经在对话框里确认过；只在管理员下执行）
///   --agent-source 目录   服务程序所在的目录（默认是 DiskThinner.exe 旁边的 Agent\，开发和测试用）
///   --agent-pipe 名字     服务的管道名（测试用另一个）；--agent-service 名字：服务名
///   --show-agent-dialog   截图后再截一张"安装常驻监控服务"对话框（只显示，不安装）
/// </summary>
internal sealed record AppOptions
{
    public string? Drive { get; init; }
    public bool ForceWalk { get; init; }
    public string? SelectPath { get; init; }
    public string? Screenshot { get; init; }
    public string? Report { get; init; }
    public bool ExitAfterScan { get; init; }

    /// <summary>窗口放在主显示器上（多显示器、缩放比例不同时用来验证高 DPI）。</summary>
    public bool PrimaryScreen { get; init; }

    /// <summary>窗口放在第一个非主显示器上（没有时放在主显示器上）。</summary>
    public bool SecondaryScreen { get; init; }

    /// <summary>启动后打开的页面：overview（默认）或 explorer。</summary>
    public string? StartPage { get; init; }

    /// <summary>空间浏览的初始视图：0 按目录，1 按归属汇总，2 按类别汇总。</summary>
    public int StartView { get; init; }

    /// <summary>不加载规则和已装软件。</summary>
    public bool NoRules { get; init; }

    /// <summary>快照库的位置（默认 %LOCALAPPDATA%\DiskThinner\snapshots.db；验证时指向别处，不影响真实数据）。</summary>
    public string? SnapshotDb { get; init; }

    /// <summary>扫描完整个盘后不自动保存快照。</summary>
    public bool NoAutoSnapshot { get; init; }

    /// <summary>操作记录库的位置（默认 %LOCALAPPDATA%\DiskThinner\user.db）。</summary>
    public string? UserDb { get; init; }

    /// <summary>截图后再打开"确认清理"对话框截一张（不会执行，截完就关掉）。</summary>
    public bool ShowConfirm { get; init; }

    /// <summary>设置文件的位置（默认 %LOCALAPPDATA%\DiskThinner\settings.json；验证时指向别处）。</summary>
    public string? SettingsFile { get; init; }

    /// <summary>写入监控库的位置（默认 %LOCALAPPDATA%\DiskThinner\monitor.db）。</summary>
    public string? MonitorDb { get; init; }

    /// <summary>启动后自动开始的写入监控时长（null 表示不自动开始）。</summary>
    public TimeSpan? StartMonitor { get; init; }

    /// <summary>自动开始时的模式（null 时用设置里的）。</summary>
    public bool? MonitorPrecise { get; init; }

    /// <summary>由 --monitor-seconds 给出（自动化验证），不经过账户不一致的对话框。</summary>
    public bool MonitorForAutomation { get; init; }

    public bool ShowMonitorDialog { get; init; }

    /// <summary>启动后安装常驻服务（模式, 保留天数）；null 表示不安装。</summary>
    public (bool Precise, int KeepDays)? InstallAgent { get; init; }

    public string? AgentSource { get; init; }

    public string? AgentPipe { get; init; }

    public string? AgentService { get; init; }

    public bool ShowAgentDialog { get; init; }

    /// <summary>参数里被忽略的值（超出范围、不认识），启动后记日志。</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    public static AppOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        // 写入监控的时长：超出范围或不是整数时忽略并记下（第 1.3 节第 6 条）
        var ignored = new List<string>();
        int? Int(string name, int min, int max)
        {
            if (Value(name) is not { } text)
            {
                return null;
            }

            if (int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= min && v <= max)
            {
                return v;
            }

            ignored.Add($"{name} {text}");
            return null;
        }

        var seconds = Int("--monitor-seconds", 10, 86_400);
        var hours = Int("--start-monitor", 1, 24);
        bool? precise = Value("--monitor-mode")?.ToLowerInvariant() switch
        {
            "precise" => true,
            "light" => false,
            null => null,
            var other => Ignore("--monitor-mode " + other),
        };

        bool? Ignore(string what)
        {
            ignored.Add(what);
            return null;
        }

        return new AppOptions
        {
            MonitorDb = Value("--monitor-db"),
            StartMonitor = seconds is { } s ? TimeSpan.FromSeconds(s) : hours is { } h ? TimeSpan.FromHours(h) : null,
            MonitorPrecise = precise,
            MonitorForAutomation = seconds is not null,
            ShowMonitorDialog = Flag("--show-monitor-dialog"),
            InstallAgent = ParseInstall(Value("--install-agent"), ignored),
            AgentSource = Value("--agent-source") is { } src && Directory.Exists(src) ? Path.GetFullPath(src) : null,
            AgentPipe = SafeName(Value("--agent-pipe")),
            AgentService = SafeName(Value("--agent-service")),
            ShowAgentDialog = Flag("--show-agent-dialog"),
            Ignored = ignored,
            Drive = NormalizeDrive(Value("--scan")) ?? ExistingFolder(Value("--scan")),
            ForceWalk = Flag("--walk"),
            SelectPath = Value("--select"),
            Screenshot = Value("--screenshot"),
            Report = Value("--report"),
            ExitAfterScan = Flag("--exit-after-scan"),
            PrimaryScreen = Flag("--primary-screen"),
            SecondaryScreen = Flag("--secondary-screen"),
            StartPage = Value("--page") is { } page && page is "overview" or "explorer" or "growth" or "cleanup" or "log" or "settings" ? page : null,
            SettingsFile = Value("--settings"),
            SnapshotDb = Value("--snapshot-db"),
            NoAutoSnapshot = Flag("--no-auto-snapshot"),
            UserDb = Value("--user-db"),
            ShowConfirm = Flag("--show-confirm"),
            StartView = Value("--view")?.ToLowerInvariant() switch
            {
                "owner" => 1,
                "kind" => 2,
                _ => 0,
            },
            NoRules = Flag("--no-rules"),
        };
    }

    /// <summary>"precise:90" → (true, 90)；格式不对时忽略并记下。</summary>
    private static (bool, int)? ParseInstall(string? value, List<string> ignored)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Split(':');
        if (parts.Length == 2 && parts[0] is "precise" or "light" && int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var keep)
            && Settings.AppSettings.MonitorKeepChoices.Contains(keep))
        {
            return (parts[0] == "precise", keep);
        }

        ignored.Add("--install-agent " + value);
        return null;
    }

    /// <summary>管道名、服务名：只允许字母、数字、点、横线、下划线。</summary>
    private static string? SafeName(string? value) =>
        value is { Length: > 0 and <= 120 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? value : null;

    /// <summary>也可以只扫描一个文件夹（总是用遍历扫描）。</summary>
    private static string? ExistingFolder(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? Path.GetFullPath(value) : null;

    /// <summary>"C"、"C:"、"C:\" 都规范成 "C:\"；其他输入忽略。</summary>
    internal static string? NormalizeDrive(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim().TrimEnd('\\');
        if (v.Length == 1)
        {
            v += ":";
        }

        return v.Length == 2 && v[1] == ':' && char.IsAsciiLetter(v[0]) ? char.ToUpperInvariant(v[0]) + @":\" : null;
    }
}

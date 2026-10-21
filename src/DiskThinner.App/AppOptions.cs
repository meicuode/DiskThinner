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
///   --page explorer      启动后打开的页面（overview / explorer / growth）
///   --snapshot-db a.db   快照库的位置
///   --no-auto-snapshot   扫描完整个盘后不自动保存快照
///   --view owner         空间浏览的视图（dir / owner / kind，对应按目录、按归属汇总、按类别汇总）
///   --no-rules           不加载规则和已装软件（对比内存用）
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

    public static AppOptions Parse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        bool Flag(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        return new AppOptions
        {
            Drive = NormalizeDrive(Value("--scan")) ?? ExistingFolder(Value("--scan")),
            ForceWalk = Flag("--walk"),
            SelectPath = Value("--select"),
            Screenshot = Value("--screenshot"),
            Report = Value("--report"),
            ExitAfterScan = Flag("--exit-after-scan"),
            PrimaryScreen = Flag("--primary-screen"),
            SecondaryScreen = Flag("--secondary-screen"),
            StartPage = Value("--page") is { } page && page is "overview" or "explorer" or "growth" ? page : null,
            SnapshotDb = Value("--snapshot-db"),
            NoAutoSnapshot = Flag("--no-auto-snapshot"),
            StartView = Value("--view")?.ToLowerInvariant() switch
            {
                "owner" => 1,
                "kind" => 2,
                _ => 0,
            },
            NoRules = Flag("--no-rules"),
        };
    }

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

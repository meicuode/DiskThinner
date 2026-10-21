using System.Diagnostics;
using System.Text.Json;
using DiskThinner.Inventory.Native;
using Microsoft.Win32;

namespace DiskThinner.Inventory;

/// <summary>
/// 收集已装软件（研发方案 3.2 节）：注册表卸载项、商店应用、npm 全局包、scoop、PATH 和 ~\.local\bin 里的命令行工具、正在运行的进程。
/// 每个来源单独捕获错误，一个来源失败不影响其他来源。只读注册表和文件，不需要管理员权限。
/// </summary>
public static class InventoryCollector
{
    private const int MaxExesPerDir = 100;
    private const int MaxCommandLineTools = 1500;

    public static InventorySnapshot Collect(CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var apps = new List<InstalledApp>();
        var errors = new List<string>();

        void Run(string name, Func<IEnumerable<InstalledApp>> source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                apps.AddRange(source());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException
                                           or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                errors.Add($"{name}：{ex.Message}");
            }
        }

        Run("注册表卸载项", ReadRegistry);
        Run("商店应用", ReadStorePackages);
        Run("npm 全局包", ReadNpmGlobal);
        Run("scoop", ReadScoop);
        Run("命令行工具", ReadCommandLineTools);
        Run("运行中的进程", ReadProcesses);
        return new InventorySnapshot(apps, sw.Elapsed, errors);
    }

    private static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\');

    private static readonly string WindowsDir = Folder(Environment.SpecialFolder.Windows);

    private static bool IsUnder(string path, string root) =>
        root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && (path.Length == root.Length || path[root.Length] == '\\');

    // ---------- 注册表卸载项 ----------

    private static IEnumerable<InstalledApp> ReadRegistry()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledApp>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null)
            {
                continue;
            }

            foreach (var keyName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(keyName);
                if (key is null || FromUninstallKey(keyName, key) is not { } app)
                {
                    continue;
                }

                if (seen.Add(app.Name + "|" + app.InstallLocation))
                {
                    result.Add(app);
                }
            }
        }

        return result;
    }

    private static InstalledApp? FromUninstallKey(string keyName, RegistryKey key)
    {
        if (key.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        // 补丁和更新挂在主程序下面，不单独算
        if (key.GetValue("ParentKeyName") is string || key.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var location = NormalizeDir(key.GetValue("InstallLocation") as string);
        var exe = ExeFromCommand(key.GetValue("DisplayIcon") as string);
        if (location is null && exe is not null && UsableInstallDir(Path.GetDirectoryName(exe)) is { } exeDir)
        {
            location = exeDir;
        }

        if (location is null && ExeFromCommand(key.GetValue("UninstallString") as string) is { } uninstaller)
        {
            location = UsableInstallDir(Path.GetDirectoryName(uninstaller));
        }

        // 系统组件（运行库等）没有安装位置时对判断归属没有帮助
        if (key.GetValue("SystemComponent") is int and 1 && location is null)
        {
            return null;
        }

        return new InstalledApp
        {
            Name = AppNames.CleanDisplayName(display),
            RawName = display,
            Publisher = AppNames.CleanPublisher(key.GetValue("Publisher") as string),
            Version = key.GetValue("DisplayVersion") as string,
            InstallLocation = location,
            ExePath = exe,
            Source = AppSource.Registry,
            Id = keyName,
        };
    }

    /// <summary>
    /// 从 DisplayIcon 或 UninstallString 里取出 exe 路径：去掉引号、参数和图标序号（"C:\a\b.exe",0 → C:\a\b.exe）。
    /// msiexec、rundll32 这类系统程序返回 null。
    /// </summary>
    internal static string? ExeFromCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var s = command.Trim();
        string path;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            path = end > 1 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            path = exe > 0 ? s[..(exe + 4)] : s.Split(',')[0];
        }

        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(path) || IsUnder(path, WindowsDir))
        {
            return null;
        }

        return path;
    }

    /// <summary>安装目录不能是安装包缓存、系统目录或盘符根这样的公共位置。</summary>
    internal static string? UsableInstallDir(string? dir)
    {
        var d = NormalizeDir(dir);
        if (d is null || IsUnder(d, WindowsDir) || d.Contains(@"\Package Cache\", StringComparison.OrdinalIgnoreCase)
            || d.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase) || d.EndsWith(@"\Installer", StringComparison.OrdinalIgnoreCase)
            || d.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return d;
    }

    /// <summary>去掉引号和结尾的 \，展开环境变量；不是完整路径或太浅（盘符根、Program Files 本身）时返回 null。</summary>
    internal static string? NormalizeDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var d = Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"').Trim()).TrimEnd('\\');
        if (d.Length < 4 || !Path.IsPathFullyQualified(d))
        {
            return null;
        }

        foreach (var common in CommonRoots)
        {
            if (string.Equals(d, common, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return d;
    }

    private static readonly string[] CommonRoots =
    [
        Folder(Environment.SpecialFolder.ProgramFiles), Folder(Environment.SpecialFolder.ProgramFilesX86), Folder(Environment.SpecialFolder.CommonApplicationData),
        Folder(Environment.SpecialFolder.LocalApplicationData), Folder(Environment.SpecialFolder.ApplicationData), Folder(Environment.SpecialFolder.UserProfile),
        Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), "Programs"), WindowsDir,
    ];

    // ---------- 商店应用 ----------

    private static IEnumerable<InstalledApp> ReadStorePackages()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (root is null)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fullName in root.GetSubKeyNames())
        {
            if (FamilyName(fullName) is not { } family || !seen.Add(family))
            {
                continue;
            }

            using var key = root.OpenSubKey(fullName);
            var display = key?.GetValue("DisplayName") as string;

            // 显示名常常是资源引用：@{...} 形式的用到时再尝试解析（能解析一部分）；ms-resource: 形式的解析不了，直接用包名
            var unresolved = display is null || display.StartsWith('@') || display.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase);
            yield return new InstalledApp
            {
                Name = unresolved ? AppNames.FromPackageFamily(family) : display!,
                RawName = display is not null && display.StartsWith('@') ? display : null,
                InstallLocation = NormalizeDir(key?.GetValue("PackageRootFolder") as string),
                Source = AppSource.Store,
                Id = family,
            };
        }
    }

    /// <summary>包全名 "名称_版本_架构_资源_发布者" → 包族名 "名称_发布者"。资源包（带资源 ID 的）返回 null。</summary>
    internal static string? FamilyName(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        if (parts.Length != 5 || parts[3].Length > 0)
        {
            return null;
        }

        return parts[0] + "_" + parts[4];
    }

    /// <summary>解析商店应用的间接显示名（@{...ms-resource://...}）。解析不了时返回 null。只在需要时调用（每次要读包的资源文件）。</summary>
    public static unsafe string? ResolveIndirectName(string? indirect)
    {
        if (string.IsNullOrEmpty(indirect) || !indirect.StartsWith('@'))
        {
            return indirect;
        }

        var buffer = stackalloc char[512];
        return Win32.SHLoadIndirectString(indirect, buffer, 512, 0) == 0 ? new string(buffer) : null;
    }

    // ---------- npm 全局包和 scoop ----------

    private static IEnumerable<InstalledApp> ReadNpmGlobal()
    {
        var modules = Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), "npm", "node_modules");
        if (!Directory.Exists(modules))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(modules))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.'))
            {
                continue;
            }

            IEnumerable<string> packages = name.StartsWith('@') ? Directory.EnumerateDirectories(dir) : [dir];
            foreach (var package in packages)
            {
                var id = name.StartsWith('@') ? name + "/" + Path.GetFileName(package) : name;
                yield return new InstalledApp
                {
                    Name = id,
                    Version = ReadPackageVersion(package),
                    InstallLocation = package,
                    Source = AppSource.Npm,
                    Id = id,
                };
            }
        }
    }

    private static string? ReadPackageVersion(string packageDir)
    {
        var file = Path.Combine(packageDir, "package.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(file);
            using var doc = JsonDocument.Parse(stream);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<InstalledApp> ReadScoop()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("SCOOP"),
            Path.Combine(Folder(Environment.SpecialFolder.UserProfile), "scoop"),
            Environment.GetEnvironmentVariable("SCOOP_GLOBAL"),
            Path.Combine(Folder(Environment.SpecialFolder.CommonApplicationData), "scoop"),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var apps = string.IsNullOrEmpty(root) ? null : Path.Combine(root, "apps");
            if (apps is null || !seen.Add(apps) || !Directory.Exists(apps))
            {
                continue;
            }

            foreach (var dir in Directory.EnumerateDirectories(apps))
            {
                yield return new InstalledApp { Name = Path.GetFileName(dir), InstallLocation = dir, Source = AppSource.Scoop, Id = Path.GetFileName(dir) };
            }
        }
    }

    // ---------- 命令行工具 ----------

    /// <summary>PATH（进程、用户、系统三处）加上常见的工具目录；Windows 目录除外。</summary>
    public static List<string> CommandLineDirs()
    {
        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var local = Folder(Environment.SpecialFolder.LocalApplicationData);
        var dirs = new List<string>
        {
            Path.Combine(profile, ".local", "bin"),
            Path.Combine(profile, ".cargo", "bin"),
            Path.Combine(profile, "go", "bin"),
            Path.Combine(profile, ".bun", "bin"),
            Path.Combine(profile, ".deno", "bin"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"),
        };
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var path = Environment.GetEnvironmentVariable("PATH", target);
            if (path is null)
            {
                continue;
            }

            foreach (var part in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var d = Environment.ExpandEnvironmentVariables(part.Trim('"')).TrimEnd('\\');
                if (d.Length > 3 && Path.IsPathFullyQualified(d) && !IsUnder(d, WindowsDir))
                {
                    dirs.Add(d);
                }
            }
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<InstalledApp> ReadCommandLineTools()
    {
        var count = 0;
        foreach (var dir in CommandLineDirs())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            IEnumerable<string> exes;
            try
            {
                exes = Directory.EnumerateFiles(dir, "*.exe").Take(MaxExesPerDir).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var exe in exes)
            {
                if (++count > MaxCommandLineTools)
                {
                    yield break;
                }

                var id = ExeIdentityReader.Read(exe);
                yield return new InstalledApp
                {
                    Name = Path.GetFileNameWithoutExtension(exe),
                    RawName = id?.DisplayName,
                    Publisher = id?.Company,
                    Version = id?.Version,
                    InstallLocation = dir,
                    ExePath = exe,
                    Source = AppSource.CommandLine,
                    Id = Path.GetFileName(exe),
                };
            }
        }
    }

    // ---------- 运行中的进程 ----------

    private static IEnumerable<InstalledApp> ReadProcesses()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledApp>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var image = ImagePath(p.Id);
                if (image is null || IsUnder(image, WindowsDir) || !seen.Add(image))
                {
                    continue;
                }

                var id = ExeIdentityReader.Read(image);
                result.Add(new InstalledApp
                {
                    Name = id?.Product is { } product ? AppNames.CleanDisplayName(product) : Path.GetFileNameWithoutExtension(image),
                    RawName = id?.DisplayName,
                    Publisher = id?.Company,
                    Version = id?.Version,
                    InstallLocation = Path.GetDirectoryName(image),
                    ExePath = image,
                    Source = AppSource.Process,
                    Id = Path.GetFileName(image),
                });
            }
        }

        return result;
    }

    /// <summary>进程的映像路径。受保护的进程（以及没有权限的进程）返回 null。</summary>
    internal static unsafe string? ImagePath(int pid)
    {
        var handle = Win32.OpenProcess(Win32.ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var size = 1024;
            return Win32.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            Win32.CloseHandle(handle);
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.App.Agent;

/// <summary>
/// 常驻服务装在哪里（docs/phase2b-design.md 5.3、5.9 节）。正式安装用 <see cref="Default"/>；测试用临时目录和测试用的服务名、管道名、会话名。
/// 程序目录和数据目录的最后两级必须是 DiskThinner\Agent：删除前按这个核对，防止配置错了删到别处。
/// </summary>
internal sealed record AgentLayout
{
    /// <summary>发布目录里的服务程序（DiskThinner.exe 旁边的 Agent\）。</summary>
    public required string SourceDir { get; init; }

    /// <summary>%ProgramFiles%\DiskThinner\Agent，下面每个版本一个目录。</summary>
    public required string ProgramRoot { get; init; }

    /// <summary>%ProgramData%\DiskThinner\Agent。</summary>
    public required string DataDir { get; init; }

    public string ServiceName { get; init; } = AgentProtocol.ServiceName;
    public string PipeName { get; init; } = AgentProtocol.PipeName;
    public string SessionName { get; init; } = AgentProtocol.SessionName;

    /// <summary>服务的命令行参数（只有测试服务有，正式安装没有）。</summary>
    public IReadOnlyList<string> ServiceArgs { get; init; } = [];

    public static readonly string[] Files = ["DiskThinner.Agent.exe", "e_sqlite3.dll"];

    public static AgentLayout Default(string? sourceDir = null) => new()
    {
        SourceDir = sourceDir ?? Path.Combine(AppContext.BaseDirectory, "Agent"),
        ProgramRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DiskThinner", "Agent"),
        DataDir = DataDirectoryAcl.DefaultPath,
    };

    public bool SourceAvailable => Files.All(f => File.Exists(Path.Combine(SourceDir, f)));

    /// <summary>路径的最后两级是 DiskThinner\Agent。</summary>
    public static bool IsAgentRoot(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        return string.Equals(Path.GetFileName(full), "Agent", StringComparison.OrdinalIgnoreCase)
               && string.Equals(Path.GetFileName(Path.GetDirectoryName(full)), "DiskThinner", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>安装时的选择。FlushSeconds 只给验收用（第 5.8 节）。</summary>
internal sealed record AgentInstallOptions(string Mode = "precise", int KeepDays = 90, int? FlushSeconds = null);

/// <summary>一次安装、更新或卸载的结果。Message 给界面显示；LogTail 是失败时服务日志的最后几行（日志不含路径）。</summary>
internal sealed record AgentOpResult(bool Ok, string Message, bool AclReset = false, IReadOnlyList<string>? LogTail = null, string? Version = null);

/// <summary>
/// 常驻服务的安装、更新、卸载（docs/phase2b-design.md 5.3 节）。只在管理员下调用；每一步写 logs\agent-install.log，失败时按相反的顺序回滚已经做了的步骤。
/// SCM、数据目录的权限、管道应答、停残留会话都可以注入（单元测试用假的）；文件复制和哈希用真实的文件系统（测试用临时目录）。
/// 只碰三个地方：程序目录、数据目录、SCM 里这一个服务名。
/// </summary>
internal sealed class AgentInstaller(AgentLayout layout, IServiceManager scm)
{
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    /// <summary>准备数据目录（建目录、设权限）；返回 true 表示原来的权限不对、被重设了。</summary>
    public Func<string, bool> PrepareDataDir { get; init; } = DataDirectoryAcl.Apply;

    /// <summary>服务启动后等管道应答：返回服务的版本，等不到时返回 null。</summary>
    public Func<string?>? Hello { get; init; }

    /// <summary>按名字停掉 ETW 会话（卸载时停残留的）。</summary>
    public Func<string, bool> StopSession { get; init; } = DiskThinner.Monitor.Etw.NativeSession.StopByName;

    public Action<string> Log { get; init; } = m => AppLog.Append(Path.Combine(AppLog.Directory, "agent-install.log"), m, 256 * 1024);

    public AgentLayout Layout => layout;

    /// <summary>安装。已经有我们的服务时按更新处理；有同名但不是我们装的服务时报错，不碰它。</summary>
    public AgentOpResult Install(AgentInstallOptions options)
    {
        Log($"install start service={layout.ServiceName} mode={options.Mode} keep={options.KeepDays}");
        var info = scm.Query(layout.ServiceName);
        if (info.Exists)
        {
            if (!IsOurs(info.BinaryPath))
            {
                return Fail(L.F("已经有一个名为 {0} 的服务，不是 DiskThinner 装的，没有改动它。", layout.ServiceName));
            }

            Log("service exists, updating instead");
            return Update();
        }

        if (Source() is not { } files)
        {
            return Fail(L.T("发布目录里没有服务程序（Agent\\DiskThinner.Agent.exe），请重新下载完整的发布包。"));
        }

        var undo = new Stack<(string Name, Action Action)>();
        string dir;
        try
        {
            // 1–2：复制到 Program Files\DiskThinner\Agent\<版本>\ 并核对哈希
            dir = VersionDir(files);
            if (CopyVerified(files, dir))
            {
                undo.Push(("remove program dir", () => DeleteTree(dir, layout.ProgramRoot)));
            }

            // 3：数据目录（不存在时建；存在时检查并重设权限）
            var dataExisted = Directory.Exists(layout.DataDir);
            var aclReset = PrepareDataDir(layout.DataDir);
            if (!dataExisted)
            {
                undo.Push(("remove data dir", () => DeleteTree(layout.DataDir, layout.DataDir)));
            }

            if (aclReset)
            {
                Log("data directory permissions were reset");
            }

            WarnUnknownFiles();

            // 4：配置（服务启动之前唯一一次由界面写这个文件）
            WriteConfig(options);

            // 5：注册服务
            scm.Create(new ServiceDefinition(layout.ServiceName, L.T("DiskThinner 写入监控"),
                L.T("记录本机磁盘上哪个程序写了哪些目录，给 DiskThinner 的增长分析用。只在本机，不联网，不删除文件。"), BinaryPath(dir)));
            undo.Push(("delete service", () => scm.Delete(layout.ServiceName)));
            Log("service created");

            // 6：启动并等应答
            if (StartAndHello() is not { } version)
            {
                var tail = LogTail();
                undo.Push(("stop service", () => scm.Stop(layout.ServiceName, TimeSpan.FromSeconds(10))));
                Rollback(undo);
                return Fail(L.T("服务没有启动起来，已经撤销安装。"), tail);
            }

            Log($"install done version={version}");
            return new AgentOpResult(true, aclReset ? L.T("已安装并启动。数据目录的权限已重设（原来的权限允许普通用户访问）。") : L.T("已安装并启动。"), aclReset, null, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidDataException or InvalidOperationException)
        {
            Log("install failed: " + ex.GetType().Name + " " + ex.Message);
            Rollback(undo);
            return Fail(L.F("安装失败，已经撤销：{0}", ex.Message));
        }
    }

    /// <summary>更新：新版本复制到自己的目录 → 停服务 → 改程序路径 → 启动并等应答；失败时改回旧路径再启动。成功后删掉旧版本的目录。</summary>
    public AgentOpResult Update()
    {
        Log("update start");
        var info = scm.Query(layout.ServiceName);
        if (!info.Exists)
        {
            return Fail(L.T("常驻服务没有安装。"));
        }

        if (!IsOurs(info.BinaryPath) || ExePath(info.BinaryPath) is not { } oldExe)
        {
            return Fail(L.F("服务 {0} 不是 DiskThinner 装的，没有改动它。", layout.ServiceName));
        }

        if (Source() is not { } files)
        {
            return Fail(L.T("发布目录里没有服务程序（Agent\\DiskThinner.Agent.exe），请重新下载完整的发布包。"));
        }

        var oldDir = Path.GetDirectoryName(oldExe)!;
        var newDir = VersionDir(files);
        var oldPath = info.BinaryPath!;
        if (string.Equals(Path.GetFullPath(oldDir).TrimEnd('\\'), Path.GetFullPath(newDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && SameFiles(files, newDir))
        {
            // 已经是这个版本：没有在运行时启动它
            if (!info.Running && StartAndHello() is null)
            {
                return Fail(L.T("已经是最新版本，但服务没有启动起来。"), LogTail());
            }

            return new AgentOpResult(true, L.T("已经是最新版本。"), Version: Version(files));
        }

        bool created;
        try
        {
            created = CopyVerified(files, newDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Log("update copy failed: " + ex.GetType().Name);
            return Fail(L.F("复制新版本失败，服务没有改动：{0}", ex.Message));
        }

        try
        {
            if (!scm.Stop(layout.ServiceName, StopWait))
            {
                if (created)
                {
                    DeleteTree(newDir, layout.ProgramRoot);
                }

                return Fail(L.T("服务 30 秒内没有停下来，没有更新。可以重启电脑后再试。"));
            }

            scm.SetBinaryPath(layout.ServiceName, BinaryPath(newDir));
            Log("binary path switched");
            if (StartAndHello() is { } version)
            {
                foreach (var dir in Directory.GetDirectories(layout.ProgramRoot))
                {
                    if (!string.Equals(Path.GetFullPath(dir).TrimEnd('\\'), Path.GetFullPath(newDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        DeleteTree(dir, layout.ProgramRoot);
                    }
                }

                Log($"update done version={version}");
                return new AgentOpResult(true, L.F("已更新到 {0}。", version), Version: version);
            }

            // 新版本起不来：改回旧版本
            var tail = LogTail();
            Log("new version did not start, restoring");
            scm.Stop(layout.ServiceName, TimeSpan.FromSeconds(10));
            scm.SetBinaryPath(layout.ServiceName, oldPath);
            scm.Start(layout.ServiceName);
            var restored = scm.WaitRunning(layout.ServiceName, StartWait);
            if (created)
            {
                DeleteTree(newDir, layout.ProgramRoot);
            }

            return Fail(restored ? L.T("更新失败，已恢复旧版本。") : L.T("更新失败，旧版本也没有启动起来。请在设置里重新安装。"), tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log("update failed: " + ex.GetType().Name + " " + ex.Message);
            var restored = false;
            try
            {
                scm.SetBinaryPath(layout.ServiceName, oldPath);
                scm.Start(layout.ServiceName);
                restored = scm.WaitRunning(layout.ServiceName, StartWait);
                if (created)
                {
                    DeleteTree(newDir, layout.ProgramRoot);
                }
            }
            catch (Exception restoreError) when (restoreError is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                Log("restore failed: " + restoreError.GetType().Name);
            }

            return Fail(restored ? L.F("更新失败，已恢复旧版本：{0}", ex.Message) : L.F("更新失败：{0}", ex.Message));
        }
    }

    /// <summary>卸载：停服务（30 秒，超时报错不强行结束）→ 删服务 → 停残留会话 → 删程序目录；勾选了时再删数据目录。</summary>
    public AgentOpResult Uninstall(bool deleteData)
    {
        Log($"uninstall start delete_data={deleteData}");
        try
        {
            var info = scm.Query(layout.ServiceName);
            if (info.Exists)
            {
                if (!IsOurs(info.BinaryPath))
                {
                    return Fail(L.F("服务 {0} 不是 DiskThinner 装的，没有改动它。", layout.ServiceName));
                }

                if (!scm.Stop(layout.ServiceName, StopWait))
                {
                    return Fail(L.T("服务 30 秒内没有停下来，没有卸载。可以重启电脑后再卸载。"));
                }

                scm.Delete(layout.ServiceName);
                Log("service deleted");
            }

            if (StopSession(layout.SessionName))
            {
                Log("stopped a residual session");
            }

            DeleteTree(layout.ProgramRoot, layout.ProgramRoot);
            var parent = Path.GetDirectoryName(Path.GetFullPath(layout.ProgramRoot).TrimEnd('\\'))!;
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }

            if (deleteData)
            {
                DeleteTree(layout.DataDir, layout.DataDir);
                Log("data deleted");
            }

            Log("uninstall done");
            return new AgentOpResult(true, deleteData ? L.T("已卸载，监控数据已删除。") : L.T("已卸载，监控数据保留着，以后重新安装可以接着用。"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log("uninstall failed: " + ex.GetType().Name + " " + ex.Message);
            return Fail(L.F("卸载没有完成：{0}", ex.Message));
        }
    }

    // ---------------- 步骤 ----------------

    private AgentOpResult Fail(string message, IReadOnlyList<string>? tail = null)
    {
        Log("failed: " + message);
        return new AgentOpResult(false, message, LogTail: tail);
    }

    private void Rollback(Stack<(string Name, Action Action)> undo)
    {
        while (undo.TryPop(out var step))
        {
            try
            {
                step.Action();
                Log("rollback: " + step.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log("rollback failed: " + step.Name + " " + ex.GetType().Name);
            }
        }
    }

    /// <summary>发布目录里的两个文件和它们的 SHA-256；缺了任何一个返回 null。</summary>
    private (string Name, string Path, string Hash)[]? Source()
    {
        if (!layout.SourceAvailable)
        {
            return null;
        }

        return [.. AgentLayout.Files.Select(f => (f, Path.Combine(layout.SourceDir, f), Hash(Path.Combine(layout.SourceDir, f))))];
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Version((string Name, string Path, string Hash)[] files)
    {
        var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(files[0].Path).ProductVersion ?? "0";
        return v.Split('+')[0];
    }

    /// <summary>版本目录：&lt;版本&gt;-&lt;exe 哈希的前 8 位&gt;（开发版本号相同、程序不同时也不会混）。</summary>
    private string VersionDir((string Name, string Path, string Hash)[] files)
    {
        var version = new string([.. Version(files).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')]);
        return Path.Combine(layout.ProgramRoot, (version.Length == 0 ? "0" : version) + "-" + files[0].Hash[..8].ToLowerInvariant());
    }

    private static bool SameFiles((string Name, string Path, string Hash)[] files, string dir) =>
        files.All(f => File.Exists(Path.Combine(dir, f.Name)) && Hash(Path.Combine(dir, f.Name)) == f.Hash);

    /// <summary>复制并核对哈希。目录已经有一样的文件时不复制、返回 false；新建了目录返回 true。哈希不一致时删掉复制的文件并抛出。</summary>
    private bool CopyVerified((string Name, string Path, string Hash)[] files, string dir)
    {
        if (SameFiles(files, dir))
        {
            return false;
        }

        foreach (var root in new[] { layout.ProgramRoot, dir })
        {
            if (Directory.Exists(root) && (new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("program directory is a reparse point");
            }
        }

        var created = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in files)
            {
                File.Copy(f.Path, Path.Combine(dir, f.Name), overwrite: true);
            }

            if (!SameFiles(files, dir))
            {
                throw new InvalidDataException(L.T("复制后的服务程序和发布包里的不一致"));
            }
        }
        catch
        {
            if (created)
            {
                DeleteTree(dir, layout.ProgramRoot);
            }

            throw;
        }

        Log($"copied to {Path.GetFileName(dir)}");
        return created;
    }

    private string BinaryPath(string dir) =>
        "\"" + Path.Combine(dir, AgentLayout.Files[0]) + "\"" + string.Concat(layout.ServiceArgs.Select(a => " " + (a.Contains(' ') ? "\"" + a + "\"" : a)));

    /// <summary>服务的程序路径（引号里的那一段）。</summary>
    internal static string? ExePath(string? binaryPath)
    {
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var b = binaryPath.Trim();
        if (b.StartsWith('"'))
        {
            var close = b.IndexOf('"', 1);
            return close > 1 ? b[1..close] : null;
        }

        var space = b.IndexOf(' ');
        return space > 0 ? b[..space] : b;
    }

    /// <summary>服务的程序在我们的程序目录下（是我们装的）。</summary>
    internal bool IsOurs(string? binaryPath)
    {
        if (ExePath(binaryPath) is not { } exe)
        {
            return false;
        }

        try
        {
            var root = Path.GetFullPath(layout.ProgramRoot).TrimEnd('\\') + "\\";
            return Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void WriteConfig(AgentInstallOptions o)
    {
        var path = Path.Combine(layout.DataDir, "agent.json");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("mode", o.Mode == "light" ? "light" : "precise");
            w.WriteNumber("keepDays", AgentProtocol.KeepDaysChoices.Contains(o.KeepDays) ? o.KeepDays : 90);
            w.WriteNumber("maxBytes", 256L << 20);
            w.WriteString("version", typeof(AgentInstaller).Assembly.GetName().Version?.ToString() ?? "0");
            if (o.FlushSeconds is { } s)
            {
                w.WriteNumber("flushSeconds", Math.Clamp(s, 5, 300));
            }

            w.WriteEndObject();
        }

        File.WriteAllBytes(path + ".tmp", ms.ToArray());
        File.Move(path + ".tmp", path, overwrite: true);
        Log("config written");
    }

    /// <summary>数据目录里有不认识的文件时只记日志，不删除。</summary>
    private void WarnUnknownFiles()
    {
        var unknown = Directory.EnumerateFileSystemEntries(layout.DataDir)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.StartsWith("monitor.db", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("agent.json", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(n, "logs", StringComparison.OrdinalIgnoreCase))
            .Count();
        if (unknown > 0)
        {
            Log($"data directory has {unknown} unknown entries (left in place)");
        }
    }

    /// <summary>启动服务，等到 RUNNING（30 秒）再等管道应答。返回服务的版本；起不来返回 null。</summary>
    private string? StartAndHello()
    {
        scm.Start(layout.ServiceName);
        if (!scm.WaitRunning(layout.ServiceName, StartWait))
        {
            var info = scm.Query(layout.ServiceName);
            Log($"service did not reach running: state={info.State} exit={info.Win32ExitCode}/{info.ServiceExitCode}");
            return null;
        }

        var version = (Hello ?? DefaultHello)();
        Log(version is null ? "no pipe answer" : "pipe answered");
        return version;
    }

    private string? DefaultHello()
    {
        var client = new AgentClient(layout.PipeName, layout.ServiceName, scm);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (client.Hello(AppVersion.Current) is { } hello)
            {
                return hello.Protocol == AgentProtocol.Version ? hello.AgentVersion : null;
            }

            Thread.Sleep(300);
        }

        Log("hello failed: " + client.LastError);
        return null;
    }

    private IReadOnlyList<string> LogTail()
    {
        try
        {
            var path = Path.Combine(layout.DataDir, "logs", "agent.log");
            return File.Exists(path) ? [.. File.ReadAllLines(path).TakeLast(8)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>删除一个目录（不跟随链接：.NET 递归删除时遇到链接只删链接本身）。只删 <paramref name="root"/> 本身或它下面的、并且根是 DiskThinner\Agent 的目录。</summary>
    private void DeleteTree(string dir, string root)
    {
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        var r = Path.GetFullPath(root).TrimEnd('\\');
        if (!AgentLayout.IsAgentRoot(r) || !(string.Equals(full, r, StringComparison.OrdinalIgnoreCase) || full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("refusing to delete outside the agent directories");
        }

        if (!Directory.Exists(full))
        {
            return;
        }

        var info = new DirectoryInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info.Delete(); // 链接本身
            return;
        }

        Directory.Delete(full, recursive: true);
    }
}

/// <summary>程序的版本（不含提交号），和服务的 hello 里的版本比较用。</summary>
internal static class AppVersion
{
    public static string Current { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault()
            as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0";
}

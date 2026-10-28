using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskThinner.Core;

namespace DiskThinner.Monitor;

/// <summary>
/// 写入监控用到的 Windows API（docs/phase2a-design.md 5.5、5.7 节）：设备路径映射、服务宿主的服务名、开始时的进程列举、兜底任务。
/// 纯逻辑通过接口用它们，测试用假的实现。
/// </summary>
public static partial class WindowsSystem
{
    /// <summary>本地固定盘的"\Device\HarddiskVolumeN → C:"映射（QueryDosDevice）。</summary>
    public static PrefixDeviceMap DeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed)
            {
                continue;
            }

            var letter = d.Name[..2];
            var n = QueryDosDeviceW(letter, buffer, buffer.Length);
            if (n > 0)
            {
                map[new string(buffer, 0, Array.IndexOf(buffer, '\0'))] = letter;
            }
        }

        return new PrefixDeviceMap(map);
    }

    /// <summary>开始监控时的进程列举：Toolhelp32 拿进程号、父进程号和 exe 名，能打开的进程再取完整路径和创建时间。</summary>
    public static unsafe List<(int Pid, int ParentPid, long CreateTicks, string Image)> Processes()
    {
        var list = new List<(int, int, long, string)>();
        var snap = CreateToolhelp32Snapshot(0x2 /* TH32CS_SNAPPROCESS */, 0);
        if (snap == -1)
        {
            return list;
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)sizeof(ProcessEntry) };
            var buf = stackalloc char[1024];
            for (var ok = Process32FirstW(snap, ref entry); ok; ok = Process32NextW(snap, ref entry))
            {
                var exe = new string(entry.ExeFileBuffer);
                var pid = (int)entry.ProcessId;
                var image = exe;
                long create = 0;
                var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
                if (h != 0)
                {
                    try
                    {
                        var len = 1024;
                        if (QueryFullProcessImageNameW(h, 0, buf, ref len))
                        {
                            image = new string(buf, 0, len);
                        }

                        if (GetProcessTimes(h, out var c, out _, out _, out _))
                        {
                            create = DateTime.FromFileTimeUtc(c).Ticks;
                        }
                    }
                    finally
                    {
                        CloseHandle(h);
                    }
                }

                list.Add((pid, (int)entry.ParentProcessId, create, image));
                entry.Size = (uint)sizeof(ProcessEntry);
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    /// <summary>服务宿主的服务（EnumServicesStatusEx）。查一次缓存起来，<see cref="Refresh"/> 再查。</summary>
    public sealed class Services : IServiceNames
    {
        private Dictionary<int, List<(string, string)>> _byPid = [];

        public Services() => Refresh();

        public IReadOnlyList<(string Name, string Display)> For(int pid) => _byPid.TryGetValue(pid, out var l) ? l : [];

        public unsafe void Refresh()
        {
            var map = new Dictionary<int, List<(string, string)>>();
            var scm = OpenSCManagerW(null, null, 0x0004 /* SC_MANAGER_ENUMERATE_SERVICE */);
            if (scm == 0)
            {
                return;
            }

            try
            {
                uint resume = 0;
                EnumServicesStatusExW(scm, 0, 0x30 /* SERVICE_WIN32 */, 0x1 /* SERVICE_ACTIVE */, null, 0, out var needed, out _, ref resume, null);
                if (needed == 0)
                {
                    return;
                }

                var buffer = new byte[needed + 1024];
                resume = 0;
                fixed (byte* p = buffer)
                {
                    if (!EnumServicesStatusExW(scm, 0, 0x30, 0x1, p, (uint)buffer.Length, out _, out var count, ref resume, null))
                    {
                        return;
                    }

                    // ENUM_SERVICE_STATUS_PROCESSW（x64）：两个字符串指针 + SERVICE_STATUS_PROCESS（9 个 DWORD），按 8 字节对齐共 56 字节
                    var size = 2 * sizeof(nint) + 36;
                    size = (size + 7) & ~7;
                    for (var i = 0; i < count; i++)
                    {
                        var item = p + i * size;
                        var name = Marshal.PtrToStringUni(*(nint*)item) ?? "";
                        var display = Marshal.PtrToStringUni(*(nint*)(item + sizeof(nint))) ?? name;
                        var pid = *(int*)(item + 2 * sizeof(nint) + 28);
                        if (pid == 0)
                        {
                            continue;
                        }

                        if (!map.TryGetValue(pid, out var l))
                        {
                            map[pid] = l = [];
                        }

                        l.Add((name, display));
                    }
                }
            }
            finally
            {
                CloseServiceHandle(scm);
            }

            _byPid = map;
        }
    }

    // ---------------- 兜底计划任务（第 5.1.4 节，D7） ----------------

    private static string Schtasks => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static string LogmanPath => Path.Combine(Environment.SystemDirectory, "logman.exe");

    /// <summary>注册兜底任务；带过期删除的失败时去掉 EndBoundary / DeleteExpiredTaskAfter 再试一次（V20）。</summary>
    public static (bool Ok, string Message) RegisterWatchdog(string sessionName, DateTimeOffset triggerAt)
    {
        var description = L.T("DiskThinner 被强制结束时停止它开的写入监控会话。监控结束后自动删除。");
        var xml = WatchdogTask.BuildXml(sessionName, triggerAt, TimeSpan.FromDays(1), LogmanPath, description);
        var (ok, message) = CreateFromXml(xml);
        if (!ok)
        {
            var text = System.Text.Encoding.Unicode.GetString(xml, 2, xml.Length - 2);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*<EndBoundary>[^<]*</EndBoundary>|\s*<DeleteExpiredTaskAfter>[^<]*</DeleteExpiredTaskAfter>", "");
            (ok, message) = CreateFromXml([0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes(text)]);
        }

        return (ok, message);
    }

    public static (bool Ok, string Message) DeleteWatchdog() => Schtask(["/Delete", "/TN", WatchdogTask.TaskName, "/F"]);

    /// <summary>兜底任务是否还在（看 System32\Tasks 下的任务文件，不启动 schtasks；只有管理员读得到，普通权限下总是 false）。</summary>
    public static bool WatchdogExists()
    {
        try
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", WatchdogTask.TaskName.TrimStart('\\')));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static (bool, string) CreateFromXml(byte[] xml)
    {
        var path = Path.Combine(Path.GetTempPath(), "DiskThinner-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllBytes(path, xml);
            return Schtask(["/Create", "/TN", WatchdogTask.TaskName, "/XML", path, "/F"]);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (bool, string) Schtask(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(Schtasks) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                p.Kill();
                return (false, "schtasks timeout");
            }

            return (p.ExitCode == 0, (error.Result + output.Result).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriClassBase;
        public uint Flags;
        public fixed char ExeFileBuffer[260];
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDeviceW(string device, [Out] char[] target, int max);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(nint process, uint flags, char* name, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(nint process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumServicesStatusExW(nint scm, int infoLevel, uint serviceType, uint serviceState, byte* services, uint size,
        out uint needed, out uint returned, ref uint resume, string? group);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskThinner.Monitor.Agent;

/// <summary>SCM 里一个服务的状态。State：missing / stopped / start-pending / stop-pending / running / paused / other。</summary>
public sealed record ServiceInfo(string State, int ProcessId, string? BinaryPath, uint Win32ExitCode, uint ServiceExitCode)
{
    public static readonly ServiceInfo Missing = new("missing", 0, null, 0, 0);

    public bool Exists => State != "missing";

    public bool Running => State == "running";
}

/// <summary>服务的注册信息（安装时用）。</summary>
public sealed record ServiceDefinition(string Name, string DisplayName, string Description, string BinaryPath);

/// <summary>SCM 的操作（安装器和界面用；测试用假的实现）。查询普通权限也可以，其余需要管理员。失败时抛 <see cref="Win32Exception"/>。</summary>
public interface IServiceManager
{
    ServiceInfo Query(string name);

    /// <summary>
    /// 注册：LocalSystem、自动（延迟）启动、独立进程；描述；失败动作"1 分钟后重启、5 分钟后重启、之后不再重启，24 小时后计数清零"，
    /// 非崩溃的错误退出也算失败（W6）；服务 SID 不受限；PRESHUTDOWN 等 15 秒（docs/phase2b-design.md 5.3 节）。
    /// </summary>
    void Create(ServiceDefinition definition);

    void SetBinaryPath(string name, string binaryPath);

    void Start(string name);

    /// <summary>发 STOP 并等到 STOPPED；超时返回 false（不强行结束）。服务已经停了时直接返回 true。</summary>
    bool Stop(string name, TimeSpan wait);

    /// <summary>等到 RUNNING；进程以错误退出（STOPPED）或超时返回 false。</summary>
    bool WaitRunning(string name, TimeSpan wait);

    void Delete(string name);
}

/// <summary>直接调用 advapi32 的 SCM API。</summary>
public sealed unsafe partial class ServiceManager : IServiceManager
{
    private const uint ScManagerConnect = 0x1;
    private const uint ScManagerCreateService = 0x2;
    private const uint ServiceQueryConfig = 0x1;
    private const uint ServiceChangeConfig = 0x2;
    private const uint ServiceQueryStatus = 0x4;
    private const uint ServiceStart = 0x10;
    private const uint ServiceStop = 0x20;
    private const uint Delete_ = 0x10000;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceErrorNormal = 1;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ErrorServiceNotActive = 1062;
    private const uint ErrorServiceAlreadyRunning = 1056;
    private const uint ErrorInsufficientBuffer = 122;

    public static readonly ServiceManager Instance = new();

    public ServiceInfo Query(string name)
    {
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == 0)
        {
            return ServiceInfo.Missing with { State = "unknown" };
        }

        try
        {
            var svc = OpenServiceW(scm, name, ServiceQueryStatus | ServiceQueryConfig);
            if (svc == 0)
            {
                return Marshal.GetLastPInvokeError() == ErrorServiceDoesNotExist ? ServiceInfo.Missing : ServiceInfo.Missing with { State = "unknown" };
            }

            try
            {
                ServiceStatusProcess status;
                uint needed;
                if (!QueryServiceStatusEx(svc, 0, &status, (uint)sizeof(ServiceStatusProcess), &needed))
                {
                    return ServiceInfo.Missing with { State = "unknown" };
                }

                return new ServiceInfo(StateName(status.CurrentState), (int)status.ProcessId, BinaryPath(svc), status.Win32ExitCode, status.ServiceSpecificExitCode);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static string StateName(uint state) => state switch
    {
        1 => "stopped",
        2 => "start-pending",
        3 => "stop-pending",
        4 => "running",
        7 => "paused",
        _ => "other",
    };

    private static string? BinaryPath(nint svc)
    {
        uint needed;
        QueryServiceConfigW(svc, null, 0, &needed);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || needed == 0 || needed > 64 * 1024)
        {
            return null;
        }

        var buffer = (byte*)NativeMemory.Alloc(needed);
        try
        {
            if (!QueryServiceConfigW(svc, (QueryServiceConfig*)buffer, needed, &needed))
            {
                return null;
            }

            var config = (QueryServiceConfig*)buffer;
            return config->BinaryPathName is null ? null : new string(config->BinaryPathName);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public void Create(ServiceDefinition d)
    {
        var scm = OpenScm(ScManagerConnect | ScManagerCreateService);
        try
        {
            var svc = CreateServiceW(scm, d.Name, d.DisplayName, 0xF01FF /* SERVICE_ALL_ACCESS */, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                d.BinaryPath, null, null, null, null /* LocalSystem */, null);
            if (svc == 0)
            {
                throw new Win32Exception();
            }

            try
            {
                fixed (char* text = d.Description)
                {
                    var description = new ServiceDescription { Description = text };
                    Config2(svc, 1 /* SERVICE_CONFIG_DESCRIPTION */, &description);
                }

                var delayed = new ServiceDelayedAutoStart { Delayed = 1 };
                Config2(svc, 3 /* SERVICE_CONFIG_DELAYED_AUTO_START_INFO */, &delayed);

                var actions = stackalloc ScAction[3];
                actions[0] = new ScAction { Type = 1 /* SC_ACTION_RESTART */, Delay = 60_000 };
                actions[1] = new ScAction { Type = 1, Delay = 300_000 };
                actions[2] = new ScAction { Type = 0 /* SC_ACTION_NONE */, Delay = 0 };
                var failure = new ServiceFailureActions { ResetPeriod = 86_400, ActionCount = 3, Actions = actions };
                Config2(svc, 2 /* SERVICE_CONFIG_FAILURE_ACTIONS */, &failure);

                var flag = new ServiceFailureActionsFlag { OnNonCrashFailures = 1 };
                Config2(svc, 4 /* SERVICE_CONFIG_FAILURE_ACTIONS_FLAG */, &flag);

                var sid = new ServiceSidInfo { SidType = 1 /* SERVICE_SID_TYPE_UNRESTRICTED */ };
                Config2(svc, 5 /* SERVICE_CONFIG_SERVICE_SID_INFO */, &sid);

                var preshutdown = new ServicePreshutdownInfo { Timeout = 15_000 };
                Config2(svc, 7 /* SERVICE_CONFIG_PRESHUTDOWN_INFO */, &preshutdown);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static void Config2(nint svc, uint level, void* info)
    {
        if (!ChangeServiceConfig2W(svc, level, info))
        {
            throw new Win32Exception();
        }
    }

    public void SetBinaryPath(string name, string binaryPath) => WithService(name, ServiceChangeConfig, svc =>
    {
        if (!ChangeServiceConfigW(svc, ServiceNoChange, ServiceNoChange, ServiceNoChange, binaryPath, null, null, null, null, null, null))
        {
            throw new Win32Exception();
        }
    });

    public void Start(string name) => WithService(name, ServiceStart, svc =>
    {
        if (!StartServiceW(svc, 0, null) && Marshal.GetLastPInvokeError() != ErrorServiceAlreadyRunning)
        {
            throw new Win32Exception();
        }
    });

    public bool Stop(string name, TimeSpan wait)
    {
        var info = Query(name);
        if (!info.Exists || info.State == "stopped")
        {
            return true;
        }

        WithService(name, ServiceStop, svc =>
        {
            ServiceStatus status;
            if (!ControlService(svc, 1 /* SERVICE_CONTROL_STOP */, &status) && Marshal.GetLastPInvokeError() != ErrorServiceNotActive)
            {
                throw new Win32Exception();
            }
        });
        return WaitFor(name, wait, s => s.State is "stopped" or "missing");
    }

    public bool WaitRunning(string name, TimeSpan wait) => WaitFor(name, wait, s => s.State is "running" or "stopped" or "missing") && Query(name).Running;

    private bool WaitFor(string name, TimeSpan wait, Func<ServiceInfo, bool> done)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (done(Query(name)))
            {
                return true;
            }

            if (sw.Elapsed > wait)
            {
                return false;
            }

            Thread.Sleep(200);
        }
    }

    public void Delete(string name) => WithService(name, Delete_, svc =>
    {
        if (!DeleteService(svc))
        {
            throw new Win32Exception();
        }
    });

    private static nint OpenScm(uint access)
    {
        var scm = OpenSCManagerW(null, null, access);
        return scm != 0 ? scm : throw new Win32Exception();
    }

    private static void WithService(string name, uint access, Action<nint> body)
    {
        var scm = OpenScm(ScManagerConnect);
        try
        {
            var svc = OpenServiceW(scm, name, access);
            if (svc == 0)
            {
                throw new Win32Exception();
            }

            try
            {
                body(svc);
            }
            finally
            {
                CloseServiceHandle(svc);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public char* BinaryPathName;
        public char* LoadOrderGroup;
        public uint TagId;
        public char* Dependencies;
        public char* ServiceStartName;
        public char* DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public char* Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStart
    {
        public int Delayed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public char* RebootMessage;
        public char* Command;
        public uint ActionCount;
        public ScAction* Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActionsFlag
    {
        public int OnNonCrashFailures;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceSidInfo
    {
        public uint SidType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServicePreshutdownInfo
    {
        public uint Timeout;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenServiceW(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateServiceW(nint scm, string name, string display, uint access, uint type, uint start, uint error, string binaryPath,
        string? group, uint* tagId, string? dependencies, string? account, string? password);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(nint svc, uint type, uint start, uint error, string? binaryPath, string? group, uint* tagId,
        string? dependencies, string? account, string? password, string? display);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(nint svc, uint level, void* info);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatusEx(nint svc, int level, ServiceStatusProcess* buffer, uint size, uint* needed);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfigW(nint svc, QueryServiceConfig* config, uint size, uint* needed);

    [LibraryImport("advapi32.dll", EntryPoint = "StartServiceW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(nint svc, uint argc, char** argv);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint svc, uint control, ServiceStatus* status);

    [LibraryImport("advapi32.dll", EntryPoint = "DeleteService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteService(nint svc);

    [LibraryImport("advapi32.dll", EntryPoint = "CloseServiceHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}

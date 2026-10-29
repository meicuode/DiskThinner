using System.Runtime.InteropServices;

namespace DiskThinner.Agent;

/// <summary>
/// 直接调用 SCM 的服务入口（E1，docs/phase2b-design.md 5.2 节，W4）：StartServiceCtrlDispatcherW 在主线程上阻塞，
/// SCM 在它自己的线程上调用 <see cref="ServiceMain"/>；控制请求（STOP、PRESHUTDOWN、POWEREVENT……）由 <see cref="Handler"/> 处理。
/// 两个回调都是 [UnmanagedCallersOnly]，不依赖 Microsoft.Extensions.Hosting，可以 Native AOT。
/// </summary>
internal static unsafe partial class ServiceControl
{
    public const uint ErrorFailedServiceControllerConnect = 1063;
    public const uint ErrorServiceSpecificError = 1066;

    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint StateStopped = 1;
    private const uint StateStartPending = 2;
    private const uint StateStopPending = 3;
    private const uint StateRunning = 4;
    private const uint AcceptStop = 0x1;
    private const uint AcceptPowerEvent = 0x40;
    private const uint AcceptPreshutdown = 0x100;
    private const uint ControlStop = 1;
    private const uint ControlInterrogate = 4;
    private const uint ControlShutdown = 5;
    private const uint ControlPowerEvent = 0xD;
    private const uint ControlPreshutdown = 0xF;
    private const uint ErrorCallNotImplemented = 120;

    private static Func<string, IServiceBody>? _factory;
    private static IServiceBody? _body;
    private static nint _handle;
    private static uint _checkPoint;
    private static readonly Lock Gate = new();

    /// <summary>服务的启动和停止（AgentHost 实现）。</summary>
    internal interface IServiceBody
    {
        /// <summary>启动（START_PENDING 期间，在 ServiceMain 的线程上）。失败时返回服务专用的错误码（非 0），服务以 STOPPED 带这个码结束，失败动作生效。</summary>
        int Start(Action<uint> progress);

        /// <summary>阻塞到要求停止（STOP、PRESHUTDOWN 或服务自己决定退出）。</summary>
        void WaitForStop();

        /// <summary>停止（STOP_PENDING 期间）。返回退出码（0 正常）。</summary>
        int Stop(Action<uint> progress);

        void RequestStop(string reason);

        void OnPowerEvent(uint eventType);
    }

    /// <summary>以服务身份运行：阻塞到服务停止。不是由 SCM 启动时返回 false（错误码 1063）。</summary>
    public static bool Run(string serviceName, Func<string, IServiceBody> factory, out int error)
    {
        _factory = factory;
        var name = Marshal.StringToHGlobalUni(serviceName);
        try
        {
            var table = stackalloc ServiceTableEntry[2];
            table[0] = new ServiceTableEntry { Name = (char*)name, Proc = &ServiceMain };
            table[1] = default;
            if (StartServiceCtrlDispatcherW(table))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    [UnmanagedCallersOnly]
    private static void ServiceMain(uint argc, char** argv)
    {
        var name = argc > 0 && argv[0] is not null ? new string(argv[0]) : "";
        try
        {
            _handle = RegisterServiceCtrlHandlerExW(argv[0], &Handler, null);
            if (_handle == 0)
            {
                return;
            }

            _body = _factory!(name);
            Report(StateStartPending, 0, 0, 10_000);
            var code = _body.Start(hint => Report(StateStartPending, 0, 0, hint));
            if (code != 0)
            {
                Report(StateStopped, ErrorServiceSpecificError, (uint)code, 0);
                return;
            }

            Report(StateRunning, 0, 0, 0);
            _body.WaitForStop();
            Report(StateStopPending, 0, 0, 15_000);
            var exit = _body.Stop(hint => Report(StateStopPending, 0, 0, hint));
            Report(StateStopped, exit == 0 ? 0 : ErrorServiceSpecificError, (uint)exit, 0);
        }
        catch (Exception)
        {
            // 异常不能穿过非托管回调：报 STOPPED 带错误码，失败动作生效
            Report(StateStopped, ErrorServiceSpecificError, 99, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static uint Handler(uint control, uint eventType, void* eventData, void* context)
    {
        try
        {
            switch (control)
            {
                case ControlStop:
                case ControlPreshutdown:
                case ControlShutdown:
                    Report(StateStopPending, 0, 0, 15_000);
                    _body?.RequestStop(control == ControlStop ? "service-stop" : "shutdown");
                    return 0;
                case ControlInterrogate:
                    return 0;
                case ControlPowerEvent:
                    _body?.OnPowerEvent(eventType);
                    return 0;
                default:
                    return ErrorCallNotImplemented;
            }
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void Report(uint state, uint win32Exit, uint specificExit, uint waitHintMs)
    {
        lock (Gate)
        {
            if (_handle == 0)
            {
                return;
            }

            var pending = state is StateStartPending or StateStopPending;
            var status = new ServiceStatus
            {
                ServiceType = ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = state == StateRunning ? AcceptStop | AcceptPreshutdown | AcceptPowerEvent : 0,
                Win32ExitCode = win32Exit,
                ServiceSpecificExitCode = specificExit,
                CheckPoint = pending ? ++_checkPoint : 0,
                WaitHint = waitHintMs,
            };
            SetServiceStatus(_handle, &status);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceTableEntry
    {
        public char* Name;
        public delegate* unmanaged<uint, char**, void> Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceCtrlDispatcherW(ServiceTableEntry* table);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial nint RegisterServiceCtrlHandlerExW(char* name, delegate* unmanaged<uint, uint, void*, void*, uint> handler, void* context);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetServiceStatus(nint handle, ServiceStatus* status);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPriorityClass(nint process, uint priorityClass);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    public const uint LoadLibrarySearchApplicationDir = 0x200;
    public const uint LoadLibrarySearchSystem32 = 0x800;
    public const uint BelowNormalPriorityClass = 0x4000;
}

// DiskThinner 常驻监控服务（阶段 2b，docs/phase2b-design.md 5.2 节）。参数见 AgentSettings。
// 由 SCM 启动时进入服务主函数；不是由 SCM 启动、又没有 --console 时，打印用法并以 2 退出。
using DiskThinner.Agent;

// 只从 System32 和程序目录加载 DLL（第 5.9 节：不加载用户目录里的任何 DLL）
ServiceControl.SetDefaultDllDirectories(ServiceControl.LoadLibrarySearchSystem32 | ServiceControl.LoadLibrarySearchApplicationDir);
ServiceControl.SetPriorityClass(ServiceControl.GetCurrentProcess(), ServiceControl.BelowNormalPriorityClass);

var settings = AgentSettings.Parse(args, out var argError);
if (settings is null)
{
    Console.Error.WriteLine("DiskThinner.Agent: " + argError);
    return AgentHost.ExitCodes.Arguments;
}

if (settings.Console)
{
    var host = new AgentHost(settings);
    var code = host.Start(_ => { });
    if (code != 0)
    {
        Console.Error.WriteLine("start failed: " + code);
        return code;
    }

    Console.WriteLine("running pid=" + Environment.ProcessId);
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        host.RequestStop("console");
    };
    if (settings.Seconds > 0)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(settings.Seconds)).ContinueWith(_ => host.RequestStop("timeout"), TaskScheduler.Default);
    }

    host.WaitForStop();
    var exit = host.Stop(_ => { });
    Console.WriteLine("stopped");
    return exit;
}

if (!ServiceControl.Run(settings.ServiceName, _ => new AgentHost(settings), out var error))
{
    if (error == ServiceControl.ErrorFailedServiceControllerConnect)
    {
        Console.Error.WriteLine(DiskThinner.Core.L.T("DiskThinner.Agent 是 Windows 服务，由 DiskThinner 的设置安装和启动。开发时用 --console [--seconds N]。"));
        return AgentHost.ExitCodes.Arguments;
    }

    return error;
}

return 0;

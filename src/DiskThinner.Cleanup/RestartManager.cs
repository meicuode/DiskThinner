using System.Runtime.InteropServices;
using DiskThinner.Cleanup.Native;

namespace DiskThinner.Cleanup;

/// <summary>占用文件的一个程序。</summary>
public sealed record FileHolder(int ProcessId, string Name, bool IsService);

/// <summary>
/// Restart Manager：查出哪些程序占用了文件；用户同意后请它们正常关闭（不强制结束，程序可以提示保存或拒绝）。
/// </summary>
public static class RestartManager
{
    /// <summary>一次最多登记这么多个文件（足够找出占用的程序，Restart Manager 处理大量文件很慢）。</summary>
    public const int MaxFiles = 200;

    /// <summary>占用这些文件的程序。查询失败时返回空列表。</summary>
    public static unsafe List<FileHolder> WhoLocks(IReadOnlyCollection<string> paths)
    {
        var result = new List<FileHolder>();
        if (paths.Count == 0)
        {
            return result;
        }

        using var session = Session.Start(paths);
        if (session is null)
        {
            return result;
        }

        uint count = 0;
        var rc = Win32.RmGetList(session.Handle, out var needed, ref count, null, out _);
        if (rc != Win32.ErrorMoreData || needed == 0)
        {
            return result;
        }

        var infos = new Win32.RmProcessInfo[needed + 4];
        count = (uint)infos.Length;
        fixed (Win32.RmProcessInfo* p = infos)
        {
            if (Win32.RmGetList(session.Handle, out _, ref count, p, out _) != 0)
            {
                return result;
            }

            for (var i = 0; i < count; i++)
            {
                var name = new string(p[i].AppName);
                var service = new string(p[i].ServiceShortName);
                result.Add(new FileHolder((int)p[i].Process.ProcessId, name.Length > 0 ? name : service, p[i].ApplicationType == 3));
            }
        }

        return result;
    }

    /// <summary>
    /// 请占用这些文件的程序正常关闭（RmShutdown，不带强制标志）。程序拒绝或没有响应时返回 false，被占用的文件照样跳过。
    /// 只在用户明确同意后调用。
    /// </summary>
    public static bool RequestClose(IReadOnlyCollection<string> paths)
    {
        using var session = Session.Start(paths);
        return session is not null && Win32.RmShutdown(session.Handle, 0, 0) == 0;
    }

    private sealed unsafe class Session : IDisposable
    {
        private Session(uint handle) => Handle = handle;

        public uint Handle { get; }

        public static Session? Start(IReadOnlyCollection<string> paths)
        {
            var key = stackalloc char[64];
            if (Win32.RmStartSession(out var handle, 0, key) != 0)
            {
                return null;
            }

            var session = new Session(handle);
            var files = paths.Take(MaxFiles).ToArray();
            var pointers = new nint[files.Length];
            try
            {
                for (var i = 0; i < files.Length; i++)
                {
                    pointers[i] = Marshal.StringToHGlobalUni(files[i]);
                }

                fixed (nint* p = pointers)
                {
                    if (Win32.RmRegisterResources(handle, (uint)files.Length, p, 0, null, 0, null) != 0)
                    {
                        session.Dispose();
                        return null;
                    }
                }
            }
            finally
            {
                foreach (var ptr in pointers)
                {
                    if (ptr != 0)
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }

            return session;
        }

        public void Dispose() => Win32.RmEndSession(Handle);
    }
}

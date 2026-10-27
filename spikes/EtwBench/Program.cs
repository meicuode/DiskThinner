// 阶段 2a.1：ETW 写入监控的开销实测（docs/phase2a-design.md 5.2 节）和只能实测确认的问题（2.2 节 V1–V22）。需要管理员。
//   EtwBench run --mode none|light|precise --workload idle|writeload|build --out <报告.txt>
//                [--seconds 180] [--writeload <exe>] [--project <csproj>] [--buffer-mb N] [--raw <文件>] [--raw-max 200000] [--slow-us 0]
//   EtwBench list-sessions --out <文件>          （V21，普通权限下运行）
//   EtwBench orphan --session <名字> --out <标记文件> --seconds 600   （V9：开会话后等着被强制结束）
// 报告是 key=value 文本。原始事件（--raw）含本机路径，只留在 artifacts\ 下，不提交。
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace EtwBench;

internal static partial class Program
{
    public static readonly Guid KernelFileGuid = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    public static readonly Guid KernelProcessGuid = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    public const ulong LightKeywords = 0x1C00;   // CREATE_NEW_FILE | DELETE_PATH | RENAME_SETLINK_PATH
    public const ulong PreciseKeywords = 0x1E10; // 轻量 + WRITE | FILENAME
    public const ulong ProcessKeyword = 0x10;

    private static int Main(string[] args)
    {
        var o = Args.Parse(args);
        try
        {
            return o.Command switch
            {
                "run" => Bench.Run(o),
                "list-sessions" => ListSessions(o),
                "orphan" => Orphan(o),
                "dump" => Dump(o),
                "replay" => Replay.Run(o),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (o.Get("out") is { } outFile)
            {
                File.AppendAllText(outFile, $"error={ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}\n");
            }

            return 3;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("用法见 Program.cs 开头");
        return 1;
    }

    /// <summary>V21：普通权限下能不能列出会话。</summary>
    private static int ListSessions(Args o)
    {
        var names = TraceEventSession.GetActiveSessionNames();
        File.WriteAllLines(o.Require("out"),
        [
            $"elevated={IsElevated()}",
            $"sessions={names.Count}",
            .. names.Where(n => n.StartsWith("DiskThinner", StringComparison.OrdinalIgnoreCase)).Select(n => "session=" + n),
        ]);
        return 0;
    }

    /// <summary>V9：开一个轻量会话后不停，等脚本强制结束这个进程，看会话是否残留。</summary>
    private static int Orphan(Args o)
    {
        var session = new TraceEventSession(o.Require("session")) { BufferSizeMB = 8, StopOnDispose = true };
        session.EnableProvider(KernelFileGuid, TraceEventLevel.Verbose, LightKeywords);
        session.EnableProvider(KernelProcessGuid, TraceEventLevel.Verbose, ProcessKeyword);
        var pump = new Thread(() => session.Source.Process()) { IsBackground = true };
        pump.Start();
        File.WriteAllText(o.Require("out"), $"started=1\npid={Environment.ProcessId}\n");
        Thread.Sleep(TimeSpan.FromSeconds(o.Int("seconds", 600)));
        session.Dispose();
        return 0;
    }

    /// <summary>读 --raw 存下的原始事件，把文件名含 --filter 的写入逐条列出（普通权限即可）：pid、IOFlags、偏移、大小、文件。</summary>
    private static int Dump(Args o)
    {
        var templates = Templates.Load(Path.Combine(AppContext.BaseDirectory, "templates.txt"));
        var filter = o.Require("filter");
        var names = new Dictionary<ulong, string>();
        var lines = new List<string>();
        var bytes = File.ReadAllBytes(o.Require("raw"));
        var pos = 6;
        var count = BitConverter.ToInt32(bytes, pos);
        pos += 4;
        for (var i = 0; i < count; i++)
        {
            var len = BitConverter.ToUInt16(bytes, pos + 22);
            var rec = bytes.AsSpan(pos, 24 + len);
            pos += 24 + len;
            int prov = rec[0], id = BitConverter.ToUInt16(rec[1..]), ver = rec[3];
            var pid = BitConverter.ToInt32(rec[4..]);
            var ts = BitConverter.ToDouble(rec[12..]);
            int ps = rec[20];
            var data = rec[24..];
            if (prov != 0 || templates.Find(Templates.KernelFile, id, ver) is not { } t)
            {
                continue;
            }

            if (id == 10)
            {
                names[t.Pointer(data, "FileKey", ps)] = t.Text(data, "FileName", ps) ?? "";
            }
            else if (id == 16 && names.TryGetValue(t.Pointer(data, "FileKey", ps), out var n) && n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"{ts:F1}\tpid={pid}\tflags=0x{t.U32(data, "IOFlags", ps):X}\toffset=0x{t.U64(data, "ByteOffset", ps):X}\tsize={t.U32(data, "IOSize", ps)}\t{Path.GetFileName(n)}");
            }
        }

        File.WriteAllLines(o.Require("out"), lines);
        return 0;
    }

    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>"C:\x\y" → "\Device\HarddiskVolume3\x\y"（Kernel-File 的路径格式）。</summary>
    public static string DevicePath(string path)
    {
        var drive = path[..2];
        var buffer = new char[1024];
        var n = QueryDosDeviceW(drive, buffer, buffer.Length);
        if (n == 0)
        {
            throw new IOException("QueryDosDevice 失败：" + Marshal.GetLastPInvokeError());
        }

        var device = new string(buffer, 0, Array.IndexOf(buffer, '\0'));
        return device + path[2..];
    }

    public static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        var n = GetShortPathNameW(path, buffer, buffer.Length);
        return n == 0 ? null : new string(buffer, 0, (int)n);
    }

    /// <summary>内核非分页池的字节数（会话缓冲区在这里，V18 / 5.2 节）。</summary>
    public static unsafe long NonPagedPoolBytes()
    {
        var info = new byte[128];
        BitConverter.TryWriteBytes(info, info.Length);
        fixed (byte* p = info)
        {
            if (!K32GetPerformanceInfo(p, info.Length))
            {
                return -1;
            }
        }

        // PERFORMANCE_INFORMATION（x64）：cb(4) + 对齐(4) + 10 个 SIZE_T；KernelNonpaged 是第 9 个，PageSize 是第 10 个
        var nonPaged = BitConverter.ToInt64(info, 8 + 8 * 8);
        var page = BitConverter.ToInt64(info, 8 + 9 * 8);
        return nonPaged * page;
    }

    /// <summary>ControlTraceW(EVENT_TRACE_CONTROL_QUERY)：会话的缓冲区大小（KB）、个数、丢失数（V18）。</summary>
    public static unsafe (uint BufferKb, uint Buffers, uint Min, uint Max, uint Lost, uint RealTimeLost, uint Mode)? QuerySession(string name)
    {
        const int size = 120 + 2048 * 2;
        var props = new byte[size];
        BitConverter.TryWriteBytes(props.AsSpan(0), size);              // Wnode.BufferSize
        BitConverter.TryWriteBytes(props.AsSpan(44), 0x00020000u);     // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        BitConverter.TryWriteBytes(props.AsSpan(112), 120u + 2048);    // LogFileNameOffset
        BitConverter.TryWriteBytes(props.AsSpan(116), 120u);           // LoggerNameOffset
        uint rc;
        fixed (byte* p = props)
        {
            rc = ControlTraceW(0, name, p, 0 /* EVENT_TRACE_CONTROL_QUERY */);
        }

        if (rc != 0)
        {
            return null;
        }

        uint U(int offset) => BitConverter.ToUInt32(props, offset);
        return (U(48), U(80), U(52), U(56), U(88), U(100), U(64));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint QueryDosDeviceW(string device, [Out] char[] target, int max);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool K32GetPerformanceInfo(byte* info, int size);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint ControlTraceW(ulong handle, string name, byte* properties, uint control);
}

internal sealed class Args
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public string Command { get; private set; } = "";

    public static Args Parse(string[] args)
    {
        var a = new Args { Command = args.Length > 0 ? args[0] : "" };
        for (var i = 1; i + 1 < args.Length; i += 2)
        {
            a._map[args[i].TrimStart('-')] = args[i + 1];
        }

        return a;
    }

    public string? Get(string name) => _map.GetValueOrDefault(name);

    public string Require(string name) => Get(name) ?? throw new ArgumentException("缺少 --" + name);

    public int Int(string name, int fallback) => Get(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
}

/// <summary>key=value 报告。</summary>
internal sealed class Report
{
    private readonly List<string> _lines = [];

    public void Add(string key, object? value) => _lines.Add(key + "=" + (value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value));

    public void Save(string path) => File.WriteAllLines(path, _lines);
}

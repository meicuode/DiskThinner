using System.Diagnostics;
using System.Text.Json;
using DiskThinner.Monitor;
using DiskThinner.Monitor.Etw;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace EtwBench;

/// <summary>
/// 阶段 2b.1 的双会话比对（docs/phase2b-design.md 2.2 节 W1、W2、W7）：TraceEvent 会话和 NativeSession 同时开着，
/// 提供程序和关键字相同（精确模式），跑一次 WriteLoad，比对两边收到的事件。
///   EtwBench compare --writeload &lt;exe&gt; --out &lt;报告.txt&gt; [--buffer-mb 32]
/// 比对两组：
///   load：事件头的进程号是 cmd.exe / WriteLoad，或者 Kernel-Process 事件的目标进程是它们——两边应该逐个一致
///     （提供程序、事件号、版本、进程号、线程号、载荷字节），时间戳只记差；
///   window：负载期间（去掉头尾各 0.5 秒）全机的事件，一边有的另一边也要有（W7：两个会话同时开着时各自完整）。
/// 报告不含路径（载荷只比较，不写出）。
/// </summary>
internal static class Compare
{
    private sealed class Ev(int provider, int id, int version, int pid, int tid, long ticks, int pointerSize, byte[] payload)
    {
        public int Provider { get; } = provider;
        public int Id { get; } = id;
        public int Version { get; } = version;
        public int Pid { get; } = pid;
        public int Tid { get; } = tid;
        public long Ticks { get; } = ticks;
        public int PointerSize { get; } = pointerSize;
        public byte[] Payload { get; } = payload;

        public string Key => $"{Provider}|{Id}|{Version}|{Pid}|{Tid}|{Convert.ToHexString(Payload)}";

        public Guid ProviderGuid => Provider == 0 ? Providers.KernelFile : Providers.KernelProcess;
    }

    private static int ProviderIndex(Guid g) => g == Providers.KernelFile ? 0 : g == Providers.KernelProcess ? 1 : -1;

    public static int Run(Args o)
    {
        var outFile = o.Require("out");
        var exe = o.Require("writeload");
        var bufferMb = o.Int("buffer-mb", 32);
        var report = new Report();
        report.Add("elevated", Program.IsElevated());
        report.Add("os", Environment.OSVersion.VersionString);
        report.Add("buffer_mb", bufferMb);

        var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        var teName = $"DiskThinnerBench-cmp-te-{stamp}";
        var ntName = $"DiskThinnerBench-cmp-nt-{stamp}";
        var te = new List<Ev>(200_000);
        var nt = new List<Ev>(200_000);
        var check = new TdhVerifier();

        var workDir = Path.Combine(Path.GetTempPath(), "DiskThinner-2a-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workDir);

        var teSession = new TraceEventSession(teName) { BufferSizeMB = bufferMb, StopOnDispose = true };
        NativeSession? native = null;
        Thread? tePump = null;
        try
        {
            teSession.EnableProvider(Program.KernelFileGuid, TraceEventLevel.Verbose, Program.PreciseKeywords);
            teSession.EnableProvider(Program.KernelProcessGuid, TraceEventLevel.Verbose, Program.ProcessKeyword);
            teSession.Source.AllEvents += e =>
            {
                var p = ProviderIndex(e.ProviderGuid);
                if (p >= 0)
                {
                    te.Add(new Ev(p, (int)e.ID, e.Version, e.ProcessID, e.ThreadID, e.TimeStamp.ToUniversalTime().Ticks, e.PointerSize, e.EventData()));
                }
            };

            native = NativeSession.Start(ntName, bufferMb,
                [(Providers.KernelFile, Program.PreciseKeywords), (Providers.KernelProcess, Program.ProcessKeyword)],
                (in RawEvent raw, EtwRecord record) =>
                {
                    check.Check(raw, record);
                    var p = ProviderIndex(raw.Provider);
                    if (p >= 0)
                    {
                        nt.Add(new Ev(p, raw.Id, raw.Version, raw.ProcessId, raw.ThreadId, raw.TimestampUtcTicks, raw.PointerSize, raw.Payload.ToArray()));
                    }
                });

            tePump = new Thread(() => teSession.Source.Process()) { IsBackground = true, Name = "TePump" };
            tePump.Start();
            native.StartPump("NativePump");
            Thread.Sleep(2000);

            var t0 = DateTime.UtcNow.Ticks;
            var sw = Stopwatch.StartNew();
            var (cmdPid, loadPid) = RunWriteLoad(exe, workDir);
            report.Add("workload_ms", sw.ElapsedMilliseconds);
            Thread.Sleep(1000);
            var t1 = DateTime.UtcNow.Ticks;
            Thread.Sleep(4000); // 两边的缓冲区都交付（FlushTimer 1 秒）

            // W2：两边的丢失数，和 ControlTrace(QUERY) 直接查到的
            report.Add("te_events_lost", teSession.EventsLost);
            report.Add("native_events_lost", native.EventsLost);
            if (Program.QuerySession(teName) is { } qt)
            {
                report.Add("te_query_lost", qt.Lost);
                report.Add("te_query_realtime_lost", qt.RealTimeLost);
            }

            if (Program.QuerySession(ntName) is { } qn)
            {
                report.Add("native_query_lost", qn.Lost);
                report.Add("native_query_realtime_lost", qn.RealTimeLost);
                report.Add("native_buffer_kb", qn.BufferKb);
                report.Add("native_buffers", qn.Buffers);
            }

            var stopped = native.Stop(TimeSpan.FromSeconds(10));
            teSession.Dispose();
            tePump.Join(TimeSpan.FromSeconds(10));
            report.Add("native_stop_ok", stopped);
            report.Add("native_callback_error", native.CallbackError?.GetType().Name ?? "");
            var active = NativeSession.ActiveSessionNames() ?? [];
            report.Add("sessions_left", active.Count(n => n == teName || n == ntName));

            report.Add("te_received", te.Count);
            report.Add("native_received", nt.Count);
            report.Add("parse_verified", check.Verified);
            report.Add("parse_mismatch", check.Mismatches);
            report.Add("parse_first_mismatch", check.FirstMismatch ?? "");

            // load：cmd.exe 和 WriteLoad 的事件逐个一致
            var pids = new HashSet<int> { cmdPid, loadPid };
            bool InLoad(Ev e)
            {
                if (pids.Contains(e.Pid))
                {
                    return true;
                }

                if (e.Provider != 1)
                {
                    return false;
                }

                var m = new MonitorEvent();
                return PayloadParser.TryParse(new RawEvent(e.ProviderGuid, e.Id, e.Version, e.Pid, e.Tid, e.Ticks, e.PointerSize, e.Payload), m)
                    && pids.Contains(m.TargetProcessId);
            }

            var teLoad = te.Where(InLoad).ToList();
            var ntLoad = nt.Where(InLoad).ToList();
            report.Add("load_te", teLoad.Count);
            report.Add("load_native", ntLoad.Count);
            report.Add("load_kinds", string.Join(",", ntLoad.GroupBy(e => $"{e.Provider}/{e.Id}/v{e.Version}").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Count()}")));
            var (onlyTe, onlyNt, diffs) = Match(teLoad, ntLoad);
            report.Add("load_only_te", onlyTe);
            report.Add("load_only_native", onlyNt);
            AddDiffs(report, "load_ts_diff", diffs);

            // window：负载期间全机的事件，一边有的另一边也要有
            var lo = t0 + TimeSpan.TicksPerMillisecond * 500;
            var hi = t1 - TimeSpan.TicksPerMillisecond * 500;
            var teAll = Counts(te);
            var ntAll = Counts(nt);
            var teIn = Counts(te.Where(e => e.Ticks >= lo && e.Ticks <= hi));
            var ntIn = Counts(nt.Where(e => e.Ticks >= lo && e.Ticks <= hi));
            report.Add("window_te", teIn.Values.Sum());
            report.Add("window_native", ntIn.Values.Sum());
            report.Add("window_te_missing_in_native", teIn.Sum(kv => Math.Max(0, kv.Value - ntAll.GetValueOrDefault(kv.Key))));
            report.Add("window_native_missing_in_te", ntIn.Sum(kv => Math.Max(0, kv.Value - teAll.GetValueOrDefault(kv.Key))));
            var (_, _, allDiffs) = Match(te, nt);
            AddDiffs(report, "all_ts_diff", allDiffs);
        }
        finally
        {
            native?.Dispose();
            teSession.Dispose();
            if (Path.GetFileName(workDir).StartsWith("DiskThinner-2a-", StringComparison.Ordinal) && workDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.Delete(workDir, recursive: true);
                }
                catch (IOException ex)
                {
                    report.Add("cleanup_error", ex.Message);
                }
            }

            report.Save(outFile);
        }

        return 0;
    }

    private static Dictionary<string, int> Counts(IEnumerable<Ev> events)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in events)
        {
            var k = e.Key;
            d[k] = d.GetValueOrDefault(k) + 1;
        }

        return d;
    }

    /// <summary>按（提供程序, 事件号, 版本, 进程号, 线程号, 载荷）配对；同一键的事件按时间顺序一一对应，记时间戳的差（100 纳秒）。</summary>
    private static (int OnlyA, int OnlyB, List<long> Diffs) Match(List<Ev> a, List<Ev> b)
    {
        var ga = a.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(e => e.Ticks).Order().ToList(), StringComparer.Ordinal);
        var gb = b.GroupBy(e => e.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(e => e.Ticks).Order().ToList(), StringComparer.Ordinal);
        var onlyA = 0;
        var onlyB = 0;
        var diffs = new List<long>();
        foreach (var (k, ta) in ga)
        {
            var tb = gb.GetValueOrDefault(k) ?? [];
            var n = Math.Min(ta.Count, tb.Count);
            for (var i = 0; i < n; i++)
            {
                diffs.Add(tb[i] - ta[i]);
            }

            onlyA += ta.Count - n;
        }

        foreach (var (k, tb) in gb)
        {
            onlyB += Math.Max(0, tb.Count - (ga.GetValueOrDefault(k)?.Count ?? 0));
        }

        return (onlyA, onlyB, diffs);
    }

    private static void AddDiffs(Report r, string prefix, List<long> diffs)
    {
        if (diffs.Count == 0)
        {
            return;
        }

        diffs.Sort();
        var abs = diffs.Select(Math.Abs).Order().ToList();
        r.Add(prefix + "_min_us", diffs[0] / 10.0);
        r.Add(prefix + "_median_us", diffs[diffs.Count / 2] / 10.0);
        r.Add(prefix + "_max_us", diffs[^1] / 10.0);
        r.Add(prefix + "_abs_p99_us", abs[(int)(abs.Count * 0.99)] / 10.0);
        r.Add(prefix + "_zero", diffs.Count(d => d == 0));
    }

    private static (int CmdPid, int LoadPid) RunWriteLoad(string exe, string workDir)
    {
        var json = Path.Combine(Path.GetTempPath(), "dt-wl-" + Guid.NewGuid().ToString("N") + ".json");
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" --dir \"{workDir}\" --report \"{json}\"\"") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var cmdPid = p.Id;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("WriteLoad 退出码 " + p.ExitCode);
        }

        var load = JsonSerializer.Deserialize(File.ReadAllText(json), BenchJson.Default.WriteLoadReport)!;
        File.Delete(json);
        return (cmdPid, load.Pid);
    }
}

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace EtwBench;

/// <summary>一次实测：开（或不开）会话，跑一种负载，记开销和 V 清单需要的数。</summary>
internal static class Bench
{
    public static int Run(Args o)
    {
        var mode = o.Require("mode");
        var workload = o.Require("workload");
        var outFile = o.Require("out");
        var report = new Report();
        report.Add("mode", mode);
        report.Add("workload", workload);
        report.Add("elevated", Program.IsElevated());
        report.Add("os", Environment.OSVersion.VersionString);
        report.Add("cpus", Environment.ProcessorCount);
        report.Add("priority", Process.GetCurrentProcess().PriorityClass);

        var templates = Templates.Load(Path.Combine(AppContext.BaseDirectory, "templates.txt"));
        var workDir = Path.Combine(Path.GetTempPath(), "DiskThinner-2a-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workDir);
        var collector = new Collector(templates, workDir, o.Int("raw-max", 0) > 0 && o.Get("raw") is not null ? o.Int("raw-max", 0) : 0, o.Int("slow-us", 0));

        TraceEventSession? session = null;
        Thread? pump = null;
        var sessionName = $"DiskThinnerBench-{mode}-{DateTime.Now:yyyyMMddHHmmss}";
        var bufferMb = o.Int("buffer-mb", mode == "precise" ? 32 : 8);
        var poolBefore = Program.NonPagedPoolBytes();
        if (mode != "none")
        {
            session = new TraceEventSession(sessionName) { BufferSizeMB = bufferMb, StopOnDispose = true };
            session.EnableProvider(Program.KernelFileGuid, TraceEventLevel.Verbose, mode == "light" ? Program.LightKeywords : Program.PreciseKeywords);
            session.EnableProvider(Program.KernelProcessGuid, TraceEventLevel.Verbose, Program.ProcessKeyword);
            session.Source.AllEvents += collector.OnEvent;
            pump = new Thread(() => session.Source.Process()) { IsBackground = true, Name = "EtwPump" };
            pump.Start();
            report.Add("session", sessionName);
            report.Add("buffer_mb_requested", bufferMb);
        }

        using var sampler = new Sampler(collector, () => session?.EventsLost ?? 0);
        Thread.Sleep(2000); // 进程表、名字缓存先建起来
        if (session is not null && Program.QuerySession(sessionName) is { } q)
        {
            report.Add("buffer_kb", q.BufferKb);
            report.Add("buffers", q.Buffers);
            report.Add("buffers_min", q.Min);
            report.Add("buffers_max", q.Max);
            report.Add("buffer_total_mb", q.BufferKb * (long)q.Buffers / 1024.0);
            report.Add("log_file_mode", "0x" + q.Mode.ToString("X"));
        }

        sampler.Mark("workload");
        var sw = Stopwatch.StartNew();
        WriteLoadReport? load = null;
        switch (workload)
        {
            case "idle":
                Thread.Sleep(TimeSpan.FromSeconds(o.Int("seconds", 180)));
                break;
            case "writeload":
                load = RunWriteLoad(o.Require("writeload"), workDir, collector);
                break;
            case "build":
                RunBuild(o.Require("project"), workDir, report);
                break;
            default:
                throw new ArgumentException("不认识的负载：" + workload);
        }

        var elapsed = sw.Elapsed;
        sampler.Mark("after");
        report.Add("workload_ms", (long)elapsed.TotalMilliseconds);
        var poolAfter = Program.NonPagedPoolBytes();

        if (workload == "writeload")
        {
            Extras(workDir, session, mode, collector);
        }

        Thread.Sleep(3000); // 等最后的缓冲区交付
        if (session is not null && Program.QuerySession(sessionName) is { } q2)
        {
            report.Add("buffers_at_end", q2.Buffers);
            report.Add("kernel_events_lost", q2.Lost);
            report.Add("kernel_realtime_buffers_lost", q2.RealTimeLost);
        }

        var lost = session?.EventsLost ?? 0;
        session?.Dispose();
        pump?.Join(TimeSpan.FromSeconds(10));
        sampler.Stop();
        report.Add("session_still_active", TraceEventSession.GetActiveSessionNames().Contains(sessionName));

        report.Add("pool_nonpaged_before", poolBefore);
        report.Add("pool_nonpaged_after_workload", poolAfter);
        report.Add("pool_nonpaged_delta_mb", (poolAfter - poolBefore) / 1048576.0);
        sampler.WriteTo(report);
        report.Add("events_received", collector.Received);
        report.Add("events_lost", lost);
        report.Add("lost_rate", collector.Received + lost == 0 ? 0 : (double)lost / (collector.Received + lost));
        collector.WriteTo(report, load);

        if (o.Get("raw") is { } raw && collector.RawCount > 0)
        {
            collector.SaveRaw(raw);
            report.Add("raw_file", raw);
            report.Add("raw_events", collector.RawCount);
        }

        // 只删除这次自己建的工作目录
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
        return 0;
    }

    private static WriteLoadReport RunWriteLoad(string exe, string workDir, Collector collector)
    {
        var json = Path.Combine(Path.GetTempPath(), "dt-wl-" + Guid.NewGuid().ToString("N") + ".json");
        // 经 cmd.exe 启动：进程链里应该有 cmd.exe（1.3 第 7 条）
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" --dir \"{workDir}\" --report \"{json}\"\"") { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        collector.CmdPid = p.Id;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("WriteLoad 退出码 " + p.ExitCode);
        }

        var load = JsonSerializer.Deserialize(File.ReadAllText(json), BenchJson.Default.WriteLoadReport)!;
        File.Delete(json);
        collector.LoadPid = load.Pid;
        return load;
    }

    private static void RunBuild(string project, string workDir, Report report)
    {
        var outDir = Path.Combine(workDir, "build-out");
        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "build", project, "-c", "Release", "--no-incremental", "--no-restore", "-m:1", "--disable-build-servers", "-nodeReuse:false", "-v", "q", "-nologo", "-o", outDir })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        report.Add("build_exit", p.ExitCode);
        if (p.ExitCode != 0)
        {
            report.Add("build_output", (stdout.Result + stderr.Result).ReplaceLineEndings(" ")[..Math.Min(400, (stdout.Result + stderr.Result).Length)]);
        }
    }

    /// <summary>
    /// V10：覆盖已有文件（CREATE_ALWAYS）、新建目录有没有 CreateNewFile；V17：精确模式下改成轻量关键字后写一个文件，
    /// 看还有没有写事件（立即生效时应该没有）。
    /// </summary>
    private static void Extras(string workDir, TraceEventSession? session, string mode, Collector collector)
    {
        var extra = Path.Combine(workDir, "extra");
        Directory.CreateDirectory(extra);
        var data = new byte[64 * 1024];
        File.WriteAllBytes(Path.Combine(extra, "over.bin"), data);
        Thread.Sleep(200);
        File.WriteAllBytes(Path.Combine(extra, "over.bin"), data); // 第二次是覆盖
        Directory.CreateDirectory(Path.Combine(extra, "subdir"));
        if (session is not null && mode == "precise")
        {
            Thread.Sleep(1500);
            session.EnableProvider(Program.KernelFileGuid, TraceEventLevel.Verbose, Program.LightKeywords);
            collector.DowngradedAt = Stopwatch.GetTimestamp();
            Thread.Sleep(500);
            File.WriteAllBytes(Path.Combine(extra, "after-downgrade.bin"), data);
        }
    }
}

/// <summary>每秒采样监控进程（就是本进程）的 CPU、内存和事件数。</summary>
internal sealed class Sampler : IDisposable
{
    private readonly Collector _collector;
    private readonly Func<int> _lost;
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly List<(long Tick, double CpuMs, long Ws, long Priv, long Events, int Lost, string Phase)> _samples = [];
    private string _phase = "warmup";

    public Sampler(Collector collector, Func<int> lost)
    {
        _collector = collector;
        _lost = lost;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sampler" };
        _thread.Start();
    }

    public void Mark(string phase) => _phase = phase;

    private void Loop()
    {
        using var self = Process.GetCurrentProcess();
        while (!_stop)
        {
            self.Refresh();
            lock (_samples)
            {
                _samples.Add((Stopwatch.GetTimestamp(), self.TotalProcessorTime.TotalMilliseconds, self.WorkingSet64, self.PrivateMemorySize64, _collector.Received, _lost(), _phase));
            }

            Thread.Sleep(1000);
        }
    }

    public void Stop()
    {
        _stop = true;
        _thread.Join(3000);
    }

    public void Dispose() => Stop();

    public void WriteTo(Report r)
    {
        List<(long Tick, double CpuMs, long Ws, long Priv, long Events, int Lost, string Phase)> s;
        lock (_samples)
        {
            s = [.. _samples];
        }

        if (s.Count < 2)
        {
            return;
        }

        var w = s.Where(x => x.Phase == "workload").ToList();
        if (w.Count < 2)
        {
            w = s;
        }

        var wall = (double)(w[^1].Tick - w[0].Tick) / Stopwatch.Frequency * 1000;
        var cpu = w[^1].CpuMs - w[0].CpuMs;
        r.Add("cpu_avg_machine_pct", wall <= 0 ? 0 : cpu / wall / Environment.ProcessorCount * 100);
        var peakCore = 0.0;
        var peakEvents = 0L;
        for (var i = 1; i < s.Count; i++)
        {
            var dt = (double)(s[i].Tick - s[i - 1].Tick) / Stopwatch.Frequency * 1000;
            if (dt > 0)
            {
                peakCore = Math.Max(peakCore, (s[i].CpuMs - s[i - 1].CpuMs) / dt * 100);
                peakEvents = Math.Max(peakEvents, (long)((s[i].Events - s[i - 1].Events) / (dt / 1000)));
            }
        }

        r.Add("cpu_peak_core_pct", peakCore);
        r.Add("events_per_sec_avg", wall <= 0 ? 0 : (w[^1].Events - w[0].Events) / (wall / 1000));
        r.Add("events_per_sec_peak", peakEvents);
        r.Add("ws_start_mb", s[0].Ws / 1048576.0);
        r.Add("ws_peak_mb", s.Max(x => x.Ws) / 1048576.0);
        r.Add("ws_delta_mb", (s.Max(x => x.Ws) - s[0].Ws) / 1048576.0);
        r.Add("private_peak_mb", s.Max(x => x.Priv) / 1048576.0);
        r.Add("lost_at_end_of_workload", w[^1].Lost);
    }
}

public sealed class WriteLoadReport
{
    public int Pid { get; set; }
    public string? ShortName { get; set; }
    public long ElapsedMs { get; set; }
    public Dictionary<string, WriteLoadPart> Parts { get; set; } = [];
}

public sealed class WriteLoadPart
{
    public string? Path { get; set; }
    public int CreatedFiles { get; set; }
    public int CreatedDirs { get; set; }
    public int Deleted { get; set; }
    public int Renamed { get; set; }
    public long BytesWritten { get; set; }
    public long AppendDataBytes { get; set; }
    public long CmdAppendBytes { get; set; }
    public int CmdPid { get; set; }
    public long FinalBytes { get; set; }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.SnakeCaseLower)]
[System.Text.Json.Serialization.JsonSerializable(typeof(WriteLoadReport))]
internal sealed partial class BenchJson : System.Text.Json.Serialization.JsonSerializerContext;

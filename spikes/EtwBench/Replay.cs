using System.Diagnostics;
using DiskThinner.Monitor;

namespace EtwBench;

/// <summary>
/// replay：把 run --raw 存下的原始事件送进 Monitor 的 PayloadParser → ReorderBuffer → MonitorAggregator（普通权限即可，
/// docs/phase2a-design.md 第 12.2 节第 6 条方案 A）。写入负载的原始事件按 WriteLoad 默认参数逐项比对，并测单线程处理速率。
///   EtwBench replay --raw <文件> --out <报告> [--mode precise|light] [--rounds 5]
/// </summary>
internal static class Replay
{
    private sealed record Rec(Guid Provider, int Id, int Version, int Pid, int Tid, long Ticks, int PointerSize, byte[] Payload);

    public static int Run(Args o)
    {
        var records = Read(o.Require("raw"));
        var mode = o.Get("mode") == "light" ? MonitorMode.Light : MonitorMode.Precise;
        var report = new Report();
        report.Add("events", records.Count);
        var devices = new PrefixDeviceMap(DosDevices());

        // 处理速率：单线程回放几遍
        var rounds = o.Int("rounds", 5);
        var sw = Stopwatch.StartNew();
        MonitorAggregator? last = null;
        for (var i = 0; i < rounds; i++)
        {
            last = Feed(records, mode, devices);
        }

        var perSecond = records.Count * rounds / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
        report.Add("replay_events_per_sec", (long)perSecond);
        var a = last!;
        report.Add("parse_errors", a.Stats.ParseErrors);
        report.Add("filtered", a.Stats.Filtered);
        report.Add("unresolved_writes", a.Stats.UnresolvedWrites);
        report.Add("duplicate_writes", a.Stats.DuplicateWrites);
        report.Add("bad_offsets", a.Stats.BadOffsets);
        report.Add("directories", a.Directories.Count);
        report.Add("chains", a.Chains.Count);
        report.Add("name_map_peak", a.NameMapPeak);
        report.Add("flags", a.Flags);

        // 写入负载的目录（%TEMP%\DiskThinner-2a-*）：按子目录汇总，和 WriteLoad 默认参数的期望比对
        var totals = new Dictionary<string, Counters>(StringComparer.OrdinalIgnoreCase);
        var chains = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var hour in a.Hours)
        {
            foreach (var ((dir, chain), c) in a.HourCounters(hour))
            {
                var path = a.Directories.PathOf(dir);
                var i = path.IndexOf("\\DiskThinner-2a-", StringComparison.OrdinalIgnoreCase);
                if (i < 0)
                {
                    continue;
                }

                var rest = path[(i + 1)..];
                var slash = rest.IndexOf('\\');
                var rel = slash < 0 ? "" : rest[(slash + 1)..];
                var t = totals.GetValueOrDefault(rel);
                t.Add(c);
                totals[rel] = t;
                if (!chains.TryGetValue(rel, out var set))
                {
                    chains[rel] = set = [];
                }

                var ch = a.Chains[chain];
                set.Add(ch.Kind == ChainKind.Normal ? ch.Links : ch.Kind.ToString());
            }
        }

        foreach (var (rel, c) in totals.OrderBy(x => x.Key))
        {
            report.Add($"dir[{rel}]", $"creates={c.Creates} dirs={c.CreatedDirs} deletes={c.Deletes} renames={c.Renames} write_bytes={c.WriteBytes} growth={c.Growth} chains={string.Join(" | ", chains[rel])}");
        }

        if (mode == MonitorMode.Precise)
        {
            Compare(report, totals);
        }

        report.Save(o.Require("out"));
        return 0;
    }

    /// <summary>WriteLoad 默认参数下各子目录的期望（第 1.3 节第 7 条、第 12.2 节第 3 条）。</summary>
    private static void Compare(Report report, Dictionary<string, Counters> totals)
    {
        var fails = 0;
        void Check(string rel, string what, long expected, Func<Counters, long> actual, long tolerance = 0)
        {
            var value = actual(totals.GetValueOrDefault(rel));
            var ok = value >= expected && value <= expected + tolerance;
            fails += ok ? 0 : 1;
            report.Add($"check[{rel}].{what}", $"{(ok ? "ok" : "MISMATCH")} {value}/{expected}");
        }

        Check("small", "creates", 500, c => c.Creates);
        Check("small", "deletes", 100, c => c.Deletes);
        Check("small", "renames", 50, c => c.Renames);
        Check("small", "write_bytes", 500 * 65536L, c => c.WriteBytes);
        Check("small", "growth", 400 * 65536L, c => c.Growth);
        Check("big", "write_bytes", 128L << 20, c => c.WriteBytes);
        Check("big", "growth", 128L << 20, c => c.Growth);
        // a：4 KiB + 两次 FILE_APPEND_DATA 追加；o：4 KiB + 重开追加 4 KiB；c：4 KiB + cmd 的 >>；另有 append.cmd 脚本（长度随路径变，放进容差）
        Check("append", "growth", 50 * 3 * 4096L + 50 * 2 * 4096L + 20 * 4096L, c => c.Growth, tolerance: 4096);
        Check("append", "write_bytes", 50 * 3 * 4096L + 50 * 2 * 4096L + 20 * 4096L, c => c.WriteBytes, tolerance: 4096);
        Check("temp", "creates", 50, c => c.Creates);
        Check("temp", "deletes", 50, c => c.Deletes);
        Check("temp", "growth", 0, c => c.Growth);
        Check("prealloc", "growth", 0, c => c.Growth);
        Check("LongDirectoryNameForShortPath", "growth", 20 * 4096L, c => c.Growth);
        Check(@"move\tmp", "growth", 0, c => c.Growth);
        Check(@"move\dst", "growth", 20 * 65536L, c => c.Growth);
        Check(@"move\done", "growth", 20 * 65536L, c => c.Growth);
        report.Add("check_failures", fails);
    }

    private static MonitorAggregator Feed(List<Rec> records, MonitorMode mode, IDeviceMap devices)
    {
        var a = new MonitorAggregator(mode, devices);
        var reorder = new ReorderBuffer(TimeSpan.FromSeconds(1));
        foreach (var r in records)
        {
            var e = new MonitorEvent();
            if (!PayloadParser.TryParse(new RawEvent(r.Provider, r.Id, r.Version, r.Pid, r.Tid, r.Ticks, r.PointerSize, r.Payload), e))
            {
                a.Stats.ParseErrors++;
                continue;
            }

            reorder.Add(e, a.Process);
        }

        reorder.Flush(a.Process);
        a.Finish(records.Count == 0 ? 0 : records[^1].Ticks);
        return a;
    }

    private static List<Rec> Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var count = BitConverter.ToInt32(bytes, 6);
        var pos = 10;
        var list = new List<Rec>(count);
        var baseTicks = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc).Ticks;
        for (var i = 0; i < count; i++)
        {
            var len = BitConverter.ToUInt16(bytes, pos + 22);
            var prov = bytes[pos] switch { 0 => Providers.KernelFile, 1 => Providers.KernelProcess, _ => Guid.Empty };
            list.Add(new Rec(prov, BitConverter.ToUInt16(bytes, pos + 1), bytes[pos + 3], BitConverter.ToInt32(bytes, pos + 4), BitConverter.ToInt32(bytes, pos + 8),
                baseTicks + (long)(BitConverter.ToDouble(bytes, pos + 12) * TimeSpan.TicksPerMillisecond), bytes[pos + 20], bytes.AsSpan(pos + 24, len).ToArray()));
            pos += 24 + len;
        }

        // 存下的是相对时间：按第一个进程启动事件的创建时间（真实的 UTC）对齐，进程表才能按时间找到实例
        foreach (var r in list.Where(r => r.Provider == Providers.KernelProcess && r.Id == 1))
        {
            var e = new MonitorEvent();
            if (PayloadParser.TryParse(new RawEvent(r.Provider, r.Id, r.Version, r.Pid, r.Tid, r.Ticks, r.PointerSize, r.Payload), e) && e.CreateTimeUtcTicks > 0)
            {
                var shift = e.CreateTimeUtcTicks - r.Ticks;
                return [.. list.Select(x => x with { Ticks = x.Ticks + shift })];
            }
        }

        return list;
    }

    private static Dictionary<string, string> DosDevices()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            var letter = d.Name[..2];
            map[Program.DevicePath(letter + "\\").TrimEnd('\\')] = letter;
        }

        return map;
    }
}

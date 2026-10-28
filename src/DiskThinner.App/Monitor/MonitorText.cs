using System.Globalization;
using DiskThinner.Core;
using DiskThinner.Monitor;
using DiskThinner.Rules;

namespace DiskThinner.App.Monitor;

/// <summary>"写入进程"一格的内容：文字、标签（"实测"或空）、工具提示。</summary>
internal sealed record WriterCell(string Text, string Tag, string? Note, bool Observed);

/// <summary>
/// 写入监控在界面上的文字（docs/phase2a-design.md 5.9.4–5.9.6 节）：进程链、时长、丢失率、"写入进程"列的三种情况。
/// 都是纯函数，页面和测试共用。库里存的进程链不含中文（服务宿主存成 svchost.exe[wuauserv,bits]），这里再格式化。
/// </summary>
internal static class MonitorText
{
    /// <summary>进程号 0 和 4 的写入。</summary>
    public static string SystemWriter => L.T("系统（缓存回写或内核）");

    /// <summary>"svchost.exe[wuauserv,bits]" → "svchost.exe（服务 wuauserv、bits）"；三个以上写"服务 A、B 等 4 个"。</summary>
    public static string Link(string link)
    {
        var open = link.IndexOf('[');
        if (open < 0 || !link.EndsWith(']'))
        {
            return link == "System" ? SystemWriter : link;
        }

        return link[..open] + L.F("（{0}）", Services(link[(open + 1)..^1]));
    }

    /// <summary>"a,b,c,d" → "服务 a、b 等 4 个"。</summary>
    public static string Services(string services)
    {
        var names = services.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length switch
        {
            0 => "",
            1 or 2 => L.F("服务 {0}", string.Join(L.T("、"), names)),
            _ => L.F("服务 {0} 等 {1} 个", string.Join(L.T("、"), names.Take(2)), names.Length),
        };
    }

    /// <summary>映像的显示名：exe 名，服务宿主带上服务名。</summary>
    public static string Image(StoredImage? image) =>
        image is null ? "—" : image.Services.Length == 0 ? image.ExeName : image.ExeName + L.F("（{0}）", Services(image.Services));

    /// <summary>写入进程（进程链的第一环）。</summary>
    public static string Writer(StoredChain chain) => chain.Kind switch
    {
        ChainKind.System => SystemWriter,
        ChainKind.Unknown => L.F("未知进程（进程号 {0}）", chain.UnknownPid),
        ChainKind.Other => L.T("其他进程"),
        _ => Image(chain.Writer),
    };

    /// <summary>完整的进程链："uv.exe ← pwsh.exe ← Code.exe"（写入进程在左）。</summary>
    public static string FullChain(StoredChain chain) =>
        chain.Kind != ChainKind.Normal || chain.Links.Length == 0 ? Writer(chain) : string.Join(" ← ", chain.Links.Split(" ← ").Select(Link));

    /// <summary>概览用的短链："写入进程 ← 顶层应用"；顶层就是自己时只有写入进程。</summary>
    public static string ShortChain(StoredChain chain)
    {
        if (chain.Kind != ChainKind.Normal || chain.Top is null || chain.Writer is null
            || string.Equals(chain.Top.Path, chain.Writer.Path, StringComparison.OrdinalIgnoreCase))
        {
            return Writer(chain);
        }

        return Writer(chain) + " ← " + Image(chain.Top);
    }

    public static string Mode(MonitorMode mode) => mode == MonitorMode.Precise ? L.T("精确") : L.T("轻量");

    /// <summary>"4 小时"、"2.5 小时"、"35 分钟"。</summary>
    public static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 60)
        {
            return L.F("{0} 分钟", Math.Max(0, (int)span.TotalMinutes));
        }

        return L.F("{0} 小时", Hours(span.TotalHours));
    }

    /// <summary>剩余时间："3 小时 12 分"、"12 分钟"、"40 秒"。</summary>
    public static string Remaining(TimeSpan span)
    {
        if (span.TotalSeconds < 60)
        {
            return L.F("{0} 秒", Math.Max(0, (int)span.TotalSeconds));
        }

        if (span.TotalHours < 1)
        {
            return L.F("{0} 分钟", (int)span.TotalMinutes);
        }

        return span.Minutes == 0 ? L.F("{0} 小时", (int)span.TotalHours) : L.F("{0} 小时 {1} 分", (int)span.TotalHours, span.Minutes);
    }

    /// <summary>状态栏用的短格式："3:12"（小时:分）。</summary>
    public static string RemainingShort(TimeSpan span) =>
        $"{(int)Math.Max(0, span.TotalHours)}:{Math.Max(0, span.Minutes):00}";

    /// <summary>覆盖卡片的"N / M 小时"；范围不到 1 小时（例如刚存的两个快照）时按分钟写"N / M 分钟"。</summary>
    public static string Coverage(double coveredHours, double rangeHours) => rangeHours < 1
        ? L.F("{0} / {1} 分钟", (int)Math.Floor(Math.Max(0, coveredHours) * 60), (int)Math.Round(rangeHours * 60))
        : L.F("{0} / {1} 小时", Hours(coveredHours), Hours(Math.Round(rangeHours)));

    /// <summary>小时数向下取整到 0.1："14"、"2.5"。</summary>
    public static string Hours(double hours) => (Math.Floor(Math.Max(0, hours) * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture);

    public static string Lost(double rate) => L.F("丢失 {0}", (rate * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%");

    public static string EndReason(string? reason) => reason switch
    {
        "timeout" => L.T("到时停止"),
        "user" => L.T("手动停止"),
        "app-exit" => L.T("程序退出时停止"),
        "crashed" => L.T("程序意外退出"),
        "external" => L.T("被外部停止"),
        "store-error" => L.T("写库失败，已停止"),
        "error" => L.T("因错误停止"),
        _ => "",
    };

    /// <summary>
    /// 会话在 [fromUtc, toUtc) 里覆盖的小时数（多个会话重叠的部分只算一次）。正在监控的会话（名字是 <paramref name="liveSession"/>）算到现在，
    /// 其他没有结束时间的会话算到最后一次落盘。
    /// </summary>
    public static double CoveredHours(IEnumerable<SessionRecord> sessions, DateTime fromUtc, DateTime toUtc, string? liveSession = null, DateTime? nowUtc = null)
    {
        var spans = sessions
            .Select(s => (Start: s.StartedUtc, End: s.EndedUtc ?? (s.Name == liveSession ? nowUtc ?? DateTime.UtcNow : s.LastFlushUtc)))
            .Select(s => (Start: s.Start < fromUtc ? fromUtc : s.Start, End: s.End > toUtc ? toUtc : s.End))
            .Where(s => s.End > s.Start)
            .OrderBy(s => s.Start)
            .ToList();
        var total = TimeSpan.Zero;
        DateTime? curStart = null, curEnd = null;
        foreach (var (start, end) in spans)
        {
            if (curEnd is { } ce && start <= ce)
            {
                curEnd = end > ce ? end : ce;
                continue;
            }

            if (curStart is { } cs && curEnd is { } e)
            {
                total += e - cs;
            }

            (curStart, curEnd) = (start, end);
        }

        if (curStart is { } lastStart && curEnd is { } lastEnd)
        {
            total += lastEnd - lastStart;
        }

        return total.TotalHours;
    }

    /// <summary>
    /// 主要进程链（第 5.9.4 节）：净增长时精确模式按估算增长、其次写入量，轻量模式按新建 + 删除 + 重命名；净减少时按删除数、其次 |估算增长|。
    /// 返回第一名和它占合计的比例；没有能比的时候返回 null。
    /// </summary>
    public static (ChainWrites Main, double Share, int Count)? MainChain(IReadOnlyList<ChainWrites> rows, bool netDecrease, bool precise)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        IEnumerable<Func<Counters, long>> metrics = netDecrease
            ? [c => c.Deletes, c => Math.Abs(c.Growth), c => c.Weight]
            : precise
                ? [c => Math.Max(0, c.Growth), c => c.WriteBytes, c => c.Creates + c.Deletes + c.Renames, c => c.Weight]
                : [c => c.Creates + c.Deletes + c.Renames, c => c.Weight];
        foreach (var metric in metrics)
        {
            var total = rows.Sum(r => metric(r.Counters));
            if (total <= 0)
            {
                continue;
            }

            var ranked = rows.Where(r => metric(r.Counters) > 0).OrderByDescending(r => metric(r.Counters)).ToList();
            return (ranked[0], (double)metric(ranked[0].Counters) / total, ranked.Count);
        }

        return null;
    }

    /// <summary>
    /// 增长分析"变化最大的目录"和概览"增长最快"的写入进程一格（第 5.9.4、5.9.5 节，第 1.3 节第 9 条）。
    /// <paramref name="index"/> 为 null 或者范围内没有监控时返回 null（调用方按"未监控"显示）。
    /// </summary>
    public static WriterCell? Cell(WriteIndex? index, double coveredHours, double rangeHours, string path, long change, bool isFile, bool vmDisk, bool shortChain = false,
        bool remainder = false)
    {
        if (index is null || coveredHours <= 0)
        {
            return null;
        }

        // 热点是文件时用它所在的目录；是"其余部分"时只取这个目录自己的行（子目录另有热点）
        var dir = isFile ? Path.GetDirectoryName(path.TrimEnd('\\')) ?? path : path;
        var rows = index.Under(dir, exact: isFile || remainder);
        var precise = index.PreciseHours > 0;
        var scope = L.F("监控 {0}内", Coverage(coveredHours, rangeHours));
        if (MainChain(rows, change < 0, precise) is not { } main)
        {
            if (index.NearestRecordedAncestor(dir) is { } ancestor)
            {
                return new WriterCell(L.F("写入记录在上级目录 {0}", PathDisplay.Shorten(ancestor)), "", L.T("监控期间这个目录还没有单独记录（刚出现、或者写入量太小），写入记在上级目录里"), false);
            }

            if (index.UnresolvedBytes > 0 && index.WriteBytes > 0 && (double)index.UnresolvedBytes / index.WriteBytes >= 0.01)
            {
                return new WriterCell(L.T("没有能对上这里的写入"), "",
                    L.F("监控的 {0} 小时里没有记录到能对上这里的写入；另有 {1} 的写入对不上目录（监控开始前已打开的文件）", Hours(coveredHours), ByteSize.Format(index.UnresolvedBytes)), false);
            }

            return precise
                ? new WriterCell(L.F("监控的 {0} 小时里没有写入", Hours(coveredHours)), "", null, false)
                : new WriterCell(L.F("监控的 {0} 小时里没有新建或删除", Hours(coveredHours)), "", L.T("轻量模式只记录新建、删除、重命名，看不到写入量"), false);
        }

        var (best, share, count) = main;
        var approx = best.Counters.Sampled || index.LostRate > 0.01 ? "≈ " : "";
        var chainText = shortChain ? ShortChain(best.Chain) : FullChain(best.Chain);
        var text = share < 0.6 && count > 1 ? L.F("{0} 等 {1} 个进程", Writer(best.Chain), count) : chainText;
        if (vmDisk)
        {
            text += L.T("（虚拟磁盘，看不到内部是谁写的）");
        }

        if (isFile)
        {
            text += L.T("（所在目录）");
        }

        var total = new Counters();
        foreach (var r in rows)
        {
            total.Add(r.Counters);
        }

        var summary = precise
            ? L.F("{0}：新建 {1} 个文件，估算增长 {2}，写入 {3}", scope, total.Creates.ToString("N0", CultureInfo.InvariantCulture), ByteSize.FormatDelta(total.Growth), ByteSize.Format(total.WriteBytes))
            : L.F("{0}：新建 {1} 个、删除 {2} 个文件（轻量模式没有写入量）", scope, total.Creates.ToString("N0", CultureInfo.InvariantCulture), total.Deletes.ToString("N0", CultureInfo.InvariantCulture));
        var note = summary + L.F("；进程链：{0}", FullChain(best.Chain)) + (approx.Length > 0 ? L.T("；数字前的 ≈ 表示按抽样估算或有丢失") : "");
        return new WriterCell(approx + text, L.T("实测"), note, true);
    }
}

/// <summary>空间浏览详情里的 ETW 证据（docs/phase2a-design.md 5.9.6 节）：一到两行文字；从没监控过时为空。</summary>
internal static class MonitorEvidence
{
    public static IReadOnlyList<string> For(WriteIndex? index, string path, bool isDirectory, DateTime? nowUtc = null)
    {
        if (index is null || index.Sessions.Count == 0)
        {
            return [];
        }

        var now = nowUtc ?? DateTime.UtcNow;
        var dir = isDirectory ? path : Path.GetDirectoryName(path.TrimEnd('\\')) ?? path;
        var here = isDirectory ? L.T("在这里") : L.T("在所在目录");
        var rows = index.Under(dir, exact: !isDirectory);
        var hours = MonitorText.Hours(MonitorText.CoveredHours(index.Sessions, now.AddDays(-30), now, nowUtc: now));
        var scope = L.F("近 30 天 {0} 次诊断（共 {1} 小时）", index.Sessions.Count, hours);
        if (rows.Count == 0)
        {
            if (index.NearestRecordedAncestor(dir) is { } ancestor)
            {
                return [L.F("{0}里，写入记录在上级目录 {1}", scope, PathDisplay.Shorten(ancestor))];
            }

            if (index.UnresolvedBytes > 0 && index.WriteBytes > 0 && (double)index.UnresolvedBytes / index.WriteBytes >= 0.01)
            {
                return [L.F("{0}里没有能对上这里的写入（另有 {1} 的写入对不上目录，是监控开始前已打开的文件）", scope, ByteSize.Format(index.UnresolvedBytes))];
            }

            return [index.PreciseHours > 0
                ? L.F("{0}里没有程序在这里写入", scope)
                : L.F("{0}里没有程序在这里新建或删除文件（轻量模式看不到写入量）", scope)];
        }

        var lines = new List<string>();
        if (index.LastSessionWithWrites(dir) is { } last)
        {
            var precise = last.Mode == MonitorMode.Precise;
            var sessionRows = index.Under(dir, exact: !isDirectory, sessionId: last.Id);
            if (MonitorText.MainChain(sessionRows, netDecrease: false, precise) is { } main)
            {
                var c = main.Main.Counters;
                var start = last.StartedUtc.ToLocalTime();
                var end = (last.EndedUtc ?? last.LastFlushUtc).ToLocalTime();
                var when = start.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + "–" + end.ToString(start.Date == end.Date ? "HH:mm" : "MM-dd HH:mm", CultureInfo.InvariantCulture);
                var approx = c.Sampled ? "≈ " : "";
                var what = !precise ? L.F("新建了 {0} 个文件", c.Creates.ToString("N0", CultureInfo.InvariantCulture))
                    : c.Creates > 0 ? L.F("新建了 {0} 个文件，估算增长 {1}{2}", c.Creates.ToString("N0", CultureInfo.InvariantCulture), approx, ByteSize.FormatDelta(c.Growth))
                    : L.F("写入了 {0}{1}", approx, ByteSize.Format(c.WriteBytes));
                lines.Add(L.F("最近一次诊断（{0}，{1}）中，{2} {3}{4}；进程链：{5}", when, MonitorText.Mode(last.Mode), MonitorText.Writer(main.Main.Chain), here, what,
                    MonitorText.FullChain(main.Main.Chain)));
            }
        }

        var sessionsWithWrites = index.Rows.Where(r => r.Dir is not null && (isDirectory ? WriteIndex.IsUnder(r.Dir, dir) : string.Equals(r.Dir.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.SessionId).Distinct().Count();
        if (index.Sessions.Count > 1)
        {
            var writers = rows.GroupBy(r => MonitorText.Writer(r.Chain)).OrderByDescending(g => g.Sum(r => r.Counters.Weight)).Take(2).Select(g => g.Key).ToList();
            lines.Add(L.F("近 30 天 {0} 次诊断共 {1} 小时，{2} 次在这里有写入，主要来自 {3}", index.Sessions.Count, hours, sessionsWithWrites, string.Join(L.T("、"), writers)));
        }

        return lines;
    }
}

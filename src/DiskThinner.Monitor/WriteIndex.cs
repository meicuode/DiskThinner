namespace DiskThinner.Monitor;

/// <summary>一个目录（或范围）的写入，按进程链汇总。</summary>
public sealed record ChainWrites(StoredChain Chain, Counters Counters);

/// <summary>写入进程排行的一行。</summary>
public sealed record WriterRank(StoredImage? Writer, ChainKind Kind, StoredImage? Top, Counters Counters, IReadOnlyList<string?> MainDirs);

/// <summary>
/// 查询（docs/phase2a-design.md 5.7 节第 4 条、第 12.2 节第 7、15 条）：一段时间内的汇总行，按会话、目录、进程链保留。
/// 界面（增长分析、概览、空间浏览）都从这里取，文字由界面决定。
/// </summary>
public sealed class WriteIndex(IReadOnlyList<SessionRecord> sessions, IReadOnlyList<IndexRow> rows, IReadOnlyList<HourCoverage> hours)
{
    public static readonly WriteIndex Empty = new([], [], []);

    public IReadOnlyList<SessionRecord> Sessions { get; } = sessions;
    public IReadOnlyList<IndexRow> Rows { get; } = rows;
    public IReadOnlyList<HourCoverage> Hours { get; } = hours;

    /// <summary>常驻服务按调用者过滤掉的写入（其他用户个人目录下的），只有计数没有路径（Q7）；没有时为 null。</summary>
    public Counters? HiddenOtherUsers { get; init; }

    public int HiddenRows { get; init; }

    /// <summary>有常驻服务的数据。</summary>
    public bool HasAgent => Sessions.Any(s => s.Source == SessionSource.Agent);

    /// <summary>
    /// 合并几份索引（本机诊断库 + 常驻服务，docs/phase2b-design.md 5.6 节）：会话、行、小时直接拼起来。
    /// 服务的会话号在转换时已经取负数（<c>AgentClient</c>），不会和诊断库的冲突；覆盖小时按 HourUtc 去重计算（<see cref="MonitoredHours"/>）。
    /// </summary>
    public static WriteIndex Merge(params WriteIndex?[] parts)
    {
        var list = parts.Where(p => p is not null && (p.Sessions.Count > 0 || p.Rows.Count > 0 || p.HiddenOtherUsers is not null)).Cast<WriteIndex>().ToList();
        if (list.Count == 0)
        {
            return Empty;
        }

        if (list.Count == 1)
        {
            return list[0];
        }

        var ids = list.SelectMany(p => p.Sessions).GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (ids.Count > 0)
        {
            throw new ArgumentException("session ids overlap: " + string.Join(",", ids));
        }

        Counters? hidden = null;
        foreach (var h in list.Select(p => p.HiddenOtherUsers).OfType<Counters>())
        {
            var c = hidden ?? new Counters();
            c.Add(h);
            hidden = c;
        }

        return new WriteIndex([.. list.SelectMany(p => p.Sessions).OrderBy(s => s.StartedUtc)], [.. list.SelectMany(p => p.Rows)], [.. list.SelectMany(p => p.Hours)])
        {
            HiddenOtherUsers = hidden,
            HiddenRows = list.Sum(p => p.HiddenRows),
        };
    }

    /// <summary>有监控的小时数（不同会话的同一个小时只算一次）。</summary>
    public int MonitoredHours => Hours.Select(h => h.HourUtc).Distinct().Count();

    /// <summary>有精确模式的小时数。</summary>
    public int PreciseHours => Hours.Where(h => h.Mode == MonitorMode.Precise).Select(h => h.HourUtc).Distinct().Count();

    public long Received => Hours.Sum(h => h.Received);
    public long Lost => Hours.Sum(h => h.Lost);
    public double LostRate => Received + Lost == 0 ? 0 : (double)Lost / (Received + Lost);
    public bool Sampled => Hours.Any(h => h.SampledOut > 0);
    public long UnresolvedBytes => Hours.Sum(h => h.UnresolvedBytes);
    public long WriteBytes => Hours.Sum(h => h.WriteBytes);

    /// <summary>
    /// 目录 <paramref name="path"/>（含下面的子目录）的写入，减去 <paramref name="excludes"/> 下面的；<paramref name="exact"/> 时只取等于这个目录的行
    /// （热点是文件时用它所在的目录）。"对不上目录"的行永远不返回。
    /// </summary>
    public IReadOnlyList<ChainWrites> Under(string path, IEnumerable<string>? excludes = null, bool exact = false, long? sessionId = null)
    {
        var self = path.TrimEnd('\\');
        var ex = excludes?.Select(e => e.TrimEnd('\\')).Where(e => !string.Equals(e, self, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var map = new Dictionary<StoredChain, Counters>();
        foreach (var r in Rows)
        {
            if (r.Dir is null || (sessionId is { } s && r.SessionId != s))
            {
                continue;
            }

            var match = exact ? string.Equals(r.Dir.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) : IsUnder(r.Dir, path);
            if (!match || ex.Any(e => IsUnder(r.Dir, e)))
            {
                continue;
            }

            var c = map.GetValueOrDefault(r.Chain);
            c.Add(r.Counters);
            map[r.Chain] = c;
        }

        return [.. map.Select(kv => new ChainWrites(kv.Key, kv.Value)).OrderByDescending(x => Sort(x.Counters))];
    }

    /// <summary>
    /// 有写入记录的最近的上级目录（热点是监控期间新出现、被并到上级的目录时，第 5.7 节第 4 条："写入记录在上级目录 X"）。
    /// </summary>
    public string? NearestRecordedAncestor(string path)
    {
        string? best = null;
        foreach (var r in Rows)
        {
            if (r.Dir is not null && IsUnder(path, r.Dir) && !string.Equals(r.Dir.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                && (best is null || r.Dir.Length > best.Length))
            {
                best = r.Dir;
            }
        }

        return best;
    }

    /// <summary>写入进程排行：按写入进程（含服务名）汇总，"对不上目录"的写入也算（主要目录写 null）。</summary>
    public IReadOnlyList<WriterRank> Writers(int top = 20)
    {
        var groups = Rows.Where(r => r.Chain.Kind != ChainKind.Other)
            .GroupBy(r => (r.Chain.Kind, Key: r.Chain.Writer is { } w ? (w.Path.ToUpperInvariant(), w.Services) : ("", ""), r.Chain.UnknownPid));
        var list = new List<WriterRank>();
        foreach (var g in groups)
        {
            var total = new Counters();
            foreach (var r in g)
            {
                total.Add(r.Counters);
            }

            var dirs = g.GroupBy(r => r.Dir, StringComparer.OrdinalIgnoreCase).Select(d => (d.Key, W: d.Sum(r => r.Counters.Weight)))
                .OrderByDescending(d => d.W).Take(2).Select(d => d.Key).ToList();
            var topApp = g.GroupBy(r => r.Chain.Top?.Path ?? "", StringComparer.OrdinalIgnoreCase).OrderByDescending(t => t.Sum(r => r.Counters.Weight)).First().First().Chain.Top;
            var first = g.First().Chain;
            list.Add(new WriterRank(first.Writer, first.Kind, topApp, total, dirs));
        }

        return [.. list.OrderByDescending(w => w.Counters.WriteBytes).ThenByDescending(w => w.Counters.Creates).Take(top)];
    }

    /// <summary>最近一次在 <paramref name="path"/> 下有写入的会话。</summary>
    public SessionRecord? LastSessionWithWrites(string path)
    {
        var ids = Rows.Where(r => r.Dir is not null && IsUnder(r.Dir, path)).Select(r => r.SessionId).ToHashSet();
        return Sessions.Where(s => ids.Contains(s.Id)).MaxBy(s => s.StartedUtc);
    }

    /// <summary>排序：精确模式按估算增长，其余按新建、删除、重命名的个数。</summary>
    private static long Sort(in Counters c) => Math.Abs(c.Growth) + c.WriteBytes / 16 + (c.Creates + c.Deletes + c.Renames) * 4096;

    public static bool IsUnder(string path, string ancestor)
    {
        var a = ancestor.TrimEnd('\\');
        return path.StartsWith(a, StringComparison.OrdinalIgnoreCase) && (path.Length == a.Length || path[a.Length] == '\\' || a.EndsWith(':'));
    }
}

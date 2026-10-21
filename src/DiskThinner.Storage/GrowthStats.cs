namespace DiskThinner.Storage;

/// <summary>按快照算的增长统计：概览的"近 7 天净增长""约 N 天后写满"、增长分析的每日净增长。</summary>
public static class GrowthStats
{
    /// <summary>一个时间点的已用和可用空间（快照或当前扫描）。</summary>
    public readonly record struct Point(DateTime TakenAtUtc, long UsedBytes, long FreeBytes);

    public static Point Of(SnapshotInfo s) => new(s.TakenAtUtc, s.UsedBytes, s.FreeBytes);

    /// <summary>
    /// 用来算"近 N 天"的起点：<paramref name="days"/> 天前或更早的最后一个快照；没有那么早的，退而用至少半天前的最早一个。
    /// 都没有时返回 null（快照历史不够）。
    /// </summary>
    public static SnapshotInfo? Baseline(IReadOnlyList<SnapshotInfo> snapshots, DateTime nowUtc, int days)
    {
        var target = nowUtc.AddDays(-days);
        return snapshots.Where(s => s.TakenAtUtc <= target).MaxBy(s => s.TakenAtUtc)
            ?? snapshots.Where(s => s.TakenAtUtc <= nowUtc.AddHours(-12)).MinBy(s => s.TakenAtUtc);
    }

    /// <summary>净增长和按这个速度多少天后写满（不在增长或没有可用空间信息时为 null）。</summary>
    public static (long Change, double Days, double? DaysToFull) Rate(Point from, Point to)
    {
        var change = to.UsedBytes - from.UsedBytes;
        var days = Math.Max((to.TakenAtUtc - from.TakenAtUtc).TotalDays, 1.0 / 24);
        var perDay = change / days;
        double? toFull = perDay > 0 && to.FreeBytes > 0 ? to.FreeBytes / perDay : null;
        return (change, days, toFull);
    }

    /// <summary>
    /// 每日净增长：每天取最后一个点，和前一个有数据的日子相比。没有快照的日子不出柱子，变化算到下一个有数据的日子里。
    /// </summary>
    public static List<(DateOnly Day, long Change)> Daily(IEnumerable<Point> points, DateTime fromUtc, DateTime toUtc, TimeZoneInfo zone)
    {
        var byDay = points.Where(p => p.TakenAtUtc <= toUtc)
            .GroupBy(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(p.TakenAtUtc, zone)))
            .Select(g => (Day: g.Key, Last: g.MaxBy(p => p.TakenAtUtc)))
            .OrderBy(x => x.Day)
            .ToList();
        var fromDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone));
        var result = new List<(DateOnly, long)>();
        for (var i = 1; i < byDay.Count; i++)
        {
            if (byDay[i].Day > fromDay)
            {
                result.Add((byDay[i].Day, byDay[i].Last.UsedBytes - byDay[i - 1].Last.UsedBytes));
            }
        }

        return result;
    }
}

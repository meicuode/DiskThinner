using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Storage;

namespace DiskThinner.App.Growth;

/// <summary>
/// 增长相关的数据（后台线程上准备好，概览和增长分析页共用）：同一个盘的快照列表、当前扫描对应的快照树、
/// 概览用的"近 7 天"对比。
/// </summary>
internal sealed class GrowthModel
{
    /// <summary>距离上一次快照不到这么久时，扫描完不再自动保存。</summary>
    public static readonly TimeSpan AutoSnapshotInterval = TimeSpan.FromHours(6);

    public required SnapshotStore Store { get; init; }

    public required string Volume { get; init; }

    /// <summary>这个盘的快照，从早到晚。</summary>
    public required IReadOnlyList<SnapshotInfo> Snapshots { get; init; }

    /// <summary>当前扫描做成的快照树：自动保存了就是保存的那一份，否则是没有写入的预览。</summary>
    public SnapshotTree? Current { get; init; }

    /// <summary>当前扫描是否已经保存为快照。</summary>
    public bool CurrentSaved { get; init; }

    public DateTime NowUtc { get; init; }

    /// <summary>概览"近 7 天"的起点快照；快照历史不够时为 null。</summary>
    public SnapshotInfo? WeekBaseline { get; init; }

    public SnapshotDiff? WeekDiff { get; init; }

    public List<GrowthHotspot> WeekHotspots { get; init; } = [];

    public GrowthStats.Point? NowPoint => Current is { } c ? GrowthStats.Of(c.Info with { TakenAtUtc = NowUtc }) : null;

    /// <summary>
    /// 后台线程上：读快照列表；整个盘扫描完、离上次快照超过 6 小时就自动保存一次（并执行保留策略）；
    /// 再和 7 天前的快照对比。
    /// </summary>
    public static GrowthModel Build(SnapshotStore store, ScanResult result, Classification? classification, bool autoSave, DateTime nowUtc, bool forceSave = false)
    {
        var tree = result.Tree;
        var volume = tree.GetPath(tree.Root);
        var snapshots = store.List(volume);
        var isVolume = volume.Length <= 3 && result.Stats.VolumeTotalBytes > 0;
        SnapshotTree current;
        var saved = false;
        if (forceSave || autoSave && isVolume && (snapshots.Count == 0 || nowUtc - snapshots[^1].TakenAtUtc >= AutoSnapshotInterval))
        {
            current = store.Save(tree, result.Stats, nowUtc);
            store.ApplyRetention(nowUtc);
            snapshots = store.List(volume);
            saved = true;
        }
        else
        {
            current = store.Preview(tree, result.Stats, nowUtc);
        }

        var earlier = snapshots.Where(s => !saved || s.Id != current.Info.Id).ToList();
        var baseline = GrowthStats.Baseline(earlier, nowUtc, 7);
        SnapshotDiff? diff = null;
        List<GrowthHotspot> hot = [];
        if (baseline is not null)
        {
            // 对比用的当前快照按起点快照记录过的路径重新剪一次，两边的值都是实测的
            var from = store.Load(baseline.Id);
            diff = SnapshotDiff.Compute(from, store.Preview(tree, result.Stats, nowUtc, compareWith: from));
            hot = diff.Hotspots(stopAt: StopAt(tree, classification));
        }

        return new GrowthModel
        {
            Store = store,
            Volume = volume,
            Snapshots = snapshots,
            Current = current,
            CurrentSaved = saved,
            NowUtc = nowUtc,
            WeekBaseline = baseline,
            WeekDiff = diff,
            WeekHotspots = hot,
        };
    }

    /// <summary>热点分解停在规则命中的位置（dir / file 规则；兜底的 under 规则范围太大，不算）。</summary>
    public static Func<string, bool>? StopAt(NodeTree tree, Classification? c)
    {
        if (c is null)
        {
            return null;
        }

        return path =>
        {
            var n = tree.FindPath(path);
            return n >= 0 && c.IsRoot(n) && c.RuleOf(n) is { Mode: not MatchMode.Under };
        };
    }

    /// <summary>保存当前扫描（界面上的"立即创建快照"，文件夹扫描也可以），返回新的模型。</summary>
    public static GrowthModel SaveCurrent(SnapshotStore store, ScanResult result, Classification? classification, DateTime nowUtc) =>
        Build(store, result, classification, autoSave: false, nowUtc, forceSave: true);
}

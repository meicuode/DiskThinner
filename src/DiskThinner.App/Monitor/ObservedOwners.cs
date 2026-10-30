using System.Globalization;
using DiskThinner.Core;
using DiskThinner.Monitor;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.App.Monitor;

/// <summary>
/// ETW 作为归属证据（docs/phase2b-design.md FR-10、5.6 节，E11）：规则和推断都不认识的目录，如果近 30 天常驻监控覆盖不少于 7 天、
/// 主要写入进程占这里估算增长的 80% 以上（而且增长不小），归属写"实测：写入进程（顶层应用）"。
/// 只用常驻服务的数据（诊断只有几个小时，不够说明问题）；只看普通进程（系统的缓存回写、未知进程不算）。
/// 同一条路径上只取最上层的一个目录，下面的子目录跟着它。
/// </summary>
internal static class ObservedOwners
{
    public const double MinShare = 0.8;
    public static readonly TimeSpan MinCoverage = TimeSpan.FromDays(7);
    public const long MinGrowth = 64L << 20;

    public static Dictionary<int, Owner> Compute(WriteIndex index, NodeTree tree, OwnerMap owners, DateTime nowUtc)
    {
        var result = new Dictionary<int, Owner>();
        var agentSessions = index.Sessions.Where(s => s.Source == SessionSource.Agent).ToList();
        var coveredHours = MonitorText.CoveredHours(agentSessions, nowUtc.AddDays(-30), nowUtc, nowUtc: nowUtc, agentLive: true);
        if (coveredHours < MinCoverage.TotalHours)
        {
            return result;
        }

        var agentIds = agentSessions.Select(s => s.Id).ToHashSet();
        var growthRows = index.Rows.Where(r => r.Dir is not null && agentIds.Contains(r.SessionId) && r.Counters.Growth > 0).ToList();

        // 分叉：每个目录下有增长的直接子目录（加上它自己有行时算一个）。往上合并只走到分叉之前，
        // 不然 AppData\Local 这种放着很多程序的目录会被算成某一个程序的（它近 30 天的增长可能正好大多来自一个程序）
        var branches = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in growthRows)
        {
            Branch(branches, row.Dir!, "");
            for (var dir = row.Dir!; DirectoryTable.Split(dir).Parent is { } parent; dir = parent)
            {
                Branch(branches, parent, dir);
            }
        }

        // 每个候选目录：所有进程链的正增长合计，和每个写入进程的增长
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byWriter = new Dictionary<string, Dictionary<string, (long Growth, StoredChain Chain)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in growthRows)
        {
            var key = row.Chain.Kind == ChainKind.Normal && row.Chain.Writer is { } w && w.Path != DiskThinner.Monitor.Agent.AgentProtocol.OtherUserImage
                ? w.Path.ToUpperInvariant() + "|" + w.Services
                : "";
            for (var dir = row.Dir; dir is not null && Depth(dir) >= 2; dir = DirectoryTable.Split(dir).Parent is { } p && branches[p].Count == 1 ? p : null)
            {
                totals[dir] = totals.GetValueOrDefault(dir) + row.Counters.Growth;
                if (key.Length == 0)
                {
                    continue;
                }

                if (!byWriter.TryGetValue(dir, out var writers))
                {
                    byWriter[dir] = writers = new Dictionary<string, (long, StoredChain)>(StringComparer.Ordinal);
                }

                var (g, chain) = writers.GetValueOrDefault(key);
                writers[key] = (g + row.Counters.Growth, chain ?? row.Chain);
            }
        }

        var days = (int)Math.Round(coveredHours / 24);
        foreach (var (dir, total) in totals.Where(kv => kv.Value >= MinGrowth).OrderBy(kv => Depth(kv.Key)).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!byWriter.TryGetValue(dir, out var writers))
            {
                continue;
            }

            var (growth, chain) = writers.Values.MaxBy(v => v.Growth);
            var share = (double)growth / total;
            if (share < MinShare)
            {
                continue;
            }

            var node = tree.FindPath(dir);
            if (node < 0 || owners.Resolve(node) is not null || HasObservedAncestor(tree, node, result))
            {
                continue;
            }

            result[node] = Make(chain, share, days);
        }

        return result;
    }

    private static void Branch(Dictionary<string, HashSet<string>> branches, string dir, string child)
    {
        if (!branches.TryGetValue(dir, out var set))
        {
            branches[dir] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        set.Add(child);
    }

    private static bool HasObservedAncestor(NodeTree tree, int node, Dictionary<int, Owner> chosen)
    {
        for (var n = node; n >= 0; n = n == tree.Root ? -1 : tree[n].Parent)
        {
            if (chosen.ContainsKey(n))
            {
                return true;
            }
        }

        return false;
    }

    private static Owner Make(StoredChain chain, double share, int days)
    {
        var writer = MonitorText.Image(chain.Writer);
        var top = chain.Top is { } t && !string.Equals(t.Path, chain.Writer?.Path, StringComparison.OrdinalIgnoreCase) ? MonitorText.Image(t) : null;
        var name = top is null ? L.F("实测：{0}", writer) : L.F("实测：{0}（{1}）", writer, top);
        var percent = (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        var evidence = new List<OwnerEvidence>
        {
            new(OwnerEvidenceKind.Monitor, L.F("规则和推断都不认识这里；近 30 天常驻监控（共 {0} 天）里 {1} 的增长来自这个进程", days, percent), chain.Writer?.Path),
            new(OwnerEvidenceKind.Monitor, L.F("进程链：{0}", MonitorText.FullChain(chain))),
        };
        return new Owner(name, null, OwnerSource.Observed, "o:" + (chain.Writer?.Path.ToUpperInvariant() ?? ""), evidence);
    }

    /// <summary>"C:\" 是 0，"C:\Users" 是 1，"C:\Users\x" 是 2。</summary>
    private static int Depth(string path) => path.TrimEnd('\\').Count(c => c == '\\');
}

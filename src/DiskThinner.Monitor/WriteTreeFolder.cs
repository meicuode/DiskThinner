namespace DiskThinner.Monitor;

/// <summary>一行汇总：目录 × 进程链（一个小时内）。</summary>
public readonly record struct WriteRow(int Dir, int Chain, Counters Counters);

[Flags]
public enum FoldFlags
{
    None = 0,

    /// <summary>有写入的锚点本身就比预算多，照样输出（行数 ≤ 硬上限）。</summary>
    BudgetExceeded = 1,

    /// <summary>超过硬上限，把一些锚点并到了上级。</summary>
    AnchorsFolded = 2,
}

/// <summary>
/// 落盘时的目录合并（docs/phase2a-design.md 5.7 节，D6）：快照里记录的目录（锚点）是合并的下限，预算内保留更细的目录。
/// 在计数器的副本上做，只输出行，不改内存里的计数器。编号 0（对不上目录）的行原样输出，不参与合并。
/// </summary>
public static class WriteTreeFolder
{
    public static (List<WriteRow> Rows, FoldFlags Flags) Fold(
        IReadOnlyDictionary<(int Dir, int Chain), Counters> counters, DirectoryTable dirs, IReadOnlySet<int> anchors, int budget = 1000, int hardLimit = 2000)
    {
        var byDir = new Dictionary<int, Dictionary<int, Counters>>();
        var unresolved = new List<WriteRow>();
        foreach (var ((dir, chain), c) in counters)
        {
            if (c.IsEmpty)
            {
                continue;
            }

            if (dir == DirectoryTable.Unresolved)
            {
                unresolved.Add(new WriteRow(dir, chain, c));
                continue;
            }

            var d = dirs.Resolve(dir);
            Add(byDir, d, chain, c);
        }

        var anchorSet = anchors.Select(dirs.Resolve).ToHashSet();
        var rows = byDir.Values.Sum(m => m.Count);
        var flags = FoldFlags.None;

        // 第 1 段：反复取权重最小的非锚点叶子目录并到上级
        if (rows > budget)
        {
            (rows, _) = FoldLeaves(byDir, dirs, d => !anchorSet.Contains(d) && dirs.Parent(d) > 0, rows, budget);
        }

        // 第 2 段：每个目录只留权重最大的 N 条进程链，其余并成"其他进程"
        foreach (var n in new[] { 8, 4, 2, 1 })
        {
            if (rows <= budget)
            {
                break;
            }

            rows = KeepTopChains(byDir, n);
        }

        // 第 3 段：还超出时，≤ 硬上限照样输出；> 硬上限时把权重最小的锚点叶子并到上级（根除外）
        if (rows > budget)
        {
            flags |= FoldFlags.BudgetExceeded;
            if (rows > hardLimit)
            {
                flags |= FoldFlags.AnchorsFolded;
                while (rows > hardLimit)
                {
                    // 一次并一个：并到同一个上级时行数可能暂时不变，按有没有并掉目录判断是否结束（只剩各盘的根时结束）
                    (rows, var merged) = FoldLeaves(byDir, dirs, d => dirs.Parent(d) > 0, rows, hardLimit, maxSteps: 1, afterMerge: parent => KeepTop(byDir, parent, 1));
                    if (merged == 0)
                    {
                        break;
                    }
                }
            }
        }

        var result = new List<WriteRow>(rows + unresolved.Count);
        foreach (var (dir, map) in byDir)
        {
            foreach (var (chain, c) in map)
            {
                result.Add(new WriteRow(dir, chain, c));
            }
        }

        result.AddRange(unresolved);
        return (result, flags);
    }

    private static void Add(Dictionary<int, Dictionary<int, Counters>> byDir, int dir, int chain, in Counters c)
    {
        if (!byDir.TryGetValue(dir, out var map))
        {
            byDir[dir] = map = [];
        }

        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(map, chain, out _);
        slot.Add(c);
    }

    /// <summary>
    /// 把满足 <paramref name="canFold"/> 的叶子目录（下面没有其他有计数的目录）按权重从小到大并到上级，直到行数 ≤ <paramref name="limit"/>。
    /// </summary>
    private static (int Rows, int Merged) FoldLeaves(Dictionary<int, Dictionary<int, Counters>> byDir, DirectoryTable dirs, Func<int, bool> canFold, int rows, int limit,
        int maxSteps = int.MaxValue, Action<int>? afterMerge = null)
    {
        // 每个目录下面有几个有计数的目录（含间接的）
        var below = new Dictionary<int, int>();
        foreach (var d in byDir.Keys)
        {
            for (var p = dirs.Parent(d); p > 0; p = dirs.Parent(p))
            {
                below[p] = below.GetValueOrDefault(p) + 1;
            }
        }

        var queue = new PriorityQueue<int, long>();
        foreach (var d in byDir.Keys)
        {
            if (below.GetValueOrDefault(d) == 0 && canFold(d))
            {
                queue.Enqueue(d, Weight(byDir[d]));
            }
        }

        var steps = 0;
        while (rows > limit && steps < maxSteps && queue.TryDequeue(out var leaf, out _))
        {
            if (!byDir.Remove(leaf, out var map))
            {
                continue;
            }

            var parent = dirs.Parent(leaf);
            var parentHad = byDir.TryGetValue(parent, out var parentMap) ? parentMap.Count : 0;
            rows -= map.Count;
            foreach (var (chain, c) in map)
            {
                Add(byDir, parent, chain, c);
            }

            afterMerge?.Invoke(parent);
            rows += byDir[parent].Count - parentHad;
            steps++;

            // 叶子没了：沿上级减一；上级变成叶子时可以接着并
            for (var p = parent; p > 0; p = dirs.Parent(p))
            {
                below[p] = below.GetValueOrDefault(p) - 1;
            }

            if (below.GetValueOrDefault(parent) == 0 && canFold(parent))
            {
                queue.Enqueue(parent, Weight(byDir[parent]));
            }
        }

        return (rows, steps);
    }

    private static int KeepTopChains(Dictionary<int, Dictionary<int, Counters>> byDir, int n)
    {
        var rows = 0;
        foreach (var dir in byDir.Keys.ToList())
        {
            KeepTop(byDir, dir, n);
            rows += byDir[dir].Count;
        }

        return rows;
    }

    /// <summary>一个目录只留权重最大的 n 条进程链（"其他进程" chain 0 不占名额），其余并进 chain 0。</summary>
    private static void KeepTop(Dictionary<int, Dictionary<int, Counters>> byDir, int dir, int n)
    {
        var map = byDir[dir];
        var named = map.Where(kv => kv.Key != 0).ToList();
        if (named.Count <= n)
        {
            return;
        }

        var drop = named.OrderByDescending(kv => kv.Value.Weight).ThenBy(kv => kv.Key).Skip(n).ToList();
        var other = map.GetValueOrDefault(0);
        foreach (var (chain, c) in drop)
        {
            other.Add(c);
            map.Remove(chain);
        }

        map[0] = other;
    }

    private static long Weight(Dictionary<int, Counters> map)
    {
        long w = 0;
        foreach (var c in map.Values)
        {
            w += c.Weight;
        }

        return w;
    }
}

using System.Globalization;
using DiskThinner.Scanner;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Storage;

/// <summary>
/// 快照库（%LOCALAPPDATA%\DiskThinner\snapshots.db，研发方案 3.5 节）。
/// path 表让同一个目录在不同快照里有稳定的编号；snap_entry 只存剪枝后的节点。
/// 每次操作单独打开连接，界面和计划任务里的 snapshot 子命令可以同时使用（SQLite 的文件锁，等待最多 5 秒）。
/// </summary>
public sealed class SnapshotStore
{
    private const int FormatVersion = 1;

    private static readonly string[] Schema =
    [
        "CREATE TABLE IF NOT EXISTS path (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, UNIQUE (parent_id, name))",
        "CREATE TABLE IF NOT EXISTS snapshot (id INTEGER PRIMARY KEY, taken_at INTEGER NOT NULL, volume TEXT NOT NULL COLLATE NOCASE, " +
            "total_bytes INTEGER NOT NULL, free_bytes INTEGER NOT NULL, scan_mode TEXT NOT NULL, min_dir_bytes INTEGER NOT NULL, " +
            "min_file_bytes INTEGER NOT NULL, root_bytes INTEGER NOT NULL, entry_count INTEGER NOT NULL)",
        "CREATE INDEX IF NOT EXISTS snapshot_volume ON snapshot (volume, taken_at)",
        "CREATE TABLE IF NOT EXISTS snap_entry (snapshot_id INTEGER NOT NULL, path_id INTEGER NOT NULL, is_dir INTEGER NOT NULL, " +
            "alloc_bytes INTEGER NOT NULL, file_count INTEGER NOT NULL, last_write INTEGER NOT NULL, unrecorded_bytes INTEGER NOT NULL, " +
            "PRIMARY KEY (snapshot_id, path_id)) WITHOUT ROWID",
    ];

    public SnapshotStore(string path)
    {
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var conn = Open();
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > FormatVersion)
        {
            throw new InvalidDataException($"快照库来自更新的版本（格式 {version}），请升级 DiskThinner");
        }

        using var tx = conn.BeginTransaction();
        foreach (var sql in Schema)
        {
            Exec(conn, sql);
        }

        Exec(conn, $"PRAGMA user_version = {FormatVersion}");
        tx.Commit();
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "snapshots.db");

    public string FilePath { get; }

    /// <summary>快照库文件的大小。</summary>
    public long FileBytes => File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout = 5000");
        Exec(conn, "PRAGMA foreign_keys = OFF");
        return conn;
    }

    /// <summary>剪枝后保存一次快照。返回保存的内容（可以直接拿来对比，不用再读一遍）。</summary>
    public SnapshotTree Save(NodeTree tree, ScanStats stats, DateTime takenAtUtc, SnapshotOptions? options = null)
    {
        options ??= SnapshotOptions.Default;
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var paths = LoadPaths(conn);
        var volume = tree.GetPath(tree.Root);
        var rerecord = ByParent(Rerecord(conn, volume), paths);
        var entries = SnapshotPruner.Prune(tree, options, new DbPathIds(conn, paths), rerecord);

        using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO snapshot (taken_at, volume, total_bytes, free_bytes, scan_mode, min_dir_bytes, min_file_bytes, root_bytes, entry_count) " +
                                 "VALUES ($t, $v, $total, $free, $mode, $dir, $file, $root, $n); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$t", ToUnixMs(takenAtUtc));
            insert.Parameters.AddWithValue("$v", volume);
            insert.Parameters.AddWithValue("$total", stats.VolumeTotalBytes);
            insert.Parameters.AddWithValue("$free", stats.VolumeFreeBytes);
            insert.Parameters.AddWithValue("$mode", stats.Mode == ScanMode.Mft ? "mft" : "walk");
            insert.Parameters.AddWithValue("$dir", options.MinDirBytes);
            insert.Parameters.AddWithValue("$file", options.MinFileBytes);
            insert.Parameters.AddWithValue("$root", entries[0].Alloc);
            insert.Parameters.AddWithValue("$n", entries.Count);
            var id = (long)insert.ExecuteScalar()!;

            using var entry = conn.CreateCommand();
            entry.CommandText = "INSERT INTO snap_entry VALUES ($s, $p, $d, $a, $f, $w, $u)";
            var ps = new[] { "$s", "$p", "$d", "$a", "$f", "$w", "$u" }.Select(n => entry.Parameters.Add(n, SqliteType.Integer)).ToArray();
            ps[0].Value = id;
            foreach (var e in entries)
            {
                ps[1].Value = e.PathId;
                ps[2].Value = e.IsDir ? 1 : 0;
                ps[3].Value = e.Alloc;
                ps[4].Value = e.FileCount;
                ps[5].Value = e.LastWrite;
                ps[6].Value = e.Unrecorded;
                entry.ExecuteNonQuery();
            }

            tx.Commit();
            var info = new SnapshotInfo(id, DateTime.SpecifyKind(FromUnixMs(ToUnixMs(takenAtUtc)), DateTimeKind.Utc), volume, stats.VolumeTotalBytes, stats.VolumeFreeBytes,
                stats.Mode == ScanMode.Mft ? "mft" : "walk", options.MinDirBytes, options.MinFileBytes, entries[0].Alloc, entries.Count);
            return new SnapshotTree(info, entries);
        }
    }

    /// <summary>
    /// 按快照的剪枝方式把当前扫描结果做成一棵快照树，但不写入（增长分析里的"当前扫描"）。
    /// 路径编号用库里已有的，新路径用负数，和任何已有快照都对不上。
    /// </summary>
    /// <param name="compareWith">
    /// 要和它对比的快照：它记录过的路径这次都记下来（哪怕已经低于阈值），这样对比时两边的值都是实测的，
    /// 不会出现"之前 200 MB、现在低于阈值没记录，只好按 0 算"。
    /// </param>
    public SnapshotTree Preview(NodeTree tree, ScanStats stats, DateTime takenAtUtc, SnapshotOptions? options = null, SnapshotTree? compareWith = null)
    {
        options ??= SnapshotOptions.Default;
        using var conn = Open();
        var volume = tree.GetPath(tree.Root);
        var ids = Rerecord(conn, volume);
        if (compareWith is not null)
        {
            for (var i = 0; i < compareWith.Count; i++)
            {
                ids.Add(compareWith.PathId(i));
            }
        }

        var paths = LoadPaths(conn);
        var entries = SnapshotPruner.Prune(tree, options, new TransientPathIds(paths), ByParent(ids, paths));
        var info = new SnapshotInfo(0, takenAtUtc, volume, stats.VolumeTotalBytes, stats.VolumeFreeBytes, stats.Mode == ScanMode.Mft ? "mft" : "walk",
            options.MinDirBytes, options.MinFileBytes, entries[0].Alloc, entries.Count);
        return new SnapshotTree(info, entries);
    }

    /// <summary>快照列表，按时间从早到晚。</summary>
    public IReadOnlyList<SnapshotInfo> List(string? volume = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, taken_at, volume, total_bytes, free_bytes, scan_mode, min_dir_bytes, min_file_bytes, root_bytes, entry_count FROM snapshot " +
                          (volume is null ? "" : "WHERE volume = $v ") + "ORDER BY taken_at, id";
        if (volume is not null)
        {
            cmd.Parameters.AddWithValue("$v", volume);
        }

        var list = new List<SnapshotInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(ReadInfo(r));
        }

        return list;
    }

    private static SnapshotInfo ReadInfo(SqliteDataReader r) => new(
        r.GetInt64(0), DateTime.SpecifyKind(FromUnixMs(r.GetInt64(1)), DateTimeKind.Utc), r.GetString(2), r.GetInt64(3), r.GetInt64(4),
        r.GetString(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt32(9));

    public SnapshotTree Load(long id)
    {
        using var conn = Open();
        SnapshotInfo info;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, taken_at, volume, total_bytes, free_bytes, scan_mode, min_dir_bytes, min_file_bytes, root_bytes, entry_count FROM snapshot WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read())
            {
                throw new KeyNotFoundException($"没有编号为 {id} 的快照");
            }

            info = ReadInfo(r);
        }

        var raw = new List<(long ParentId, SnapshotEntry Entry)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT e.path_id, p.parent_id, p.name, e.is_dir, e.alloc_bytes, e.file_count, e.last_write, e.unrecorded_bytes " +
                              "FROM snap_entry e JOIN path p ON p.id = e.path_id WHERE e.snapshot_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                raw.Add((r.GetInt64(1), new SnapshotEntry
                {
                    PathId = r.GetInt64(0),
                    Name = r.GetString(2),
                    IsDir = r.GetInt64(3) != 0,
                    Alloc = r.GetInt64(4),
                    FileCount = r.GetInt32(5),
                    LastWrite = r.GetInt64(6),
                    Unrecorded = r.GetInt64(7),
                }));
            }
        }

        return new SnapshotTree(info, Order(raw));
    }

    /// <summary>把读出来的条目排成"父项在前"，根放在下标 0。父项不在快照里的条目（库损坏）丢弃。</summary>
    private static List<SnapshotEntry> Order(List<(long ParentId, SnapshotEntry Entry)> raw)
    {
        var byParent = raw.ToLookup(x => x.ParentId, x => x.Entry);
        var ids = raw.Select(x => x.Entry.PathId).ToHashSet();
        var root = raw.FirstOrDefault(x => !ids.Contains(x.ParentId)).Entry ?? throw new InvalidDataException("快照里没有根目录");
        var ordered = new List<SnapshotEntry> { root };
        root.Parent = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            foreach (var child in byParent[ordered[i].PathId].OrderByDescending(e => e.Alloc))
            {
                child.Parent = i;
                ordered.Add(child);
            }
        }

        return ordered;
    }

    public void Delete(long id)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        DeleteSnapshot(conn, id);
        tx.Commit();
    }

    private static void DeleteSnapshot(SqliteConnection conn, long id)
    {
        foreach (var sql in new[] { "DELETE FROM snap_entry WHERE snapshot_id = $id", "DELETE FROM snapshot WHERE id = $id" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 保留策略：最近 <paramref name="keepDays"/> 天的快照全部保留；更早的每个盘每周只留最后一个。返回删除的个数。
    /// </summary>
    public int ApplyRetention(DateTime nowUtc, int keepDays = 180)
    {
        var cutoff = nowUtc.AddDays(-keepDays);
        var remove = SnapshotRetention.ToRemove(List(), cutoff);
        if (remove.Count == 0)
        {
            return 0;
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        foreach (var id in remove)
        {
            DeleteSnapshot(conn, id);
        }

        tx.Commit();
        return remove.Count;
    }

    /// <summary>上一次同一个盘的快照里，凭自己大小记录过的路径：这次低于阈值也要再记一次。</summary>
    private static HashSet<long> Rerecord(SqliteConnection conn, string volume)
    {
        var set = new HashSet<long>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT e.path_id FROM snap_entry e JOIN snapshot s ON s.id = e.snapshot_id " +
                          "WHERE s.id = (SELECT id FROM snapshot WHERE volume = $v ORDER BY taken_at DESC, id DESC LIMIT 1) " +
                          "AND e.alloc_bytes >= CASE WHEN e.is_dir <> 0 THEN s.min_dir_bytes ELSE s.min_file_bytes END";
        cmd.Parameters.AddWithValue("$v", volume);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            set.Add(r.GetInt64(0));
        }

        return set;
    }

    /// <summary>把要再记一次的路径编号换成"父目录编号 → 子项名称"，剪枝时按名称查，不用为每个子项生成字符串。</summary>
    private static Dictionary<long, HashSet<string>> ByParent(HashSet<long> ids, Dictionary<(long, string), long> paths)
    {
        var map = new Dictionary<long, HashSet<string>>();
        if (ids.Count == 0)
        {
            return map;
        }

        foreach (var ((parent, name), id) in paths)
        {
            if (ids.Contains(id))
            {
                if (!map.TryGetValue(parent, out var names))
                {
                    map[parent] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                names.Add(name);
            }
        }

        return map;
    }

    private static Dictionary<(long, string), long> LoadPaths(SqliteConnection conn)
    {
        var map = new Dictionary<(long, string), long>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, parent_id, name FROM path";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map.TryAdd((r.GetInt64(1), TransientPathIds.PathKey(r.GetString(2))), r.GetInt64(0));
        }

        return map;
    }

    /// <summary>写入时用的路径编号：新路径直接插入 path 表（在保存快照的同一个事务里）。</summary>
    private sealed class DbPathIds(SqliteConnection conn, Dictionary<(long, string), long> known) : IPathIds
    {
        private SqliteCommand? _insert;

        public long? Find(long parentId, string name) => known.TryGetValue((parentId, TransientPathIds.PathKey(name)), out var id) ? id : null;

        public long GetOrAdd(long parentId, string name)
        {
            var key = (parentId, TransientPathIds.PathKey(name));
            if (known.TryGetValue(key, out var id))
            {
                return id;
            }

            if (_insert is null)
            {
                _insert = conn.CreateCommand();
                _insert.CommandText = "INSERT INTO path (parent_id, name) VALUES ($p, $n); SELECT last_insert_rowid();";
                _insert.Parameters.Add("$p", SqliteType.Integer);
                _insert.Parameters.Add("$n", SqliteType.Text);
            }

            _insert.Parameters[0].Value = parentId;
            _insert.Parameters[1].Value = name;
            id = (long)_insert.ExecuteScalar()!;
            known[key] = id;
            return id;
        }
    }

    private static long ToUnixMs(DateTime utc) => new DateTimeOffset(utc.ToUniversalTime()).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}

/// <summary>保留策略的计算部分（单独拆出来便于测试）。</summary>
public static class SnapshotRetention
{
    /// <summary>早于 <paramref name="cutoffUtc"/> 的快照，每个盘每个 ISO 周只留时间最晚的一个，其余返回待删除。</summary>
    public static List<long> ToRemove(IEnumerable<SnapshotInfo> snapshots, DateTime cutoffUtc)
    {
        var remove = new List<long>();
        foreach (var week in snapshots.Where(s => s.TakenAtUtc < cutoffUtc)
                     .GroupBy(s => (Volume: s.Volume.ToUpperInvariant(), Year: ISOWeek.GetYear(s.TakenAtUtc), Week: ISOWeek.GetWeekOfYear(s.TakenAtUtc))))
        {
            var keep = week.MaxBy(s => (s.TakenAtUtc, s.Id))!;
            remove.AddRange(week.Where(s => s.Id != keep.Id).Select(s => s.Id));
        }

        return remove;
    }
}

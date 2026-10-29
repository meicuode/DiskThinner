using System.Globalization;
using DiskThinner.Core;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Monitor;

/// <summary>会话行的内容（开始时插入，每次落盘更新）。</summary>
public sealed class SessionRecord
{
    public long Id { get; set; }
    public required string Name { get; init; }
    public required DateTime StartedUtc { get; init; }
    public required DateTime PlannedEndUtc { get; init; }
    public DateTime? EndedUtc { get; set; }
    public DateTime LastFlushUtc { get; set; }
    public required MonitorMode Mode { get; set; }
    public string? EndReason { get; set; }
    public DateTime? DowngradedUtc { get; set; }
    public MonitorStats Stats { get; set; } = new();
    public long PeakEventsPerSecond { get; set; }
    public int AvgCpuPermille { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public SessionFlags Flags { get; set; }

    public TimeSpan Covered => (EndedUtc ?? LastFlushUtc) - StartedUtc;
}

/// <summary>落盘的一行：目录（null 表示对不上目录）× 进程链。</summary>
public sealed record StoredRow(string? Dir, ProcessChain Chain, Counters Counters);

/// <summary>一个小时的落盘数据（已经合并过）。</summary>
public sealed record HourData(DateTime HourUtc, MonitorMode Mode, MonitorStats Stats, int Flags, IReadOnlyList<StoredRow> Rows);

/// <summary>
/// 写入监控的库（%LOCALAPPDATA%\DiskThinner\monitor.db，docs/phase2a-design.md 5.8 节，D4）。表结构和快照库一样手写 SQL；
/// 每次操作单独开连接（Pooling=false，busy_timeout 5 秒）。
/// </summary>
public sealed class MonitorStore
{
    private const int FormatVersion = 1;
    private static readonly string[] Schema =
    [
        "CREATE TABLE IF NOT EXISTS session (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, started_at INTEGER NOT NULL, planned_end INTEGER NOT NULL, " +
            "ended_at INTEGER, last_flush INTEGER NOT NULL, mode TEXT NOT NULL, end_reason TEXT, downgraded_at INTEGER, " +
            "received INTEGER NOT NULL, lost INTEGER NOT NULL, sampled_out INTEGER NOT NULL, parse_errors INTEGER NOT NULL, filtered INTEGER NOT NULL, " +
            "unresolved_writes INTEGER NOT NULL, unresolved_bytes INTEGER NOT NULL, bad_offsets INTEGER NOT NULL, peak_events_per_sec INTEGER NOT NULL, " +
            "avg_cpu_permille INTEGER NOT NULL, peak_ws_bytes INTEGER NOT NULL, flags INTEGER NOT NULL)",
        "CREATE TABLE IF NOT EXISTS session_hour (session_id INTEGER NOT NULL, hour INTEGER NOT NULL, mode TEXT NOT NULL, received INTEGER NOT NULL, " +
            "lost INTEGER NOT NULL, sampled_out INTEGER NOT NULL, unresolved_bytes INTEGER NOT NULL, write_bytes INTEGER NOT NULL, flags INTEGER NOT NULL, " +
            "PRIMARY KEY (session_id, hour)) WITHOUT ROWID",
        "CREATE TABLE IF NOT EXISTS dir (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, UNIQUE (parent_id, name))",
        "CREATE TABLE IF NOT EXISTS image (id INTEGER PRIMARY KEY, path TEXT NOT NULL, path_key TEXT NOT NULL, services TEXT NOT NULL, " +
            "product TEXT, company TEXT, UNIQUE (path_key, services))",
        "CREATE TABLE IF NOT EXISTS chain (id INTEGER PRIMARY KEY, kind INTEGER NOT NULL, writer_id INTEGER NOT NULL, top_id INTEGER NOT NULL, " +
            "links TEXT NOT NULL, unknown_pid INTEGER NOT NULL, UNIQUE (kind, writer_id, top_id, links, unknown_pid))",
        "CREATE TABLE IF NOT EXISTS write_stat (session_id INTEGER NOT NULL, hour INTEGER NOT NULL, dir_id INTEGER NOT NULL, chain_id INTEGER NOT NULL, " +
            "created_files INTEGER NOT NULL, created_dirs INTEGER NOT NULL, deleted INTEGER NOT NULL, renamed INTEGER NOT NULL, " +
            "write_bytes INTEGER NOT NULL, write_ops INTEGER NOT NULL, growth_bytes INTEGER NOT NULL, estimated INTEGER NOT NULL, " +
            "PRIMARY KEY (session_id, hour, dir_id, chain_id)) WITHOUT ROWID",
        // chain 0：合并时并到一起的"其他进程"
        "INSERT OR IGNORE INTO chain (id, kind, writer_id, top_id, links, unknown_pid) VALUES (0, 3, 0, 0, '', 0)",
    ];

    private MonitorStore(string path) => FilePath = path;

    public string FilePath { get; }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "monitor.db");

    /// <summary>打开（不存在时建库）。</summary>
    public static MonitorStore Open(string path)
    {
        var store = new MonitorStore(Path.GetFullPath(path));
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        using var conn = store.Connect(create: true);
        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > FormatVersion)
        {
            throw new InvalidDataException(L.F("写入监控的数据来自更新的版本（格式 {0}），请升级 DiskThinner", version));
        }

        if (version == 0)
        {
            Exec(conn, "PRAGMA auto_vacuum = INCREMENTAL"); // 建表之前设
        }

        using var tx = conn.BeginTransaction();
        foreach (var sql in Schema)
        {
            Exec(conn, sql);
        }

        Exec(conn, $"PRAGMA user_version = {FormatVersion}");
        tx.Commit();
        return store;
    }

    /// <summary>只读打开：文件不存在时返回 null，不建库（界面在没监控过时用）。</summary>
    public static MonitorStore? OpenExisting(string path) => File.Exists(path) ? Open(path) : null;

    /// <summary>库里实际占用的字节（页数减空闲页，按大小保留时用它而不是文件大小，第 12.2 节第 14 条）。</summary>
    public long UsedBytes
    {
        get
        {
            using var conn = Connect();
            var pages = Convert.ToInt64(Scalar(conn, "PRAGMA page_count"), CultureInfo.InvariantCulture);
            var free = Convert.ToInt64(Scalar(conn, "PRAGMA freelist_count"), CultureInfo.InvariantCulture);
            var size = Convert.ToInt64(Scalar(conn, "PRAGMA page_size"), CultureInfo.InvariantCulture);
            return (pages - free) * size;
        }
    }

    public long FileBytes => File.Exists(FilePath) ? new FileInfo(FilePath).Length : 0;

    // ---------------- 写 ----------------

    /// <summary>会话开始时立即插入一行（计数为 0），刚开始就被强制结束的会话也能在清理时找到。</summary>
    public long BeginSession(SessionRecord s)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO session (name, started_at, planned_end, last_flush, mode, received, lost, sampled_out, parse_errors, filtered, " +
                          "unresolved_writes, unresolved_bytes, bad_offsets, peak_events_per_sec, avg_cpu_permille, peak_ws_bytes, flags) " +
                          "VALUES ($n, $s, $p, $s, $m, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", s.Name);
        cmd.Parameters.AddWithValue("$s", Ms(s.StartedUtc));
        cmd.Parameters.AddWithValue("$p", Ms(s.PlannedEndUtc));
        cmd.Parameters.AddWithValue("$m", ModeText(s.Mode));
        s.Id = (long)cmd.ExecuteScalar()!;
        s.LastFlushUtc = s.StartedUtc;
        return s.Id;
    }

    /// <summary>
    /// 落盘：一个事务里删掉这些小时的旧行、写合并后的行、补上新出现的目录 / 映像 / 进程链，更新会话和小时的统计。
    /// <paramref name="identity"/> 给映像取产品名和公司名（第一次出现时调用，可以为 null）。
    /// </summary>
    public void Flush(SessionRecord s, IReadOnlyList<HourData> hours, Func<string, (string? Product, string? Company)>? identity = null)
    {
        using var conn = Connect();
        using var tx = conn.BeginTransaction();
        var dirs = new DirIds(conn);
        var images = new Dictionary<(string, string), long>();
        var chains = new Dictionary<ProcessChain, long>();
        foreach (var h in hours)
        {
            var hour = Hour(h.HourUtc);
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM write_stat WHERE session_id = $s AND hour = $h";
                del.Parameters.AddWithValue("$s", s.Id);
                del.Parameters.AddWithValue("$h", hour);
                del.ExecuteNonQuery();
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT OR REPLACE INTO write_stat (session_id, hour, dir_id, chain_id, created_files, created_dirs, deleted, renamed, " +
                              "write_bytes, write_ops, growth_bytes, estimated) VALUES ($s, $h, $d, $c, $cf, $cd, $del, $ren, $wb, $wo, $g, $e)";
            var ps = new[] { "$s", "$h", "$d", "$c", "$cf", "$cd", "$del", "$ren", "$wb", "$wo", "$g", "$e" }.Select(n => ins.Parameters.Add(n, SqliteType.Integer)).ToArray();
            ins.Prepare();
            long writeBytes = 0;
            foreach (var row in h.Rows)
            {
                var c = row.Counters;
                writeBytes += c.WriteBytes;
                var values = new object[]
                {
                    s.Id, hour, row.Dir is null ? 0 : dirs.Get(row.Dir), ChainId(conn, row.Chain, chains, images, identity),
                    c.Creates, c.CreatedDirs, c.Deletes, c.Renames, c.WriteBytes, c.WriteOps, c.Growth, c.Sampled ? 1 : 0,
                };
                for (var i = 0; i < ps.Length; i++)
                {
                    ps[i].Value = values[i];
                }

                ins.ExecuteNonQuery();
            }

            using var sh = conn.CreateCommand();
            sh.CommandText = "INSERT OR REPLACE INTO session_hour (session_id, hour, mode, received, lost, sampled_out, unresolved_bytes, write_bytes, flags) " +
                             "VALUES ($s, $h, $m, $r, $l, $so, $u, $wb, $f)";
            sh.Parameters.AddWithValue("$s", s.Id);
            sh.Parameters.AddWithValue("$h", hour);
            sh.Parameters.AddWithValue("$m", ModeText(h.Mode));
            sh.Parameters.AddWithValue("$r", h.Stats.Received);
            sh.Parameters.AddWithValue("$l", h.Stats.Lost);
            sh.Parameters.AddWithValue("$so", h.Stats.SampledOut);
            sh.Parameters.AddWithValue("$u", h.Stats.UnresolvedBytes);
            sh.Parameters.AddWithValue("$wb", writeBytes);
            sh.Parameters.AddWithValue("$f", h.Flags);
            sh.ExecuteNonQuery();
        }

        UpdateSession(conn, s);
        tx.Commit();
    }

    private static void UpdateSession(SqliteConnection conn, SessionRecord s)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE session SET ended_at = $end, last_flush = $lf, mode = $m, end_reason = $er, downgraded_at = $dg, received = $r, lost = $l, " +
                          "sampled_out = $so, parse_errors = $pe, filtered = $fi, unresolved_writes = $uw, unresolved_bytes = $ub, bad_offsets = $bo, " +
                          "peak_events_per_sec = $pk, avg_cpu_permille = $cpu, peak_ws_bytes = $ws, flags = $f WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", s.Id);
        cmd.Parameters.AddWithValue("$end", s.EndedUtc is { } e ? Ms(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$lf", Ms(s.LastFlushUtc));
        cmd.Parameters.AddWithValue("$m", ModeText(s.Mode));
        cmd.Parameters.AddWithValue("$er", (object?)s.EndReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dg", s.DowngradedUtc is { } d ? Ms(d) : DBNull.Value);
        cmd.Parameters.AddWithValue("$r", s.Stats.Received);
        cmd.Parameters.AddWithValue("$l", s.Stats.Lost);
        cmd.Parameters.AddWithValue("$so", s.Stats.SampledOut);
        cmd.Parameters.AddWithValue("$pe", s.Stats.ParseErrors);
        cmd.Parameters.AddWithValue("$fi", s.Stats.Filtered);
        cmd.Parameters.AddWithValue("$uw", s.Stats.UnresolvedWrites);
        cmd.Parameters.AddWithValue("$ub", s.Stats.UnresolvedBytes);
        cmd.Parameters.AddWithValue("$bo", s.Stats.BadOffsets);
        cmd.Parameters.AddWithValue("$pk", s.PeakEventsPerSecond);
        cmd.Parameters.AddWithValue("$cpu", s.AvgCpuPermille);
        cmd.Parameters.AddWithValue("$ws", s.PeakWorkingSetBytes);
        cmd.Parameters.AddWithValue("$f", (int)s.Flags);
        cmd.ExecuteNonQuery();
    }

    private static long ChainId(SqliteConnection conn, ProcessChain chain, Dictionary<ProcessChain, long> cache, Dictionary<(string, string), long> images,
        Func<string, (string?, string?)>? identity)
    {
        if (chain.Kind == ChainKind.Other)
        {
            return 0;
        }

        if (cache.TryGetValue(chain, out var id))
        {
            return id;
        }

        var writer = ImageId(conn, chain.WriterImage, chain.WriterServices, images, identity);
        var top = ImageId(conn, chain.TopImage, chain.TopServices, images, identity);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO chain (kind, writer_id, top_id, links, unknown_pid) VALUES ($k, $w, $t, $l, $u); " +
                          "SELECT id FROM chain WHERE kind = $k AND writer_id = $w AND top_id = $t AND links = $l AND unknown_pid = $u";
        cmd.Parameters.AddWithValue("$k", (int)chain.Kind);
        cmd.Parameters.AddWithValue("$w", writer);
        cmd.Parameters.AddWithValue("$t", top);
        cmd.Parameters.AddWithValue("$l", chain.Links);
        cmd.Parameters.AddWithValue("$u", chain.UnknownPid);
        id = (long)cmd.ExecuteScalar()!;
        cache[chain] = id;
        return id;
    }

    private static long ImageId(SqliteConnection conn, string path, string services, Dictionary<(string, string), long> cache, Func<string, (string?, string?)>? identity)
    {
        if (path.Length == 0)
        {
            return 0;
        }

        var key = (path.ToUpperInvariant(), services);
        if (cache.TryGetValue(key, out var id))
        {
            return id;
        }

        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT id FROM image WHERE path_key = $k AND services = $s";
            find.Parameters.AddWithValue("$k", key.Item1);
            find.Parameters.AddWithValue("$s", services);
            if (find.ExecuteScalar() is long existing)
            {
                cache[key] = existing;
                return existing;
            }
        }

        // 程序身份在落盘线程上读（第 12.2 节第 18 条），程序以后被卸载也能显示
        var (product, company) = identity?.Invoke(path) ?? (null, null);
        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO image (path, path_key, services, product, company) VALUES ($p, $k, $s, $pr, $c); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$p", path);
        ins.Parameters.AddWithValue("$k", key.Item1);
        ins.Parameters.AddWithValue("$s", services);
        ins.Parameters.AddWithValue("$pr", (object?)product ?? DBNull.Value);
        ins.Parameters.AddWithValue("$c", (object?)company ?? DBNull.Value);
        id = (long)ins.ExecuteScalar()!;
        cache[key] = id;
        return id;
    }

    // ---------------- 会话状态 ----------------

    public IReadOnlyList<OpenSessionRow> OpenSessions()
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, last_flush FROM session WHERE ended_at IS NULL";
        using var r = cmd.ExecuteReader();
        var list = new List<OpenSessionRow>();
        while (r.Read())
        {
            list.Add(new OpenSessionRow(r.GetInt64(0), r.GetString(1), FromMs(r.GetInt64(2))));
        }

        return list;
    }

    /// <summary>标"程序意外退出"：只改读到时 last_flush 还没变的行（期间被别的实例更新过就不改，第 12.2 节第 9 条）。</summary>
    public int MarkCrashed(IEnumerable<OpenSessionRow> rows)
    {
        using var conn = Connect();
        var n = 0;
        foreach (var row in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE session SET ended_at = last_flush, end_reason = 'crashed' WHERE id = $id AND ended_at IS NULL AND last_flush = $lf";
            cmd.Parameters.AddWithValue("$id", row.Id);
            cmd.Parameters.AddWithValue("$lf", Ms(row.LastFlushUtc));
            n += cmd.ExecuteNonQuery();
        }

        return n;
    }

    // ---------------- 保留 ----------------

    /// <summary>
    /// 保留（D5）：按开始时间删最早的整个会话，直到"都在 <paramref name="keep"/> 之内"和"实际占用 ≤ <paramref name="maxBytes"/>"两条都满足；
    /// 没人引用的目录、映像、进程链一起删，然后 incremental_vacuum。正在进行的会话不删。返回删掉的会话数。
    /// </summary>
    public int Prune(DateTime nowUtc, TimeSpan keep, long maxBytes)
    {
        var deleted = 0;
        using var conn = Connect();
        var cutoff = Ms(nowUtc - keep);
        while (true)
        {
            long? oldest;
            bool expired;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, started_at FROM session WHERE ended_at IS NOT NULL ORDER BY started_at LIMIT 1";
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    break;
                }

                oldest = r.GetInt64(0);
                expired = r.GetInt64(1) < cutoff;
            }

            if (!expired && Used(conn) <= maxBytes)
            {
                break;
            }

            using (var tx = conn.BeginTransaction())
            {
                foreach (var sql in new[] { "DELETE FROM write_stat WHERE session_id = $id", "DELETE FROM session_hour WHERE session_id = $id", "DELETE FROM session WHERE id = $id" })
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$id", oldest);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }

            deleted++;
            if (deleted % 4 == 1)
            {
                Collect(conn); // 释放的页按实际占用算，删一个会话后要先回收才看得出变小了
            }
        }

        if (deleted > 0)
        {
            Collect(conn);
        }

        return deleted;
    }

    private static void Collect(SqliteConnection conn)
    {
        using (var tx = conn.BeginTransaction())
        {
            Exec(conn, "DELETE FROM chain WHERE id <> 0 AND id NOT IN (SELECT DISTINCT chain_id FROM write_stat)");
            Exec(conn, "DELETE FROM image WHERE id NOT IN (SELECT writer_id FROM chain UNION SELECT top_id FROM chain)");

            // 目录：没有被引用、也没有子节点的行，反复做到没有为止
            while (Convert.ToInt64(Scalar(conn, "SELECT count(*) FROM dir WHERE id NOT IN (SELECT DISTINCT dir_id FROM write_stat) AND id NOT IN (SELECT parent_id FROM dir)"), CultureInfo.InvariantCulture) > 0)
            {
                Exec(conn, "DELETE FROM dir WHERE id NOT IN (SELECT DISTINCT dir_id FROM write_stat) AND id NOT IN (SELECT parent_id FROM dir)");
            }

            tx.Commit();
        }

        Exec(conn, "PRAGMA incremental_vacuum");
    }

    private static long Used(SqliteConnection conn) =>
        (Convert.ToInt64(Scalar(conn, "PRAGMA page_count"), CultureInfo.InvariantCulture) - Convert.ToInt64(Scalar(conn, "PRAGMA freelist_count"), CultureInfo.InvariantCulture))
        * Convert.ToInt64(Scalar(conn, "PRAGMA page_size"), CultureInfo.InvariantCulture);

    // ---------------- 读 ----------------

    /// <summary>和 [fromUtc, toUtc) 有交集的会话（按开始时间）。</summary>
    public IReadOnlyList<SessionRecord> Sessions(DateTime fromUtc, DateTime toUtc)
    {
        using var conn = Connect();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, started_at, planned_end, ended_at, last_flush, mode, end_reason, downgraded_at, received, lost, sampled_out, " +
                          "parse_errors, filtered, unresolved_writes, unresolved_bytes, bad_offsets, peak_events_per_sec, avg_cpu_permille, peak_ws_bytes, flags " +
                          "FROM session WHERE started_at < $to AND coalesce(ended_at, last_flush) >= $from ORDER BY started_at";
        cmd.Parameters.AddWithValue("$from", Ms(fromUtc));
        cmd.Parameters.AddWithValue("$to", Ms(toUtc));
        using var r = cmd.ExecuteReader();
        var list = new List<SessionRecord>();
        while (r.Read())
        {
            list.Add(new SessionRecord
            {
                Id = r.GetInt64(0),
                Name = r.GetString(1),
                StartedUtc = FromMs(r.GetInt64(2)),
                PlannedEndUtc = FromMs(r.GetInt64(3)),
                EndedUtc = r.IsDBNull(4) ? null : FromMs(r.GetInt64(4)),
                LastFlushUtc = FromMs(r.GetInt64(5)),
                Mode = r.GetString(6) == "light" ? MonitorMode.Light : MonitorMode.Precise,
                EndReason = r.IsDBNull(7) ? null : r.GetString(7),
                DowngradedUtc = r.IsDBNull(8) ? null : FromMs(r.GetInt64(8)),
                Stats = new MonitorStats
                {
                    Received = r.GetInt64(9), Lost = r.GetInt64(10), SampledOut = r.GetInt64(11), ParseErrors = r.GetInt64(12), Filtered = r.GetInt64(13),
                    UnresolvedWrites = r.GetInt64(14), UnresolvedBytes = r.GetInt64(15), BadOffsets = r.GetInt64(16),
                },
                PeakEventsPerSecond = r.GetInt64(17),
                AvgCpuPermille = r.GetInt32(18),
                PeakWorkingSetBytes = r.GetInt64(19),
                Flags = (SessionFlags)r.GetInt32(20),
            });
        }

        return list;
    }

    /// <summary>[fromUtc, toUtc) 内的汇总行（小时桶 [h, h+1) 和范围有交集就算），给 <see cref="WriteIndex"/> 用。</summary>
    public WriteIndex LoadIndex(DateTime fromUtc, DateTime toUtc)
    {
        var sessions = Sessions(fromUtc, toUtc);
        using var conn = Connect();
        var dirPaths = LoadDirPaths(conn);
        var images = LoadImages(conn);
        var chains = LoadChains(conn, images);
        var rows = new List<IndexRow>();
        var hours = new List<HourCoverage>();
        var fromHour = Hour(fromUtc);
        var toHour = Hour(toUtc - TimeSpan.FromTicks(1));
        foreach (var s in sessions)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT hour, dir_id, chain_id, created_files, created_dirs, deleted, renamed, write_bytes, write_ops, growth_bytes, estimated " +
                                  "FROM write_stat WHERE session_id = $s AND hour BETWEEN $a AND $b";
                cmd.Parameters.AddWithValue("$s", s.Id);
                cmd.Parameters.AddWithValue("$a", fromHour);
                cmd.Parameters.AddWithValue("$b", toHour);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var dirId = r.GetInt64(1);
                    rows.Add(new IndexRow(s.Id, FromHour(r.GetInt64(0)), dirId == 0 ? null : dirPaths.GetValueOrDefault(dirId),
                        chains.GetValueOrDefault(r.GetInt64(2)) ?? StoredChain.Other,
                        new Counters
                        {
                            Creates = r.GetInt64(3), CreatedDirs = r.GetInt64(4), Deletes = r.GetInt64(5), Renames = r.GetInt64(6),
                            WriteBytes = r.GetInt64(7), WriteOps = r.GetInt64(8), Growth = r.GetInt64(9), Sampled = r.GetInt64(10) != 0,
                        }));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT hour, mode, received, lost, sampled_out, unresolved_bytes, write_bytes, flags FROM session_hour WHERE session_id = $s AND hour BETWEEN $a AND $b";
                cmd.Parameters.AddWithValue("$s", s.Id);
                cmd.Parameters.AddWithValue("$a", fromHour);
                cmd.Parameters.AddWithValue("$b", toHour);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    hours.Add(new HourCoverage(s.Id, FromHour(r.GetInt64(0)), r.GetString(1) == "light" ? MonitorMode.Light : MonitorMode.Precise,
                        r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7)));
                }
            }
        }

        return new WriteIndex(sessions, rows, hours);
    }

    /// <summary>
    /// 常驻服务的查询（docs/phase2b-design.md 5.5 节）：[fromUtc, toUtc) 内的行按（目录, 进程链）合计，跨会话、跨小时。
    /// 每行的会话号取最近的一个会话、小时取最早的一个小时，Sampled 取或。会话和按小时的覆盖照原样给出。30 天的数据也只有几万行，在 SQL 里合计，不把逐小时的行读进内存。
    /// </summary>
    public WriteIndex LoadMerged(DateTime fromUtc, DateTime toUtc)
    {
        var sessions = Sessions(fromUtc, toUtc);
        using var conn = Connect();
        var dirPaths = LoadDirPaths(conn);
        var images = LoadImages(conn);
        var chains = LoadChains(conn, images);
        var fromHour = Hour(fromUtc);
        var toHour = Hour(toUtc - TimeSpan.FromTicks(1));
        var rows = new List<IndexRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT max(session_id), min(hour), dir_id, chain_id, sum(created_files), sum(created_dirs), sum(deleted), sum(renamed), " +
                              "sum(write_bytes), sum(write_ops), sum(growth_bytes), max(estimated) FROM write_stat WHERE hour BETWEEN $a AND $b GROUP BY dir_id, chain_id";
            cmd.Parameters.AddWithValue("$a", fromHour);
            cmd.Parameters.AddWithValue("$b", toHour);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var dirId = r.GetInt64(2);
                rows.Add(new IndexRow(r.GetInt64(0), FromHour(r.GetInt64(1)), dirId == 0 ? null : dirPaths.GetValueOrDefault(dirId),
                    chains.GetValueOrDefault(r.GetInt64(3)) ?? StoredChain.Other,
                    new Counters
                    {
                        Creates = r.GetInt64(4), CreatedDirs = r.GetInt64(5), Deletes = r.GetInt64(6), Renames = r.GetInt64(7),
                        WriteBytes = r.GetInt64(8), WriteOps = r.GetInt64(9), Growth = r.GetInt64(10), Sampled = r.GetInt64(11) != 0,
                    }));
            }
        }

        var hours = new List<HourCoverage>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT session_id, hour, mode, received, lost, sampled_out, unresolved_bytes, write_bytes, flags FROM session_hour WHERE hour BETWEEN $a AND $b";
            cmd.Parameters.AddWithValue("$a", fromHour);
            cmd.Parameters.AddWithValue("$b", toHour);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                hours.Add(new HourCoverage(r.GetInt64(0), FromHour(r.GetInt64(1)), r.GetString(2) == "light" ? MonitorMode.Light : MonitorMode.Precise,
                    r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8)));
            }
        }

        return new WriteIndex(sessions, rows, hours);
    }

    /// <summary>最早的会话的开始时间（服务状态里的"从哪天开始记录"）；没有会话时为 null。</summary>
    public DateTime? FirstSessionStartUtc()
    {
        using var conn = Connect();
        return Scalar(conn, "SELECT min(started_at) FROM session") is long ms ? FromMs(ms) : null;
    }

    private static Dictionary<long, string> LoadDirPaths(SqliteConnection conn)
    {
        var nodes = new Dictionary<long, (long Parent, string Name)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, parent_id, name FROM dir";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                nodes[r.GetInt64(0)] = (r.GetInt64(1), r.GetString(2));
            }
        }

        var paths = new Dictionary<long, string>();
        string PathOf(long id)
        {
            if (paths.TryGetValue(id, out var p))
            {
                return p;
            }

            var (parent, name) = nodes[id];
            p = parent == 0 ? name : Join(PathOf(parent), name);
            paths[id] = p;
            return p;
        }

        foreach (var id in nodes.Keys)
        {
            PathOf(id);
        }

        return paths;
    }

    private static Dictionary<long, StoredImage> LoadImages(SqliteConnection conn)
    {
        var map = new Dictionary<long, StoredImage>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, path, services, product, company FROM image";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetInt64(0)] = new StoredImage(r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4));
        }

        return map;
    }

    private static Dictionary<long, StoredChain> LoadChains(SqliteConnection conn, Dictionary<long, StoredImage> images)
    {
        var map = new Dictionary<long, StoredChain>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, writer_id, top_id, links, unknown_pid FROM chain";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetInt64(0)] = new StoredChain((ChainKind)r.GetInt32(1), images.GetValueOrDefault(r.GetInt64(2)), images.GetValueOrDefault(r.GetInt64(3)), r.GetString(4), r.GetInt32(5));
        }

        return map;
    }

    // ---------------- 工具 ----------------

    private SqliteConnection Connect(bool create = false)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA busy_timeout = 5000");
        return conn;
    }

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

    private static string ModeText(MonitorMode m) => m == MonitorMode.Light ? "light" : "precise";

    private static long Ms(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>UTC 整点的 Unix 秒数 ÷ 3600。</summary>
    internal static long Hour(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() / 3600;

    internal static DateTime FromHour(long hour) => DateTimeOffset.FromUnixTimeSeconds(hour * 3600).UtcDateTime;

    private static string Join(string parent, string name) => parent.EndsWith('\\') ? parent + name : parent + "\\" + name;

    /// <summary>目录按段存（和快照库的 path 表一样），落盘时补上新出现的。</summary>
    private sealed class DirIds(SqliteConnection conn)
    {
        private readonly Dictionary<string, long> _cache = new(StringComparer.OrdinalIgnoreCase);

        public long Get(string path)
        {
            if (_cache.TryGetValue(path, out var id))
            {
                return id;
            }

            var (parentPath, name) = DirectoryTable.Split(path);
            var parent = parentPath is null ? 0 : Get(parentPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO dir (parent_id, name) VALUES ($p, $n); SELECT id FROM dir WHERE parent_id = $p AND name = $n";
            cmd.Parameters.AddWithValue("$p", parent);
            cmd.Parameters.AddWithValue("$n", parentPath is null ? name.TrimEnd('\\') + "\\" : name);
            id = (long)cmd.ExecuteScalar()!;
            _cache[path] = id;
            return id;
        }
    }
}

public sealed record StoredImage(string Path, string Services, string? Product, string? Company)
{
    public string ExeName => System.IO.Path.GetFileName(Path);
}

/// <summary>库里读出来的进程链。</summary>
public sealed record StoredChain(ChainKind Kind, StoredImage? Writer, StoredImage? Top, string Links, int UnknownPid)
{
    public static readonly StoredChain Other = new(ChainKind.Other, null, null, "", 0);
}

public sealed record IndexRow(long SessionId, DateTime HourUtc, string? Dir, StoredChain Chain, Counters Counters);

public sealed record HourCoverage(long SessionId, DateTime HourUtc, MonitorMode Mode, long Received, long Lost, long SampledOut, long UnresolvedBytes, long WriteBytes, int Flags);

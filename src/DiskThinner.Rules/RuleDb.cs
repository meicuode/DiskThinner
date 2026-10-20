using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Rules;

/// <summary>
/// 规则库的 SQLite 分发格式（研发方案 3.3 节）。dtrule 负责写入，客户端只读。
/// 打开时按 SQLite 官方对"来源不可信的数据库"的建议加固：只读 + immutable、关闭 trusted_schema、核对版本和表结构、quick_check。
/// </summary>
public static class RuleDb
{
    public const int FormatVersion = 1;

    private static readonly string[] Schema =
    [
        "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL) WITHOUT ROWID",
        "CREATE TABLE apps (id TEXT PRIMARY KEY, name TEXT NOT NULL, vendor TEXT) WITHOUT ROWID",
        "CREATE TABLE detect (app_id TEXT NOT NULL, type TEXT NOT NULL, value TEXT NOT NULL)",
        "CREATE TABLE locations (ord INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, app_id TEXT, mode TEXT NOT NULL, pattern TEXT NOT NULL, " +
            "kind TEXT NOT NULL, risk TEXT NOT NULL, tier TEXT NOT NULL, label TEXT, regenerates INTEGER NOT NULL, requires_closed INTEGER NOT NULL, " +
            "relocate_env TEXT, relocate_note TEXT)",
        "CREATE TABLE conditions (location_ord INTEGER NOT NULL, ord INTEGER NOT NULL, type TEXT NOT NULL, pattern TEXT NOT NULL)",
        "CREATE TABLE actions (location_ord INTEGER NOT NULL, ord INTEGER NOT NULL, type TEXT NOT NULL, scope TEXT, older_than_days INTEGER, " +
            "keep_latest INTEGER, command_id TEXT, text TEXT, url TEXT)",
        "CREATE TABLE texts (location_ord INTEGER NOT NULL, lang TEXT NOT NULL, purpose TEXT NOT NULL, impact TEXT NOT NULL, PRIMARY KEY (location_ord, lang)) WITHOUT ROWID",
        "CREATE TABLE evidence (location_ord INTEGER NOT NULL, ord INTEGER NOT NULL, url TEXT, note TEXT)",
    ];

    private static readonly string[] Tables = ["actions", "apps", "conditions", "detect", "evidence", "locations", "meta", "texts"];

    /// <summary>写入规则库。先写临时文件再改名，内容按规则顺序插入并 VACUUM，同样的输入得到同样的文件。</summary>
    public static void Write(RuleSet rules, string path)
    {
        var temp = path + ".tmp";
        File.Delete(temp);
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
        {
            conn.Open();
            Exec(conn, "PRAGMA page_size = 4096");
            Exec(conn, "PRAGMA journal_mode = OFF");
            Exec(conn, $"PRAGMA user_version = {FormatVersion}");
            foreach (var sql in Schema)
            {
                Exec(conn, sql);
            }

            using (var tx = conn.BeginTransaction())
            {
                Insert(conn, "INSERT INTO meta VALUES ($k, $v)", ("$k", "version"), ("$v", rules.Version));
                Insert(conn, "INSERT INTO meta VALUES ($k, $v)", ("$k", "schema"), ("$v", RuleSet.SchemaVersion.ToString(CultureInfo.InvariantCulture)));
                Insert(conn, "INSERT INTO meta VALUES ($k, $v)", ("$k", "locations"), ("$v", rules.Locations.Count.ToString(CultureInfo.InvariantCulture)));

                foreach (var app in rules.Apps.OrderBy(a => a.Id, StringComparer.Ordinal))
                {
                    Insert(conn, "INSERT INTO apps VALUES ($id, $name, $vendor)", ("$id", app.Id), ("$name", app.Name), ("$vendor", app.Vendor));
                    foreach (var exe in app.DetectExes)
                    {
                        Insert(conn, "INSERT INTO detect VALUES ($a, 'exe', $v)", ("$a", app.Id), ("$v", exe));
                    }

                    foreach (var p in app.Processes)
                    {
                        Insert(conn, "INSERT INTO detect VALUES ($a, 'process', $v)", ("$a", app.Id), ("$v", p));
                    }
                }

                for (var i = 0; i < rules.Locations.Count; i++)
                {
                    var r = rules.Locations[i];
                    Insert(conn, "INSERT INTO locations VALUES ($ord, $id, $app, $mode, $pattern, $kind, $risk, $tier, $label, $regen, $closed, $env, $note)",
                        ("$ord", i), ("$id", r.Id), ("$app", r.AppId), ("$mode", Names.Code(r.Mode)), ("$pattern", r.Pattern),
                        ("$kind", Names.Code(r.Kind)), ("$risk", Names.Code(r.Risk)), ("$tier", Names.Code(r.Tier)), ("$label", r.Label),
                        ("$regen", r.Regenerates ? 1 : 0), ("$closed", r.RequiresClosed ? 1 : 0), ("$env", r.Relocate?.Env), ("$note", r.Relocate?.Note));
                    Insert(conn, "INSERT INTO texts VALUES ($ord, 'zh', $p, $i)", ("$ord", i), ("$p", r.Purpose), ("$i", r.Impact));

                    var k = 0;
                    foreach (var c in r.Conditions)
                    {
                        foreach (var p in c.Patterns)
                        {
                            Insert(conn, "INSERT INTO conditions VALUES ($ord, $k, $t, $p)", ("$ord", i), ("$k", k++), ("$t", ConditionCode(c.Type)), ("$p", p));
                        }
                    }

                    for (var a = 0; a < r.Actions.Count; a++)
                    {
                        object?[] v = r.Actions[a] switch
                        {
                            DeleteAction d => ["delete", ScopeCode(d.Scope), d.OlderThanDays, d.KeepLatest, null, null, null],
                            CommandAction c => ["command", null, null, null, c.CommandId, null, null],
                            GuideAction g => ["guide", null, null, null, null, g.Text, g.Url],
                            _ => throw new InvalidOperationException("未知的动作类型"),
                        };
                        Insert(conn, "INSERT INTO actions VALUES ($ord, $a, $t, $s, $o, $k, $c, $x, $u)",
                            ("$ord", i), ("$a", a), ("$t", v[0]), ("$s", v[1]), ("$o", v[2]), ("$k", v[3]), ("$c", v[4]), ("$x", v[5]), ("$u", v[6]));
                    }

                    for (var e = 0; e < r.Evidence.Count; e++)
                    {
                        Insert(conn, "INSERT INTO evidence VALUES ($ord, $e, $u, $n)", ("$ord", i), ("$e", e), ("$u", r.Evidence[e].Url), ("$n", r.Evidence[e].Note));
                    }
                }

                tx.Commit();
            }

            Exec(conn, "VACUUM");
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>以只读、不可修改的方式打开并加固，读入全部规则。格式或结构不对时抛出 <see cref="InvalidDataException"/>。</summary>
    public static RuleSet Read(string path)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        Exec(conn, "PRAGMA trusted_schema = OFF");

        if (Scalar(conn, "PRAGMA user_version") is not long version || version != FormatVersion)
        {
            throw new InvalidDataException($"规则库格式版本不符（需要 {FormatVersion}）");
        }

        var objects = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT type || ':' || name FROM sqlite_master WHERE name NOT LIKE 'sqlite_autoindex_%' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                objects.Add(reader.GetString(0));
            }
        }

        if (!objects.SequenceEqual(Tables.Select(t => "table:" + t)))
        {
            throw new InvalidDataException("规则库的表结构不符，拒绝加载：" + string.Join(", ", objects));
        }

        if (Scalar(conn, "PRAGMA quick_check") as string != "ok")
        {
            throw new InvalidDataException("规则库文件损坏");
        }

        var meta = Query(conn, "SELECT key, value FROM meta", r => (r.GetString(0), r.GetString(1))).ToDictionary(x => x.Item1, x => x.Item2);
        var detect = Query(conn, "SELECT app_id, type, value FROM detect ORDER BY rowid", r => (App: r.GetString(0), Type: r.GetString(1), Value: r.GetString(2)))
            .ToLookup(x => x.App);
        var apps = Query(conn, "SELECT id, name, vendor FROM apps", r => new AppInfo(
            r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
            detect[r.GetString(0)].Where(d => d.Type == "exe").Select(d => d.Value).ToList(),
            detect[r.GetString(0)].Where(d => d.Type == "process").Select(d => d.Value).ToList())).ToList();

        var texts = Query(conn, "SELECT location_ord, purpose, impact FROM texts WHERE lang = 'zh'", r => (Ord: r.GetInt64(0), Purpose: r.GetString(1), Impact: r.GetString(2)))
            .ToDictionary(x => x.Ord);
        var conditions = Query(conn, "SELECT location_ord, type, pattern FROM conditions ORDER BY location_ord, ord", r => (Ord: r.GetInt64(0), Type: r.GetString(1), Pattern: r.GetString(2)))
            .ToLookup(x => x.Ord);
        var actions = Query(conn, "SELECT location_ord, type, scope, older_than_days, keep_latest, command_id, text, url FROM actions ORDER BY location_ord, ord",
            r => (Ord: r.GetInt64(0), Action: ReadAction(r))).ToLookup(x => x.Ord);
        var evidence = Query(conn, "SELECT location_ord, url, note FROM evidence ORDER BY location_ord, ord",
            r => (Ord: r.GetInt64(0), Item: new Evidence(r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)))).ToLookup(x => x.Ord);

        var locations = Query(conn, "SELECT ord, id, app_id, mode, pattern, kind, risk, tier, label, regenerates, requires_closed, relocate_env, relocate_note FROM locations ORDER BY ord", r =>
        {
            var ord = r.GetInt64(0);
            string? Opt(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            if (!Names.TryParse(r.GetString(3), out MatchMode mode) || !Names.TryParse(r.GetString(5), out Kind kind) ||
                !Names.TryParse(r.GetString(6), out Risk risk) || !Names.TryParse(r.GetString(7), out Tier tier))
            {
                // 不认识的取值按最保守处理：不能删
                mode = MatchMode.Dir;
                kind = Kind.System;
                risk = Risk.Never;
                tier = Tier.Draft;
            }

            var env = Opt(11);
            var note = Opt(12);
            return new LocationRule
            {
                Id = r.GetString(1),
                AppId = Opt(2),
                Mode = mode,
                Pattern = r.GetString(4),
                Kind = kind,
                Risk = risk,
                Tier = tier,
                Label = Opt(8),
                Regenerates = r.GetInt64(9) != 0,
                RequiresClosed = r.GetInt64(10) != 0,
                Relocate = env is null && note is null ? null : new Relocation(env, note),
                Purpose = texts.TryGetValue(ord, out var t) ? t.Purpose : "",
                Impact = texts.TryGetValue(ord, out var t2) ? t2.Impact : "",
                Conditions = conditions[ord].GroupBy(c => c.Type)
                    .Select(g => new Condition(ParseCondition(g.Key), g.Select(c => c.Pattern).ToList())).ToList(),
                Actions = actions[ord].Select(a => a.Action).OfType<RuleAction>().ToList(),
                Evidence = evidence[ord].Select(e => e.Item).ToList(),
            };
        }).ToList();

        return new RuleSet(apps, locations) { Version = meta.GetValueOrDefault("version", "") };
    }

    private static RuleAction? ReadAction(SqliteDataReader r)
    {
        string? Opt(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        int? OptInt(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        return r.GetString(1) switch
        {
            "delete" => new DeleteAction(Opt(2) switch { "children" => DeleteScope.Children, "self" => DeleteScope.Self, _ => DeleteScope.Contents }, OptInt(3), OptInt(4)),
            "command" when Opt(5) is { } id && KnownCommands.Find(id) is not null => new CommandAction(id),
            "guide" => new GuideAction(Opt(6) ?? "", Opt(7)),
            _ => null, // 不认识的动作类型忽略
        };
    }

    public static string ConditionCode(ConditionType t) => t switch
    {
        ConditionType.HasAny => "has_any",
        ConditionType.SiblingAny => "sibling_any",
        _ => "not_under",
    };

    private static ConditionType ParseCondition(string code) => code switch
    {
        "has_any" => ConditionType.HasAny,
        "sibling_any" => ConditionType.SiblingAny,
        _ => ConditionType.NotUnder,
    };

    public static string ScopeCode(DeleteScope s) => s switch
    {
        DeleteScope.Children => "children",
        DeleteScope.Self => "self",
        _ => "contents",
    };

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

    private static void Insert(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        cmd.ExecuteNonQuery();
    }

    private static List<T> Query<T>(SqliteConnection conn, string sql, Func<SqliteDataReader, T> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
        {
            list.Add(map(reader));
        }

        return list;
    }
}

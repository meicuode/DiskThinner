using System.Globalization;
using System.Text;
using DiskThinner.Core;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Storage;

/// <summary>操作记录的类型。</summary>
public enum AuditAction
{
    /// <summary>删除文件或目录。</summary>
    Delete,

    /// <summary>运行官方清理命令。</summary>
    Command,

    /// <summary>用户打开所在位置自己处理，再重新测量。</summary>
    Manual,

    /// <summary>保护名单、复核或账户检查拦下了，没有执行。</summary>
    Blocked,
}

/// <summary>一项操作的结果。</summary>
public enum AuditResult
{
    Succeeded,

    /// <summary>做了一部分（例如有文件被占用而跳过）。</summary>
    Partial,

    Failed,

    /// <summary>没有执行（被拦截、被取消）。</summary>
    NotRun,
}

/// <summary>操作记录里的一条。</summary>
public sealed record AuditEntry
{
    public long Id { get; init; }

    public required DateTime AtUtc { get; init; }

    /// <summary>同一次清理的各项共用一个编号。</summary>
    public string? RunId { get; init; }

    public required AuditAction Action { get; init; }

    /// <summary>路径，或运行的命令。</summary>
    public required string Target { get; init; }

    /// <summary>界面上的名称（"当前用户的临时文件"）。</summary>
    public string? Title { get; init; }

    /// <summary>计划时的大小。</summary>
    public long Bytes { get; init; }

    /// <summary>实际释放的空间（执行前后可用空间之差）；不知道时为 null。</summary>
    public long? FreedBytes { get; init; }

    /// <summary>依据："规则 sys.temp"、"保护名单：凭据文件"、"用户自行处理"。</summary>
    public required string Basis { get; init; }

    public required AuditResult Result { get; init; }

    /// <summary>补充说明：跳过的文件、占用的程序、命令输出的摘要、错误信息。</summary>
    public string? Detail { get; init; }

    public int Files { get; init; }

    public int Errors { get; init; }
}

/// <summary>
/// 操作记录（研发方案 3.6 节）：写在 user.db 的 audit_log 表里，只追加。
/// 表上的触发器拒绝 UPDATE 和 DELETE，所以程序里的任何代码（包括以后的版本）都改不了已有的记录。
/// </summary>
public sealed class AuditLog
{
    private const int FormatVersion = 1;

    private static readonly string[] Schema =
    [
        "CREATE TABLE IF NOT EXISTS audit_log (id INTEGER PRIMARY KEY, at INTEGER NOT NULL, run_id TEXT, action TEXT NOT NULL, " +
            "target TEXT NOT NULL, title TEXT, bytes INTEGER NOT NULL, freed INTEGER, basis TEXT NOT NULL, result TEXT NOT NULL, " +
            "detail TEXT, files INTEGER NOT NULL, errors INTEGER NOT NULL)",
        "CREATE INDEX IF NOT EXISTS audit_at ON audit_log (at)",
        "CREATE TRIGGER IF NOT EXISTS audit_no_update BEFORE UPDATE ON audit_log BEGIN SELECT RAISE(ABORT, 'append-only'); END",
        "CREATE TRIGGER IF NOT EXISTS audit_no_delete BEFORE DELETE ON audit_log BEGIN SELECT RAISE(ABORT, 'append-only'); END",
    ];

    public AuditLog(string path)
    {
        FilePath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version > FormatVersion)
            {
                throw new InvalidDataException(L.F("user.db 来自更新的版本（格式 {0}），请升级 DiskThinner", version));
            }
        }

        using var tx = conn.BeginTransaction();
        foreach (var sql in Schema)
        {
            Exec(conn, sql);
        }

        Exec(conn, $"PRAGMA user_version = {FormatVersion}");
        tx.Commit();
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "user.db");

    public string FilePath { get; }

    /// <summary>追加一条记录，返回它的编号。</summary>
    public long Append(AuditEntry entry)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO audit_log (at, run_id, action, target, title, bytes, freed, basis, result, detail, files, errors) " +
            "VALUES ($at, $run, $action, $target, $title, $bytes, $freed, $basis, $result, $detail, $files, $errors); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$at", new DateTimeOffset(entry.AtUtc.ToUniversalTime()).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$run", (object?)entry.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$action", Code(entry.Action));
        cmd.Parameters.AddWithValue("$target", entry.Target);
        cmd.Parameters.AddWithValue("$title", (object?)entry.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bytes", entry.Bytes);
        cmd.Parameters.AddWithValue("$freed", (object?)entry.FreedBytes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$basis", entry.Basis);
        cmd.Parameters.AddWithValue("$result", Code(entry.Result));
        cmd.Parameters.AddWithValue("$detail", (object?)entry.Detail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$files", entry.Files);
        cmd.Parameters.AddWithValue("$errors", entry.Errors);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>全部记录，最新的在前。</summary>
    public List<AuditEntry> List(AuditAction? action = null)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, at, run_id, action, target, title, bytes, freed, basis, result, detail, files, errors FROM audit_log " +
            (action is null ? "" : "WHERE action = $action ") + "ORDER BY at DESC, id DESC";
        if (action is { } a)
        {
            cmd.Parameters.AddWithValue("$action", Code(a));
        }

        var list = new List<AuditEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AuditEntry
            {
                Id = r.GetInt64(0),
                AtUtc = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).UtcDateTime,
                RunId = r.IsDBNull(2) ? null : r.GetString(2),
                Action = ParseAction(r.GetString(3)),
                Target = r.GetString(4),
                Title = r.IsDBNull(5) ? null : r.GetString(5),
                Bytes = r.GetInt64(6),
                FreedBytes = r.IsDBNull(7) ? null : r.GetInt64(7),
                Basis = r.GetString(8),
                Result = ParseResult(r.GetString(9)),
                Detail = r.IsDBNull(10) ? null : r.GetString(10),
                Files = r.GetInt32(11),
                Errors = r.GetInt32(12),
            });
        }

        return list;
    }

    /// <summary>导出成 CSV（UTF-8 带 BOM，Excel 直接打开不乱码）。时间按本地时间写。</summary>
    public static void ExportCsv(IEnumerable<AuditEntry> entries, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T("时间,操作,对象,名称,大小（字节）,实际释放（字节）,依据,结果,说明,文件数,错误数"));
        foreach (var e in entries)
        {
            sb.AppendJoin(',',
                Csv(e.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                Csv(Display(e.Action)),
                Csv(e.Target),
                Csv(e.Title),
                e.Bytes.ToString(CultureInfo.InvariantCulture),
                e.FreedBytes?.ToString(CultureInfo.InvariantCulture) ?? "",
                Csv(e.Basis),
                Csv(Display(e.Result)),
                Csv(e.Detail),
                e.Files.ToString(CultureInfo.InvariantCulture),
                e.Errors.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    /// <summary>
    /// CSV 字段：有逗号、引号、换行时加引号；以 = + - @ 开头的加单引号，防止 Excel 当成公式执行（CSV 注入）。
    /// </summary>
    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    public static string Display(AuditAction action) => action switch
    {
        AuditAction.Delete => L.T("删除"),
        AuditAction.Command => L.T("运行命令"),
        AuditAction.Manual => L.T("手动清理"),
        _ => L.T("拦截"),
    };

    public static string Display(AuditResult result) => result switch
    {
        AuditResult.Succeeded => L.T("成功"),
        AuditResult.Partial => L.T("部分成功"),
        AuditResult.Failed => L.T("失败"),
        _ => L.T("未执行"),
    };

    private static string Code(AuditAction action) => action switch
    {
        AuditAction.Delete => "delete",
        AuditAction.Command => "command",
        AuditAction.Manual => "manual",
        _ => "blocked",
    };

    private static AuditAction ParseAction(string code) => code switch
    {
        "delete" => AuditAction.Delete,
        "command" => AuditAction.Command,
        "manual" => AuditAction.Manual,
        _ => AuditAction.Blocked,
    };

    private static string Code(AuditResult result) => result switch
    {
        AuditResult.Succeeded => "ok",
        AuditResult.Partial => "partial",
        AuditResult.Failed => "failed",
        _ => "not_run",
    };

    private static AuditResult ParseResult(string code) => code switch
    {
        "ok" => AuditResult.Succeeded,
        "partial" => AuditResult.Partial,
        "failed" => AuditResult.Failed,
        _ => AuditResult.NotRun,
    };

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
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

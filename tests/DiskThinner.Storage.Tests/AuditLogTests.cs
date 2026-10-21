using Microsoft.Data.Sqlite;

namespace DiskThinner.Storage.Tests;

/// <summary>操作记录只追加（研发方案 M4 验收：audit_log 无法修改和删除）。</summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-audit-" + Guid.NewGuid().ToString("N"));

    public AuditLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private AuditLog Log() => new(Path.Combine(_dir, "user.db"));

    private static AuditEntry Entry(DateTime at, AuditAction action = AuditAction.Delete, string target = @"C:\Users\demo\AppData\Local\Temp") => new()
    {
        AtUtc = at,
        RunId = "run-1",
        Action = action,
        Target = target,
        Title = "当前用户的临时文件",
        Bytes = 4_000_000_000,
        FreedBytes = 3_900_000_000,
        Basis = "规则 sys.temp",
        Result = AuditResult.Succeeded,
        Detail = "删除 12 个文件",
        Files = 12,
    };

    [Fact]
    public void AppendsAndListsNewestFirst()
    {
        var log = Log();
        var t = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        log.Append(Entry(t));
        log.Append(Entry(t.AddMinutes(1), AuditAction.Command, "uv cache clean"));
        log.Append(Entry(t.AddMinutes(2), AuditAction.Blocked, @"C:\Users\demo\.codex\auth.json") with { Result = AuditResult.NotRun, FreedBytes = null });

        var all = log.List();
        Assert.Equal([AuditAction.Blocked, AuditAction.Command, AuditAction.Delete], all.Select(e => e.Action));
        Assert.Null(all[0].FreedBytes);
        Assert.Equal(3_900_000_000, all[2].FreedBytes);
        Assert.Equal(t, all[2].AtUtc);
        Assert.Single(log.List(AuditAction.Command));
    }

    [Fact]
    public void RecordsCannotBeChangedOrDeleted()
    {
        var log = Log();
        log.Append(Entry(DateTime.UtcNow));

        using var conn = new SqliteConnection($"Data Source={log.FilePath};Pooling=False");
        conn.Open();
        foreach (var sql in new[] { "UPDATE audit_log SET freed = 0", "DELETE FROM audit_log", "UPDATE audit_log SET result = 'ok' WHERE id = 1" })
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
            Assert.Contains("append-only", ex.Message);
        }

        Assert.Single(log.List());
        Assert.Equal(3_900_000_000, log.List()[0].FreedBytes);
    }

    [Fact]
    public void ReopeningKeepsTheTriggers()
    {
        Log().Append(Entry(DateTime.UtcNow));
        var again = Log();
        again.Append(Entry(DateTime.UtcNow));
        using var conn = new SqliteConnection($"Data Source={again.FilePath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'trigger'";
        Assert.Equal(2L, cmd.ExecuteScalar());
        Assert.Equal(2, again.List().Count);
    }

    [Fact]
    public void CsvQuotesFieldsAndDefusesFormulas()
    {
        Assert.Equal("plain", AuditLog.Csv("plain"));
        Assert.Equal("\"a,b\"", AuditLog.Csv("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", AuditLog.Csv("say \"hi\""));
        Assert.Equal("'=HYPERLINK(1)", AuditLog.Csv("=HYPERLINK(1)"));
        Assert.Equal("\"'-1,2\"", AuditLog.Csv("-1,2"));

        var path = Path.Combine(_dir, "log.csv");
        AuditLog.ExportCsv([Entry(DateTime.UtcNow) with { Detail = "第一行\n第二行" }], path);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = File.ReadAllText(path);
        Assert.StartsWith("时间,操作,对象", text);
        Assert.Contains("\"第一行\n第二行\"", text);
    }
}

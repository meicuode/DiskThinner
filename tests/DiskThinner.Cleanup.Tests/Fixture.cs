using System.Diagnostics;
using DiskThinner.Rules;
using DiskThinner.Scanner;
using DiskThinner.Scanner.Walk;
using DiskThinner.Storage;
using Microsoft.Data.Sqlite;

namespace DiskThinner.Cleanup.Tests;

/// <summary>
/// 删除类测试的测试盘（研发方案第 6 节）：所有被删的文件都由测试自己在这里建立，junction 和硬链接的目标也都在这里面。
///
/// 设置了环境变量 DT_CLEAN_TEST_ROOT 时（管理员验收 tools/verify/m4-elevated.ps1 挂上的 VHDX），
/// 它必须在卷标为 DTTEST 的盘上，否则直接报错，不会在别的盘上删任何东西；这时还会核对可用空间的实际变化。
/// 没有设置时用 %TEMP% 下新建的目录；执行器另外用 RequiredRoot 限定只能删这个目录里面的东西。
/// </summary>
internal sealed class Fixture : IDisposable
{
    public const string TestVolumeLabel = "DTTEST";

    public Fixture()
    {
        var volume = Environment.GetEnvironmentVariable("DT_CLEAN_TEST_ROOT");
        if (!string.IsNullOrEmpty(volume))
        {
            var label = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(volume))!).VolumeLabel;
            if (!string.Equals(label, TestVolumeLabel, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"DT_CLEAN_TEST_ROOT 必须在卷标为 {TestVolumeLabel} 的测试盘上：{volume}（卷标是 \"{label}\"）");
            }

            Root = Path.Combine(Path.GetFullPath(volume), "dt-" + Guid.NewGuid().ToString("N")[..8]);
            OnTestVolume = true;
        }
        else
        {
            Root = Path.Combine(Path.GetTempPath(), "dt-clean-" + Guid.NewGuid().ToString("N")[..8]);
        }

        Directory.CreateDirectory(Root);
        Db = Path.Combine(Path.GetTempPath(), "dt-audit-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        SharedAudit = Environment.GetEnvironmentVariable("DT_CLEAN_TEST_AUDIT");
    }

    /// <summary>
    /// 设置了 DT_CLEAN_TEST_AUDIT 时，每个测试结束后把自己的操作记录追加到这个库里（管理员验收用它给操作记录页截图）。
    /// 每个测试仍然用自己的库做断言。
    /// </summary>
    private string? SharedAudit { get; }

    public string Root { get; }

    /// <summary>在 DTTEST 测试盘上（可以核对可用空间的变化）。</summary>
    public bool OnTestVolume { get; }

    public string Db { get; }

    public string Profile => Path.Combine(Root, "Users", "demo");

    public string Local => Path.Combine(Profile, "AppData", "Local");

    /// <summary>测试用的系统布局：系统盘就是测试目录。</summary>
    public PathContext Context => new(Root, Root + @"\Users", Root + @"\Windows", Root + @"\ProgramData", Root + @"\Program Files", Root + @"\Program Files (x86)");

    public string File(string path, long bytes, DateTime? lastWriteUtc = null, FileAttributes? attributes = null)
    {
        var full = Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write))
        {
            // 写入真实数据（不是稀疏文件），占用和大小一致
            var block = new byte[64 * 1024];
            Random.Shared.NextBytes(block);
            for (long left = bytes; left > 0; left -= block.Length)
            {
                fs.Write(block, 0, (int)Math.Min(block.Length, left));
            }
        }

        if (lastWriteUtc is { } t)
        {
            System.IO.File.SetLastWriteTimeUtc(full, t);
        }

        if (attributes is { } a)
        {
            System.IO.File.SetAttributes(full, a);
        }

        return full;
    }

    public string Dir(string path, DateTime? lastWriteUtc = null)
    {
        var full = Path.IsPathFullyQualified(path) ? path : Path.Combine(Root, path);
        Directory.CreateDirectory(full);
        if (lastWriteUtc is { } t)
        {
            Directory.SetLastWriteTimeUtc(full, t);
        }

        return full;
    }

    public static void Junction(string link, string target) => Cmd("mklink", "/J", link, target);

    public static void HardLink(string link, string existing) => Cmd("mklink", "/H", link, existing);

    private static void Cmd(params string[] args)
    {
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("/c");
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{string.Join(' ', args)} 失败：{output}");
        }
    }

    public static LocationRule Rule(string id, string pattern, Risk risk, params RuleAction[] actions) => new()
    {
        Id = id,
        Mode = MatchMode.Dir,
        Pattern = pattern,
        Kind = risk == Risk.Low ? Kind.PackageCache : Kind.Cache,
        Risk = risk,
        Tier = Tier.Reviewed,
        Purpose = id + " 的测试数据",
        Impact = "测试",
        Actions = actions,
    };

    /// <summary>扫描测试目录、匹配规则、生成计划。</summary>
    public Env Plan(LocationRule[] rules, PlanOptions? options = null, IEnumerable<ProtectionEntry>? extraProtection = null)
    {
        var engine = new RuleEngine(new RuleSet([], rules), Context, extraProtection);
        var tree = WalkScanner.Scan(Root).Tree;
        var c = engine.Classify(tree);
        var plan = PlanBuilder.Build(tree, c, (options ?? new PlanOptions()) with { CurrentProfile = Profile, MinBytes = 1 });
        return new Env(this, engine, tree, c, plan);
    }

    public void Dispose()
    {
        if (SharedAudit is not null && System.IO.File.Exists(Db))
        {
            var shared = new AuditLog(SharedAudit);
            foreach (var e in new AuditLog(Db).List().AsEnumerable().Reverse())
            {
                shared.Append(e with { Id = 0 });
            }
        }

        SqliteConnection.ClearAllPools();
        TryDelete(Db);
        if (!Directory.Exists(Root))
        {
            return;
        }

        try
        {
            RemoveTree(Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 测试里故意占用的文件可能还没释放，留在临时目录里
        }
    }

    /// <summary>
    /// 清掉测试自己建的目录。junction 先单独删掉（只删链接本身，不进入目标）：
    /// Directory.Delete 递归删除时，目标先被删掉的 junction 会报"拒绝访问"。
    /// </summary>
    private static void RemoveTree(string dir)
    {
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if ((System.IO.File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(sub, recursive: false);
            }
            else
            {
                RemoveTree(sub);
            }
        }

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            System.IO.File.SetAttributes(file, FileAttributes.Normal);
            System.IO.File.Delete(file);
        }

        System.IO.File.SetAttributes(dir, FileAttributes.Directory);
        Directory.Delete(dir, recursive: false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>一次扫描和计划的结果，以及执行它的方法。</summary>
internal sealed record Env(Fixture Fixture, RuleEngine Engine, NodeTree Tree, Classification C, CleanupPlan Plan)
{
    public PlanItem Item(string path) => Plan.Items.Single(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase) && i.Group != PlanGroup.Blocked);

    public AuditLog Audit { get; } = new(Fixture.Db);

    public CleanupRun Run(params PlanItem[] only) => Run(new ExecutorOptions(), only);

    public CleanupRun Run(ExecutorOptions options, params PlanItem[] only)
    {
        var items = only.Length > 0 ? only : Plan.Items.ToArray();
        var executor = new CleanupExecutor(Engine, Audit, options with { RequiredRoot = Fixture.Root });
        return executor.Execute(items, null, CancellationToken.None);
    }
}

// 阶段 0.2：MFT 扫描与目录遍历扫描的对比验证。
//
//   ScanBench walk <路径> --out <目录>     普通权限即可
//   ScanBench mft  <盘符> --out <目录>     需要管理员
//   ScanBench compare --out <目录>         对比上面两次的输出，生成 compare.md
//   ScanBench all  <盘符> --out <目录>     依次在子进程里跑 walk 和 mft（各自统计峰值内存），再对比
//
// 每种模式输出 <mode>-summary.txt（键值对）和 <mode>-dirs.tsv（深度不超过 3 的目录）。

using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using DiskThinner.Core;
using DiskThinner.Scanner;
using DiskThinner.Scanner.Mft;
using DiskThinner.Scanner.Walk;

var inv = CultureInfo.InvariantCulture;
if (args.Length < 1)
{
    Console.WriteLine("用法: ScanBench walk|mft|compare|all [路径或盘符] --out <目录>");
    return 2;
}

var command = args[0];
var outDir = GetOption("--out") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "scanbench");
Directory.CreateDirectory(outDir);

try
{
    switch (command)
    {
        case "walk":
            RunWalk(args[1]);
            return 0;
        case "mft":
            RunMft(args[1]);
            return 0;
        case "compare":
            Compare();
            return 0;
        case "all":
            return RunAll(args[1]);
        default:
            Console.WriteLine($"未知命令 {command}");
            return 2;
    }
}
catch (Exception ex)
{
    File.WriteAllText(Path.Combine(outDir, $"{command}-error.txt"), ex.ToString());
    Console.Error.WriteLine(ex);
    return 1;
}

string? GetOption(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

void RunWalk(string root)
{
    var threads = GetOption("--threads") is { } t ? int.Parse(t, inv) : (int?)null;
    var progress = new Progress<ScanProgress>(p => Console.Write($"\r已列出 {p.Done:N0} 个目录"));
    var result = WalkScanner.Scan(root, threads, progress);
    Console.WriteLine();
    Report("walk", result, allLinks: null);
}

void RunMft(string drive)
{
    var result = MftScanner.Scan(drive);

    // 再按"每个硬链接名都计大小"汇总一次，用于和遍历扫描对比
    var sw = Stopwatch.StartNew();
    result.Tree.Finalize(countHardLinksOnce: false);
    var allLinks = Dump(result.Tree);
    var refinalize = sw.Elapsed;
    result.Tree.Finalize(countHardLinksOnce: true);
    result.Tree.PropagateFlagToDescendants(NodeFlags.Metadata);
    Report("mft", result, allLinks, refinalize);
}

void Report(string mode, ScanResult result, List<string>? allLinks, TimeSpan? refinalize = null)
{
    var (tree, stats) = result;

    using var proc = Process.GetCurrentProcess();
    proc.Refresh();
    var workingSetBeforeTrim = proc.WorkingSet64;

    // 扫描是一次性的重活：结束后做一次激进回收，把空闲的 GC 内存还给系统，得到常驻内存的真实水平
    GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    proc.Refresh();

    var root = tree[tree.Root];
    var lines = new List<string>
    {
        $"mode={mode}",
        $"root={stats.Root}",
        $"elevated={IsElevated()}",
        $"machine_cpus={Environment.ProcessorCount}",
        $"read_ms={stats.ReadDuration.TotalMilliseconds:F0}",
        $"build_ms={stats.BuildDuration.TotalMilliseconds:F0}",
        $"finalize_ms={stats.FinalizeDuration.TotalMilliseconds:F0}",
        $"total_ms={stats.TotalDuration.TotalMilliseconds:F0}",
        $"nodes={stats.Nodes}",
        $"unreachable={stats.Unreachable}",
        $"files={root.FileCount}",
        $"dirs={root.DirCount}",
        $"logical_bytes={root.LogicalSize}",
        $"alloc_bytes={root.AllocSize}",
        $"volume_total_bytes={stats.VolumeTotalBytes}",
        $"volume_free_bytes={stats.VolumeFreeBytes}",
        $"volume_used_bytes={stats.VolumeTotalBytes - stats.VolumeFreeBytes}",
        $"cluster_size={stats.ClusterSize}",
        $"managed_heap_bytes={GC.GetTotalMemory(false)}",
        $"working_set_before_trim_bytes={workingSetBeforeTrim}",
        $"working_set_bytes={proc.WorkingSet64}",
        $"peak_working_set_bytes={proc.PeakWorkingSet64}",
        $"private_bytes={proc.PrivateMemorySize64}",
        $"tree_bytes={tree.ApproximateBytes}",
    };

    if (mode == "mft")
    {
        lines.AddRange(
        [
            $"mft_records={stats.MftRecords}",
            $"mft_records_in_use={stats.MftRecordsInUse}",
            $"mft_bad_records={stats.MftBadRecords}",
            $"mft_malformed_runs={stats.MftMalformedRuns}",
            $"mft_bytes_read={stats.MftBytesRead}",
            $"mft_record_size={stats.MftRecordSize}",
            $"mft_data_segments={stats.MftDataSegments}",
            $"hardlink_secondary_names={stats.HardLinkSecondaryNames}",
            $"ads_bytes={stats.AlternateStreamBytes}",
            $"dir_index_bytes={stats.DirectoryIndexBytes}",
            $"metadata_alloc_bytes={SumRootChildren(tree, NodeFlags.Metadata, n => n.AllocSize)}",
            $"refinalize_ms={refinalize?.TotalMilliseconds:F0}",
        ]);
    }
    else
    {
        lines.AddRange(
        [
            $"access_denied_dirs={stats.AccessDeniedDirectories}",
            $"error_dirs={stats.ErrorDirectories}",
            $"reparse_dirs={stats.ReparsePointDirectories}",
        ]);
    }

    File.WriteAllLines(Path.Combine(outDir, $"{mode}-summary.txt"), lines);
    File.WriteAllLines(Path.Combine(outDir, $"{mode}-dirs.tsv"), allLinks ?? Dump(tree));
    if (mode == "mft")
    {
        File.WriteAllLines(Path.Combine(outDir, "mft-dirs-dedup.tsv"), Dump(tree));
    }

    Console.WriteLine($"{mode}: {stats.TotalDuration.TotalSeconds:F1} 秒，{root.FileCount:N0} 个文件，{root.DirCount:N0} 个目录，" +
                      $"占用 {ByteSize.Format(root.AllocSize)}，峰值内存 {ByteSize.Format(proc.PeakWorkingSet64)}");
}

static long SumRootChildren(NodeTree tree, NodeFlags flag, Func<Node, long> value)
{
    long sum = 0;
    foreach (var c in tree.Children(tree.Root))
    {
        if ((tree[c].Flags & flag) != 0)
        {
            sum += value(tree[c]);
        }
    }

    return sum;
}

// 深度不超过 3 的目录：路径、逻辑大小、分配大小、文件数、目录数、标记
List<string> Dump(NodeTree tree)
{
    var lines = new List<string> { "path\tlogical\talloc\tfiles\tdirs\tflags" };
    var stack = new Stack<(int Node, int Depth)>();
    stack.Push((tree.Root, 0));
    while (stack.Count > 0)
    {
        var (n, depth) = stack.Pop();
        ref var node = ref tree[n];
        if (!node.IsDirectory)
        {
            continue;
        }

        lines.Add(string.Join('\t', tree.GetPath(n), node.LogicalSize.ToString(inv), node.AllocSize.ToString(inv),
            node.FileCount.ToString(inv), node.DirCount.ToString(inv), ((int)node.Flags).ToString(inv)));
        if (depth < 3)
        {
            foreach (var c in tree.Children(n))
            {
                stack.Push((c, depth + 1));
            }
        }
    }

    return lines;
}

int RunAll(string drive)
{
    var self = Environment.ProcessPath!;
    int Child(string mode, string target)
    {
        var psi = new ProcessStartInfo(self) { UseShellExecute = false };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--out");
        psi.ArgumentList.Add(outDir);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }

    var d = drive.TrimEnd('\\');
    var walk = Child("walk", d + @"\");
    var mft = IsElevated() ? Child("mft", d) : -1;
    if (mft == -1)
    {
        Console.WriteLine("当前不是管理员，跳过 MFT 扫描");
    }

    if (walk == 0 && mft == 0)
    {
        Compare();
    }

    File.WriteAllText(Path.Combine(outDir, "all-done.txt"), $"walk={walk} mft={mft}");
    return walk == 0 && mft is 0 or -1 ? 0 : 1;
}

void Compare()
{
    var walkSummary = ReadSummary("walk");
    var mftSummary = ReadSummary("mft");
    var walk = ReadDirs("walk-dirs.tsv");
    var mft = ReadDirs("mft-dirs.tsv");
    var mftDedup = ReadDirs("mft-dirs-dedup.tsv");

    const int incomplete = (int)(NodeFlags.AccessDenied | NodeFlags.Incomplete);
    var md = new StringBuilder();
    md.AppendLine("# 阶段 0.2：MFT 扫描与遍历扫描对比");
    md.AppendLine();
    md.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
    md.AppendLine();
    md.AppendLine("## 性能");
    md.AppendLine();
    md.AppendLine("| 指标 | 遍历扫描 | MFT 扫描 |");
    md.AppendLine("|---|---|---|");
    Row("总耗时", s => $"{long.Parse(s["total_ms"], inv) / 1000.0:F1} 秒");
    Row("其中读取", s => $"{long.Parse(s["read_ms"], inv) / 1000.0:F1} 秒");
    Row("文件数", s => long.Parse(s["files"], inv).ToString("N0", inv));
    Row("目录数", s => long.Parse(s["dirs"], inv).ToString("N0", inv));
    Row("节点数", s => long.Parse(s["nodes"], inv).ToString("N0", inv));
    Row("占用（汇总）", s => ByteSize.Format(long.Parse(s["alloc_bytes"], inv)));
    Row("逻辑大小（汇总）", s => ByteSize.Format(long.Parse(s["logical_bytes"], inv)));
    Row("卷已用空间", s => ByteSize.Format(long.Parse(s["volume_used_bytes"], inv)));
    Row("峰值内存（工作集）", s => ByteSize.Format(long.Parse(s["peak_working_set_bytes"], inv)));
    Row("扫描后常驻内存（回收后工作集）", s => ByteSize.Format(long.Parse(s["working_set_bytes"], inv)));
    Row("扫描后托管堆", s => ByteSize.Format(long.Parse(s["managed_heap_bytes"], inv)));
    Row("树结构占用", s => ByteSize.Format(long.Parse(s["tree_bytes"], inv)));
    md.AppendLine();

    md.AppendLine("MFT 扫描的其他数据：");
    md.AppendLine();
    foreach (var key in new[] { "mft_records", "mft_records_in_use", "mft_bad_records", "mft_malformed_runs", "mft_record_size", "mft_data_segments", "hardlink_secondary_names", "unreachable" })
    {
        md.AppendLine($"- {key} = {mftSummary.GetValueOrDefault(key, "-")}");
    }

    md.AppendLine($"- 备用数据流 = {ByteSize.Format(long.Parse(mftSummary["ads_bytes"], inv))}");
    md.AppendLine($"- 目录索引 = {ByteSize.Format(long.Parse(mftSummary["dir_index_bytes"], inv))}");
    md.AppendLine($"- NTFS 元数据文件（根目录下）= {ByteSize.Format(long.Parse(mftSummary["metadata_alloc_bytes"], inv))}");
    md.AppendLine($"- 遍历扫描：无权限目录 {walkSummary["access_denied_dirs"]} 个，出错目录 {walkSummary["error_dirs"]} 个，跳过的 junction/符号链接目录 {walkSummary["reparse_dirs"]} 个");
    md.AppendLine();

    // 只比较遍历扫描能完整列出的目录；MFT 侧按"每个硬链接名都计大小"汇总，与遍历口径一致
    long wFiles = 0, mFiles = 0, wLogical = 0, mLogical = 0;
    var rows = new List<(string Path, long WF, long MF, long WL, long ML, bool Complete)>();
    foreach (var (path, w) in walk)
    {
        if (!mft.TryGetValue(path, out var m))
        {
            continue;
        }

        var depth = path.TrimEnd('\\').Count(c => c == '\\');
        var complete = (w.Flags & incomplete) == 0;
        if (depth == 1 && complete)
        {
            wFiles += w.Files;
            mFiles += m.Files;
            wLogical += w.Logical;
            mLogical += m.Logical;
        }

        if (depth <= 2)
        {
            rows.Add((path, w.Files, m.Files, w.Logical, m.Logical, complete));
        }
    }

    md.AppendLine("## 一致性（只统计遍历扫描能完整列出的顶层目录）");
    md.AppendLine();
    md.AppendLine("| 指标 | 遍历扫描 | MFT 扫描 | 差异 |");
    md.AppendLine("|---|---|---|---|");
    md.AppendLine($"| 文件数 | {wFiles:N0} | {mFiles:N0} | {mFiles - wFiles:+#,0;-#,0;0}（{Pct(mFiles - wFiles, wFiles)}） |");
    md.AppendLine($"| 逻辑大小 | {ByteSize.Format(wLogical)} | {ByteSize.Format(mLogical)} | {ByteSize.FormatDelta(mLogical - wLogical)}（{Pct(mLogical - wLogical, wLogical)}） |");
    md.AppendLine();
    md.AppendLine("硬链接只计一次时，MFT 侧根目录逻辑大小为 " +
                  ByteSize.Format(mftDedup.TryGetValue(mftSummary["root"], out var dr) ? dr.Logical : 0) + "。");
    md.AppendLine();

    md.AppendLine("## 差异最大的目录（深度 ≤ 2，按大小差异排序）");
    md.AppendLine();
    md.AppendLine("| 目录 | 完整 | 遍历文件数 | MFT 文件数 | 遍历大小 | MFT 大小 | 差异 |");
    md.AppendLine("|---|---|---|---|---|---|---|");
    foreach (var r in rows.OrderByDescending(r => Math.Abs(r.ML - r.WL)).Take(25))
    {
        md.AppendLine($"| `{r.Path}` | {(r.Complete ? "是" : "否")} | {r.WF:N0} | {r.MF:N0} | {ByteSize.Format(r.WL)} | {ByteSize.Format(r.ML)} | {ByteSize.FormatDelta(r.ML - r.WL)} |");
    }

    File.WriteAllText(Path.Combine(outDir, "compare.md"), md.ToString());
    Console.WriteLine($"对比报告：{Path.Combine(outDir, "compare.md")}");

    void Row(string name, Func<Dictionary<string, string>, string> f) =>
        md.AppendLine($"| {name} | {f(walkSummary)} | {f(mftSummary)} |");
}

static string Pct(long delta, long total) => total == 0 ? "-" : (delta * 100.0 / total).ToString("+0.000;-0.000;0", CultureInfo.InvariantCulture) + "%";

Dictionary<string, string> ReadSummary(string mode) =>
    File.ReadAllLines(Path.Combine(outDir, $"{mode}-summary.txt"))
        .Select(l => l.Split('=', 2))
        .ToDictionary(p => p[0], p => p[1]);

Dictionary<string, (long Logical, long Alloc, long Files, long Dirs, int Flags)> ReadDirs(string file)
{
    var dict = new Dictionary<string, (long, long, long, long, int)>(StringComparer.OrdinalIgnoreCase);
    foreach (var line in File.ReadLines(Path.Combine(outDir, file)).Skip(1))
    {
        var p = line.Split('\t');
        dict[p[0]] = (long.Parse(p[1], inv), long.Parse(p[2], inv), long.Parse(p[3], inv), long.Parse(p[4], inv), int.Parse(p[5], inv));
    }

    return dict;
}

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
}

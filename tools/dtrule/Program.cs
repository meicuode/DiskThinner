// dtrule：DiskThinner 规则维护工具。
//
//   dtrule validate <rules 目录>                     校验全部 YAML 规则
//   dtrule build    <rules 目录> --out rules.db      校验并编译成规则库（有错误时失败）
//   dtrule probe    <rules 目录> [--drive C:] [--walk] [--out report.md]
//                                                    在本机扫描并统计规则覆盖率（管理员下用 MFT 扫描）

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiskThinner.Core;
using DiskThinner.Rules;
using DiskThinner.RuleTools;
using DiskThinner.Scanner;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length < 2)
{
    Console.WriteLine("用法: dtrule validate|build|probe <rules 目录> [选项]");
    return 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var command = args[0];
var dir = Path.GetFullPath(args[1]);
var (rules, loadIssues) = RuleLoader.LoadDirectory(dir);
var (errors, warnings) = RuleValidator.Validate(rules, loadIssues);

foreach (var w in warnings)
{
    Console.WriteLine("警告 " + w);
}

foreach (var e in errors)
{
    Console.Error.WriteLine("错误 " + e);
}

Console.WriteLine($"规则 {rules.Locations.Count} 条，应用 {rules.Apps.Count} 个，版本 {rules.Version}；错误 {errors.Count}，警告 {warnings.Count}");

switch (command)
{
    case "validate":
        return errors.Count == 0 ? 0 : 1;

    case "build":
    {
        if (errors.Count > 0)
        {
            return 1;
        }

        var output = Path.GetFullPath(Option("--out") ?? "rules.db");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        RuleDb.Write(rules, output);

        // 回读一遍，确认客户端能用同样的加固方式打开
        var back = RuleDb.Read(output);
        if (back.Locations.Count != rules.Locations.Count)
        {
            Console.Error.WriteLine("回读的规则数不一致");
            return 1;
        }

        Console.WriteLine($"已写入 {output}（{ByteSize.Format(new FileInfo(output).Length)}）");
        return 0;
    }

    case "probe":
        if (errors.Count > 0)
        {
            return 1;
        }

        return Probe(rules, Option("--drive") ?? Path.GetPathRoot(Environment.SystemDirectory)!, args.Contains("--walk"), Option("--out"));

    default:
        Console.WriteLine($"未知命令 {command}");
        return 2;
}

static int Probe(RuleSet rules, string drive, bool walk, string? output)
{
    var inv = CultureInfo.InvariantCulture;
    var sw = Stopwatch.StartNew();
    var scan = VolumeScanner.Scan(drive, walk ? ScanPreference.Walk : ScanPreference.Auto);
    var scanTime = sw.Elapsed;
    var tree = scan.Tree;
    var engine = new RuleEngine(rules, PathContext.FromSystem());
    var c = engine.Classify(tree);

    var user = Environment.UserName;
    var userRoot = tree.FindPath(Path.Combine(Path.GetPathRoot(drive)!, "Users", user));
    string Redact(string path) => Regex.Replace(path, @"^([A-Z]:\\Users\\)([^\\]+)", m =>
        m.Groups[2].Value.Equals(user, StringComparison.OrdinalIgnoreCase) ? "%USERPROFILE%" : m.Groups[1].Value + "<其他用户>", RegexOptions.IgnoreCase);
    string Pct(long part, long total) => total == 0 ? "-" : (part * 100.0 / total).ToString("0.0", inv) + "%";

    long Explained(int node) => node < 0 ? 0 : tree[node].AllocSize - c.Unexplained(node);

    var md = new StringBuilder();
    md.AppendLine("# 规则覆盖率实测（dtrule probe）");
    md.AppendLine();
    md.AppendLine($"规则版本 {rules.Version}，{rules.Locations.Count} 条；{(scan.Stats.Mode == ScanMode.Mft ? "MFT" : "遍历")}扫描 {drive}，{tree.Count:N0} 个节点，" +
                  $"扫描 {scanTime.TotalSeconds:F1} 秒，规则匹配 {c.Duration.TotalMilliseconds:F0} 毫秒。");
    md.AppendLine();
    md.AppendLine("| 范围 | 总占用 | 规则解释 | 解释率 | 可清理 |");
    md.AppendLine("|---|---|---|---|---|");
    md.AppendLine($"| 整个 {drive.TrimEnd('\\')} | {ByteSize.Format(tree[tree.Root].AllocSize)} | {ByteSize.Format(Explained(tree.Root))} | {Pct(Explained(tree.Root), tree[tree.Root].AllocSize)} | {ByteSize.Format(c.TotalCleanable)} |");
    if (userRoot >= 0)
    {
        md.AppendLine($"| %USERPROFILE% | {ByteSize.Format(tree[userRoot].AllocSize)} | {ByteSize.Format(Explained(userRoot))} | {Pct(Explained(userRoot), tree[userRoot].AllocSize)} | {ByteSize.Format(c.Cleanable(userRoot))} |");
    }

    md.AppendLine();
    md.AppendLine($"可清理按风险：安全 {ByteSize.Format(c.CleanableBytes(Risk.Safe))}，需重新下载 {ByteSize.Format(c.CleanableBytes(Risk.Low))}，丢历史或登录态 {ByteSize.Format(c.CleanableBytes(Risk.Medium))}。");
    md.AppendLine();

    md.AppendLine("## 命中最多的规则");
    md.AppendLine();
    md.AppendLine("| 规则 | 类别 | 风险 | 命中位置 | 占用 | 可清理 |");
    md.AppendLine("|---|---|---|---|---|---|");
    var byRule = c.Roots.GroupBy(r => r.Rule)
        .Select(g => (Rule: rules.Locations[g.Key], Roots: g.Count(), Bytes: g.Sum(r => r.Bytes), Clean: g.Sum(r => r.CleanableBytes)))
        .OrderByDescending(x => x.Bytes).ToList();
    foreach (var x in byRule.Take(50))
    {
        md.AppendLine($"| `{x.Rule.Id}` | {Names.Display(x.Rule.Kind)} | {Names.Display(x.Rule.Risk)} | {x.Roots:N0} | {ByteSize.Format(x.Bytes)} | {ByteSize.Format(x.Clean)} |");
    }

    var unused = rules.Locations.Where(r => byRule.All(x => x.Rule.Id != r.Id)).Select(r => $"`{r.Id}`").ToList();
    md.AppendLine();
    md.AppendLine($"在这台机器上没有命中的规则（{unused.Count} 条）：{string.Join("、", unused)}");
    md.AppendLine();

    md.AppendLine("## 还没解释的大块（≥ 200 MB）");
    md.AppendLine();
    md.AppendLine("| 位置 | 未解释 |");
    md.AppendLine("|---|---|");
    foreach (var (node, bytes, rest) in c.UnexplainedHotspots(200L << 20).Take(30))
    {
        md.AppendLine($"| `{Redact(tree.GetPath(node))}`{(rest ? "（其余部分）" : "")} | {ByteSize.Format(bytes)} |");
    }

    var text = md.ToString();
    if (output is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, text);
        Console.WriteLine($"报告：{output}");
    }
    else
    {
        Console.WriteLine(text);
    }

    return 0;
}

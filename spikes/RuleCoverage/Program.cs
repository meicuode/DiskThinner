// 阶段 0.4：规则覆盖率实验。
//
//   RuleCoverage <WizTree 导出的 CSV> [--rules candidate-rules.txt] [--out coverage.md] [--user 用户名]
//
// 读入 WizTree 的完整导出（文件 + 目录），用候选规则从上往下匹配，统计各层规则能解释多少空间，
// 再用"热点分解"列出还没被解释的大块，作为下一批规则的候选。输出里的用户名会替换成 %USERPROFILE%。

using System.Diagnostics;
using System.Globalization;
using System.Text;
using DiskThinner.Core;
using DiskThinner.Scanner;
using RuleCoverage;

var inv = CultureInfo.InvariantCulture;
if (args.Length < 1)
{
    Console.WriteLine("用法: RuleCoverage <WizTree CSV> [--rules 文件] [--out 文件] [--user 用户名]");
    return 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var csvPath = args[0];
var rulesPath = Option("--rules") ?? Path.Combine(AppContext.BaseDirectory, "candidate-rules.txt");
var outPath = Option("--out") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "rulecoverage", "coverage.md");
var user = Option("--user") ?? Environment.UserName;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

var sw = Stopwatch.StartNew();
var (tree, meta) = WizTreeCsv.Load(csvPath);
Console.WriteLine($"读入 {tree.Count:N0} 个节点，用时 {sw.Elapsed.TotalSeconds:F1} 秒");
var rules = Rule.Load(rulesPath);
Console.WriteLine($"规则 {rules.Count} 条");

var userRoot = tree.FindPath($@"C:\Users\{user}");
if (userRoot < 0)
{
    Console.WriteLine($"找不到 C:\\Users\\{user}");
    return 1;
}

string[][] layerSets =
[
    ["system"],
    ["system", "generic"],
    ["system", "generic", "devtools"],
    ["system", "generic", "devtools", "apps"],
];
string[] layerNames = ["只有系统位置", "+ 通用结构规则", "+ 开发与 AI 工具", "+ 常用软件抽样"];

sw.Restart();
var results = layerSets.Select(set => Matcher.Run(tree, rules.Where(r => set.Contains(r.Layer)).ToList(), userRoot)).ToList();
Console.WriteLine($"匹配 {layerSets.Length} 轮，用时 {sw.Elapsed.TotalSeconds:F1} 秒");
var full = results[^1];

string Redact(string path)
{
    var prefix = $@"C:\Users\{user}";
    if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return "%USERPROFILE%" + path[prefix.Length..];
    }

    return System.Text.RegularExpressions.Regex.Replace(path, @"^C:\\Users\\[^\\]+", @"C:\Users\<其他用户>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

var md = new StringBuilder();
md.AppendLine("# 阶段 0.4：规则覆盖率实验");
md.AppendLine();
md.AppendLine($"数据：WizTree 导出（{meta.Generated}），{tree.Count:N0} 个节点；候选规则 {rules.Count} 条（`spikes/RuleCoverage/candidate-rules.txt`）。");
md.AppendLine($"口径：按分配大小统计。WizTree 根目录记录的占用为 {ByteSize.Format(meta.RootAlloc)}，本程序由文件汇总得到 {ByteSize.Format(tree[tree.Root].AllocSize)}。");
md.AppendLine();
md.AppendLine("\"解释\"指这块空间命中了某条规则，知道它是什么、风险多大（包括\"不能删\"的系统文件和用户数据）。\"可清理\"指风险为安全、需重新下载、丢历史或登录态的部分；\"系统工具处理\"指只能用 DISM、系统设置等官方入口处理的部分。");
md.AppendLine();

md.AppendLine("## 覆盖率");
md.AppendLine();
md.AppendLine("| 规则层 | 规则数 | 用户目录解释率 | 用户目录可清理 | 整个 C 盘解释率 | C 盘可清理 | C 盘系统工具处理 |");
md.AppendLine("|---|---|---|---|---|---|---|");
for (var i = 0; i < results.Count; i++)
{
    var r = results[i];
    var count = rules.Count(x => layerSets[i].Contains(x.Layer));
    md.AppendLine($"| {layerNames[i]} | {count} | {Pct(r.Explained[userRoot], tree[userRoot].AllocSize)} | {ByteSize.Format(r.Cleanable[userRoot])} | " +
                  $"{Pct(r.Explained[tree.Root], tree[tree.Root].AllocSize)} | {ByteSize.Format(r.Cleanable[tree.Root])} | {ByteSize.Format(r.Guide[tree.Root])} |");
}

md.AppendLine();
md.AppendLine($"用户目录（%USERPROFILE%）共 {ByteSize.Format(tree[userRoot].AllocSize)}，整个 C 盘共 {ByteSize.Format(tree[tree.Root].AllocSize)}。");
md.AppendLine();

md.AppendLine("## 按风险（全部规则）");
md.AppendLine();
md.AppendLine("| 风险 | 用户目录 | 整个 C 盘 |");
md.AppendLine("|---|---|---|");
foreach (var risk in new[] { "safe", "low", "medium", "high", "never", "guide" })
{
    md.AppendLine($"| {RiskName(risk)} | {ByteSize.Format(full.ByRiskUser.GetValueOrDefault(risk))} | {ByteSize.Format(full.ByRiskAll.GetValueOrDefault(risk))} |");
}

md.AppendLine($"| 未解释 | {ByteSize.Format(tree[userRoot].AllocSize - full.Explained[userRoot])} | {ByteSize.Format(tree[tree.Root].AllocSize - full.Explained[tree.Root])} |");
md.AppendLine();

md.AppendLine("## 命中最多的规则（全部规则，整个 C 盘）");
md.AppendLine();
md.AppendLine("| 规则 | 层 | 类别 | 风险 | 命中空间 | 命中节点 |");
md.AppendLine("|---|---|---|---|---|---|");
foreach (var (rule, bytes) in full.ByRule.OrderByDescending(kv => kv.Value).Take(40).Select(kv => (rules.First(r => r.Id == kv.Key), kv.Value)))
{
    md.AppendLine($"| `{rule.Id}` | {rule.Layer} | {rule.Category} | {RiskName(rule.Risk)} | {ByteSize.Format(bytes)} | {full.HitNodes.GetValueOrDefault(rule.Id):N0} |");
}

var unused = rules.Where(r => !full.ByRule.ContainsKey(r.Id)).Select(r => $"`{r.Id}`").ToList();
md.AppendLine();
md.AppendLine($"在这份数据上没有命中的规则（{unused.Count} 条）：{string.Join("、", unused)}");
md.AppendLine();

md.AppendLine("## 还没解释的大块（热点分解，阈值 200 MB）");
md.AppendLine();
md.AppendLine("算法与研发方案 3.5 节相同，只是把\"变化量\"换成\"未解释的空间\"：大块往下拆，拆到变化分散为止；各项互不重叠。");
md.AppendLine();
md.AppendLine("| 位置 | 未解释 | 所在目录总大小 |");
md.AppendLine("|---|---|---|");
foreach (var (node, bytes, rest) in Hotspots.Find(tree, full.Unexplained, 200L << 20).OrderByDescending(h => h.Bytes).Take(40))
{
    md.AppendLine($"| `{Redact(tree.GetPath(node))}{(rest ? "`（其余部分）" : "`")} | {ByteSize.Format(bytes)} | {ByteSize.Format(tree[node].AllocSize)} |");
}

File.WriteAllText(outPath, md.ToString());
Console.WriteLine($"报告：{outPath}");
return 0;

string Pct(long part, long total) => total == 0 ? "-" : (part * 100.0 / total).ToString("0.0", inv) + "%";

static string RiskName(string risk) => risk switch
{
    "safe" => "安全",
    "low" => "需重新下载",
    "medium" => "丢历史或登录态",
    "high" => "丢用户数据",
    "never" => "不能删",
    "guide" => "系统工具处理",
    _ => risk,
};

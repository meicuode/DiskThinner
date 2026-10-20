using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiskThinner.Core;

namespace ScanBench;

/// <summary>
/// M1 验收：和 WizTree 比较主要目录的占用（按分配大小）。
/// 有多个硬链接名的文件只能算在其中一个目录里，WizTree 和本程序选的可能不同；
/// 所以把"硬链接文件占目录 1% 以上"的目录单独列出，不计入验收口径。各目录加总后的总量不受影响。
/// </summary>
internal static class WizTreeCompare
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Row(string Path, int Depth, long Ours, long Wiz, long Linked)
    {
        public double Error => Wiz == 0 ? 0 : Math.Abs(Ours - Wiz) * 100.0 / Wiz;

        /// <summary>误差不到 1%。</summary>
        public bool Within1Percent => Error < 1;

        /// <summary>差值不超过目录里硬链接文件的量：可以完全由"共享文件算在哪个目录"解释。</summary>
        public bool ExplainedByHardLinks => !Within1Percent && Math.Abs(Ours - Wiz) <= Linked;

        public bool Unexplained => !Within1Percent && !ExplainedByHardLinks;
    }

    /// <summary>读取 ScanBench 导出的目录表：path、logical、alloc、files、dirs、flags、linked。</summary>
    public static Dictionary<string, (long Alloc, long Linked)> ReadOurs(string tsv)
    {
        var dict = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(tsv).Skip(1))
        {
            var p = line.Split('\t');
            dict[p[0]] = (long.Parse(p[2], Inv), p.Length > 6 ? long.Parse(p[6], Inv) : 0);
        }

        return dict;
    }

    /// <summary>读取 WizTree 的目录导出（/exportfiles=0）：第一行是生成信息，第二行是表头，路径带引号，目录以 \ 结尾。</summary>
    public static Dictionary<string, long> ReadWizTree(string csv)
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(csv).Skip(1))
        {
            if (!line.StartsWith('"'))
            {
                continue;
            }

            var end = line.IndexOf("\",", 1, StringComparison.Ordinal);
            var path = line[1..end].Replace("\"\"", "\"");
            var fields = line[(end + 2)..].Split(',');
            if (path.EndsWith('\\') && long.TryParse(fields[1], Inv, out var alloc))
            {
                dict[path.Length == 3 ? path : path.TrimEnd('\\')] = alloc;
            }
        }

        return dict;
    }

    public static List<Row> Rows(Dictionary<string, (long Alloc, long Linked)> ours, Dictionary<string, long> wiz)
    {
        var rows = new List<Row>();
        foreach (var (path, (alloc, linked)) in ours)
        {
            var depth = path.TrimEnd('\\').Count(c => c == '\\');
            if (depth <= 3 && alloc >= 100L << 20 && wiz.TryGetValue(path, out var w))
            {
                rows.Add(new Row(path, depth, alloc, w, linked));
            }
        }

        return rows;
    }

    public static string Report(string generated, List<Row> rows)
    {
        var main = rows.Where(r => r.Depth <= 2 && r.Wiz >= 1L << 30).ToList();

        static string Line(string name, List<Row> set) =>
            $"| {name} | {set.Count} | {set.Count(r => r.Within1Percent)} | {set.Count(r => r.ExplainedByHardLinks)} | {set.Count(r => r.Unexplained)} | {(set.Count == 0 ? 0 : set.Max(r => r.Error)):F2}% |";

        var md = new StringBuilder();
        md.AppendLine("# M1：与 WizTree 对比主要目录占用");
        md.AppendLine();
        md.AppendLine($"WizTree：{generated}；本程序：MFT 扫描，硬链接只算一次。按分配大小比较。");
        md.AppendLine();
        md.AppendLine("有多个硬链接名的文件只能算在其中一个目录里，WizTree 和本程序选的可能不同。差值不超过目录里硬链接文件的量时，记为\"硬链接归属差异\"，不是测量误差；各目录加总后的总量不受影响。");
        md.AppendLine();
        md.AppendLine("| 范围 | 目录数 | 误差 < 1% | 硬链接归属差异 | 无法解释 | 最大误差 |");
        md.AppendLine("|---|---|---|---|---|---|");
        md.AppendLine(Line("深度 ≤ 2 且 ≥ 1 GB（验收口径）", main));
        md.AppendLine(Line("深度 ≤ 3 且 ≥ 100 MB", rows));
        md.AppendLine();
        md.AppendLine("## 深度 ≤ 2 且 ≥ 1 GB 的目录");
        md.AppendLine();
        md.AppendLine("| 目录 | WizTree | 本程序 | 误差 | 目录内硬链接文件 |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var r in main.OrderByDescending(r => r.Wiz))
        {
            md.AppendLine($"| `{Redact(r.Path)}` | {ByteSize.Format(r.Wiz)} | {ByteSize.Format(r.Ours)} | {r.Error:F2}%{(r.ExplainedByHardLinks ? "（硬链接归属）" : r.Unexplained ? "（无法解释）" : "")} | {ByteSize.Format(r.Linked)} |");
        }

        md.AppendLine();
        md.AppendLine("## 误差最大的 15 个目录（深度 ≤ 3 且 ≥ 100 MB）");
        md.AppendLine();
        md.AppendLine("| 目录 | WizTree | 本程序 | 差异 | 误差 | 硬链接文件 |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows.OrderByDescending(r => r.Error).Take(15))
        {
            md.AppendLine($"| `{Redact(r.Path)}` | {ByteSize.Format(r.Wiz)} | {ByteSize.Format(r.Ours)} | {ByteSize.FormatDelta(r.Ours - r.Wiz)} | {r.Error:F2}% | {ByteSize.Format(r.Linked)} |");
        }

        return md.ToString();
    }

    public static string Redact(string path) =>
        Regex.Replace(path, @"^C:\\Users\\" + Regex.Escape(Environment.UserName) + @"(?=\\|$)", "%USERPROFILE%", RegexOptions.IgnoreCase);
}

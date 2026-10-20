using System.Globalization;
using System.Text;
using DiskThinner.Scanner;

namespace RuleCoverage;

internal sealed record CsvMeta(string Generated, long RootAlloc);

/// <summary>
/// 读取 WizTree 的完整导出。第一行是"生成由 WizTree …"，第二行是表头：文件名称,大小,分配,修改时间,属性,文件,文件夹。
/// 目录行以 \ 结尾。只取文件行的分配大小，目录大小由 NodeTree 自己汇总。
/// </summary>
internal static class WizTreeCsv
{
    public static (NodeTree Tree, CsvMeta Meta) Load(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20);
        var generated = reader.ReadLine() ?? "";
        reader.ReadLine(); // 表头

        var tree = new NodeTree(1 << 20);
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long rootAlloc = 0;

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var (name, rest) = SplitPath(line);
            var fields = rest.Split(',');
            var alloc = long.Parse(fields[1], CultureInfo.InvariantCulture);
            var modified = DateTime.TryParse(fields[2], CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.AssumeLocal, out var m)
                ? m.ToUniversalTime().ToFileTimeUtc()
                : 0;

            if (name.EndsWith('\\'))
            {
                var dirPath = name.Length == 3 ? name : name.TrimEnd('\\');
                if (tree.Root < 0 && name.Length == 3)
                {
                    rootAlloc = alloc;
                }

                EnsureDir(dirPath, modified);
            }
            else
            {
                var slash = name.LastIndexOf('\\');
                var parent = EnsureDir(slash == 2 ? name[..3] : name[..slash], 0);
                tree.Add(parent, name.AsSpan(slash + 1), NodeFlags.None, long.Parse(fields[0], CultureInfo.InvariantCulture), alloc, modified);
            }
        }

        tree.Finalize();
        return (tree, new CsvMeta(generated.Trim(), rootAlloc));

        int EnsureDir(string dirPath, long modified)
        {
            if (dirs.TryGetValue(dirPath, out var existing))
            {
                return existing;
            }

            int index;
            if (dirPath.Length <= 3)
            {
                index = tree.Add(-1, dirPath, NodeFlags.Directory, 0, 0, modified);
                tree.Root = index;
            }
            else
            {
                var slash = dirPath.LastIndexOf('\\');
                var parent = EnsureDir(slash == 2 ? dirPath[..3] : dirPath[..slash], 0);
                index = tree.Add(parent, dirPath.AsSpan(slash + 1), NodeFlags.Directory, 0, 0, modified);
            }

            dirs[dirPath] = index;
            return index;
        }
    }

    /// <summary>第一个字段是带引号的路径，内部引号写成两个双引号。</summary>
    private static (string Path, string Fields) SplitPath(string line)
    {
        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            return (line[..comma], line[(comma + 1)..]);
        }

        var sb = new StringBuilder();
        var i = 1;
        while (i < line.Length)
        {
            if (line[i] == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                break;
            }

            sb.Append(line[i++]);
        }

        return (sb.ToString(), line[(i + 2)..]);
    }
}

using System.Globalization;
using System.Text.Json;
using DiskThinner.Scanner;

namespace DiskThinner.Tests.Shared;

/// <summary>
/// 读取 tests/fixtures 下的 JSON（格式见 tests/fixtures/README.md）：目录树和大小。
/// 这个文件链接进 Rules.Tests 和 Storage.Tests 两个测试项目。
/// </summary>
internal static class Fixtures
{
    /// <summary>tests/fixtures/&lt;子目录&gt;。</summary>
    public static string Directory(string kind) => System.IO.Path.Combine(RepoRoot(), "tests", "fixtures", kind);

    /// <summary>某个子目录下全部 JSON 文件，按文件名排序。</summary>
    public static IEnumerable<string> Files(string kind) =>
        System.IO.Directory.EnumerateFiles(Directory(kind), "*.json").Order(StringComparer.Ordinal);

    public static JsonDocument Load(string kind, string file) => JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(Directory(kind), file)));

    /// <summary>仓库根目录（包含 DiskThinner.slnx 的目录）。</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "DiskThinner.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根目录");
    }

    /// <summary>目录树：["路径", 大小]、["路径", 大小, ["标记"]]、["目录/", 0]、{ "repeat", "dir", "name", "size" }。根是 C:\。</summary>
    public static NodeTree Tree(JsonElement items)
    {
        var tree = new NodeTree(64);
        tree.Root = tree.Add(-1, @"C:\", NodeFlags.Directory, 0, 0, 0);
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["C:"] = tree.Root };

        int Dir(string path)
        {
            if (dirs.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var slash = path.LastIndexOf('\\');
            var index = tree.Add(Dir(path[..slash]), path.AsSpan(slash + 1), NodeFlags.Directory, 0, 0, 0);
            dirs[path] = index;
            return index;
        }

        void File(string path, long size, NodeFlags flags)
        {
            if (path.EndsWith('\\'))
            {
                Dir(path.TrimEnd('\\'));
                return;
            }

            var slash = path.LastIndexOf('\\');
            tree.Add(Dir(path[..slash]), path.AsSpan(slash + 1), flags, size, size, 0);
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var dir = Path(item.GetProperty("dir").GetString()!);
                var name = Path(item.GetProperty("name").GetString()!); // 可以带子目录：s{i}/x.tmp
                var size = Size(item.GetProperty("size"));
                for (var i = 0; i < item.GetProperty("repeat").GetInt32(); i++)
                {
                    File(dir + "\\" + name.Replace("{i}", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), size, NodeFlags.None);
                }

                continue;
            }

            var flags = NodeFlags.None;
            if (item.GetArrayLength() > 2)
            {
                foreach (var f in item[2].EnumerateArray())
                {
                    flags |= f.GetString() switch
                    {
                        "hard-linked" => NodeFlags.HardLinked,
                        "hard-link-secondary" => NodeFlags.HardLinkSecondary | NodeFlags.HardLinked,
                        "metadata" => NodeFlags.Metadata,
                        "reparse-point" => NodeFlags.ReparsePoint,
                        var s => throw new InvalidDataException("不认识的标记：" + s),
                    };
                }
            }

            File(Path(item[0].GetString()!), Size(item[1]), flags);
        }

        tree.Finalize();
        return tree;
    }

    /// <summary>C:/a/b → C:\a\b（以 / 结尾的保留结尾的 \）。</summary>
    public static string Path(string p) => p.Replace('/', '\\');

    /// <summary>数字，或 "300M"、"-150M"、"4G"、"64K"（1K = 1024）。</summary>
    public static long Size(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Number)
        {
            return e.GetInt64();
        }

        var s = e.GetString()!.Trim();
        var unit = char.ToUpperInvariant(s[^1]) switch
        {
            'K' => 1L << 10,
            'M' => 1L << 20,
            'G' => 1L << 30,
            _ => 1L,
        };
        return (long)(double.Parse(unit == 1 ? s : s[..^1], CultureInfo.InvariantCulture) * unit);
    }
}

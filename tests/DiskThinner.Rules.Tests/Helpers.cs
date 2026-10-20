using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.Rules.Tests;

internal static class Helpers
{
    /// <summary>用"路径 → 大小"列表构造树，路径从盘符开始（C:\...）。以 \ 结尾的条目是空目录。</summary>
    public static NodeTree Tree(params (string Path, long Size)[] items) => Tree(items.Select(i => (i.Path, i.Size, NodeFlags.None)).ToArray());

    public static NodeTree Tree(params (string Path, long Size, NodeFlags Flags)[] items)
    {
        var tree = new NodeTree(64);
        tree.Root = tree.Add(-1, @"C:\", NodeFlags.Directory, 0, 0, 0);
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [@"C:"] = tree.Root };

        int Dir(string path)
        {
            if (dirs.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var slash = path.LastIndexOf('\\');
            var parent = Dir(path[..slash]);
            var index = tree.Add(parent, path.AsSpan(slash + 1), NodeFlags.Directory, 0, 0, 0);
            dirs[path] = index;
            return index;
        }

        foreach (var (path, size, flags) in items)
        {
            if (path.EndsWith('\\'))
            {
                Dir(path.TrimEnd('\\'));
                continue;
            }

            var slash = path.LastIndexOf('\\');
            tree.Add(Dir(path[..slash]), path.AsSpan(slash + 1), flags, size, size, 0);
        }

        tree.Finalize();
        return tree;
    }

    public static LocationRule Rule(string id, MatchMode mode, string pattern, Risk risk = Risk.Safe, Kind kind = Kind.Cache,
        Tier tier = Tier.Reviewed, params Condition[] conditions) => new()
    {
        Id = id,
        Mode = mode,
        Pattern = pattern,
        Kind = kind,
        Risk = risk,
        Tier = tier,
        Purpose = "测试",
        Impact = "测试",
        Conditions = conditions,
        Evidence = [new Evidence(null, "测试")],
        // 可清理的规则默认带一个删除动作（只有可执行的规则才计入可清理）
        Actions = risk <= Risk.Medium ? [new DeleteAction(DeleteScope.Contents)] : [],
    };

    public static Classification Classify(NodeTree tree, params LocationRule[] rules) =>
        new RuleEngine(new RuleSet([], rules), PathContext.Default).Classify(tree);

    public static string? RuleIdAt(this Classification c, string path) => c.RuleOf(c.Tree.FindPath(path))?.Id;

    public static Risk? RiskAt(this Classification c, string path) => c.EffectiveRisk(c.Tree.FindPath(path));

    /// <summary>仓库根目录（包含 DiskThinner.slnx 的目录）。</summary>
    public static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DiskThinner.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("找不到仓库根目录");
    }
}

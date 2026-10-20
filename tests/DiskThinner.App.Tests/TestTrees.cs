using DiskThinner.Scanner;

namespace DiskThinner.App.Tests;

/// <summary>用"路径 → 大小"列表构造 NodeTree，目录按需自动创建。</summary>
internal static class TestTrees
{
    public static NodeTree Build(params (string Path, long Size)[] files)
    {
        var tree = new NodeTree(64);
        tree.Root = tree.Add(-1, @"C:\", NodeFlags.Directory, 0, 0, 0);
        var dirs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = tree.Root };

        int Dir(string path)
        {
            if (dirs.TryGetValue(path, out var existing))
            {
                return existing;
            }

            var slash = path.LastIndexOf('\\');
            var parent = Dir(slash < 0 ? "" : path[..slash]);
            var index = tree.Add(parent, slash < 0 ? path : path[(slash + 1)..], NodeFlags.Directory, 0, 0, 0);
            dirs[path] = index;
            return index;
        }

        foreach (var (path, size) in files)
        {
            var slash = path.LastIndexOf('\\');
            var parent = Dir(slash < 0 ? "" : path[..slash]);
            tree.Add(parent, slash < 0 ? path : path[(slash + 1)..], NodeFlags.None, size, size, 0);
        }

        tree.Finalize();
        return tree;
    }

    public static NodeTree Sample() => Build(
        (@"Windows\System32\a.dll", 600),
        (@"Windows\System32\b.dll", 200),
        (@"Windows\explorer.exe", 100),
        (@"Users\demo\AppData\Local\uv\cache\x.whl", 400),
        (@"Users\demo\AppData\Local\Temp\t.tmp", 50),
        (@"Users\demo\big.iso", 300),
        (@"pagefile.sys", 350));
}

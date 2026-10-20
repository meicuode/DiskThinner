using DiskThinner.Scanner;

namespace UiBench;

/// <summary>生成指定节点数的模拟文件树：目录越深子目录越少，文件大小呈长尾分布。固定随机种子，结果可复现。</summary>
internal static class SyntheticTree
{
    private static readonly string[] Extensions = [".dll", ".exe", ".log", ".json", ".dat", ".pak", ".cache", ".tmp", ".png", ".js", ".pdb", ".zip"];

    public static NodeTree Generate(int nodeCount, int seed = 42)
    {
        var random = new Random(seed);
        var tree = new NodeTree(nodeCount + 16);
        var now = DateTime.UtcNow.ToFileTimeUtc();
        tree.Root = tree.Add(-1, @"C:\", NodeFlags.Directory, 0, 0, now);

        var queue = new Queue<(int Node, int Depth)>();
        queue.Enqueue((tree.Root, 0));
        Span<char> name = stackalloc char[64];

        while (tree.Count < nodeCount && queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            var subdirs = depth == 0 ? 24 : Math.Max(0, (int)(random.NextDouble() * (9 - Math.Min(depth, 8))) + (depth < 3 ? 2 : 0));
            var files = (int)(Math.Pow(random.NextDouble(), 2) * 40);

            for (var i = 0; i < subdirs && tree.Count < nodeCount; i++)
            {
                var len = Format(name, "folder_", depth * 1000 + i, "");
                var child = tree.Add(dir, name[..len], NodeFlags.Directory, 0, 0, now - random.NextInt64(0, 864_000_000_000L * 365));
                queue.Enqueue((child, depth + 1));
            }

            for (var i = 0; i < files && tree.Count < nodeCount; i++)
            {
                var ext = Extensions[random.Next(Extensions.Length)];
                var len = Format(name, "file_", random.Next(1_000_000), ext);
                // 对数正态分布：大部分几 KB 到几 MB，少数几百 MB
                var size = (long)Math.Exp(random.NextDouble() * 6 + random.NextDouble() * 6 + 4);
                var alloc = (size + 4095) / 4096 * 4096;
                tree.Add(dir, name[..len], NodeFlags.None, size, alloc, now - random.NextInt64(0, 864_000_000_000L * 365));
            }

            // 队列空了但节点数还不够：从根重新开始继续往下长
            if (queue.Count == 0 && tree.Count < nodeCount)
            {
                queue.Enqueue((tree.Root, 1));
            }
        }

        tree.Finalize();
        return tree;
    }

    private static int Format(Span<char> buffer, string prefix, int number, string suffix)
    {
        prefix.AsSpan().CopyTo(buffer);
        number.TryFormat(buffer[prefix.Length..], out var written);
        suffix.AsSpan().CopyTo(buffer[(prefix.Length + written)..]);
        return prefix.Length + written + suffix.Length;
    }
}

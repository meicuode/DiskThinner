// 阶段 0.3：虚拟 ListView 树与方块图的性能验证。
//
//   UiBench                          交互模式，130 万个模拟节点
//   UiBench --nodes 2000000          指定节点数
//   UiBench --scan C:\Users\demo     用真实目录（遍历扫描）代替模拟数据，检查方块图的实际观感
//   UiBench --bench --out <文件>     自动执行测量脚本，结果写入文件后退出（同目录下保存截图）

using System.Diagnostics;
using System.Globalization;
using UiBench;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var nodes = Option(args, "--nodes") is { } n ? int.Parse(n, CultureInfo.InvariantCulture) : 1_300_000;
        var bench = args.Contains("--bench");
        var output = bench ? Option(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, "uibench-results.txt") : null;

        var results = new List<string>();
        var sw = Stopwatch.StartNew();
        var scan = Option(args, "--scan");
        var tree = scan is null ? SyntheticTree.Generate(nodes) : DiskThinner.Scanner.Walk.WalkScanner.Scan(scan).Tree;
        results.Add($"source={scan ?? "synthetic"}");
        results.Add($"nodes={tree.Count}");
        results.Add($"generate_ms={sw.Elapsed.TotalMilliseconds:F0}");
        results.Add($"root_alloc_bytes={tree[tree.Root].AllocSize}");

        if (output is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        }

        Application.Run(new BenchForm(tree, output, results));
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}

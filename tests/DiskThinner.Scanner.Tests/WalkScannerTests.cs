using System.Diagnostics;
using DiskThinner.Scanner.Walk;

namespace DiskThinner.Scanner.Tests;

public sealed class WalkScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dt-walk-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "dt-outside-" + Guid.NewGuid().ToString("N"));

    public WalkScannerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllBytes(Path.Combine(_root, "top.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "a", "one.bin"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(_root, "a", "b", "two.bin"), new byte[2000]);

        // junction 指向测试目录之外的大文件，扫描时不应进入
        Directory.CreateDirectory(_outside);
        File.WriteAllBytes(Path.Combine(_outside, "huge.bin"), new byte[500_000]);
        RunCmd($"mklink /J \"{Path.Combine(_root, "link")}\" \"{_outside}\"");
    }

    [Fact]
    public void SumsSizesAndDoesNotFollowJunctions()
    {
        var (tree, stats) = WalkScanner.Scan(_root);
        var root = tree[tree.Root];

        Assert.Equal(6000, root.LogicalSize);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(3, root.DirCount); // a、a\b、link（link 本身算目录但不展开）
        Assert.Equal(1, stats.ReparsePointDirectories);

        var link = tree.FindPath(Path.Combine(_root, "link"));
        Assert.True((tree[link].Flags & NodeFlags.ReparsePoint) != 0);
        Assert.Equal(-1, tree[link].FirstChild);
    }

    [Fact]
    public void AllocationIsRoundedToClusters()
    {
        var (tree, stats) = WalkScanner.Scan(_root);
        var one = tree.FindPath(Path.Combine(_root, "a", "one.bin"));
        Assert.Equal(stats.ClusterSize, tree[one].AllocSize);
    }

    [Fact]
    public void PathsRoundTrip()
    {
        var (tree, _) = WalkScanner.Scan(_root);
        var two = tree.FindPath(Path.Combine(_root, "a", "b", "two.bin"));
        Assert.Equal(Path.Combine(_root, "a", "b", "two.bin"), tree.GetPath(two), ignoreCase: true);
    }

    public void Dispose()
    {
        var link = Path.Combine(_root, "link");
        if (Directory.Exists(link))
        {
            Directory.Delete(link); // 只删除 junction 本身
        }

        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private static void RunCmd(string arguments)
    {
        // cmd 自己解析命令行，不能用 ArgumentList（会把内部引号转义成 \"）
        var psi = new ProcessStartInfo("cmd.exe", "/c " + arguments) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("mklink 失败");
        }
    }
}

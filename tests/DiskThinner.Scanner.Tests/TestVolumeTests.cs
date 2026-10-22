using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DiskThinner.Scanner.Mft;
using DiskThinner.Scanner.Native;
using DiskThinner.Scanner.Walk;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace DiskThinner.Scanner.Tests;

/// <summary>
/// 只在 VHDX 测试盘上以管理员运行（tools/verify/scan-elevated.ps1 挂上卷标为 DTTEST 的盘，并设置 DT_SCAN_TEST_VOLUME）；
/// 平时和 CI 上跳过。
/// </summary>
public sealed class TestVolumeFactAttribute : FactAttribute
{
    public TestVolumeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TestVolume.Variable)))
        {
            Skip = $"没有设置 {TestVolume.Variable}：要在 VHDX 测试盘上以管理员运行，见 tools/verify/scan-elevated.ps1";
        }
        else if (!VolumeScanner.IsElevated())
        {
            Skip = "MFT 扫描需要管理员权限";
        }
    }
}

/// <summary>
/// 在测试盘上造出真实的 NTFS 情况：常驻的小文件、压缩、稀疏、备用数据流（文件和目录上的）、硬链接、junction、
/// 1000 层嵌套和 255 个字符的文件名。造完刷新整个卷，再分别做一次 MFT 扫描（整个盘）和遍历扫描（测试目录）。
/// </summary>
public sealed partial class TestVolume : IDisposable
{
    public const string Variable = "DT_SCAN_TEST_VOLUME";
    public const string Label = "DTTEST";
    public const int Depth = 1000;
    public const long Kb = 1024;
    public const long Mb = 1024 * Kb;

    /// <summary>255 个字符，NTFS 单个名字的上限。</summary>
    public static readonly string LongName = new string('n', 251) + ".bin";

    /// <summary>深层目录相对测试目录的路径：deep\d000\d001\…\d999。</summary>
    public static readonly string DeepDir = "deep" + string.Concat(Enumerable.Range(0, Depth).Select(i => @"\d" + i.ToString("D3", CultureInfo.InvariantCulture)));

    public string Root { get; } = "";
    public ScanResult Mft { get; } = null!;
    public ScanResult Walk { get; } = null!;
    /// <summary>压缩文件实际占用的空间（GetCompressedFileSize）。</summary>
    public long CompressedOnDisk { get; private set; }
    /// <summary>稀疏文件实际占用的空间（GetCompressedFileSize）。</summary>
    public long SparseOnDisk { get; private set; }

    public TestVolume()
    {
        var volume = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrEmpty(volume) || !VolumeScanner.IsElevated())
        {
            return; // 测试会被跳过
        }

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(volume))!);
        if (!string.Equals(drive.VolumeLabel, Label, StringComparison.Ordinal) || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{Variable} 必须是卷标为 {Label} 的 NTFS 测试盘：{volume}（卷标 \"{drive.VolumeLabel}\"，{drive.DriveFormat}）");
        }

        Root = Path.Combine(drive.RootDirectory.FullName, "dt-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
        Build();

        // MFT 扫描直接读卷：先把所有元数据写到盘上
        var letter = drive.Name.TrimEnd('\\');
        FlushVolume(letter);
        Mft = MftScanner.Scan(letter);
        Walk = WalkScanner.Scan(Root);
    }

    public string P(string relative) => Path.Combine(Root, relative);

    private void Build()
    {
        var random = new Random(20261022);
        Write("plain.bin", Bytes(random, Mb + 123));
        Write("tiny.txt", Bytes(random, 100)); // 常驻在 MFT 记录里，不占簇

        // 压缩：先在空文件上开启压缩再写入；内容重复，能压到很小但不是 0
        var compressed = P("compressed.txt");
        using (var fs = new FileStream(compressed, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            SetCompression(fs.SafeFileHandle);
            var text = new StringBuilder();
            for (var i = 0; text.Length < 4 * Mb; i++)
            {
                text.Append("DiskThinner compressed line ").Append(i % 1000).Append("\r\n");
            }

            fs.Write(Encoding.ASCII.GetBytes(text.ToString(0, (int)(4 * Mb))));
            fs.Flush(flushToDisk: true);
        }

        CompressedOnDisk = OnDiskSize(compressed);

        // 稀疏：64 MB 的文件只在 16 MB 处写了 1 MB
        var sparse = P("sparse.bin");
        using (var fs = new FileStream(sparse, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            SetSparse(fs.SafeFileHandle);
            fs.SetLength(64 * Mb);
            fs.Position = 16 * Mb;
            fs.Write(Bytes(random, Mb));
            fs.Flush(flushToDisk: true);
        }

        SparseOnDisk = OnDiskSize(sparse);

        // 备用数据流：文件上一个 3 MB 的、一个 50 字节（常驻）的；目录上一个 2 MB 的
        Write("ads.bin", Bytes(random, 256 * Kb));
        Write("ads.bin:extra", Bytes(random, 3 * Mb));
        Write("ads.bin:small", Bytes(random, 50));
        Directory.CreateDirectory(P("ads-dir"));
        Write(@"ads-dir\inside.bin", Bytes(random, 64 * Kb));
        Write("ads-dir:dirstream", Bytes(random, 2 * Mb));

        // 硬链接：同一个文件两个名字
        Directory.CreateDirectory(P("links"));
        Write(@"links\a.bin", Bytes(random, 512 * Kb));
        if (!CreateHardLink(P(@"links\b.bin"), P(@"links\a.bin"), 0))
        {
            throw new IOException("创建硬链接失败：" + Marshal.GetLastPInvokeError());
        }

        // junction 指向测试目录里的另一个目录：两种扫描都不进入，目标只算一次
        Directory.CreateDirectory(P("target"));
        Write(@"target\t.bin", Bytes(random, 128 * Kb));
        Cmd($"mklink /J \"{P("jn")}\" \"{P("target")}\"");

        // 1000 层嵌套（路径约 5000 个字符），最深处一个普通文件和一个 255 个字符的文件名
        Directory.CreateDirectory(P(DeepDir));
        Write(Path.Combine(DeepDir, "deep.bin"), Bytes(random, 40 * Kb));
        Write(Path.Combine(DeepDir, LongName), Bytes(random, 8 * Kb));
    }

    private void Write(string relative, byte[] data)
    {
        using var fs = new FileStream(P(relative), FileMode.CreateNew, FileAccess.Write);
        fs.Write(data);
        fs.Flush(flushToDisk: true);
    }

    private static byte[] Bytes(Random random, long length)
    {
        var data = new byte[length];
        random.NextBytes(data);
        return data;
    }

    private static unsafe void SetCompression(SafeFileHandle handle)
    {
        const uint fsctlSetCompression = 0x9C040;
        ushort format = 1; // COMPRESSION_FORMAT_DEFAULT
        if (!Win32.DeviceIoControl(handle, fsctlSetCompression, &format, sizeof(ushort), null, 0, out _, 0))
        {
            throw new IOException("开启压缩失败：" + Marshal.GetLastPInvokeError());
        }
    }

    private static unsafe void SetSparse(SafeFileHandle handle)
    {
        const uint fsctlSetSparse = 0x900C4;
        if (!Win32.DeviceIoControl(handle, fsctlSetSparse, null, 0, null, 0, out _, 0))
        {
            throw new IOException("设置稀疏失败：" + Marshal.GetLastPInvokeError());
        }
    }

    private static long OnDiskSize(string path)
    {
        var low = GetCompressedFileSize(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0)
        {
            throw new IOException("GetCompressedFileSize 失败：" + Marshal.GetLastPInvokeError());
        }

        return ((long)high << 32) | low;
    }

    private static void FlushVolume(string letter)
    {
        const uint genericWrite = 0x40000000;
        using var handle = Win32.CreateFile(@"\\.\" + letter, Win32.GenericRead | genericWrite, Win32.FileShareRead | Win32.FileShareWrite,
            0, Win32.OpenExisting, 0, 0);
        if (handle.IsInvalid || !FlushFileBuffers(handle))
        {
            throw new IOException("刷新卷失败：" + Marshal.GetLastPInvokeError());
        }
    }

    private static void Cmd(string arguments)
    {
        // cmd 自己解析命令行，不能用 ArgumentList（会把内部引号转义成 \"）
        var psi = new ProcessStartInfo("cmd.exe", "/c " + arguments) { UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException("命令失败：" + arguments);
        }
    }

    public void Dispose()
    {
        if (Root.Length == 0 || !Directory.Exists(Root))
        {
            return;
        }

        if (Directory.Exists(P("jn")))
        {
            Directory.Delete(P("jn")); // 只删 junction 本身
        }

        Directory.Delete(Root, recursive: true);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);
}

/// <summary>
/// 测试盘上的扫描结果：MFT 扫描、遍历扫描和期望值逐项比对（研发方案第 6 节集成测试）。
/// 期望值来自构造时写入的字节数，压缩和稀疏文件的实际占用取 GetCompressedFileSize。
/// </summary>
public sealed class TestVolumeTests(TestVolume v, ITestOutputHelper output) : IClassFixture<TestVolume>
{
    private const long Kb = TestVolume.Kb;
    private const long Mb = TestVolume.Mb;

    private long Cluster => v.Mft.Stats.ClusterSize;

    private long Round(long bytes) => (bytes + Cluster - 1) / Cluster * Cluster;

    private (Node Mft, Node Walk) Nodes(string relative)
    {
        var path = v.P(relative);
        var m = v.Mft.Tree.FindPath(path);
        var w = v.Walk.Tree.FindPath(path);
        Assert.True(m >= 0, "MFT 扫描里没有 " + relative);
        Assert.True(w >= 0, "遍历扫描里没有 " + relative);
        return (v.Mft.Tree[m], v.Walk.Tree[w]);
    }

    /// <summary>每个文件：逻辑大小、MFT 的实际占用、遍历扫描的估算占用。</summary>
    private Dictionary<string, (long Logical, long MftAlloc, long WalkAlloc)> Expected() => new()
    {
        ["plain.bin"] = (Mb + 123, Round(Mb + 123), Round(Mb + 123)),
        ["tiny.txt"] = (100, 0, Cluster),
        ["compressed.txt"] = (4 * Mb, v.CompressedOnDisk, 4 * Mb),
        ["sparse.bin"] = (64 * Mb, Mb, 64 * Mb),
        ["ads.bin"] = (256 * Kb, 256 * Kb + 3 * Mb, 256 * Kb),
        [@"ads-dir\inside.bin"] = (64 * Kb, 64 * Kb, 64 * Kb),
        [@"links\a.bin"] = (512 * Kb, 512 * Kb, 512 * Kb),
        [@"links\b.bin"] = (512 * Kb, 512 * Kb, 512 * Kb),
        [@"target\t.bin"] = (128 * Kb, 128 * Kb, 128 * Kb),
        [Path.Combine(TestVolume.DeepDir, "deep.bin")] = (40 * Kb, 40 * Kb, 40 * Kb),
        [Path.Combine(TestVolume.DeepDir, TestVolume.LongName)] = (8 * Kb, 8 * Kb, 8 * Kb),
    };

    [TestVolumeFact]
    public void EveryFileMatchesTheExpectedSizes()
    {
        var failures = new List<string>();
        var s = v.Mft.Stats;
        output.WriteLine($"簇 {s.ClusterSize}，MFT 记录 {s.MftRecordsInUse:N0}，备用数据流合计 {s.AlternateStreamBytes:N0}，MFT 扫描 {s.TotalDuration.TotalMilliseconds:N0} ms，遍历 {v.Walk.Stats.TotalDuration.TotalMilliseconds:N0} ms");
        output.WriteLine("文件\t逻辑大小\tMFT 占用\t遍历占用");
        foreach (var (relative, e) in Expected())
        {
            var (m, w) = Nodes(relative);
            var name = relative.Length > 60 ? "…" + relative[^40..] : relative;
            output.WriteLine($"{name}\t{m.LogicalSize:N0}\t{m.AllocSize:N0}\t{w.AllocSize:N0}");
            Check($"{name} MFT 逻辑大小", e.Logical, m.LogicalSize);
            Check($"{name} MFT 占用", e.MftAlloc, m.AllocSize);
            Check($"{name} 遍历 逻辑大小", e.Logical, w.LogicalSize);
            Check($"{name} 遍历 占用", e.WalkAlloc, w.AllocSize);
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));

        void Check(string what, long expected, long actual)
        {
            if (expected != actual)
            {
                failures.Add($"{what}：期望 {expected:N0}，实际 {actual:N0}");
            }
        }
    }

    [TestVolumeFact]
    public void CompressedFileUsesItsOnDiskSize()
    {
        var (m, w) = Nodes("compressed.txt");
        Assert.InRange(v.CompressedOnDisk, Cluster, 2 * Mb); // 确实压缩了
        Assert.Equal(v.CompressedOnDisk, m.AllocSize);
        Assert.True((m.Flags & NodeFlags.Compressed) != 0);
        Assert.True((w.Flags & NodeFlags.Compressed) != 0);
        Assert.Equal(4 * Mb, w.AllocSize); // 遍历扫描只能按逻辑大小估算（已知局限）
    }

    [TestVolumeFact]
    public void SparseFileCountsOnlyTheWrittenRange()
    {
        var (m, w) = Nodes("sparse.bin");
        Assert.Equal(Mb, v.SparseOnDisk);
        Assert.Equal(Mb, m.AllocSize);
        Assert.Equal(64 * Mb, m.LogicalSize);
        Assert.True((m.Flags & NodeFlags.Sparse) != 0);
        Assert.True((w.Flags & NodeFlags.Sparse) != 0);
    }

    [TestVolumeFact]
    public void AlternateStreamsCountAsAllocationInMftOnly()
    {
        var (m, w) = Nodes("ads.bin");
        Assert.Equal(256 * Kb, m.LogicalSize);
        Assert.Equal(256 * Kb + 3 * Mb, m.AllocSize); // 50 字节的流常驻在记录里，不占簇
        Assert.Equal(256 * Kb, w.AllocSize);          // 遍历扫描看不到备用数据流（已知局限）
        Assert.True(v.Mft.Stats.AlternateStreamBytes >= 3 * Mb + 50 + 2 * Mb);

        // 已知局限：目录上的备用数据流只计入 AlternateStreamBytes，不计入目录的占用
        var (dir, _) = Nodes("ads-dir");
        Assert.Equal(64 * Kb, dir.AllocSize);
    }

    [TestVolumeFact]
    public void HardLinkIsCountedOnceByMftAndTwiceByWalk()
    {
        var (a, _) = Nodes(@"links\a.bin");
        var (b, _) = Nodes(@"links\b.bin");
        Assert.True((a.Flags & b.Flags & NodeFlags.HardLinked) != 0);
        Assert.True(((a.Flags ^ b.Flags) & NodeFlags.HardLinkSecondary) != 0, "两个名字里恰好一个是次要名字");

        var (mDir, wDir) = Nodes("links");
        Assert.Equal(512 * Kb, mDir.AllocSize);
        Assert.Equal(1024 * Kb, wDir.AllocSize);
        Assert.Equal(2, mDir.FileCount);
        Assert.Equal(2, wDir.FileCount);
    }

    [TestVolumeFact]
    public void JunctionIsNotFollowed()
    {
        var (m, w) = Nodes("jn");
        Assert.True((m.Flags & NodeFlags.ReparsePoint) != 0);
        Assert.True((w.Flags & NodeFlags.ReparsePoint) != 0);
        Assert.Equal(-1, m.FirstChild);
        Assert.Equal(-1, w.FirstChild);
        Assert.Equal(0, m.AllocSize);
        Assert.Equal(0, w.AllocSize);
    }

    [TestVolumeFact]
    public void DeepNestingAndLongNamesRoundTrip()
    {
        var file = v.P(Path.Combine(TestVolume.DeepDir, TestVolume.LongName));
        Assert.True(file.Length > 5000, "路径长度 " + file.Length);
        foreach (var (label, tree) in new[] { ("MFT", v.Mft.Tree), ("遍历", v.Walk.Tree) })
        {
            var node = tree.FindPath(file);
            Assert.True(node >= 0, label + " 找不到最深处的文件");
            Assert.Equal(file, tree.GetPath(node), ignoreCase: true);
            Assert.Equal(TestVolume.LongName, tree.GetNameString(node));

            // 1000 层目录逐层汇总：最上层的 deep 目录 = 最深处两个文件之和
            var deep = tree.FindPath(v.P("deep"));
            Assert.Equal(48 * Kb, tree[deep].AllocSize);
            Assert.Equal(2, tree[deep].FileCount);
            Assert.Equal(TestVolume.Depth, tree[deep].DirCount);
            Assert.Equal(tree.GetDepth(deep) + TestVolume.Depth + 1, tree.GetDepth(node));
        }
    }

    [TestVolumeFact]
    public void BothScannersSeeTheSameTree()
    {
        var mft = v.Mft.Tree;
        var walk = v.Walk.Tree;
        var mRoot = mft.FindPath(v.Root);
        Assert.True(mRoot >= 0);

        var failures = new List<string>();
        var stack = new Stack<int>([walk.Root]);
        var visited = 0;
        while (stack.Count > 0)
        {
            var w = stack.Pop();
            visited++;
            var path = walk.GetPath(w);
            var m = mft.FindPath(path);
            if (m < 0)
            {
                failures.Add("MFT 里没有 " + path);
                continue;
            }

            if (mft[m].IsDirectory != walk[w].IsDirectory || (!walk[w].IsDirectory && mft[m].LogicalSize != walk[w].LogicalSize))
            {
                failures.Add($"{path}：MFT {mft[m].IsDirectory}/{mft[m].LogicalSize}，遍历 {walk[w].IsDirectory}/{walk[w].LogicalSize}");
            }

            foreach (var c in walk.Children(w))
            {
                stack.Push(c);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        Assert.Equal(mft[mRoot].FileCount, walk[walk.Root].FileCount);
        Assert.Equal(mft[mRoot].DirCount, walk[walk.Root].DirCount);
        Assert.Equal(mft[mRoot].FileCount + mft[mRoot].DirCount + 1, visited);

        // 合计：MFT 的硬链接只算一次，遍历扫描两个名字都算
        var e = Expected();
        Assert.Equal(e.Sum(x => x.Value.MftAlloc) - e[@"links\b.bin"].MftAlloc, mft[mRoot].AllocSize);
        Assert.Equal(e.Sum(x => x.Value.WalkAlloc), walk[walk.Root].AllocSize);
        Assert.Equal(e.Sum(x => x.Value.Logical) - e[@"links\b.bin"].Logical, mft[mRoot].LogicalSize);
        Assert.Equal(e.Sum(x => x.Value.Logical), walk[walk.Root].LogicalSize);
    }
}

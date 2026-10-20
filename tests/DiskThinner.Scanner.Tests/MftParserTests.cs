using System.Buffers.Binary;
using DiskThinner.Scanner.Mft;

namespace DiskThinner.Scanner.Tests;

public class DataRunsTests
{
    [Fact]
    public void DecodesSingleRun()
    {
        var runs = DataRuns.Decode([0x21, 0x18, 0x34, 0x56, 0x00]);
        Assert.Equal([new DataRun(0, 0x5634, 0x18)], runs);
    }

    [Fact]
    public void OffsetsAreRelativeAndSigned()
    {
        // 第一段：16 簇，从 LCN 0x1000 开始；第二段：8 簇，偏移 -16
        var runs = DataRuns.Decode([0x21, 0x10, 0x00, 0x10, 0x11, 0x08, 0xF0, 0x00]);
        Assert.Equal(2, runs.Count);
        Assert.Equal(new DataRun(0, 0x1000, 0x10), runs[0]);
        Assert.Equal(new DataRun(0x10, 0x0FF0, 0x08), runs[1]);
    }

    [Fact]
    public void SparseRunDoesNotMoveBaseLcn()
    {
        var runs = DataRuns.Decode([0x11, 0x04, 0x20, 0x01, 0x10, 0x11, 0x02, 0x10, 0x00]);
        Assert.Equal(new DataRun(0, 0x20, 4), runs[0]);
        Assert.Equal(new DataRun(4, -1, 0x10), runs[1]);
        Assert.Equal(new DataRun(0x14, 0x30, 2), runs[2]);
    }

    [Fact]
    public void RejectsMalformedHeader()
    {
        Assert.Throws<InvalidDataException>(() => DataRuns.Decode([0x10, 0x05, 0x00]));
        Assert.Equal(-1, DataRuns.CountAllocatedClusters([0x10, 0x05, 0x00]));
    }

    [Fact]
    public void CountsOnlyAllocatedClusters()
    {
        // 4 个分配簇 + 16 个稀疏簇 + 2 个分配簇
        Assert.Equal(6, DataRuns.CountAllocatedClusters([0x11, 0x04, 0x20, 0x01, 0x10, 0x11, 0x02, 0x10, 0x00]));
    }
}

public class FixupTests
{
    [Fact]
    public void RestoresSectorTails()
    {
        // 常驻数据跨过第一个 512 字节段的末尾
        var rec = RecordBuilder.File().StandardInfo().ResidentData(800).Build();
        Assert.Equal(0xABCD, BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(510)));

        Assert.True(MftRecord.ApplyFixup(rec));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(510)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(1022)));
    }

    [Fact]
    public void DetectsTornWrite()
    {
        var rec = RecordBuilder.File().StandardInfo().Build();
        rec[1022] ^= 0xFF; // 第二段的序列号不一致，相当于记录只写了一半
        Assert.False(MftRecord.ApplyFixup(rec));
    }

    [Fact]
    public void RejectsMissingSignature()
    {
        Assert.False(MftRecord.ApplyFixup(new byte[1024]));
    }
}

public class MftTablesTests
{
    private const long Root = 5;
    private const long Users = 30;

    private static NodeTree BuildSample(out MftTables tables, bool countHardLinksOnce = true)
    {
        var t = new MftTables(64);
        tables = t;
        void Put(int record, byte[] data) => t.Parse(data, record);

        Put(5, RecordBuilder.Directory().StandardInfo().FileName(Root, ".", 3).Build());
        Put(30, RecordBuilder.Directory().StandardInfo().FileName(Root, "Users", 3).Build());

        // 长名 + 8.3 短名：只应出现长名
        Put(31, RecordBuilder.File().StandardInfo()
            .FileName(Users, "a-long-name.txt", 1).FileName(Users, "A-LONG~1.TXT", 2)
            .NonResidentData(10_000, 12_288).Build());

        // 常驻小文件：不占簇
        Put(32, RecordBuilder.File().StandardInfo().FileName(Users, "small.txt", 3).ResidentData(100).Build());

        // 两个硬链接名，分别在 Users 和根目录下
        Put(33, RecordBuilder.File(linkCount: 2).StandardInfo()
            .FileName(Users, "link1.dll", 3).FileName(Root, "link2.dll", 3)
            .NonResidentData(5_000, 8_192).Build());

        // 数据属性放在扩展记录 34 里，基础记录是 35
        Put(34, RecordBuilder.Extension(35).NonResidentData(1_000_000_000, 1_000_013_824).Build());
        Put(35, RecordBuilder.File().StandardInfo().FileName(Users, "big.bin", 3).Build());

        // 压缩文件：数据段里只有 16 个簇真正分配，其余是稀疏段
        Put(36, RecordBuilder.File().StandardInfo(attributes: 0x800).FileName(Users, "packed.log", 3)
            .NonResidentData(400_000, 409_600, attrFlags: 0x0001, compressedSize: 65_536, runs: RecordBuilder.Runs(16, 84)).Build());

        // 类似 $BadClus:$Bad 的稀疏命名流：头部声称分配了 1 TB，实际一个簇都没有
        Put(40, RecordBuilder.File().StandardInfo().FileName(Root, "sparse.bin", 3)
            .ResidentData(0).NonResidentData(1L << 40, 1L << 40, streamName: "Bad", runs: RecordBuilder.Runs(0, (1L << 40) / 4096)).Build());

        // 数据分成两个片段，第二个片段在扩展记录 42 里（基础记录 41）
        Put(41, RecordBuilder.File().StandardInfo().FileName(Users, "fragmented.vhdx", 3)
            .NonResidentData(81_920, 81_920, runs: RecordBuilder.Runs(12)).Build());
        Put(42, RecordBuilder.Extension(41).NonResidentData(0, 0, lowestVcn: 12, runs: RecordBuilder.Runs(8)).Build());

        // 备用数据流不计入逻辑大小
        Put(37, RecordBuilder.File().StandardInfo().FileName(Users, "ads.txt", 3)
            .ResidentData(10).NonResidentData(50_000, 53_248, streamName: "Zone").Build());

        // 父目录不存在：孤立记录
        Put(38, RecordBuilder.File().StandardInfo().FileName(50, "orphan.tmp", 3).ResidentData(1).Build());

        // 未使用的记录
        Put(39, new RecordBuilder(flags: 0).StandardInfo().FileName(Users, "deleted.txt", 3).Build());

        // NTFS 元数据：$MFT
        Put(0, RecordBuilder.File().StandardInfo().FileName(Root, "$MFT", 3).NonResidentData(1_048_576, 1_048_576).Build());

        var tree = t.BuildTree(@"C:\", out _);
        tree.Finalize(countHardLinksOnce);
        tree.PropagateFlagToDescendants(NodeFlags.Metadata);
        return tree;
    }

    [Fact]
    public void BuildsTreeWithRootNameAndChildren()
    {
        var tree = BuildSample(out _);
        Assert.Equal(@"C:\", tree.GetNameString(tree.Root));

        var users = tree.FindPath(@"C:\Users");
        Assert.True(users >= 0);
        Assert.True(tree[users].IsDirectory);
        Assert.True(tree.FindPath(@"C:\Users\a-long-name.txt") >= 0);
        Assert.Equal(-1, tree.FindPath(@"C:\Users\A-LONG~1.TXT"));
        Assert.Equal(-1, tree.FindPath(@"C:\Users\deleted.txt"));
    }

    [Fact]
    public void ResidentDataHasNoAllocation()
    {
        var tree = BuildSample(out _);
        var small = tree.FindPath(@"C:\Users\small.txt");
        Assert.Equal(100, tree[small].LogicalSize);
        Assert.Equal(0, tree[small].AllocSize);
    }

    [Fact]
    public void ExtensionRecordDataBelongsToBaseRecord()
    {
        var tree = BuildSample(out _);
        var big = tree.FindPath(@"C:\Users\big.bin");
        Assert.Equal(1_000_000_000, tree[big].LogicalSize);
        Assert.Equal(1_000_013_824, tree[big].AllocSize);
    }

    [Fact]
    public void CompressedFileUsesCompressedSize()
    {
        var tree = BuildSample(out _);
        var packed = tree.FindPath(@"C:\Users\packed.log");
        Assert.Equal(400_000, tree[packed].LogicalSize);
        Assert.Equal(65_536, tree[packed].AllocSize);
        Assert.True((tree[packed].Flags & NodeFlags.Compressed) != 0);
    }

    [Fact]
    public void SparseStreamOccupiesNothing()
    {
        var tree = BuildSample(out _);
        var sparse = tree.FindPath(@"C:\sparse.bin");
        Assert.Equal(0, tree[sparse].AllocSize);
    }

    [Fact]
    public void FragmentedStreamSumsAllSegments()
    {
        var tree = BuildSample(out _);
        var vhdx = tree.FindPath(@"C:\Users\fragmented.vhdx");
        Assert.Equal(81_920, tree[vhdx].LogicalSize);
        Assert.Equal(20 * 4096, tree[vhdx].AllocSize);
    }

    [Fact]
    public void AlternateStreamCountsAllocationButNotLogicalSize()
    {
        var tree = BuildSample(out var tables);
        var ads = tree.FindPath(@"C:\Users\ads.txt");
        Assert.Equal(10, tree[ads].LogicalSize);
        Assert.Equal(53_248, tree[ads].AllocSize);
        Assert.Equal(50_000 + (1L << 40), tables.AlternateStreamBytes); // 含 sparse.bin 的稀疏命名流（逻辑大小）
    }

    [Fact]
    public void HardLinkIsCountedOnceByDefault()
    {
        var tree = BuildSample(out _);
        var link1 = tree.FindPath(@"C:\Users\link1.dll");
        var link2 = tree.FindPath(@"C:\link2.dll");
        Assert.True(link1 >= 0 && link2 >= 0);

        var secondary = (tree[link1].Flags & NodeFlags.HardLinkSecondary) != 0 ? link1 : link2;
        var primary = secondary == link1 ? link2 : link1;
        Assert.True((tree[primary].Flags & NodeFlags.HardLinkSecondary) == 0);

        // 根目录总量只含一份 5,000 字节
        long expected = 10_000 + 100 + 5_000 + 1_000_000_000 + 400_000 + 10 + 1_048_576 + 81_920;
        Assert.Equal(expected, tree[tree.Root].LogicalSize);
    }

    [Fact]
    public void BothHardLinkNamesAreMarked()
    {
        var tree = BuildSample(out _);
        Assert.True((tree[tree.FindPath(@"C:\Users\link1.dll")].Flags & NodeFlags.HardLinked) != 0);
        Assert.True((tree[tree.FindPath(@"C:\link2.dll")].Flags & NodeFlags.HardLinked) != 0);
        Assert.True((tree[tree.FindPath(@"C:\Users\big.bin")].Flags & NodeFlags.HardLinked) == 0);
    }

    /// <summary>同一文件在 Windows\WinSxS 和 Windows\System32 下各有一个名字，WinSxS 那个排在前面。</summary>
    private static NodeTree BuildComponentStoreSample(HardLinkAttribution attribution)
    {
        var t = new MftTables(64);
        t.Parse(RecordBuilder.Directory().StandardInfo().FileName(5, ".", 3).Build(), 5);
        t.Parse(RecordBuilder.Directory().StandardInfo().FileName(5, "Windows", 3).Build(), 30);
        t.Parse(RecordBuilder.Directory().StandardInfo().FileName(30, "WinSxS", 3).Build(), 31);
        t.Parse(RecordBuilder.Directory().StandardInfo().FileName(30, "System32", 3).Build(), 32);
        t.Parse(RecordBuilder.File(linkCount: 2).StandardInfo()
            .FileName(31, "ntdll.dll", 3).FileName(32, "ntdll.dll", 3)
            .NonResidentData(40_960, 40_960).Build(), 33);
        var tree = t.BuildTree(@"C:\", out _, attribution);
        tree.Finalize();
        return tree;
    }

    [Theory]
    [InlineData(HardLinkAttribution.FirstName, @"C:\Windows\WinSxS")]
    [InlineData(HardLinkAttribution.LastName, @"C:\Windows\System32")]
    [InlineData(HardLinkAttribution.LowestParentRecord, @"C:\Windows\WinSxS")]
    [InlineData(HardLinkAttribution.OutsideComponentStore, @"C:\Windows\System32")]
    public void HardLinkAttributionChoosesDirectory(HardLinkAttribution attribution, string expectedOwner)
    {
        var tree = BuildComponentStoreSample(attribution);
        var other = expectedOwner.EndsWith("WinSxS", StringComparison.Ordinal) ? @"C:\Windows\System32" : @"C:\Windows\WinSxS";
        Assert.Equal(40_960, tree[tree.FindPath(expectedOwner)].AllocSize);
        Assert.Equal(0, tree[tree.FindPath(other)].AllocSize);
        Assert.Equal(40_960, tree[tree.Root].AllocSize); // 总量不受归属方式影响
    }

    [Fact]
    public void HardLinkCanBeCountedPerName()
    {
        var tree = BuildSample(out _, countHardLinksOnce: false);
        long expected = 10_000 + 100 + 5_000 * 2 + 1_000_000_000 + 400_000 + 10 + 1_048_576 + 81_920;
        Assert.Equal(expected, tree[tree.Root].LogicalSize);
    }

    [Fact]
    public void CountsFilesAndDirectories()
    {
        var tree = BuildSample(out _);
        var root = tree[tree.Root];
        Assert.Equal(1, root.DirCount);   // Users
        Assert.Equal(10, root.FileCount); // Users 下 7 个 + link2.dll + sparse.bin + $MFT（孤立记录不可达）
    }

    [Fact]
    public void TrimExcessKeepsNamesAndSizes()
    {
        var tree = BuildSample(out _);
        var before = tree.GetPath(tree.FindPath(@"C:\Users\big.bin"));
        var rootAlloc = tree[tree.Root].AllocSize;
        tree.TrimExcess();
        Assert.Equal(before, tree.GetPath(tree.FindPath(@"C:\Users\big.bin")));
        Assert.Equal(rootAlloc, tree[tree.Root].AllocSize);
        Assert.Equal(@"C:\", tree.GetNameString(tree.Root));
    }

    [Fact]
    public void OrphanIsUnreachable()
    {
        var tree = BuildSample(out _);
        Assert.Equal(1, tree.UnreachableCount);
    }

    [Fact]
    public void MetadataIsFlagged()
    {
        var tree = BuildSample(out _);
        var mft = tree.FindPath(@"C:\$MFT");
        Assert.True((tree[mft].Flags & NodeFlags.Metadata) != 0);
        Assert.True((tree[tree.FindPath(@"C:\Users")].Flags & NodeFlags.Metadata) == 0);
    }

    [Fact]
    public void ChildrenAreSortedByAllocationDescending()
    {
        var tree = BuildSample(out _);
        var users = tree.FindPath(@"C:\Users");
        var sizes = tree.Children(users).Select(c => tree[c].AllocSize).ToList();
        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
        Assert.Equal("big.bin", tree.GetNameString(tree[users].FirstChild));
    }
}

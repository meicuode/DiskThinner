using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskThinner.Scanner.Native;
using Microsoft.Win32.SafeHandles;

namespace DiskThinner.Scanner.Mft;

/// <summary>
/// 直接读取 NTFS 主文件表（$MFT）构建文件树。需要管理员权限，只支持 NTFS 卷。
/// 流程：取卷参数 → 读记录 0 得到 $MFT 自身的数据段 → 按数据段顺序大块读取全部记录 → 逐条解析 → 建树。
/// </summary>
public static class MftScanner
{
    private const int ChunkBytes = 4 * 1024 * 1024;

    /// <param name="volume">盘符，例如 "C:"。</param>
    public static ScanResult Scan(string volume, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default,
        HardLinkAttribution attribution = HardLinkAttribution.FirstName)
    {
        var drive = NormalizeDrive(volume);
        var beforeScan = Environment.WorkingSet;
        var stopwatch = Stopwatch.StartNew();

        using var handle = Win32.CreateFile(@"\\.\" + drive, Win32.GenericRead,
            Win32.FileShareRead | Win32.FileShareWrite | Win32.FileShareDelete, 0, Win32.OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            throw error == 5
                ? new UnauthorizedAccessException("读取 MFT 需要管理员权限")
                : new Win32Exception(error, $"无法打开卷 {drive}");
        }

        var vd = GetVolumeData(handle);
        var recordSize = (int)vd.BytesPerFileRecordSegment;
        var clusterSize = (int)vd.BytesPerCluster;
        var sectorSize = (int)vd.BytesPerSector;

        var mftRuns = ReadMftRuns(handle, vd, out var mftDataSize, out var segments);
        var recordCount = (int)Math.Min(Math.Min(vd.MftValidDataLength, mftDataSize) / recordSize, int.MaxValue);

        var tables = new MftTables(recordCount, clusterSize);
        var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes + recordSize);
        long bytesRead = 0;
        var recordIndex = 0;
        var carry = 0;

        foreach (var run in mftRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runBytes = run.Length * clusterSize;

            if (run.Lcn < 0)
            {
                // 稀疏段：对应的记录全为 0，直接跳过
                recordIndex += (int)((carry + runBytes) / recordSize);
                carry = 0;
                continue;
            }

            var offset = run.Lcn * clusterSize;
            var remaining = runBytes;
            while (remaining > 0 && recordIndex < recordCount)
            {
                var space = (buffer.Length - carry) / sectorSize * sectorSize;
                var toRead = (int)Math.Min(remaining, Math.Min(space, ChunkBytes));
                Win32.ReadAt(handle, buffer.AsSpan(carry, toRead), offset);
                bytesRead += toRead;
                offset += toRead;
                remaining -= toRead;

                var total = carry + toRead;
                var pos = 0;
                while (pos + recordSize <= total && recordIndex < recordCount)
                {
                    tables.Parse(buffer.AsSpan(pos, recordSize), recordIndex++);
                    pos += recordSize;
                }

                carry = total - pos;
                if (carry > 0)
                {
                    buffer.AsSpan(pos, carry).CopyTo(buffer);
                }

                progress?.Report(new ScanProgress(recordIndex, recordCount, "读取 MFT"));
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var readDuration = stopwatch.Elapsed;
        var afterRead = Environment.WorkingSet;
        var names = tables.NameCount;
        stopwatch.Restart();

        var tree = tables.BuildTree(drive + @"\", out var hardLinks, attribution);
        var afterBuild = Environment.WorkingSet;
        var inUse = tables.InUseRecords;
        var bad = tables.BadRecords;
        var malformed = tables.MalformedRuns;
        var ads = tables.AlternateStreamBytes;
        var dirIndex = tables.DirectoryIndexBytes;
        var nameCapacity = tables.NameCapacity;
        tables = null!;
        buffer = null!;

        // 按记录号累积的临时数组有上百 MB（都在大对象堆上），要等到完整回收才会释放。
        // 在汇总和整理之前主动回收一次，把峰值内存压下来；扫描是一次性操作，这点开销可以接受。
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);

        var buildDuration = stopwatch.Elapsed;
        stopwatch.Restart();

        tree.Finalize(countHardLinksOnce: true);
        tree.PropagateFlagToDescendants(NodeFlags.Metadata);
        tree.TrimExcess();
        var finalizeDuration = stopwatch.Elapsed;
        var afterFinalize = Environment.WorkingSet;

        Win32.GetDiskFreeSpaceEx(drive + @"\", out _, out var totalBytes, out var freeBytes);

        var stats = new ScanStats
        {
            Mode = ScanMode.Mft,
            Root = drive + @"\",
            ReadDuration = readDuration,
            BuildDuration = buildDuration,
            FinalizeDuration = finalizeDuration,
            VolumeTotalBytes = totalBytes,
            VolumeFreeBytes = freeBytes,
            ClusterSize = clusterSize,
            MftRecords = recordCount,
            MftRecordsInUse = inUse,
            MftBadRecords = bad,
            MftMalformedRuns = malformed,
            MftBytesRead = bytesRead,
            MftRecordSize = recordSize,
            MftDataSegments = segments,
            AlternateStreamBytes = ads,
            DirectoryIndexBytes = dirIndex,
            HardLinkSecondaryNames = hardLinks,
            Nodes = tree.Count,
            Unreachable = tree.UnreachableCount,
            NameChars = tree.NameChars,
            NameCharsCapacity = nameCapacity,
            MftNames = names,
            WorkingSetBeforeScan = beforeScan,
            WorkingSetAfterRead = afterRead,
            WorkingSetAfterBuild = afterBuild,
            WorkingSetAfterFinalize = afterFinalize,
        };
        return new ScanResult(tree, stats);
    }

    private static string NormalizeDrive(string volume)
    {
        var v = volume.Trim().TrimEnd('\\');
        if (v.Length != 2 || v[1] != ':' || !char.IsAsciiLetter(v[0]))
        {
            throw new ArgumentException("请指定盘符，例如 C:", nameof(volume));
        }

        return char.ToUpperInvariant(v[0]) + ":";
    }

    private static unsafe Win32.NtfsVolumeData GetVolumeData(SafeFileHandle handle)
    {
        Win32.NtfsVolumeData data;
        if (!Win32.DeviceIoControl(handle, Win32.FsctlGetNtfsVolumeData, null, 0, &data, sizeof(Win32.NtfsVolumeData), out _, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new NotSupportedException($"不是 NTFS 卷，或无法读取卷参数（错误 {error}）");
        }

        return data;
    }

    /// <summary>
    /// 读取 $MFT 自身（记录 0）的数据段列表。$MFT 非常碎片化时，后续片段会放在其他记录里，
    /// 由记录 0 的 $ATTRIBUTE_LIST 指出，这里一并读取。
    /// </summary>
    private static List<DataRun> ReadMftRuns(SafeFileHandle handle, Win32.NtfsVolumeData vd, out long dataSize, out int segmentCount)
    {
        var recordSize = (int)vd.BytesPerFileRecordSegment;
        var clusterSize = (long)vd.BytesPerCluster;
        var record0 = ReadRecordAtOffset(handle, vd.MftStartLcn * clusterSize, recordSize, vd);

        var segments = new List<(long LowestVcn, List<DataRun> Runs)>();
        var extensionRecords = new SortedSet<long>();
        dataSize = 0;

        CollectMftData(record0, segments, ref dataSize);

        foreach (var attr in MftRecord.Attributes(record0))
        {
            if (attr.Type != MftRecord.AttrAttributeList)
            {
                continue;
            }

            if (attr.NonResident)
            {
                throw new NotSupportedException("$MFT 的属性列表是非常驻的，暂不支持，请使用遍历扫描");
            }

            var list = attr.Value;
            var pos = 0;
            while (pos + 0x1A <= list.Length)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(list[pos..]);
                int entryLength = BinaryPrimitives.ReadUInt16LittleEndian(list[(pos + 4)..]);
                if (entryLength < 0x1A)
                {
                    break;
                }

                var nameLength = list[pos + 6];
                var segment = (long)(BinaryPrimitives.ReadUInt64LittleEndian(list[(pos + 0x10)..]) & MftRecord.ReferenceMask);
                if (type == MftRecord.AttrData && nameLength == 0 && segment != 0)
                {
                    extensionRecords.Add(segment);
                }

                pos += entryLength;
            }
        }

        foreach (var record in extensionRecords)
        {
            // 扩展记录本身位于 $MFT 中，用已知的数据段换算出它在卷上的位置
            var known = Merge(segments);
            var vcn = record * recordSize / clusterSize;
            var withinCluster = record * recordSize % clusterSize;
            var run = known.FirstOrDefault(r => r.Lcn >= 0 && vcn >= r.Vcn && vcn < r.Vcn + r.Length);
            if (run.Length == 0)
            {
                throw new InvalidDataException($"无法定位 $MFT 的扩展记录 {record}");
            }

            var offset = (run.Lcn + (vcn - run.Vcn)) * clusterSize + withinCluster;
            var ext = ReadRecordAtOffset(handle, offset, recordSize, vd);
            CollectMftData(ext, segments, ref dataSize);
        }

        segmentCount = segments.Count;
        return Merge(segments);
    }

    private static List<DataRun> Merge(List<(long LowestVcn, List<DataRun> Runs)> segments) =>
        segments.OrderBy(s => s.LowestVcn).SelectMany(s => s.Runs).ToList();

    private static void CollectMftData(byte[] record, List<(long LowestVcn, List<DataRun> Runs)> segments, ref long dataSize)
    {
        foreach (var attr in MftRecord.Attributes(record))
        {
            if (attr.Type == MftRecord.AttrData && attr.NonResident && attr.NameLength == 0)
            {
                var lowestVcn = attr.LowestVcn;
                if (lowestVcn == 0)
                {
                    dataSize = attr.DataSize;
                }

                if (segments.All(s => s.LowestVcn != lowestVcn))
                {
                    segments.Add((lowestVcn, DataRuns.Decode(attr.DataRuns, lowestVcn)));
                }
            }
        }
    }

    private static byte[] ReadRecordAtOffset(SafeFileHandle handle, long offset, int recordSize, Win32.NtfsVolumeData vd)
    {
        // 卷读取必须按扇区对齐：读取包含该记录的整段扇区，再截取
        var sector = (long)vd.BytesPerSector;
        var alignedOffset = offset / sector * sector;
        var lead = (int)(offset - alignedOffset);
        var length = (int)((lead + recordSize + sector - 1) / sector * sector);
        var buffer = new byte[length];
        Win32.ReadAt(handle, buffer, alignedOffset);

        var record = buffer.AsSpan(lead, recordSize).ToArray();
        if (!MftRecord.ApplyFixup(record))
        {
            throw new InvalidDataException($"MFT 记录校验失败（偏移 {offset}）");
        }

        return record;
    }
}

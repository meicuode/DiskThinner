using DiskThinner.Core;

namespace DiskThinner.Scanner.Mft;

/// <summary>一段连续的簇。<see cref="Lcn"/> 为 -1 表示稀疏段（磁盘上没有数据）。</summary>
internal readonly record struct DataRun(long Vcn, long Lcn, long Length);

/// <summary>解码非常驻属性的数据段列表（mapping pairs）。</summary>
internal static class DataRuns
{
    /// <summary>
    /// 每段以一个头字节开始：低 4 位是长度字段的字节数，高 4 位是偏移字段的字节数。
    /// 长度是无符号数；偏移是相对上一段起始簇号的有符号数；偏移字节数为 0 表示稀疏段。
    /// </summary>
    public static List<DataRun> Decode(ReadOnlySpan<byte> runs, long startVcn = 0)
    {
        var result = new List<DataRun>();
        var pos = 0;
        long lcn = 0;
        var vcn = startVcn;

        while (pos < runs.Length)
        {
            var header = runs[pos++];
            if (header == 0)
            {
                break;
            }

            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > runs.Length)
            {
                throw new InvalidDataException(L.T("数据段列表格式错误"));
            }

            long length = 0;
            for (var i = 0; i < lengthSize; i++)
            {
                length |= (long)runs[pos + i] << (8 * i);
            }

            pos += lengthSize;

            if (offsetSize == 0)
            {
                result.Add(new DataRun(vcn, -1, length));
            }
            else
            {
                long delta = 0;
                for (var i = 0; i < offsetSize; i++)
                {
                    delta |= (long)runs[pos + i] << (8 * i);
                }

                // 符号扩展
                var shift = 64 - 8 * offsetSize;
                delta = (delta << shift) >> shift;
                pos += offsetSize;
                lcn += delta;
                result.Add(new DataRun(vcn, lcn, length));
            }

            vcn += length;
        }

        return result;
    }

    /// <summary>
    /// 统计实际分配了的簇数（跳过稀疏段），不分配内存。用于计算真实占用：
    /// 稀疏文件、压缩文件、$BadClus:$Bad 这类"逻辑上很大、实际不占空间"的流，用这个值才准确。
    /// 格式错误时返回 -1。
    /// </summary>
    public static long CountAllocatedClusters(ReadOnlySpan<byte> runs)
    {
        long clusters = 0;
        var pos = 0;
        while (pos < runs.Length)
        {
            var header = runs[pos++];
            if (header == 0)
            {
                return clusters;
            }

            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            if (lengthSize == 0 || lengthSize > 8 || offsetSize > 8 || pos + lengthSize + offsetSize > runs.Length)
            {
                return -1;
            }

            if (offsetSize != 0)
            {
                long length = 0;
                for (var i = 0; i < lengthSize; i++)
                {
                    length |= (long)runs[pos + i] << (8 * i);
                }

                clusters += length;
            }

            pos += lengthSize + offsetSize;
        }

        return clusters;
    }
}

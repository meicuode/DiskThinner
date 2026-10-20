using System.Buffers.Binary;
using System.Text;

namespace DiskThinner.Scanner.Tests;

/// <summary>按 NTFS 磁盘格式构造 FILE 记录，用来测试解析器。Build() 会像磁盘上一样写入 fixup。</summary>
internal sealed class RecordBuilder
{
    private const int UsaOffset = 0x30;
    private const int FirstAttribute = 0x38;
    private const ushort Usn = 0xABCD;

    private readonly byte[] _rec;
    private int _pos = FirstAttribute;
    private ushort _nextId;

    public RecordBuilder(int size = 1024, ushort flags = 0x1, long baseRecord = 0, ushort linkCount = 1)
    {
        _rec = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(_rec, 0x454C4946);
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x04), UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x06), (ushort)(size / 512 + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x12), linkCount);
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x14), FirstAttribute);
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(_rec.AsSpan(0x1C), (uint)size);
        BinaryPrimitives.WriteUInt64LittleEndian(_rec.AsSpan(0x20), (ulong)baseRecord);
    }

    public static RecordBuilder File(ushort linkCount = 1) => new(flags: 0x1, linkCount: linkCount);
    public static RecordBuilder Directory() => new(flags: 0x3);
    public static RecordBuilder Extension(long baseRecord) => new(flags: 0x1, baseRecord: baseRecord);

    public RecordBuilder StandardInfo(long lastWrite = 132_000_000_000_000_000, uint attributes = 0)
    {
        var value = new byte[0x48];
        BinaryPrimitives.WriteInt64LittleEndian(value.AsSpan(0x08), lastWrite);
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(0x20), attributes);
        return Resident(0x10, value, null);
    }

    public RecordBuilder FileName(long parent, string name, byte ns = 1)
    {
        var value = new byte[0x42 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(value, (ulong)parent | (1UL << 48));
        value[0x40] = (byte)name.Length;
        value[0x41] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(value, 0x42);
        return Resident(0x30, value, null);
    }

    public RecordBuilder ResidentData(int length, string? streamName = null) => Resident(0x80, new byte[length], streamName);

    public RecordBuilder NonResidentData(long dataSize, long allocSize, long lowestVcn = 0, ushort attrFlags = 0,
        long? compressedSize = null, string? streamName = null, byte[]? runs = null, uint type = 0x80)
    {
        runs ??= Runs(allocSize / 4096);
        var headerLength = compressedSize.HasValue ? 0x48 : 0x40;
        var nameLength = streamName?.Length ?? 0;
        var runsOffset = Align8(headerLength + nameLength * 2);
        var length = Align8(runsOffset + runs.Length);
        var a = _rec.AsSpan(_pos, length);

        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a[0x04..], (uint)length);
        a[0x08] = 1;
        a[0x09] = (byte)nameLength;
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x0A..], (ushort)headerLength);
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x0C..], attrFlags);
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x0E..], _nextId++);
        BinaryPrimitives.WriteInt64LittleEndian(a[0x10..], lowestVcn);
        BinaryPrimitives.WriteInt64LittleEndian(a[0x18..], lowestVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x20..], (ushort)runsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(a[0x28..], allocSize);
        BinaryPrimitives.WriteInt64LittleEndian(a[0x30..], dataSize);
        BinaryPrimitives.WriteInt64LittleEndian(a[0x38..], dataSize);
        if (compressedSize.HasValue)
        {
            BinaryPrimitives.WriteInt64LittleEndian(a[0x40..], compressedSize.Value);
        }

        if (streamName is not null)
        {
            Encoding.Unicode.GetBytes(streamName).CopyTo(a[headerLength..]);
        }

        runs.CopyTo(a[runsOffset..]);
        _pos += length;
        return this;
    }

    private RecordBuilder Resident(uint type, byte[] value, string? name)
    {
        var nameLength = name?.Length ?? 0;
        var valueOffset = Align8(0x18 + nameLength * 2);
        var length = Align8(valueOffset + value.Length);
        var a = _rec.AsSpan(_pos, length);

        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a[0x04..], (uint)length);
        a[0x08] = 0;
        a[0x09] = (byte)nameLength;
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x0A..], 0x18);
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x0E..], _nextId++);
        BinaryPrimitives.WriteUInt32LittleEndian(a[0x10..], (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a[0x14..], (ushort)valueOffset);
        if (name is not null)
        {
            Encoding.Unicode.GetBytes(name).CopyTo(a[0x18..]);
        }

        value.CopyTo(a[valueOffset..]);
        _pos += length;
        return this;
    }

    public byte[] Build()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_rec.AsSpan(_pos), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(_rec.AsSpan(0x18), (uint)(_pos + 8));
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(0x28), _nextId);

        // 写入 fixup：把每个 512 字节段的最后两个字节存进更新序列数组，再用序列号覆盖
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(_rec.AsSpan(0x06));
        BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(UsaOffset), Usn);
        for (var i = 1; i < usaCount; i++)
        {
            var pos = i * 512 - 2;
            _rec[UsaOffset + 2 * i] = _rec[pos];
            _rec[UsaOffset + 2 * i + 1] = _rec[pos + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(_rec.AsSpan(pos), Usn);
        }

        return (byte[])_rec.Clone();
    }

    private static int Align8(int value) => (value + 7) & ~7;

    /// <summary>编码数据段列表：先是 <paramref name="allocated"/> 个实际分配的簇（从 LCN 1 开始），再跟 <paramref name="sparse"/> 个稀疏簇。</summary>
    public static byte[] Runs(long allocated, long sparse = 0)
    {
        var bytes = new List<byte>();
        if (allocated > 0)
        {
            var length = LittleEndian(allocated);
            bytes.Add((byte)(0x10 | length.Length));
            bytes.AddRange(length);
            bytes.Add(0x01);
        }

        if (sparse > 0)
        {
            var length = LittleEndian(sparse);
            bytes.Add((byte)length.Length);
            bytes.AddRange(length);
        }

        bytes.Add(0x00);
        return [.. bytes];
    }

    private static byte[] LittleEndian(long value)
    {
        var bytes = new List<byte>();
        do
        {
            bytes.Add((byte)(value & 0xFF));
            value >>= 8;
        }
        while (value > 0);

        // 长度字段是无符号数，但保持最高位为 0 更接近真实写法
        if ((bytes[^1] & 0x80) != 0)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }
}

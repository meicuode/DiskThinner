using System.Buffers.Binary;

namespace DiskThinner.Scanner.Mft;

/// <summary>NTFS FILE 记录的底层解析：fixup 和属性遍历。只做纯数据处理，便于用构造的字节数据做单元测试。</summary>
internal static class MftRecord
{
    public const uint FileSignature = 0x454C4946; // "FILE"
    public const uint EndMarker = 0xFFFFFFFF;
    public const int FixupStride = 512;           // NTFS 的 fixup 固定按 512 字节分段，与物理扇区大小无关

    public const ushort FlagInUse = 0x0001;
    public const ushort FlagDirectory = 0x0002;

    public const uint AttrStandardInformation = 0x10;
    public const uint AttrAttributeList = 0x20;
    public const uint AttrFileName = 0x30;
    public const uint AttrData = 0x80;
    public const uint AttrIndexAllocation = 0xA0;

    public const ushort AttrFlagCompressed = 0x0001;
    public const ushort AttrFlagEncrypted = 0x4000;
    public const ushort AttrFlagSparse = 0x8000;

    public const byte NamespacePosix = 0;
    public const byte NamespaceWin32 = 1;
    public const byte NamespaceDos = 2;
    public const byte NamespaceWin32AndDos = 3;

    public const long ReferenceMask = 0x0000_FFFF_FFFF_FFFF;

    public static ushort Flags(ReadOnlySpan<byte> rec) => BinaryPrimitives.ReadUInt16LittleEndian(rec[0x16..]);
    public static ushort LinkCount(ReadOnlySpan<byte> rec) => BinaryPrimitives.ReadUInt16LittleEndian(rec[0x12..]);
    public static long BaseRecord(ReadOnlySpan<byte> rec) => (long)(BinaryPrimitives.ReadUInt64LittleEndian(rec[0x20..]) & ReferenceMask);

    /// <summary>
    /// 校验并还原 fixup：每个 512 字节段的最后两个字节在磁盘上被替换成了更新序列号，原值保存在更新序列数组里。
    /// 签名不对或序列号不一致（记录写到一半、已损坏）时返回 false。
    /// </summary>
    public static bool ApplyFixup(Span<byte> rec)
    {
        if (rec.Length < 0x30 || BinaryPrimitives.ReadUInt32LittleEndian(rec) != FileSignature)
        {
            return false;
        }

        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x04..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x06..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > rec.Length || (usaCount - 1) * FixupStride > rec.Length)
        {
            return false;
        }

        var usn = BinaryPrimitives.ReadUInt16LittleEndian(rec[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var pos = i * FixupStride - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(rec[pos..]) != usn)
            {
                return false;
            }

            rec[pos] = rec[usaOffset + 2 * i];
            rec[pos + 1] = rec[usaOffset + 2 * i + 1];
        }

        return true;
    }

    public static AttributeEnumerator Attributes(ReadOnlySpan<byte> rec) => new(rec);

    public ref struct AttributeEnumerator
    {
        private readonly ReadOnlySpan<byte> _rec;
        private readonly int _limit;
        private int _next;

        public AttributeEnumerator(ReadOnlySpan<byte> rec)
        {
            _rec = rec;
            int bytesInUse = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec[0x18..]);
            _limit = Math.Min(rec.Length, bytesInUse <= 0 ? rec.Length : bytesInUse);
            _next = BinaryPrimitives.ReadUInt16LittleEndian(rec[0x14..]);
            Current = default;
        }

        public AttributeView Current { get; private set; }

        public readonly AttributeEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next < 0x18 || _next + 8 > _limit)
            {
                return false;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(_rec[_next..]);
            if (type == EndMarker)
            {
                return false;
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_rec[(_next + 4)..]);
            if (length < 0x18 || (length & 7) != 0 || _next + length > _limit)
            {
                return false; // 结构损坏，停止解析这条记录
            }

            Current = new AttributeView(_rec.Slice(_next, length));
            _next += length;
            return true;
        }
    }
}

/// <summary>单个属性的只读视图。</summary>
internal readonly ref struct AttributeView
{
    private readonly ReadOnlySpan<byte> _a;

    public AttributeView(ReadOnlySpan<byte> attribute) => _a = attribute;

    public uint Type => BinaryPrimitives.ReadUInt32LittleEndian(_a);
    public bool NonResident => _a[0x08] != 0;
    public int NameLength => _a[0x09];
    public ushort AttrFlags => BinaryPrimitives.ReadUInt16LittleEndian(_a[0x0C..]);

    // ---- 常驻属性 ----

    /// <summary>常驻属性的值；越界时返回空。</summary>
    public ReadOnlySpan<byte> Value
    {
        get
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(_a[0x10..]);
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(_a[0x14..]);
            return length < 0 || offset + length > _a.Length ? [] : _a.Slice(offset, length);
        }
    }

    public int ValueLength => (int)BinaryPrimitives.ReadUInt32LittleEndian(_a[0x10..]);

    // ---- 非常驻属性 ----

    public long LowestVcn => BinaryPrimitives.ReadInt64LittleEndian(_a[0x10..]);
    public long AllocatedSize => BinaryPrimitives.ReadInt64LittleEndian(_a[0x28..]);
    public long DataSize => BinaryPrimitives.ReadInt64LittleEndian(_a[0x30..]);

    /// <summary>实际占用的磁盘空间：压缩或稀疏属性用"压缩后大小"字段，否则用分配大小。只在 LowestVcn 为 0 的片段上有效。</summary>
    public long OnDiskSize
    {
        get
        {
            var compressedOrSparse = (AttrFlags & (MftRecord.AttrFlagCompressed | MftRecord.AttrFlagSparse)) != 0;
            return compressedOrSparse && _a.Length >= 0x48
                ? BinaryPrimitives.ReadInt64LittleEndian(_a[0x40..])
                : AllocatedSize;
        }
    }

    public ReadOnlySpan<byte> DataRuns
    {
        get
        {
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(_a[0x20..]);
            return offset <= 0 || offset >= _a.Length ? [] : _a[offset..];
        }
    }
}

using System.Buffers.Binary;

namespace DiskThinner.Scanner.Mft;

/// <summary>
/// 解析过程中按记录号累积的数据。扩展记录（属性放不下时溢出到的记录）里的属性归到它的基础记录上，
/// 所以不需要单独解析 $ATTRIBUTE_LIST。全部记录处理完后调用 <see cref="BuildTree"/> 生成 <see cref="NodeTree"/>。
/// </summary>
internal sealed class MftTables
{
    private const byte StateInUse = 1;
    private const byte StateDirectory = 2;

    private const uint FileAttributeHidden = 0x2;
    private const uint FileAttributeSystem = 0x4;
    private const uint FileAttributeSparse = 0x200;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint FileAttributeCompressed = 0x800;
    private const uint FileAttributeOffline = 0x1000;
    private const uint FileAttributeRecallOnOpen = 0x40000;
    private const uint FileAttributeRecallOnDataAccess = 0x400000;

    private struct NameEntry
    {
        public int Record;
        public int ParentRecord;
        public int CharOffset;
        public byte Length;
        public byte Namespace;
    }

    private readonly byte[] _state;
    private readonly long[] _logical;
    private readonly long[] _alloc;
    private readonly long[] _lastWrite;
    private readonly uint[] _attributes;
    private readonly byte[] _nonDosNames;

    private NameEntry[] _names;
    private int _nameCount;
    private char[] _chars;
    private int _charCount;

    private readonly long _clusterSize;

    public MftTables(int recordCount, int clusterSize = 4096)
    {
        RecordCount = recordCount;
        _clusterSize = clusterSize;
        _state = new byte[recordCount];
        _logical = new long[recordCount];
        _alloc = new long[recordCount];
        _lastWrite = new long[recordCount];
        _attributes = new uint[recordCount];
        _nonDosNames = new byte[recordCount];
        _names = new NameEntry[Math.Max(1024, recordCount + recordCount / 4)];
        _chars = new char[Math.Max(4096, recordCount * 20)];
    }

    public int RecordCount { get; }
    public long InUseRecords { get; private set; }
    public long BadRecords { get; private set; }
    public long AlternateStreamBytes { get; private set; }
    public long DirectoryIndexBytes { get; private set; }

    /// <summary>解析一条已读入内存的记录（原地应用 fixup）。</summary>
    public void Parse(Span<byte> rec, int recordNumber)
    {
        if ((uint)recordNumber >= (uint)RecordCount)
        {
            return;
        }

        // 未使用过的记录全为 0，没有签名，直接跳过
        if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != MftRecord.FileSignature)
        {
            return;
        }

        if (!MftRecord.ApplyFixup(rec))
        {
            if ((MftRecord.Flags(rec) & MftRecord.FlagInUse) != 0)
            {
                BadRecords++;
            }

            return;
        }

        var flags = MftRecord.Flags(rec);
        if ((flags & MftRecord.FlagInUse) == 0)
        {
            return;
        }

        var baseRef = MftRecord.BaseRecord(rec);
        var owner = baseRef == 0 ? recordNumber : (int)Math.Min(baseRef, int.MaxValue);
        if ((uint)owner >= (uint)RecordCount)
        {
            BadRecords++;
            return;
        }

        if (owner == recordNumber)
        {
            InUseRecords++;
            _state[owner] |= StateInUse;
            if ((flags & MftRecord.FlagDirectory) != 0)
            {
                _state[owner] |= StateDirectory;
            }
        }

        foreach (var attr in MftRecord.Attributes(rec))
        {
            switch (attr.Type)
            {
                case MftRecord.AttrStandardInformation when !attr.NonResident:
                {
                    var v = attr.Value;
                    if (v.Length >= 0x24)
                    {
                        _lastWrite[owner] = BinaryPrimitives.ReadInt64LittleEndian(v[0x08..]);
                        _attributes[owner] = BinaryPrimitives.ReadUInt32LittleEndian(v[0x20..]);
                    }

                    break;
                }

                case MftRecord.AttrFileName when !attr.NonResident:
                    AddName(attr.Value, owner);
                    break;

                case MftRecord.AttrData:
                    if (!attr.NonResident)
                    {
                        // 常驻数据存放在 MFT 记录内部，不额外占用簇
                        if (attr.NameLength == 0)
                        {
                            _logical[owner] += attr.ValueLength;
                        }
                        else
                        {
                            AlternateStreamBytes += attr.ValueLength;
                        }
                    }
                    else
                    {
                        // 一个流可能分成多个片段（分布在扩展记录里）：占用按每个片段的数据段累加，
                        // 逻辑大小只在第一个片段（LowestVcn 为 0）上有效
                        _alloc[owner] += AllocatedBytes(attr);
                        if (attr.LowestVcn == 0)
                        {
                            if (attr.NameLength == 0)
                            {
                                _logical[owner] += attr.DataSize;
                            }
                            else
                            {
                                AlternateStreamBytes += attr.DataSize;
                            }
                        }
                    }

                    break;

                case MftRecord.AttrIndexAllocation when attr.NonResident:
                    DirectoryIndexBytes += AllocatedBytes(attr);
                    break;
            }
        }
    }

    public long MalformedRuns { get; private set; }

    private long AllocatedBytes(AttributeView attr)
    {
        var clusters = DataRuns.CountAllocatedClusters(attr.DataRuns);
        if (clusters >= 0)
        {
            return clusters * _clusterSize;
        }

        // 数据段列表损坏时退回到属性头里的字段
        MalformedRuns++;
        return attr.LowestVcn == 0 ? attr.OnDiskSize : 0;
    }

    private void AddName(ReadOnlySpan<byte> v, int owner)
    {
        if (v.Length < 0x42)
        {
            return;
        }

        var parent = (long)(BinaryPrimitives.ReadUInt64LittleEndian(v) & MftRecord.ReferenceMask);
        var length = v[0x40];
        var ns = v[0x41];
        if (v.Length < 0x42 + length * 2)
        {
            return;
        }

        if (_nameCount == _names.Length)
        {
            Array.Resize(ref _names, _names.Length * 2);
        }

        if (_charCount + length > _chars.Length)
        {
            Array.Resize(ref _chars, Math.Max(_chars.Length * 2, _charCount + length));
        }

        var dest = _chars.AsSpan(_charCount, length);
        for (var i = 0; i < length; i++)
        {
            dest[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(v[(0x42 + i * 2)..]);
        }

        _names[_nameCount++] = new NameEntry
        {
            Record = owner,
            ParentRecord = (int)Math.Min(parent, int.MaxValue),
            CharOffset = _charCount,
            Length = length,
            Namespace = ns,
        };
        _charCount += length;

        if (ns != MftRecord.NamespaceDos && _nonDosNames[owner] < byte.MaxValue)
        {
            _nonDosNames[owner]++;
        }
    }

    /// <summary>
    /// 生成文件树。每个非 DOS 文件名生成一个节点；同一记录的第二个及以后的文件名标为硬链接。
    /// 记录 5 是根目录，0–15 中的其他记录是 NTFS 元数据。
    /// </summary>
    public NodeTree BuildTree(string rootName, out int hardLinkSecondaries)
    {
        var tree = new NodeTree(_nameCount + 16, _chars, _charCount);
        var recordToNode = new int[RecordCount];
        Array.Fill(recordToNode, -1);
        var parentRecord = new int[_nameCount];
        hardLinkSecondaries = 0;

        for (var i = 0; i < _nameCount; i++)
        {
            ref var e = ref _names[i];
            var r = e.Record;
            if ((_state[r] & StateInUse) == 0)
            {
                continue;
            }

            // DOS 8.3 短名总是伴随一个 Win32 长名，只在没有其他名字时才使用
            if (e.Namespace == MftRecord.NamespaceDos && _nonDosNames[r] > 0)
            {
                continue;
            }

            var flags = MapFlags(r);
            var primary = recordToNode[r] < 0;
            if (!primary)
            {
                flags |= NodeFlags.HardLinkSecondary;
                hardLinkSecondaries++;
            }

            var isDir = (flags & NodeFlags.Directory) != 0;
            var index = tree.AddPooled(-1, e.CharOffset, e.Length, flags,
                isDir ? 0 : _logical[r], isDir ? 0 : _alloc[r], _lastWrite[r]);
            parentRecord[index] = e.ParentRecord;
            if (primary)
            {
                recordToNode[r] = index;
            }
        }

        const int rootRecord = 5;
        var root = rootRecord < RecordCount ? recordToNode[rootRecord] : -1;
        if (root < 0)
        {
            throw new InvalidDataException("MFT 中找不到根目录记录");
        }

        tree.Rename(root, rootName);
        tree.Root = root;

        for (var i = 0; i < tree.Count; i++)
        {
            ref var node = ref tree[i];
            if (i == root)
            {
                node.Parent = -1;
                continue;
            }

            var pr = parentRecord[i];
            var p = (uint)pr < (uint)RecordCount ? recordToNode[pr] : -1;
            if (p < 0 || !tree[p].IsDirectory || p == i)
            {
                node.Parent = -1;
                node.Flags |= NodeFlags.Orphan;
            }
            else
            {
                node.Parent = p;
            }
        }

        return tree;
    }

    private NodeFlags MapFlags(int record)
    {
        var flags = NodeFlags.None;
        if ((_state[record] & StateDirectory) != 0)
        {
            flags |= NodeFlags.Directory;
        }

        var a = _attributes[record];
        if ((a & FileAttributeHidden) != 0) flags |= NodeFlags.Hidden;
        if ((a & FileAttributeSystem) != 0) flags |= NodeFlags.System;
        if ((a & FileAttributeReparsePoint) != 0) flags |= NodeFlags.ReparsePoint;
        if ((a & FileAttributeCompressed) != 0) flags |= NodeFlags.Compressed;
        if ((a & FileAttributeSparse) != 0) flags |= NodeFlags.Sparse;
        if ((a & (FileAttributeOffline | FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess)) != 0) flags |= NodeFlags.CloudPlaceholder;

        if (record < 16 && record != 5)
        {
            flags |= NodeFlags.Metadata;
        }

        return flags;
    }
}

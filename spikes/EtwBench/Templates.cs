using System.Buffers.Binary;
using System.Text;

namespace EtwBench;

/// <summary>
/// 按 tests/fixtures/monitor/templates.txt（系统清单导出的事件模板）读原始载荷。固定长度字段的偏移预先算好，
/// 字符串和 SID 之后的字段运行时按顺序走过去。只给 2a.1 的实测用，2a.2 换成 Monitor 里手写的解析器（用同一份模板核对）。
/// </summary>
internal sealed class Templates
{
    public const string KernelFile = "Microsoft-Windows-Kernel-File";
    public const string KernelProcess = "Microsoft-Windows-Kernel-Process";

    private readonly Dictionary<(string Provider, int Id, int Version), Template> _map = [];

    public static Templates Load(string path)
    {
        var t = new Templates();
        Template? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("event ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                current = new Template(parts[1], int.Parse(parts[2]), int.Parse(parts[3].TrimStart('v')));
                t._map[(current.Provider, current.Id, current.Version)] = current;
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && current is not null)
            {
                var parts = line.Trim().Split(' ');
                current.Fields.Add((parts[0], parts[1]));
            }
        }

        return t;
    }

    public Template? Find(string provider, int id, int version) => _map.GetValueOrDefault((provider, id, version));

    public int Count => _map.Count;
}

internal sealed class Template(string provider, int id, int version)
{
    public string Provider { get; } = provider;
    public int Id { get; } = id;
    public int Version { get; } = version;
    public List<(string Name, string Type)> Fields { get; } = [];
    private readonly Dictionary<string, int> _fixedOffsets = new(StringComparer.Ordinal);
    private int _pointerSize = -1;

    private static int FixedSize(string type, int pointerSize) => type switch
    {
        "win:Pointer" => pointerSize,
        "win:UInt32" or "win:Int32" or "win:HexInt32" => 4,
        "win:UInt64" or "win:Int64" or "win:FILETIME" or "win:HexInt64" => 8,
        "win:UInt16" => 2,
        "win:UInt8" or "win:Boolean" => 1,
        _ => -1, // 字符串、SID：长度可变
    };

    /// <summary>字段在载荷里的偏移；前面有可变长度字段时按实际数据走过去。找不到返回 -1。</summary>
    public int Offset(ReadOnlySpan<byte> data, string name, int pointerSize)
    {
        if (_pointerSize != pointerSize)
        {
            _fixedOffsets.Clear();
            _pointerSize = pointerSize;
            var off = 0;
            foreach (var (n, type) in Fields)
            {
                _fixedOffsets[n] = off;
                var size = FixedSize(type, pointerSize);
                if (size < 0)
                {
                    break; // 之后的字段偏移不固定
                }

                off += size;
            }
        }

        if (_fixedOffsets.TryGetValue(name, out var fixedOffset))
        {
            return fixedOffset;
        }

        var pos = 0;
        foreach (var (n, type) in Fields)
        {
            if (n == name)
            {
                return pos;
            }

            var size = FixedSize(type, pointerSize);
            if (size >= 0)
            {
                pos += size;
            }
            else if (type == "win:UnicodeString")
            {
                pos += UnicodeLength(data[pos..]) + 2;
            }
            else if (type == "win:AnsiString")
            {
                var end = data[pos..].IndexOf((byte)0);
                pos += (end < 0 ? data.Length - pos : end) + 1;
            }
            else if (type == "win:SID")
            {
                // 部分事件在 SID 前有 8 字节的令牌指针前缀，这里按清单里没有前缀处理：Revision(1) + Count(1) + Authority(6) + 4×Count
                pos += 8 + 4 * data[pos + 1];
            }
            else
            {
                return -1;
            }

            if (pos > data.Length)
            {
                return -1;
            }
        }

        return -1;
    }

    private static int UnicodeLength(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                return i;
            }
        }

        return data.Length & ~1;
    }

    public ulong U64(ReadOnlySpan<byte> data, string name, int pointerSize)
    {
        var o = Offset(data, name, pointerSize);
        return o < 0 || o + 8 > data.Length ? 0 : BinaryPrimitives.ReadUInt64LittleEndian(data[o..]);
    }

    public uint U32(ReadOnlySpan<byte> data, string name, int pointerSize)
    {
        var o = Offset(data, name, pointerSize);
        return o < 0 || o + 4 > data.Length ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);
    }

    public ulong Pointer(ReadOnlySpan<byte> data, string name, int pointerSize) =>
        pointerSize == 8 ? U64(data, name, pointerSize) : U32(data, name, pointerSize);

    public string? Text(ReadOnlySpan<byte> data, string name, int pointerSize)
    {
        var o = Offset(data, name, pointerSize);
        if (o < 0 || o > data.Length)
        {
            return null;
        }

        var type = Fields.First(f => f.Name == name).Type;
        if (type == "win:AnsiString")
        {
            var end = data[o..].IndexOf((byte)0);
            return Encoding.ASCII.GetString(data.Slice(o, end < 0 ? data.Length - o : end));
        }

        return Encoding.Unicode.GetString(data.Slice(o, UnicodeLength(data[o..])));
    }
}

using DiskThinner.Core;

namespace DiskThinner.Scanner;

/// <summary>
/// 扫描得到的文件树。先用 <see cref="Add"/> 写入节点（只需指定父节点），
/// 全部写完后调用一次 <see cref="Finalize"/> 建立子节点链表、汇总大小、按占用排序。
/// 写入阶段不是线程安全的，调用方负责加锁。
/// </summary>
public sealed class NodeTree
{
    private Node[] _nodes;
    private int _count;
    private readonly NamePool _names;

    public NodeTree(int initialCapacity = 1 << 16)
    {
        _nodes = new Node[Math.Max(16, initialCapacity)];
        _names = new NamePool();
    }

    /// <summary>直接使用已有的名称池（MFT 解析时已经把所有文件名解码到池里），避免再复制一份。</summary>
    internal NodeTree(int initialCapacity, NamePool namePool)
    {
        _nodes = new Node[Math.Max(16, initialCapacity)];
        _names = namePool;
    }

    /// <summary>添加一个名称已在名称池中的节点。</summary>
    internal int AddPooled(int parent, int nameOffset, int nameLength, NodeFlags flags, long logicalSize, long allocSize, long lastWriteUtc)
    {
        if (_count == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        var index = _count++;
        _nodes[index] = new Node
        {
            Parent = parent,
            FirstChild = -1,
            NextSibling = -1,
            NameOffset = nameOffset,
            NameLength = (ushort)nameLength,
            Flags = flags,
            LogicalSize = logicalSize,
            AllocSize = allocSize,
            LastWriteUtc = lastWriteUtc,
            Tag = -1,
        };
        return index;
    }

    public int Count => _count;

    public int Root { get; set; } = -1;

    /// <summary>汇总时是否只把硬链接的大小算一次（默认）。</summary>
    public bool CountHardLinksOnce { get; private set; } = true;

    /// <summary>无法从根到达的节点数（MFT 中父目录缺失的记录）。</summary>
    public int UnreachableCount { get; private set; }

    public ref Node this[int index] => ref _nodes[index];

    public ReadOnlySpan<char> GetName(int index)
    {
        ref var node = ref _nodes[index];
        return _names.Get(node.NameOffset, node.NameLength);
    }

    public string GetNameString(int index) => new(GetName(index));

    /// <summary>节点数组与名称池占用的字节数（不含临时结构）。</summary>
    public long ApproximateBytes => (long)_nodes.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<Node>() + _names.Capacity * sizeof(char);

    public void EnsureCapacity(int nodes)
    {
        if (_nodes.Length < nodes)
        {
            Array.Resize(ref _nodes, nodes);
        }
    }

    public int Add(int parent, ReadOnlySpan<char> name, NodeFlags flags, long logicalSize, long allocSize, long lastWriteUtc)
    {
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentException(L.T("名称过长"), nameof(name));
        }

        if (_count == _nodes.Length)
        {
            Array.Resize(ref _nodes, _nodes.Length * 2);
        }

        var nameOffset = AppendName(name);
        var index = _count++;
        _nodes[index] = new Node
        {
            Parent = parent,
            FirstChild = -1,
            NextSibling = -1,
            NameOffset = nameOffset,
            NameLength = (ushort)name.Length,
            Flags = flags,
            LogicalSize = logicalSize,
            AllocSize = allocSize,
            LastWriteUtc = lastWriteUtc,
            Tag = -1,
        };
        return index;
    }

    public void Rename(int index, ReadOnlySpan<char> name)
    {
        _nodes[index].NameOffset = AppendName(name);
        _nodes[index].NameLength = (ushort)name.Length;
    }

    private int AppendName(ReadOnlySpan<char> name) => _names.Append(name);

    /// <summary>
    /// 建立子节点链表，自底向上汇总大小、文件数、目录数和最后写入时间，再把每个目录的子项按占用降序排列。
    /// 可以重复调用（例如切换是否重复计算硬链接）。
    /// </summary>
    public void Finalize(bool countHardLinksOnce = true)
    {
        if (Root < 0)
        {
            throw new InvalidOperationException(L.T("未设置根节点"));
        }

        CountHardLinksOnce = countHardLinksOnce;
        var nodes = _nodes;
        var count = _count;

        // 1. 重建子节点链表（倒序插入，保持写入顺序）
        for (var i = 0; i < count; i++)
        {
            nodes[i].FirstChild = -1;
            nodes[i].NextSibling = -1;
        }

        for (var i = count - 1; i >= 0; i--)
        {
            var parent = nodes[i].Parent;
            if (parent >= 0 && i != Root)
            {
                nodes[i].NextSibling = nodes[parent].FirstChild;
                nodes[parent].FirstChild = i;
            }
        }

        // 2. 先序遍历得到顺序；倒序处理即可保证子节点先于父节点
        var order = new int[count];
        var orderLength = 0;
        var stack = new Stack<int>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order[orderLength++] = n;
            for (var c = nodes[n].FirstChild; c >= 0; c = nodes[c].NextSibling)
            {
                stack.Push(c);
            }
        }

        UnreachableCount = count - orderLength;

        // 3. 目录的汇总值清零（目录自身不计大小，最后写入时间保留自身值再取最大）
        for (var k = 0; k < orderLength; k++)
        {
            ref var n = ref nodes[order[k]];
            n.Flags &= ~NodeFlags.Incomplete;
            if (n.IsDirectory)
            {
                n.LogicalSize = 0;
                n.AllocSize = 0;
                n.FileCount = 0;
                n.DirCount = 0;
            }
        }

        // 4. 自底向上汇总
        for (var k = orderLength - 1; k > 0; k--)
        {
            var i = order[k];
            ref var child = ref nodes[i];
            ref var parent = ref nodes[child.Parent];

            if (child.IsDirectory)
            {
                parent.DirCount += child.DirCount + 1;
                parent.FileCount += child.FileCount;
            }
            else
            {
                parent.FileCount += 1;
            }

            var countSize = !countHardLinksOnce || (child.Flags & NodeFlags.HardLinkSecondary) == 0;
            if (countSize)
            {
                parent.LogicalSize += child.LogicalSize;
                parent.AllocSize += child.AllocSize;
            }

            if (child.LastWriteUtc > parent.LastWriteUtc)
            {
                parent.LastWriteUtc = child.LastWriteUtc;
            }

            if ((child.Flags & (NodeFlags.AccessDenied | NodeFlags.Incomplete)) != 0)
            {
                parent.Flags |= NodeFlags.Incomplete;
            }
        }

        // 5. 子项按占用降序排列
        var buffer = new List<int>();
        for (var k = 0; k < orderLength; k++)
        {
            var dir = order[k];
            if (nodes[dir].FirstChild < 0 || nodes[nodes[dir].FirstChild].NextSibling < 0)
            {
                continue;
            }

            buffer.Clear();
            for (var c = nodes[dir].FirstChild; c >= 0; c = nodes[c].NextSibling)
            {
                buffer.Add(c);
            }

            buffer.Sort((a, b) => nodes[b].AllocSize.CompareTo(nodes[a].AllocSize));
            nodes[dir].FirstChild = buffer[0];
            for (var j = 0; j < buffer.Count; j++)
            {
                nodes[buffer[j]].NextSibling = j + 1 < buffer.Count ? buffer[j + 1] : -1;
            }
        }
    }

    /// <summary>
    /// 释放多余容量：节点数组截到实际长度，名称池只保留被引用的名称（MFT 解析时的 8.3 短名等会被丢掉）。
    /// 扫描结束后调用一次即可。
    /// </summary>
    public void TrimExcess()
    {
        // 按名称在池中的位置排序后原地前移（见 NamePool.Compact），只需要几个下标数组，不用再分配一整份名称池。
        var order = new int[_count];
        for (var i = 0; i < _count; i++)
        {
            order[i] = i;
        }

        var nodes = _nodes;
        Array.Sort(order, (a, b) => nodes[a].NameOffset.CompareTo(nodes[b].NameOffset));

        var offsets = new int[_count];
        var lengths = new int[_count];
        for (var k = 0; k < _count; k++)
        {
            offsets[k] = nodes[order[k]].NameOffset;
            lengths[k] = nodes[order[k]].NameLength;
        }

        _names.Compact(offsets, lengths);
        for (var k = 0; k < _count; k++)
        {
            nodes[order[k]].NameOffset = offsets[k];
        }

        if (_nodes.Length > _count + _count / 10 + 1024)
        {
            Array.Resize(ref _nodes, Math.Max(16, _count));
        }
    }

    public long NameChars => _names.Used;
    public long NameCapacity => _names.Capacity;

    /// <summary>把 <paramref name="flag"/> 从带有它的节点传播到整棵子树。需在 <see cref="Finalize"/> 之后调用。</summary>
    public void PropagateFlagToDescendants(NodeFlags flag)
    {
        var stack = new Stack<int>();
        stack.Push(Root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            var inherit = (_nodes[n].Flags & flag) != 0;
            for (var c = _nodes[n].FirstChild; c >= 0; c = _nodes[c].NextSibling)
            {
                if (inherit)
                {
                    _nodes[c].Flags |= flag;
                }

                stack.Push(c);
            }
        }
    }

    public IEnumerable<int> Children(int index)
    {
        for (var c = _nodes[index].FirstChild; c >= 0; c = _nodes[c].NextSibling)
        {
            yield return c;
        }
    }

    public int FindChild(int parent, ReadOnlySpan<char> name)
    {
        for (var c = _nodes[parent].FirstChild; c >= 0; c = _nodes[c].NextSibling)
        {
            if (GetName(c).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }

        return -1;
    }

    /// <summary>按完整路径查找节点，例如 <c>C:\Users\demo</c>。找不到返回 -1。</summary>
    public int FindPath(string path)
    {
        if (Root < 0)
        {
            return -1;
        }

        var rootName = GetName(Root).TrimEnd('\\');
        var span = path.AsSpan().TrimEnd('\\');
        if (!span.StartsWith(rootName, StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var current = Root;
        var rest = span[rootName.Length..].TrimStart('\\');
        while (!rest.IsEmpty && current >= 0)
        {
            var sep = rest.IndexOf('\\');
            var part = sep < 0 ? rest : rest[..sep];
            current = FindChild(current, part);
            rest = sep < 0 ? [] : rest[(sep + 1)..];
        }

        return current;
    }

    public string GetPath(int index)
    {
        var parts = new List<int>();
        for (var n = index; n >= 0; n = n == Root ? -1 : _nodes[n].Parent)
        {
            parts.Add(n);
        }

        var sb = new System.Text.StringBuilder();
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            var name = GetName(parts[i]);
            if (sb.Length > 0 && sb[^1] != '\\')
            {
                sb.Append('\\');
            }

            sb.Append(name);
        }

        return sb.ToString();
    }

    public int GetDepth(int index)
    {
        var depth = 0;
        for (var n = index; n != Root && n >= 0; n = _nodes[n].Parent)
        {
            depth++;
        }

        return depth;
    }
}

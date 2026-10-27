namespace DiskThinner.Monitor;

/// <summary>
/// 会话级的目录表：按段存的树，不区分大小写，一个会话内编号不变（docs/phase2a-design.md 5.6.1 节）。只建到 32 层
/// （更深的算在第 32 层，和快照一样），最多 200,000 个节点，满了以后新目录记到已有的最近的上级上。
/// 编号 0 保留给"对不上目录"（监控开始前已打开的文件的写入，第 12.2 节第 7 条）。
/// </summary>
public sealed class DirectoryTable
{
    public const int Unresolved = 0;
    public const int MaxDepth = 32;

    private readonly int _maxNodes;
    private readonly List<Node> _nodes = [new Node(-1, "", -1)];
    private readonly Dictionary<string, int> _roots = new(StringComparer.OrdinalIgnoreCase);

    public DirectoryTable(int maxNodes = 200_000) => _maxNodes = maxNodes;

    public int Count => _nodes.Count - 1;

    /// <summary>节点数到了上限，有目录被记到了上级。</summary>
    public bool Full { get; private set; }

    public int Parent(int id) => _nodes[Resolve(id)].Parent;

    public int Depth(int id) => _nodes[Resolve(id)].Depth;

    /// <summary>目录"C:\a\b"的编号，没有就建（到 32 层为止）。<paramref name="path"/> 是盘符形式的目录路径。</summary>
    public int GetOrAdd(string path) => Walk(path, create: true);

    /// <summary>不建新节点：已有的这个目录的编号，没有返回 -1。</summary>
    public int Find(string path)
    {
        var id = Walk(path, create: false, exact: true);
        return id;
    }

    public string PathOf(int id)
    {
        id = Resolve(id);
        if (id <= 0)
        {
            return "";
        }

        var parts = new List<string>();
        for (var n = id; n > 0; n = _nodes[n].Parent)
        {
            parts.Add(_nodes[n].Name);
        }

        parts.Reverse();
        return parts[0] + string.Join('\\', parts.Skip(1));
    }

    public bool IsUnder(int id, int ancestor)
    {
        id = Resolve(id);
        ancestor = Resolve(ancestor);
        for (var n = id; n > 0; n = _nodes[n].Parent)
        {
            if (n == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 目录改名（第 12.2 节第 3 条）：原地改父节点和名字，编号不变；新路径已经有节点时把旧节点并进去（子节点挂过去，旧编号记为别名）。
    /// 返回改名后的编号。
    /// </summary>
    public int Move(int id, string newPath)
    {
        id = Resolve(id);
        var (parentPath, name) = Split(newPath);
        if (parentPath is null || id <= 0)
        {
            return id;
        }

        var newParent = GetOrAdd(parentPath);
        if (newParent == id || IsUnder(newParent, id))
        {
            return id;
        }

        var node = _nodes[id];
        _nodes[node.Parent].Children?.Remove(node.Name);
        var existing = _nodes[newParent].Children?.GetValueOrDefault(name) ?? 0;
        if (existing > 0)
        {
            // 并进已有的节点：子节点挂过去，旧节点成为它的别名
            foreach (var (childName, childId) in node.Children ?? [])
            {
                var target = _nodes[existing].Children ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (!target.ContainsKey(childName))
                {
                    target[childName] = childId;
                    _nodes[childId] = _nodes[childId] with { Parent = existing };
                }
            }

            _nodes[id] = node with { AliasOf = existing, Children = null };
            return existing;
        }

        _nodes[id] = node with { Parent = newParent, Name = name, Depth = _nodes[newParent].Depth + 1 };
        (_nodes[newParent].Children ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))[name] = id;
        return id;
    }

    /// <summary>别名（被并进别的节点的旧编号）换成现在的编号。</summary>
    public int Resolve(int id)
    {
        for (var i = 0; i < 64 && id > 0 && _nodes[id].AliasOf > 0; i++)
        {
            id = _nodes[id].AliasOf;
        }

        return id;
    }

    /// <summary>"C:\a\b\c.txt" → ("C:\a\b", "c.txt")；盘根下的"C:\x" → ("C:\", "x")。</summary>
    public static (string? Parent, string Name) Split(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var i = trimmed.LastIndexOf('\\');
        if (i < 0)
        {
            return (null, trimmed);
        }

        var parent = trimmed[..i];
        if (parent.Length == 2 && parent[1] == ':')
        {
            parent += "\\";
        }

        return (parent, trimmed[(i + 1)..]);
    }

    private int Walk(string path, bool create, bool exact = false)
    {
        if (path.Length < 2 || path[1] != ':')
        {
            return -1;
        }

        var rootName = char.ToUpperInvariant(path[0]) + ":\\";
        if (!_roots.TryGetValue(rootName, out var current))
        {
            if (!create || _nodes.Count > _maxNodes)
            {
                return -1;
            }

            _nodes.Add(new Node(0, rootName, 0));
            current = _nodes.Count - 1;
            _roots[rootName] = current;
        }

        var rest = path.AsSpan(2).Trim('\\');
        while (!rest.IsEmpty)
        {
            var sep = rest.IndexOf('\\');
            var segment = sep < 0 ? rest : rest[..sep];
            rest = sep < 0 ? [] : rest[(sep + 1)..];
            if (segment.IsEmpty)
            {
                continue;
            }

            var node = _nodes[current];
            var children = node.Children;
            var key = segment.ToString();
            if (children is not null && children.TryGetValue(key, out var child))
            {
                current = Resolve(child);
                continue;
            }

            if (!create)
            {
                return exact ? -1 : current;
            }

            if (node.Depth >= MaxDepth)
            {
                return current; // 更深的算在第 32 层
            }

            if (_nodes.Count > _maxNodes)
            {
                Full = true;
                return current; // 满了：记到已有的最近的上级
            }

            _nodes.Add(new Node(current, key, node.Depth + 1));
            var id = _nodes.Count - 1;
            (_nodes[current].Children ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))[key] = id;
            current = id;
        }

        return current;
    }

    private sealed record Node(int Parent, string Name, int Depth)
    {
        public Dictionary<string, int>? Children { get; set; }
        public int AliasOf { get; init; } = -1;
    }
}

/// <summary>设备路径 → 盘符路径（QueryDosDevice 的映射）。只记有盘符的本地固定盘。测试用假的。</summary>
public interface IDeviceMap
{
    /// <summary>"\Device\HarddiskVolume3\a\b" → "C:\a\b"；不认识的前缀（网络路径、没有盘符的卷）返回 null。</summary>
    string? ToDosPath(string devicePath);
}

/// <summary>按前缀表换算的 <see cref="IDeviceMap"/>：前缀是"\Device\HarddiskVolume3"，值是"C:"。</summary>
public sealed class PrefixDeviceMap(IReadOnlyDictionary<string, string> prefixes) : IDeviceMap
{
    private readonly (string Device, string Drive)[] _prefixes = [.. prefixes.Select(p => (p.Key.TrimEnd('\\'), p.Value.TrimEnd('\\'))).OrderByDescending(p => p.Item1.Length)];

    public string? ToDosPath(string devicePath)
    {
        if (devicePath.Length >= 3 && devicePath[1] == ':' && devicePath[2] == '\\')
        {
            return devicePath; // 已经是盘符形式（进程列举得到的映像路径）
        }

        foreach (var (device, drive) in _prefixes)
        {
            if (devicePath.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                && (devicePath.Length == device.Length || devicePath[device.Length] == '\\'))
            {
                var rest = devicePath[device.Length..];
                return drive + (rest.Length == 0 ? "\\" : rest);
            }
        }

        return null;
    }
}

/// <summary>
/// FileKey → 文件所在目录和文件名（精确模式，NameCreate 时加）。名字缓存拆除时 NameDelete 很多（V15），名字表会一直涨，
/// 所以有上限：两代表近似"最近使用"，当前代满了整代淘汰最旧的一代，计入 Evicted（第 12.2 节第 4 条）。
/// 目录用驻留的字符串存，不在这里建目录节点（延迟建目录节点）。
/// </summary>
internal sealed class FileNameMap(int capacity)
{
    private readonly int _half = Math.Max(16, capacity / 2);
    private Dictionary<ulong, FileName> _current = [];
    private Dictionary<ulong, FileName> _previous = [];
    private readonly Dictionary<string, string> _dirs = new(StringComparer.OrdinalIgnoreCase);

    public long Evicted { get; private set; }

    public int Count => _current.Count + _previous.Count;

    public int Peak { get; private set; }

    public void Set(ulong key, string dir, string name)
    {
        if (!_dirs.TryGetValue(dir, out var interned))
        {
            if (_dirs.Count > _half)
            {
                _dirs.Clear(); // 只是驻留池：清掉后已有的项还引用着原来的字符串
            }

            _dirs[dir] = interned = dir;
        }

        _previous.Remove(key);
        _current[key] = new FileName(interned, name);
        Rotate();
        Peak = Math.Max(Peak, Count);
    }

    public bool TryGet(ulong key, out FileName value)
    {
        if (_current.TryGetValue(key, out value))
        {
            return true;
        }

        if (_previous.Remove(key, out value))
        {
            _current[key] = value; // 用到了就挪到当前代
            Rotate();
            return true;
        }

        return false;
    }

    public bool Remove(ulong key, out FileName value) => _current.Remove(key, out value) || _previous.Remove(key, out value);

    private void Rotate()
    {
        if (_current.Count < _half)
        {
            return;
        }

        Evicted += _previous.Count;
        _previous = _current;
        _current = [];
    }
}

internal readonly record struct FileName(string Dir, string Name)
{
    public string FullPath => Dir.EndsWith('\\') ? Dir + Name : Dir + "\\" + Name;
}

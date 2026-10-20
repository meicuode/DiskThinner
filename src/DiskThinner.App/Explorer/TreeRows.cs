using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 树形列表的行模型：维护"当前可见的行"（节点 + 缩进层级）。展开时把子节点插到父行后面，折叠时移除整棵子树。
/// 和界面无关，便于单元测试；ListView 在虚拟模式下只按行号来取数据。
/// </summary>
internal sealed class TreeRows
{
    private readonly List<(int Node, int Depth)> _rows = [];
    private readonly NodeTree _tree;
    private readonly bool[] _expanded;

    public TreeRows(NodeTree tree)
    {
        _tree = tree;
        _expanded = new bool[tree.Count];
        _rows.Add((tree.Root, 0));
    }

    public int Count => _rows.Count;

    public NodeTree Tree => _tree;

    public int NodeAt(int row) => _rows[row].Node;

    public int DepthAt(int row) => _rows[row].Depth;

    public bool IsExpanded(int node) => _expanded[node];

    public bool HasChildren(int node) => _tree[node].FirstChild >= 0;

    /// <summary>展开一行，返回新增的行数。</summary>
    public int Expand(int row)
    {
        var (node, depth) = _rows[row];
        if (_expanded[node] || !HasChildren(node))
        {
            return 0;
        }

        _expanded[node] = true;
        var insert = new List<(int, int)>();
        AppendVisibleChildren(node, depth + 1, insert);
        _rows.InsertRange(row + 1, insert);
        return insert.Count;
    }

    /// <summary>折叠一行，返回移除的行数。</summary>
    public int Collapse(int row)
    {
        var (node, depth) = _rows[row];
        if (!_expanded[node])
        {
            return 0;
        }

        _expanded[node] = false;
        var end = SubtreeEnd(row);
        _rows.RemoveRange(row + 1, end - row - 1);
        return end - row - 1;
    }

    public void Toggle(int row)
    {
        if (_expanded[_rows[row].Node])
        {
            Collapse(row);
        }
        else
        {
            Expand(row);
        }
    }

    /// <summary>行 <paramref name="row"/> 的子树之后的第一行。</summary>
    public int SubtreeEnd(int row)
    {
        var depth = _rows[row].Depth;
        var end = row + 1;
        while (end < _rows.Count && _rows[end].Depth > depth)
        {
            end++;
        }

        return end;
    }

    /// <summary>父节点所在的行；根返回 -1。</summary>
    public int ParentRow(int row)
    {
        var depth = _rows[row].Depth;
        for (var r = row - 1; r >= 0; r--)
        {
            if (_rows[r].Depth < depth)
            {
                return r;
            }
        }

        return -1;
    }

    /// <summary>展开所有祖先，返回节点所在的行；节点不在树里返回 -1。</summary>
    public int Reveal(int node)
    {
        var chain = new List<int>();
        for (var n = node; n >= 0 && n != _tree.Root; n = _tree[n].Parent)
        {
            chain.Add(n);
        }

        if (chain.Count > 0 && _tree[chain[^1]].Parent != _tree.Root)
        {
            return -1; // 不可达节点
        }

        var row = 0;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            Expand(row);
            var target = chain[i];
            var childDepth = _rows[row].Depth + 1;
            var end = SubtreeEnd(row);
            var found = -1;
            for (var r = row + 1; r < end; r++)
            {
                if (_rows[r].Depth == childDepth && _rows[r].Node == target)
                {
                    found = r;
                    break;
                }
            }

            if (found < 0)
            {
                return -1;
            }

            row = found;
        }

        return row;
    }

    /// <summary>当前行列表中节点所在的行（不展开任何东西）；不可见返回 -1。</summary>
    public int FindVisibleRow(int node)
    {
        for (var r = 0; r < _rows.Count; r++)
        {
            if (_rows[r].Node == node)
            {
                return r;
            }
        }

        return -1;
    }

    private void AppendVisibleChildren(int node, int depth, List<(int, int)> output)
    {
        for (var c = _tree[node].FirstChild; c >= 0; c = _tree[c].NextSibling)
        {
            output.Add((c, depth));
            if (_expanded[c])
            {
                AppendVisibleChildren(c, depth + 1, output);
            }
        }
    }
}

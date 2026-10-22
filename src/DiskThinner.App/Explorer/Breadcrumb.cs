using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>面包屑（原型 .crumb）：显示选中项的路径，每一级都可以点击跳转；路径很深时前面折叠成"…"。</summary>
internal sealed class Breadcrumb : FlowLayoutPanel
{
    private const int MaxSegments = 7;

    public Breadcrumb()
    {
        AutoSize = false;
        WrapContents = false;
        FlowDirection = FlowDirection.LeftToRight;
        BackColor = Color.Transparent;
        Padding = new Padding(0);
        Margin = new Padding(0);
        AccessibleName = L.T("当前位置");
        AccessibleRole = AccessibleRole.ToolBar;
    }

    public event EventHandler<int>? NodeClicked;

    /// <summary>当前显示的各级文字（测试用）。</summary>
    internal IEnumerable<string> Segments => Controls.OfType<Label>().Select(c => c.Text);

    /// <summary>模拟点击某一级（测试用）。</summary>
    internal void ClickSegment(string text)
    {
        if (Controls.OfType<LinkLabel>().FirstOrDefault(l => l.Text == text) is { Tag: int node })
        {
            NodeClicked?.Invoke(this, node);
        }
    }

    public void Show(NodeTree? tree, int node)
    {
        SuspendLayout();
        Controls.Clear();
        if (tree is not null && node >= 0)
        {
            var chain = new List<int>();
            for (var n = node; n >= 0; n = n == tree.Root ? -1 : tree[n].Parent)
            {
                chain.Add(n);
            }

            chain.Reverse();
            var skip = Math.Max(0, chain.Count - MaxSegments);
            if (skip > 0)
            {
                AddSeparator(L.T("…"));
            }

            for (var i = skip; i < chain.Count; i++)
            {
                if (i > skip || skip > 0)
                {
                    AddSeparator("›");
                }

                var n = chain[i];
                var name = tree.GetNameString(n);
                if (i == chain.Count - 1)
                {
                    Controls.Add(MakeLabel(name, Theme.UiBold, Theme.Text));
                }
                else
                {
                    var link = new LinkLabel
                    {
                        Text = name,
                        AutoSize = true,
                        Font = Theme.Ui,
                        LinkColor = Theme.Muted,
                        ActiveLinkColor = Theme.Accent,
                        LinkBehavior = LinkBehavior.HoverUnderline,
                        Margin = new Padding(0, 4, 0, 0),
                        UseMnemonic = false,
                        AccessibleName = L.F("转到 {0}", name),
                        Tag = n,
                    };
                    link.LinkClicked += (_, _) => NodeClicked?.Invoke(this, (int)link.Tag!);
                    Controls.Add(link);
                }
            }
        }

        ResumeLayout();
    }

    /// <summary>显示不对应目录的位置（汇总视图里的组），例如"按归属汇总 › uv"。</summary>
    public void ShowText(params string[] parts)
    {
        SuspendLayout();
        Controls.Clear();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                AddSeparator("›");
            }

            Controls.Add(MakeLabel(parts[i], i == parts.Length - 1 ? Theme.UiBold : Theme.Ui, i == parts.Length - 1 ? Theme.Text : Theme.Muted));
        }

        ResumeLayout();
    }

    private void AddSeparator(string text) => Controls.Add(MakeLabel(text, Theme.Ui, Theme.Subtle));

    private static Label MakeLabel(string text, Font font, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font,
        ForeColor = color,
        Margin = new Padding(2, 4, 2, 0),
        UseMnemonic = false,
    };
}

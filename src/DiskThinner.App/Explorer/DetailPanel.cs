using System.Diagnostics;
using System.Globalization;
using DiskThinner.App.Controls;
using DiskThinner.App.Formatting;
using DiskThinner.App.Rules;
using DiskThinner.Core;
using DiskThinner.Inventory;
using DiskThinner.Rules;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 右侧详情（原型 .ex-detail）：大小和属性，规则给出的归属、类别、作用、删除后影响、证据、处理方式和"从根上解决"。
/// 删除和运行命令在 M4 才实现，这里先展示，按钮置灰；"打开所在位置，自己清理"现在就能用。
/// </summary>
internal sealed class DetailPanel : Panel
{
    private readonly FlowLayoutPanel _content;
    private NodeTree? _tree;
    private int _node = -1;
    private ScanStats? _stats;
    private Classification? _classification;
    private OwnerMap? _owners;
    private SpaceGroup? _group;
    private int _version;

    public DetailPanel()
    {
        AutoScroll = true;
        BackColor = Theme.Surface;
        Padding = new Padding(16, 14, 16, 14);
        AccessibleName = L.T("详情");
        _content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(Padding.Left, Padding.Top),
            BackColor = Color.Transparent,
        };
        Controls.Add(_content);
    }

    /// <summary>"加入清理计划"或"运行 X"：要处理的节点（规则命中的位置）和指定的官方命令（null 表示默认方式）。</summary>
    internal readonly record struct PlanRequest(int Node, string? CommandId);

    public event EventHandler<int>? OpenLocationRequested;
    internal event EventHandler<PlanRequest>? AddToPlanRequested;
    public event EventHandler<int>? CopyPathRequested;
    public event EventHandler<int>? ShowInMapRequested;
    public event EventHandler<int>? SelectRequested;

    private int ContentWidth => Math.Max(120, ClientSize.Width - Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

    /// <summary>当前显示的节点（测试用）。</summary>
    internal int ShownNode => _node;

    /// <summary>当前显示的全部文字（测试用）。</summary>
    internal IEnumerable<string> Texts => Descendants(_content).Select(c => c.Text).Where(t => !string.IsNullOrEmpty(t));

    private static IEnumerable<Control> Descendants(Control c) => c.Controls.Cast<Control>().SelectMany(x => Descendants(x).Prepend(x));

    /// <param name="writes">近 30 天的写入监控汇总（docs/phase2a-design.md 5.9.6 节）；为 null 时不显示 ETW 证据。</param>
    public void ShowNode(NodeTree? tree, int node, ScanStats? stats, Classification? classification = null, OwnerMap? owners = null,
        DiskThinner.Monitor.WriteIndex? writes = null)
    {
        _tree = tree;
        _node = node;
        _stats = stats;
        _classification = classification;
        _owners = classification is null ? null : owners ?? OwnerMap.RulesOnly(classification);
        _writes = writes;
        _group = null;
        Rebuild();
    }

    private DiskThinner.Monitor.WriteIndex? _writes;

    /// <summary>显示汇总视图里的一组（应用或类别）。</summary>
    public void ShowGroup(NodeTree tree, SpaceGroup group, Classification? classification, OwnerMap? owners)
    {
        _tree = tree;
        _node = -1;
        _classification = classification;
        _owners = owners;
        _group = group;
        Rebuild();
    }

    /// <summary>当前显示的组（测试用）。</summary>
    internal SpaceGroup? ShownGroup => _group;

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (_content.Controls.Count > 0 && Math.Abs(_content.Controls[0].Width - ContentWidth) > 8)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        SuspendLayout();
        _content.SuspendLayout();
        foreach (Control c in _content.Controls)
        {
            c.Dispose();
        }

        _content.Controls.Clear();
        AutoScrollPosition = Point.Empty;
        _content.Location = new Point(Padding.Left, Padding.Top);
        _version++;

        if (_tree is not null && _group is not null)
        {
            BuildForGroup(_tree, _group);
        }
        else if (_tree is null || _node < 0)
        {
            _content.Controls.Add(Wrap(Ui.Label(L.T("选中左侧的目录或文件查看详情。"), Theme.Ui, Theme.Muted)));
        }
        else
        {
            BuildFor(_tree, _node);
        }

        _content.ResumeLayout();
        ResumeLayout();
    }

    private void BuildFor(NodeTree tree, int n)
    {
        ref var node = ref tree[n];
        var isDir = node.IsDirectory;
        var path = tree.GetPath(n);
        var width = ContentWidth;
        var c = _classification;
        var rule = c?.RuleOf(n);
        var risk = c?.EffectiveRisk(n);
        var protection = c?.ProtectionOf(n);
        var root = c?.RootOf(n);

        // ---- 标题：名称 + 风险标签 + 属性标签 ----
        var title = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(width, 0), Width = width, Margin = new Padding(0), BackColor = Color.Transparent };
        title.Controls.Add(new Label
        {
            Text = TreeListView.DisplayName(tree, n),
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Font = Theme.Title,
            ForeColor = Theme.Text,
            UseMnemonic = false,
            Margin = new Padding(0, 0, 0, 2),
        });
        if (risk is { } r)
        {
            var style = RuleView.RiskStyle(r);
            title.Controls.Add(Ui.Tag(RuleView.RiskLabel(r, rule), style.Back, style.Fore));
        }

        foreach (var tag in FlagTags(node))
        {
            title.Controls.Add(Ui.Tag(tag, Theme.TagBack, Theme.TagText));
        }

        _content.Controls.Add(title);

        // ---- 路径：可以选中复制 ----
        var pathBox = new TextBox
        {
            Text = path,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Muted,
            Font = Theme.Mono,
            Multiline = true,
            WordWrap = true,
            Width = width,
            TabStop = true,
            AccessibleName = L.T("完整路径"),
            Margin = new Padding(0, 2, 0, 8),
        };
        _content.Controls.Add(pathBox); // 先加入，拿到按 DPI 缩放后的字体再量高度
        var textSize = TextRenderer.MeasureText(path, pathBox.Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        pathBox.Height = textSize.Height + 4;

        // ---- 键值表 ----
        var kv = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Width = width, Margin = new Padding(0), BackColor = Color.Transparent };
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.Scale(this, 76)));
        kv.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var valueWidth = width - Ui.Scale(this, 80);
        void Row(string key, string value, Font? font = null, Color? color = null)
        {
            kv.Controls.Add(new Label { Text = key, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui, Margin = new Padding(0, 3, 4, 3), UseMnemonic = false });
            kv.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(valueWidth, 0), ForeColor = color ?? Theme.Text, Font = font ?? Theme.Ui, Margin = new Padding(0, 3, 0, 3), UseMnemonic = false });
        }

        var size = ByteSize.Format(node.AllocSize);
        Row(L.T("大小"), isDir ? L.F("{0} · {1} 个文件", size, node.FileCount.ToString("N0", CultureInfo.InvariantCulture)) : size, Theme.UiBold);

        if (c is not null && node.AllocSize > 0)
        {
            var cleanable = c.Cleanable(n);
            if (cleanable > 0)
            {
                var shared = root is not null && c.IsRoot(n) ? root.SharedBytes : 0;
                Row(L.T("可释放"), L.F("约 {0}{1}", ByteSize.Format(cleanable), (shared > 1 << 20 ? L.F("（另有 {0} 与别处用硬链接共享，删了也不释放）", ByteSize.Format(shared)) : "")));
            }

            var unexplained = c.Unexplained(n);
            if (isDir && unexplained > 0 && rule is null)
            {
                Row(L.T("未识别"), ByteSize.Format(unexplained), color: ColorTranslator.FromHtml("#5b2a86"));
            }
        }

        var resolved = _owners?.Resolve(n);
        if (resolved is { } ro)
        {
            // 归属 + 来源标签；推断是在上级目录做出的，补一句说明
            var cell = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(valueWidth, 0), Margin = new Padding(0, 1, 0, 1), BackColor = Color.Transparent };
            cell.Controls.Add(new Label { Text = ro.Owner.Display, AutoSize = true, MaximumSize = new Size(valueWidth, 0), ForeColor = Theme.Text, Font = Theme.Ui, Margin = new Padding(0, 2, 4, 0), UseMnemonic = false });
            var (tagText, tagStyle) = RuleView.SourceTag(ro.Owner.Source);
            cell.Controls.Add(new TagBox(tagText, tagStyle));
            if (ro.Root != n && ro.Owner.Source is OwnerSource.Inferred or OwnerSource.Observed)
            {
                cell.SetFlowBreak(cell.Controls[^1], true);
                cell.Controls.Add(new Label { Text = ro.Owner.Source == OwnerSource.Observed ? L.F("按上级目录 {0} 的写入监控", tree.GetNameString(ro.Root)) : L.F("按上级目录 {0} 推断", tree.GetNameString(ro.Root)), AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Small, Margin = new Padding(0, 2, 0, 0), UseMnemonic = false });
            }

            kv.Controls.Add(new Label { Text = L.T("归属"), AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Ui, Margin = new Padding(0, 3, 4, 3), UseMnemonic = false });
            kv.Controls.Add(cell);
        }

        if (rule is not null)
        {
            Row(L.T("类别"), Names.Display(rule.Kind));
            Row(L.T("作用"), rule.Purpose);
            Row(L.T("删除后"), rule.Impact);
        }

        if (isDir)
        {
            Row(L.T("子目录"), L.F("{0} 个", node.DirCount.ToString("N0", CultureInfo.InvariantCulture)));
        }

        var diff = node.LogicalSize - node.AllocSize;
        if (Math.Abs(diff) > 1 << 20 && Math.Abs(diff) > node.AllocSize / 100)
        {
            Row(L.T("逻辑大小"), ByteSize.Format(node.LogicalSize) + (diff > 0 ? L.T("（压缩、稀疏或未下载到本地，实际占用更小）") : L.T("（按簇分配，实际占用更大）")));
        }

        if (n != tree.Root)
        {
            Row(L.T("占上级"), TreeListView.PercentOfParent(tree, n).ToString("0.0", CultureInfo.InvariantCulture) + "%");
        }

        Row(isDir ? L.T("最后写入") : L.T("修改时间"), TimeText.Full(node.LastWriteUtc) + (isDir ? L.T("（含子项）") : ""));
        _content.Controls.Add(kv);

        // ---- 保护和说明 ----
        if (protection is { Level: ProtectionLevel.Hard or ProtectionLevel.Guide or ProtectionLevel.Soft } p)
        {
            var text = p.Level switch
            {
                ProtectionLevel.Hard => L.F("在保护名单里：{0}。DiskThinner 不会删除这里的任何东西。", p.Reason),
                ProtectionLevel.Guide => L.F("{0}。DiskThinner 不会直接删除，只给出处理步骤。", p.Reason),
                _ => L.F("{0}。只有实测过的规则才会在这里给出清理建议，请自己判断。", p.Reason),
            };
            _content.Controls.Add(Callout(text, width, p.Level == ProtectionLevel.Hard));
        }

        foreach (var note in Notes(node, isDir))
        {
            _content.Controls.Add(Callout(note, width, false));
        }

        if (rule is not null && root is not null && !c!.IsRoot(n))
        {
            var link = new LinkLabel
            {
                Text = L.F("规则命中的是上级目录：{0}", tree.GetNameString(root.Node)),
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Font = Theme.Small,
                LinkColor = Theme.Accent,
                Margin = new Padding(0, 8, 0, 0),
                UseMnemonic = false,
            };
            link.LinkClicked += (_, _) => SelectRequested?.Invoke(this, root.Node);
            _content.Controls.Add(link);
        }

        if (c is not null && rule is null && node.AllocSize > 0 && c.Unexplained(n) >= node.AllocSize)
        {
            var hint = resolved is null
                ? L.T("没有规则认识这里。打开所在位置看看内容。")
                : L.T("知道归谁，但没有规则说明这里存的是什么、能不能删。可以在对应软件的设置里找清理选项，或者打开所在位置看看。");
            _content.Controls.Add(Wrap(Ui.Label(hint, Theme.Small, Theme.Muted), new Padding(0, 10, 0, 0)));
        }

        // ---- 证据：归属的依据（本机上看到的程序、已装软件、目录名）+ 规则的依据 + 写入监控（ETW） ----
        var ownerEvidence = OwnerEvidenceFor(resolved);
        var showRuleEvidence = rule is { Evidence.Count: > 0 } && c!.IsRoot(n);
        var etw = Monitor.MonitorEvidence.For(_writes, path, isDir);
        if (ownerEvidence.Count > 0 || showRuleEvidence || etw.Count > 0)
        {
            _content.Controls.Add(SectionTitle(L.T("证据")));
            foreach (var line in etw)
            {
                var label = Ui.Label(line, Theme.Small, Theme.Text);
                label.MaximumSize = new Size(width - TagWidth("ETW"), 0);
                label.Margin = new Padding(0, 2, 0, 0);
                _content.Controls.Add(TaggedItem("ETW", RuleView.ObservedSource, label, width));
            }

            AddOwnerEvidence(ownerEvidence, width);
            if (showRuleEvidence)
            {
                foreach (var ev in rule!.Evidence)
                {
                    _content.Controls.Add(TaggedItem(L.T("规则"), RuleView.RuleSource, EvidenceItem(ev, width - TagWidth(L.T("规则"))), width));
                }

                var tierText = rule.Tier switch
                {
                    Tier.Verified => L.T("已实测"),
                    Tier.Reviewed => L.T("人工审阅"),
                    _ => L.T("草稿，未审阅"),
                };
                _content.Controls.Add(Wrap(Ui.Label(L.F("规则 {0} · {1}", rule.Id, tierText), Theme.Small, Theme.Subtle), new Padding(0, 4, 0, 0)));
            }
        }

        // ---- 操作 ----
        _content.Controls.Add(SectionTitle(L.T("操作")));
        if (rule is not null && Classification.IsCleanable(risk) && Classification.IsActionable(rule) && c!.RootOf(n) is { } matchRoot)
        {
            // 规则的处理方式作用在规则命中的那个位置上（可能是选中项的上级）；都只是加入清理计划，确认后才执行
            var target = matchRoot.Node;
            var first = true;
            foreach (var action in rule.Actions)
            {
                if (action is CommandAction cmd && KnownCommands.Find(cmd.CommandId) is { } known)
                {
                    var run = Ui.Button(L.F("运行 {0}{1}", known.Display, (first ? L.T("（推荐）") : "")), primary: first,
                        accessibleDescription: L.F("{0}。加入清理计划，确认后才运行", known.Description));
                    new ToolTip().SetToolTip(run, known.Description);
                    var id = known.Id;
                    run.Click += (_, _) => AddToPlanRequested?.Invoke(this, new PlanRequest(target, id));
                    _content.Controls.Add(Stretch(run, width));
                    first = false;
                }
            }

            var delete = rule.Actions.OfType<DeleteAction>().FirstOrDefault();
            var add = Ui.Button(L.F("加入清理计划{0}", (delete is null ? "" : L.F("：{0}", DescribeDelete(delete)))), primary: first,
                accessibleDescription: L.T("加入清理计划，确认后才执行"));
            add.Click += (_, _) => AddToPlanRequested?.Invoke(this, new PlanRequest(target, null));
            _content.Controls.Add(Stretch(add, width));
            var scope = target == n ? "" : L.F("按规则处理的是整个 {0}。", PathDisplay.Shorten(_tree!.GetPath(target)));
            _content.Controls.Add(Wrap(Ui.Label(L.F("{0}加入后在清理计划里确认，确认之前不会删除任何东西。", scope), Theme.Small, Theme.Subtle), new Padding(0, 0, 0, 6)));
        }

        foreach (var guide in rule?.Actions.OfType<GuideAction>() ?? [])
        {
            _content.Controls.Add(GuideItem(guide, width));
        }

        var open = Ui.Button(isDir ? L.T("打开所在位置，自己清理") : L.T("在资源管理器中显示，自己清理"),
            accessibleDescription: L.T("用资源管理器打开，DiskThinner 不会删除任何东西"));
        open.Click += (_, _) => OpenLocationRequested?.Invoke(this, n);
        _content.Controls.Add(Stretch(open, width));

        var copy = Ui.Button(L.T("复制路径"));
        copy.Click += (_, _) => CopyPathRequested?.Invoke(this, n);
        _content.Controls.Add(Stretch(copy, width));

        if (isDir && node.FirstChild >= 0)
        {
            var map = Ui.Button(L.T("在方块图中只显示这个目录"));
            map.Click += (_, _) => ShowInMapRequested?.Invoke(this, n);
            _content.Controls.Add(Stretch(map, width));
        }

        // ---- 从根上解决 ----
        if (rule?.Relocate is { } relocate && c!.IsRoot(n))
        {
            _content.Controls.Add(SectionTitle(L.T("从根上解决")));
            var text = relocate.Env is { } env
                ? L.F("设置环境变量 {0}，把它放到其他盘，以后不再占 C 盘。{1}", env, (relocate.Note is null ? "" : relocate.Note))
                : relocate.Note ?? "";
            _content.Controls.Add(Callout(text, width, false, info: true));
            if (relocate.Env is { } envName)
            {
                var copyCmd = Ui.Button(L.T("复制设置命令"));
                copyCmd.Click += (_, _) => Shell.ShellActions.CopyPath($"[Environment]::SetEnvironmentVariable('{envName}', 'D:\\{envName}', 'User')");
                copyCmd.AccessibleDescription = L.T("复制一条 PowerShell 命令，按需修改路径后运行");
                _content.Controls.Add(copyCmd);
                _content.Controls.Add(Wrap(Ui.Label(L.T("命令里的 D:\\ 路径只是示例，请改成你想放的位置；改完后要重新打开相关程序，并删除旧位置的内容。"), Theme.Small, Theme.Subtle), new Padding(0, 4, 0, 0)));
            }
        }
    }

    /// <summary>归属的证据：规则写明的应用用本机上找到的程序位置，推断的用推断时记下的依据。</summary>
    private IReadOnlyList<OwnerEvidence> OwnerEvidenceFor((Owner Owner, int Root)? resolved)
    {
        if (resolved is not { } r || _owners is null || _classification is not { } c)
        {
            return [];
        }

        if (r.Owner.Source == OwnerSource.Rule)
        {
            return c.RuleOf(r.Root)?.AppId is { } appId ? _owners.AppEvidence(appId) : [];
        }

        return r.Owner.Evidence;
    }

    /// <summary>列出归属证据；第一个带程序路径的证据，在后台查它的数字签名，查到后补一行。</summary>
    private void AddOwnerEvidence(IReadOnlyList<OwnerEvidence> evidence, int width)
    {
        foreach (var ev in evidence)
        {
            var label = Ui.Label(ev.Text, Theme.Small, Theme.Text);
            label.MaximumSize = new Size(width - TagWidth(ev.KindText), 0);
            label.Margin = new Padding(0, 2, 0, 0);
            _content.Controls.Add(TaggedItem(ev.KindText, RuleView.EvidenceStyle(ev.Kind), label, width));
        }

        if (evidence.FirstOrDefault(e => e.ExePath is not null && e.Kind is OwnerEvidenceKind.Program or OwnerEvidenceKind.Process or OwnerEvidenceKind.CommandLine)
                ?.ExePath is not { } exe || !File.Exists(exe))
        {
            return;
        }

        var name = Path.GetFileName(exe);
        var text = Ui.Label(L.F("正在检查 {0} 的数字签名…", name), Theme.Small, Theme.Subtle);
        text.MaximumSize = new Size(width - TagWidth(L.T("签名")), 0);
        text.Margin = new Padding(0, 2, 0, 0);
        _content.Controls.Add(TaggedItem(L.T("签名"), RuleView.ObservedSource, text, width));
        var version = _version;
        CheckSignature();

        async void CheckSignature()
        {
            SignatureInfo sig;
            try
            {
                sig = await Task.Run(() => ExeIdentityReader.ReadSignature(exe));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                sig = SignatureInfo.None;
            }

            if (version != _version || text.IsDisposed)
            {
                return; // 期间已经换成别的节点
            }

            text.ForeColor = Theme.Text;
            text.Text = sig switch
            {
                { Signer: null } => L.F("{0} 没有嵌入的数字签名", name),
                { Valid: true } => L.F("{0} 由 {1} 签名（签名有效）", name, sig.Signer),
                _ => L.F("{0} 由 {1} 签名（无法验证，代码 0x{2:X8}）", name, sig.Signer, sig.Status),
            };
        }
    }

    private int TagWidth(string text) =>
        TextRenderer.MeasureText(text, Theme.Small, Size.Empty, TextFormatFlags.NoPadding).Width + Ui.Scale(this, 8) + Ui.Scale(this, 8);

    /// <summary>证据的一行：左边一个来源标签，右边文字。</summary>
    private static Control TaggedItem(string tag, RuleView.TagStyle style, Control content, int width)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, MaximumSize = new Size(width, 0), Margin = new Padding(0, 2, 0, 2), BackColor = Color.Transparent };
        row.Controls.Add(new TagBox(tag, style));
        row.Controls.Add(content);
        return row;
    }

    /// <summary>汇总视图里选中一组时：总量、可释放、最大的几个位置（可点击）、归属的依据。</summary>
    private void BuildForGroup(NodeTree tree, SpaceGroup group)
    {
        var width = ContentWidth;
        var title = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(width, 0), Width = width, Margin = new Padding(0), BackColor = Color.Transparent };
        title.Controls.Add(new Label { Text = group.Name, AutoSize = true, MaximumSize = new Size(width, 0), Font = Theme.Title, ForeColor = Theme.Text, UseMnemonic = false, Margin = new Padding(0, 0, 6, 2) });
        if (group.Owner is { } owner)
        {
            var (tag, style) = RuleView.SourceTag(owner.Source);
            title.Controls.Add(new TagBox(tag, style) { Margin = new Padding(0, 6, 0, 0) });
        }
        else if (group.IsUnknown)
        {
            title.Controls.Add(new TagBox(L.T("未识别"), RuleView.Unknown) { Margin = new Padding(0, 6, 0, 0) });
        }

        _content.Controls.Add(title);

        var total = Math.Max(1, tree[tree.Root].AllocSize);
        var lines = new List<string>
        {
            L.F("共 {0}，占扫描总量的 {1}%", ByteSize.Format(group.Bytes), (group.Bytes * 100.0 / total).ToString("0.0", CultureInfo.InvariantCulture)),
            L.F("分布在 {0} 个位置", group.LocationCount.ToString("N0", CultureInfo.InvariantCulture)),
        };
        if (group.CleanableBytes > 0)
        {
            lines.Add(L.F("其中约 {0} 可以清理", ByteSize.Format(group.CleanableBytes)));
        }

        foreach (var line in lines)
        {
            _content.Controls.Add(Wrap(Ui.Label(line, line == lines[0] ? Theme.UiBold : Theme.Ui, Theme.Text), new Padding(0, 4, 0, 0)));
        }

        if (group.IsUnknown)
        {
            _content.Controls.Add(Callout(group.Kind is null && group.Owner is null && group.Name == L.T("未识别")
                ? L.T("没有规则认识这些位置。打开所在位置看看内容。")
                : L.T("没有规则认识、也推断不出归属的位置。打开所在位置看看内容。"), width, false));
        }

        _content.Controls.Add(SectionTitle(L.T("最大的几个位置")));
        foreach (var loc in group.Locations.Where(l => !l.IsRemainder).Take(8))
        {
            var path = PathDisplay.Shorten(tree.GetPath(loc.Node)) + (loc.IsParentRemainder ? L.T("（其余部分）") : "");
            var link = new LinkLabel
            {
                Text = $"{path} · {ByteSize.Format(loc.Bytes)}",
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Font = Theme.Small,
                LinkColor = Theme.Accent,
                Margin = new Padding(0, 2, 0, 2),
                UseMnemonic = false,
                AccessibleDescription = L.T("选中这个位置"),
            };
            var node = loc.Node;
            link.LinkClicked += (_, _) => SelectRequested?.Invoke(this, node);
            _content.Controls.Add(link);
        }

        if (group.Owner is { Source: OwnerSource.Inferred or OwnerSource.Observed } inferred)
        {
            _content.Controls.Add(SectionTitle(L.T("证据")));
            AddOwnerEvidence(inferred.Evidence, width);
        }
        else if (group.Owner is { Source: OwnerSource.Rule } && _owners is not null && _classification is { } c
                 && group.Locations.FirstOrDefault(l => l.Rule >= 0 && c.Engine.Rules.Locations[l.Rule].AppId is not null) is { } withApp)
        {
            var evidence = _owners.AppEvidence(c.Engine.Rules.Locations[withApp.Rule].AppId!);
            if (evidence.Count > 0)
            {
                _content.Controls.Add(SectionTitle(L.T("证据")));
                AddOwnerEvidence(evidence, width);
            }
        }

        _content.Controls.Add(Wrap(Ui.Label(L.T("双击列表里的位置，可以在目录视图里定位。"), Theme.Small, Theme.Subtle), new Padding(0, 12, 0, 0)));
    }

    /// <summary>原型的来源标签（.s-rule / .s-infer / .s-obs）：自绘，推断是虚线框。</summary>
    private sealed class TagBox : Control
    {
        private readonly RuleView.TagStyle _style;

        public TagBox(string text, RuleView.TagStyle style)
        {
            _style = style;
            Text = text;
            Font = Theme.Small;
            Margin = new Padding(0, 2, 6, 0);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            AccessibleRole = AccessibleRole.StaticText;
            AccessibleName = text;
            Size = GetPreferredSize(Size.Empty);
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            var t = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var h = Math.Max(t.Height + 4, Ui.Scale(this, 17));
            var pad = Math.Max(4, h / 4);
            return new Size(t.Width + pad * 2, h);
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            Size = GetPreferredSize(Size.Empty);
        }

        protected override void OnPaint(PaintEventArgs e) => RuleView.DrawTag(e.Graphics, Text, Font, Point.Empty, Height, _style);
    }

    private static string DescribeDelete(DeleteAction d)
    {
        var what = d.Scope switch
        {
            DeleteScope.Children => L.T("逐个删除子项"),
            DeleteScope.Self => L.T("删除"),
            _ => L.T("清空内容"),
        };
        var filters = new List<string>();
        if (d.OlderThanDays is { } days)
        {
            filters.Add(L.F("只删 {0} 天前的", days));
        }

        if (d.KeepLatest is { } keep)
        {
            filters.Add(L.F("保留最新的 {0} 个", keep));
        }

        return filters.Count == 0 ? what : L.F("{0}（{1}）", what, string.Join(L.T("，"), filters));
    }

    private IEnumerable<string> Notes(Node node, bool isDir)
    {
        var f = node.Flags;
        if ((f & NodeFlags.Metadata) != 0)
        {
            yield return L.T("NTFS 的内部文件，由文件系统自己管理，不能删除。");
        }

        if ((f & NodeFlags.HardLinkSecondary) != 0)
        {
            yield return L.T("这是同一个文件的另一个硬链接名，大小已经算在别处；只删这个名字不会释放空间。");
        }
        else if ((f & NodeFlags.HardLinked) != 0)
        {
            yield return L.T("这个文件在别的目录里还有硬链接名（大小算在这里）。只删其中一个名字不会释放空间。");
        }

        if ((f & NodeFlags.AccessDenied) != 0)
        {
            yield return L.T("没有权限列出这个目录的内容，显示的大小偏小。以管理员身份重启后用 MFT 扫描可以看到。");
        }
        else if ((f & NodeFlags.Incomplete) != 0)
        {
            yield return L.T("这个目录下有没权限列出的子目录，显示的大小偏小。");
        }

        if ((f & NodeFlags.ReparsePoint) != 0 && isDir)
        {
            yield return L.T("这是 junction 或符号链接，指向别的位置，没有展开计算，大小为 0。");
        }

        if ((f & NodeFlags.CloudPlaceholder) != 0)
        {
            yield return L.T("云文件占位符（如 OneDrive 按需下载），内容不在本地，不占空间。");
        }

        if (_stats?.Mode == ScanMode.Walk && (isDir || node.AllocSize > 0))
        {
            yield return L.T("当前是遍历扫描：占用按簇估算，正在写入的文件可能偏小，硬链接会重复计算。");
        }
    }

    private static IEnumerable<string> FlagTags(Node node)
    {
        var f = node.Flags;
        if ((f & NodeFlags.Metadata) != 0) yield return L.T("NTFS 元数据");
        if ((f & NodeFlags.System) != 0) yield return L.T("系统");
        if ((f & NodeFlags.Hidden) != 0) yield return L.T("隐藏");
        if ((f & NodeFlags.ReparsePoint) != 0) yield return L.T("链接");
        if ((f & NodeFlags.HardLinked) != 0) yield return L.T("硬链接");
        if ((f & NodeFlags.Compressed) != 0) yield return L.T("压缩");
        if ((f & NodeFlags.Sparse) != 0) yield return L.T("稀疏");
        if ((f & NodeFlags.CloudPlaceholder) != 0) yield return L.T("云文件");
    }

    private Control SectionTitle(string text)
    {
        var l = Ui.Label(text, Theme.SmallBold, Theme.Muted);
        l.Margin = new Padding(0, 16, 0, 6);
        return l;
    }

    private Control Wrap(Label label, Padding? margin = null)
    {
        label.MaximumSize = new Size(ContentWidth, 0);
        label.Margin = margin ?? new Padding(0);
        return label;
    }

    private Control EvidenceItem(Evidence ev, int width)
    {
        if (ev.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            var link = new LinkLabel
            {
                Text = ev.Note ?? uri.Host + uri.AbsolutePath,
                AutoSize = true,
                MaximumSize = new Size(width, 0),
                Font = Theme.Small,
                LinkColor = Theme.Accent,
                Margin = new Padding(0, 2, 0, 2),
                UseMnemonic = false,
                AccessibleDescription = L.F("在浏览器中打开 {0}", url),
            };
            new ToolTip().SetToolTip(link, url);
            link.LinkClicked += (_, _) => OpenUrl(url);
            return link;
        }

        return Wrap(Ui.Label(ev.Note ?? ev.Url ?? "", Theme.Small, Theme.Muted), new Padding(0, 2, 0, 2));
    }

    private Control GuideItem(GuideAction guide, int width)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = width, Margin = new Padding(0, 0, 0, 6), BackColor = Color.Transparent };
        panel.Controls.Add(Callout(guide.Text, width, false, info: true));
        if (guide.Url is { } url && url.StartsWith("https://", StringComparison.Ordinal))
        {
            var link = new LinkLabel { Text = L.T("查看官方说明"), AutoSize = true, Font = Theme.Small, LinkColor = Theme.Accent, Margin = new Padding(0, 2, 0, 0) };
            link.LinkClicked += (_, _) => OpenUrl(url);
            panel.Controls.Add(link);
        }

        return panel;
    }

    /// <summary>只打开规则里的 https 链接，交给系统默认浏览器。</summary>
    private void OpenUrl(string url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(FindForm(), L.F("无法打开浏览器：{0}", ex.Message), "DiskThinner", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private Control Callout(string text, int width, bool danger, bool info = false)
    {
        var (bar, back, fore) = danger
            ? (ColorTranslator.FromHtml("#a4262c"), ColorTranslator.FromHtml("#fde7e9"), ColorTranslator.FromHtml("#6b1419"))
            : info
                ? (Theme.Accent, Theme.AccentWeak, Theme.Text)
                : (Theme.WarnBorder, Theme.WarnBack, Theme.WarnText);
        var panel = new CalloutPanel(bar)
        {
            Width = width,
            Padding = new Padding(Ui.Scale(this, 10), 6, 8, 6),
            Margin = new Padding(0, 8, 0, 0),
            BackColor = back,
            // 不设名字时 WinForms 会借用前面一个标签的文字，读屏软件会读出不相干的内容
            AccessibleName = danger ? L.T("警告") : info ? L.T("说明") : L.T("注意"),
            AccessibleRole = AccessibleRole.Grouping,
        };
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(width - panel.Padding.Horizontal, 0),
            Font = Theme.Small,
            ForeColor = fore,
            Location = new Point(panel.Padding.Left, panel.Padding.Top),
            UseMnemonic = false,
        };
        panel.Controls.Add(label);
        panel.Height = label.PreferredHeight + panel.Padding.Vertical;
        return panel;
    }

    private static Control Stretch(Button b, int width)
    {
        b.AutoSize = false;
        b.Width = width;
        b.Height = Math.Max(28, b.PreferredSize.Height);
        b.TextAlign = ContentAlignment.MiddleLeft;
        b.Margin = new Padding(0, 0, 0, 6);
        return b;
    }

    /// <summary>原型 .callout：左侧 3 像素竖条。</summary>
    private sealed class CalloutPanel(Color bar) : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var brush = new SolidBrush(bar);
            e.Graphics.FillRectangle(brush, 0, 0, Ui.Scale(this, 3), Height);
        }
    }
}

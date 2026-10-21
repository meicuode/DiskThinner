using DiskThinner.App.Controls;
using DiskThinner.App.Settings;
using DiskThinner.App.Shell;
using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.App.Pages;

/// <summary>
/// 设置（原型 .p-settings）：左边是分类，右边是各组卡片。快照、清理、规则三组可用；
/// 写入监控（阶段 2）和 AI（阶段 3）显示为后续版本。改动立即保存到 settings.json。
/// </summary>
internal sealed class SettingsPage : UserControl
{
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Background };
    private readonly TableLayoutPanel _stack = new() { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = Point.Empty, BackColor = Color.Transparent };
    private readonly Dictionary<string, Control> _sections = [];

    private readonly CheckBox _daily = new() { Text = "每天", AutoSize = true, Font = Theme.Ui, AccessibleName = "每日快照" };
    private readonly ComboBox _dailyTime = Combo("每日快照的时间", Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToArray(), width: 100);
    private readonly Label _dailyHint = Hint("");
    private readonly CheckBox _auto = new() { Text = "扫描整个盘后，距离上次快照超过 6 小时就自动保存一次", AutoSize = true, Font = Theme.Ui };
    private readonly ComboBox _minDir = Combo("只记录大于", AppSettings.MinDirChoices.Select(m => $"{m} MB 的目录").ToArray());
    private readonly ComboBox _keep = Combo("快照保留时长", AppSettings.KeepDayChoices.Select(d => $"{d} 天").ToArray());
    private readonly Label _dbSize = Ui.Label("", Theme.Small, Theme.Muted);
    private readonly ComboBox _tempAge = Combo("临时文件最小年龄", ["只删 24 小时前的", "只删 7 天前的"]);
    private readonly ComboBox _locked = Combo("被占用的文件", ["跳过并记录", "询问是否关闭程序"]);
    private readonly ListBox _protected = new() { Height = 96, Width = 420, Font = Theme.Ui, IntegralHeight = false, AccessibleName = "我添加的保护目录" };
    private readonly Button _remove = Ui.Button("移除");
    private readonly Label _rules = Ui.Label("", Theme.Ui);
    private readonly Label _paths = Ui.Label("", Theme.Small, Theme.Muted);
    private AppSettings _settings = new();
    private bool _binding;

    public SettingsPage()
    {
        BackColor = Theme.Background;
        Padding = new Padding(20, 16, 20, 16);
        AccessibleName = "设置";

        var title = Ui.Label("设置", new Font(Theme.UiFontName, 15F, FontStyle.Bold));
        title.Dock = DockStyle.Top;
        title.Padding = new Padding(0, 0, 0, 12);

        var nav = new FlowLayoutPanel { Dock = DockStyle.Left, Width = 150, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 4, 12, 0) };
        nav.AccessibleName = "设置分类";

        AddSection(nav, "写入监控", Card("写入监控（ETW）",
            Row("", Later("关闭", "只用扫描和每日快照，看得到哪里变了，看不到是谁写的。不需要管理员权限。", true)),
            Row("", Later("临时诊断", "需要时手动开启 1 到 24 小时，到时自动停止。不安装后台服务。", false)),
            Row("", Later("常驻服务", "安装 Windows 服务持续记录。服务只做监控，不联网，不删除文件。", false)),
            Row("", Hint("写入监控在后续版本（阶段 2）提供，这一版固定为\u201c关闭\u201d。"))));

        var dailyRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _daily.Margin = new Padding(0, 4, 8, 0);
        dailyRow.Controls.AddRange([_daily, _dailyTime]);
        var keepRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        _dbSize.Margin = new Padding(8, 6, 0, 0);
        keepRow.Controls.AddRange([_keep, _dbSize]);
        AddSection(nav, "快照", Card("快照",
            Row("每日快照", Stack(dailyRow, _dailyHint)),
            Row("扫描后保存", _auto),
            Row("只记录大于", Stack(_minDir, Hint("更小的目录并入上级。改小会让快照库变大；文件固定为 100 MB 以上才单独记录。"))),
            Row("保留", Stack(keepRow, Hint("这么多天内的快照全部保留，更早的每周只留一个。")))));

        AddSection(nav, "AI", Card("AI",
            Row("", Hint("AI 分析在后续版本（阶段 3）提供：接口配置、发送前预览、MCP 服务都会放在这里。DiskThinner 现在不联网。"))));

        var protectButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        var showBuiltIn = Ui.Button("查看内置保护名单");
        var add = Ui.Button("添加我的保护目录");
        showBuiltIn.Margin = new Padding(0, 0, 8, 0);
        add.Margin = new Padding(0, 0, 8, 0);
        _remove.Margin = new Padding(0);
        protectButtons.Controls.AddRange([showBuiltIn, add, _remove]);
        _protected.Margin = new Padding(0, 8, 0, 0);
        AddSection(nav, "清理", Card("清理",
            Row("临时文件", Stack(_tempAge, Hint("避免删掉正在安装或运行的程序刚写的临时文件。规则本身更严格时以规则为准。"))),
            Row("被占用的文件", Stack(_locked, Hint("询问时，同意了才请程序正常关闭，不会强制结束。确认清理时还可以再改。"))),
            Row("保护名单", Stack(protectButtons, _protected,
                Hint("内置名单不能删除，只能追加。添加的目录和里面的所有内容都不会被清理：执行清理时立即生效，清理计划在重新扫描后更新。")))));

        var openData = Ui.Button("打开数据目录");
        openData.Margin = new Padding(0, 8, 0, 0);
        AddSection(nav, "规则", Card("规则",
            Row("内置规则", _rules),
            Row("本地规则", Hint("后续版本提供（保存 AI 结论为本地规则、导入、导出）。")),
            Row("数据位置", Stack(_paths, openData))));

        _scroll.Controls.Add(_stack);
        _scroll.Resize += (_, _) => FitWidth();
        Controls.Add(_scroll);
        Controls.Add(nav);
        Controls.Add(title);

        _daily.CheckedChanged += (_, _) =>
        {
            if (!_binding)
            {
                ScheduleChanged?.Invoke(this, (_daily.Checked, (string)_dailyTime.SelectedItem!));
            }
        };
        _dailyTime.SelectedIndexChanged += (_, _) =>
        {
            if (_binding)
            {
                return;
            }

            Change(_settings with { Snapshot = _settings.Snapshot with { DailyTime = (string)_dailyTime.SelectedItem! } });
            if (_daily.Checked)
            {
                ScheduleChanged?.Invoke(this, (true, (string)_dailyTime.SelectedItem!));
            }
        };
        _auto.CheckedChanged += (_, _) => Change(_settings with { Snapshot = _settings.Snapshot with { AutoAfterScan = _auto.Checked } });
        _minDir.SelectedIndexChanged += (_, _) => Change(_settings with { Snapshot = _settings.Snapshot with { MinDirMb = AppSettings.MinDirChoices[_minDir.SelectedIndex] } });
        _keep.SelectedIndexChanged += (_, _) => Change(_settings with { Snapshot = _settings.Snapshot with { KeepDays = AppSettings.KeepDayChoices[_keep.SelectedIndex] } });
        _tempAge.SelectedIndexChanged += (_, _) => Change(_settings with { Cleanup = _settings.Cleanup with { TempMinAgeDays = AppSettings.TempAgeChoices[_tempAge.SelectedIndex] } });
        _locked.SelectedIndexChanged += (_, _) => Change(_settings with { Cleanup = _settings.Cleanup with { AskWhenLocked = _locked.SelectedIndex == 1 } });
        showBuiltIn.Click += (_, _) => ShowBuiltInProtection();
        add.Click += (_, _) => AddProtected();
        _remove.Click += (_, _) => RemoveProtected();
        _protected.SelectedIndexChanged += (_, _) => _remove.Enabled = _protected.SelectedIndex >= 0;
        openData.Click += (_, _) =>
        {
            var dir = Path.GetDirectoryName(AppSettings.DefaultPath)!;
            Directory.CreateDirectory(dir);
            ShellActions.OpenLocation(dir, isDirectory: true, FindForm());
        };
        Bind(_settings);
    }

    /// <summary>设置改了（已经是规范化的值），由主窗口保存并应用。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>开启或关闭每日快照（注册或移除计划任务）。</summary>
    public event EventHandler<(bool Enabled, string Time)>? ScheduleChanged;

    public AppSettings Settings => _settings;

    internal ListBox ProtectedList => _protected;

    internal ComboBox TempAgeBox => _tempAge;

    public void SetSettings(AppSettings settings) => Bind(settings);

    public void SetSchedule(ScheduledSnapshot.Status status)
    {
        _binding = true;
        _daily.Checked = status.Registered;
        if (status.Registered && status.Time is { } t && !_dailyTime.Items.Contains(t))
        {
            _dailyTime.Items.Add(t);
        }

        if (status.Registered && status.Time is { } time)
        {
            _dailyTime.SelectedItem = time;
        }

        _binding = false;
        _dailyHint.Text = status.Registered
            ? status.Highest ? "已开启，以最高权限运行（MFT 扫描，几秒完成）。" : "已开启，以普通权限运行（遍历扫描，较慢）。以管理员身份打开 DiskThinner 再开启可以改用 MFT。"
            : "通过 Windows 计划任务运行 DiskThinner.exe snapshot，不需要打开界面。";
    }

    public void SetInfo(int rules, string? rulesError, long snapshotDbBytes)
    {
        _rules.Text = rulesError is null ? $"{rules} 条 · 随程序更新" : "没有加载：" + rulesError;
        _dbSize.Text = snapshotDbBytes > 0 ? $"当前占用 {ByteSize.Format(snapshotDbBytes)}" : "";
        var root = Path.GetDirectoryName(AppSettings.DefaultPath)!;
        _paths.Text = $"设置：{PathDisplay.Shorten(AppSettings.DefaultPath)}\n快照：{PathDisplay.Shorten(Path.Combine(root, "snapshots.db"))}\n" +
            $"操作记录：{PathDisplay.Shorten(Path.Combine(root, "user.db"))}\n日志：{PathDisplay.Shorten(AppLog.Directory)}";
    }

    private void Bind(AppSettings s)
    {
        _binding = true;
        _settings = s;
        _dailyTime.SelectedItem = _dailyTime.Items.Contains(s.Snapshot.DailyTime) ? s.Snapshot.DailyTime : "12:00";
        _auto.Checked = s.Snapshot.AutoAfterScan;
        _minDir.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.MinDirChoices, s.Snapshot.MinDirMb));
        _keep.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.KeepDayChoices, s.Snapshot.KeepDays));
        _tempAge.SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.TempAgeChoices, s.Cleanup.TempMinAgeDays));
        _locked.SelectedIndex = s.Cleanup.AskWhenLocked ? 1 : 0;
        _protected.Items.Clear();
        foreach (var p in s.Cleanup.ProtectedPaths)
        {
            _protected.Items.Add(p);
        }

        _remove.Enabled = false;
        _binding = false;
    }

    private void Change(AppSettings next)
    {
        if (_binding)
        {
            return;
        }

        _settings = next.Normalized();
        SettingsChanged?.Invoke(this, _settings);
    }

    private void AddProtected()
    {
        using var dlg = new FolderBrowserDialog { Description = "选择不希望被清理的目录（里面的所有内容都会受保护）", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        var path = Path.GetFullPath(dlg.SelectedPath).TrimEnd('\\');
        if (!AppSettings.IsValidProtectedPath(path))
        {
            MessageBox.Show(FindForm(), "这个位置不能作为保护目录：需要是本机磁盘上的目录，路径里不能有 * ? { }，也不能是整个盘。", "保护名单", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_settings.Cleanup.ProtectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var next = _settings with { Cleanup = _settings.Cleanup with { ProtectedPaths = [.. _settings.Cleanup.ProtectedPaths, path] } };
        Bind(next.Normalized());
        SettingsChanged?.Invoke(this, _settings);
    }

    private void RemoveProtected()
    {
        if (_protected.SelectedItem is not string path)
        {
            return;
        }

        var next = _settings with { Cleanup = _settings.Cleanup with { ProtectedPaths = _settings.Cleanup.ProtectedPaths.Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase)).ToList() } };
        Bind(next.Normalized());
        SettingsChanged?.Invoke(this, _settings);
    }

    private void ShowBuiltInProtection()
    {
        using var dlg = new Form
        {
            Text = "内置保护名单",
            Font = Theme.Ui,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96F, 96F),
            ClientSize = new Size(820, 480),
            MinimizeBox = false,
            ShowInTaskbar = false,
        };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, AccessibleName = "内置保护名单" };
        list.Columns.Add("位置", 420);
        list.Columns.Add("级别", 90);
        list.Columns.Add("原因", 280);
        foreach (var e in ProtectionList.BuiltIn)
        {
            list.Items.Add(new ListViewItem([e.Pattern, e.Level == ProtectionLevel.None ? "例外（可清理）" : ProtectionList.Display(e.Level), e.Reason]));
        }

        var note = new Label { Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(10, 8, 10, 4), Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false,
            Text = "保护名单优先于任何规则：硬保护的位置任何规则都不能清理；\u201c系统设置\u201d只给引导；\u201c用户文件\u201d只有实测过的规则可以提出建议。\u201c例外\u201d是在受保护的目录里放开的子目录（如 Windows\\Temp）。" };
        dlg.Controls.Add(list);
        dlg.Controls.Add(note);
        dlg.Load += (_, _) =>
        {
            int[] widths = [420, 90, 280];
            for (var c = 0; c < widths.Length; c++)
            {
                list.Columns[c].Width = Ui.Scale(dlg, widths[c]);
            }

            note.Height = Ui.Scale(dlg, 44);
        };
        dlg.ShowDialog(FindForm());
    }

    private void AddSection(FlowLayoutPanel nav, string name, Control card)
    {
        _sections[name] = card;
        card.Margin = new Padding(0, 0, 0, 12);
        _stack.Controls.Add(card);
        var link = new LinkLabel { Text = name, AutoSize = true, Font = Theme.Ui, LinkColor = Theme.Text, ActiveLinkColor = Theme.Accent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 4, 0, 8) };
        link.LinkClicked += (_, _) => _scroll.ScrollControlIntoView(card);
        nav.Controls.Add(link);
    }

    /// <summary>卡片自动长高，宽度跟着页面：用最小 / 最大宽度把自动大小的宽度定住。</summary>
    private void FitWidth()
    {
        var width = Math.Max(400, _scroll.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
        foreach (Control card in _stack.Controls)
        {
            if (card.MinimumSize.Width != width)
            {
                card.MinimumSize = new Size(width, 0);
                card.MaximumSize = new Size(width, 0);
            }
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        FitWidth();
    }

    /// <summary>卡片里滚动时不要跳回顶部（和概览一样）。</summary>
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    private static Panel Card(string title, params Control[] rows)
    {
        var card = new Panel { BackColor = Theme.Card, Padding = new Padding(18, 14, 18, 10), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        var table = new TableLayoutPanel { Location = new Point(18, 14), ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0) };
        var heading = Ui.Label(title, Theme.PageTitle);
        heading.Margin = new Padding(0, 0, 0, 8);
        table.Controls.Add(heading);
        foreach (var r in rows)
        {
            table.Controls.Add(r);
        }

        card.Controls.Add(table);
        card.AccessibleName = title;
        return card;
    }

    /// <summary>原型的 .form-row：左边标签，右边内容。</summary>
    private static TableLayoutPanel Row(string label, Control content)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 6) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var lbl = Ui.Label(label, Theme.Ui, Theme.Muted);
        lbl.Margin = new Padding(0, 5, 8, 0);
        row.Controls.Add(lbl, 0, 0);
        row.Controls.Add(content, 1, 0);
        if (!string.IsNullOrEmpty(label))
        {
            content.AccessibleName ??= label;
        }

        return row;
    }

    private static FlowLayoutPanel Stack(params Control[] items)
    {
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        flow.Controls.AddRange(items);
        return flow;
    }

    private static Label Hint(string text)
    {
        var l = Ui.Label(text, Theme.Small, Theme.Muted);
        l.MaximumSize = new Size(620, 0);
        l.Margin = new Padding(0, 4, 0, 0);
        return l;
    }

    /// <summary>后续版本的选项：显示但不能选。</summary>
    private static Control Later(string title, string detail, bool selected)
    {
        var flow = Stack(
            new RadioButton { Text = title + (selected ? "" : "（后续版本）"), Checked = selected, Enabled = false, AutoSize = true, Font = Theme.Ui, Margin = new Padding(0) },
            Hint(detail));
        flow.Margin = new Padding(0, 0, 0, 4);
        return flow;
    }

    private static ComboBox Combo(string name, string[] items, int width = 220)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Font = Theme.Ui, AccessibleName = name, Margin = new Padding(0) };
        c.Items.AddRange(items);
        return c;
    }
}

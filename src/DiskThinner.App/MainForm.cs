using System.Security.Principal;

namespace DiskThinner.App;

/// <summary>
/// 阶段 0 的主窗口骨架：左侧导航 + 内容区 + 状态栏。
/// 页面内容在 M1 之后按原型逐页实现。
/// </summary>
internal sealed class MainForm : Form
{
    // 阶段 1 的导航项。"AI 分析"在阶段 3 才加入。
    private static readonly string[] Pages = ["概览", "空间浏览", "增长分析", "清理计划", "操作记录", "设置"];

    private readonly Label _content;

    public MainForm()
    {
        Text = "DiskThinner";
        Font = new Font("Microsoft YaHei UI", 9F);
        MinimumSize = new Size(960, 600);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.CenterScreen;

        var nav = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 168,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(243, 243, 243),
            AccessibleName = "导航",
            AccessibleRole = AccessibleRole.List,
        };

        _content = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.DimGray,
            AccessibleRole = AccessibleRole.StaticText,
        };

        foreach (var page in Pages)
        {
            var button = new Button
            {
                Text = page,
                Width = 148,
                Height = 34,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                AccessibleName = page,
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => ShowPage(page);
            nav.Controls.Add(button);
        }

        var status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel(IsElevated() ? "管理员模式" : "普通权限（MFT 快速扫描需要管理员）")
        {
            AccessibleName = "权限状态",
        });

        Controls.Add(_content);
        Controls.Add(nav);
        Controls.Add(status);

        ShowPage(Pages[0]);
    }

    private void ShowPage(string page) => _content.Text = $"{page}\n\n阶段 0 骨架，页面内容在 M1 之后按原型实现。";

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

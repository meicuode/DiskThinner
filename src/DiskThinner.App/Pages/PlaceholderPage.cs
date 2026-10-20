namespace DiskThinner.App.Pages;

/// <summary>还没实现的页面：说明会在哪个里程碑做，避免用户以为功能坏了。</summary>
internal sealed class PlaceholderPage : UserControl
{
    public PlaceholderPage(string title, string milestone, string description)
    {
        BackColor = Theme.Background;
        var heading = new Label
        {
            Text = title,
            AutoSize = true,
            Font = Theme.PageTitle,
            ForeColor = Theme.Text,
            Location = new Point(20, 16),
        };
        var body = new Label
        {
            Text = $"这个页面会在 {milestone} 实现：{description}\n\n现在可以先用左侧的\u201c空间浏览\u201d查看哪里占用最大。",
            AutoSize = true,
            MaximumSize = new Size(640, 0),
            Font = Theme.Ui,
            ForeColor = Theme.Muted,
            Location = new Point(20, 52),
            UseMnemonic = false,
        };
        Controls.Add(heading);
        Controls.Add(body);
        AccessibleName = title;
    }
}

using DiskThinner.App;
using DiskThinner.App.Rules;
using DiskThinner.Rules;

namespace DiskThinner.App.Tests;

/// <summary>
/// 文字和背景的对比度（研发方案 4 节：按 WCAG 2.x AA 设计）。普通文字至少 4.5:1；
/// 界面上用来辨认状态的图形（焦点框、选中竖条、展开三角）至少 3:1。
/// 列的是界面代码里实际用到的搭配，新加颜色搭配时在这里补一行。是否真正达标还要用"讲述人"和高对比度主题实测。
/// </summary>
public class ContrastTests
{
    private static Color Hex(string html) => ColorTranslator.FromHtml(html);

    /// <summary>WCAG 2.x 的相对亮度。</summary>
    internal static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    internal static double Ratio(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    [Fact]
    public void FormulaMatchesKnownValues()
    {
        Assert.Equal(21.0, Ratio(Color.Black, Color.White), 2);
        Assert.Equal(1.0, Ratio(Color.White, Color.White), 2);
        Assert.Equal(4.54, Ratio(Hex("#767676"), Color.White), 2); // 白底上最浅的 AA 灰
    }

    public static TheoryData<string, string, string> TextPairs()
    {
        var data = new TheoryData<string, string, string>();
        void Add(string name, Color fore, Color back) => data.Add(name, ColorTranslator.ToHtml(fore), ColorTranslator.ToHtml(back));

        foreach (var (backName, back) in new[]
        {
            ("Background", Theme.Background), ("Surface", Theme.Surface), ("Card", Theme.Card), ("HeaderBack", Theme.HeaderBack),
            ("TitleBar", Theme.TitleBar), ("RowHover", Theme.RowHover),
        })
        {
            Add("Text / " + backName, Theme.Text, back);
            Add("Muted / " + backName, Theme.Muted, back);
            Add("Subtle / " + backName, Theme.Subtle, back);
            Add("Accent / " + backName, Theme.Accent, back);
        }

        Add("Text / RowSelected", Theme.Text, Theme.RowSelected);
        Add("Muted / HeaderBack（清理计划里不能勾选的行被选中）", Theme.Muted, Theme.HeaderBack);
        Add("导航文字 / Surface", Hex("#333333"), Theme.Surface);
        Add("导航文字 / NavHover", Hex("#333333"), Theme.NavHover);
        Add("导航选中 Accent / NavSelected", Theme.Accent, Theme.NavSelected);
        Add("Chip 选中 Accent / AccentWeak", Theme.Accent, Theme.AccentWeak);
        Add("主按钮 白字 / Accent", Color.White, Theme.Accent);
        Add("主按钮 白字 / AccentHover", Color.White, Theme.AccentHover);
        Add("普通按钮 Text / NavHover（按下）", Theme.Text, Theme.NavHover);
        Add("增长 Up / Card", Theme.Up, Theme.Card);
        Add("减少 Down / Card", Theme.Down, Theme.Card);
        Add("提示条 WarnText / WarnBack", Theme.WarnText, Theme.WarnBack);
        Add("标签 TagText / TagBack", Theme.TagText, Theme.TagBack);
        Add("未开启监控标签 Subtle / #f3f3f3", Theme.Subtle, Hex("#f3f3f3"));
        Add("详情面板 未识别 / Card", Hex("#5b2a86"), Theme.Card);
        Add("写入监控胶囊 Text / NavHover（悬停）", Theme.Text, Theme.NavHover);
        Add("写入监控胶囊 Accent / NavHover（悬停）", Theme.Accent, Theme.NavHover);
        Add("写入监控胶囊 Subtle / NavHover（悬停）", Theme.Subtle, Theme.NavHover);
        Add("覆盖时段 丢失率偏高 WarnText / Card", Theme.WarnText, Theme.Card);
        Add("开启写入监控对话框 账户提示 WarnText / WarnBack", Theme.WarnText, Theme.WarnBack);
        Add("开启写入监控对话框 注意事项 Text / Background", Theme.Text, Theme.Background);
        Add("未监控标签", RuleView.NotMonitored.Fore, RuleView.NotMonitored.Back);
        Add("详情面板 警告框文字", Hex("#6b1419"), Hex("#fde7e9"));
        foreach (var risk in Enum.GetValues<Risk>())
        {
            var s = RuleView.RiskStyle(risk);
            Add("风险标签 " + risk, s.Fore, s.Back);
        }

        foreach (var (name, s) in new[] { ("规则", RuleView.RuleSource), ("推断", RuleView.InferredSource), ("未识别", RuleView.Unknown), ("实测", RuleView.ObservedSource) })
        {
            Add("来源标签 " + name, s.Fore, s.Back);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TextPairs))]
    public void TextMeetsAa(string name, string fore, string back)
    {
        var ratio = Ratio(Hex(fore), Hex(back));
        Assert.True(ratio >= 4.5, $"{name}：{fore} 在 {back} 上对比度 {ratio:0.00}:1，AA 要求普通文字至少 4.5:1");
    }

    public static TheoryData<string, string, string> GraphicPairs()
    {
        var data = new TheoryData<string, string, string>();
        void Add(string name, Color fore, Color back) => data.Add(name, ColorTranslator.ToHtml(fore), ColorTranslator.ToHtml(back));
        Add("焦点框 Accent / Card", Theme.Accent, Theme.Card);
        Add("焦点框 Accent / NavSelected", Theme.Accent, Theme.NavSelected);
        Add("导航选中竖条 Accent / NavSelected", Theme.Accent, Theme.NavSelected);
        Add("展开三角 #666666 / Card", Hex("#666666"), Theme.Card);
        Add("展开三角 #666666 / RowSelected", Hex("#666666"), Theme.RowSelected);
        Add("方块图选中框 #111111 / Unknown 类别色", Hex("#111111"), RuleView.ColorOf(RuleView.Group.Unknown));
        Add("方块图选中框 #111111 / 虚拟磁盘类别色", Hex("#111111"), RuleView.ColorOf(RuleView.Group.VmDisk));
        Add("写入监控胶囊圆点 关闭 / Background", Controls.StatusPill.Idle, Theme.Background);
        Add("写入监控胶囊圆点 诊断中 / Background", Controls.StatusPill.Running, Theme.Background);
        Add("写入监控胶囊圆点 残留 / Background", Controls.StatusPill.Warning, Theme.Background);
        Add("写入监控胶囊圆点 关闭 / NavHover", Controls.StatusPill.Idle, Theme.NavHover);
        Add("覆盖时段 Accent / TitleBar 底条", Theme.Accent, Theme.TitleBar);
        return data;
    }

    [Theory]
    [MemberData(nameof(GraphicPairs))]
    public void StateGraphicsMeetAa(string name, string fore, string back)
    {
        var ratio = Ratio(Hex(fore), Hex(back));
        Assert.True(ratio >= 3.0, $"{name}：{fore} 在 {back} 上对比度 {ratio:0.00}:1，AA 要求界面图形至少 3:1");
    }
}

using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.App.Rules;

/// <summary>规则结果在界面上的表现：风险标签的颜色、类别颜色、归属文字。颜色取自原型（.r-* 和 .c-* 样式）。</summary>
internal static class RuleView
{
    public readonly record struct TagStyle(Color Back, Color Fore, Color? Border = null, bool Dashed = false);

    public static TagStyle RiskStyle(Risk risk) => risk switch
    {
        Risk.Safe => new(ColorTranslator.FromHtml("#e1f3df"), ColorTranslator.FromHtml("#0d5f0d")),
        Risk.Low => new(ColorTranslator.FromHtml("#e1edf9"), ColorTranslator.FromHtml("#0b4f8c")),
        Risk.Medium => new(ColorTranslator.FromHtml("#fff0c2"), ColorTranslator.FromHtml("#6e4600")),
        Risk.High => new(ColorTranslator.FromHtml("#fde3e5"), ColorTranslator.FromHtml("#9c1f25")),
        Risk.Guide => new(ColorTranslator.FromHtml("#e8e8e8"), ColorTranslator.FromHtml("#3d3d3d")),
        _ => new(ColorTranslator.FromHtml("#595959"), Color.White),
    };

    /// <summary>来源标签（原型 .s-*）：规则 = 实线灰框，推断 = 虚线框，实测 = 蓝框。</summary>
    public static readonly TagStyle RuleSource = new(Color.White, ColorTranslator.FromHtml("#404040"), ColorTranslator.FromHtml("#b4b4b4"));
    public static readonly TagStyle InferredSource = new(Color.White, ColorTranslator.FromHtml("#404040"), ColorTranslator.FromHtml("#9a9a9a"), Dashed: true);
    public static readonly TagStyle Unknown = new(ColorTranslator.FromHtml("#f8f2fd"), ColorTranslator.FromHtml("#5b2a86"), ColorTranslator.FromHtml("#d6c1ea"));

    /// <summary>风险标签的文字：规则可以给一个更具体的标签（如"用 DISM 清理"）。</summary>
    public static string RiskLabel(Risk risk, LocationRule? rule) =>
        rule?.Label is { } label && risk == rule.Risk ? label : Names.Display(risk);

    /// <summary>方块图和图例用的类别分组。</summary>
    public enum Group { System, Program, Cache, Temp, Package, UserData, VmDisk, Unknown, Other }

    public static Group GroupOf(Kind kind) => kind switch
    {
        Kind.System or Kind.Backup => Group.System,
        Kind.Program => Group.Program,
        Kind.Cache or Kind.BrowserData => Group.Cache,
        Kind.Temp or Kind.CrashDump or Kind.Log or Kind.RecycleBin => Group.Temp,
        Kind.PackageCache or Kind.UpdateCache or Kind.Model or Kind.BuildOutput => Group.Package,
        Kind.UserData or Kind.SessionHistory or Kind.AppData or Kind.Config or Kind.Credential => Group.UserData,
        Kind.VmDisk => Group.VmDisk,
        _ => Group.Other,
    };

    public static Color ColorOf(Group g) => g switch
    {
        Group.System => ColorTranslator.FromHtml("#b3b9c0"),
        Group.Program => ColorTranslator.FromHtml("#9fb2cc"),
        Group.Cache => ColorTranslator.FromHtml("#8dbbe8"),
        Group.Temp => ColorTranslator.FromHtml("#f2b072"),
        Group.Package => ColorTranslator.FromHtml("#86cbbf"),
        Group.UserData => ColorTranslator.FromHtml("#a7d394"),
        Group.VmDisk => ColorTranslator.FromHtml("#7d858f"),
        Group.Unknown => ColorTranslator.FromHtml("#c3b5e3"),
        _ => ColorTranslator.FromHtml("#dadde2"),
    };

    public static string NameOf(Group g) => g switch
    {
        Group.System => L.T("系统"),
        Group.Program => L.T("程序"),
        Group.Cache => L.T("缓存"),
        Group.Temp => L.T("临时 / 转储 / 日志"),
        Group.Package => L.T("包缓存 / 下载"),
        Group.UserData => L.T("用户与应用数据"),
        Group.VmDisk => L.T("虚拟磁盘"),
        Group.Unknown => L.T("未识别"),
        _ => L.T("其他"),
    };

    public static readonly Group[] LegendOrder =
        [Group.System, Group.Program, Group.Cache, Group.Temp, Group.Package, Group.UserData, Group.VmDisk, Group.Unknown];

    /// <summary>实测（原型 .s-obs）：本机上看到的事实，例如程序就装在这里、正在运行。</summary>
    public static readonly TagStyle ObservedSource = new(Color.White, Theme.Accent, ColorTranslator.FromHtml("#8db8e3"));

    /// <summary>归属的来源标签：规则 = 实线框，推断 = 虚线框。</summary>
    public static (string Text, TagStyle Style) SourceTag(OwnerSource source) => source == OwnerSource.Rule ? (L.T("规则"), RuleSource) : (L.T("推断"), InferredSource);

    /// <summary>证据的标签样式：本机上看到的（程序、进程、命令行工具）用实测样式，按名称对上的用推断样式。</summary>
    public static TagStyle EvidenceStyle(OwnerEvidenceKind kind) => kind switch
    {
        OwnerEvidenceKind.Program or OwnerEvidenceKind.Process or OwnerEvidenceKind.CommandLine => ObservedSource,
        _ => InferredSource,
    };

    /// <summary>画一个原型样式的小标签，返回占用的宽度。</summary>
    public static int DrawTag(Graphics g, string text, Font font, Point location, int height, TagStyle style)
    {
        var size = TextRenderer.MeasureText(g, text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var pad = Math.Max(4, height / 4);
        var rect = new Rectangle(location.X, location.Y, size.Width + pad * 2, height);
        using (var back = new SolidBrush(style.Back))
        {
            g.FillRectangle(back, rect);
        }

        if (style.Border is { } border)
        {
            using var pen = new Pen(border) { DashStyle = style.Dashed ? System.Drawing.Drawing2D.DashStyle.Dash : System.Drawing.Drawing2D.DashStyle.Solid };
            g.DrawRectangle(pen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        }

        TextRenderer.DrawText(g, text, font, rect, style.Fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return rect.Width;
    }
}

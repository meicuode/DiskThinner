using System.Globalization;
using System.Resources;

namespace DiskThinner.Core;

/// <summary>
/// 界面文字的本地化（研发方案 1.2 节：字符串放进 resx，以后补英文界面）。
///
/// 和 ASP.NET Core 的 IStringLocalizer、gettext 一样，源代码里的中文就是键：<c>L.T("清理计划")</c>、
/// <c>L.F("扫描 {0} 项", count)</c>。代码里能直接看到界面上的文字，翻译时只需要一份
/// Strings.&lt;语言&gt;.resx，键是中文原文，值是译文；没有译文时显示中文。
///
/// 全部键都在 Strings.resx 里（由 tools/i18n 从源代码生成，CI 检查是否同步，也检查有没有没包起来的中文）。
/// 现在只有中文（中性语言 zh-Hans），<see cref="T"/> 直接返回原文，不查表。
/// </summary>
public static class L
{
    /// <summary>源代码里的文字用的语言。</summary>
    public static readonly CultureInfo Neutral = CultureInfo.GetCultureInfo("zh-Hans");

    private static readonly ResourceManager Resources = new("DiskThinner.Core.Strings", typeof(L).Assembly);
    private static CultureInfo _culture = Neutral;
    private static bool _neutral = true;

    /// <summary>
    /// 界面语言。默认是中文，和系统语言无关；以后有了设置项，要在程序启动、任何界面和静态字段初始化之前设置
    /// （有些文字在静态字段里，只在第一次用到时取一次）。
    /// </summary>
    public static CultureInfo Culture
    {
        get => _culture;
        set
        {
            _culture = value;
            _neutral = IsNeutral(value);
        }
    }

    /// <summary>界面文字：当前语言的译文，没有译文时返回原文。</summary>
    public static string T(string text) => _neutral ? text : Resources.GetString(text, _culture) ?? text;

    /// <summary>带参数的界面文字（复合格式：{0}、{1:N0}……），数字和日期按当前区域格式化。</summary>
    public static string F(string format, params ReadOnlySpan<object?> args) =>
        string.Format(CultureInfo.CurrentCulture, T(format), args);

    /// <summary>zh-Hans 以及它下面的 zh-CN、zh-SG 都直接用原文。</summary>
    private static bool IsNeutral(CultureInfo culture)
    {
        for (var c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (string.Equals(c.Name, Neutral.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

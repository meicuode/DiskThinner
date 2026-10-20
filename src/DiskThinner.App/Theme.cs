namespace DiskThinner.App;

/// <summary>界面配色与字体，取值来自 HTML 原型（prototype/index.html 的 CSS 变量）。</summary>
internal static class Theme
{
    public static readonly Color Background = ColorTranslator.FromHtml("#f3f3f3");
    public static readonly Color Surface = ColorTranslator.FromHtml("#f9f9f9");
    public static readonly Color Card = Color.White;
    public static readonly Color Border = ColorTranslator.FromHtml("#e1e1e1");
    public static readonly Color BorderStrong = ColorTranslator.FromHtml("#c4c4c4");
    public static readonly Color Text = ColorTranslator.FromHtml("#1a1a1a");
    public static readonly Color Muted = ColorTranslator.FromHtml("#5c5c5c");
    public static readonly Color Subtle = ColorTranslator.FromHtml("#808080");
    public static readonly Color Accent = ColorTranslator.FromHtml("#0a5fb4");
    public static readonly Color AccentHover = ColorTranslator.FromHtml("#09539d");
    public static readonly Color AccentWeak = ColorTranslator.FromHtml("#e6f0fa");
    public static readonly Color TitleBar = ColorTranslator.FromHtml("#eceef1");

    public static readonly Color NavHover = ColorTranslator.FromHtml("#eaecef");
    public static readonly Color NavSelected = ColorTranslator.FromHtml("#dfe9f6");
    public static readonly Color RowSelected = ColorTranslator.FromHtml("#d9e8f8");
    public static readonly Color RowHover = ColorTranslator.FromHtml("#f4f8fc");
    public static readonly Color RowLine = ColorTranslator.FromHtml("#f0f0f0");
    public static readonly Color HeaderBack = ColorTranslator.FromHtml("#fafafa");

    public static readonly Color BarBack = ColorTranslator.FromHtml("#ececec");
    public static readonly Color BarFill = ColorTranslator.FromHtml("#6fa0d6");
    public static readonly Color FolderIcon = ColorTranslator.FromHtml("#e3b448");
    public static readonly Color FileIcon = ColorTranslator.FromHtml("#c3c8ce");

    public static readonly Color Up = ColorTranslator.FromHtml("#a4262c");
    public static readonly Color Down = ColorTranslator.FromHtml("#0d5f0d");

    public static readonly Color WarnBorder = ColorTranslator.FromHtml("#c19c00");
    public static readonly Color WarnBack = ColorTranslator.FromHtml("#fff8db");
    public static readonly Color WarnText = ColorTranslator.FromHtml("#4d3b00");

    public static readonly Color TagBack = ColorTranslator.FromHtml("#e8e8e8");
    public static readonly Color TagText = ColorTranslator.FromHtml("#3d3d3d");

    public const string UiFontName = "Microsoft YaHei UI";
    public const string MonoFontName = "Cascadia Mono";

    public static readonly Font Ui = new(UiFontName, 9F);
    public static readonly Font UiBold = new(UiFontName, 9F, FontStyle.Bold);
    public static readonly Font Small = new(UiFontName, 8.25F);
    public static readonly Font SmallBold = new(UiFontName, 8.25F, FontStyle.Bold);
    public static readonly Font Title = new(UiFontName, 12F, FontStyle.Bold);
    public static readonly Font PageTitle = new(UiFontName, 11F, FontStyle.Bold);
    public static readonly Font Mono = CreateMono();

    /// <summary>Windows 11 用 Segoe Fluent Icons，Windows 10 用 Segoe MDL2 Assets。</summary>
    public static readonly Font Icons = CreateIconFont();

    private static Font CreateMono()
    {
        using var probe = new Font(MonoFontName, 8.25F);
        return probe.Name == MonoFontName ? new Font(MonoFontName, 8.25F) : new Font("Consolas", 8.5F);
    }

    private static Font CreateIconFont()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using var probe = new Font(name, 10F);
            if (probe.Name == name)
            {
                return new Font(name, 10F);
            }
        }

        return new Font("Segoe UI Symbol", 10F);
    }
}

namespace DiskThinner.App;

/// <summary>
/// 可选的 AI 模块（docs/phase3-design.md 4.2 节）。主程序的其他代码只通过这个接口使用 AI；
/// 不带 AI 模块构建（-p:DiskThinnerAi=false）时 <see cref="OptionalAi.Current"/> 为 null，所有 AI 入口都不出现。
/// 本文件始终编译，不引用 DiskThinner.AI。
/// </summary>
internal interface IOptionalAi
{
    /// <summary>设置里"AI"一组的内容：标签、正文、说明（说明可以为空）。</summary>
    IReadOnlyList<(string Label, string Text, string Hint)> SettingsRows { get; }
}

internal static partial class OptionalAi
{
    /// <summary>AI 模块；没有编译进来时为 null。</summary>
    public static IOptionalAi? Current { get; } = Create();

    private static IOptionalAi? Create()
    {
        IOptionalAi? ai = null;
        CreateCore(ref ai);
        return ai;
    }

    // 实现在 Ai/AiFeature.cs（只在 DiskThinnerAi=true 时编译）。没有实现时编译器连同调用一起去掉，不用 #if，也不用反射。
    static partial void CreateCore(ref IOptionalAi? ai);
}

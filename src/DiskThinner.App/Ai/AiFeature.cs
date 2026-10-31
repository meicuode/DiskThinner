using DiskThinner.AI;

// 只在 DiskThinnerAi=true 时编译（见 DiskThinner.App.csproj）。切掉 AI 时删除整个 Ai 文件夹。
namespace DiskThinner.App
{
    internal static partial class OptionalAi
    {
        static partial void CreateCore(ref IOptionalAi? ai) => ai = new Ai.AiFeature();
    }
}

namespace DiskThinner.App.Ai
{
    /// <summary>AI 模块在主程序里的实现：把界面接到 DiskThinner.AI 上。</summary>
    internal sealed class AiFeature : IOptionalAi
    {
        public IReadOnlyList<(string Label, string Text, string Hint)> SettingsRows => AiModule.SettingsRows();
    }
}

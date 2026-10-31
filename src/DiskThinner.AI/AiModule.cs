using DiskThinner.Core;

namespace DiskThinner.AI;

/// <summary>AI 模块的说明文字和版本（设置里"AI"一组用）。</summary>
public static class AiModule
{
    /// <summary>证据卡格式的版本，写进证据卡的 format 字段。</summary>
    public const string EvidenceFormat = "diskthinner.evidence/v1";

    /// <summary>设置里"AI"一组的内容：标签、正文、说明。</summary>
    public static IReadOnlyList<(string Label, string Text, string Hint)> SettingsRows() =>
    [
        (L.T("AI 分析提示词"),
            L.T("在空间浏览里选中规则不认识的目录，点\"生成 AI 分析提示词\"，复制到任意 AI 对话里。"),
            L.T("DiskThinner 不联网。提示词里只有目录结构和统计信息，用户名已替换；只有你自己粘贴时，这些信息才会离开这台电脑。")),
        (L.T("AI 接口"),
            L.T("后续版本提供：直接调用 OpenAI 兼容接口（含中转站）或 Anthropic 接口，发送前预览，MCP 服务。"),
            ""),
    ];
}

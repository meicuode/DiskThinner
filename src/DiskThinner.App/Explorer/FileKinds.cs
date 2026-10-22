using DiskThinner.Core;
using DiskThinner.Scanner;

namespace DiskThinner.App.Explorer;

/// <summary>
/// 方块图的着色分类。M1 还没有规则，先按扩展名粗分；M2 规则上线后改为按规则类别着色（原型的图例）。
/// 颜色取自原型的 .c-* 样式。
/// </summary>
internal enum FileKind
{
    Other,
    Program,
    TempLog,
    Archive,
    Media,
    Data,
    System,
}

internal static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        Add(FileKind.Program, ".exe", ".dll", ".sys", ".msi", ".msp", ".mui", ".ocx", ".node", ".pyd", ".so", ".winmd", ".pak", ".asar", ".bin", ".cat");
        Add(FileKind.TempLog, ".tmp", ".temp", ".log", ".etl", ".dmp", ".mdmp", ".hdmp", ".bak", ".old", ".evtx", ".trace");
        Add(FileKind.Archive, ".zip", ".7z", ".rar", ".cab", ".nupkg", ".whl", ".tar", ".gz", ".tgz", ".xz", ".zst", ".jar", ".vsix", ".appx", ".msix", ".msixbundle", ".iso", ".esd", ".wim", ".crx");
        Add(FileKind.Media, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".mp3", ".wav", ".flac", ".m4a", ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".psd", ".txt", ".md");
        Add(FileKind.Data, ".db", ".sqlite", ".sqlite3", ".ldb", ".mdb", ".edb", ".idx", ".cache", ".dat", ".pack", ".blob", ".jsonl", ".gguf", ".safetensors", ".ckpt", ".onnx", ".pt");
        Add(FileKind.System, ".vhdx", ".vhd", ".vmdk", ".vdi", ".avhdx");
    }

    private static void Add(FileKind kind, params string[] extensions)
    {
        foreach (var e in extensions)
        {
            ByExtension[e] = kind;
        }
    }

    public static FileKind Classify(NodeTree tree, int node)
    {
        ref var n = ref tree[node];
        if ((n.Flags & NodeFlags.Metadata) != 0)
        {
            return FileKind.System;
        }

        var name = tree.GetName(node);
        if (node != tree.Root && tree[node].Parent == tree.Root &&
            (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)))
        {
            return FileKind.System;
        }

        var dot = name.LastIndexOf('.');
        if (dot <= 0)
        {
            return FileKind.Other;
        }

        return ByExtension.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name[dot..], out var kind) ? kind : FileKind.Other;
    }

    public static Color ColorOf(FileKind kind) => kind switch
    {
        FileKind.Program => ColorTranslator.FromHtml("#9fb2cc"),
        FileKind.TempLog => ColorTranslator.FromHtml("#f2b072"),
        FileKind.Archive => ColorTranslator.FromHtml("#86cbbf"),
        FileKind.Media => ColorTranslator.FromHtml("#a7d394"),
        FileKind.Data => ColorTranslator.FromHtml("#8dbbe8"),
        FileKind.System => ColorTranslator.FromHtml("#7d858f"),
        _ => ColorTranslator.FromHtml("#dadde2"),
    };

    public static string NameOf(FileKind kind) => kind switch
    {
        FileKind.Program => L.T("程序"),
        FileKind.TempLog => L.T("临时 / 日志 / 转储"),
        FileKind.Archive => L.T("压缩包 / 安装包"),
        FileKind.Media => L.T("文档 / 媒体"),
        FileKind.Data => L.T("数据库 / 缓存 / 模型"),
        FileKind.System => L.T("系统 / 虚拟磁盘"),
        _ => L.T("其他"),
    };

    public static readonly FileKind[] LegendOrder =
        [FileKind.System, FileKind.Program, FileKind.Data, FileKind.TempLog, FileKind.Archive, FileKind.Media, FileKind.Other];
}

using DiskThinner.Core;

namespace DiskThinner.Inventory;

/// <summary>已装软件的来源。</summary>
public enum AppSource
{
    /// <summary>注册表卸载项（HKLM、HKCU、WOW6432Node）。</summary>
    Registry,

    /// <summary>商店应用（已注册的 AppX / MSIX 包）。</summary>
    Store,

    /// <summary>npm 全局包。</summary>
    Npm,

    /// <summary>scoop 安装的应用。</summary>
    Scoop,

    /// <summary>PATH 和 ~\.local\bin 等目录里的命令行工具。</summary>
    CommandLine,

    /// <summary>正在运行的进程。</summary>
    Process,
}

/// <summary>一个已装软件（或命令行工具、运行中的程序）。</summary>
public sealed record InstalledApp
{
    /// <summary>显示名，已去掉版本号和架构（"Postman x64 11.2.0" → "Postman"）。</summary>
    public required string Name { get; init; }

    public required AppSource Source { get; init; }

    /// <summary>原始显示名。商店应用可能是 @{包名?ms-resource://...} 这样的间接字符串，用到时再解析。</summary>
    public string? RawName { get; init; }

    /// <summary>厂商，已去掉公司后缀（"JetBrains s.r.o." → "JetBrains"）。</summary>
    public string? Publisher { get; init; }

    public string? Version { get; init; }

    /// <summary>安装位置（完整路径，不带结尾的 \）。</summary>
    public string? InstallLocation { get; init; }

    /// <summary>主程序路径（注册表的 DisplayIcon、命令行工具、进程映像）。</summary>
    public string? ExePath { get; init; }

    /// <summary>来源里的标识：注册表键名、商店包族名、npm 包名、exe 文件名。</summary>
    public string? Id { get; init; }

    public string SourceText => Source switch
    {
        AppSource.Registry => L.T("已装软件"),
        AppSource.Store => L.T("商店应用"),
        AppSource.Npm => L.T("npm 全局包"),
        AppSource.Scoop => "scoop",
        AppSource.CommandLine => L.T("命令行工具"),
        _ => L.T("运行中的程序"),
    };
}

/// <summary>一次收集到的已装软件清单。</summary>
public sealed class InventorySnapshot(IReadOnlyList<InstalledApp> apps, TimeSpan duration, IReadOnlyList<string> errors)
{
    public static readonly InventorySnapshot Empty = new([], TimeSpan.Zero, []);

    public IReadOnlyList<InstalledApp> Apps { get; } = apps;

    public TimeSpan Duration { get; } = duration;

    /// <summary>某个来源读取失败的说明（不影响其他来源）。</summary>
    public IReadOnlyList<string> Errors { get; } = errors;

    public int Count(AppSource source) => Apps.Count(a => a.Source == source);
}

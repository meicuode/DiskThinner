using DiskThinner.Core;

namespace DiskThinner.Rules;

/// <summary>
/// 允许规则引用的官方清理命令（白名单，写死在程序里）。规则只能写编号，不能写任意命令行。
/// 执行时（M4）只调用 Inventory 探测到的那个 exe，参数按数组传递，不经过 shell。
/// </summary>
public sealed record KnownCommand(string Id, string Exe, IReadOnlyList<string> Arguments, string Description)
{
    public string Display => Exe.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).Replace(".cmd", "", StringComparison.OrdinalIgnoreCase) + " " + string.Join(' ', Arguments);
}

public static class KnownCommands
{
    public static readonly IReadOnlyList<KnownCommand> All =
    [
        new("uv.cache-clean", "uv.exe", ["cache", "clean"], L.T("清空 uv 缓存")),
        new("uv.cache-prune", "uv.exe", ["cache", "prune"], L.T("只删除不再使用的缓存条目")),
        new("pip.cache-purge", "pip.exe", ["cache", "purge"], L.T("清空 pip 缓存")),
        new("npm.cache-clean", "npm.cmd", ["cache", "clean", "--force"], L.T("清空 npm 缓存")),
        new("pnpm.store-prune", "pnpm.exe", ["store", "prune"], L.T("删除 pnpm 仓库里没有被任何项目引用的包")),
        new("yarn.cache-clean", "yarn.cmd", ["cache", "clean"], L.T("清空 Yarn 缓存")),
        new("nuget.http-cache", "dotnet.exe", ["nuget", "locals", "http-cache", "--clear"], L.T("清空 NuGet 下载缓存")),
        new("nuget.global-packages", "dotnet.exe", ["nuget", "locals", "global-packages", "--clear"], L.T("清空 NuGet 全局包目录")),
        new("nuget.plugins-cache", "dotnet.exe", ["nuget", "locals", "plugins-cache", "--clear"], L.T("清空 NuGet 插件缓存")),
        new("go.clean-cache", "go.exe", ["clean", "-cache"], L.T("清空 Go 构建缓存")),
        new("go.clean-modcache", "go.exe", ["clean", "-modcache"], L.T("清空 Go 模块缓存")),
        new("conda.clean-all", "conda.exe", ["clean", "--all", "--yes"], L.T("清理 conda 的包缓存和索引缓存")),
    ];

    public static KnownCommand? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}

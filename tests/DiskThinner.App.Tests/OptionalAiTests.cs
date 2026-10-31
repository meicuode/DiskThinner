using System.Reflection;
using DiskThinner.App.Pages;

namespace DiskThinner.App.Tests;

/// <summary>
/// 可选的 AI 模块（docs/phase3-design.md 4.2 节）。这些测试在两种构建下都编译、都要通过：
/// 默认构建（带 AI）和 -p:DiskThinnerAi=false（不带 AI，tools/verify/run.ps1 noai、CI 的 no-ai）。
/// </summary>
public class OptionalAiTests
{
    private static Assembly App => typeof(SettingsPage).Assembly;

    private static bool AppReferencesAi => App.GetReferencedAssemblies().Any(a => a.Name == "DiskThinner.AI");

    [Fact]
    public void ModulePresentExactlyWhenReferenced()
    {
        Assert.Equal(AppReferencesAi, OptionalAi.Current is not null);
    }

    [Fact]
    public void SettingsHasAiSectionOnlyWithModule() => UiThread.Run(() =>
    {
        using var page = new SettingsPage();
        Assert.Equal(OptionalAi.Current is not null, page.SectionNames.Contains("AI"));
    });

    /// <summary>
    /// 不联网：主程序和它引用的所有 DiskThinner 程序集都不引用 System.Net.Http、System.Net.Sockets
    /// （常驻服务的命名管道在 System.IO.Pipes 里，不算）。以后 AI 接口加进来时，只有 DiskThinner.AI 可以例外（5.7.1 节）。
    /// </summary>
    [Fact]
    public void NoAssemblyReferencesNetworking()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Assembly>([App]);
        while (queue.Count > 0)
        {
            var asm = queue.Dequeue();
            if (!seen.Add(asm.GetName().Name!))
            {
                continue;
            }

            foreach (var r in asm.GetReferencedAssemblies())
            {
                Assert.False(r.Name is "System.Net.Http" or "System.Net.Sockets", $"{asm.GetName().Name} 引用了 {r.Name}");
                if (r.Name!.StartsWith("DiskThinner", StringComparison.Ordinal))
                {
                    queue.Enqueue(Assembly.Load(r));
                }
            }
        }

        Assert.Contains("DiskThinner.Rules", seen);
    }
}

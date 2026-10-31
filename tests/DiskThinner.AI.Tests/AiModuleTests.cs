using DiskThinner.AI;

namespace DiskThinner.AI.Tests;

public class AiModuleTests
{
    [Fact]
    public void SettingsRowsSayNoNetwork()
    {
        var rows = AiModule.SettingsRows();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Hint.Contains("不联网", StringComparison.Ordinal));
    }

    /// <summary>本轮 AI 模块不联网（docs/phase3-design.md 1.2 节）：不引用网络程序集。</summary>
    [Fact]
    public void DoesNotReferenceNetworking()
    {
        var refs = typeof(AiModule).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("System.Net.Http", refs);
        Assert.DoesNotContain("System.Net.Sockets", refs);
    }
}

using DiskThinner.Rules;
using DiskThinner.Scanner;
using static DiskThinner.Rules.Tests.Helpers;
namespace DiskThinner.Rules.Tests;
/// <summary>
/// 规则匹配的语义（dir / file / under、条件、汇总、硬链接）和保护名单（研发方案 M2 验收要求的专门测试）
/// 都写在 JSON fixtures 里：tests/fixtures/matching/*.json，由 <see cref="FixtureTests"/> 运行。
/// 这里只留 fixtures 表达不了的检查。
/// </summary>
public class RuleEngineTests
{
    private static readonly NodeTree Sample = Tree(
        (@"C:\Users\demo\AppData\Local\Temp\x.tmp", 500),
        (@"C:\Users\demo\Documents\report.docx", 900),
        (@"C:\Windows\System32\kernel32.dll", 800),
        (@"C:\pagefile.sys", 5000),
        (@"C:\Unknown\blob.bin", 700));
    [Fact]
    public void UnexplainedHotspotsDoNotOverlap()
    {
        var c = Classify(Sample, Rule("temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp"));
        var spots = c.UnexplainedHotspots(600);
        Assert.Contains(spots, s => Sample.GetPath(s.Node) == @"C:\pagefile.sys");
        Assert.Contains(spots, s => Sample.GetPath(s.Node) == @"C:\Unknown\blob.bin");
        var total = spots.Sum(s => s.Bytes);
        Assert.True(total <= c.Unexplained(Sample.Root));
    }
}

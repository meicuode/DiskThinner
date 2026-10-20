using DiskThinner.Rules;
using DiskThinner.Scanner;
using static DiskThinner.Rules.Tests.Helpers;

namespace DiskThinner.Rules.Tests;

public class RuleEngineTests
{
    private static readonly NodeTree Sample = Tree(
        (@"C:\Users\demo\AppData\Local\uv\cache\archive\a.whl", 4000),
        (@"C:\Users\demo\AppData\Local\uv\python\py.exe", 1000),
        (@"C:\Users\demo\AppData\Local\Temp\x.tmp", 500),
        (@"C:\Users\demo\AppData\Local\Temp\sub\y.tmp", 300),
        (@"C:\Users\demo\AppData\Roaming\Code\Cache\data_1", 200),
        (@"C:\Users\demo\AppData\Roaming\Code\Local Storage\leveldb\000003.log", 50),
        (@"C:\Users\demo\AppData\Roaming\Other\Cache\data", 70),
        (@"C:\Users\demo\.ssh\id_ed25519", 1),
        (@"C:\Users\demo\Documents\report.docx", 900),
        (@"C:\Users\other\AppData\Local\Temp\z.tmp", 100),
        (@"C:\Windows\System32\kernel32.dll", 800),
        (@"C:\Windows\Temp\w.tmp", 60),
        (@"C:\pagefile.sys", 5000),
        (@"C:\Unknown\blob.bin", 700));

    [Fact]
    public void DirRuleClaimsWholeSubtree()
    {
        var c = Classify(Sample, Rule("uv", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low));
        Assert.Equal("uv", c.RuleIdAt(@"C:\Users\demo\AppData\Local\uv\cache"));
        Assert.Equal("uv", c.RuleIdAt(@"C:\Users\demo\AppData\Local\uv\cache\archive\a.whl"));
        Assert.Null(c.RuleIdAt(@"C:\Users\demo\AppData\Local\uv\python\py.exe"));
        Assert.True(c.IsRoot(Sample.FindPath(@"C:\Users\demo\AppData\Local\uv\cache")));
        Assert.Equal(4000, c.RootOf(Sample.FindPath(@"C:\Users\demo\AppData\Local\uv\cache\archive"))!.Bytes);
    }

    [Fact]
    public void UserTokensApplyToEveryProfile()
    {
        var c = Classify(Sample, Rule("temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp"));
        Assert.Equal("temp", c.RuleIdAt(@"C:\Users\demo\AppData\Local\Temp\sub\y.tmp"));
        Assert.Equal("temp", c.RuleIdAt(@"C:\Users\other\AppData\Local\Temp\z.tmp"));
        Assert.Equal(2, c.Roots.Count);
    }

    [Fact]
    public void UnderIsFallbackAndMoreSpecificRulesWin()
    {
        var c = Classify(Sample,
            Rule("profile", MatchMode.Under, @"%USERPROFILE%\AppData", Risk.High, Kind.AppData),
            Rule("uv", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low));
        Assert.Equal("uv", c.RuleIdAt(@"C:\Users\demo\AppData\Local\uv\cache\archive\a.whl"));
        Assert.Equal("profile", c.RuleIdAt(@"C:\Users\demo\AppData\Local\uv\python\py.exe"));
        Assert.Null(c.RuleIdAt(@"C:\Users\demo\Documents\report.docx"));
    }

    [Fact]
    public void DeeperUnderReplacesShallowerUnder()
    {
        var c = Classify(Sample,
            Rule("windows", MatchMode.Under, @"%WINDIR%\System32", Risk.Never, Kind.System),
            Rule("all", MatchMode.Under, @"%SYSTEMDRIVE%\Windows", Risk.High, Kind.System));
        Assert.Equal("windows", c.RuleIdAt(@"C:\Windows\System32\kernel32.dll"));
        Assert.Equal("all", c.RuleIdAt(@"C:\Windows\Temp\w.tmp"));
    }

    [Fact]
    public void SiblingConditionDistinguishesChromiumCache()
    {
        var c = Classify(Sample, Rule("chromium", MatchMode.Dir, @"%USERPROFILE%\AppData\**\Cache", Risk.Safe, Kind.Cache, Tier.Reviewed,
            new Condition(ConditionType.SiblingAny, ["Local Storage", "GPUCache"])));
        Assert.Equal("chromium", c.RuleIdAt(@"C:\Users\demo\AppData\Roaming\Code\Cache\data_1"));
        Assert.Null(c.RuleIdAt(@"C:\Users\demo\AppData\Roaming\Other\Cache\data"));
    }

    [Fact]
    public void HasAnyAndNotUnderConditions()
    {
        var tree = Tree(
            (@"C:\Users\demo\proj\.venv\pyvenv.cfg", 1),
            (@"C:\Users\demo\proj\.venv\lib.py", 100),
            (@"C:\Users\demo\proj\fake\.venv\lib.py", 100),
            (@"C:\Users\demo\AppData\x\.venv\pyvenv.cfg", 1));
        var c = Classify(tree, Rule("venv", MatchMode.Dir, @"%USERPROFILE%\**\.venv", Risk.Low, Kind.BuildOutput, Tier.Reviewed,
            new Condition(ConditionType.HasAny, ["pyvenv.cfg"]),
            new Condition(ConditionType.NotUnder, [@"%USERPROFILE%\AppData"])));
        Assert.Equal("venv", c.RuleIdAt(@"C:\Users\demo\proj\.venv\lib.py"));
        Assert.Null(c.RuleIdAt(@"C:\Users\demo\proj\fake\.venv\lib.py"));
        Assert.Null(c.RuleIdAt(@"C:\Users\demo\AppData\x\.venv\pyvenv.cfg"));
    }

    [Fact]
    public void FileRuleMatchesFilesOnly()
    {
        var c = Classify(Sample, Rule("tmp", MatchMode.File, @"%USERPROFILE%\**\*.tmp"));
        Assert.Equal("tmp", c.RuleIdAt(@"C:\Users\demo\AppData\Local\Temp\sub\y.tmp"));
        Assert.Null(c.RuleIdAt(@"C:\Users\demo\AppData\Local\Temp\sub"));
    }

    [Fact]
    public void CleanableAndUnexplainedAreSummedUpTheTree()
    {
        var c = Classify(Sample,
            Rule("temp", MatchMode.Dir, @"%LOCALAPPDATA%\Temp"),
            Rule("uv", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low),
            Rule("pagefile", MatchMode.File, @"%SYSTEMDRIVE%\pagefile.sys", Risk.Guide, Kind.System));
        var demo = Sample.FindPath(@"C:\Users\demo");
        Assert.Equal(500 + 300 + 4000, c.Cleanable(demo));
        Assert.Equal(500 + 300 + 100 + 4000, c.TotalCleanable);
        Assert.Equal(900, c.CleanableBytes(Risk.Safe));
        Assert.Equal(4000, c.CleanableBytes(Risk.Low));
        Assert.Equal(700, c.Unexplained(Sample.FindPath(@"C:\Unknown")));
        Assert.Equal(0, c.Unexplained(Sample.FindPath(@"C:\pagefile.sys")));
        Assert.Equal(Risk.Guide, c.RiskAt(@"C:\pagefile.sys"));
    }

    [Fact]
    public void GuideOnlyRulesAreNotCountedAsCleanable()
    {
        var c = Classify(Sample, Rule("uv", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low) with { Actions = [new GuideAction("在程序里清理")] });
        Assert.Equal(Risk.Low, c.RiskAt(@"C:\Users\demo\AppData\Local\uv\cache\archive\a.whl"));
        Assert.Equal(0, c.TotalCleanable);
    }

    [Fact]
    public void FallbackRulesAreNotCountedAsCleanable()
    {
        var c = Classify(Sample, Rule("appdata", MatchMode.Under, @"%APPDATA%\Code", Risk.Medium, Kind.AppData));
        var file = Sample.FindPath(@"C:\Users\demo\AppData\Roaming\Code\Cache\data_1");
        Assert.Equal("appdata", c.RuleOf(file)?.Id);
        Assert.Equal(Risk.Medium, c.EffectiveRisk(file));
        Assert.False(c.IsCleanableNode(file));
        Assert.Equal(0, c.TotalCleanable);
        Assert.Equal(0, c.Unexplained(Sample.FindPath(@"C:\Users\demo\AppData\Roaming\Code")));
    }

    [Fact]
    public void HardLinkedFilesAreNotCountedAsCleanable()
    {
        var tree = Tree(
            (@"C:\Users\demo\AppData\Local\uv\cache\a.whl", 1000, NodeFlags.HardLinked),
            (@"C:\Users\demo\AppData\Local\uv\cache\b.whl", 300, NodeFlags.None));
        var c = Classify(tree, Rule("uv", MatchMode.Dir, @"%LOCALAPPDATA%\uv\cache", Risk.Low));
        var root = c.RootOf(tree.FindPath(@"C:\Users\demo\AppData\Local\uv\cache"))!;
        Assert.Equal(1300, root.Bytes);
        Assert.Equal(1000, root.SharedBytes);
        Assert.Equal(300, root.CleanableBytes);
    }

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

/// <summary>保护名单：任何规则都不能让它变成可删（研发方案 M2 验收要求的专门测试）。</summary>
public class ProtectionTests
{
    private static readonly NodeTree Sample = Tree(
        (@"C:\Users\demo\.ssh\id_ed25519", 1L, NodeFlags.None),
        (@"C:\Users\demo\.ssh\known_hosts", 1L, NodeFlags.None),
        (@"C:\Users\demo\.aws\credentials", 1L, NodeFlags.None),
        (@"C:\Users\demo\.claude\.credentials.json", 1L, NodeFlags.None),
        (@"C:\Users\demo\.codex\auth.json", 1L, NodeFlags.None),
        (@"C:\Users\demo\.codex\log\codex.log", 10L, NodeFlags.None),
        (@"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Local State", 1L, NodeFlags.None),
        (@"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Login Data", 1L, NodeFlags.None),
        (@"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Cache\f_000001", 100L, NodeFlags.None),
        (@"C:\Users\demo\AppData\Roaming\Microsoft\Protect\S-1-5-21\key", 1L, NodeFlags.None),
        (@"C:\Users\demo\Documents\thesis.docx", 500L, NodeFlags.None),
        (@"C:\Users\demo\NTUSER.DAT", 10L, NodeFlags.None),
        (@"C:\Windows\System32\ntdll.dll", 800L, NodeFlags.None),
        (@"C:\Windows\Temp\w.tmp", 60L, NodeFlags.None),
        (@"C:\Windows\WinSxS\amd64_x\a.dll", 300L, NodeFlags.None),
        (@"C:\Program Files\App\app.exe", 400L, NodeFlags.None),
        (@"C:\pagefile.sys", 5000L, NodeFlags.None),
        (@"C:\$MFT", 1000L, NodeFlags.Metadata));

    private static readonly string[] HardProtected =
    [
        @"C:\Users\demo\.ssh\id_ed25519", @"C:\Users\demo\.ssh\known_hosts", @"C:\Users\demo\.aws\credentials",
        @"C:\Users\demo\.claude\.credentials.json", @"C:\Users\demo\.codex\auth.json",
        @"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Local State",
        @"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Login Data",
        @"C:\Users\demo\AppData\Roaming\Microsoft\Protect\S-1-5-21\key",
        @"C:\Users\demo\NTUSER.DAT", @"C:\Windows\System32\ntdll.dll", @"C:\Program Files\App\app.exe", @"C:\$MFT",
    ];

    /// <summary>最坏情况：一条"整个盘都安全可删"的规则（校验会拒绝它，但引擎本身也必须守住）。</summary>
    private static Classification ClassifyWithEverythingSafe(Tier tier) => Classify(Sample,
        Rule("evil-dir", MatchMode.Dir, @"%SYSTEMDRIVE%\**\*", Risk.Safe, Kind.Temp, tier),
        Rule("evil-file", MatchMode.File, @"%SYSTEMDRIVE%\**\*", Risk.Safe, Kind.Temp, tier));

    [Theory]
    [InlineData(Tier.Draft)]
    [InlineData(Tier.Verified)]
    public void NoRuleCanMakeHardProtectedPathsCleanable(Tier tier)
    {
        var c = ClassifyWithEverythingSafe(tier);
        foreach (var path in HardProtected)
        {
            var node = Sample.FindPath(path);
            Assert.True(node >= 0, path);
            Assert.Equal(Risk.Never, c.EffectiveRisk(node));
            Assert.Equal(0, c.Cleanable(node));
        }
    }

    [Fact]
    public void GuideLocationsStayGuide()
    {
        var c = ClassifyWithEverythingSafe(Tier.Verified);
        Assert.Equal(Risk.Guide, c.RiskAt(@"C:\pagefile.sys"));
        Assert.Equal(Risk.Guide, c.RiskAt(@"C:\Windows\WinSxS\amd64_x\a.dll"));
    }

    [Fact]
    public void SoftProtectionNeedsVerifiedRule()
    {
        Assert.Equal(Risk.High, ClassifyWithEverythingSafe(Tier.Reviewed).RiskAt(@"C:\Users\demo\Documents\thesis.docx"));
        Assert.Equal(Risk.Safe, ClassifyWithEverythingSafe(Tier.Verified).RiskAt(@"C:\Users\demo\Documents\thesis.docx"));
    }

    [Fact]
    public void ExceptionsInsideProtectedDirectoriesAreReleased()
    {
        var c = ClassifyWithEverythingSafe(Tier.Reviewed);
        Assert.Equal(Risk.Safe, c.RiskAt(@"C:\Windows\Temp\w.tmp"));
        Assert.Equal(ProtectionLevel.None, c.ProtectionLevelOf(Sample.FindPath(@"C:\Windows\Temp\w.tmp")));
    }

    [Fact]
    public void ProtectionInsideClaimedSubtreeStillApplies()
    {
        // 浏览器资料目录被一条规则整体认领，里面的登录数据仍然受保护，其余部分按规则的风险
        var c = Classify(Sample, Rule("profile", MatchMode.Dir, @"%LOCALAPPDATA%\Google\Chrome\User Data", Risk.Medium, Kind.BrowserData));
        Assert.Equal(Risk.Never, c.RiskAt(@"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Login Data"));
        Assert.Equal(Risk.Medium, c.RiskAt(@"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Cache\f_000001"));
    }

    [Fact]
    public void CodexLogIsNotProtectedButAuthIs()
    {
        var c = Classify(Sample, Rule("codex-log", MatchMode.Dir, @"%USERPROFILE%\.codex\log"));
        Assert.Equal(Risk.Safe, c.RiskAt(@"C:\Users\demo\.codex\log\codex.log"));
        Assert.Equal(Risk.Never, c.RiskAt(@"C:\Users\demo\.codex\auth.json"));
    }

    [Fact]
    public void ProtectionCheckAgreesWithEngine()
    {
        var check = new ProtectionCheck(PathContext.Default);
        var c = ClassifyWithEverythingSafe(Tier.Reviewed);
        foreach (var path in HardProtected.Where(p => !p.EndsWith("$MFT", StringComparison.Ordinal)))
        {
            Assert.Equal(c.ProtectionLevelOf(Sample.FindPath(path)), check.LevelFor(path));
        }

        Assert.Equal(ProtectionLevel.None, check.LevelFor(@"C:\Windows\Temp\w.tmp"));
        Assert.Equal(ProtectionLevel.Soft, check.LevelFor(@"C:\Users\demo\Documents\thesis.docx"));
    }
}

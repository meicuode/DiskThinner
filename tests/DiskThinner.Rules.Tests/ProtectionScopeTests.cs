namespace DiskThinner.Rules.Tests;

/// <summary>清理时逐项检查保护名单用的"某个目录下面可能命中的保护项"。</summary>
public sealed class ProtectionScopeTests
{
    [Theory]
    [InlineData(@"C:\Users\*\.ssh", @"C:\Users\demo", true)]
    [InlineData(@"C:\Users\*\.ssh", @"C:\Users\demo\.ssh", false)] // 正好是它本身，不是"下面"
    [InlineData(@"C:\Users\*\.ssh", @"C:\Users\demo\AppData", false)]
    [InlineData(@"C:\Users\*\AppData\Local\**\User Data\Local State", @"C:\Users\demo\AppData\Local\Google\Chrome", true)]
    [InlineData(@"C:\Users\*\**\wallet.dat", @"C:\Users\demo\AppData\Local\Temp", true)]
    [InlineData(@"C:\Windows\Temp", @"C:\Users\demo\AppData\Local\Temp", false)]
    public void CouldMatchBelow(string pattern, string dir, bool expected) =>
        Assert.Equal(expected, CompiledPattern.Compile(pattern).CouldMatchBelow(dir));

    [Fact]
    public void ScopeOnlyKeepsRelevantEntriesAndAgreesWithTheFullCheck()
    {
        var check = new ProtectionCheck(PathContext.Default);
        var temp = @"C:\Users\demo\AppData\Local\Temp";
        var scope = check.ScopeBelow(temp);
        Assert.InRange(scope.Count, 1, 10); // 只剩 ** 开头的几项（钱包、浏览器登录数据……）

        var wallet = temp + @"\x\wallet.dat";
        Assert.Equal(check.EntryFor(wallet), scope.EntryAt(wallet));
        Assert.Null(scope.EntryAt(temp + @"\x\other.dat"));

        var chrome = @"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Cache";
        var login = @"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Default\Login Data";
        Assert.Null(check.ScopeBelow(chrome).EntryAt(chrome + @"\data_1"));
        Assert.Equal(ProtectionLevel.Hard, check.ScopeBelow(@"C:\Users\demo\AppData\Local\Google\Chrome").EntryAt(login)?.Level);
    }
}

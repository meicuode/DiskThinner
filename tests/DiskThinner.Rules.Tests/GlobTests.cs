using DiskThinner.Rules;

namespace DiskThinner.Rules.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("Cache", "cache", true)]
    [InlineData("Cache", "Caches", false)]
    [InlineData("app-*", "app-1.2.3", true)]
    [InlineData("app-*", "app", false)]
    [InlineData("*.log", "a.LOG", true)]
    [InlineData("*.{dmp,mdmp}", "x.mdmp", true)]
    [InlineData("*.{dmp,mdmp}", "x.dmpx", false)]
    [InlineData("{log,logs}", "Logs", true)]
    [InlineData("ext?.vhdx", "ext4.vhdx", true)]
    [InlineData(".{vscode,kiro}", ".kiro", true)]
    [InlineData("*-updater", "antigravity-updater", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void SegmentMatches(string pattern, string name, bool expected) =>
        Assert.Equal(expected, Segment.Parse(pattern).Matches(name));

    [Theory]
    [InlineData(@"C:\Users\*\AppData\**\Crashpad", @"C:\Users\demo\AppData\Crashpad", true)]
    [InlineData(@"C:\Users\*\AppData\**\Crashpad", @"C:\Users\demo\AppData\Local\Google\Chrome\User Data\Crashpad", true)]
    [InlineData(@"C:\Users\*\AppData\**\Crashpad", @"C:\Users\demo\Crashpad", false)]
    [InlineData(@"C:\Windows\Microsoft.NET\*\*\Temporary ASP.NET Files", @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\Temporary ASP.NET Files", true)]
    public void PatternMatches(string pattern, string path, bool expected) =>
        Assert.Equal(expected, CompiledPattern.Compile(pattern).Matches(path));

    [Fact]
    public void TokensExpandForAllUsers()
    {
        Assert.Equal(@"C:\Users\*\AppData\Local\uv\cache", PathTokens.Expand(@"%LOCALAPPDATA%\uv\cache", PathContext.Default));
        Assert.Equal(@"C:\Program Files (x86)\Foo", PathTokens.Expand(@"%PROGRAMFILES(X86)%\Foo", PathContext.Default));
        Assert.Throws<FormatException>(() => PathTokens.Expand(@"C:\Temp", PathContext.Default));
        Assert.Null(PathTokens.LeadingToken(@"%USERPROFILEX%\a"));
    }
}

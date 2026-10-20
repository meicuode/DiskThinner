using DiskThinner.App.Explorer;
using DiskThinner.App.Formatting;
using DiskThinner.Core;

namespace DiskThinner.App.Tests;

public class FormattingTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 15, 30, 0, DateTimeKind.Local);

    private static long Ago(TimeSpan span) => (Now - span).ToUniversalTime().ToFileTimeUtc();

    [Theory]
    [InlineData(10, "刚刚")]
    [InlineData(5 * 60, "5 分钟前")]
    [InlineData(2 * 3600, "2 小时前")]
    public void RelativeTimeWithinToday(int seconds, string expected) =>
        Assert.Equal(expected, TimeText.Relative(Ago(TimeSpan.FromSeconds(seconds)), Now));

    [Fact]
    public void RelativeTimeEarlierToday() =>
        Assert.Equal("今天 08:30", TimeText.Relative(Ago(TimeSpan.FromHours(7)), Now));

    [Fact]
    public void RelativeTimeDays()
    {
        Assert.Equal("昨天", TimeText.Relative(Ago(TimeSpan.FromHours(20)), Now));
        Assert.Equal("47 天前", TimeText.Relative(Ago(TimeSpan.FromDays(47)), Now));
        Assert.Equal("2026-01-02", TimeText.Relative(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Local).ToUniversalTime().ToFileTimeUtc(), Now));
        Assert.Equal("", TimeText.Relative(0, Now));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(4096, "4.00 KB")]
    [InlineData(4_692_251_688, "4.37 GB")]
    [InlineData(18_146_236_416, "16.9 GB")]
    [InlineData(200_574_956_854, "186.8 GB")]
    public void ByteSizeMatchesPrototype(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));

    [Fact]
    public void ByteSizeDelta()
    {
        Assert.Equal("+1.20 GB", ByteSize.FormatDelta(1_288_490_189));
        Assert.Equal("\u22121.90 GB", ByteSize.FormatDelta(-2_040_109_466));
        Assert.Equal("0", ByteSize.FormatDelta(0));
    }

    [Theory]
    [InlineData("C", @"C:\")]
    [InlineData("c:", @"C:\")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData(@"C:\Users", null)]
    [InlineData("", null)]
    [InlineData("12", null)]
    public void DriveArgument(string input, string? expected) => Assert.Equal(expected, AppOptions.NormalizeDrive(input));

    [Fact]
    public void FileKindsByExtension()
    {
        var tree = TestTrees.Build(("a.DLL", 1), ("b.log", 1), ("c.nupkg", 1), ("d.mp4", 1), ("e.sqlite", 1), ("disk.vhdx", 1), ("noext", 1), ("pagefile.sys", 1));
        FileKind Kind(string name) => FileKinds.Classify(tree, tree.FindPath(@"C:\" + name));
        Assert.Equal(FileKind.Program, Kind("a.DLL"));
        Assert.Equal(FileKind.TempLog, Kind("b.log"));
        Assert.Equal(FileKind.Archive, Kind("c.nupkg"));
        Assert.Equal(FileKind.Media, Kind("d.mp4"));
        Assert.Equal(FileKind.Data, Kind("e.sqlite"));
        Assert.Equal(FileKind.System, Kind("disk.vhdx"));
        Assert.Equal(FileKind.Other, Kind("noext"));
        Assert.Equal(FileKind.System, Kind("pagefile.sys"));
    }
}

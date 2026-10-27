namespace DiskThinner.Monitor.Tests;

public class DirectoryTableTests
{
    [Fact]
    public void PathsRoundTripCaseInsensitively()
    {
        var t = new DirectoryTable();
        var a = t.GetOrAdd(@"C:\Users\demo\AppData");
        Assert.Equal(a, t.GetOrAdd(@"c:\users\DEMO\appdata\"));
        Assert.Equal(@"C:\Users\demo\AppData", t.PathOf(a));
        Assert.Equal(@"C:\", t.PathOf(t.GetOrAdd(@"C:\")));
        Assert.True(t.IsUnder(a, t.GetOrAdd(@"C:\Users")));
        Assert.Equal(-1, t.Find(@"C:\Users\other"));
        Assert.Equal(a, t.Find(@"C:\Users\demo\AppData"));
    }

    [Fact]
    public void DeeperThan32LevelsCountsAtLevel32()
    {
        var t = new DirectoryTable();
        var deep = @"C:" + string.Concat(Enumerable.Range(0, 40).Select(i => $@"\d{i}"));
        var id = t.GetOrAdd(deep);
        Assert.Equal(DirectoryTable.MaxDepth, t.Depth(id));
        Assert.Equal(@"C:" + string.Concat(Enumerable.Range(0, 32).Select(i => $@"\d{i}")), t.PathOf(id));
    }

    [Fact]
    public void FullTableUsesTheNearestExistingAncestor()
    {
        var t = new DirectoryTable(maxNodes: 4);
        var users = t.GetOrAdd(@"C:\Users\demo");
        var more = t.GetOrAdd(@"C:\Users\demo\a\b\c");
        Assert.True(t.Full);
        Assert.True(t.IsUnder(more, users) || more == users);
    }

    [Fact]
    public void RenamedDirectoryKeepsItsId()
    {
        var t = new DirectoryTable();
        var stage = t.GetOrAdd(@"C:\x\move\stage");
        var child = t.GetOrAdd(@"C:\x\move\stage\sub");
        var moved = t.Move(stage, @"C:\x\move\done");
        Assert.Equal(stage, moved);
        Assert.Equal(@"C:\x\move\done", t.PathOf(stage));
        Assert.Equal(@"C:\x\move\done\sub", t.PathOf(child));
        Assert.Equal(-1, t.Find(@"C:\x\move\stage"));
    }

    [Fact]
    public void RenamingOntoAnExistingDirectoryMergesIt()
    {
        var t = new DirectoryTable();
        var a = t.GetOrAdd(@"C:\x\a");
        var aChild = t.GetOrAdd(@"C:\x\a\only-in-a");
        var b = t.GetOrAdd(@"C:\x\b");
        var merged = t.Move(a, @"C:\x\b");
        Assert.Equal(b, merged);
        Assert.Equal(b, t.Resolve(a));
        Assert.Equal(@"C:\x\b\only-in-a", t.PathOf(aChild));
    }

    [Fact]
    public void DeviceMapConvertsKnownPrefixesOnly()
    {
        var map = new PrefixDeviceMap(new Dictionary<string, string> { [@"\Device\HarddiskVolume3"] = "C:", [@"\Device\HarddiskVolume30"] = "D:" });
        Assert.Equal(@"C:\Users\x.txt", map.ToDosPath(@"\Device\HarddiskVolume3\Users\x.txt"));
        Assert.Equal(@"D:\a", map.ToDosPath(@"\device\harddiskvolume30\a"));
        Assert.Null(map.ToDosPath(@"\Device\Mup\server\share\a"));
        Assert.Null(map.ToDosPath(@"\Device\HarddiskVolume31\a"));
        Assert.Equal(@"C:\Windows\x.exe", map.ToDosPath(@"C:\Windows\x.exe"));
    }
}

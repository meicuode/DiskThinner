namespace DiskThinner.Scanner.Tests;

public class NamePoolTests
{
    [Fact]
    public void AppendAndGetRoundTrip()
    {
        var pool = new NamePool();
        var a = pool.Append("hello");
        var b = pool.Append("世界");
        Assert.Equal("hello", pool.Get(a, 5).ToString());
        Assert.Equal("世界", pool.Get(b, 2).ToString());
        Assert.Equal(7, pool.Used);
    }

    [Fact]
    public void NameNeverSpansChunks()
    {
        var pool = new NamePool();
        var filler = new string('x', NamePool.ChunkSize - 3);
        pool.Append(filler);
        var offset = pool.Append("abcdef"); // 剩 3 个字符放不下，换到下一块
        Assert.Equal(1, offset >> NamePool.ChunkShift);
        Assert.Equal(0, offset & (NamePool.ChunkSize - 1));
        Assert.Equal("abcdef", pool.Get(offset, 6).ToString());
        Assert.Equal(2L * NamePool.ChunkSize, pool.Capacity);
    }

    [Fact]
    public void CompactMovesLiveNamesForwardAndDropsEmptyChunks()
    {
        var pool = new NamePool();
        var names = new List<(int Offset, string Text)>();
        for (var i = 0; i < 300_000; i++)
        {
            var text = "name-" + i.ToString("D6") + (i % 3 == 0 ? "-long-suffix" : "");
            names.Add((pool.Append(text), text));
        }

        Assert.True(pool.Capacity >= 4L * NamePool.ChunkSize);

        // 只保留每 4 个里的 1 个（模拟丢掉 8.3 短名和已删除记录）
        var keep = names.Where((_, i) => i % 4 == 0).ToList();
        var offsets = keep.Select(k => k.Offset).ToArray();
        var lengths = keep.Select(k => k.Text.Length).ToArray();
        pool.Compact(offsets, lengths);

        for (var i = 0; i < keep.Count; i++)
        {
            Assert.Equal(keep[i].Text, pool.Get(offsets[i], lengths[i]).ToString());
        }

        Assert.Equal(lengths.Sum(), pool.Used);
        Assert.True(pool.Capacity <= 2L * NamePool.ChunkSize);
    }

    [Fact]
    public void CompactKeepsSharedOffsets()
    {
        var pool = new NamePool();
        pool.Append("dead");
        var shared = pool.Append("shared");
        int[] offsets = [shared, shared];
        pool.Compact(offsets, [6, 6]);
        Assert.Equal(offsets[0], offsets[1]);
        Assert.Equal("shared", pool.Get(offsets[0], 6).ToString());
    }
}

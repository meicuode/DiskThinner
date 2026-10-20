namespace DiskThinner.Scanner;

/// <summary>
/// 分块的名称池：每块 1M 个字符（2 MB），一个名称不会跨块。
/// 扩容只追加新块，不复制已有内容；整理时原地前移、丢掉末尾空块。
/// 这样百万级文件名的池子在增长和整理时都不会出现"新旧两份同时存在"的内存峰值。
/// 偏移量 = 块号 &lt;&lt; 20 | 块内位置。
/// </summary>
public sealed class NamePool
{
    public const int ChunkShift = 20;
    public const int ChunkSize = 1 << ChunkShift;
    private const int Mask = ChunkSize - 1;

    private readonly List<char[]> _chunks = [];
    private int _end;      // 下一个可写位置（编码后的偏移量）
    private long _used;    // 实际写入的字符数（不含块尾空隙）

    /// <summary>已写入的字符数。</summary>
    public long Used => _used;

    /// <summary>已分配的字符数。</summary>
    public long Capacity => (long)_chunks.Count * ChunkSize;

    public ReadOnlySpan<char> Get(int offset, int length) =>
        _chunks[offset >> ChunkShift].AsSpan(offset & Mask, length);

    public int Append(ReadOnlySpan<char> name)
    {
        var span = Reserve(name.Length, out var offset);
        name.CopyTo(span);
        return offset;
    }

    /// <summary>预留一段空间，调用方直接写入（MFT 解析时把 UTF-16 名称直接解码到池里）。</summary>
    public Span<char> Reserve(int length, out int offset)
    {
        if (length > ChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "名称过长");
        }

        var chunk = _end >> ChunkShift;
        var inChunk = _end & Mask;
        if (inChunk + length > ChunkSize)
        {
            chunk++;
            inChunk = 0;
        }

        while (_chunks.Count <= chunk)
        {
            _chunks.Add(GC.AllocateUninitializedArray<char>(ChunkSize));
        }

        offset = (chunk << ChunkShift) | inChunk;
        _end = offset + length;
        _used += length;
        return _chunks[chunk].AsSpan(inChunk, length);
    }

    /// <summary>
    /// 原地整理：按 <paramref name="offsets"/> 的先后顺序把名称依次前移紧排，返回每个名称的新偏移量。
    /// <paramref name="offsets"/> 必须按偏移量升序。由于是对原排列的子序列做同样的"放不下就换块"紧排，
    /// 每个名称的新位置都不会超过原位置，所以前移不会覆盖还没搬的内容。
    /// </summary>
    public void Compact(Span<int> offsets, ReadOnlySpan<int> lengths)
    {
        var target = 0;
        long used = 0;
        var lastSource = -1;
        var lastTarget = 0;
        for (var i = 0; i < offsets.Length; i++)
        {
            var source = offsets[i];
            var length = lengths[i];
            if (source == lastSource)
            {
                offsets[i] = lastTarget; // 共用同一段名称
                continue;
            }

            var chunk = target >> ChunkShift;
            var inChunk = target & Mask;
            if (inChunk + length > ChunkSize)
            {
                chunk++;
                inChunk = 0;
            }

            var newOffset = (chunk << ChunkShift) | inChunk;
            if (newOffset != source)
            {
                Get(source, length).CopyTo(_chunks[chunk].AsSpan(inChunk, length));
            }

            offsets[i] = newOffset;
            lastSource = source;
            lastTarget = newOffset;
            target = newOffset + length;
            used += length;
        }

        _end = target;
        _used = used;
        var keep = (target + Mask) >> ChunkShift;
        if (keep < _chunks.Count)
        {
            _chunks.RemoveRange(Math.Max(1, keep), _chunks.Count - Math.Max(1, keep));
        }
    }
}

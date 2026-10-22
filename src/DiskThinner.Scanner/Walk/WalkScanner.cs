using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using DiskThinner.Core;
using DiskThinner.Scanner.Native;

namespace DiskThinner.Scanner.Walk;

/// <summary>
/// 逐个目录列出内容的扫描方式，普通权限可用。多线程并行，不进入 junction 和符号链接。
/// 局限：无法识别硬链接（每个名字都按完整大小计算）；分配大小按簇大小向上取整估算。
/// </summary>
public static class WalkScanner
{
    private const uint FileAttributeRecallOnOpen = 0x40000;
    private const uint FileAttributeRecallOnDataAccess = 0x400000;

    private readonly struct Entry(int nameOffset, int nameLength, FileAttributes attributes, long length, long lastWrite)
    {
        public readonly int NameOffset = nameOffset;
        public readonly int NameLength = nameLength;
        public readonly FileAttributes Attributes = attributes;
        public readonly long Length = length;
        public readonly long LastWrite = lastWrite;
    }

    /// <summary>每个工作线程复用的缓冲区：一个目录的所有条目先收集在这里，再整体写入树。</summary>
    private sealed class WorkerBuffer
    {
        public readonly List<Entry> Entries = new(256);
        public char[] Names = new char[8192];
        public int NamesLength;

        public void Reset()
        {
            Entries.Clear();
            NamesLength = 0;
        }

        public void Add(ref FileSystemEntry entry)
        {
            var name = entry.FileName;
            if (NamesLength + name.Length > Names.Length)
            {
                Array.Resize(ref Names, Math.Max(Names.Length * 2, NamesLength + name.Length));
            }

            name.CopyTo(Names.AsSpan(NamesLength));
            Entries.Add(new Entry(NamesLength, name.Length, entry.Attributes, entry.Length, entry.LastWriteTimeUtc.UtcDateTime.ToFileTimeUtc()));
            NamesLength += name.Length;
        }
    }

    public static ScanResult Scan(string rootPath, int? workerCount = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(rootPath);
        if (!root.EndsWith('\\'))
        {
            root += '\\';
        }

        var stopwatch = Stopwatch.StartNew();
        var clusterSize = GetClusterSize(root);
        var tree = new NodeTree(1 << 20);
        var rootInfo = new DirectoryInfo(root);
        tree.Root = tree.Add(-1, Path.GetPathRoot(root) == root ? root : root.TrimEnd('\\'), NodeFlags.Directory, 0, 0,
            rootInfo.Exists ? rootInfo.LastWriteTimeUtc.ToFileTimeUtc() : 0);

        var queue = new BlockingCollection<(int Node, string Path)>(new ConcurrentQueue<(int, string)>());
        var pending = 1;
        var directoriesDone = 0L;
        var bytesSeen = 0L;
        int denied = 0, reparse = 0, errors = 0;
        var treeLock = new object();

        queue.Add((tree.Root, root), cancellationToken);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            BufferSize = 64 * 1024,
        };

        void Worker()
        {
            try
            {
                WorkerLoop();
            }
            catch (OperationCanceledException)
            {
                // 取消时各线程直接退出，由调用方统一抛出
            }
        }

        void WorkerLoop()
        {
            var buffer = new WorkerBuffer();
            var subdirs = new List<(int, string)>();
            foreach (var (node, path) in queue.GetConsumingEnumerable(cancellationToken))
            {
                buffer.Reset();
                subdirs.Clear();
                var failure = NodeFlags.None;
                try
                {
                    var enumerable = new FileSystemEnumerable<bool>(path, (ref FileSystemEntry e) =>
                    {
                        buffer.Add(ref e);
                        return true;
                    }, options);
                    foreach (var _ in enumerable)
                    {
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    failure = NodeFlags.AccessDenied;
                    Interlocked.Increment(ref denied);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 扫描期间被删除的目录、设备错误等：记为无法列出，继续扫描其他目录
                    failure = NodeFlags.AccessDenied;
                    Interlocked.Increment(ref errors);
                }

                long dirBytes = 0;
                lock (treeLock)
                {
                    tree[node].Flags |= failure;
                    foreach (var e in buffer.Entries)
                    {
                        var name = buffer.Names.AsSpan(e.NameOffset, e.NameLength);
                        var isDir = (e.Attributes & FileAttributes.Directory) != 0;
                        var flags = MapFlags(e.Attributes);
                        var alloc = isDir || (flags & NodeFlags.CloudPlaceholder) != 0 ? 0 : RoundUp(e.Length, clusterSize);
                        dirBytes += alloc;
                        var child = tree.Add(node, name, flags, isDir ? 0 : e.Length, alloc, e.LastWrite);

                        if (isDir)
                        {
                            if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                reparse++;
                            }
                            else
                            {
                                subdirs.Add((child, string.Concat(path, name, "\\")));
                            }
                        }
                    }
                }

                Interlocked.Add(ref pending, subdirs.Count);
                foreach (var item in subdirs)
                {
                    queue.Add(item, cancellationToken);
                }

                var bytes = Interlocked.Add(ref bytesSeen, dirBytes);
                var done = Interlocked.Increment(ref directoriesDone);
                if ((done & 0x3FF) == 0)
                {
                    progress?.Report(new ScanProgress(done, 0, L.T("列出目录"), bytes));
                }

                if (Interlocked.Decrement(ref pending) == 0)
                {
                    queue.CompleteAdding();
                }
            }
        }

        // 阶段 0 实测：瓶颈在文件系统（含杀毒等过滤驱动），16 线程比 8 线程约快 1.3 倍，再多收益不明显
        var threads = Enumerable.Range(0, workerCount ?? Math.Clamp(Environment.ProcessorCount, 2, 16))
            .Select(_ => new Thread(Worker) { IsBackground = true, Name = "WalkScanner" })
            .ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        cancellationToken.ThrowIfCancellationRequested();

        var readDuration = stopwatch.Elapsed;
        stopwatch.Restart();
        tree.Finalize(countHardLinksOnce: true);
        tree.TrimExcess();
        var finalizeDuration = stopwatch.Elapsed;

        Win32.GetDiskFreeSpaceEx(Path.GetPathRoot(root)!, out _, out var totalBytes, out var freeBytes);

        var stats = new ScanStats
        {
            Mode = ScanMode.Walk,
            Root = root,
            ReadDuration = readDuration,
            FinalizeDuration = finalizeDuration,
            VolumeTotalBytes = totalBytes,
            VolumeFreeBytes = freeBytes,
            ClusterSize = clusterSize,
            AccessDeniedDirectories = denied,
            ReparsePointDirectories = reparse,
            ErrorDirectories = errors,
            Nodes = tree.Count,
            Unreachable = tree.UnreachableCount,
        };
        return new ScanResult(tree, stats);
    }

    private static NodeFlags MapFlags(FileAttributes a)
    {
        var flags = NodeFlags.None;
        if ((a & FileAttributes.Directory) != 0) flags |= NodeFlags.Directory;
        if ((a & FileAttributes.ReparsePoint) != 0) flags |= NodeFlags.ReparsePoint;
        if ((a & FileAttributes.Hidden) != 0) flags |= NodeFlags.Hidden;
        if ((a & FileAttributes.System) != 0) flags |= NodeFlags.System;
        if ((a & FileAttributes.Compressed) != 0) flags |= NodeFlags.Compressed;
        if ((a & FileAttributes.SparseFile) != 0) flags |= NodeFlags.Sparse;
        if (((uint)a & (FileAttributeRecallOnOpen | FileAttributeRecallOnDataAccess)) != 0 || (a & FileAttributes.Offline) != 0)
        {
            flags |= NodeFlags.CloudPlaceholder;
        }

        return flags;
    }

    private static long RoundUp(long length, int clusterSize) =>
        length <= 0 ? 0 : (length + clusterSize - 1) / clusterSize * clusterSize;

    private static int GetClusterSize(string path)
    {
        var root = Path.GetPathRoot(path) ?? path;
        return Win32.GetDiskFreeSpace(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _)
            ? (int)(sectorsPerCluster * bytesPerSector)
            : 4096;
    }
}

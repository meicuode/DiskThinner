using DiskThinner.Inventory;
using DiskThinner.Monitor.Etw;
using DiskThinner.Storage;

namespace DiskThinner.App.Monitor;

/// <summary>
/// 写入监控的锚点（docs/phase2a-design.md 5.7 节第 1 条、第 12.2 节第 12 条）：每个盘最近一次快照里记录的目录。
/// 快照库不存在时只用盘根，不建库（直接 new SnapshotStore 会建库）。
/// </summary>
internal sealed class SnapshotAnchors(string snapshotDb) : IAnchorSource
{
    public IReadOnlyCollection<string> Load()
    {
        if (!File.Exists(snapshotDb))
        {
            return [];
        }

        try
        {
            var store = new SnapshotStore(snapshotDb);
            var paths = new List<string>();
            foreach (var latest in store.List().GroupBy(s => s.Volume, StringComparer.OrdinalIgnoreCase).Select(g => g.MaxBy(s => s.TakenAtUtc)!))
            {
                var tree = store.Load(latest.Id);
                for (var i = 0; i < tree.Count; i++)
                {
                    if (tree.IsDirectory(i))
                    {
                        paths.Add(tree.GetPath(i));
                    }
                }
            }

            return paths;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

internal static class MonitorIdentity
{
    /// <summary>映像的产品名和公司名（版本信息，落盘线程上读，有缓存）。</summary>
    public static (string? Product, string? Company) Read(string path)
    {
        if (path.Length < 3 || path[1] != ':')
        {
            return (null, null);
        }

        var id = ExeIdentityReader.Read(path);
        return (id?.Product, id?.Company);
    }
}

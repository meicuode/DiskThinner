namespace DiskThinner.App.Commands;

/// <summary>
/// 子命令的 --report：key=value 的 UTF-8 文件，每次整体重写（先写同目录的临时文件再替换）。值里的换行换成空格。
/// 写不了只记一次日志，不影响子命令本身。
/// </summary>
internal sealed class KeyValueReport(string? path, Action<string> log)
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private bool _warned;

    public void Set(string key, string value)
    {
        if (!_values.ContainsKey(key))
        {
            _order.Add(key);
        }

        _values[key] = value.ReplaceLineEndings(" ");
    }

    public void Save()
    {
        if (path is null)
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".tmp";
            File.WriteAllLines(tmp, _order.Select(k => $"{k}={_values[k]}"));
            File.Move(tmp, full, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_warned)
            {
                _warned = true;
                log("report write failed: " + ex.GetType().Name);
            }
        }
    }
}

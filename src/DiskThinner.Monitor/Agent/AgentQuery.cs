namespace DiskThinner.Monitor.Agent;

/// <summary>
/// 普通用户调用者的过滤（docs/phase2b-design.md 5.5 节，E4）：目录在其他用户个人目录下的行删掉（合成一份没有路径的计数），
/// 映像在其他用户个人目录下的进程链换成"其他用户的程序"。已提权的管理员不过滤（<see cref="For"/> 返回 null）。
/// "其他用户的个人目录"是 ProfileList 里不是调用者的那些 ProfileImagePath。
/// </summary>
public sealed class CallerFilter
{
    private readonly List<string> _others;

    public CallerFilter(IEnumerable<string> otherProfiles) =>
        _others = [.. otherProfiles.Where(p => p.Length >= 3).Select(p => p.TrimEnd('\\'))];

    public IReadOnlyList<string> OtherProfiles => _others;

    /// <summary>已提权的管理员：null（不过滤）；其他人：按调用者的 SID 去掉自己的个人目录。</summary>
    public static CallerFilter? For(bool elevatedAdmin, string callerSid, IReadOnlyDictionary<string, string> profiles) =>
        elevatedAdmin ? null : new CallerFilter(profiles.Where(kv => !string.Equals(kv.Key, callerSid, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value));

    public bool HidesDir(string? dir) => dir is not null && _others.Any(p => WriteIndex.IsUnder(dir, p));

    public bool IsOtherUserImage(string? path) => path is not null && path.Length > 0 && _others.Any(p => WriteIndex.IsUnder(path, p));

    /// <summary>本机的个人目录：SID → ProfileImagePath（HKLM\...\ProfileList，环境变量已展开）。</summary>
    public static Dictionary<string, string> ReadProfiles()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var list = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (list is null)
        {
            return map;
        }

        foreach (var sid in list.GetSubKeyNames())
        {
            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is string path && path.Length > 0)
            {
                map[sid] = Environment.ExpandEnvironmentVariables(path);
            }
        }

        return map;
    }
}

/// <summary>查询的结果：会话、按小时的覆盖、按（目录, 进程链）合计的行，以及过滤掉的计数。</summary>
public sealed record AgentQueryResult(IReadOnlyList<SessionDto> Sessions, IReadOnlyList<HourDto> Hours, IReadOnlyList<RowDto> Rows, bool Folded, Counters Hidden, int HiddenRows);

/// <summary>
/// 把 <see cref="MonitorStore.LoadMerged"/> 读出的数据按调用者过滤，行数超过上限时把权重最小的行逐级并到上级目录（再不够就并成"其他进程"，最后按权重截断），
/// 结果标出 Folded（docs/phase2b-design.md 5.5 节）。
/// </summary>
public static class AgentQuery
{
    public static AgentQueryResult Build(WriteIndex index, CallerFilter? filter, int maxRows = AgentProtocol.MaxRows)
    {
        var hidden = new Counters();
        var hiddenRows = 0;
        var rows = new List<Row>(index.Rows.Count);
        foreach (var r in index.Rows)
        {
            if (filter?.HidesDir(r.Dir) == true)
            {
                hidden.Add(r.Counters);
                hiddenRows++;
                continue;
            }

            rows.Add(new Row(r.SessionId, r.HourUtc, r.Dir, filter is null ? r.Chain : Sanitize(r.Chain, filter), r.Counters));
        }

        rows = Aggregate(rows);
        var folded = false;
        for (var pass = 0; rows.Count > maxRows && pass < 64; pass++)
        {
            folded = true;
            var excess = rows.Count - maxRows;
            var light = rows.OrderBy(r => r.Counters.Weight).Take(excess).ToHashSet();
            var changed = false;
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!light.Contains(r))
                {
                    continue;
                }

                if (r.Dir is not null && DirectoryTable.Split(r.Dir).Parent is { } parent)
                {
                    rows[i] = r with { Dir = parent };
                    changed = true;
                }
                else if (r.Chain.Kind != ChainKind.Other)
                {
                    rows[i] = r with { Chain = StoredChain.Other };
                    changed = true;
                }
            }

            rows = Aggregate(rows);
            if (!changed)
            {
                break;
            }
        }

        if (rows.Count > maxRows)
        {
            folded = true;
            rows = [.. rows.OrderByDescending(r => r.Counters.Weight).Take(maxRows)];
        }

        var dtos = rows.OrderBy(r => r.Dir ?? "", StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.Counters.Weight).Select(r => new RowDto
        {
            SessionId = r.SessionId, HourUtc = r.HourUtc, Dir = r.Dir, Kind = (int)r.Chain.Kind, Writer = ImageDto.From(r.Chain.Writer), Top = ImageDto.From(r.Chain.Top),
            Links = r.Chain.Links, UnknownPid = r.Chain.UnknownPid, Counters = CountersDto.From(r.Counters),
        }).ToList();
        return new AgentQueryResult([.. index.Sessions.Select(SessionDto.From)], [.. index.Hours.Select(HourDto.From)], dtos, folded, hidden, hiddenRows);
    }

    private static StoredChain Sanitize(StoredChain c, CallerFilter filter)
    {
        var writerHidden = filter.IsOtherUserImage(c.Writer?.Path);
        var topHidden = filter.IsOtherUserImage(c.Top?.Path);
        if (!writerHidden && !topHidden)
        {
            return c;
        }

        var placeholder = new StoredImage(AgentProtocol.OtherUserImage, "", null, null);
        return c with
        {
            Writer = writerHidden ? placeholder : c.Writer,
            Top = topHidden ? placeholder : c.Top,
            Links = AgentProtocol.OtherUserImage,
        };
    }

    /// <summary>同一（目录, 进程链）的行合并：计数相加，会话号取最大（最近），小时取最早。</summary>
    private static List<Row> Aggregate(List<Row> rows)
    {
        var map = new Dictionary<(string Dir, StoredChain Chain), Row>(new KeyComparer());
        foreach (var r in rows)
        {
            var key = (r.Dir ?? "", r.Chain);
            if (map.TryGetValue(key, out var existing))
            {
                var c = existing.Counters;
                c.Add(r.Counters);
                map[key] = existing with
                {
                    SessionId = Math.Max(existing.SessionId, r.SessionId),
                    HourUtc = r.HourUtc < existing.HourUtc ? r.HourUtc : existing.HourUtc,
                    Counters = c,
                };
            }
            else
            {
                map[key] = r;
            }
        }

        return [.. map.Values];
    }

    private sealed record Row(long SessionId, DateTime HourUtc, string? Dir, StoredChain Chain, Counters Counters);

    private sealed class KeyComparer : IEqualityComparer<(string Dir, StoredChain Chain)>
    {
        public bool Equals((string Dir, StoredChain Chain) x, (string Dir, StoredChain Chain) y) =>
            string.Equals(x.Dir, y.Dir, StringComparison.OrdinalIgnoreCase) && x.Chain == y.Chain;

        public int GetHashCode((string Dir, StoredChain Chain) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Dir), obj.Chain);
    }
}

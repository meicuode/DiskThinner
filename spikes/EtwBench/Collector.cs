using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;

namespace EtwBench;

/// <summary>
/// 在事件泵线程上处理每个事件：做的事和产品里的汇总差不多（解析载荷、按 FileKey 对文件名、按目录累计），
/// 这样测出来的开销接近真实；另外记下 V 清单要看的数。只在一个线程上调用，统计字段只有采样线程读。
/// </summary>
internal sealed unsafe class Collector
{
    private readonly Templates _templates;
    private readonly string[] _prefixes;
    private readonly int _rawMax;
    private readonly int _slowUs;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly HashSet<int> _preexisting = [.. Process.GetProcesses().Select(p => p.Id)];
    private long _received;

    public Collector(Templates templates, string workDir, int rawMax, int slowUs)
    {
        _templates = templates;
        var prefixes = new List<string> { Program.DevicePath(workDir) + "\\" };
        if (Program.ShortPath(workDir) is { } s && !string.Equals(s, workDir, StringComparison.OrdinalIgnoreCase))
        {
            prefixes.Add(Program.DevicePath(s) + "\\");
        }

        _prefixes = [.. prefixes];
        _rawMax = rawMax;
        _slowUs = slowUs;
    }

    public long Received => Volatile.Read(ref _received);
    public int LoadPid { get; set; }
    public int CmdPid { get; set; }
    public long DowngradedAt { get; set; }

    // ---- 计数 ----
    private readonly Dictionary<(int Prov, int Id, int Ver), long> _counts = [];
    private readonly Dictionary<(int Prov, int Id, int Ver), long> _noTemplate = [];
    private readonly List<long> _filePerSec = [];
    private readonly List<long> _writePerSec = [];

    // ---- 文件名 ----
    private readonly Dictionary<ulong, string> _names = [];
    private readonly HashSet<ulong> _deletedKeys = [];
    private readonly Dictionary<ulong, long> _pendingWriteKeys = [];
    private long _namePeak, _keyReuse, _writeBeforeName, _writesAfterNameDelete;

    // ---- 写入 ----
    private long _writes, _writeBytes, _unresolvedWrites, _unresolvedBytes, _afterDowngradeWrites, _writesAfterDowngradeAll;

    // ---- 顺序和进程 ----
    private double _maxTs;
    private long _inversions;
    private double _maxInversionMs;
    private readonly Dictionary<int, (int Ppid, string Image)> _processes = [];
    private readonly Dictionary<int, long> _firstUnknownPidEvent = [];
    private long _earlyProcesses, _earlyFileEvents, _unknownPidFileEvents;
    private readonly Dictionary<string, long> _imageWriteBytes = new(StringComparer.OrdinalIgnoreCase);

    // ---- 工作目录里的 ----
    private readonly Dictionary<string, PartStats> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _pidCategories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _flagHistogram = new(StringComparer.Ordinal);
    private readonly List<string> _samples = [];
    private long _shortNamePaths;
    private string? _firstPathSeen;

    // ---- 原始事件 ----
    private readonly List<byte[]> _raw = [];

    public int RawCount => _raw.Count;

    public void OnEvent(TraceEvent e)
    {
        var arrival = Interlocked.Increment(ref _received);
        var now = Stopwatch.GetTimestamp();
        var ts = e.TimeStampRelativeMSec;
        if (ts < _maxTs)
        {
            _inversions++;
            _maxInversionMs = Math.Max(_maxInversionMs, _maxTs - ts);
        }
        else
        {
            _maxTs = ts;
        }

        var prov = e.ProviderGuid == Program.KernelFileGuid ? 0 : e.ProviderGuid == Program.KernelProcessGuid ? 1 : 2;
        var id = (int)e.ID;
        var ver = e.Version;
        var key = (prov, id, ver);
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
        var second = (int)((now - _start) / Stopwatch.Frequency);
        if (prov == 0)
        {
            Bump(_filePerSec, second);
            if (id == 16)
            {
                Bump(_writePerSec, second);
            }
        }

        var data = new ReadOnlySpan<byte>((void*)e.DataStart, e.EventDataLength);
        if (_raw.Count < _rawMax)
        {
            var rec = new byte[24 + data.Length];
            rec[0] = (byte)prov;
            BitConverter.TryWriteBytes(rec.AsSpan(1), (ushort)id);
            rec[3] = (byte)ver;
            BitConverter.TryWriteBytes(rec.AsSpan(4), e.ProcessID);
            BitConverter.TryWriteBytes(rec.AsSpan(8), e.ThreadID);
            BitConverter.TryWriteBytes(rec.AsSpan(12), ts);
            rec[20] = (byte)e.PointerSize;
            BitConverter.TryWriteBytes(rec.AsSpan(22), (ushort)data.Length);
            data.CopyTo(rec.AsSpan(24));
            _raw.Add(rec);
        }

        if (_slowUs > 0)
        {
            var until = now + _slowUs * Stopwatch.Frequency / 1_000_000;
            while (Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(20);
            }
        }

        if (prov == 2)
        {
            return;
        }

        var t = _templates.Find(prov == 0 ? Templates.KernelFile : Templates.KernelProcess, id, ver);
        if (t is null)
        {
            _noTemplate[key] = _noTemplate.GetValueOrDefault(key) + 1;
            return;
        }

        var ps = e.PointerSize;
        var pid = e.ProcessID;
        if (prov == 1)
        {
            OnProcess(t, data, ps, id, arrival);
            return;
        }

        if (!_processes.ContainsKey(pid) && !_preexisting.Contains(pid))
        {
            _unknownPidFileEvents++;
            _firstUnknownPidEvent.TryAdd(pid, arrival);
        }

        switch (id)
        {
            case 10: // NameCreate
            {
                var fk = t.Pointer(data, "FileKey", ps);
                var name = t.Text(data, "FileName", ps) ?? "";
                _firstPathSeen ??= name;
                if (_names.TryGetValue(fk, out var old) && !string.Equals(old, name, StringComparison.OrdinalIgnoreCase))
                {
                    _keyReuse++;
                }

                _names[fk] = name;
                _deletedKeys.Remove(fk);
                if (_pendingWriteKeys.Remove(fk, out var at) && (now - at) < 2 * Stopwatch.Frequency)
                {
                    _writeBeforeName++;
                }

                _namePeak = Math.Max(_namePeak, _names.Count);
                if (Under(name) is { } rel)
                {
                    Part(rel).NameCreates++;
                }

                break;
            }

            case 11: // NameDelete
            {
                var fk = t.Pointer(data, "FileKey", ps);
                var name = t.Text(data, "FileName", ps) ?? "";
                if (Under(name) is { } rel)
                {
                    var p = Part(rel);
                    p.NameDeletes++;
                    if (rel.EndsWith("big.bin", StringComparison.OrdinalIgnoreCase) || rel.Contains("\\r", StringComparison.OrdinalIgnoreCase))
                    {
                        p.NameDeletesOfSurvivors++; // 这些文件没有被删除：NameDelete 在关闭或名字缓存拆除时也会触发（V15）
                    }
                }

                _names.Remove(fk);
                if (_deletedKeys.Count < 200_000)
                {
                    _deletedKeys.Add(fk);
                }

                break;
            }

            case 16: // Write
            {
                var fk = t.Pointer(data, "FileKey", ps);
                var size = t.U32(data, "IOSize", ps);
                var offset = t.U64(data, "ByteOffset", ps);
                var flags = t.U32(data, "IOFlags", ps);
                _writes++;
                _writeBytes += size;
                if (DowngradedAt != 0 && now > DowngradedAt)
                {
                    _writesAfterDowngradeAll++;
                }

                if (!_names.TryGetValue(fk, out var name))
                {
                    _unresolvedWrites++;
                    _unresolvedBytes += size;
                    if (_deletedKeys.Contains(fk))
                    {
                        _writesAfterNameDelete++;
                    }

                    if (_pendingWriteKeys.Count < 100_000)
                    {
                        _pendingWriteKeys.TryAdd(fk, now);
                    }

                    break;
                }

                var image = _processes.TryGetValue(pid, out var proc) ? Path.GetFileName(proc.Image) : pid is 0 or 4 ? "System" : "pid";
                _imageWriteBytes[image] = _imageWriteBytes.GetValueOrDefault(image) + size;
                if (Under(name) is not { } relPath)
                {
                    break;
                }

                var part = Part(relPath);
                part.WriteOps++;
                part.WriteBytes += size;
                var who = Who(pid);
                part.WriteBytesBy[who] = part.WriteBytesBy.GetValueOrDefault(who) + size;
                var hk = $"{who}:0x{flags:X}";
                _flagHistogram[hk] = _flagHistogram.GetValueOrDefault(hk) + 1;
                var file = Path.GetFileName(relPath);
                if (file.StartsWith('a') || file.StartsWith('c'))
                {
                    var sk = offset == ulong.MaxValue ? "offset_ffffffffffffffff" : offset == ulong.MaxValue - 1 ? "offset_fffffffffffffffe" : "offset_real";
                    var kk = (file.StartsWith('a') ? "a_" : "c_") + sk;
                    part.Offsets[kk] = part.Offsets.GetValueOrDefault(kk) + 1;
                }

                if (file.Equals("after-downgrade.bin", StringComparison.OrdinalIgnoreCase))
                {
                    _afterDowngradeWrites++;
                }

                break;
            }

            case 26: // DeletePath
            case 27: // RenamePath
            case 28: // SetLinkPath
            case 30: // CreateNewFile
            {
                var path = t.Text(data, id == 30 ? "FileName" : "FilePath", ps) ?? "";
                _firstPathSeen ??= path;
                if (Under(path) is not { } rel)
                {
                    break;
                }

                var part = Part(rel);
                var who = Who(pid);
                _pidCategories[who] = _pidCategories.GetValueOrDefault(who) + 1;
                if (path.Contains('~'))
                {
                    _shortNamePaths++;
                }

                if (id == 30)
                {
                    var opts = t.U32(data, "CreateOptions", ps);
                    if ((opts & 1) != 0)
                    {
                        part.CreateDirs++;
                    }
                    else
                    {
                        part.Creates++;
                    }

                    if ((opts & 0x1000) != 0)
                    {
                        part.DeleteOnCloseCreates++;
                    }
                }
                else if (id == 26)
                {
                    part.Deletes++;
                }
                else if (id == 27)
                {
                    part.Renames++;
                    if (part.RenameSamples.Count < 3)
                    {
                        part.RenameSamples.Add(rel);
                    }
                }
                else
                {
                    part.Links++;
                }

                break;
            }
        }
    }

    private void OnProcess(Template t, ReadOnlySpan<byte> data, int ps, int id, long arrival)
    {
        if (id is not (1 or 15))
        {
            return;
        }

        var pid = (int)t.U32(data, "ProcessID", ps);
        var ppid = (int)t.U32(data, "ParentProcessID", ps);
        var image = t.Text(data, "ImageName", ps) ?? "";
        _processes[pid] = (ppid, image);
        if (id == 1 && _firstUnknownPidEvent.Remove(pid, out var first) && first < arrival)
        {
            _earlyProcesses++;
            _earlyFileEvents++;
        }

        if (pid == LoadPid || pid == CmdPid)
        {
            _samples.Add($"process {pid} parent {ppid} {Path.GetFileName(image)} image_format={(image.StartsWith("\\Device\\", StringComparison.OrdinalIgnoreCase) ? "device" : "other")}");
        }
    }

    private string Who(int pid) =>
        pid == LoadPid ? "load" : pid == CmdPid ? "cmd" : pid == Environment.ProcessId ? "bench" : pid is 0 or 4 ? "system" : _processes.TryGetValue(pid, out var p) && p.Image.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) ? "cmd" : "other";

    private string? Under(string path)
    {
        foreach (var p in _prefixes)
        {
            if (path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                return path[p.Length..];
            }
        }

        return null;
    }

    private PartStats Part(string rel)
    {
        var segs = rel.Split('\\');
        var name = segs[0].ToLowerInvariant();
        if (name == "move" && segs.Length > 1)
        {
            name = "move-" + segs[1].ToLowerInvariant();
        }
        else if (name.Contains('~') || name == "longdirectorynameforshortpath")
        {
            name = "short";
        }

        if (!_parts.TryGetValue(name, out var p))
        {
            _parts[name] = p = new PartStats();
        }

        return p;
    }

    private static void Bump(List<long> list, int second)
    {
        while (list.Count <= second)
        {
            list.Add(0);
        }

        list[second]++;
    }

    public void WriteTo(Report r, WriteLoadReport? load)
    {
        foreach (var ((prov, id, ver), n) in _counts.OrderBy(x => x.Key))
        {
            r.Add($"count_{(prov == 0 ? "file" : prov == 1 ? "process" : "other")}_{id}_v{ver}", n);
        }

        foreach (var ((prov, id, ver), n) in _noTemplate)
        {
            r.Add($"no_template_{prov}_{id}_v{ver}", n);
        }

        r.Add("file_events_per_sec_peak", _filePerSec.Count == 0 ? 0 : _filePerSec.Max());
        r.Add("write_events_per_sec_peak", _writePerSec.Count == 0 ? 0 : _writePerSec.Max());
        r.Add("writes", _writes);
        r.Add("write_bytes", _writeBytes);
        r.Add("unresolved_writes", _unresolvedWrites);
        r.Add("unresolved_bytes", _unresolvedBytes);
        r.Add("unresolved_bytes_ratio", _writeBytes == 0 ? 0 : (double)_unresolvedBytes / _writeBytes);
        r.Add("writes_after_name_delete", _writesAfterNameDelete);
        r.Add("name_map_peak", _namePeak);
        r.Add("name_map_end", _names.Count);
        r.Add("file_key_reused", _keyReuse);
        r.Add("v22_write_before_namecreate", _writeBeforeName);
        r.Add("v22_timestamp_inversions", _inversions);
        r.Add("v22_max_inversion_ms", _maxInversionMs);
        r.Add("v22_unknown_pid_file_events", _unknownPidFileEvents);
        r.Add("v22_file_event_before_process_start", _earlyProcesses);
        r.Add("processes_seen", _processes.Count);
        r.Add("first_path_seen", _firstPathSeen);
        r.Add("v13_short_name_paths", _shortNamePaths);
        r.Add("v17_writes_after_downgrade_in_workdir", _afterDowngradeWrites);
        r.Add("v17_writes_after_downgrade_all", _writesAfterDowngradeAll);
        foreach (var (k, v) in _pidCategories.OrderBy(x => x.Key))
        {
            r.Add("v1_namespace_events_by_" + k, v);
        }

        foreach (var (k, v) in _flagHistogram.OrderByDescending(x => x.Value).Take(12))
        {
            r.Add("v2_write_flags_" + k, v);
        }

        foreach (var (k, v) in _imageWriteBytes.OrderByDescending(x => x.Value).Take(10))
        {
            r.Add("top_writer_" + k, v);
        }

        foreach (var s in _samples.Take(10))
        {
            r.Add("sample", s);
        }

        foreach (var (name, p) in _parts.OrderBy(x => x.Key))
        {
            var exp = load?.Parts.GetValueOrDefault(name);
            r.Add($"part_{name}_creates", exp is null ? $"{p.Creates}" : $"{p.Creates}/{exp.CreatedFiles}");
            r.Add($"part_{name}_create_dirs", exp is null ? $"{p.CreateDirs}" : $"{p.CreateDirs}/{exp.CreatedDirs}");
            r.Add($"part_{name}_deletes", exp is null ? $"{p.Deletes}" : $"{p.Deletes}/{exp.Deleted}");
            r.Add($"part_{name}_renames", exp is null ? $"{p.Renames}" : $"{p.Renames}/{exp.Renamed}");
            r.Add($"part_{name}_write_bytes", exp is null ? $"{p.WriteBytes}" : $"{p.WriteBytes}/{exp.BytesWritten}");
            r.Add($"part_{name}_write_ops", p.WriteOps);
            foreach (var (who, b) in p.WriteBytesBy.OrderBy(x => x.Key))
            {
                r.Add($"part_{name}_write_bytes_by_{who}", b);
            }

            r.Add($"part_{name}_name_creates", p.NameCreates);
            r.Add($"part_{name}_name_deletes", p.NameDeletes);
            r.Add($"part_{name}_name_deletes_of_survivors", p.NameDeletesOfSurvivors);
            r.Add($"part_{name}_delete_on_close_creates", p.DeleteOnCloseCreates);
            r.Add($"part_{name}_links", p.Links);
            foreach (var (k, v) in p.Offsets)
            {
                r.Add($"part_{name}_{k}", v);
            }

            foreach (var s in p.RenameSamples)
            {
                r.Add($"part_{name}_rename_sample", s);
            }
        }

        if (load is not null)
        {
            r.Add("load_pid", load.Pid);
            r.Add("load_elapsed_ms", load.ElapsedMs);
            r.Add("load_short_name", load.ShortName);
        }
    }

    public void SaveRaw(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write("DTRAW1"u8);
        w.Write(_raw.Count);
        foreach (var rec in _raw)
        {
            w.Write(rec);
        }
    }

    private sealed class PartStats
    {
        public int Creates, CreateDirs, DeleteOnCloseCreates, Deletes, Renames, Links, NameCreates, NameDeletes, NameDeletesOfSurvivors;
        public long WriteOps, WriteBytes;
        public Dictionary<string, long> WriteBytesBy { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> Offsets { get; } = new(StringComparer.Ordinal);
        public List<string> RenameSamples { get; } = [];
    }
}

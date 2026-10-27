// 写入监控的测试负载（docs/phase2a-design.md 8.2 节，第 12.2 节第 3、5 条）。
//   DiskThinner.WriteLoad.exe --dir <目录> --report <json>
//     [--small 500] [--small-size 65536] [--rename 50] [--delete 100] [--big-size 134217728] [--chunk 1048576]
//     [--append 50] [--reopen 50] [--delete-on-close 50] [--prealloc 20] [--short-path 20] [--move 20]
// --dir 必须在 %TEMP% 下、名字以 DiskThinner-2a- 开头，否则退出码 2，什么都不写。只在这个目录下写。
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskThinner.WriteLoad;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        var o = Options.Parse(args);
        if (o is null)
        {
            Console.Error.WriteLine("参数不对，见源代码开头的用法");
            return 1;
        }

        var dir = Path.GetFullPath(o.Dir);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!dir.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(dir.TrimEnd('\\')).StartsWith("DiskThinner-2a-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("--dir 必须在 %TEMP% 下、名字以 DiskThinner-2a- 开头：" + dir);
            return 2;
        }

        Directory.CreateDirectory(dir);
        var report = new Report { Pid = Environment.ProcessId, StartedUtc = DateTime.UtcNow };
        var sw = Stopwatch.StartNew();
        Small(dir, o, report.Part("small"));
        Big(dir, o, report.Part("big"));
        Append(dir, o, report.Part("append"));
        DeleteOnClose(dir, o, report.Part("temp"));
        Prealloc(dir, o, report.Part("prealloc"));
        report.ShortName = ShortPath(dir, o, report.Part("short"));
        Move(dir, o, report);
        report.ElapsedMs = sw.ElapsedMilliseconds;
        report.EndedUtc = DateTime.UtcNow;
        foreach (var (name, part) in report.Parts)
        {
            var sub = Path.Combine(dir, part.Dir ?? name);
            part.FinalBytes = Directory.Exists(sub) ? Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        }

        File.WriteAllText(o.Report, JsonSerializer.Serialize(report, ReportJson.Default.Report));
        return 0;
    }

    private static readonly byte[] Chunk4K = Fill(4096);

    private static byte[] Fill(int length)
    {
        var data = new byte[length];
        new Random(20261026).NextBytes(data);
        return data;
    }

    private static void Write(string path, byte[] data, Part part, FileMode mode = FileMode.CreateNew)
    {
        using var fs = new FileStream(path, mode, FileAccess.Write, FileShare.Read);
        fs.Write(data);
        part.BytesWritten += data.Length;
        if (mode == FileMode.CreateNew)
        {
            part.CreatedFiles++;
        }
    }

    /// <summary>small\：f{i}.bin，前 rename 个改名为 r{i}.bin，接下来的 delete 个删除。</summary>
    private static void Small(string root, Options o, Part part)
    {
        var dir = Path.Combine(root, "small");
        Directory.CreateDirectory(dir);
        part.CreatedDirs++;
        var data = Fill(o.SmallSize);
        for (var i = 0; i < o.Small; i++)
        {
            Write(Path.Combine(dir, $"f{i}.bin"), data, part);
        }

        for (var i = 0; i < o.Rename && i < o.Small; i++)
        {
            File.Move(Path.Combine(dir, $"f{i}.bin"), Path.Combine(dir, $"r{i}.bin"));
            part.Renamed++;
        }

        for (var i = o.Rename; i < o.Rename + o.Delete && i < o.Small; i++)
        {
            File.Delete(Path.Combine(dir, $"f{i}.bin"));
            part.Deleted++;
        }
    }

    /// <summary>big\big.bin：普通缓存写，每次 chunk 字节。</summary>
    private static void Big(string root, Options o, Part part)
    {
        var dir = Path.Combine(root, "big");
        Directory.CreateDirectory(dir);
        part.CreatedDirs++;
        var chunk = Fill(o.Chunk);
        using var fs = new FileStream(Path.Combine(dir, "big.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
        part.CreatedFiles++;
        for (long done = 0; done < o.BigSize; done += chunk.Length)
        {
            var n = (int)Math.Min(chunk.Length, o.BigSize - done);
            fs.Write(chunk, 0, n);
            part.BytesWritten += n;
        }
    }

    /// <summary>
    /// append\：a{i}.bin 先写 4 KiB，再用只有 FILE_APPEND_DATA 的句柄追加两次 4 KiB（V16 的哨兵偏移）；
    /// o{i}.bin 先写 4 KiB，再普通打开、移到末尾写 4 KiB；c{i}.bin 先写 4 KiB，再用 cmd 的 >> 追加。
    /// </summary>
    private static void Append(string root, Options o, Part part)
    {
        var dir = Path.Combine(root, "append");
        Directory.CreateDirectory(dir);
        part.CreatedDirs++;
        for (var i = 0; i < o.Append; i++)
        {
            var path = Path.Combine(dir, $"a{i}.bin");
            Write(path, Chunk4K, part);
            using var h = CreateFileW(path, 0x00100004 /* FILE_APPEND_DATA | SYNCHRONIZE */, 1 /* FILE_SHARE_READ */, 0, 3 /* OPEN_EXISTING */, 0x80, 0);
            if (h.IsInvalid)
            {
                throw new IOException("打开追加句柄失败：" + Marshal.GetLastPInvokeError());
            }

            for (var k = 0; k < 2; k++)
            {
                if (!WriteFile(h, Chunk4K, Chunk4K.Length, out var written, 0) || written != Chunk4K.Length)
                {
                    throw new IOException("追加写失败：" + Marshal.GetLastPInvokeError());
                }

                part.BytesWritten += written;
                part.AppendDataBytes += written;
            }
        }

        for (var i = 0; i < o.Reopen; i++)
        {
            var path = Path.Combine(dir, $"o{i}.bin");
            Write(path, Chunk4K, part);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            fs.Seek(0, SeekOrigin.End);
            fs.Write(Chunk4K);
            part.BytesWritten += Chunk4K.Length;
        }

        // cmd 的 >> 追加：一次 cmd 处理所有文件，避免启动很多进程
        var count = Math.Min(o.Append, 20);
        if (count > 0)
        {
            var script = Path.Combine(dir, "append.cmd");
            var lines = new List<string> { "@echo off" };
            for (var i = 0; i < count; i++)
            {
                var path = Path.Combine(dir, $"c{i}.bin");
                Write(path, Chunk4K, part);
                lines.Add($"echo DiskThinner append line {i}>> \"{path}\"");
            }

            File.WriteAllLines(script, lines);
            part.CreatedFiles++;
            part.BytesWritten += new FileInfo(script).Length;
            var before = Enumerable.Range(0, count).Sum(i => new FileInfo(Path.Combine(dir, $"c{i}.bin")).Length);
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            p.WaitForExit();
            var after = Enumerable.Range(0, count).Sum(i => new FileInfo(Path.Combine(dir, $"c{i}.bin")).Length);
            part.CmdAppendBytes = after - before;
            part.CmdPid = p.Id;
        }
    }

    /// <summary>temp\：t{i}.tmp 用 DeleteOnClose 创建，写 64 KiB 后关闭（V12）。</summary>
    private static void DeleteOnClose(string root, Options o, Part part)
    {
        var dir = Path.Combine(root, "temp");
        Directory.CreateDirectory(dir);
        part.CreatedDirs++;
        var data = Fill(64 * 1024);
        for (var i = 0; i < o.DeleteOnClose; i++)
        {
            using var fs = new FileStream(Path.Combine(dir, $"t{i}.tmp"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.DeleteOnClose);
            fs.Write(data);
            part.CreatedFiles++;
            part.Deleted++;
            part.BytesWritten += data.Length;
        }
    }

    /// <summary>prealloc\：p{i}.bin 只 SetLength(1 MiB)，不写（V16）。</summary>
    private static void Prealloc(string root, Options o, Part part)
    {
        var dir = Path.Combine(root, "prealloc");
        Directory.CreateDirectory(dir);
        part.CreatedDirs++;
        for (var i = 0; i < o.Prealloc; i++)
        {
            using var fs = new FileStream(Path.Combine(dir, $"p{i}.bin"), FileMode.CreateNew, FileAccess.Write);
            fs.SetLength(1024 * 1024);
            part.CreatedFiles++;
        }
    }

    /// <summary>LongDirectoryNameForShortPath\：通过 8.3 短路径写 s{i}.bin（V13）。没有短名时跳过，返回 null。</summary>
    private static string? ShortPath(string root, Options o, Part part)
    {
        var dir = Path.Combine(root, "LongDirectoryNameForShortPath");
        part.Dir = "LongDirectoryNameForShortPath";
        Directory.CreateDirectory(dir);
        part.CreatedDirs++;
        var buffer = new char[1024];
        var n = GetShortPathNameW(dir, buffer, buffer.Length);
        var shortDir = n > 0 ? new string(buffer, 0, (int)n) : null;
        if (shortDir is null || string.Equals(shortDir, dir, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        for (var i = 0; i < o.ShortPath; i++)
        {
            Write(Path.Combine(shortDir, $"s{i}.bin"), Chunk4K, part);
        }

        return Path.GetFileName(shortDir.TrimEnd('\\'));
    }

    /// <summary>
    /// move\：m{i}.bin 写到 move\tmp\ 再移到 move\dst\（跨目录移动）；再建 move\stage\ 写 m{i}.bin，整个目录改名为 move\done\（目录改名）。
    /// 期望：dst\、done\ 的估算增长等于最终大小，tmp\、stage\ 为 0。
    /// </summary>
    private static void Move(string root, Options o, Report report)
    {
        var tmp = report.Part("move-tmp");
        tmp.Dir = @"move\tmp";
        var dst = report.Part("move-dst");
        dst.Dir = @"move\dst";
        var done = report.Part("move-done");
        done.Dir = @"move\done";
        var tmpDir = Path.Combine(root, "move", "tmp");
        var dstDir = Path.Combine(root, "move", "dst");
        var stageDir = Path.Combine(root, "move", "stage");
        Directory.CreateDirectory(tmpDir);
        Directory.CreateDirectory(dstDir);
        Directory.CreateDirectory(stageDir);
        tmp.CreatedDirs += 2; // move\ 和 move\tmp\
        dst.CreatedDirs++;
        done.CreatedDirs++;
        var data = Fill(64 * 1024);
        for (var i = 0; i < o.Move; i++)
        {
            var path = Path.Combine(tmpDir, $"m{i}.bin");
            Write(path, data, tmp);
            File.Move(path, Path.Combine(dstDir, $"m{i}.bin"));
            dst.Renamed++;
        }

        for (var i = 0; i < o.Move; i++)
        {
            Write(Path.Combine(stageDir, $"m{i}.bin"), data, done);
        }

        Directory.Move(stageDir, Path.Combine(root, "move", "done"));
        done.Renamed++;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteFile(Microsoft.Win32.SafeHandles.SafeFileHandle file, byte[] buffer, int count, out int written, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetShortPathNameW(string longPath, [Out] char[] shortPath, int length);
}

internal sealed record Options(string Dir, string Report, int Small, int SmallSize, int Rename, int Delete, long BigSize, int Chunk,
    int Append, int Reopen, int DeleteOnClose, int Prealloc, int ShortPath, int Move)
{
    public static Options? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            map[args[i][2..]] = args[i + 1];
        }

        if (args.Length % 2 != 0 || !map.TryGetValue("dir", out var dir) || !map.TryGetValue("report", out var report))
        {
            return null;
        }

        long Get(string name, long fallback) => map.TryGetValue(name, out var v) ? long.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : fallback;
        return new Options(dir, report, (int)Get("small", 500), (int)Get("small-size", 65536), (int)Get("rename", 50), (int)Get("delete", 100),
            Get("big-size", 128L << 20), (int)Get("chunk", 1 << 20), (int)Get("append", 50), (int)Get("reopen", 50), (int)Get("delete-on-close", 50),
            (int)Get("prealloc", 20), (int)Get("short-path", 20), (int)Get("move", 20));
    }
}

internal sealed class Part
{
    [JsonIgnore]
    public string? Dir { get; set; }

    public string? Path => Dir;
    public int CreatedFiles { get; set; }
    public int CreatedDirs { get; set; }
    public int Deleted { get; set; }
    public int Renamed { get; set; }
    public long BytesWritten { get; set; }
    public long AppendDataBytes { get; set; }
    public long CmdAppendBytes { get; set; }
    public int CmdPid { get; set; }
    public long FinalBytes { get; set; }
}

internal sealed class Report
{
    public int Pid { get; set; }
    public string? ShortName { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime EndedUtc { get; set; }
    public long ElapsedMs { get; set; }
    public Dictionary<string, Part> Parts { get; set; } = new(StringComparer.Ordinal);

    public Part Part(string name)
    {
        if (!Parts.TryGetValue(name, out var p))
        {
            Parts[name] = p = new Part();
        }

        return p;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Report))]
internal sealed partial class ReportJson : JsonSerializerContext;

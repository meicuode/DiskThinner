using System.Buffers.Binary;
using System.Text;

namespace DiskThinner.Monitor.Tests;

/// <summary>
/// 手写的载荷解析和系统清单导出的模板（tests/fixtures/monitor/templates.txt，tools/verify/export-etw-templates.ps1）一致
/// （docs/phase2a-design.md 5.4 节）：按模板的字段类型拼字节，每个字段填可以辨认的值，检查解析器取到的字段都对。
/// </summary>
public class TemplateConformanceTests
{
    private const long FileTimeEpoch = 504911232000000000L;

    internal sealed record EventTemplate(string Provider, int Id, int Version, List<(string Name, string Type)> Fields);

    internal static string TemplatesPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "monitor", "templates.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("找不到 tests/fixtures/monitor/templates.txt");
    }

    internal static List<EventTemplate> Load()
    {
        var list = new List<EventTemplate>();
        foreach (var line in File.ReadAllLines(TemplatesPath()))
        {
            if (line.StartsWith("event ", StringComparison.Ordinal))
            {
                var p = line.Split(' ');
                list.Add(new EventTemplate(p[1], int.Parse(p[2]), int.Parse(p[3].TrimStart('v')), []));
            }
            else if (line.StartsWith("  ", StringComparison.Ordinal) && list.Count > 0)
            {
                var p = line.Trim().Split(' ');
                list[^1].Fields.Add((p[0], p[1]));
            }
        }

        return list;
    }

    private static Guid ProviderGuid(string name) => name == "Microsoft-Windows-Kernel-File" ? Providers.KernelFile : Providers.KernelProcess;

    public static TheoryData<string, int, int> Events()
    {
        var data = new TheoryData<string, int, int>();
        foreach (var t in Load())
        {
            data.Add(t.Provider, t.Id, t.Version);
        }

        return data;
    }

    [Fact]
    public void FixtureHasEveryVersionTheParserSupports()
    {
        var templates = Load();
        Assert.True(templates.Count >= 20, "模板文件只有 " + templates.Count + " 个事件版本");
        foreach (var t in templates)
        {
            Assert.True(PayloadParser.Supports(ProviderGuid(t.Provider), t.Id, t.Version), $"系统清单里有 {t.Provider} {t.Id} v{t.Version}，解析器还不支持");
        }
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void ParserReadsEveryUsedField(string provider, int id, int version)
    {
        var t = Load().Single(x => x.Provider == provider && x.Id == id && x.Version == version);
        foreach (var sidCount in new[] { 1, 5 })
        {
            var (bytes, expected) = Build(t, sidCount);
            var e = new MonitorEvent();
            var raw = new RawEvent(ProviderGuid(provider), id, version, 1234, 5678, 99, 8, bytes);
            Assert.True(PayloadParser.TryParse(raw, e), $"{provider} {id} v{version} 解析失败");
            foreach (var (field, value) in expected)
            {
                object? actual = field switch
                {
                    "FileKey" => e.FileKey,
                    "Irp" => e.Irp,
                    "ByteOffset" => e.ByteOffset,
                    "IOSize" => e.IoSize,
                    "IOFlags" => e.IoFlags,
                    "CreateOptions" => e.CreateOptions,
                    "FileName" or "FilePath" => e.Path,
                    "ProcessID" => (uint)e.TargetProcessId,
                    "ParentProcessID" => (uint)e.ParentProcessId,
                    "ProcessSequenceNumber" => e.Sequence,
                    "ParentProcessSequenceNumber" => e.ParentSequence,
                    "CreateTime" => (ulong)(e.CreateTimeUtcTicks - FileTimeEpoch),
                    "ExitTime" => (ulong)(e.ExitTimeUtcTicks - FileTimeEpoch),
                    "ImageName" => e.ImageName,
                    _ => value,
                };
                Assert.True(Equals(value, actual), $"{provider} {id} v{version} 的 {field}：期望 {value}，实际 {actual}");
            }

            Assert.Equal(1234, e.ProcessId);

            // 截断的载荷：解析失败，不抛异常
            var truncated = new RawEvent(ProviderGuid(provider), id, version, 1, 1, 1, 8, bytes.AsSpan(0, 6));
            Assert.False(PayloadParser.TryParse(truncated, new MonitorEvent()));
        }
    }

    [Fact]
    public void UnknownVersionsAreRejected()
    {
        Assert.False(PayloadParser.TryParse(new RawEvent(Providers.KernelFile, 16, 9, 1, 1, 1, 8, new byte[64]), new MonitorEvent()));
        Assert.False(PayloadParser.TryParse(new RawEvent(Providers.KernelFile, 12, 0, 1, 1, 1, 8, new byte[64]), new MonitorEvent()));
        Assert.False(PayloadParser.TryParse(new RawEvent(Guid.NewGuid(), 1, 0, 1, 1, 1, 8, new byte[64]), new MonitorEvent()));
    }

    /// <summary>按模板拼字节，返回每个字段填的值（字段名 → 期望解析出的值）。</summary>
    internal static (byte[] Bytes, Dictionary<string, object> Expected) Build(EventTemplate t, int sidCount)
    {
        var ms = new MemoryStream();
        var expected = new Dictionary<string, object>(StringComparer.Ordinal);
        var i = 0;
        foreach (var (name, type) in t.Fields)
        {
            i++;
            switch (type)
            {
                case "win:Pointer":
                case "win:UInt64":
                case "win:HexInt64":
                {
                    var v = 0x1111_2222_0000_0000UL + (ulong)i;
                    Write64(ms, v);
                    expected[name] = v;
                    break;
                }

                case "win:FILETIME":
                {
                    var v = 133_000_000_000_000_000UL + (ulong)i;
                    Write64(ms, v);
                    expected[name] = v;
                    break;
                }

                case "win:UInt32":
                case "win:HexInt32":
                {
                    var v = 0x4000_0000U + (uint)i;
                    var b = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
                    ms.Write(b);
                    expected[name] = v;
                    break;
                }

                case "win:UnicodeString":
                {
                    var s = $@"\Device\HarddiskVolume3\dir{i}\文件{i}.txt";
                    ms.Write(Encoding.Unicode.GetBytes(s + "\0"));
                    expected[name] = s;
                    break;
                }

                case "win:AnsiString":
                {
                    var s = $"image{i}.exe";
                    ms.Write(Encoding.ASCII.GetBytes(s + "\0"));
                    expected[name] = s;
                    break;
                }

                case "win:SID":
                {
                    ms.WriteByte(1);
                    ms.WriteByte((byte)sidCount);
                    ms.Write(new byte[] { 0, 0, 0, 0, 0, 16 });
                    for (var k = 0; k < sidCount; k++)
                    {
                        ms.Write(BitConverter.GetBytes(0x3000 + k));
                    }

                    break;
                }

                default:
                    throw new InvalidDataException("模板里有不认识的类型：" + type);
            }
        }

        return (ms.ToArray(), expected);
    }

    private static void Write64(Stream s, ulong v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(b, v);
        s.Write(b);
    }
}

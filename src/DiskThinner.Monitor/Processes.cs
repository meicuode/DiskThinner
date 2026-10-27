using DiskThinner.Core;

namespace DiskThinner.Monitor;

/// <summary>一个进程实例：（进程号, 创建时间）标识，进程号复用不会混（docs/phase2a-design.md 5.5.1 节）。</summary>
public sealed class ProcessInstance
{
    public required int Pid { get; init; }
    public required long CreateTicks { get; init; }
    public required int ParentPid { get; init; }
    public ulong Sequence { get; init; }
    public ulong ParentSequence { get; init; }

    /// <summary>映像路径（设备路径或盘符路径）；打不开的受保护进程只有 exe 名。</summary>
    public required string Image { get; init; }

    public long ExitTicks { get; set; }

    /// <summary>服务宿主里的服务（短名, 显示名），没有为空。</summary>
    public IReadOnlyList<(string Name, string Display)> Services { get; set; } = [];

    public string ExeName => Path.GetFileName(Image);

    /// <summary>进程链缓存：第一次要的时候算好（父进程链在进程启动之后不会变）。</summary>
    internal int ChainId { get; set; } = -1;
}

/// <summary>服务进程号 → 服务（EnumServicesStatusEx）。测试用假的。</summary>
public interface IServiceNames
{
    IReadOnlyList<(string Name, string Display)> For(int pid);
}

/// <summary>进程表：按进程号存各个实例；查事件属于哪个实例、父进程是谁。</summary>
public sealed class ProcessTable
{
    public const int MaxExited = 100_000;

    private readonly Dictionary<int, List<ProcessInstance>> _byPid = [];
    private readonly Dictionary<ulong, ProcessInstance> _bySequence = [];
    private readonly Queue<ProcessInstance> _exited = new();

    public int Count { get; private set; }

    public ProcessInstance Add(int pid, long createTicks, int parentPid, string image, ulong sequence = 0, ulong parentSequence = 0)
    {
        var list = GetList(pid);
        foreach (var existing in list)
        {
            if (existing.CreateTicks == createTicks && createTicks != 0)
            {
                return existing; // 列举和事件都看到了同一个进程：以先到的为准
            }
        }

        var p = new ProcessInstance { Pid = pid, CreateTicks = createTicks, ParentPid = parentPid, Image = image, Sequence = sequence, ParentSequence = parentSequence };
        list.Add(p);
        Count++;
        if (sequence != 0)
        {
            _bySequence[sequence] = p;
        }

        return p;
    }

    public void Exit(int pid, long createTicks, long exitTicks)
    {
        if (!_byPid.TryGetValue(pid, out var list))
        {
            return;
        }

        foreach (var p in list)
        {
            if (p.ExitTicks == 0 && (createTicks == 0 || p.CreateTicks == createTicks))
            {
                p.ExitTicks = exitTicks;
                _exited.Enqueue(p);
                break;
            }
        }

        while (_exited.Count > MaxExited)
        {
            var old = _exited.Dequeue();
            if (_byPid.TryGetValue(old.Pid, out var l) && l.Remove(old))
            {
                Count--;
                if (old.Sequence != 0)
                {
                    _bySequence.Remove(old.Sequence);
                }
            }
        }
    }

    /// <summary>
    /// 在 <paramref name="ticks"/> 时刻进程号为 <paramref name="pid"/> 的实例：创建时间 ≤ 事件时间、没退出或退出时间 ≥ 事件时间，
    /// 有多个时取创建最晚的。找不到返回 null（第 12.2 节第 2 条）。
    /// </summary>
    public ProcessInstance? Find(int pid, long ticks)
    {
        if (!_byPid.TryGetValue(pid, out var list))
        {
            return null;
        }

        ProcessInstance? best = null;
        foreach (var p in list)
        {
            if (p.CreateTicks <= ticks && (p.ExitTicks == 0 || p.ExitTicks >= ticks) && (best is null || p.CreateTicks > best.CreateTicks))
            {
                best = p;
            }
        }

        return best;
    }

    /// <summary>父进程：有序号时按序号精确匹配；否则取进程号相同、创建早于子进程、子进程创建时还没退出的实例。</summary>
    public ProcessInstance? Parent(ProcessInstance child)
    {
        if (child.ParentSequence != 0 && _bySequence.TryGetValue(child.ParentSequence, out var bySeq))
        {
            return bySeq;
        }

        if (child.ParentPid == child.Pid || !_byPid.TryGetValue(child.ParentPid, out var list))
        {
            return null;
        }

        ProcessInstance? best = null;
        foreach (var p in list)
        {
            if (p.CreateTicks <= child.CreateTicks && (p.ExitTicks == 0 || p.ExitTicks >= child.CreateTicks) && (best is null || p.CreateTicks > best.CreateTicks))
            {
                best = p;
            }
        }

        return best;
    }

    public IEnumerable<ProcessInstance> All => _byPid.Values.SelectMany(l => l);

    private List<ProcessInstance> GetList(int pid)
    {
        if (!_byPid.TryGetValue(pid, out var list))
        {
            _byPid[pid] = list = [];
        }

        return list;
    }
}

public enum ChainKind : byte
{
    Normal,

    /// <summary>进程号 0 和 4：缓存回写或内核。</summary>
    System,

    /// <summary>没见过的进程号。</summary>
    Unknown,

    /// <summary>进程链表满了，或者合并时并到一起的"其他进程"（chain_id 0）。</summary>
    Other,
}

/// <summary>一条进程链：写入进程、顶层应用、显示用的链（docs/phase2a-design.md 5.5.3 节）。服务名逗号分隔；Links 形如"uv.exe ← pwsh.exe ← Code.exe"。</summary>
public sealed record ProcessChain(ChainKind Kind, string WriterImage, string WriterServices, string TopImage, string TopServices, string Links, int UnknownPid = 0)
{
    public static readonly ProcessChain Other = new(ChainKind.Other, "", "", "", "", "");
    public static readonly ProcessChain SystemChain = new(ChainKind.System, "System", "", "System", "", "System");

    public string WriterExe => Path.GetFileName(WriterImage);
    public string TopExe => Path.GetFileName(TopImage);
}

/// <summary>按 D1（最外层的不透明程序）算顶层应用和进程链；把进程链编号（一个会话最多 10,000 种，超出的算"其他进程"）。</summary>
public sealed class ChainTable
{
    public const int MaxChains = 10_000;

    private static readonly HashSet<string> SystemRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "explorer.exe",
        "userinit.exe", "sihost.exe", "svchost.exe", "taskhostw.exe",
    };

    private static readonly HashSet<string> Transparent = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe", "OpenConsole.exe", "WindowsTerminal.exe", "wt.exe", "bash.exe", "sh.exe",
        "node.exe", "python.exe", "pythonw.exe", "py.exe", "java.exe", "javaw.exe", "dotnet.exe",
    };

    private readonly List<ProcessChain> _chains = [ProcessChain.Other];
    private readonly Dictionary<ProcessChain, int> _ids = [];

    public bool Full { get; private set; }

    public int Count => _chains.Count - 1;

    public ProcessChain this[int id] => id >= 0 && id < _chains.Count ? _chains[id] : ProcessChain.Other;

    public IReadOnlyList<ProcessChain> All => _chains;

    public static bool IsSystemRoot(string exe) => SystemRoots.Contains(exe);

    public static bool IsTransparent(string exe) => Transparent.Contains(exe);

    public int Intern(ProcessChain chain)
    {
        if (chain.Kind == ChainKind.Other)
        {
            return 0;
        }

        if (_ids.TryGetValue(chain, out var id))
        {
            return id;
        }

        if (_chains.Count > MaxChains)
        {
            Full = true;
            return 0;
        }

        _chains.Add(chain);
        _ids[chain] = _chains.Count - 1;
        return _chains.Count - 1;
    }

    /// <summary>事件属于的进程链编号：进程号 0 / 4 是系统；没见过的进程是"未知进程"。</summary>
    public int For(int pid, ProcessInstance? process, ProcessTable table)
    {
        if (pid is 0 or 4)
        {
            return Intern(ProcessChain.SystemChain);
        }

        if (process is null)
        {
            return Intern(new ProcessChain(ChainKind.Unknown, "", "", "", "", "", pid));
        }

        if (process.ChainId < 0)
        {
            process.ChainId = Intern(Resolve(process, table));
        }

        return process.ChainId;
    }

    /// <summary>
    /// 从写入进程沿父进程链往上走，碰到系统根就停（有服务名的服务宿主作为最后一环显示），取路上最外层的不透明进程作为顶层；
    /// 全是透明进程时取写入进程自己。服务宿主自己写入时顶层就是它的服务。相邻的同名进程合成一个。
    /// </summary>
    public static ProcessChain Resolve(ProcessInstance writer, ProcessTable table)
    {
        var writerServices = Join(writer.Services);
        if (writer.Services.Count > 0)
        {
            return new ProcessChain(ChainKind.Normal, writer.Image, writerServices, writer.Image, writerServices, Display(writer));
        }

        var path = new List<ProcessInstance> { writer };
        ProcessInstance? stopHost = null;
        var seen = new HashSet<ProcessInstance> { writer };
        for (var cur = table.Parent(writer); cur is not null && path.Count < 64 && seen.Add(cur); cur = table.Parent(cur))
        {
            if (IsSystemRoot(cur.ExeName))
            {
                if (cur.Services.Count > 0)
                {
                    stopHost = cur;
                }

                break;
            }

            if (!string.Equals(cur.ExeName, path[^1].ExeName, StringComparison.OrdinalIgnoreCase))
            {
                path.Add(cur);
            }
        }

        var top = writer;
        for (var i = path.Count - 1; i >= 0; i--)
        {
            if (!IsTransparent(path[i].ExeName))
            {
                top = path[i];
                break;
            }
        }

        var links = new List<string> { Display(writer) };
        var topIndex = path.IndexOf(top);
        for (var i = 1; i < path.Count && i <= 3; i++)
        {
            links.Add(Display(path[i]));
        }

        if (topIndex > 3)
        {
            links.Add(L.T("…"));
            links.Add(Display(top));
        }

        if (stopHost is not null && path.Count <= 3)
        {
            links.Add(Display(stopHost));
        }

        return new ProcessChain(ChainKind.Normal, writer.Image, "", top.Image, Join(top.Services), string.Join(" ← ", links));
    }

    /// <summary>存储用的形式：服务宿主写成 svchost.exe[wuauserv,bits]，界面再格式化成"svchost.exe（服务 wuauserv）"。</summary>
    private static string Display(ProcessInstance p) => p.Services.Count == 0 ? p.ExeName : $"{p.ExeName}[{Join(p.Services)}]";

    private static string Join(IReadOnlyList<(string Name, string Display)> services) => string.Join(",", services.Select(s => s.Name));
}

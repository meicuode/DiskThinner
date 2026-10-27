namespace DiskThinner.Monitor.Tests;

/// <summary>顶层应用和进程链（docs/phase2a-design.md 5.5.3 节，D1：最外层的不透明程序）。</summary>
public class ProcessChainTests
{
    private readonly ProcessTable _table = new();
    private long _clock = 1000;

    private ProcessInstance Start(int pid, int parent, string exe, params (string, string)[] services)
    {
        var p = _table.Add(pid, _clock += 10, parent, @"C:\Program Files\X\" + exe);
        p.Services = services;
        return p;
    }

    private static string Links(ProcessChain c) => c.Links;

    [Fact]
    public void TopIsTheOutermostOpaqueProgram()
    {
        Start(1, 0, "explorer.exe");
        Start(2, 1, "WindowsTerminal.exe");
        Start(3, 2, "pwsh.exe");
        Start(4, 3, "claude.exe");
        var node = Start(5, 4, "node.exe");
        var c = ChainTable.Resolve(node, _table);
        Assert.Equal("claude.exe", c.TopExe);
        Assert.Equal("node.exe", c.WriterExe);
        Assert.Equal("node.exe ← claude.exe ← pwsh.exe ← WindowsTerminal.exe", Links(c));
    }

    [Fact]
    public void ShellsAreSkippedAndEditorIsTop()
    {
        Start(1, 0, "explorer.exe");
        Start(2, 1, "Code.exe");
        Start(3, 2, "pwsh.exe");
        var uv = Start(4, 3, "uv.exe");
        var c = ChainTable.Resolve(uv, _table);
        Assert.Equal("Code.exe", c.TopExe);
        Assert.Equal("uv.exe ← pwsh.exe ← Code.exe", Links(c));
    }

    [Fact]
    public void WriterIsTopWhenEverythingAboveIsTransparent()
    {
        Start(1, 0, "explorer.exe");
        Start(2, 1, "WindowsTerminal.exe");
        Start(3, 2, "pwsh.exe");
        var codex = Start(4, 3, "codex.exe");
        Assert.Equal("codex.exe", ChainTable.Resolve(codex, _table).TopExe);
    }

    [Fact]
    public void ServiceHostIsItsOwnTopAndChildrenShowTheService()
    {
        Start(1, 0, "services.exe");
        var host = Start(2, 1, "svchost.exe", ("wuauserv", "Windows 更新"));
        var c = ChainTable.Resolve(host, _table);
        Assert.Equal("wuauserv", c.TopServices);
        Assert.Equal("svchost.exe[wuauserv]", c.Links);

        var worker = Start(3, 2, "TiWorker.exe");
        var w = ChainTable.Resolve(worker, _table);
        Assert.Equal("TiWorker.exe", w.TopExe);
        Assert.Equal("TiWorker.exe ← svchost.exe[wuauserv]", w.Links);
    }

    [Fact]
    public void AdjacentSameNameProcessesAreMerged()
    {
        Start(1, 0, "explorer.exe");
        Start(2, 1, "msedge.exe");
        var child = Start(3, 2, "msedge.exe");
        Assert.Equal("msedge.exe", ChainTable.Resolve(child, _table).Links);
    }

    [Fact]
    public void LongChainsShowTheTopAfterAnEllipsis()
    {
        Start(1, 0, "explorer.exe");
        Start(2, 1, "Code.exe");
        Start(3, 2, "pwsh.exe");
        Start(4, 3, "cmd.exe");
        Start(5, 4, "node.exe");
        Start(6, 5, "python.exe");
        var w = Start(7, 6, "uv.exe");
        var c = ChainTable.Resolve(w, _table);
        Assert.Equal("Code.exe", c.TopExe);
        Assert.Equal("uv.exe ← python.exe ← node.exe ← cmd.exe ← … ← Code.exe", c.Links);
    }

    [Fact]
    public void SystemAndUnknownProcesses()
    {
        var chains = new ChainTable();
        Assert.Equal(ChainKind.System, chains[chains.For(4, null, _table)].Kind);
        Assert.Equal(ChainKind.System, chains[chains.For(0, null, _table)].Kind);
        var unknown = chains[chains.For(777, null, _table)];
        Assert.Equal(ChainKind.Unknown, unknown.Kind);
        Assert.Equal(777, unknown.UnknownPid);
    }

    [Fact]
    public void ReusedProcessIdsResolveByTime()
    {
        var a = _table.Add(100, 1000, 0, @"C:\a.exe");
        _table.Exit(100, 1000, 2000);
        var b = _table.Add(100, 3000, 0, @"C:\b.exe");
        Assert.Same(a, _table.Find(100, 1500));
        Assert.Same(b, _table.Find(100, 3500));
        Assert.Null(_table.Find(100, 500));
    }

    [Fact]
    public void ParentMatchesBySequenceNumberWhenPresent()
    {
        var oldParent = _table.Add(10, 100, 0, @"C:\old.exe", sequence: 50);
        _table.Exit(10, 100, 200);
        var newParent = _table.Add(10, 300, 0, @"C:\new.exe", sequence: 60);
        var child = _table.Add(11, 400, 10, @"C:\child.exe", sequence: 61, parentSequence: 50);
        Assert.Same(oldParent, _table.Parent(child)); // 按序号：父进程早已退出、进程号被复用也认得出
        var child2 = _table.Add(12, 400, 10, @"C:\child2.exe");
        Assert.Same(newParent, _table.Parent(child2)); // 没有序号：取子进程创建时还在的那个
    }

    [Fact]
    public void ChainTableIsBounded()
    {
        var chains = new ChainTable();
        for (var i = 0; i < ChainTable.MaxChains + 50; i++)
        {
            chains.Intern(new ProcessChain(ChainKind.Normal, $"w{i}.exe", "", "t.exe", "", $"w{i}.exe"));
        }

        Assert.True(chains.Full);
        Assert.Equal(ChainTable.MaxChains, chains.Count);
        Assert.Equal(0, chains.Intern(new ProcessChain(ChainKind.Normal, "late.exe", "", "t.exe", "", "late.exe")));
    }
}

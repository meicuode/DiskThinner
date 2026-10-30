using System.ComponentModel;
using DiskThinner.App.Agent;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.App.Tests;

/// <summary>
/// 安装器的步骤和回滚（docs/phase2b-design.md 5.3 节、8.1 节 InstallerTests）：假的 SCM，真实的文件复制（临时目录），
/// 数据目录的权限和管道应答换成假的。每一步失败时，已经做了的步骤都撤销；已有同名服务时的判断；更新失败时恢复旧版本。
/// </summary>
public sealed class AgentInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DiskThinner-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeScm _scm = new();
    private readonly List<string> _log = [];
    private readonly AgentLayout _layout;
    private string? _helloVersion = "0.3.0";

    public AgentInstallerTests()
    {
        var source = Path.Combine(_root, "release", "Agent");
        Directory.CreateDirectory(source);
        WriteSource("v1");
        _layout = new AgentLayout
        {
            SourceDir = source,
            ProgramRoot = Path.Combine(_root, "pf", "DiskThinner", "Agent"),
            DataDir = Path.Combine(_root, "pd", "DiskThinner", "Agent"),
            ServiceName = "DiskThinnerAgentTest",
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void WriteSource(string content)
    {
        var source = Path.Combine(_root, "release", "Agent");
        File.WriteAllText(Path.Combine(source, "DiskThinner.Agent.exe"), "exe " + content);
        File.WriteAllText(Path.Combine(source, "e_sqlite3.dll"), "dll " + content);
    }

    private AgentInstaller Installer(Func<string, bool>? prepare = null) => new(_layout, _scm)
    {
        PrepareDataDir = prepare ?? (d =>
        {
            Directory.CreateDirectory(d);
            return false;
        }),
        Hello = () => _helloVersion,
        StopSession = _ => false,
        Log = _log.Add,
    };

    private string[] VersionDirs() => Directory.Exists(_layout.ProgramRoot) ? Directory.GetDirectories(_layout.ProgramRoot) : [];

    [Fact]
    public void Install_CopiesRegistersStarts()
    {
        var r = Installer().Install(new AgentInstallOptions("light", 30, 5));
        Assert.True(r.Ok, r.Message);
        var dir = Assert.Single(VersionDirs());
        Assert.Equal("exe v1", File.ReadAllText(Path.Combine(dir, "DiskThinner.Agent.exe")));
        var svc = _scm.Services["DiskThinnerAgentTest"];
        Assert.Equal("running", svc.State);
        Assert.StartsWith("\"" + Path.Combine(dir, "DiskThinner.Agent.exe") + "\"", svc.BinaryPath, StringComparison.Ordinal);
        var config = File.ReadAllText(Path.Combine(_layout.DataDir, "agent.json"));
        Assert.Contains("\"mode\": \"light\"", config, StringComparison.Ordinal);
        Assert.Contains("\"keepDays\": 30", config, StringComparison.Ordinal);
        Assert.Contains("\"flushSeconds\": 5", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_MissingSource_FailsWithoutChanges()
    {
        File.Delete(Path.Combine(_layout.SourceDir, "e_sqlite3.dll"));
        var r = Installer().Install(new AgentInstallOptions());
        Assert.False(r.Ok);
        Assert.Empty(_scm.Services);
        Assert.False(Directory.Exists(_layout.ProgramRoot));
    }

    [Fact]
    public void Install_CreateFails_RollsBackCopyAndNewDataDir()
    {
        _scm.FailCreate = true;
        var r = Installer().Install(new AgentInstallOptions());
        Assert.False(r.Ok);
        Assert.Empty(VersionDirs());
        Assert.False(Directory.Exists(_layout.DataDir));
        Assert.Empty(_scm.Services);
    }

    [Fact]
    public void Install_ServiceDoesNotStart_RollsBackEverything_KeepsExistingData()
    {
        Directory.CreateDirectory(_layout.DataDir);
        File.WriteAllText(Path.Combine(_layout.DataDir, "monitor.db"), "old data");
        _scm.StartFails = true;
        var r = Installer().Install(new AgentInstallOptions());
        Assert.False(r.Ok);
        Assert.Empty(_scm.Services);
        Assert.Empty(VersionDirs());
        Assert.Equal("old data", File.ReadAllText(Path.Combine(_layout.DataDir, "monitor.db")));
    }

    [Fact]
    public void Install_NoPipeAnswer_RollsBack()
    {
        _helloVersion = null;
        var r = Installer().Install(new AgentInstallOptions());
        Assert.False(r.Ok);
        Assert.Empty(_scm.Services);
        Assert.Contains("stop", _scm.Calls);
    }

    [Fact]
    public void Install_ForeignServiceWithSameName_IsNotTouched()
    {
        _scm.Services["DiskThinnerAgentTest"] = new FakeScm.Svc { BinaryPath = "\"C:\\evil\\agent.exe\"", State = "running" };
        var r = Installer().Install(new AgentInstallOptions());
        Assert.False(r.Ok);
        Assert.Equal("\"C:\\evil\\agent.exe\"", _scm.Services["DiskThinnerAgentTest"].BinaryPath);
        Assert.DoesNotContain("stop", _scm.Calls);
        Assert.False(Directory.Exists(_layout.ProgramRoot));
        Assert.False(Installer().Uninstall(deleteData: true).Ok);
        Assert.True(_scm.Services.ContainsKey("DiskThinnerAgentTest"));
    }

    [Fact]
    public void Install_AclReset_IsReported()
    {
        var r = Installer(d =>
        {
            Directory.CreateDirectory(d);
            return true;
        }).Install(new AgentInstallOptions());
        Assert.True(r.Ok);
        Assert.True(r.AclReset);
        Assert.Contains("权限已重设", r.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_SwitchesVersion_DeletesOld()
    {
        Assert.True(Installer().Install(new AgentInstallOptions()).Ok);
        var oldDir = Assert.Single(VersionDirs());
        WriteSource("v2");
        var r = Installer().Update();
        Assert.True(r.Ok, r.Message);
        var newDir = Assert.Single(VersionDirs());
        Assert.NotEqual(oldDir, newDir);
        Assert.Contains(Path.GetFileName(newDir), _scm.Services["DiskThinnerAgentTest"].BinaryPath, StringComparison.Ordinal);
        Assert.Equal("running", _scm.Services["DiskThinnerAgentTest"].State);

        // 同一个版本再更新一次：什么都不做
        var again = Installer().Update();
        Assert.True(again.Ok);
        Assert.Contains("最新", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_NewVersionFails_RestoresOld()
    {
        Assert.True(Installer().Install(new AgentInstallOptions()).Ok);
        var oldPath = _scm.Services["DiskThinnerAgentTest"].BinaryPath;
        var oldDir = Assert.Single(VersionDirs());
        WriteSource("v2-broken");
        _scm.StartFailsUnless = p => p == oldPath; // 只有旧版本起得来
        var r = Installer().Update();
        Assert.False(r.Ok);
        Assert.Contains("已恢复旧版本", r.Message, StringComparison.Ordinal);
        Assert.Equal(oldPath, _scm.Services["DiskThinnerAgentTest"].BinaryPath);
        Assert.Equal("running", _scm.Services["DiskThinnerAgentTest"].State);
        Assert.Equal(oldDir, Assert.Single(VersionDirs()));
    }

    [Fact]
    public void Update_StopTimesOut_NothingChanges()
    {
        Assert.True(Installer().Install(new AgentInstallOptions()).Ok);
        var oldPath = _scm.Services["DiskThinnerAgentTest"].BinaryPath;
        WriteSource("v2");
        _scm.StopHangs = true;
        var r = Installer().Update();
        Assert.False(r.Ok);
        Assert.Equal(oldPath, _scm.Services["DiskThinnerAgentTest"].BinaryPath);
        Assert.Single(VersionDirs());
    }

    [Fact]
    public void Uninstall_KeepsDataUnlessAsked()
    {
        Assert.True(Installer().Install(new AgentInstallOptions()).Ok);
        File.WriteAllText(Path.Combine(_layout.DataDir, "monitor.db"), "data");
        var r = Installer().Uninstall(deleteData: false);
        Assert.True(r.Ok, r.Message);
        Assert.Empty(_scm.Services);
        Assert.False(Directory.Exists(_layout.ProgramRoot));
        Assert.False(Directory.Exists(Path.GetDirectoryName(_layout.ProgramRoot)), "空的 DiskThinner 目录也删掉");
        Assert.True(File.Exists(Path.Combine(_layout.DataDir, "monitor.db")));

        // 保留的数据：重新安装后还在
        Assert.True(Installer().Install(new AgentInstallOptions()).Ok);
        Assert.Equal("data", File.ReadAllText(Path.Combine(_layout.DataDir, "monitor.db")));
        Assert.True(Installer().Uninstall(deleteData: true).Ok);
        Assert.False(Directory.Exists(_layout.DataDir));
    }

    [Fact]
    public void Uninstall_StopTimesOut_DoesNotDelete()
    {
        Assert.True(Installer().Install(new AgentInstallOptions()).Ok);
        _scm.StopHangs = true;
        var r = Installer().Uninstall(deleteData: true);
        Assert.False(r.Ok);
        Assert.True(_scm.Services.ContainsKey("DiskThinnerAgentTest"));
        Assert.Single(VersionDirs());
        Assert.True(Directory.Exists(_layout.DataDir));
    }

    [Fact]
    public void Layout_RefusesUnexpectedRoots()
    {
        var bad = _layout with { DataDir = Path.Combine(_root, "pd", "Somewhere") };
        Directory.CreateDirectory(bad.DataDir);
        File.WriteAllText(Path.Combine(bad.DataDir, "keep.txt"), "x");
        var installer = new AgentInstaller(bad, _scm) { PrepareDataDir = _ => false, Hello = () => "0.3.0", StopSession = _ => false, Log = _log.Add };
        Assert.False(installer.Uninstall(deleteData: true).Ok);
        Assert.True(File.Exists(Path.Combine(bad.DataDir, "keep.txt")));
        Assert.True(AgentLayout.IsAgentRoot(@"C:\Program Files\DiskThinner\Agent"));
        Assert.False(AgentLayout.IsAgentRoot(@"C:\Program Files"));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\DiskThinner\\Agent\\0.3.0-ab\\DiskThinner.Agent.exe\" --x", "C:\\Program Files\\DiskThinner\\Agent\\0.3.0-ab\\DiskThinner.Agent.exe")]
    [InlineData("C:\\x\\a.exe -k", "C:\\x\\a.exe")]
    [InlineData("", null)]
    public void ExePath_Parses(string binary, string? expected) => Assert.Equal(expected, AgentInstaller.ExePath(binary));

    /// <summary>假的 SCM：记下调用，按开关模拟失败。</summary>
    private sealed class FakeScm : IServiceManager
    {
        public sealed class Svc
        {
            public string BinaryPath { get; set; } = "";
            public string State { get; set; } = "stopped";
        }

        public Dictionary<string, Svc> Services { get; } = [];
        public List<string> Calls { get; } = [];
        public bool FailCreate { get; set; }
        public bool StartFails { get; set; }
        public bool StopHangs { get; set; }
        public Func<string, bool>? StartFailsUnless { get; set; }

        public ServiceInfo Query(string name) =>
            Services.TryGetValue(name, out var s) ? new ServiceInfo(s.State, s.State == "running" ? 1234 : 0, s.BinaryPath, 0, 0) : ServiceInfo.Missing;

        public void Create(ServiceDefinition d)
        {
            Calls.Add("create");
            if (FailCreate)
            {
                throw new Win32Exception(5);
            }

            Services[d.Name] = new Svc { BinaryPath = d.BinaryPath };
        }

        public void SetBinaryPath(string name, string binaryPath)
        {
            Calls.Add("path");
            Services[name].BinaryPath = binaryPath;
        }

        public void Start(string name)
        {
            Calls.Add("start");
            var s = Services[name];
            s.State = StartFails || (StartFailsUnless is { } ok && !ok(s.BinaryPath)) ? "stopped" : "running";
        }

        public bool Stop(string name, TimeSpan wait)
        {
            Calls.Add("stop");
            if (!Services.TryGetValue(name, out var s))
            {
                return true;
            }

            if (StopHangs && s.State == "running")
            {
                return false;
            }

            s.State = "stopped";
            return true;
        }

        public bool WaitRunning(string name, TimeSpan wait) => Services.TryGetValue(name, out var s) && s.State == "running";

        public void Delete(string name)
        {
            Calls.Add("delete");
            if (Services.TryGetValue(name, out var s) && s.State == "running")
            {
                throw new InvalidOperationException("delete while running");
            }

            Services.Remove(name);
        }
    }
}

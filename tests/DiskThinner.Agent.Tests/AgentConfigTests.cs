namespace DiskThinner.Agent.Tests;

/// <summary>服务的配置文件和命令行参数（docs/phase2b-design.md 5.2 节）。</summary>
public sealed class AgentConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DiskThinner-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public AgentConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Missing_UsesDefaults()
    {
        var c = AgentConfig.Load(Path.Combine(_dir, "agent.json"), _log.Add);
        Assert.Equal("precise", c.Mode);
        Assert.Equal(90, c.KeepDays);
        Assert.Equal(AgentConfig.DefaultMaxBytes, c.MaxBytes);
        Assert.Null(c.FlushSeconds);
    }

    [Fact]
    public void SaveLoad_RoundTrips_AndNormalizes()
    {
        var path = Path.Combine(_dir, "agent.json");
        new AgentConfig { Mode = "light", KeepDays = 30, FlushSeconds = 5, Version = "0.3.0" }.Save(path);
        var c = AgentConfig.Load(path, _log.Add);
        Assert.Equal("light", c.Mode);
        Assert.Equal(30, c.KeepDays);
        Assert.Equal(5, c.FlushSeconds);
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, """{"mode":"turbo","keepDays":7,"maxBytes":1,"flushSeconds":1}""");
        var n = AgentConfig.Load(path, _log.Add);
        Assert.Equal("precise", n.Mode);
        Assert.Equal(90, n.KeepDays);
        Assert.Equal(AgentConfig.DefaultMaxBytes, n.MaxBytes);
        Assert.Null(n.FlushSeconds);
    }

    [Fact]
    public void Corrupt_IsRenamedAndDefaultsUsed()
    {
        var path = Path.Combine(_dir, "agent.json");
        File.WriteAllText(path, "{ not json");
        var c = AgentConfig.Load(path, _log.Add);
        Assert.Equal("precise", c.Mode);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(_dir, "agent.json.bad-*"));
        Assert.Contains(_log, l => l.StartsWith("config unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_KeepsLatestHalfWhenFull()
    {
        var log = new AgentLog(Path.Combine(_dir, "logs", "agent.log"));
        var line = new string('x', 1000);
        for (var i = 0; i < 700; i++)
        {
            log.Write(i + " " + line);
        }

        var size = new FileInfo(log.FilePath).Length;
        Assert.True(size <= AgentLog.MaxBytes + 2048, "日志 " + size + " 字节");
        var tail = log.Tail(1);
        Assert.Contains("699 ", Assert.Single(tail));
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--console", "--seconds", "30" }, true)]
    [InlineData(new[] { "--service-name", "DiskThinnerAgentTest", "--pipe", "DiskThinner.AgentTest.abc", "--session", "DiskThinner-AgentTest-1" }, true)]
    [InlineData(new[] { "--session", "DiskThinner-Diag-20261029000000" }, false)]
    [InlineData(new[] { "--session", "NT Kernel Logger" }, false)]
    [InlineData(new[] { "--pipe", @"..\evil" }, false)]
    [InlineData(new[] { "--seconds" }, false)]
    [InlineData(new[] { "--format", "c:" }, false)]
    public void Arguments(string[] args, bool ok)
    {
        var s = AgentSettings.Parse(args, out var error);
        Assert.Equal(ok, s is not null);
        Assert.Equal(ok, error is null);
    }

    [Fact]
    public void Defaults_PointAtProgramData()
    {
        var s = AgentSettings.Parse([], out _)!;
        Assert.Equal("DiskThinnerAgent", s.ServiceName);
        Assert.Equal(@"DiskThinner.Agent.v1", s.PipeName);
        Assert.Equal("DiskThinner-Agent", s.SessionName);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), s.DataDir, StringComparison.OrdinalIgnoreCase);
        Assert.True(s.CheckAcl);
        Assert.Equal(TimeSpan.FromMinutes(1), s.RestartDelay);
    }
}

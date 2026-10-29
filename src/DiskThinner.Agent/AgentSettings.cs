using System.Globalization;
using DiskThinner.Monitor.Agent;

namespace DiskThinner.Agent;

/// <summary>
/// 命令行参数。正式安装的服务不带参数（全用默认值）；提权测试装一个测试服务，用自己的服务名、管道名、会话名和数据目录
/// （docs/phase2b-design.md 8.2 节），互不影响：
///   DiskThinner.Agent.exe [--service-name N] [--data-dir D] [--pipe P] [--session S] [--restart-seconds 60]
///   DiskThinner.Agent.exe --console [--seconds N] [...]   不经过 SCM，前台跑一会儿（开发和测试用，不检查数据目录的权限）
/// </summary>
internal sealed class AgentSettings
{
    public string ServiceName { get; private set; } = AgentProtocol.ServiceName;
    public string DataDir { get; private set; } = DataDirectoryAcl.DefaultPath;
    public string PipeName { get; private set; } = AgentProtocol.PipeName;
    public string SessionName { get; private set; } = AgentProtocol.SessionName;
    public bool Console { get; private set; }
    public int Seconds { get; private set; }
    public TimeSpan RestartDelay { get; private set; } = TimeSpan.FromMinutes(1);

    public string DbPath => Path.Combine(DataDir, "monitor.db");
    public string ConfigPath => Path.Combine(DataDir, "agent.json");
    public string LogPath => Path.Combine(DataDir, "logs", "agent.log");

    /// <summary>服务模式检查数据目录的权限（第 5.9 节）；前台模式不检查。</summary>
    public bool CheckAcl => !Console;

    public static AgentSettings? Parse(string[] args, out string? error)
    {
        var s = new AgentSettings();
        error = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + args[i]);
            try
            {
                switch (args[i])
                {
                    case "--console":
                        s.Console = true;
                        break;
                    case "--seconds":
                        s.Seconds = int.Parse(Next(), CultureInfo.InvariantCulture);
                        break;
                    case "--service-name":
                        s.ServiceName = Name(Next(), 80);
                        break;
                    case "--data-dir":
                        s.DataDir = Path.GetFullPath(Next());
                        break;
                    case "--pipe":
                        s.PipeName = Name(Next(), 200);
                        break;
                    case "--session":
                        s.SessionName = Name(Next(), 200);
                        break;
                    case "--restart-seconds":
                        s.RestartDelay = TimeSpan.FromSeconds(Math.Clamp(int.Parse(Next(), CultureInfo.InvariantCulture), 1, 3600));
                        break;
                    default:
                        throw new ArgumentException("unknown argument " + args[i]);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            {
                error = ex.Message;
                return null;
            }
        }

        // 测试会话名必须以 DiskThinner 开头、不能是 2a 诊断的前缀（2a 的清理只处理 DiskThinner-Diag-）
        if (!s.SessionName.StartsWith("DiskThinner", StringComparison.Ordinal) || s.SessionName.StartsWith("DiskThinner-Diag-", StringComparison.Ordinal))
        {
            error = "bad session name";
            return null;
        }

        return s;
    }

    private static string Name(string value, int max) =>
        value.Length is > 0 and <= 80 * 3 && value.Length <= max && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
            ? value
            : throw new ArgumentException("bad name");
}

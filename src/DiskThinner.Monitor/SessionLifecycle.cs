using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DiskThinner.Core;

namespace DiskThinner.Monitor;

/// <summary>
/// 全局互斥量：同一时间只有一个 DiskThinner 在监控（docs/phase2a-design.md 5.1.1 节）。Mutex 有线程归属，
/// 所以只在自己的专用线程上获取和释放；其他线程（计时器、泵线程、界面）调用 Dispose 只发信号。
/// </summary>
public sealed class MonitorLock : IDisposable
{
    public const string DefaultName = @"Global\DiskThinner.Monitor";

    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _owner;
    private int _disposed;

    private MonitorLock(string name, TaskCompletionSource<bool> acquired)
    {
        _owner = new Thread(() =>
        {
            try
            {
                using var m = new Mutex(false, name);
                bool owned;
                try
                {
                    owned = m.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    owned = true; // 上一个持有者没释放就退出了，也算拿到
                }

                acquired.TrySetResult(owned);
                if (!owned)
                {
                    return;
                }

                _release.Wait();
                m.ReleaseMutex(); // 同一个线程释放
            }
            catch (Exception)
            {
                // 对象存在但打不开（普通权限遇到管理员建的）、同名的别的内核对象等：当作拿不到。专用线程上不能有未处理异常
                acquired.TrySetResult(false);
            }
        })
        { IsBackground = true, Name = "DiskThinner.Monitor.Lock" };
        _owner.Start();
    }

    /// <summary>拿不到（另一个实例在监控）返回 null。名字可以注入，测试用 Local\DiskThinner.Test.&lt;guid&gt;。</summary>
    public static MonitorLock? TryAcquire(string name = DefaultName)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var l = new MonitorLock(name, acquired);
        if (acquired.Task.Wait(TimeSpan.FromSeconds(5)) && acquired.Task.Result)
        {
            return l;
        }

        l.Dispose(); // 超时或拿不到：发释放信号，专用线程万一稍后拿到也会立即释放
        return null;
    }

    /// <summary>别的进程是否开着这个互斥量（第 5.1.2 节）：能打开，或者对象存在但没有权限打开，都算。</summary>
    public static bool ExistsElsewhere(string name = DefaultName)
    {
        try
        {
            if (Mutex.TryOpenExisting(name, out var m))
            {
                m.Dispose();
                return true;
            }

            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>任何线程都可以调用，只生效一次。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _release.Set();
        if (_owner.Join(TimeSpan.FromSeconds(2)))
        {
            _release.Dispose();
        }
    }
}

/// <summary>未结束的会话记录（monitor.db 里 ended_at 为空的行）。</summary>
public sealed record OpenSessionRow(long Id, string SessionName, DateTime LastFlushUtc);

/// <summary>残留判定的结果（第 5.1.2 节）。</summary>
public sealed record LivenessResult(bool Live, IReadOnlyList<string> Residual, IReadOnlyList<OpenSessionRow> Orphans);

public static class SessionLiveness
{
    /// <summary>诊断会话的名字（2b 的服务用另一个前缀）。</summary>
    public static readonly Regex NamePattern = new(@"^DiskThinner-Diag-\d{14}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// 分清"有实例在监控 / 残留会话 / 没结束的会话记录"。<paramref name="activeNames"/> 是活动会话里符合名字的，列不出来时为 null。
    /// 自己正在监控的那一行（<paramref name="selfSession"/>）永远不算孤儿。<paramref name="namePattern"/> 默认是 <see cref="NamePattern"/>，提权测试注入自己的前缀。
    /// </summary>
    public static LivenessResult Classify(bool selfMonitoring, bool lockExistsElsewhere, IReadOnlyCollection<string>? activeNames,
        IReadOnlyList<OpenSessionRow> openRows, string? selfSession = null, Regex? namePattern = null)
    {
        var live = selfMonitoring || lockExistsElsewhere;
        var pattern = namePattern ?? NamePattern;
        var ours = activeNames?.Where(n => pattern.IsMatch(n)).ToList();
        if (!live)
        {
            return new LivenessResult(false, ours ?? [], [.. openRows.Where(r => r.SessionName != selfSession)]);
        }

        if (ours is null)
        {
            return new LivenessResult(true, [], []); // 判断不了就不动
        }

        var active = new HashSet<string>(ours, StringComparer.OrdinalIgnoreCase);
        return new LivenessResult(true, [], [.. openRows.Where(r => r.SessionName != selfSession && !active.Contains(r.SessionName))]);
    }
}

/// <summary>
/// 截止检查（第 5.1.3 节）：墙钟到了计划结束时间，或者经过的时间到了时长，任一满足就停。睡眠唤醒后按墙钟立即停；
/// 系统时间往回调时由经过时间兜底。时钟可注入。
/// </summary>
public sealed class SessionDeadline
{
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan> _elapsed;

    public SessionDeadline(DateTime startUtc, TimeSpan duration, Func<DateTime> utcNow, Func<TimeSpan> elapsed)
    {
        if (duration < MinDuration || duration > MaxDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, L.T("10 秒到 24 小时"));
        }

        StartUtc = startUtc;
        Duration = duration;
        _utcNow = utcNow;
        _elapsed = elapsed;
    }

    public DateTime StartUtc { get; }
    public TimeSpan Duration { get; }
    public DateTime EndUtc => StartUtc + Duration;

    public bool ShouldStop() => _utcNow() >= EndUtc || _elapsed() >= Duration;

    /// <summary>界面上的"剩余时间"：两种时钟取较小的。</summary>
    public TimeSpan Remaining
    {
        get
        {
            var byWall = EndUtc - _utcNow();
            var byElapsed = Duration - _elapsed();
            var r = byWall < byElapsed ? byWall : byElapsed;
            return r < TimeSpan.Zero ? TimeSpan.Zero : r;
        }
    }

    public static string NewSessionName(DateTime utcNow) => "DiskThinner-Diag-" + utcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 兜底计划任务的 XML（第 5.1.4 节，D7）：程序被强制结束时，在计划结束时间加余量后以 SYSTEM 运行 logman 停掉会话；过期后自己删除。
/// </summary>
public static class WatchdogTask
{
    public const string TaskName = @"\DiskThinner\StopDiagnostic";

    /// <summary>余量（第 12.2 节第 13 条）：程序被强制结束时最长 24 小时 1 分钟。</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    public static byte[] BuildXml(string sessionName, DateTimeOffset triggerAt, TimeSpan expireAfter, string logmanPath, string description)
    {
        if (!SessionLiveness.NamePattern.IsMatch(sessionName))
        {
            throw new ArgumentException(L.F("会话名不合法：{0}", sessionName), nameof(sessionName));
        }

        static string Time(DateTimeOffset t) => t.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = encoding, Indent = true }))
        {
            const string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            w.WriteStartDocument();
            w.WriteStartElement("Task", ns);
            w.WriteAttributeString("version", "1.2");
            w.WriteStartElement("RegistrationInfo", ns);
            w.WriteElementString("Description", ns, description);
            w.WriteEndElement();
            w.WriteStartElement("Triggers", ns);
            w.WriteStartElement("TimeTrigger", ns);
            w.WriteElementString("StartBoundary", ns, Time(triggerAt));
            w.WriteElementString("EndBoundary", ns, Time(triggerAt + expireAfter));
            w.WriteElementString("Enabled", ns, "true");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("Principals", ns);
            w.WriteStartElement("Principal", ns);
            w.WriteAttributeString("id", "Author");
            w.WriteElementString("UserId", ns, "S-1-5-18");
            w.WriteElementString("RunLevel", ns, "HighestAvailable");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteStartElement("Settings", ns);
            w.WriteElementString("MultipleInstancesPolicy", ns, "IgnoreNew");
            w.WriteElementString("DisallowStartIfOnBatteries", ns, "false");
            w.WriteElementString("StopIfGoingOnBatteries", ns, "false");
            w.WriteElementString("StartWhenAvailable", ns, "true");
            w.WriteElementString("ExecutionTimeLimit", ns, "PT5M");
            w.WriteElementString("DeleteExpiredTaskAfter", ns, "PT0S");
            w.WriteElementString("Enabled", ns, "true");
            w.WriteEndElement();
            w.WriteStartElement("Actions", ns);
            w.WriteAttributeString("Context", "Author");
            w.WriteStartElement("Exec", ns);
            w.WriteElementString("Command", ns, logmanPath);
            w.WriteElementString("Arguments", ns, $"stop {sessionName} -ets");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return ms.ToArray();
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DiskThinner.Monitor.Etw;

/// <summary>回调里的原始记录（只在回调执行期间有效），给 <see cref="TdhVerifier"/> 用 TDH 按名字取字段。</summary>
public readonly unsafe ref struct EtwRecord
{
    internal EtwRecord(NativeEtw.EventRecord* record) => Record = record;

    internal NativeEtw.EventRecord* Record { get; }
}

/// <summary>每个事件调用一次（在泵线程上）。<paramref name="record"/> 只在这次调用期间有效。</summary>
public delegate void EtwEventCallback(in RawEvent raw, EtwRecord record);

/// <summary>
/// 直接调用 ETW API 的实时会话（docs/phase2b-design.md 5.1 节）：StartTraceW 开会话，EnableTraceEx2 启用提供程序，
/// OpenTraceW + ProcessTrace 在专用的泵线程上消费，回调是 [UnmanagedCallersOnly] 函数指针。不依赖 TraceEvent，可以 Native AOT。
/// 只把启用了的提供程序的事件交给调用方（会话开头 ETW 自己的日志头事件不交）。
/// 停止只执行一次：ControlTrace(STOP) → 等泵线程结束（当前线程就是泵线程时不等）→ CloseTrace；泵线程没有按时结束时不释放回调用到的句柄。
/// </summary>
public sealed unsafe class NativeSession : IDisposable
{
    /// <summary>FILETIME（1601 年起的 100 纳秒）换成 DateTime 的 Ticks（0001 年起）。</summary>
    private const long FileTimeEpochTicks = 504_911_232_000_000_000;

    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _providers = [];
    private readonly EtwEventCallback _onEvent;
    private readonly TaskCompletionSource<uint> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NativeEtw.EventTraceProperties* _query;
    private char* _name;
    private GCHandle _self;
    private ulong _sessionHandle;
    private ulong _traceHandle = NativeEtw.InvalidProcessTraceHandle;
    private Thread? _pump;
    private int _stopping;
    private int _faulted;
    private long _lostSnapshot;

    private NativeSession(string name, EtwEventCallback onEvent)
    {
        Name = name;
        _onEvent = onEvent;
    }

    public string Name { get; }

    /// <summary>回调抛出的异常（会话随后结束消费，泵线程返回）。</summary>
    public Exception? CallbackError { get; private set; }

    /// <summary>ProcessTrace 返回时完成，值是它的返回码。</summary>
    public Task<uint> Completed => _completed.Task;

    /// <summary>当前线程是不是泵线程（在回调里调用 Stop 时不能等泵线程）。</summary>
    public bool OnPumpThread => Thread.CurrentThread == _pump;

    /// <summary>
    /// 开会话并启用提供程序（还不开始消费，调用 <see cref="StartPump"/> 之前到的事件留在会话缓冲区里）。
    /// 会话名已经存在时抛 <see cref="Win32Exception"/>（ERROR_ALREADY_EXISTS），由调用方决定换名字还是先停掉旧的。
    /// </summary>
    public static NativeSession Start(string name, int bufferSizeMb, IReadOnlyList<(Guid Provider, ulong Keywords)> providers, EtwEventCallback onEvent)
    {
        if (string.IsNullOrEmpty(name) || name.Length >= NativeEtw.MaxNameChars)
        {
            throw new ArgumentException("bad session name", nameof(name));
        }

        var s = new NativeSession(name, onEvent);
        var p = NativeEtw.AllocProperties();
        try
        {
            p->WnodeFlags = NativeEtw.WnodeFlagTracedGuid;
            p->WnodeClientContext = 1; // QPC，消费时 ETW 换成 FILETIME
            p->BufferSize = 64; // KB
            p->MinimumBuffers = p->MaximumBuffers = (uint)Math.Clamp(bufferSizeMb, 1, 1024) * 16;
            p->LogFileMode = NativeEtw.EventTraceRealTimeMode;
            p->FlushTimer = 1; // 秒：空闲时事件也在 1 秒内交给消费者
            var rc = NativeEtw.StartTrace(out s._sessionHandle, name, p);
            if (rc != NativeEtw.ErrorSuccess)
            {
                throw new Win32Exception((int)rc);
            }
        }
        finally
        {
            NativeEtw.Free(p);
        }

        try
        {
            foreach (var (provider, keywords) in providers)
            {
                s.Enable(provider, keywords);
            }

            s._query = NativeEtw.AllocProperties();
            s._name = (char*)NativeMemory.AllocZeroed((nuint)(name.Length + 1) * sizeof(char));
            name.AsSpan().CopyTo(new Span<char>(s._name, name.Length));
            s._self = GCHandle.Alloc(s);
            var logfile = new NativeEtw.EventTraceLogfile
            {
                LoggerName = s._name,
                ProcessTraceMode = NativeEtw.ProcessTraceModeRealTime | NativeEtw.ProcessTraceModeEventRecord,
                EventRecordCallback = &OnRecord,
                Context = (void*)GCHandle.ToIntPtr(s._self),
            };
            s._traceHandle = NativeEtw.OpenTrace(&logfile);
            if (s._traceHandle == NativeEtw.InvalidProcessTraceHandle)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError() is var e and not 0 ? e : (int)NativeEtw.ErrorAccessDenied);
            }
        }
        catch
        {
            // 会话已经开了：停掉它再把异常抛给调用方
            var stop = NativeEtw.AllocProperties();
            NativeEtw.ControlTrace(s._sessionHandle, null, stop, NativeEtw.EventTraceControlStop);
            NativeEtw.Free(stop);
            s.Release(pumpExited: true);
            throw;
        }

        return s;
    }

    /// <summary>（重新）启用一个提供程序：降级时用轻量的关键字再启用一次 Kernel-File（2a 的 V17：立即生效）。</summary>
    public void Enable(Guid provider, ulong keywords)
    {
        var rc = NativeEtw.EnableTraceEx2(_sessionHandle, &provider, NativeEtw.EventControlCodeEnableProvider, NativeEtw.TraceLevelVerbose, keywords, 0, 0, null);
        if (rc != NativeEtw.ErrorSuccess)
        {
            throw new Win32Exception((int)rc);
        }

        lock (_gate)
        {
            _providers.Add(provider);
        }
    }

    /// <summary>在专用的后台线程上开始消费。</summary>
    public void StartPump(string threadName)
    {
        _pump = new Thread(Pump) { IsBackground = true, Name = threadName };
        _pump.Start();
    }

    private void Pump()
    {
        uint rc;
        var handle = _traceHandle;
        try
        {
            rc = NativeEtw.ProcessTrace(&handle, 1, null, null);
        }
        catch (Exception ex)
        {
            CallbackError ??= ex;
            rc = NativeEtw.ErrorCancelled;
        }

        _completed.TrySetResult(rc);
    }

    [UnmanagedCallersOnly]
    private static void OnRecord(NativeEtw.EventRecord* r)
    {
        // 异常不能穿过非托管回调：在这里接住，记下来，停止消费
        try
        {
            if (r->UserContext is null || GCHandle.FromIntPtr((nint)r->UserContext).Target is not NativeSession s)
            {
                return;
            }

            s.Dispatch(r);
        }
        catch (Exception ex)
        {
            if (r->UserContext is not null && GCHandle.FromIntPtr((nint)r->UserContext).Target is NativeSession s)
            {
                s.Fault(ex);
            }
        }
    }

    private void Dispatch(NativeEtw.EventRecord* r)
    {
        if (Volatile.Read(ref _faulted) != 0)
        {
            return;
        }

        bool known;
        lock (_gate)
        {
            known = _providers.Contains(r->ProviderId);
        }

        if (!known)
        {
            return; // ETW 自己的日志头事件等
        }

        var pointerSize = (r->Flags & NativeEtw.EventHeaderFlag32BitHeader) != 0 ? 4 : (r->Flags & NativeEtw.EventHeaderFlag64BitHeader) != 0 ? 8 : IntPtr.Size;
        var raw = new RawEvent(r->ProviderId, r->Id, r->Version, (int)r->ProcessId, (int)r->ThreadId, r->TimeStamp + FileTimeEpochTicks, pointerSize,
            new ReadOnlySpan<byte>(r->UserData, r->UserDataLength));
        _onEvent(raw, new EtwRecord(r));
    }

    private void Fault(Exception ex)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        CallbackError = ex;

        // 在回调外面结束消费：CloseTrace 让 ProcessTrace 处理完当前缓冲区后返回
        var handle = _traceHandle;
        ThreadPool.UnsafeQueueUserWorkItem(_ => NativeEtw.CloseTrace(handle), null);
    }

    /// <summary>
    /// 内核侧丢的事件数（ControlTrace QUERY 的 EventsLost）。停止开始之后、查询失败时返回最后一次查到的值，不抛异常（2a 的 0x80071069）。
    /// </summary>
    public long EventsLost
    {
        get
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                return Interlocked.Read(ref _lostSnapshot);
            }

            lock (_gate)
            {
                if (_query is null)
                {
                    return Interlocked.Read(ref _lostSnapshot);
                }

                var rc = NativeEtw.ControlTrace(_sessionHandle, null, _query, NativeEtw.EventTraceControlQuery);
                if (rc == NativeEtw.ErrorSuccess)
                {
                    Interlocked.Exchange(ref _lostSnapshot, _query->EventsLost);
                }
            }

            return Interlocked.Read(ref _lostSnapshot);
        }
    }

    /// <summary>
    /// 停止会话并结束消费。只执行一次；返回 false 表示泵线程没有在 <paramref name="wait"/> 内结束（这时回调用到的句柄不释放，避免泵线程访问已释放的内存）。
    /// </summary>
    public bool Stop(TimeSpan wait)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (_query is not null)
            {
                NativeEtw.ControlTrace(_sessionHandle, null, _query, NativeEtw.EventTraceControlStop);
            }
        }

        var exited = _pump is null || OnPumpThread || _pump.Join(wait);
        if (_traceHandle != NativeEtw.InvalidProcessTraceHandle)
        {
            NativeEtw.CloseTrace(_traceHandle);
        }

        if (!exited && _pump is not null)
        {
            exited = _pump.Join(TimeSpan.FromSeconds(2));
        }

        Release(pumpExited: exited && !OnPumpThread);
        return exited;
    }

    private void Release(bool pumpExited)
    {
        lock (_gate)
        {
            if (_query is not null)
            {
                NativeEtw.Free(_query);
                _query = null;
            }
        }

        if (!pumpExited)
        {
            return;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }

        if (_name is not null)
        {
            NativeMemory.Free(_name);
            _name = null;
        }
    }

    public void Dispose() => Stop(TimeSpan.FromSeconds(5));

    /// <summary>只开会话、启用一个提供程序，不消费也不停（提权测试用来模拟进程被强制结束后留下的会话）。</summary>
    internal static void StartUnconsumed(string name, Guid provider, ulong keywords)
    {
        var p = NativeEtw.AllocProperties();
        try
        {
            p->WnodeFlags = NativeEtw.WnodeFlagTracedGuid;
            p->WnodeClientContext = 1;
            p->BufferSize = 64;
            p->MinimumBuffers = p->MaximumBuffers = 16;
            p->LogFileMode = NativeEtw.EventTraceRealTimeMode;
            var rc = NativeEtw.StartTrace(out var handle, name, p);
            if (rc != NativeEtw.ErrorSuccess)
            {
                throw new Win32Exception((int)rc);
            }

            rc = NativeEtw.EnableTraceEx2(handle, &provider, NativeEtw.EventControlCodeEnableProvider, NativeEtw.TraceLevelVerbose, keywords, 0, 0, null);
            if (rc != NativeEtw.ErrorSuccess)
            {
                NativeEtw.ControlTrace(handle, null, p, NativeEtw.EventTraceControlStop);
                throw new Win32Exception((int)rc);
            }
        }
        finally
        {
            NativeEtw.Free(p);
        }
    }

    /// <summary>按名字停掉一个会话（任何进程开的，需要管理员）。会话不存在时返回 false。</summary>
    public static bool StopByName(string name) => NativeEtw.Stop(name) == NativeEtw.ErrorSuccess;

    /// <summary>活动会话的名字；列不出来时为 null。</summary>
    public static IReadOnlyList<string>? ActiveSessionNames() => NativeEtw.ActiveSessionNames();
}

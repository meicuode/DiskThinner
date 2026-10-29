using System.Runtime.InteropServices;

namespace DiskThinner.Monitor.Etw;

/// <summary>
/// ETW 的结构和函数（advapi32、tdh），给 <see cref="NativeSession"/> 用（docs/phase2b-design.md 5.1 节）。只支持 64 位进程（发布包只有 win-x64）。
/// 结构的大小和字段偏移按 Windows SDK 10.0.26100 的 evntrace.h / evntcons.h / tdh.h 在 x64 上用 offsetof 打印出来的值写死，
/// NativeEtwLayoutTests 逐个核对。
/// </summary>
internal static unsafe partial class NativeEtw
{
    public const uint WnodeFlagTracedGuid = 0x00020000;
    public const uint EventTraceRealTimeMode = 0x00000100;
    public const uint ProcessTraceModeRealTime = 0x00000100;
    public const uint ProcessTraceModeEventRecord = 0x10000000;
    public const ushort EventHeaderFlag32BitHeader = 0x0020;
    public const ushort EventHeaderFlag64BitHeader = 0x0040;
    public const uint EventControlCodeEnableProvider = 1;
    public const uint EventTraceControlQuery = 0;
    public const uint EventTraceControlStop = 1;
    public const byte TraceLevelVerbose = 5;

    public const uint ErrorSuccess = 0;
    public const uint ErrorAccessDenied = 5;
    public const uint ErrorAlreadyExists = 183;
    public const uint ErrorMoreData = 234;
    public const uint ErrorCancelled = 1223;
    public const uint ErrorNotFound = 1168;
    public const uint ErrorWmiInstanceNotFound = 4201;
    public const ulong InvalidProcessTraceHandle = ulong.MaxValue;

    /// <summary>会话名（含结尾的 0）最多这么多个字符；ETW 的上限是 1024。</summary>
    public const int MaxNameChars = 1024;

    /// <summary>EVENT_TRACE_PROPERTIES（120 字节），后面紧跟会话名和日志文件名的缓冲区。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    public struct EventTraceProperties
    {
        // WNODE_HEADER（48 字节）
        [FieldOffset(0)] public uint WnodeBufferSize;
        [FieldOffset(24)] public Guid WnodeGuid;
        [FieldOffset(40)] public uint WnodeClientContext;
        [FieldOffset(44)] public uint WnodeFlags;

        [FieldOffset(48)] public uint BufferSize;
        [FieldOffset(52)] public uint MinimumBuffers;
        [FieldOffset(56)] public uint MaximumBuffers;
        [FieldOffset(64)] public uint LogFileMode;
        [FieldOffset(68)] public uint FlushTimer;
        [FieldOffset(80)] public uint NumberOfBuffers;
        [FieldOffset(88)] public uint EventsLost;
        [FieldOffset(92)] public uint BuffersWritten;
        [FieldOffset(100)] public uint RealTimeBuffersLost;
        [FieldOffset(112)] public uint LogFileNameOffset;
        [FieldOffset(116)] public uint LoggerNameOffset;
    }

    /// <summary>EVENT_TRACE_LOGFILEW（448 字节），只用到这几个字段。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    public struct EventTraceLogfile
    {
        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(424)] public delegate* unmanaged<EventRecord*, void> EventRecordCallback;
        [FieldOffset(440)] public void* Context;
    }

    /// <summary>EVENT_RECORD（112 字节，EVENT_HEADER 在开头的 80 字节里）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct EventRecord
    {
        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort Id;
        [FieldOffset(42)] public byte Version;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public void* UserData;
        [FieldOffset(104)] public void* UserContext;
    }

    /// <summary>PROPERTY_DATA_DESCRIPTOR（16 字节）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PropertyDataDescriptor
    {
        [FieldOffset(0)] public ulong PropertyName;
        [FieldOffset(8)] public uint ArrayIndex;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "StartTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint StartTrace(out ulong handle, string name, EventTraceProperties* properties);

    [LibraryImport("advapi32.dll", EntryPoint = "ControlTraceW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint ControlTrace(ulong handle, string? name, EventTraceProperties* properties, uint controlCode);

    [LibraryImport("advapi32.dll")]
    public static partial uint EnableTraceEx2(ulong handle, Guid* provider, uint controlCode, byte level, ulong matchAny, ulong matchAll, uint timeout, void* parameters);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)]
    public static partial ulong OpenTrace(EventTraceLogfile* logfile);

    [LibraryImport("advapi32.dll")]
    public static partial uint ProcessTrace(ulong* handles, uint count, long* start, long* end);

    [LibraryImport("advapi32.dll")]
    public static partial uint CloseTrace(ulong handle);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryAllTracesW")]
    public static partial uint QueryAllTraces(EventTraceProperties** properties, uint count, uint* loggers);

    [LibraryImport("tdh.dll")]
    public static partial uint TdhGetPropertySize(EventRecord* record, uint contextCount, void* context, uint descriptorCount, PropertyDataDescriptor* descriptors, uint* size);

    [LibraryImport("tdh.dll")]
    public static partial uint TdhGetProperty(EventRecord* record, uint contextCount, void* context, uint descriptorCount, PropertyDataDescriptor* descriptors, uint bufferSize, byte* buffer);

    /// <summary>EVENT_TRACE_PROPERTIES 加上两个名字缓冲区，按 8 字节对齐分配在非托管内存里。用完 <see cref="Free"/>。</summary>
    public static EventTraceProperties* AllocProperties()
    {
        var size = (uint)(sizeof(EventTraceProperties) + 2 * MaxNameChars * sizeof(char));
        var p = (EventTraceProperties*)NativeMemory.AllocZeroed(size);
        p->WnodeBufferSize = size;
        p->LoggerNameOffset = (uint)sizeof(EventTraceProperties);
        p->LogFileNameOffset = (uint)(sizeof(EventTraceProperties) + MaxNameChars * sizeof(char));
        return p;
    }

    public static void Free(EventTraceProperties* p) => NativeMemory.Free(p);

    /// <summary>属性后面的会话名（QueryAllTraces 填进去的）。</summary>
    public static string LoggerName(EventTraceProperties* p) =>
        p->LoggerNameOffset == 0 ? "" : new string((char*)((byte*)p + p->LoggerNameOffset));

    /// <summary>按名字停一个会话（任何进程开的都可以，需要管理员）。会话不存在时返回 ERROR_WMI_INSTANCE_NOT_FOUND。</summary>
    public static uint Stop(string name)
    {
        var p = AllocProperties();
        try
        {
            return ControlTrace(0, name, p, EventTraceControlStop);
        }
        finally
        {
            Free(p);
        }
    }

    /// <summary>
    /// 活动会话的名字（QueryAllTracesW，普通权限也能列出，2a 的 V21）。缓冲区不够时加倍重试。列不出来时返回 null。
    /// </summary>
    public static List<string>? ActiveSessionNames()
    {
        for (var count = 64u; count <= 1024; count *= 2)
        {
            var array = new EventTraceProperties*[count];
            try
            {
                for (var i = 0; i < count; i++)
                {
                    array[i] = AllocProperties();
                }

                uint loggers = 0;
                uint rc;
                fixed (EventTraceProperties** a = array)
                {
                    rc = QueryAllTraces(a, count, &loggers);
                }

                if (rc == ErrorMoreData)
                {
                    continue;
                }

                if (rc != ErrorSuccess)
                {
                    return null;
                }

                var names = new List<string>((int)loggers);
                for (var i = 0; i < loggers && i < count; i++)
                {
                    names.Add(LoggerName(array[i]));
                }

                return names;
            }
            finally
            {
                foreach (var p in array)
                {
                    if (p is not null)
                    {
                        Free(p);
                    }
                }
            }
        }

        return null;
    }
}

using System.Runtime.InteropServices;
using DiskThinner.Monitor.Etw;

namespace DiskThinner.Monitor.Tests;

/// <summary>
/// 自己声明的 ETW 结构（docs/phase2b-design.md 5.1 节、8.1 节）：大小和字段偏移必须和 Windows SDK 一致，否则 ETW 会写坏内存或读错字段。
/// 期望值是 2026-10-29 在 x64 上用 Windows SDK 10.0.26100 的 evntrace.h / evntcons.h / tdh.h 编译一个小程序、用 sizeof / offsetof 打印出来的。
/// </summary>
public unsafe class NativeEtwLayoutTests
{
    [Fact]
    public void StructSizesMatchTheSdk()
    {
        Assert.Equal(8, IntPtr.Size); // 只支持 64 位进程
        Assert.Equal(120, sizeof(NativeEtw.EventTraceProperties));
        Assert.Equal(448, sizeof(NativeEtw.EventTraceLogfile));
        Assert.Equal(112, sizeof(NativeEtw.EventRecord));
        Assert.Equal(16, sizeof(NativeEtw.PropertyDataDescriptor));
    }

    [Theory]
    [InlineData(nameof(NativeEtw.EventTraceProperties.WnodeBufferSize), 0)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.WnodeGuid), 24)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.WnodeClientContext), 40)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.WnodeFlags), 44)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.BufferSize), 48)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.MinimumBuffers), 52)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.MaximumBuffers), 56)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.LogFileMode), 64)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.FlushTimer), 68)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.NumberOfBuffers), 80)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.EventsLost), 88)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.BuffersWritten), 92)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.RealTimeBuffersLost), 100)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.LogFileNameOffset), 112)]
    [InlineData(nameof(NativeEtw.EventTraceProperties.LoggerNameOffset), 116)]
    public void EventTracePropertiesOffsets(string field, int offset) =>
        Assert.Equal(offset, (int)Marshal.OffsetOf<NativeEtw.EventTraceProperties>(field));

    [Theory]
    [InlineData(nameof(NativeEtw.EventTraceLogfile.LoggerName), 8)]
    [InlineData(nameof(NativeEtw.EventTraceLogfile.ProcessTraceMode), 28)]
    [InlineData(nameof(NativeEtw.EventTraceLogfile.EventRecordCallback), 424)]
    [InlineData(nameof(NativeEtw.EventTraceLogfile.Context), 440)]
    public void EventTraceLogfileOffsets(string field, int offset) =>
        Assert.Equal(offset, (int)Marshal.OffsetOf<NativeEtw.EventTraceLogfile>(field));

    [Theory]
    [InlineData(nameof(NativeEtw.EventRecord.Flags), 4)]
    [InlineData(nameof(NativeEtw.EventRecord.ThreadId), 8)]
    [InlineData(nameof(NativeEtw.EventRecord.ProcessId), 12)]
    [InlineData(nameof(NativeEtw.EventRecord.TimeStamp), 16)]
    [InlineData(nameof(NativeEtw.EventRecord.ProviderId), 24)]
    [InlineData(nameof(NativeEtw.EventRecord.Id), 40)] // EVENT_HEADER.EventDescriptor 在 40，Id 是它的第 0 个字节
    [InlineData(nameof(NativeEtw.EventRecord.Version), 42)]
    [InlineData(nameof(NativeEtw.EventRecord.UserDataLength), 86)]
    [InlineData(nameof(NativeEtw.EventRecord.UserData), 96)]
    [InlineData(nameof(NativeEtw.EventRecord.UserContext), 104)]
    public void EventRecordOffsets(string field, int offset) =>
        Assert.Equal(offset, (int)Marshal.OffsetOf<NativeEtw.EventRecord>(field));

    [Fact]
    public void ConstantsMatchTheSdk()
    {
        Assert.Equal(0x20000u, NativeEtw.WnodeFlagTracedGuid);
        Assert.Equal(0x100u, NativeEtw.EventTraceRealTimeMode);
        Assert.Equal(0x100u, NativeEtw.ProcessTraceModeRealTime);
        Assert.Equal(0x10000000u, NativeEtw.ProcessTraceModeEventRecord);
        Assert.Equal(0x20, NativeEtw.EventHeaderFlag32BitHeader);
        Assert.Equal(0x40, NativeEtw.EventHeaderFlag64BitHeader);
        Assert.Equal(1u, NativeEtw.EventControlCodeEnableProvider);
        Assert.Equal(0u, NativeEtw.EventTraceControlQuery);
        Assert.Equal(1u, NativeEtw.EventTraceControlStop);
    }

    [Fact]
    public void PropertiesBufferHasRoomForBothNames()
    {
        var p = NativeEtw.AllocProperties();
        try
        {
            Assert.Equal(120u, p->LoggerNameOffset);
            Assert.Equal(120u + NativeEtw.MaxNameChars * 2, p->LogFileNameOffset);
            Assert.Equal(120u + NativeEtw.MaxNameChars * 4, p->WnodeBufferSize);
            Assert.Equal("", NativeEtw.LoggerName(p));
        }
        finally
        {
            NativeEtw.Free(p);
        }
    }

    /// <summary>普通权限也能列出会话（2a 的 V21），CI 上也能跑。</summary>
    [Fact]
    public void ActiveSessionNamesCanBeListed()
    {
        var names = NativeSession.ActiveSessionNames();
        Assert.NotNull(names);
        Assert.All(names, n => Assert.False(string.IsNullOrEmpty(n)));
    }
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskThinner.Scanner.Native;

internal static partial class Win32
{
    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;
    public const uint OpenExisting = 3;
    public const uint FsctlGetNtfsVolumeData = 0x00090064;

    [StructLayout(LayoutKind.Sequential)]
    public struct NtfsVolumeData
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, void* inBuffer, int inBufferSize,
        void* outBuffer, int outBufferSize, out int bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool ReadFile(
        SafeFileHandle file, byte* buffer, int numberOfBytesToRead, out int numberOfBytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directoryName, out long freeBytesAvailable, out long totalBytes, out long totalFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(string rootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint numberOfFreeClusters, out uint totalNumberOfClusters);

    /// <summary>从卷句柄的指定偏移读取。偏移和长度必须是扇区大小的整数倍。</summary>
    public static unsafe void ReadAt(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        var overlapped = new NativeOverlapped
        {
            OffsetLow = unchecked((int)(offset & 0xFFFFFFFF)),
            OffsetHigh = unchecked((int)(offset >> 32)),
        };

        fixed (byte* p = buffer)
        {
            if (!ReadFile(handle, p, buffer.Length, out var read, &overlapped))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), $"读取卷失败（偏移 {offset}）");
            }

            if (read != buffer.Length)
            {
                throw new IOException($"读取卷时数据不完整：期望 {buffer.Length} 字节，实际 {read} 字节（偏移 {offset}）");
            }
        }
    }
}

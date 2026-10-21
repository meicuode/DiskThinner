using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskThinner.Cleanup.Native;

/// <summary>清理用到的 Win32 接口。路径一律先用 <see cref="Extended"/> 加上 \\?\ 前缀，不受 260 字符限制。</summary>
internal static unsafe partial class Win32
{
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;
    public const int ErrorSharingViolation = 32;
    public const int ErrorLockViolation = 33;
    public const int ErrorNotSupported = 50;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorDirNotEmpty = 145;
    public const int ErrorMoreData = 234;
    public const int ErrorInvalidFunction = 1;

    public const uint FileAttributeReadOnly = 0x1;
    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeNormal = 0x80;
    public const uint FileAttributeReparsePoint = 0x400;
    public const uint InvalidFileAttributes = 0xFFFFFFFF;

    /// <summary>junction（目录挂载点）。</summary>
    public const uint ReparseTagMountPoint = 0xA0000003;

    /// <summary>符号链接。</summary>
    public const uint ReparseTagSymlink = 0xA000000C;

    public const uint AccessDelete = 0x00010000;
    public const uint AccessSynchronize = 0x00100000;
    public const uint FileListDirectory = 0x1;
    public const uint FileReadAttributes = 0x80;
    public const uint FileWriteAttributes = 0x100;
    public const uint ShareRead = 1;
    public const uint ShareWrite = 2;
    public const uint ShareDelete = 4;
    public const uint OpenExisting = 3;
    public const uint FlagBackupSemantics = 0x02000000;
    public const uint FlagOpenReparsePoint = 0x00200000;

    public const int FileBasicInfoClass = 0;
    public const int FileStandardInfoClass = 1;
    public const int FileDispositionInfoClass = 4;
    public const int FileAttributeTagInfoClass = 9;
    public const int FileDispositionInfoExClass = 21;
    public const uint DispositionDelete = 0x1;
    public const uint DispositionPosixSemantics = 0x2;
    public const uint DispositionIgnoreReadOnly = 0x10;

    private const int FindExInfoBasic = 1;
    private const uint FindFirstExLargeFetch = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FindData
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh;
        public uint AccessLow, AccessHigh;
        public uint WriteLow, WriteHigh;
        public uint SizeHigh, SizeLow;

        /// <summary>带重解析属性时是重解析标记。</summary>
        public uint Reserved0;

        public uint Reserved1;
        public fixed char FileName[260];
        public fixed char AlternateFileName[14];

        public readonly long Size => ((long)SizeHigh << 32) | SizeLow;

        public readonly long LastWriteUtc => ((long)WriteHigh << 32) | WriteLow;

        public readonly long CreationUtc => ((long)CreationHigh << 32) | CreationLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RmUniqueProcess
    {
        public uint ProcessId;
        public uint StartLow;
        public uint StartHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        public fixed char AppName[256];
        public fixed char ServiceShortName[64];
        public int ApplicationType;
        public uint AppStatus;
        public uint TsSessionId;
        public int Restartable;
    }

    /// <summary>加上 \\?\ 前缀（UNC 路径用 \\?\UNC\），绕开 MAX_PATH 并且不做路径规范化。</summary>
    public static string Extended(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    public static nint FindFirst(string extendedPattern, out FindData data) =>
        FindFirstFileEx(extendedPattern, FindExInfoBasic, out data, 0, 0, FindFirstExLargeFetch);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindFirstFileEx(string fileName, int infoLevel, out FindData data, int searchOp, nint filter, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNext(nint handle, out FindData data);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint securityAttributes, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetFileAttributes(string fileName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directory, out long freeBytesAvailable, out long totalBytes, out long totalFreeBytes);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmStartSession(out uint session, uint flags, char* sessionKey);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmRegisterResources(uint session, uint fileCount, nint* files, uint appCount, RmUniqueProcess* apps, uint serviceCount, nint* services);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmGetList(uint session, out uint needed, ref uint count, RmProcessInfo* infos, out uint rebootReasons);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmShutdown(uint session, uint flags, nint callback);

    [LibraryImport("rstrtmgr.dll")]
    public static partial int RmEndSession(uint session);

    public const uint WtsCurrentSession = 0xFFFFFFFF;
    public const int WtsUserName = 5;
    public const int WtsDomainName = 7;

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WtsQuerySessionInformation(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    public static partial void WtsFreeMemory(nint memory);
}

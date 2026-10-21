using System.Runtime.InteropServices;

namespace DiskThinner.Inventory.Native;

internal static unsafe partial class Win32
{
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* exeName, ref int size);

    /// <summary>解析 @{包名?ms-resource://...} 这样的间接字符串。</summary>
    [LibraryImport("shlwapi.dll", EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHLoadIndirectString(string source, char* outBuf, int outBufSize, nint reserved);

    // ---------- Authenticode ----------

    public const uint WtdUiNone = 2;
    public const uint WtdRevokeNone = 0;
    public const uint WtdChoiceFile = 1;
    public const uint WtdStateActionVerify = 1;
    public const uint WtdStateActionClose = 2;
    public const uint WtdRevocationCheckNone = 0x10;
    public const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    public const uint CertNameAttrType = 3;
    public const uint CertNameSimpleDisplayType = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinTrustFileInfo
    {
        public uint CbStruct;
        public char* FilePath;
        public nint File;
        public Guid* KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinTrustData
    {
        public uint CbStruct;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public WinTrustFileInfo* File;
        public uint StateAction;
        public nint StateData;
        public char* UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    /// <summary>CRYPT_PROVIDER_CERT 的开头部分：只用到证书指针。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CryptProviderCert
    {
        public uint CbStruct;
        public nint Cert;
    }

    [LibraryImport("wintrust.dll")]
    public static partial int WinVerifyTrust(nint hwnd, Guid* actionId, WinTrustData* data);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperGetProvSignerFromChain(nint provData, uint signerIndex, int counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    public static partial CryptProviderCert* WTHelperGetProvCertFromChain(nint signer, uint certIndex);

    [LibraryImport("crypt32.dll", EntryPoint = "CertGetNameStringW")]
    public static partial uint CertGetNameString(nint certContext, uint type, uint flags, nint typePara, char* nameString, uint nameLength);
}

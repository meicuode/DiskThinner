using System.Collections.Concurrent;
using System.Diagnostics;
using DiskThinner.Inventory.Native;

namespace DiskThinner.Inventory;

/// <summary>程序文件自己声明的身份（版本信息里的产品名、公司名）。</summary>
public sealed record ExeIdentity(string Path, string? Product, string? Company, string? Description, string? Version)
{
    /// <summary>用来显示的名字：产品名，没有时用文件说明。</summary>
    public string? DisplayName => Product ?? Description;
}

/// <summary>Authenticode 签名的检查结果。</summary>
public sealed record SignatureInfo(string? Signer, bool Valid, int Status)
{
    public static readonly SignatureInfo None = new(null, false, unchecked((int)0x800B0100));
}

/// <summary>读取 exe 的版本信息和签名。结果按路径缓存（同一次运行里文件不会变）。</summary>
public static class ExeIdentityReader
{
    private static readonly ConcurrentDictionary<string, ExeIdentity?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SignatureInfo> Signatures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取版本信息。文件不存在、没有版本信息或读不了时返回 null。</summary>
    public static ExeIdentity? Read(string path) => Cache.GetOrAdd(path, static p =>
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(p);
            var product = Clean(info.ProductName);
            var company = Clean(info.CompanyName);
            var description = Clean(info.FileDescription);
            return product is null && company is null && description is null
                ? null
                : new ExeIdentity(p, product, AppNames.CleanPublisher(company), description, Clean(info.ProductVersion));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    });

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// 校验文件里嵌入的 Authenticode 签名，返回签名者。不联网检查吊销（只用本机缓存），所以不会卡住；
    /// 但要读完整个文件算哈希，大文件需要几百毫秒，应在后台线程调用。系统文件多用目录签名，这里会返回"没有签名"。
    /// </summary>
    public static SignatureInfo ReadSignature(string path) => Signatures.GetOrAdd(path, static p => VerifySignature(p));

    private static unsafe SignatureInfo VerifySignature(string path)
    {
        if (!File.Exists(path))
        {
            return SignatureInfo.None;
        }

        var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        fixed (char* p = path)
        {
            var file = new Win32.WinTrustFileInfo { CbStruct = (uint)sizeof(Win32.WinTrustFileInfo), FilePath = p };
            var data = new Win32.WinTrustData
            {
                CbStruct = (uint)sizeof(Win32.WinTrustData),
                UiChoice = Win32.WtdUiNone,
                RevocationChecks = Win32.WtdRevokeNone,
                UnionChoice = Win32.WtdChoiceFile,
                File = &file,
                StateAction = Win32.WtdStateActionVerify,
                ProvFlags = Win32.WtdRevocationCheckNone | Win32.WtdCacheOnlyUrlRetrieval,
            };

            var status = Win32.WinVerifyTrust(-1, &action, &data);
            string? signer = null;
            try
            {
                var prov = data.StateData == 0 ? 0 : Win32.WTHelperProvDataFromStateData(data.StateData);
                var sgnr = prov == 0 ? 0 : Win32.WTHelperGetProvSignerFromChain(prov, 0, 0, 0);
                var cert = sgnr == 0 ? null : Win32.WTHelperGetProvCertFromChain(sgnr, 0);
                if (cert is not null && cert->Cert != 0)
                {
                    // 优先取证书主题里的组织（O=Microsoft Corporation）；没有时取常用名（CN=.NET 这类产品线名称不如组织好认）
                    var buffer = stackalloc char[256];
                    var oid = "2.5.4.10\0"u8;
                    uint len;
                    fixed (byte* o = oid)
                    {
                        len = Win32.CertGetNameString(cert->Cert, Win32.CertNameAttrType, 0, (nint)o, buffer, 256);
                    }

                    if (len <= 1)
                    {
                        len = Win32.CertGetNameString(cert->Cert, Win32.CertNameSimpleDisplayType, 0, 0, buffer, 256);
                    }

                    if (len > 1)
                    {
                        signer = new string(buffer, 0, (int)len - 1);
                    }
                }
            }
            finally
            {
                data.StateAction = Win32.WtdStateActionClose;
                Win32.WinVerifyTrust(-1, &action, &data);
            }

            return new SignatureInfo(signer, status == 0, status);
        }
    }
}

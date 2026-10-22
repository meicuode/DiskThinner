using System.ComponentModel;
using System.Security.Principal;
using DiskThinner.Core;
using DiskThinner.Scanner.Mft;
using DiskThinner.Scanner.Walk;

namespace DiskThinner.Scanner;

public enum ScanPreference
{
    /// <summary>能用 MFT 就用 MFT（管理员 + NTFS + 整个盘），否则遍历。</summary>
    Auto,
    Walk,
}

/// <summary>扫描入口：自动选择 MFT 或遍历扫描。MFT 失败时自动退回遍历，并在结果里说明原因。</summary>
public static class VolumeScanner
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>不扫描，只判断会用哪种方式，以及不能用 MFT 的原因（界面提前提示用）。</summary>
    public static (ScanMode Mode, string? Reason) Plan(string root, ScanPreference preference = ScanPreference.Auto)
    {
        if (preference == ScanPreference.Walk)
        {
            return (ScanMode.Walk, null);
        }

        var full = Path.GetFullPath(root);
        var driveRoot = Path.GetPathRoot(full);
        if (driveRoot is null || !string.Equals(full.TrimEnd('\\'), driveRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return (ScanMode.Walk, L.T("扫描的是文件夹而不是整个盘"));
        }

        if (!IsElevated())
        {
            return (ScanMode.Walk, L.T("当前不是管理员权限"));
        }

        try
        {
            var format = new DriveInfo(driveRoot).DriveFormat;
            if (!string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return (ScanMode.Walk, L.F("文件系统是 {0}，不是 NTFS", format));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ScanMode.Walk, L.F("无法读取磁盘信息：{0}", ex.Message));
        }

        return (ScanMode.Mft, null);
    }

    public static ScanResult Scan(string root, ScanPreference preference = ScanPreference.Auto,
        IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var (mode, reason) = Plan(root, preference);
        if (mode == ScanMode.Mft)
        {
            try
            {
                return MftScanner.Scan(Path.GetPathRoot(Path.GetFullPath(root))!.TrimEnd('\\'), progress, cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or NotSupportedException or InvalidDataException or IOException or Win32Exception)
            {
                reason = L.F("MFT 读取失败，已改用遍历扫描：{0}", ex.Message);
            }
        }

        var result = WalkScanner.Scan(root, progress: progress, cancellationToken: cancellationToken);
        result.Stats.MftUnavailableReason = reason;
        return result;
    }
}

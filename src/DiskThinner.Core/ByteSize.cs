using System.Globalization;

namespace DiskThinner.Core;

/// <summary>字节数的显示格式，与原型一致：小于 10 保留两位小数（4.37 GB），否则保留一位（16.9 GB、186.8 GB）。</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "\u2212" + Format(bytes == long.MinValue ? long.MaxValue : -bytes);
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
        return text + " " + Units[unit];
    }

    /// <summary>带正负号的变化量，例如 "+1.20 GB"、"−1.90 GB"、"0"。</summary>
    public static string FormatDelta(long delta) => delta switch
    {
        0 => "0",
        > 0 => "+" + Format(delta),
        _ => Format(delta),
    };
}

using System.Globalization;
using DiskThinner.Core;

namespace DiskThinner.App.Formatting;

/// <summary>时间显示：列表里用相对时间（与原型一致），详情里用完整时间。</summary>
internal static class TimeText
{
    public static string Relative(long fileTimeUtc, DateTime nowLocal)
    {
        if (fileTimeUtc <= 0)
        {
            return "";
        }

        DateTime local;
        try
        {
            local = DateTime.FromFileTimeUtc(fileTimeUtc).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }

        var span = nowLocal - local;
        if (span < TimeSpan.Zero)
        {
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (span.TotalMinutes < 1)
        {
            return L.T("刚刚");
        }

        if (span.TotalHours < 1)
        {
            return L.F("{0} 分钟前", (int)span.TotalMinutes);
        }

        if (local.Date == nowLocal.Date)
        {
            return span.TotalHours < 6 ? L.F("{0} 小时前", (int)span.TotalHours) : L.F("今天 {0}", local.ToString("HH:mm", CultureInfo.InvariantCulture));
        }

        var days = (nowLocal.Date - local.Date).Days;
        return days switch
        {
            1 => L.T("昨天"),
            < 60 => L.F("{0} 天前", days),
            _ => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    public static string Full(long fileTimeUtc)
    {
        if (fileTimeUtc <= 0)
        {
            return L.T("未知");
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTimeUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return L.T("未知");
        }
    }
}

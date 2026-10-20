using System.Globalization;

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
            return "刚刚";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (local.Date == nowLocal.Date)
        {
            return span.TotalHours < 6 ? $"{(int)span.TotalHours} 小时前" : "今天 " + local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        var days = (nowLocal.Date - local.Date).Days;
        return days switch
        {
            1 => "昨天",
            < 60 => $"{days} 天前",
            _ => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }

    public static string Full(long fileTimeUtc)
    {
        if (fileTimeUtc <= 0)
        {
            return "未知";
        }

        try
        {
            return DateTime.FromFileTimeUtc(fileTimeUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "未知";
        }
    }
}

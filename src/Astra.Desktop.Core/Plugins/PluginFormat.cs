using System.Globalization;

namespace Astra.Desktop.Core.Plugins;

/// <summary>
/// Date and time texts the Plugins and Smart routing pages show: <c>formatDateTime</c> / <c>formatTime</c> of
/// <c>web/src/lib/format.ts</c> (Intl, 24-hour clock, zh-CN or en-US shapes) in the local time zone, as the browser does.
/// </summary>
public static class PluginFormat
{
    /// <summary>The em dash the web shows for "no value".</summary>
    public const string Dash = "—";

    /// <summary>The zone times are shown in (the machine's; the visual scenes pin UTC like the web capture's <c>timezoneId</c>).</summary>
    public static TimeZoneInfo DisplayZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>"2026/10/26 12:34" (zh) or "10/26/2026, 12:34" (en); a dash for null (<c>formatDateTime</c>).</summary>
    public static string DateTime(DateTimeOffset? at, string locale)
    {
        if (at is null) return Dash;
        var d = TimeZoneInfo.ConvertTime(at.Value, DisplayZone);
        return locale == "zh"
            ? string.Create(CultureInfo.InvariantCulture, $"{d.Year:0000}/{d.Month:00}/{d.Day:00} {d.Hour:00}:{d.Minute:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{d.Month:00}/{d.Day:00}/{d.Year:0000}, {d.Hour:00}:{d.Minute:00}");
    }

    /// <summary>
    /// Today's times with seconds ("12:34:56"), other days as "10/26 12:34" (zh) or "10/26, 12:34" (en) (<c>formatTime</c>);
    /// <paramref name="now"/> decides what "today" is (pages pass their clock so scenes are stable).
    /// </summary>
    public static string Time(DateTimeOffset at, string locale, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= DisplayZone;
        var d = TimeZoneInfo.ConvertTime(at, zone);
        var today = TimeZoneInfo.ConvertTime(now, zone);
        if (d.Date == today.Date) return string.Create(CultureInfo.InvariantCulture, $"{d.Hour:00}:{d.Minute:00}:{d.Second:00}");
        var day = string.Create(CultureInfo.InvariantCulture, $"{d.Month:00}/{d.Day:00}");
        var hm = string.Create(CultureInfo.InvariantCulture, $"{d.Hour:00}:{d.Minute:00}");
        return locale == "zh" ? $"{day} {hm}" : $"{day}, {hm}";
    }
}

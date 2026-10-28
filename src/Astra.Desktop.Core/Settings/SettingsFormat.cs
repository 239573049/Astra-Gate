using System.Globalization;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// The date / number formats the Settings and Privacy pages use (web/src/lib/format.ts <c>formatDateTime</c>, <c>formatDuration</c>,
/// <c>formatTime</c>, <c>formatTokens</c>). They are written out by hand, not taken from <c>CultureInfo</c>: a desktop build may run
/// with invariant globalization, and the web's <c>Intl</c> output (zh-CN "2026/10/26 12:00", en-US "10/26/2026, 12:00") has to stay the same.
/// </summary>
public static class SettingsFormat
{
    private static bool Zh(string language) => language == "zh";

    /// <summary>The zone times are shown in when a caller passes none (the machine's; the visual scenes pin UTC like the web capture).</summary>
    public static TimeZoneInfo DisplayZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>web <c>formatDateTime</c>: date and 24-hour time in local time; "—" for a missing value.</summary>
    public static string DateTime(DateTimeOffset? at, string language, TimeZoneInfo? zone = null)
    {
        if (at is not { } value) return "—";
        var local = TimeZoneInfo.ConvertTime(value, zone ?? DisplayZone);
        return Zh(language)
            ? local.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM/dd/yyyy, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// web <c>formatTime</c>: today's requests show hours, minutes and seconds, other days month / day and hours, minutes
    /// (<paramref name="now"/> is the page clock, not the wall clock).
    /// </summary>
    public static string Time(DateTimeOffset at, DateTimeOffset now, string language, TimeZoneInfo? zone = null)
    {
        zone ??= DisplayZone;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        if (local.Date == TimeZoneInfo.ConvertTime(now, zone).Date) return local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return Zh(language)
            ? local.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM/dd, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// web <c>new Date(x).toLocaleString()</c> (the update row's "last checked" detail): zh "2026/10/26 11:25:00", en "10/26/2026, 11:25:00 AM".
    /// </summary>
    public static string Full(DateTimeOffset at, string language, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone ?? DisplayZone);
        return Zh(language)
            ? local.ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("M/d/yyyy, h:mm:ss tt", CultureInfo.InvariantCulture);
    }

    /// <summary>web <c>formatDuration</c>: one unit, "12 分钟" / "3 hours": minutes under two hours, hours under two days, else days.</summary>
    public static string Duration(double ms, string language)
    {
        var minutes = Math.Max(1, (long)Math.Floor(ms / 60_000));
        var (value, unit) = minutes < 120 ? (minutes, 0) : minutes < 48 * 60 ? (minutes / 60, 1) : (minutes / 1440, 2);
        if (Zh(language)) return $"{value} {(unit == 0 ? "分钟" : unit == 1 ? "小时" : "天")}";
        var noun = unit == 0 ? "minute" : unit == 1 ? "hour" : "day";
        return $"{value} {noun}{(value == 1 ? "" : "s")}";
    }

    /// <summary>web <c>formatTokens</c> (not compact): en-US thousands separators; "—" for null.</summary>
    public static string Count(long? n) => n is null ? "—" : n.Value.ToString("N0", CultureInfo.InvariantCulture);
}

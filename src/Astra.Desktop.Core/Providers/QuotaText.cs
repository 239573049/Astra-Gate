using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Providers;

/// <summary>One plan of a balance / quota snapshot (web <c>ProviderQuotaPlan</c>); a missing field stays null, never a made-up number.</summary>
public sealed record QuotaPlan(
    string? Name, string? Unit, double? Remaining, double? Total, double? Used, double? UsedPercent, string? ResetsAtUtc, int? WindowMinutes, string? Extra);

/// <summary>
/// The stored balance / quota snapshot of an API-key provider (the <c>quota</c> JSON of <see cref="ProviderDto"/>, web <c>ProviderQuotaSnapshot</c>).
/// <see cref="PlanLabel"/> is the extractor's plan badge and <see cref="InvalidMessage"/> the upstream's words when <see cref="IsValid"/> is false.
/// </summary>
public sealed record QuotaSnapshot(
    DateTimeOffset? FetchedAt, bool? IsValid, List<QuotaPlan> Plans, string? Error, string? ErrorCode, DateTimeOffset? ErrorAt,
    string? PlanLabel = null, string? InvalidMessage = null)
{
    /// <summary>Reads the snapshot JSON; null for none.</summary>
    public static QuotaSnapshot? Parse(JsonObject? json)
    {
        if (json is null) return null;
        var plans = new List<QuotaPlan>();
        if (json["plans"] is JsonArray arr)
            foreach (var p in arr.OfType<JsonObject>())
                plans.Add(new QuotaPlan(Str(p, "name"), Str(p, "unit"), Num(p, "remaining"), Num(p, "total"), Num(p, "used"), Num(p, "usedPercent"),
                    Str(p, "resetsAtUtc"), (int?)Num(p, "windowMinutes"), Str(p, "extra")));
        return new QuotaSnapshot(Date(json, "fetchedAtUtc"), json["isValid"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null, plans,
            Str(json, "error"), Str(json, "errorCode"), Date(json, "errorAtUtc"), Str(json, "planLabel"), Str(json, "invalidMessage"));
    }

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static DateTimeOffset? Date(JsonObject o, string k)
        => Str(o, k) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}

/// <summary>
/// The provider card's balance line (web <c>lib/quota.ts</c> <c>quotaSummary</c> / <c>quotaFailure</c> / <c>quotaEnabled</c>). Texts come from the
/// shared <c>providers.quota.*</c> catalog keys. Differences from the web: amounts use <c>CultureInfo</c> ("¥110.00" / "US$42.18" for a currency
/// the culture does not own) instead of <c>Intl.NumberFormat</c>; window lengths and "resets in" use the two catalog languages only.
/// </summary>
public static class QuotaText
{
    private static readonly System.Text.RegularExpressions.Regex Currency = new("^[A-Z]{3}$");

    /// <summary>API-key provider whose usage query is on (subscriptions show their quota per account instead).</summary>
    public static bool Enabled(ProviderDto p) => p.AuthScheme != "oauth-subscription" && p.QuotaConfig.Enabled;

    /// <summary>The failure to show: only when newer than the last good snapshot.</summary>
    public static (string? Code, string Message)? Failure(QuotaSnapshot? s)
    {
        if (s?.Error is null or "") return null;
        if (s.FetchedAt is { } f && s.ErrorAt is { } e && e < f) return null;
        return (s.ErrorCode, s.Error);
    }

    /// <summary>The failure in words: the catalog text of its code, else the server's message.</summary>
    public static string FailureText((string? Code, string Message) f)
    {
        if (f.Code is { } code && Loc.Has("providers.quota.error." + code)) return Loc.T("providers.quota.error." + code);
        return f.Message;
    }

    /// <summary>"¥110.00" / "$0.0042" for ISO currencies, "1,200 credits" otherwise.</summary>
    public static string FormatAmount(double value, string? unit)
    {
        var culture = CultureInfo.GetCultureInfo(Loc.Language == "zh" ? "zh-CN" : "en-US");
        if (unit is not null && Currency.IsMatch(unit))
        {
            var small = value != 0 && Math.Abs(value) < 1;
            var symbol = KnownSymbol(unit);
            var digits = small ? 4 : 2;
            var number = Math.Abs(value).ToString("#,##0." + new string('0', 2) + new string('#', digits - 2), culture);
            return (value < 0 ? "-" : "") + symbol + number;
        }
        var n = value.ToString("#,##0.##", culture);
        return unit is { Length: > 0 } ? $"{n} {unit}" : n;
    }

    private static string KnownSymbol(string iso) => iso switch
    {
        "USD" => Loc.Language == "zh" ? "US$" : "$",
        "CNY" => Loc.Language == "zh" ? "¥" : "CN¥",
        "EUR" => "€",
        "JPY" => "JP¥",
        "GBP" => "£",
        _ => iso + " ",
    };

    /// <summary>Share used 0–100: reported directly, or derived from used / total; null when unknown.</summary>
    public static double? UsedPercentOf(QuotaPlan p)
    {
        if (p.UsedPercent is { } up && double.IsFinite(up)) return Math.Clamp(up, 0, 100);
        if (p.Used is { } u && p.Total is { } t && t > 0) return Math.Clamp(u / t * 100, 0, 100);
        return null;
    }

    private static bool IsWindow(QuotaPlan p) => p.WindowMinutes is not null && p.UsedPercent is not null;

    /// <summary>One short phrase per plan: "余额 ¥110.00", "5 小时已用 12%", "已用 30%".</summary>
    public static string? PlanPhrase(QuotaPlan p)
    {
        var used = UsedPercentOf(p);
        if (IsWindow(p))
            return Loc.T("providers.quota.window", ("window", WindowLength(p.WindowMinutes!.Value)), ("percent", Round(used ?? 0)));
        if (p.Remaining is { } rem)
        {
            var amount = Loc.T("providers.quota.balance", ("amount", FormatAmount(rem, p.Unit)));
            return used is { } u ? $"{amount} · {Loc.T("providers.quota.usedPercent", ("percent", Round(u)))}" : amount;
        }
        if (used is { } pct) return Loc.T("providers.quota.usedPercent", ("percent", Round(pct)));
        if (p.Used is { } us && p.Total is { } tot)
            return Loc.T("providers.quota.totalUsed", ("used", FormatAmount(us, p.Unit)), ("total", FormatAmount(tot, p.Unit)));
        return null;
    }

    /// <summary>The card line: every plan's phrase joined by " · ", or null when there is nothing yet.</summary>
    public static string? Summary(QuotaSnapshot? s)
    {
        var parts = (s?.Plans ?? []).Select(PlanPhrase).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).ToList();
        if (s?.IsValid == false) parts.Insert(0, Loc.T("providers.quota.invalid"));
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static long Round(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    /// <summary>"5小时" / "5h", "7天" / "7d" (web <c>windowLength</c>).</summary>
    public static string WindowLength(int minutes)
    {
        var zh = Loc.Language == "zh";
        string Unit(int v, string zhUnit, string enUnit) => zh ? $"{v}{zhUnit}" : $"{v}{enUnit}";
        if (minutes >= 1440 && minutes % 1440 == 0) return Unit(minutes / 1440, "天", "d");
        if (minutes >= 60) return Unit((int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero), "小时", "h");
        return Unit(minutes, "分钟", "m");
    }
}

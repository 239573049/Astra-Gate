using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Core.Tray;

/// <summary>What a quota row measures (web lib/tray.ts <c>QuotaKind</c>): subscription windows / credits, or an API provider's window / balance.</summary>
public enum TrayQuotaKind { Session, Weekly, Credits, Window, Balance }

/// <summary>Money (or credits) left on a balance row.</summary>
public sealed record TrayAmount(double Value, string? Unit);

/// <summary>One quota row (web <c>QuotaRow</c>).</summary>
/// <param name="Kind">What it measures.</param>
/// <param name="WindowMinutes">Window length (session / weekly / window) for the "5小时" label.</param>
/// <param name="Remaining">Remaining share 0–100; null for a balance with no known total (no meter, never the tightest).</param>
/// <param name="ResetsAt">ISO reset time, or null.</param>
/// <param name="Amount">Balance amount, when the row is a balance with an amount.</param>
/// <param name="Label">Balance label (plan / currency name) from the upstream.</param>
public sealed record TrayQuotaRow(TrayQuotaKind Kind, int? WindowMinutes, double? Remaining, string? ResetsAt, TrayAmount? Amount = null, string? Label = null);

/// <summary>The quota rows of one account or API provider (web <c>QuotaPlan</c>).</summary>
public sealed record TrayQuotaPlan(string AccountId, string ProviderId, string ProviderName, string? ProviderIcon, string AccountName, IReadOnlyList<TrayQuotaRow> Rows);

/// <summary>A row with its plan, where the row is known to have a share.</summary>
public sealed record TightRow(TrayQuotaPlan Plan, TrayQuotaRow Row, double Remaining);

/// <summary>One bar of the hourly chart.</summary>
public sealed record HourBar(string Bucket, long Value);

/// <summary>The color of a quota meter (web <c>quotaTone</c>).</summary>
public enum QuotaTone { Green, Orange, Red }

/// <summary>
/// The pure helpers behind the tray panel and the menu-bar title: <c>web/src/lib/tray.ts</c> ported (plus the panel's <c>quotaRowName</c>).
/// Times are the page clock's; relative texts ("3分钟前", "in 2 hours") are written out for the two catalog languages the way
/// <c>Intl.RelativeTimeFormat(…, { numeric: 'auto' })</c> words them, so they do not depend on the machine's ICU data.
/// </summary>
public static class TrayMath
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Remaining below this share counts as running low (orange) (<c>QUOTA_LOW</c>).</summary>
    public const double QuotaLow = 30;

    /// <summary>Remaining below this share is critical (red) (<c>QUOTA_CRITICAL</c>).</summary>
    public const double QuotaCritical = 10;

    /// <summary>Requests that did not succeed (<c>failedRequests</c>).</summary>
    public static long FailedRequests(StatsSummaryDto s) => Math.Max(0, JsRound(s.Requests * (1 - s.SuccessRate)));

    /// <summary>Tile-sized cost: "$0", "$&lt;0.01", "$0.37", "$12.40", "$1.2K" (<c>formatCostShort</c>).</summary>
    public static string FormatCostShort(decimal usd)
    {
        if (usd <= 0) return "$0";
        if (usd < 0.01m) return "$<0.01";
        if (usd < 1000) return "$" + Math.Round(usd, 2, MidpointRounding.AwayFromZero).ToString("F2", Inv);
        return "$" + StatsFormat.Compact(usd);
    }

    /// <summary>Today's requests per local hour, all 24 in order, zero where nothing happened (<c>hourlyRequests</c>).</summary>
    public static List<HourBar> HourlyRequests(IEnumerable<TimeseriesPointDto> points, LocalTime now)
        => StatsMath.FillBuckets(StatsMath.BucketTotals(points, TrendMeasure.Requests), StatsMath.RangeBuckets(StatsRange.Today, now))
            .Select(b => new HourBar(b.Bucket, (long)b.Value)).ToList();

    /// <summary>Yesterday's tokens from a daily series ("yyyy-MM-dd" local buckets); 0 when absent (<c>yesterdayTokens</c>).</summary>
    public static long YesterdayTokens(IEnumerable<TimeseriesPointDto> points, LocalTime now)
    {
        var key = now.Now.Date.AddDays(-1).ToString("yyyy-MM-dd", Inv);
        return points.Where(p => p.Bucket == key).Sum(p => p.Tokens);
    }

    /// <summary>Relative change today vs yesterday (0.25 = +25%); null when yesterday had nothing (<c>changeRatio</c>).</summary>
    public static double? ChangeRatio(long today, long yesterday) => yesterday <= 0 ? null : (double)(today - yesterday) / yesterday;

    /// <summary>The model with the most tokens today and its share of all today's tokens (<c>topModelShare</c>).</summary>
    public static (string Model, double Share)? TopModelShare(IEnumerable<TopModelDto> top, long totalTokens)
    {
        // A stable sort like the web's [...top].sort((a, b) => b.tokens - a.tokens)[0]: the first of equals wins.
        var best = top.OrderByDescending(m => m.Tokens).FirstOrDefault();
        if (best is null || best.Tokens <= 0) return null;
        return (best.Model, totalTokens > 0 ? Math.Min(1, (double)best.Tokens / totalTokens) : 1);
    }

    // ---------- subscription quota windows ----------

    private static double? RemainingOf(double? used) => used is { } u && double.IsFinite(u) ? Math.Min(100, Math.Max(0, 100 - u)) : null;

    private static TrayQuotaRow? WindowRow(TrayQuotaKind kind, JsonNode? w)
    {
        if (w is not JsonObject o) return null;
        var remaining = RemainingOf(Num(o, "usedPercent"));
        if (remaining is null) return null;
        var minutes = Num(o, "windowMinutes") is { } m ? (int)m : kind == TrayQuotaKind.Session ? 300 : 10_080;
        return new TrayQuotaRow(kind, minutes, remaining, Str(o, "resetsAtUtc"));
    }

    /// <summary>Quota windows of the active subscription accounts that have a snapshot (<c>quotaPlans</c>).</summary>
    public static List<TrayQuotaPlan> QuotaPlans(IEnumerable<(ProviderDto Provider, ProviderAccountDto Account)> accounts)
    {
        var plans = new List<TrayQuotaPlan>();
        foreach (var (provider, account) in accounts)
        {
            if (account.Status != "active" || account.Quota is not JsonObject q) continue;
            var rows = new List<TrayQuotaRow>();
            if (WindowRow(TrayQuotaKind.Session, q["session"]) is { } s) rows.Add(s);
            if (WindowRow(TrayQuotaKind.Weekly, q["weekly"]) is { } w) rows.Add(w);
            if (q["credits"] is JsonObject c && RemainingOf(Num(c, "usedPercent")) is { } credits)
                rows.Add(new TrayQuotaRow(TrayQuotaKind.Credits, null, credits, null));
            if (rows.Count == 0) continue;
            var label = Str(q, "planLabel");
            plans.Add(new TrayQuotaPlan(account.Id, provider.Id, string.IsNullOrEmpty(label) ? provider.Name : label, provider.Icon,
                !string.IsNullOrEmpty(account.DisplayName) ? account.DisplayName : !string.IsNullOrEmpty(account.AccountEmail) ? account.AccountEmail! : account.Id, rows));
        }
        return plans;
    }

    /// <summary>
    /// Rows of API-key providers whose usage query is on: plan windows become meters, balances show their amount (a meter only with a
    /// known total). A failed query keeps the last good numbers (<c>providerQuotaPlans</c>).
    /// </summary>
    public static List<TrayQuotaPlan> ProviderQuotaPlans(IEnumerable<ProviderDto> providers)
    {
        var plans = new List<TrayQuotaPlan>();
        foreach (var p in providers)
        {
            if (!p.Enabled || !QuotaText.Enabled(p)) continue;
            var snapshot = QuotaSnapshot.Parse(p.Quota);
            if (snapshot?.IsValid == false) continue;
            var rows = new List<TrayQuotaRow>();
            foreach (var plan in snapshot?.Plans ?? [])
            {
                var used = QuotaText.UsedPercentOf(plan);
                if (plan.WindowMinutes is not null && plan.UsedPercent is not null)
                    rows.Add(new TrayQuotaRow(TrayQuotaKind.Window, plan.WindowMinutes, 100 - (used ?? 0), plan.ResetsAtUtc));
                else if (plan.Remaining is not null || used is not null)
                    rows.Add(new TrayQuotaRow(TrayQuotaKind.Balance, null, used is null ? null : 100 - used, plan.ResetsAtUtc,
                        plan.Remaining is { } r ? new TrayAmount(r, plan.Unit) : null, plan.Name));
            }
            if (rows.Count == 0) continue;
            plans.Add(new TrayQuotaPlan("provider:" + p.Id, p.Id, p.Name, p.Icon, "", rows));
        }
        return plans;
    }

    /// <summary>The window closest to running out across every plan; rows without a share are skipped (<c>tightestQuota</c>).</summary>
    public static TightRow? TightestQuota(IEnumerable<TrayQuotaPlan> plans)
    {
        TightRow? best = null;
        foreach (var plan in plans)
            foreach (var row in plan.Rows)
                if (row.Remaining is { } r && (best is null || r < best.Remaining)) best = new TightRow(plan, row, r);
        return best;
    }

    /// <summary>The <paramref name="limit"/> windows closest to running out, tightest first (<c>tightestRows</c>; a stable sort).</summary>
    public static List<TightRow> TightestRows(IEnumerable<TrayQuotaPlan> plans, int limit)
        => plans.SelectMany(p => p.Rows.Where(r => r.Remaining is not null).Select(r => new TightRow(p, r, r.Remaining!.Value)))
            .OrderBy(t => t.Remaining).Take(limit).ToList();

    /// <summary>Meter color for a remaining share (<c>quotaTone</c>).</summary>
    public static QuotaTone Tone(double remaining) => remaining < QuotaCritical ? QuotaTone.Red : remaining < QuotaLow ? QuotaTone.Orange : QuotaTone.Green;

    /// <summary>"5小时" / "5h", "7天" / "7d" (<c>windowLength</c>; the same words as the provider card).</summary>
    public static string WindowLength(int minutes) => QuotaText.WindowLength(minutes);

    /// <summary>A row's name: the balance label, "额度" for credits / a window of unknown length, else the window length (panel <c>quotaRowName</c>).</summary>
    public static string RowName(TrayQuotaRow row) =>
        row.Kind == TrayQuotaKind.Balance ? row.Label ?? Loc.T("tray.quotaBalance")
        : row.Kind == TrayQuotaKind.Credits || row.WindowMinutes is null ? Loc.T("tray.quotaCredits")
        : WindowLength(row.WindowMinutes.Value);

    /// <summary>A remaining share as the panel prints it: "42%".</summary>
    public static string Percent(double remaining) => JsRound(remaining).ToString(Inv) + "%";

    /// <summary>"1小时后" / "in 1 hour"; null when unknown or already past (<c>resetsIn</c>).</summary>
    public static string? ResetsIn(string? iso, DateTimeOffset now)
    {
        if (iso is null || !DateTimeOffset.TryParse(iso, Inv, DateTimeStyles.AssumeUniversal, out var at)) return null;
        var ms = (at - now).TotalMilliseconds;
        if (ms <= 0) return null;
        var hours = ms / 3_600_000;
        if (hours >= 48) return Relative(JsRound(hours / 24), RelUnit.Day);
        if (hours >= 1) return Relative(JsRound(hours), RelUnit.Hour);
        return Relative(Math.Max(1, JsRound(ms / 60_000)), RelUnit.Minute);
    }

    /// <summary><paramref name="justNow"/> under a minute, then "3分钟前" / "3 minutes ago"; null when never fetched (<c>updatedAgo</c>).</summary>
    public static string? UpdatedAgo(DateTimeOffset? at, DateTimeOffset now, string justNow)
    {
        if (at is not { } a) return null;
        var seconds = Math.Max(0, (now - a).TotalSeconds);
        if (seconds < 60) return justNow;
        if (seconds < 3600) return Relative(-JsRound(seconds / 60), RelUnit.Minute);
        return Relative(-JsRound(seconds / 3600), RelUnit.Hour);
    }

    private enum RelUnit { Minute, Hour, Day }

    /// <summary><c>Intl.RelativeTimeFormat(locale, { numeric: 'auto' }).format(value, unit)</c> for zh-CN / en-US.</summary>
    private static string Relative(long value, RelUnit unit)
    {
        var zh = Loc.Language == "zh";
        var n = Math.Abs(value);
        var future = value > 0;
        if (unit == RelUnit.Day)
        {
            if (value == 1) return zh ? "明天" : "tomorrow";
            if (value == -1) return zh ? "昨天" : "yesterday";
            if (zh && value == 2) return "后天";
            if (zh && value == -2) return "前天";
        }
        var count = n.ToString("#,##0", Inv);
        if (zh)
        {
            var u = unit switch { RelUnit.Minute => "分钟", RelUnit.Hour => "小时", _ => "天" };
            return count + u + (future ? "后" : "前");
        }
        var word = unit switch { RelUnit.Minute => "minute", RelUnit.Hour => "hour", _ => "day" } + (n == 1 ? "" : "s");
        return future ? $"in {count} {word}" : $"{count} {word} ago";
    }

    /// <summary>JavaScript's <c>Math.round</c> (half up, also for negatives).</summary>
    private static long JsRound(double v) => (long)Math.Floor(v + 0.5);

    private static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}

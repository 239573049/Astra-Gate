using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Overview;

/// <summary>The two looks a daily bill can be printed in (web <c>BillStyle</c>): a thermal receipt and a comic page.</summary>
public static class BillStyles
{
    /// <summary>A · the thermal receipt (the default).</summary>
    public const string Receipt = "receipt";

    /// <summary>E · the comic page.</summary>
    public const string Manga = "manga";

    /// <summary>Both, in the switch's order (web <c>BILL_STYLES</c>).</summary>
    public static readonly IReadOnlyList<string> All = [Receipt, Manga];

    /// <summary>A known style, else <see cref="Receipt"/>.</summary>
    public static string Normalize(string? style) => style == Manga ? Manga : Receipt;

    /// <summary>The i18n key of a style's label in the switch.</summary>
    public static string LabelKey(string style) => "bill.style." + Normalize(style);
}

/// <summary>One model line of a provider on the bill (web <c>BillModelLine</c>).</summary>
public sealed record BillModelLine(string Name, string Cost, string Requests, string Input, string Output);

/// <summary>One provider on the bill (web <c>BillProviderBlock</c>).</summary>
/// <param name="Id">The provider id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Upper">The name in capitals (the receipt prints it so).</param>
/// <param name="Cost">Its cost.</param>
/// <param name="Requests">Its requests.</param>
/// <param name="Share">Share of the day's cost (of its requests when nothing cost anything), 0..1.</param>
/// <param name="Models">Its model lines.</param>
public sealed record BillProviderBlock(string Id, string Name, string Upper, string Cost, string Requests, double Share, IReadOnlyList<BillModelLine> Models);

/// <summary>The provider with the largest share and that share as a whole percent ("91%").</summary>
public sealed record BillLeader(string Name, string Share);

/// <summary>A bill shaped for printing (web <c>BillView</c>): every string both looks print.</summary>
public sealed record BillView
{
    /// <summary>"AG-20261111".</summary>
    public required string Number { get; init; }

    /// <summary>"2026-11-11".</summary>
    public required string Date { get; init; }

    /// <summary>"周三" / "Wed".</summary>
    public required string Weekday { get; init; }

    /// <summary>"11.11".</summary>
    public required string DateShort { get; init; }

    /// <summary>"00:00 – 18:42"; a finished day ends at "24:00".</summary>
    public required string Window { get; init; }

    /// <summary>The window's end, "18:42".</summary>
    public required string Until { get; init; }

    /// <summary>"2026-11-11 18:42".</summary>
    public required string GeneratedAt { get; init; }

    /// <summary>The day's cost.</summary>
    public required string Total { get; init; }

    /// <summary>The day's requests.</summary>
    public required string Requests { get; init; }

    /// <summary>"99.3%", or a dash without requests.</summary>
    public required string SuccessRate { get; init; }

    /// <summary>Input tokens, grouped in full: "8,728,100".</summary>
    public required string InputTokens { get; init; }

    /// <summary>Output tokens, grouped in full.</summary>
    public required string OutputTokens { get; init; }

    /// <summary>Input + output, compact: "9.4M".</summary>
    public required string TokensCompact { get; init; }

    /// <summary>The providers, most expensive first.</summary>
    public required IReadOnlyList<BillProviderBlock> Providers { get; init; }

    /// <summary>The leading provider, or null on an empty day.</summary>
    public BillLeader? Top { get; init; }
}

/// <summary>
/// The overview's daily bill shaped for printing: <c>web/src/lib/bill.ts</c> ported. The text must be byte-identical to the web's
/// (<c>DailyBillShapingTests</c> pins the same cases as <c>web/src/test/bill.test.ts</c>), so amounts and counts go through
/// <see cref="StatsFormat"/> and weekdays through <see cref="ActivityHeatmapModel.WeekdayName"/>; change both files together.
/// Wall-clock times are the bill's own offset (<see cref="DailyBillDto.TzOffsetMinutes"/>), never the machine's zone.
/// </summary>
public static class DailyBillShaping
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Shapes <paramref name="bill"/> for printing in <paramref name="language"/> ("zh" / "en").</summary>
    public static BillView Shape(DailyBillDto bill, string language)
    {
        var offset = TimeSpan.FromMinutes(bill.TzOffsetMinutes);
        var day = DateTime.ParseExact(bill.Date, "yyyy-MM-dd", Inv);
        // Monday-first row of the day, as the heatmap's weekday names count.
        var row = ((int)day.DayOfWeek + 6) % 7;
        var from = bill.FromUtc.ToOffset(offset);
        var to = bill.ToUtc.ToOffset(offset);
        var dayEnded = bill.ToUtc - bill.FromUtc >= TimeSpan.FromDays(1);
        var until = dayEnded ? "24:00" : to.ToString("HH:mm", Inv);
        var generated = bill.GeneratedAtUtc.ToOffset(offset);

        var byCost = bill.CostUsd > 0;
        var providers = bill.Providers.Select(p => new BillProviderBlock(
            p.ProviderId,
            p.Name,
            p.Name.ToUpperInvariant(),
            StatsFormat.Usd(p.CostUsd),
            StatsFormat.Tokens(p.Requests),
            byCost ? (double)p.CostUsd / (double)bill.CostUsd : bill.Requests > 0 ? (double)p.Requests / bill.Requests : 0,
            p.Models.Select(m => new BillModelLine(
                m.Model, StatsFormat.Usd(m.CostUsd), StatsFormat.Tokens(m.Requests),
                StatsFormat.Tokens(m.InputTokens, compact: true), StatsFormat.Tokens(m.OutputTokens, compact: true))).ToList())).ToList();
        // The first of equal shares leads (web: reduce keeps `best` on a tie).
        BillProviderBlock? lead = null;
        foreach (var p in providers)
            if (lead is null || p.Share > lead.Share) lead = p;

        return new BillView
        {
            Number = "AG-" + bill.Date.Replace("-", "", StringComparison.Ordinal),
            Date = bill.Date,
            Weekday = ActivityHeatmapModel.WeekdayName(row, language),
            DateShort = day.ToString("MM.dd", Inv),
            Window = from.ToString("HH:mm", Inv) + " – " + until,
            Until = until,
            GeneratedAt = generated.ToString("yyyy-MM-dd HH:mm", Inv),
            Total = StatsFormat.Usd(bill.CostUsd),
            Requests = StatsFormat.Tokens(bill.Requests),
            SuccessRate = bill.Requests > 0 ? StatsFormat.Percent((double)bill.SuccessRequests / bill.Requests) : StatsFormat.Dash,
            InputTokens = StatsFormat.Tokens(bill.InputTokens),
            OutputTokens = StatsFormat.Tokens(bill.OutputTokens),
            TokensCompact = StatsFormat.Tokens(bill.InputTokens + bill.OutputTokens, compact: true),
            Providers = providers,
            Top = lead is null ? null : new BillLeader(lead.Name, Math.Round(lead.Share * 100, MidpointRounding.AwayFromZero).ToString("0", Inv) + "%"),
        };
    }

    /// <summary>The file name a saved bill gets: "astra-bill-2026-11-11-receipt.png" (web <c>billFileName</c>).</summary>
    public static string FileName(string date, string style) => $"astra-bill-{date}-{BillStyles.Normalize(style)}.png";
}

/// <summary>Where the last picked bill style is kept: the desktop's <c>desktop-prefs.json</c> (web: localStorage <c>astra.bill.style</c>).</summary>
public interface IBillPrefsStore
{
    /// <summary>The stored value of <paramref name="key"/> (a copy), or null.</summary>
    JsonNode? Get(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (null removes it) and saves.</summary>
    void Set(string key, JsonNode? value);
}

/// <summary>The remembered bill style (top-level prefs key <see cref="Key"/>); without a store (tests, scenes) it lives in memory.</summary>
public static class BillPrefs
{
    /// <summary>The top-level prefs key.</summary>
    public const string Key = "billStyle";

    private static IBillPrefsStore? _store;
    private static string? _style;

    /// <summary>Attaches <paramref name="store"/> (null detaches); the style is read from it when next asked for.</summary>
    public static void Use(IBillPrefsStore? store)
    {
        if (ReferenceEquals(store, _store)) return;
        _store = store;
        _style = null;
    }

    /// <summary><see cref="BillStyles.Receipt"/> or <see cref="BillStyles.Manga"/>.</summary>
    public static string Style
    {
        get => _style ??= BillStyles.Normalize(_store?.Get(Key) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null);
        set
        {
            _style = BillStyles.Normalize(value);
            _store?.Set(Key, _style == BillStyles.Receipt ? null : JsonValue.Create(_style));
        }
    }
}

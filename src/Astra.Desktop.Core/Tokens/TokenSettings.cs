using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Tokens;

/// <summary>A budget being edited: the limit stays null while its field is empty and is dropped on save (web <c>DraftBudget</c>).</summary>
public sealed record DraftBudget(string Metric, string Period, decimal? Limit);

/// <summary>The budget editor's rules (web <c>BudgetsEditor</c> in <c>TokensPage.tsx</c>).</summary>
public static class BudgetRules
{
    /// <summary>The metrics, in the order a new row picks from.</summary>
    public static readonly IReadOnlyList<string> Metrics = ["cost", "requests", "tokens"];

    /// <summary>The periods.</summary>
    public static readonly IReadOnlyList<string> Periods = ["day", "week", "month", "total"];

    /// <summary>The i18n key of a metric's name.</summary>
    public static string MetricKey(string metric) => "tokens.metric." + metric;

    /// <summary>The i18n key of a period's name.</summary>
    public static string PeriodKey(string period) => "tokens.period." + period;

    /// <summary>A new row starts on the first metric x period not used yet; null when every combination is taken.</summary>
    public static DraftBudget? NextFree(IReadOnlyList<DraftBudget> budgets)
    {
        foreach (var metric in Metrics)
            foreach (var period in Periods)
                if (!budgets.Any(b => b.Metric == metric && b.Period == period)) return new DraftBudget(metric, period, null);
        return null;
    }

    /// <summary>Two rows on the same metric and period (the save button stays off and the field shows an error).</summary>
    public static bool HasDuplicate(IReadOnlyList<DraftBudget> budgets)
        => budgets.Select(b => b.Metric + ":" + b.Period).Distinct(StringComparer.Ordinal).Count() < budgets.Count;

    /// <summary>
    /// The <c>limits</c> body field: budgets with a positive limit plus the whole-number RPM cap; null (unlimited) when there is neither.
    /// </summary>
    public static JsonNode? LimitsJson(IReadOnlyList<DraftBudget> budgets, double? rpm)
    {
        var kept = budgets.Where(b => b.Limit is > 0).ToList();
        var cap = rpm is > 0 ? (int?)Math.Floor(rpm.Value) : null;
        if (kept.Count == 0 && cap is null) return null;
        var array = new JsonArray();
        foreach (var b in kept) array.Add((JsonNode)new JsonObject { ["metric"] = b.Metric, ["period"] = b.Period, ["limit"] = b.Limit!.Value });
        return new JsonObject { ["budgets"] = array, ["rpm"] = cap };
    }
}

/// <summary>Whether a token's key works with any client or only the chosen ones (web <c>clientsMode</c>).</summary>
public enum ClientsMode
{
    /// <summary>Any client (<c>allowedClients</c> is null).</summary>
    Any,

    /// <summary>Only the chosen kinds; an empty list means direct calls only.</summary>
    Only,
}

/// <summary>
/// Everything the token settings sheet edits, as one value: built from a token when the sheet opens, turned into a PATCH body on
/// save (web <c>TokenSettingsSheet</c>).
/// </summary>
public sealed record TokenSettingsDraft(
    string Name, PoolDraft Pool, IReadOnlyList<DraftBudget> Budgets, double? Rpm, ClientsMode Mode, IReadOnlyList<string> Allowed)
{
    /// <summary>The draft of a saved token.</summary>
    public static TokenSettingsDraft Of(TokenDto token) => new(
        token.Name, PoolDrafts.Of(token),
        token.Limits?.Budgets.Select(b => new DraftBudget(b.Metric, b.Period, b.Limit)).ToList() ?? [],
        token.Limits?.Rpm, token.AllowedClients is null ? ClientsMode.Any : ClientsMode.Only, token.AllowedClients ?? []);

    /// <summary>Save is allowed with a name and no duplicate budget.</summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && !BudgetRules.HasDuplicate(Budgets);

    /// <summary>
    /// The PATCH body: name, the pool (replacing the direct provider; members of deleted providers dropped), the limits (null =
    /// unlimited) and the allowed clients (null = any).
    /// </summary>
    public JsonObject ToPatch(IEnumerable<string> knownProviderIds)
    {
        var body = new JsonObject
        {
            ["name"] = Name.Trim(),
            ["pool"] = PoolDrafts.ToJson(Pool, knownProviderIds),
            ["limits"] = BudgetRules.LimitsJson(Budgets, Rpm),
        };
        if (Mode == ClientsMode.Any) body["allowedClients"] = null;
        else
        {
            var kinds = new JsonArray();
            foreach (var k in Allowed) kinds.Add((JsonNode?)JsonValue.Create(k));
            body["allowedClients"] = kinds;
        }
        return body;
    }

    /// <summary>The kinds offered when choosing allowed clients: the catalog plus already-chosen kinds outside it (plugin agents stay selectable).</summary>
    public static List<string> ClientChoices(IEnumerable<string> catalog, IEnumerable<string> allowed)
        => catalog.Concat(allowed).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Formats a limit for an input field (invariant, no trailing zeros).</summary>
    public static string LimitText(decimal? limit) => limit?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";
}

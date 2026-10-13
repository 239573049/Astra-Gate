using System.Globalization;
using System.Text.Json.Nodes;

namespace Astra.Core.Alerts;

/// <summary>What an alert is about (plan: alerts-webhooks-token-limits §3).</summary>
public static class AlertSubjectTypes
{
    /// <summary>An API-key provider (its balance query).</summary>
    public const string Provider = "provider";

    /// <summary>A subscription account.</summary>
    public const string Account = "account";

    /// <summary>A gateway token.</summary>
    public const string Token = "token";
}

/// <summary>The alert events. Adding one: a descriptor in <see cref="AlertEvents.All"/>, its evaluation in
/// <see cref="AlertConditions"/>, a message in the server's AlertMessages, i18n <c>alerts.event.*</c> and the docs.</summary>
public static class AlertEventIds
{
    public const string ProviderBalanceLow = "provider.balance.low";
    public const string ProviderQuotaFailed = "provider.quota.failed";
    public const string AccountQuotaHigh = "account.quota.high";
    public const string AccountAuthInvalid = "account.auth.invalid";
    public const string AccountQuotaFailed = "account.quota.failed";
    public const string TokenUsageHigh = "token.usage.high";
    public const string TokenLimitReached = "token.limit.reached";

    /// <summary>Only ever sent by a webhook's test button.</summary>
    public const string Test = "test";
}

/// <summary>One condition parameter of an event: a number, one of <see cref="Choices"/>, or free text.</summary>
public sealed record AlertParamDescriptor(string Name, string Kind, IReadOnlyList<string>? Choices = null, double? Default = null, bool Required = false)
{
    public const string Number = "number";
    public const string Choice = "choice";
    public const string Text = "text";
}

/// <summary>An event the rules can subscribe to; the web UI renders the condition form from <see cref="Params"/>.</summary>
public sealed record AlertEventDescriptor(string Id, string SubjectType, IReadOnlyList<AlertParamDescriptor> Params);

public static class AlertEvents
{
    public static readonly IReadOnlyList<AlertEventDescriptor> All =
    [
        new(AlertEventIds.ProviderBalanceLow, AlertSubjectTypes.Provider,
        [
            new("metric", AlertParamDescriptor.Choice, ["remaining", "usedPercent"]),
            new("value", AlertParamDescriptor.Number, Required: true),
            new("plan", AlertParamDescriptor.Text),
        ]),
        new(AlertEventIds.ProviderQuotaFailed, AlertSubjectTypes.Provider, [new("value", AlertParamDescriptor.Number, Default: 3)]),
        new(AlertEventIds.AccountQuotaHigh, AlertSubjectTypes.Account,
        [
            new("window", AlertParamDescriptor.Choice, ["any", "session", "weekly", "credits"]),
            new("value", AlertParamDescriptor.Number, Default: 90),
        ]),
        new(AlertEventIds.AccountAuthInvalid, AlertSubjectTypes.Account, []),
        new(AlertEventIds.AccountQuotaFailed, AlertSubjectTypes.Account, [new("value", AlertParamDescriptor.Number, Default: 3)]),
        new(AlertEventIds.TokenUsageHigh, AlertSubjectTypes.Token,
        [
            new("budget", AlertParamDescriptor.Text),
            new("value", AlertParamDescriptor.Number, Default: 80),
        ]),
        new(AlertEventIds.TokenLimitReached, AlertSubjectTypes.Token, [new("budget", AlertParamDescriptor.Text)]),
    ];

    public static AlertEventDescriptor? Find(string id) => All.FirstOrDefault(e => e.Id == id);
}

/// <summary>
/// What a subject looks like right now, reported by whoever just learned it (a quota query, the limiter, the sync worker).
/// Observation shapes: provider <c>{ quota }</c> (the stored snapshot); account <c>{ status?, quota?, failures?, error? }</c>;
/// token <c>{ budgets: [{ key, metric, period, used, limit, usedPercent, exhausted, resetsAt }], rpmLimited? }</c>. A field
/// left out means "not known by this report" — rules depending on it keep their state.
/// </summary>
public sealed record AlertSignal(string SubjectType, string SubjectId, string SubjectName, JsonObject Observation);

/// <summary>Where subjects report what they look like; the alert engine decides whether anything fires. Never blocks or throws.</summary>
public interface IAlertSignals
{
    void Report(AlertSignal signal);
}

/// <summary>Outcome of a condition: firing or not, plus the observed figures messages and payloads show.</summary>
public sealed record AlertEvaluation(bool Firing, JsonObject Observed);

/// <summary>Condition validation and evaluation per event (pure: unit-tested, used by the server's alert engine).</summary>
public static class AlertConditions
{
    /// <summary>The condition with defaults filled in; throws <see cref="ArgumentException"/> when it does not fit the event.</summary>
    public static JsonObject Normalize(string eventId, JsonObject? condition)
    {
        var descriptor = AlertEvents.Find(eventId) ?? throw new ArgumentException($"unknown event {eventId}");
        condition ??= [];
        var result = new JsonObject();
        foreach (var p in descriptor.Params)
        {
            var node = condition[p.Name];
            switch (p.Kind)
            {
                case AlertParamDescriptor.Number:
                    var n = Num(node) ?? p.Default;
                    if (n is null)
                    {
                        if (p.Required) throw new ArgumentException($"{p.Name} is required");
                        continue;
                    }
                    if (n < 0) throw new ArgumentException($"{p.Name} must not be negative");
                    result[p.Name] = n;
                    break;
                case AlertParamDescriptor.Choice:
                    var choice = Str(node) ?? p.Choices![0];
                    if (!p.Choices!.Contains(choice)) throw new ArgumentException($"{p.Name} must be one of {string.Join(", ", p.Choices!)}");
                    result[p.Name] = choice;
                    break;
                default:
                    if (Str(node) is { Length: > 0 } text) result[p.Name] = text.Trim();
                    break;
            }
        }
        foreach (var (key, _) in condition)
            if (descriptor.Params.All(p => p.Name != key)) throw new ArgumentException($"unknown condition field {key}");
        return result;
    }

    /// <summary>Evaluates a (normalized) condition; null when the observation does not tell (state is kept).</summary>
    public static AlertEvaluation? Evaluate(string eventId, JsonObject condition, JsonObject observation) => eventId switch
    {
        AlertEventIds.ProviderBalanceLow => Balance(condition, observation["quota"] as JsonObject),
        AlertEventIds.ProviderQuotaFailed => observation["quota"] is JsonObject q ? Failures(condition, q) : null,
        AlertEventIds.AccountQuotaHigh => Window(condition, observation["quota"] as JsonObject),
        AlertEventIds.AccountAuthInvalid => Str(observation["status"]) is { } status
            ? new AlertEvaluation(status != "active", new JsonObject { ["status"] = status })
            : null,
        AlertEventIds.AccountQuotaFailed => observation.ContainsKey("failures") ? Failures(condition, observation) : null,
        AlertEventIds.TokenUsageHigh => TokenUsage(condition, observation),
        AlertEventIds.TokenLimitReached => TokenLimit(condition, observation),
        _ => null,
    };

    private static AlertEvaluation? Balance(JsonObject condition, JsonObject? quota)
    {
        if (quota?["plans"] is not JsonArray plans || quota["isValid"] is JsonValue valid && valid.TryGetValue<bool>(out var ok) && !ok)
            return null;
        var metric = Str(condition["metric"]) ?? "remaining";
        var wanted = Str(condition["plan"]);
        var value = Num(condition["value"]) ?? 0;
        foreach (var plan in plans.OfType<JsonObject>())
        {
            if (wanted is not null && !string.Equals(Str(plan["name"]), wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var figure = metric == "remaining" ? Num(plan["remaining"]) : UsedPercent(plan);
            if (figure is not { } f) continue;
            var observed = new JsonObject
            {
                ["plan"] = Str(plan["name"]),
                ["unit"] = Str(plan["unit"]),
                ["remaining"] = Num(plan["remaining"]),
                ["total"] = Num(plan["total"]),
                ["usedPercent"] = UsedPercent(plan),
            };
            return new AlertEvaluation(metric == "remaining" ? f < value : f >= value, observed);
        }
        return null;
    }

    private static double? UsedPercent(JsonObject plan) =>
        Num(plan["usedPercent"]) ?? (Num(plan["used"]) is { } used && Num(plan["total"]) is { } total && total > 0
            ? Math.Round(used / total * 100, 1)
            : null);

    private static AlertEvaluation Failures(JsonObject condition, JsonObject node)
    {
        var failures = Num(node["failures"]) ?? 0;
        return new AlertEvaluation(failures >= (Num(condition["value"]) ?? 3),
            new JsonObject { ["failures"] = failures, ["error"] = Str(node["error"]) });
    }

    private static AlertEvaluation? Window(JsonObject condition, JsonObject? quota)
    {
        if (quota is null) return null;
        var window = Str(condition["window"]) ?? "any";
        (string Name, double Used, string? Resets)? worst = null;
        foreach (var name in window == "any" ? new[] { "session", "weekly", "credits" } : [window])
        {
            if (quota[name] is not JsonObject w || Num(w["usedPercent"]) is not { } used) continue;
            if (worst is null || used > worst.Value.Used) worst = (name, used, Str(w["resetsAtUtc"]));
        }
        if (worst is not { } found) return null;
        return new AlertEvaluation(found.Used >= (Num(condition["value"]) ?? 90),
            new JsonObject { ["window"] = found.Name, ["usedPercent"] = found.Used, ["resetsAt"] = found.Resets });
    }

    private static IEnumerable<JsonObject> Budgets(JsonObject condition, JsonObject observation)
    {
        var wanted = Str(condition["budget"]);
        return (observation["budgets"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(b => wanted is null || Str(b["key"]) == wanted);
    }

    private static AlertEvaluation? TokenUsage(JsonObject condition, JsonObject observation)
    {
        if (observation["budgets"] is null) return null;
        var worst = Budgets(condition, observation).MaxBy(b => Num(b["usedPercent"]) ?? 0);
        if (worst is null) return null;
        return new AlertEvaluation((Num(worst["usedPercent"]) ?? 0) >= (Num(condition["value"]) ?? 80), worst.DeepClone().AsObject());
    }

    private static AlertEvaluation? TokenLimit(JsonObject condition, JsonObject observation)
    {
        if (observation["budgets"] is null && observation["rpmLimited"] is null) return null;
        var exhausted = Budgets(condition, observation).FirstOrDefault(b => b["exhausted"] is JsonValue v && v.TryGetValue<bool>(out var e) && e);
        if (exhausted is not null) return new AlertEvaluation(true, exhausted.DeepClone().AsObject());
        var rpm = Str(condition["budget"]) is null or "rpm" && observation["rpmLimited"] is JsonValue r && r.TryGetValue<bool>(out var limited) && limited;
        return new AlertEvaluation(rpm, rpm ? new JsonObject { ["key"] = "rpm" } : []);
    }

    public static double? Num(JsonNode? node) => node is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d
        : v.TryGetValue<decimal>(out var m) ? (double)m
        : v.TryGetValue<long>(out var l) ? l
        : v.TryGetValue<int>(out var i) ? i
        : v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p
        : null
        : null;

    public static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

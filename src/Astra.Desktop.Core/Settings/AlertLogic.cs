using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Settings;

/// <summary>An option of a chip / select list: the stored value and what it is called.</summary>
public readonly record struct Option(string Value, string Label);

/// <summary>
/// Settings › Notifications › alert rules (web NotificationSettings <c>RuleSheet</c>): the event descriptors the server reports drive the
/// condition form (<c>alerts.param.*</c>, <c>alerts.choice.*</c>), the subjects an event can be scoped to depend on its subject type, and
/// the rule bodies are built here (linked update 11 in AGENTS.md: a new event is data on the server plus catalog keys).
/// </summary>
public static class AlertLogic
{
    /// <summary>The event's name.</summary>
    public static Msg EventName(string id) => new("alerts.event." + id);

    /// <summary>The event's explanation.</summary>
    public static Msg EventHint(string id) => new("alerts.event." + id + ".hint");

    /// <summary>A condition field's label.</summary>
    public static Msg ParamLabel(string name) => new("alerts.param." + name);

    /// <summary>A text condition field's placeholder.</summary>
    public static Msg ParamPlaceholder(string name) => new("alerts.param." + name + ".placeholder");

    /// <summary>A choice's label.</summary>
    public static Msg ChoiceLabel(string name, string choice) => new($"alerts.choice.{name}.{choice}");

    /// <summary>The subject type's name.</summary>
    public static Msg SubjectLabel(string type) => new("alerts.subject." + type);

    /// <summary>
    /// The condition a new rule of <paramref name="evt"/> starts with: a choice's first option, a number's default (empty when it has none),
    /// a text field empty.
    /// </summary>
    public static Dictionary<string, object?> Defaults(AlertEventDto? evt)
    {
        var c = new Dictionary<string, object?>();
        foreach (var p in evt?.Params ?? [])
            c[p.Name] = p.Kind == "choice" ? p.Choices?.FirstOrDefault() ?? "" : p.Kind == "number" ? (p.Default is { } d ? (decimal)d : null) : "";
        return c;
    }

    /// <summary>The condition as the API wants it: empty values left out, numbers as numbers.</summary>
    public static JsonObject ConditionBody(IReadOnlyDictionary<string, object?> condition)
    {
        var o = new JsonObject();
        foreach (var (key, value) in condition)
        {
            switch (value)
            {
                case null: break;
                case string s when s == "": break;
                case string s: o[key] = s; break;
                case decimal m: o[key] = m == Math.Floor(m) && Math.Abs(m) < 1e15m ? JsonValue.Create((long)m) : JsonValue.Create(m); break;
                case double d: o[key] = JsonValue.Create(d); break;
                case long l: o[key] = JsonValue.Create(l); break;
                case int i: o[key] = JsonValue.Create(i); break;
            }
        }
        return o;
    }

    /// <summary>Reads a stored condition value into a form value (number or text).</summary>
    public static object? FromNode(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s;
        if (v.TryGetValue<long>(out var l)) return (decimal)l;
        if (v.TryGetValue<int>(out var i)) return (decimal)i;
        if (v.TryGetValue<decimal>(out var m)) return m;
        if (v.TryGetValue<double>(out var d)) return (decimal)d;
        return null;
    }

    /// <summary>
    /// The options of "only these subjects": providers with a balance query (not subscription / plugin ones), subscription accounts as
    /// "provider / account", or tokens.
    /// </summary>
    public static IReadOnlyList<Option> SubjectOptions(
        string? subjectType, JsonArray providers, IReadOnlyList<TokenDto> tokens, IReadOnlyDictionary<string, List<ProviderAccountDto>> accounts)
    {
        switch (subjectType)
        {
            case "provider":
                return providers.OfType<JsonObject>()
                    .Where(p => Str(p, "authScheme") is not ("oauth-subscription" or "plugin"))
                    .Select(p => new Option(Str(p, "id") ?? "", Str(p, "name") ?? Str(p, "id") ?? "")).ToList();
            case "token":
                return tokens.Select(t => new Option(t.Id, t.Name)).ToList();
            case "account":
                return SubscriptionProviders(providers).SelectMany(p =>
                        accounts.TryGetValue(p.Id, out var list)
                            ? list.Select(a => new Option(a.Id, $"{p.Name} / {(string.IsNullOrEmpty(a.DisplayName) ? a.Id : a.DisplayName)}"))
                            : [])
                    .ToList();
            default:
                return [];
        }
    }

    /// <summary>The subscription providers (the ones whose accounts a rule can name).</summary>
    public static IReadOnlyList<(string Id, string Name)> SubscriptionProviders(JsonArray providers)
        => providers.OfType<JsonObject>().Where(p => Str(p, "authScheme") == "oauth-subscription")
            .Select(p => (Str(p, "id") ?? "", Str(p, "name") ?? Str(p, "id") ?? "")).ToList();

    /// <summary>The chips of a subject list: the options plus any saved subject that is no longer an option (shown by its id).</summary>
    public static IReadOnlyList<Option> WithOrphans(IReadOnlyList<Option> options, IEnumerable<string> selected)
        => options.Concat(selected.Where(s => options.All(o => o.Value != s)).Select(s => new Option(s, s))).ToList();

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>The rule editor's state: one rule being created or edited.</summary>
public sealed class RuleDraft
{
    private readonly AlertRuleDto? _rule;

    /// <summary>Starts a form for <paramref name="rule"/> (null = new) over the server's event descriptors and the existing webhooks.</summary>
    public RuleDraft(AlertRuleDto? rule, IReadOnlyList<AlertEventDto> events, IReadOnlyList<WebhookDto> webhooks)
    {
        _rule = rule;
        var first = events.FirstOrDefault();
        Name = rule?.Name ?? "";
        EventId = rule?.Event ?? first?.Id ?? "";
        Condition = rule is null ? AlertLogic.Defaults(first) : AlertLogic.Defaults(events.FirstOrDefault(e => e.Id == rule.Event));
        if (rule is not null)
            foreach (var (key, node) in rule.Condition) Condition[key] = AlertLogic.FromNode(node);
        OnlySelected = rule?.Subjects is not null;
        Subjects = [.. rule?.Subjects ?? []];
        WebhookIds = [.. rule?.WebhookIds ?? webhooks.Select(w => w.Id)];
        NotifyResolved = rule?.NotifyResolved ?? false;
        Repeat = rule?.RepeatMinutes;
        Cooldown = rule?.CooldownMinutes ?? 60;
    }

    /// <summary>Whether the form creates a rule.</summary>
    public bool IsNew => _rule is null;

    /// <summary>The rule being edited.</summary>
    public AlertRuleDto? Rule => _rule;

    /// <summary>Rule name.</summary>
    public string Name { get; set; }

    /// <summary>The event the rule watches.</summary>
    public string EventId { get; private set; }

    /// <summary>The event's parameters (number / choice / text values).</summary>
    public Dictionary<string, object?> Condition { get; private set; }

    /// <summary>Scoped to the listed subjects instead of every one of the type.</summary>
    public bool OnlySelected { get; set; }

    /// <summary>The listed subject ids.</summary>
    public List<string> Subjects { get; }

    /// <summary>The webhooks that receive it.</summary>
    public List<string> WebhookIds { get; }

    /// <summary>Also notify when it recovers.</summary>
    public bool NotifyResolved { get; set; }

    /// <summary>Repeat the notification every N minutes while firing (null = once).</summary>
    public decimal? Repeat { get; set; }

    /// <summary>Quiet minutes after a notification.</summary>
    public decimal? Cooldown { get; set; }

    /// <summary>Picks another event: the condition restarts from its defaults and the subject list is cleared.</summary>
    public void ChangeEvent(string id, IReadOnlyList<AlertEventDto> events)
    {
        EventId = id;
        Condition = AlertLogic.Defaults(events.FirstOrDefault(e => e.Id == id));
        Subjects.Clear();
    }

    /// <summary>Whether Save is enabled.</summary>
    public bool CanSave => Name.Trim() != "" && EventId != "";

    /// <summary>The request body (create and update share it).</summary>
    public JsonObject Body()
    {
        var o = JsonPatch.Of(
            ("name", Name.Trim()), ("event", EventId), ("notifyResolved", NotifyResolved),
            ("repeatMinutes", Repeat is { } r && r > 0 ? (long)Math.Floor(r) : (long?)null),
            ("cooldownMinutes", (long)Math.Max(0, Math.Floor(Cooldown ?? 0))));
        o["condition"] = AlertLogic.ConditionBody(Condition);
        o["subjects"] = OnlySelected ? JsonPatch.Strings(Subjects) : null;
        o["webhookIds"] = JsonPatch.Strings(WebhookIds);
        return o;
    }
}

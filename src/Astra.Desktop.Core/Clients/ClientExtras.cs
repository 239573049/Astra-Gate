using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Clients;

namespace Astra.Desktop.Core.Clients;

/// <summary>
/// One option value as stored in <c>extras.options</c> (web <c>ClientOptionValues</c>): a boolean, a positive integer or an enum
/// string. A closed set of cases instead of <c>object</c>, so the JSON written for it needs no reflection.
/// </summary>
public abstract record OptionValue
{
    /// <summary>A switch that is on (off is "absent").</summary>
    public sealed record Bool(bool Value) : OptionValue;

    /// <summary>A positive integer.</summary>
    public sealed record Int(long Value) : OptionValue;

    /// <summary>An enum choice other than "default".</summary>
    public sealed record Choice(string Value) : OptionValue;

    /// <summary>The value as JSON.</summary>
    public JsonNode ToJson() => this switch
    {
        Bool b => JsonValue.Create(b.Value),
        Int i => JsonValue.Create(i.Value),
        Choice c => JsonValue.Create(c.Value),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>The text of the comparison key (JS <c>`${value}`</c>).</summary>
    public string KeyText => this switch
    {
        Bool b => b.Value ? "true" : "false",
        Int i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Choice c => c.Value,
        _ => "",
    };
}

/// <summary>One model a client lists (extras.models), with the protocols the gateway passes it through in, preferred first (web <c>ProtocolModel</c>).</summary>
public sealed record ProtocolModel(string Id, string Name, IReadOnlyList<string> Native);

/// <summary>
/// The parts of a client's <c>extras</c> the panel edits, ported one-for-one from web/src/lib: <c>clientOptions.ts</c> (option values),
/// <c>clientProtocols.ts</c> (per-model protocol overrides), <c>modelSlots.ts</c> (Claude Code model tiers) and <c>roleMap.ts</c>
/// (Claude Desktop roles). Maps are plain dictionaries; each has a stable comparison key like the web's <c>…Key</c> helpers.
/// </summary>
public static class ClientExtras
{
    // ---------- options (clientOptions.ts) ----------

    /// <summary>The known, non-default options of <c>extras.options</c> (web <c>clientOptionsOf</c>).</summary>
    public static Dictionary<string, OptionValue> OptionsOf(JsonObject? extras, IEnumerable<ClientOptionDescriptor>? descriptors)
    {
        var result = new Dictionary<string, OptionValue>(StringComparer.Ordinal);
        if (extras?["options"] is not JsonObject raw) return result;
        foreach (var d in descriptors ?? [])
        {
            var v = raw[d.Key];
            if (d.Type == "bool" && v is JsonValue b && b.GetValueKind() == JsonValueKind.True) result[d.Key] = new OptionValue.Bool(true);
            else if (d.Type == "int" && v is JsonValue n && n.GetValueKind() == JsonValueKind.Number && n.TryGetValue<double>(out var num) && num > 0)
                result[d.Key] = new OptionValue.Int((long)num);
            else if (d.Type == "enum" && v is JsonValue s && s.GetValueKind() == JsonValueKind.String && s.GetValue<string>() is var text
                     && text != "default" && d.Choices?.Contains(text) == true)
                result[d.Key] = new OptionValue.Choice(text);
        }
        return result;
    }

    /// <summary>
    /// Sets one option (web <c>ClientOptionsGroup</c> <c>set</c>): null, false, 0, "default" and "" remove it (the default needs no entry).
    /// Returns a new map.
    /// </summary>
    public static Dictionary<string, OptionValue> SetOption(IReadOnlyDictionary<string, OptionValue> values, string key, OptionValue? value)
    {
        var next = new Dictionary<string, OptionValue>(values, StringComparer.Ordinal);
        var clear = value switch
        {
            null => true,
            OptionValue.Bool b => !b.Value,
            OptionValue.Int i => i.Value == 0,
            OptionValue.Choice c => c.Value is "default" or "",
            _ => true,
        };
        if (clear) next.Remove(key);
        else next[key] = value!;
        return next;
    }

    /// <summary>The integer typed into an int option: its digits only (web <c>replace(/\D/g, '')</c>), null when there are none.</summary>
    public static long? ParseIntOption(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0) return null;
        return long.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : long.MaxValue;
    }

    /// <summary>Stable comparison key of an option map (web <c>clientOptionsKey</c>).</summary>
    public static string OptionsKey(IReadOnlyDictionary<string, OptionValue> o)
        => string.Join("\n", o.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + o[k].KeyText));

    /// <summary>An option map as the JSON object stored in <c>extras.options</c>.</summary>
    public static JsonObject OptionsJson(IReadOnlyDictionary<string, OptionValue> o)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in o) obj[k] = v.ToJson();
        return obj;
    }

    // ---------- protocols (clientProtocols.ts) ----------

    /// <summary>The wire protocol ids in the web's order.</summary>
    public static readonly IReadOnlyList<string> Protocols = ["openai-chat", "openai-responses", "anthropic", "gemini"];

    private static bool IsProtocol(JsonNode? v) => v is JsonValue s && s.GetValueKind() == JsonValueKind.String && Protocols.Contains(s.GetValue<string>());

    /// <summary>The user's per-model protocol choices (<c>extras.protocolOverrides</c>); unknown values are dropped (web <c>protocolOverridesOf</c>).</summary>
    public static Dictionary<string, string> OverridesOf(JsonObject? extras)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extras?["protocolOverrides"] is not JsonObject raw) return result;
        foreach (var (model, value) in raw)
            if (IsProtocol(value)) result[model] = value!.GetValue<string>();
        return result;
    }

    /// <summary>The models of <c>extras.models</c>, in order, without duplicates (web <c>protocolModelsOf</c>).</summary>
    public static List<ProtocolModel> ProtocolModelsOf(JsonObject? extras)
    {
        var result = new List<ProtocolModel>();
        if (extras?["models"] is not JsonObject raw) return result;
        foreach (var (_, value) in raw)
        {
            if (value is not JsonObject m) continue;
            var id = m["id"] is JsonValue iv && iv.GetValueKind() == JsonValueKind.String ? iv.GetValue<string>() : null;
            if (string.IsNullOrEmpty(id) || result.Any(x => x.Id == id)) continue;
            var name = m["name"] is JsonValue nv && nv.GetValueKind() == JsonValueKind.String && nv.GetValue<string>() is { Length: > 0 } n ? n : id;
            var native = m["protocols"] is JsonArray arr ? arr.Where(IsProtocol).Select(p => p!.GetValue<string>()).ToList() : [];
            result.Add(new ProtocolModel(id, name, native));
        }
        return result;
    }

    /// <summary>
    /// The protocol a model gets without a choice of its own (server <c>ClientAdapterBase.ModelProtocols</c>, web <c>autoProtocol</c>): its first
    /// native protocol the client speaks; otherwise the client's default, which the gateway translates.
    /// </summary>
    public static (string Protocol, bool Translated) AutoProtocol(ProtocolModel model, IReadOnlyList<string> supported)
    {
        var native = model.Native.FirstOrDefault(supported.Contains);
        return native is not null ? (native, false) : (supported.Count > 0 ? supported[0] : "openai-chat", true);
    }

    /// <summary>Sets one model's protocol: "auto" removes the override (web <c>ClientProtocolsGroup</c> <c>set</c>). Returns a new map.</summary>
    public static Dictionary<string, string> SetOverride(IReadOnlyDictionary<string, string> values, string model, string value)
    {
        var next = new Dictionary<string, string>(values, StringComparer.Ordinal);
        if (value == AutoValue) next.Remove(model);
        else next[model] = value;
        return next;
    }

    /// <summary>The select value meaning "no override".</summary>
    public const string AutoValue = "auto";

    /// <summary>Stable comparison key of an override map (web <c>protocolOverridesKey</c>).</summary>
    public static string OverridesKey(IReadOnlyDictionary<string, string> o)
        => string.Join("\n", o.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + o[k]));

    /// <summary>An override map as JSON.</summary>
    public static JsonObject OverridesJson(IReadOnlyDictionary<string, string> o)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in o) obj[k] = v;
        return obj;
    }

    // ---------- Claude Code model slots (modelSlots.ts) ----------

    /// <summary>Claude Code model slots the console writes into ~/.claude/settings.json (plan §7.3, web <c>CLAUDE_CODE_MODELS</c>).</summary>
    public static readonly IReadOnlyList<string> ClaudeCodeModels = ["ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL"];

    /// <summary>The non-empty, known slots of <c>extras.models</c>, trimmed (web <c>modelSlotsOf</c>).</summary>
    public static Dictionary<string, string> SlotsOf(JsonObject? extras) => StringMap(extras?["models"] as JsonObject, ClaudeCodeModels);

    /// <summary>Stable comparison key of a slot map (web <c>modelSlotsKey</c>).</summary>
    public static string SlotsKey(IReadOnlyDictionary<string, string> m) => string.Join("\n", ClaudeCodeModels.Select(s => m.GetValueOrDefault(s) ?? ""));

    // ---------- Claude Desktop roles (roleMap.ts) ----------

    /// <summary>Claude Desktop role ids the gateway maps to provider models (plan §7.5, web <c>ROLES</c>).</summary>
    public static readonly IReadOnlyList<string> Roles = ["sonnet", "opus", "haiku"];

    /// <summary>The non-empty, known roles of <c>extras.roleMap</c>, trimmed (web <c>rolesOf</c>).</summary>
    public static Dictionary<string, string> RolesOf(JsonObject? extras) => StringMap(extras?["roleMap"] as JsonObject, Roles);

    /// <summary>Stable comparison key of a role map (web <c>roleKey</c>).</summary>
    public static string RoleKey(IReadOnlyDictionary<string, string> m) => string.Join("\n", Roles.Select(r => m.GetValueOrDefault(r) ?? ""));

    /// <summary>Whether any role is set.</summary>
    public static bool HasRole(IReadOnlyDictionary<string, string> m) => Roles.Any(r => !string.IsNullOrEmpty(m.GetValueOrDefault(r)));

    private static readonly Regex SmallModel = new(@"(^|[-_.:/\s])(haiku|flash|mini|lite|nano|small|fast|air)($|[-_.:/\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A role map suggested from a provider's models (web <c>suggestRoles</c>): the model named after the role when there is one, else the
    /// first full-size model for sonnet and opus and the first small one (flash, mini, air …) for haiku. Empty without models.
    /// </summary>
    public static Dictionary<string, string> SuggestRoles(IReadOnlyList<string> models)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (models.Count == 0) return result;
        string? Named(string r) => models.FirstOrDefault(m => m.Contains(r, StringComparison.OrdinalIgnoreCase));
        var large = models.FirstOrDefault(m => !SmallModel.IsMatch(m)) ?? models[0];
        var small = models.FirstOrDefault(m => SmallModel.IsMatch(m)) ?? large;
        result["sonnet"] = Named("sonnet") ?? large;
        result["opus"] = Named("opus") ?? Named("sonnet") ?? large;
        result["haiku"] = Named("haiku") ?? small;
        return result;
    }

    /// <summary>A string map as JSON (slots, roles).</summary>
    public static JsonObject StringJson(IReadOnlyDictionary<string, string> m)
    {
        var obj = new JsonObject();
        foreach (var (k, v) in m) obj[k] = v;
        return obj;
    }

    private static Dictionary<string, string> StringMap(JsonObject? raw, IReadOnlyList<string> keys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (raw is null) return result;
        foreach (var k in keys)
            if (raw[k] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Trim() is { Length: > 0 } s)
                result[k] = s;
        return result;
    }
}

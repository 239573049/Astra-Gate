using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Adapters;

/// <summary>
/// ZCode (zcode.z.ai, Zhipu's Electron coding agent; Coexist): adds personal providers to
/// <c>~/.zcode/v2/provider_config.json</c> (the home is <c>ZCODE_DESKTOP_HOME_DIR</c>, else the user's home, as ZCode resolves
/// it). That file holds the providers the user added in ZCode's settings, with plaintext keys; ZCode polls it every second and
/// reloads it, so the entries appear without a restart. ZCode's own (Zhipu account / Coding Plan) providers live elsewhere and
/// are left alone. Verified against ZCode 3.15.3's host bundle (<c>NodePersonalProviderConfigRepository</c> and its zod
/// schemas), 2026-10.
/// <para>
/// The file is <c>{"schemaVersion":1,"config":{"providerOrder"?, "providerConfigRules":{"providerRules":[…]},
/// "modelConfigRules":{"providerModelRules":[…],"manualProviderModelRules":[…]}}}</c>, parsed strictly (<c>JSON.parse</c>, then
/// strict schemas): a file that fails is read as empty, so every value here uses only fields those schemas know. A rule is
/// <c>{"providerId","providerName","enabled","config":{"group":"standard-personal","access":{"type":"api-key","apiKey"},
/// "api":{"type","baseUrl"},"personalModelIds","modelOrder"}}</c>. A provider speaks one <c>api.type</c>
/// (<c>openai-chat-completions</c> / <c>openai-responses</c> take <c>…/v1</c> and add <c>/chat/completions</c> /
/// <c>/responses</c>; <c>anthropic-messages</c> takes the gateway root and adds <c>/v1/messages</c>), so models go to one rule
/// per protocol — <c>astra</c>, <c>astra-responses</c>, <c>astra-anthropic</c> — and the last two exist only while they have
/// models. Each model gets a <c>providerModelRules</c> entry with what Astra knows of it (context window, image / PDF input)
/// and <c>supportsNativeWebSearch: false</c>, since a provider-side search tool is not something the gateway serves.
/// <c>providerOrder</c> is left alone (ZCode lists providers it does not name after the ones it does), and so is the default
/// model, which ZCode keeps per task: Astra's models are picked in ZCode's model picker.
/// </para>
/// <para>
/// ZCode imports its older <c>config.json</c> providers only while <c>provider_config.json</c> does not exist, so Astra never
/// creates the file: enabling asks for ZCode to have been started once. ZCode rewrites the whole file in its canonical form
/// whenever it reads or saves it, and its settings add fields to the rules (the provider's <c>enabled</c> switch, a dragged
/// <c>modelOrder</c>, a model's <c>enabled</c>): Astra owns only the fields it writes (<see cref="OwnedPartsEqual"/>), carries
/// ZCode's additions over when it rewrites a rule, and does not count them as drift.
/// </para>
/// </summary>
public sealed class ZCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private const string ProviderRulesPath = "config.providerConfigRules.providerRules";

    private const string ModelRulesPath = "config.modelConfigRules.providerModelRules";

    /// <summary>Fields of a provider rule that ZCode's settings change; Astra writes them but does not own them.</summary>
    private static readonly string[] ZCodeOwnedRuleFields = ["enabled", "config.modelOrder"];

    /// <summary>The providers Astra writes, one per protocol: id, ZCode <c>api.type</c> and display name.</summary>
    private static readonly (ApiProtocol Protocol, string Id, string ApiType, string Name)[] Providers =
    [
        (ApiProtocol.OpenAIChat, ProviderId, "openai-chat-completions", "Astra"),
        (ApiProtocol.OpenAIResponses, ProviderId + "-responses", "openai-responses", "Astra (Responses)"),
        (ApiProtocol.Anthropic, ProviderId + "-anthropic", "anthropic-messages", "Astra (Anthropic)"),
    ];

    private string DataDir => Path.Combine(
        Env.GetEnvironmentVariable("ZCODE_DESKTOP_HOME_DIR") is { Length: > 0 } home ? home : Env.Combine(), ".zcode", "v2");

    private string ConfigFile => Path.Combine(DataDir, "provider_config.json");

    public override string Kind => ClientKinds.ZCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect() => Env.DirectoryExists(DataDir)
        ? ClientDetection.Found(null, $"data directory {DataDir}")
        : ClientDetection.NotFound($"no {DataDir}");

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    /// <summary>ZCode reads the file with <c>JSON.parse</c>: a comment or trailing comma makes it an empty config.</summary>
    protected override IEnumerable<string> StrictJsonFiles() => [ConfigFile];

    private static string RulePath(string providerId) => $"{ProviderRulesPath}.{JsoncEditor.Selector(("providerId", providerId))}";

    private static string ModelRulePath(string providerId, string modelId) =>
        $"{ModelRulesPath}.{JsoncEditor.Selector(("providerId", providerId), ("modelId", modelId))}";

    private static bool IsOurs(string keyPath) =>
        keyPath.StartsWith(ProviderRulesPath + ".", StringComparison.Ordinal) || keyPath.StartsWith(ModelRulesPath + ".", StringComparison.Ordinal);

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        if (!Env.FileExists(ConfigFile))
            throw new EditorException($"ZCode has not created {ConfigFile} yet; start ZCode once, then enable it here.");
        var root = ctx.GatewayBaseUrl.TrimEnd('/');
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var entries = ModelEntries(ctx);
        var hints = ModelHints(ctx);
        var changes = new List<ConfigChange>();
        var written = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (protocol, id, apiType, name) in Providers)
        {
            var own = entries.Where(e => protocols[e.Id] == protocol).Select(e => e.Id).ToList();
            if (protocol != ApiProtocol.OpenAIChat && own.Count == 0) continue;
            var ids = new JsonArray([.. own.Select(m => (JsonNode)m)]);
            var config = new JsonObject
            {
                ["group"] = "standard-personal",
                ["access"] = new JsonObject { ["type"] = "api-key", ["apiKey"] = ctx.LocalKey },
                ["api"] = new JsonObject { ["type"] = apiType, ["baseUrl"] = protocol == ApiProtocol.Anthropic ? root : root + "/v1" },
            };
            if (own.Count > 0)
            {
                config["personalModelIds"] = ids;
                config["modelOrder"] = ids.DeepClone();
            }
            var rule = new JsonObject { ["providerId"] = id, ["providerName"] = name, ["enabled"] = true, ["config"] = config };
            changes.Add(Change(RulePath(id), rule, ZCodeOwnedRuleFields));
            written.Add(RulePath(id));
            foreach (var model in own)
            {
                if (!SelectorSafe(model)) continue;
                hints.TryGetValue(model, out var info);
                var path = ModelRulePath(id, model);
                changes.Add(Change(path, ModelRule(id, model, info), []));
                written.Add(path);
            }
        }
        // Rules Astra wrote before that are not needed any more (a model left the provider, a protocol has no models now).
        foreach (var entry in Store.List(Kind))
        {
            if (entry.FilePath != ConfigFile || !IsOurs(entry.KeyPath) || written.Contains(entry.KeyPath)) continue;
            if (CurrentValue(ConfigFile, ConfigFileFormat.Json, entry.KeyPath) is { } before)
                changes.Add(Removal(entry.KeyPath, before));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var entry in Store.List(Kind))
        {
            if (entry.FilePath != ConfigFile || !IsOurs(entry.KeyPath)) continue;
            if (CurrentValue(ConfigFile, ConfigFileFormat.Json, entry.KeyPath) is { } before)
                changes.Add(Removal(entry.KeyPath, before));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), StillOurs(ConfigFile, ConfigFileFormat.Json, RulePath(ProviderId)), []);

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        file == ConfigFile && IsOurs(keyPath)
            ? OwnedPartsEqual(current, appliedJson, ExcludedFor(keyPath))
            : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    private static string[] ExcludedFor(string keyPath) =>
        keyPath.StartsWith(ProviderRulesPath + ".", StringComparison.Ordinal) ? ZCodeOwnedRuleFields : [];

    /// <summary>
    /// The change that writes <paramref name="next"/> at <paramref name="path"/>. When Astra wrote the value before, what ZCode
    /// added to it since (and the fields in <paramref name="zcodeFields"/>) is carried over.
    /// </summary>
    private ConfigChange Change(string path, JsonObject next, string[] zcodeFields)
    {
        var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, path);
        if (Store.Get(Kind, ConfigFile, path) is { } state && Parse(before) is { } current)
        {
            CarryOver(current, Parse(state.AppliedValueJson), next, zcodeFields, "");
            if (next["config"] is JsonObject config)
            {
                if (config["personalModelIds"] is not JsonArray ids) config.Remove("modelOrder");
                else if (current["config"]?["modelOrder"] is JsonArray order) config["modelOrder"] = MergedOrder(order, ids);
            }
        }
        return new ConfigChange(ConfigFile, ConfigFileFormat.Json, path, before, next.ToJsonString())
        {
            StillOurs = (now, applied) => OwnedPartsEqual(now, applied, zcodeFields),
        };
    }

    private ConfigChange Removal(string path, string before) =>
        new(ConfigFile, ConfigFileFormat.Json, path, before, null)
        {
            StillOurs = (now, applied) => OwnedPartsEqual(now, applied, ExcludedFor(path)),
        };

    /// <summary>A model's <c>providerModelRules</c> entry, in ZCode's own key order.</summary>
    private static JsonObject ModelRule(string providerId, string modelId, JsonObject? info)
    {
        var properties = new JsonObject();
        if (PositiveLong(info?["contextWindow"]) is { } context) properties["contextWindow"] = context;
        var input = new JsonObject();
        if (BoolHint(info?["vision"]) is { } vision) input["supportsImage"] = vision;
        if (BoolHint(info?["pdf"]) is { } pdf) input["supportsPdf"] = pdf;
        if (input.Count > 0) properties["inputFormat"] = input;
        properties["supportsNativeWebSearch"] = false;
        return new JsonObject
        {
            ["modelId"] = modelId,
            ["config"] = new JsonObject { ["properties"] = properties },
            ["providerId"] = providerId,
        };
    }

    /// <summary>A model id that an array selector can address (ids holding '[', ']', '=' or '&amp;' get no model rule).</summary>
    private static bool SelectorSafe(string modelId) => modelId.IndexOfAny(['[', ']', '=', '&']) < 0;

    /// <summary>
    /// Whether <paramref name="currentText"/> still holds every value Astra wrote (<paramref name="appliedJson"/>) outside the
    /// <paramref name="excluded"/> paths; fields ZCode added are not compared. Arrays count as single values.
    /// </summary>
    private static bool OwnedPartsEqual(string? currentText, string? appliedJson, string[] excluded)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        return Parse(currentText) is { } current && Parse(appliedJson) is { } applied && Covers(current, applied, excluded, "");
    }

    private static bool Covers(JsonObject current, JsonObject applied, string[] excluded, string prefix)
    {
        foreach (var (key, value) in applied)
        {
            var path = prefix + key;
            if (excluded.Contains(path)) continue;
            if (value is JsonObject inner)
            {
                if (current[key] is not JsonObject now || !Covers(now, inner, excluded, path + ".")) return false;
            }
            else if (!JsonNode.DeepEquals(value, current[key])) return false;
        }
        return true;
    }

    /// <summary>
    /// Copies into <paramref name="next"/> what the current value holds that Astra did not write last time (ZCode's additions),
    /// and the current value of the <paramref name="zcodeFields"/> paths; a field Astra wrote before and no longer writes goes.
    /// </summary>
    private static void CarryOver(JsonObject current, JsonObject? previous, JsonObject next, string[] zcodeFields, string prefix)
    {
        foreach (var (key, value) in current)
        {
            var path = prefix + key;
            if (zcodeFields.Contains(path))
            {
                if (value is not null) next[key] = value.DeepClone();
                continue;
            }
            if (next[key] is JsonObject nextInner && value is JsonObject currentInner)
                CarryOver(currentInner, previous?[key] as JsonObject, nextInner, zcodeFields, path + ".");
            else if (!next.ContainsKey(key) && previous?.ContainsKey(key) != true)
                next[key] = value?.DeepClone();
        }
    }

    /// <summary>ZCode's model order kept for the models still listed, then the new ones in Astra's order.</summary>
    private static JsonArray MergedOrder(JsonArray order, JsonArray ids)
    {
        var listed = ids.Select(n => n?.GetValue<string>()).OfType<string>().ToList();
        var kept = order.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null).OfType<string>()
            .Where(listed.Contains).Distinct().ToList();
        return new JsonArray([.. kept.Concat(listed.Where(id => !kept.Contains(id))).Select(id => (JsonNode)id)]);
    }

    private static JsonObject? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

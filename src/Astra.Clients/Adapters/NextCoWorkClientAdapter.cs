using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// NextCoWork (nextco.work, Electron desktop agent; Coexist): adds one provider, <c>providers.astra</c>, to the user-level
/// <c>~/.next-cowork/providers.json</c>. That file holds the providers the user added themselves (plus their models and
/// plaintext keys) and NextCoWork hot-reloads it, so the entry appears in its settings without a restart; NextCoWork's
/// own providers are left alone. NextCoWork builds before the file existed ignore it and keep providers in their database.
/// <para>
/// The entry is <c>{"name":"Astra","protocol":"openai-chat","baseUrl":"&lt;gateway&gt;/v1","apiKey":…,"models":{"&lt;id&gt;":
/// {"upstreamModel":…, "protocolOverride":…}}}</c>: for <c>openai-chat</c> NextCoWork expects the version segment inside
/// <c>baseUrl</c> and appends <c>/chat/completions</c> itself; a model whose native protocol is Responses or Anthropic carries
/// a per-model <c>protocolOverride</c> (see <see cref="ModelsJson"/>). <c>priority</c> and <c>enabled</c> are left out so they take NextCoWork's defaults (50,
/// enabled) and the entry stays exactly what Astra wrote until the user edits it in NextCoWork. The whole provider is one
/// state key, so a refreshed model list replaces the old one wholesale. NextCoWork keeps the default model in its
/// database, so Astra cannot select one — the selected model is only guaranteed to be in the list.
/// </para>
/// <para>
/// The entry is shared with NextCoWork: editing a model in its UI, or syncing its model catalog, writes the whole resolved
/// alias back (thinking settings, the full capability set, <c>catalogOverrides</c>, <c>source</c> …), and editing the provider
/// adds <c>priority</c> / <c>enabled</c>. Astra owns only what it writes — the provider's <c>name</c>, <c>protocol</c>,
/// <c>baseUrl</c>, <c>apiKey</c>, the set of models and, per model, the fields and capabilities it put there
/// (<see cref="ValueStillOurs"/>); everything else is NextCoWork's and is carried over when Astra rewrites the entry. A
/// <c>protocolOverride</c> the user changes in NextCoWork is their choice: <see cref="ChangedProtocolChoices"/> reports it
/// so the server records it with the client's protocol overrides.
/// </para>
/// </summary>
public sealed class NextCoWorkClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store), IClientProtocolChoices
{
    /// <summary>Provider fields Astra writes and owns; any other provider field is NextCoWork's.</summary>
    private static readonly string[] OwnedProviderFields = ["name", "protocol", "baseUrl", "apiKey"];

    /// <summary>Model fields Astra may write; <c>protocolOverride</c> is shared (see <see cref="ChangedProtocolChoices"/>).</summary>
    private static readonly string[] OwnedModelFields = ["upstreamModel", "displayName", "contextWindow", "maxOutputTokens"];

    private const string ProtocolOverrideField = "protocolOverride";

    public const string ProviderId = "astra";

    private string ConfigDir => Env.Combine(".next-cowork");

    private string ConfigFile => Path.Combine(ConfigDir, "providers.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.NextCoWork;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect() => Env.DirectoryExists(ConfigDir)
        ? ClientDetection.Found(null, $"config directory {ConfigDir}")
        : ClientDetection.NotFound($"no {ConfigDir}");

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["protocol"] = "openai-chat",
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["apiKey"] = ctx.LocalKey,
        };
        var models = ModelsJson(ctx);
        if (models.Count > 0) provider["models"] = models;
        var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        // What NextCoWork added to the entry Astra wrote stays; a provider Astra never wrote is replaced as before.
        if (Store.Get(Kind, ConfigFile, ProviderPath) is not null && Parse(before) is { } current) CarryOver(current, provider);
        return BuildPlan([new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, before, provider.ToJsonString())
        {
            StillOurs = (now, applied) => OwnedPartsEqual(now, applied),
        }]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildPlan(before is null ? [] : [new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, before, null)
        {
            StillOurs = (now, applied) => OwnedPartsEqual(now, applied),
        }]);
    }

    public IReadOnlyDictionary<string, ApiProtocol> ChangedProtocolChoices()
    {
        var changed = new Dictionary<string, ApiProtocol>(StringComparer.Ordinal);
        if (Store.Get(Kind, ConfigFile, ProviderPath) is not { AppliedValueJson: { } appliedJson }
            || Parse(appliedJson) is not { } applied
            || Parse(CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath)) is not { } current
            || current["models"] is not JsonObject models)
            return changed;
        foreach (var (id, node) in models)
        {
            if (node is not JsonObject model || applied["models"]?[id] is not JsonObject written) continue;
            var now = Text(model[ProtocolOverrideField]) ?? Text(current["protocol"]);
            if (now != (Text(written[ProtocolOverrideField]) ?? Text(applied["protocol"])) && ApiProtocols.TryParse(now, out var protocol))
                changed[id] = protocol;
        }
        return changed;
    }

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        file == ConfigFile && keyPath == ProviderPath
            ? OwnedPartsEqual(current, appliedJson)
            : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    /// <summary>
    /// Whether the entry still holds what Astra wrote in the parts Astra owns: the provider fields it wrote, the same set of
    /// models, and per model the fields and capabilities it wrote. NextCoWork's additions and <c>protocolOverride</c> are
    /// not compared.
    /// </summary>
    private static bool OwnedPartsEqual(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (Parse(currentText) is not { } current || Parse(appliedJson) is not { } applied) return false;
        foreach (var field in OwnedProviderFields)
            if (applied.ContainsKey(field) && !JsonNode.DeepEquals(applied[field], current[field])) return false;
        var appliedModels = applied["models"] as JsonObject ?? [];
        var currentModels = current["models"] as JsonObject ?? [];
        if (appliedModels.Count != currentModels.Count) return false;
        foreach (var (id, node) in appliedModels)
        {
            if (node is not JsonObject written || currentModels[id] is not JsonObject now) return false;
            foreach (var field in OwnedModelFields)
                if (written.ContainsKey(field) && !JsonNode.DeepEquals(written[field], now[field])) return false;
            if (written["capabilities"] is JsonObject capabilities)
                foreach (var (key, value) in capabilities)
                    if (!JsonNode.DeepEquals(value, now["capabilities"]?[key])) return false;
        }
        return true;
    }

    /// <summary>Copies what NextCoWork added to the current entry (provider and per-model fields Astra does not own) into the new one.</summary>
    private static void CarryOver(JsonObject current, JsonObject next)
    {
        foreach (var (key, value) in current)
            if (key != "models" && !OwnedProviderFields.Contains(key) && !next.ContainsKey(key)) next[key] = value?.DeepClone();
        if (current["models"] is not JsonObject currentModels || next["models"] is not JsonObject nextModels) return;
        foreach (var (id, node) in nextModels)
        {
            if (node is not JsonObject model || currentModels[id] is not JsonObject old) continue;
            foreach (var (key, value) in old)
            {
                if (OwnedModelFields.Contains(key) || key is ProtocolOverrideField || model.ContainsKey(key) && key != "capabilities") continue;
                if (key == "capabilities")
                {
                    if (value is not JsonObject oldCapabilities) continue;
                    var capabilities = model["capabilities"] as JsonObject ?? (JsonObject)(model["capabilities"] = new JsonObject())!;
                    foreach (var (name, flag) in oldCapabilities)
                        if (!capabilities.ContainsKey(name)) capabilities[name] = flag?.DeepClone();
                    continue;
                }
                model[key] = value?.DeepClone();
            }
        }
    }

    private static JsonObject? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json, documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>
    /// The bound provider's models as NextCoWork aliases: <c>{"&lt;id&gt;": {"upstreamModel", optional "displayName",
    /// "protocolOverride", "contextWindow", "maxOutputTokens", "capabilities"}}</c>. NextCoWork sends a model over
    /// <c>protocolOverride ?? provider.protocol</c> and joins <c>/chat/completions</c>, <c>/responses</c> or <c>/v1/messages</c>
    /// to the one <c>…/v1</c> base (it drops the doubled <c>/v1</c>), so a model whose native protocol is Responses or
    /// Anthropic gets that override and every other model inherits <c>openai-chat</c>.
    /// </summary>
    private JsonObject ModelsJson(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var models = new JsonObject();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var model = new JsonObject { ["upstreamModel"] = id };
            if (name is not null) model["displayName"] = name;
            if (protocols[id] != ApiProtocol.OpenAIChat) model["protocolOverride"] = protocols[id].ToId();
            var context = PositiveLong(info?["contextWindow"]);
            if (context is not null) model["contextWindow"] = context;
            // NextCoWork refuses every turn of a model whose maxOutputTokens exceeds its window (200k when unset).
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxOutput) model["maxOutputTokens"] = Math.Min(maxOutput, context ?? DefaultContextWindow);
            // Only the hints Astra actually knows are written; NextCoWork fills the rest from its own catalog and defaults.
            // Thinking (thinkingConfig, reasoningEfforts) is deliberately left to that catalog: it knows each model's
            // parameter shape per protocol, and a wrong one makes every request fail.
            var capabilities = new JsonObject();
            if (BoolHint(info?["vision"]) is { } vision)
            {
                capabilities["vision"] = vision;
                capabilities["visionInput"] = vision;
            }
            if (BoolHint(info?["pdf"]) is { } pdf) capabilities["fileInput"] = pdf;
            if (capabilities.Count > 0) model["capabilities"] = capabilities;
            models[id] = model;
        }
        return models;
    }

    /// <summary>NextCoWork's context window for a model that does not declare one.</summary>
    private const long DefaultContextWindow = 200_000;
}

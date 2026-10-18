using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// TKCode (github.com/AtomUICommunity/TKCode, an Avalonia desktop agent client; Coexist): adds one custom provider,
/// <c>providers.astra</c>, to <c>~/.tkcode/settings.json</c> (the home is <c>TKCODE_HOME</c>, else <c>~/.tkcode</c>) and, when a
/// model is selected, sets the top-level <c>model</c> to "astra/&lt;id&gt;". The file is JSONC that TKCode hot-reloads (500 ms
/// debounce), so the provider shows up in its model picker without a restart; the user's own providers, MCP servers and
/// other settings are left alone. Verified against TKCode 0.1.2 (<c>SettingsFile</c>, <c>ModelCatalog</c>), 2026-10.
/// <para>
/// A custom provider needs <c>api</c> and <c>baseUrl</c>: <c>{"name":"Astra","api":"chat","baseUrl":"&lt;gateway&gt;/v1","apiKey":…,
/// "models":{"&lt;id&gt;":{…}}}</c>. TKCode joins <c>/chat/completions</c> or <c>/responses</c> to the base, and for <c>messages</c> drops a
/// trailing <c>/v1</c> first (its adapter adds <c>/v1/messages</c>), so one <c>…/v1</c> base serves all three protocols and a
/// model whose native protocol is Responses or Anthropic only names its own <c>api</c>. Per-model <c>name</c>, <c>contextWindow</c>,
/// <c>maxTokens</c>, <c>reasoning</c> and <c>images</c> are written when Astra knows them; everything else (efforts, thinking
/// format) comes from TKCode's catalog, which also lends its metadata to proxied models by id. A file TKCode cannot parse, or a
/// number outside its <c>int</c>, makes it ignore the whole file, so values are written only when they fit.
/// </para>
/// <para>
/// The entry is shared with TKCode's settings page, which turns the provider off (<c>enabled</c>), switches models on and off
/// (the <c>models</c> list), edits per-model overrides and changes a model's protocol. Astra owns only what it writes — the
/// provider's <c>name</c>, <c>api</c>, <c>baseUrl</c> and <c>apiKey</c>, the set of models and the model fields above
/// (<see cref="OwnedPartsEqual"/>); anything else TKCode adds is carried over when Astra rewrites the entry and is not drift. A
/// model's <c>api</c> is the user's choice once changed there: <see cref="ChangedProtocolChoices"/> reports it so the server
/// records it with the client's protocol overrides.
/// </para>
/// </summary>
public sealed class TKCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store), IClientProtocolChoices
{
    public const string ProviderId = "astra";

    /// <summary>Provider fields Astra writes and owns; any other provider field (<c>enabled</c>, <c>headers</c>, <c>compat</c>) is TKCode's or the user's.</summary>
    private static readonly string[] OwnedProviderFields = ["name", "api", "baseUrl", "apiKey"];

    /// <summary>Model fields Astra writes and owns. <c>api</c> is shared: it is compared by <see cref="ChangedProtocolChoices"/> instead.</summary>
    private static readonly string[] OwnedModelFields = ["name", "contextWindow", "maxTokens", "reasoning", "images"];

    private const string ApiField = "api";

    private const string ChatApi = "chat";

    private const string ModelKey = "model";

    private string DataDir => Env.GetEnvironmentVariable("TKCODE_HOME") is { Length: > 0 } home ? home : Env.Combine(".tkcode");

    private string ConfigFile => Path.Combine(DataDir, "settings.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.TKCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect() => Env.DirectoryExists(DataDir)
        ? ClientDetection.Found(null, $"data directory {DataDir}")
        : ClientDetection.NotFound($"no {DataDir}");

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            [ApiField] = ChatApi,
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["apiKey"] = ctx.LocalKey,
        };
        var models = ModelsJson(ctx);
        // Without a models list TKCode asks the gateway's GET /v1/models and enables everything it returns.
        if (models.Count > 0) provider["models"] = models;
        var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        // What TKCode added to the entry Astra wrote stays; a provider Astra never wrote is replaced.
        if (Store.Get(Kind, ConfigFile, ProviderPath) is not null && Parse(before) is { } current) CarryOver(current, provider);
        var changes = new List<ConfigChange>
        {
            new(ConfigFile, ConfigFileFormat.Json, ProviderPath, before, provider.ToJsonString())
            {
                StillOurs = (now, applied) => OwnedPartsEqual(now, applied),
            },
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, ModelKey, CurrentValue(ConfigFile, ConfigFileFormat.Json, ModelKey),
                JsonString($"{ProviderId}/{ctx.Model}")));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        if (CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath) is { } provider)
        {
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, provider, null)
            {
                StillOurs = (now, applied) => OwnedPartsEqual(now, applied),
            });
        }
        if (CurrentValue(ConfigFile, ConfigFileFormat.Json, ModelKey) is { } model
            && StringOf(model)?.StartsWith(ProviderId + "/", StringComparison.Ordinal) == true)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, ModelKey, model, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath), []);

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
            // A model without its own api follows the provider's.
            var now = Text(model[ApiField]) ?? Text(current[ApiField]);
            if (now != (Text(written[ApiField]) ?? Text(applied[ApiField])) && ProtocolOfApi(now) is { } protocol)
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
    /// models, and per model the fields it wrote. TKCode's additions and a model's <c>api</c> are not compared.
    /// </summary>
    private static bool OwnedPartsEqual(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (Parse(currentText) is not { } current || Parse(appliedJson) is not { } applied) return false;
        foreach (var field in OwnedProviderFields)
            if (!JsonNode.DeepEquals(applied[field], current[field])) return false;
        var appliedModels = applied["models"] as JsonObject ?? [];
        var currentModels = current["models"] as JsonObject ?? [];
        if (appliedModels.Count != currentModels.Count) return false;
        foreach (var (id, node) in appliedModels)
        {
            if (node is not JsonObject written || currentModels[id] is not JsonObject now) return false;
            foreach (var field in OwnedModelFields)
                if (written.ContainsKey(field) && !JsonNode.DeepEquals(written[field], now[field])) return false;
        }
        return true;
    }

    /// <summary>Copies what TKCode added to the current entry (provider and per-model fields Astra does not own) into the new one.</summary>
    private static void CarryOver(JsonObject current, JsonObject next)
    {
        foreach (var (key, value) in current)
            if (key != "models" && !OwnedProviderFields.Contains(key)) next[key] = value?.DeepClone();
        if (current["models"] is not JsonObject currentModels || next["models"] is not JsonObject nextModels) return;
        foreach (var (id, node) in nextModels)
        {
            if (node is not JsonObject model || currentModels[id] is not JsonObject old) continue;
            foreach (var (key, value) in old)
                if (key != ApiField && !OwnedModelFields.Contains(key)) model[key] = value?.DeepClone();
        }
    }

    /// <summary>
    /// The bound provider's models as TKCode model entries: <c>{"&lt;id&gt;": {optional "api", "name", "contextWindow", "maxTokens",
    /// "reasoning", "images"}}</c>. Only the facts Astra knows are written; TKCode's defaults (128k window, 16k output, no
    /// reasoning, no images) and its catalog fill the rest.
    /// </summary>
    private JsonObject ModelsJson(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var models = new JsonObject();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var model = new JsonObject();
            if (protocols[id] != ApiProtocol.OpenAIChat) model[ApiField] = protocols[id] == ApiProtocol.Anthropic ? "messages" : "responses";
            if (name is not null && name != id) model["name"] = name;
            if (IntHint(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (IntHint(info?["maxOutputTokens"]) is { } maxOutput) model["maxTokens"] = maxOutput;
            if (BoolHint(info?["reasoning"]) is { } reasoning) model["reasoning"] = reasoning;
            if (BoolHint(info?["vision"]) is { } vision) model["images"] = vision;
            models[id] = model;
        }
        return models;
    }

    /// <summary>A positive hint that fits TKCode's <c>int</c> (a larger number would make it reject the whole file), else null.</summary>
    private static int? IntHint(JsonNode? node) => PositiveLong(node) is { } value and <= int.MaxValue ? (int)value : null;

    private static ApiProtocol? ProtocolOfApi(string? api) => api switch
    {
        ChatApi => ApiProtocol.OpenAIChat,
        "responses" => ApiProtocol.OpenAIResponses,
        "messages" => ApiProtocol.Anthropic,
        _ => null,
    };

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
}

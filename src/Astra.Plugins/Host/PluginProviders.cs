using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;

namespace Astra.Plugins.Host;

/// <summary>A provider a plugin signs in to, as the host lists it (magpie's plugin.Provider).</summary>
/// <param name="Id">OpenCode's id: google, github-copilot, zcode.</param>
/// <param name="Api">Where the provider's requests go by default, as its config says.</param>
/// <param name="Usage">Whether the plugin tells each account's allowance (auth.usage).</param>
/// <param name="Checkin">Whether the plugin presses its vendor's daily check-in (auth.checkin).</param>
public sealed record PluginProviderInfo(
    string Id, string Spec, string Name, string? Npm, string? Api, List<PluginAuthMethod> Methods, string? Icon,
    bool Usage, bool Checkin, int MaxConcurrency, List<PluginModelInfo> Models, List<PluginAccountInfo> Accounts,
    bool FellBack, string? ListError)
{
    public PluginModelInfo? Model(string id) => Models.FirstOrDefault(m => m.Id == id);
}

/// <summary>A way a plugin signs in: "oauth" (a browser page, then the plugin notices or a code is pasted) or "api" (a key).</summary>
public sealed record PluginAuthMethod(string Type, string Label, string? Placeholder);

/// <summary>A model a plugin's provider serves, as OpenCode lists it.</summary>
/// <param name="Npm">The AI SDK package it is spoken to with, which decides the wire API.</param>
/// <param name="ApiId">The id sent upstream when it differs from <see cref="Id"/>.</param>
/// <param name="Input">The prompt cap, when the plugin names one below the context window.</param>
public sealed record PluginModelInfo(
    string Id, string Name, string? Npm, string? Url, string? ApiId, long Context, long Input, long Output,
    bool Reasoning, bool Image, List<string> Variants, decimal? CostInput, decimal? CostOutput, bool Free, bool Decides)
{
    /// <summary>The id the vendor knows it by.</summary>
    public string UpstreamId => string.IsNullOrEmpty(ApiId) ? Id : ApiId;

    /// <summary>The context window: the prompt cap a plugin names wins, but never above the window it also names.</summary>
    public long ContextWindow => Input > 0 && (Context == 0 || Input < Context) ? Input : Context;
}

/// <summary>One account signed in to a plugin's provider; <see cref="Key"/> is where plugin-auth.json keeps it (id, else id#slot).</summary>
/// <param name="Models">The provider's model ids this account has, when the plugin listed them per account.</param>
public sealed record PluginAccountInfo(string Key, string Type, string? AccountId, string? Hint, List<string>? Models);

/// <summary>
/// The providers the plugin host serves and how a request reaches them: the listing (cached per host and
/// re-read when a sign-in changes), each account's loader options (where its requests go), and the wire API
/// each model is spoken to on — what OpenCode's AI SDK package for it speaks (magpie's pluginProtocol).
/// </summary>
public sealed partial class PluginProviders
{
    /// <summary>Where each AI SDK package sends a model by default.</summary>
    public static readonly IReadOnlyDictionary<string, string> NpmBase = new Dictionary<string, string>
    {
        ["@ai-sdk/anthropic"] = "https://api.anthropic.com/v1",
        ["@ai-sdk/openai"] = "https://api.openai.com/v1",
        ["@ai-sdk/google"] = "https://generativelanguage.googleapis.com/v1beta",
        ["@ai-sdk/github-copilot"] = "https://api.githubcopilot.com",
        ["@ai-sdk/xai"] = "https://api.x.ai/v1",
        ["@ai-sdk/mistral"] = "https://api.mistral.ai/v1",
        ["@ai-sdk/groq"] = "https://api.groq.com/openai/v1",
        ["@ai-sdk/deepseek"] = "https://api.deepseek.com/v1",
    };

    private readonly PluginHost _host;
    private readonly SemaphoreSlim _listLock = new(1, 1);
    private (long Generation, long Version, List<PluginProviderInfo> List)? _cached;
    private long _version;
    private readonly Dictionary<(long, string, string), string?> _loaders = new();

    public PluginProviders(PluginHost host)
    {
        _host = host;
        host.Changed += () => Interlocked.Increment(ref _version);
    }

    public PluginHost Host => _host;

    /// <summary>The providers the host lists now; read again after a sign-in changed or a new host started.</summary>
    /// <param name="proxies">Each provider's and account's own proxy ({id: {"": p, key: p}}), for its model listing.</param>
    public async Task<List<PluginProviderInfo>> ListAsync(JsonObject? proxies = null, bool force = false, CancellationToken ct = default)
    {
        if (!force && _cached is { } c && c.Generation == _host.Generation && c.Version == Interlocked.Read(ref _version)) return c.List;
        await _listLock.WaitAsync(ct);
        try
        {
            if (!force && _cached is { } again && again.Generation == _host.Generation && again.Version == Interlocked.Read(ref _version))
                return again.List;
            if (!await _host.HasPluginsAsync(ct)) return [];
            var version = Interlocked.Read(ref _version);
            var node = await _host.CallAsync("providers", new JsonObject { ["proxies"] = proxies ?? new JsonObject() }, ct);
            var list = (node as JsonArray ?? []).OfType<JsonObject>().Select(Parse).ToList();
            _cached = (_host.Generation, version, list);
            return list;
        }
        finally
        {
            _listLock.Release();
        }
    }

    /// <summary>Where the account's requests go, as its loader says (cached per host); null when it says nowhere.</summary>
    public async Task<string?> LoaderBaseAsync(string provider, string account, string? proxy, CancellationToken ct = default)
    {
        var key = (_host.Generation, provider, account);
        lock (_loaders)
            if (_loaders.TryGetValue(key, out var cached)) return cached;
        var p = new JsonObject { ["provider"] = provider, ["account"] = account };
        if (proxy is not null) p["proxy"] = proxy;
        var result = await _host.CallAsync("load", p, ct);
        var baseUrl = Str(result, "baseURL");
        lock (_loaders) _loaders[key] = baseUrl;
        return baseUrl;
    }

    /// <summary>Forgets the loaders of the account (all of the provider's when null), so they are asked again.</summary>
    public async Task ReloadAsync(string provider, string? account, CancellationToken ct = default)
    {
        lock (_loaders)
            foreach (var k in _loaders.Keys.Where(k => k.Item2 == provider && (account is null || k.Item3 == account)).ToList())
                _loaders.Remove(k);
        var p = new JsonObject { ["provider"] = provider };
        if (account is not null) p["account"] = account;
        await _host.CallAsync("reload", p, ct);
    }

    /// <summary>
    /// The wire API a model is spoken to on, as OpenCode's AI SDK package for it speaks: Anthropic's, OpenAI's
    /// Responses (and GitHub Copilot's for GPT-5 and after, but gpt-5-mini), Gemini's, else chat completions.
    /// </summary>
    public static ApiProtocol ProtocolOf(string providerId, PluginModelInfo model) => model.Npm switch
    {
        "@ai-sdk/anthropic" or "@ai-sdk/google-vertex/anthropic" => ApiProtocol.Anthropic,
        "@ai-sdk/openai" or "@ai-sdk/azure" => ApiProtocol.OpenAIResponses,
        "@ai-sdk/google" => ApiProtocol.Gemini,
        "@ai-sdk/github-copilot" => CopilotApi(model.UpstreamId),
        _ when providerId.StartsWith("github-copilot", StringComparison.Ordinal) => CopilotApi(model.UpstreamId),
        _ => ApiProtocol.OpenAIChat,
    };

    private static ApiProtocol CopilotApi(string model) =>
        GptN().Match(model) is { Success: true } m && int.Parse(m.Groups[1].Value) >= 5 && !model.StartsWith("gpt-5-mini", StringComparison.Ordinal)
            ? ApiProtocol.OpenAIResponses
            : ApiProtocol.OpenAIChat;

    /// <summary>One listed provider, read leniently: a field of the wrong type reads as absent.</summary>
    public static PluginProviderInfo Parse(JsonObject p)
    {
        var models = (p["models"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new PluginModelInfo(
            Str(m, "id") ?? "", Str(m, "name") ?? Str(m, "id") ?? "", Str(m, "npm"), Str(m, "url"), Str(m, "apiId"),
            Long(m["context"]), Long(m["input"]), Long(m["output"]), Bool(m["reasoning"]), Bool(m["image"]),
            Strings(m["variants"]) ?? [], Dec(m["cost"]?["input"]), Dec(m["cost"]?["output"]), Bool(m["free"]), Bool(m["decides"])))
            .Where(m => m.Id.Length > 0).ToList();
        var accounts = (p["accounts"] as JsonArray ?? []).OfType<JsonObject>().Select(a => new PluginAccountInfo(
            Str(a, "key") ?? "", Str(a, "type") ?? "", Str(a, "accountId"), Str(a, "hint"), Strings(a["models"])))
            .Where(a => a.Key.Length > 0).ToList();
        var methods = (p["methods"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(m => new PluginAuthMethod(Str(m, "type") ?? "", Str(m, "label") ?? "", Str(m, "placeholder"))).ToList();
        return new PluginProviderInfo(Str(p, "id") ?? "", Str(p, "spec") ?? "", Str(p, "name") ?? Str(p, "id") ?? "", Str(p, "npm"), Str(p, "api"),
            methods, Str(p, "icon"), Bool(p["usage"]), Bool(p["checkin"]), (int)Long(p["maxConcurrency"]), models, accounts,
            Bool(p["fellBack"]), Str(p, "listError"));
    }

    /// <summary>A non-empty string field of a host answer, else null.</summary>
    public static string? Str(JsonNode? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s ? s : null;

    private static long Long(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? (long)d : 0;

    private static decimal? Dec(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && decimal.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool Bool(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static List<string>? Strings(JsonNode? n) =>
        n is JsonArray a ? a.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).ToList() : null;

    [GeneratedRegex(@"^gpt-(\d+)")]
    private static partial Regex GptN();
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>Everything a reader needs from the machine; tests inject a fake home and an in-memory state store.</summary>
public sealed record ImportContext(
    ClientEnvironment Env, IForeignDbOpener Db, IClientConfigStateStore State, Func<string, bool> IsGatewayUrl);

/// <summary>A source application providers can be imported from. Readers are read-only and never touch the network.</summary>
public interface IImportSource
{
    string Id { get; }
    string Name { get; }
    ImportSourceResult Read();
}

/// <summary>URL hygiene for imported base URLs: trim whitespace and the trailing slash, nothing else (the path is kept as is).</summary>
public static class EndpointNormalizer
{
    /// <summary>The cleaned URL, or null when it is not an absolute http(s) URL.</summary>
    public static string? Normalize(string? url)
    {
        var trimmed = url?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(trimmed)) return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? trimmed : null;
    }

    public static string? Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    public static bool IsLoopback(string url) =>
        Host(url) is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "0.0.0.0";
}

/// <summary>Small lenient-JSON helpers shared by the readers.</summary>
internal static class ImportJson
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parses JSON or JSONC; null for blank or malformed text.</summary>
    public static JsonNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text, documentOptions: Lenient); }
        catch (JsonException) { return null; }
    }

    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public static bool Truthy(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<long>(out var n) => n != 0,
        JsonValue v when v.TryGetValue<string>(out var s) => s is "1" or "true" or "True",
        _ => false,
    };

    public static string? Str(JsonObject? o, params string[] names)
    {
        if (o is null) return null;
        foreach (var name in names)
            if (Str(o[name]) is { } value) return value;
        return null;
    }

    /// <summary>Model ids from an array of strings / objects ({id|name|model|slug}) or from the keys of an object.</summary>
    public static List<string> ModelIds(JsonNode? node)
    {
        var result = new List<string>();
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    var id = item is JsonObject o ? Str(o, "id", "name", "model", "slug") : Str(item);
                    if (id is not null) result.Add(id);
                }
                break;
            case JsonObject map:
                result.AddRange(map.Select(kv => kv.Key).Where(k => k.Trim().Length > 0));
                break;
        }
        return result;
    }

    public static Dictionary<string, string> Headers(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject o)
            foreach (var (key, value) in o)
                if (Str(value) is { } text && key.Trim().Length > 0) result[key.Trim()] = text;
        return result;
    }

    /// <summary>The wire protocol an app names with free text such as "@ai-sdk/anthropic" or "openai-responses".</summary>
    public static ApiProtocol ProtocolFromHint(string? hint)
    {
        var h = hint?.ToLowerInvariant() ?? "";
        if (h.Contains("anthropic")) return ApiProtocol.Anthropic;
        if (h.Contains("responses")) return ApiProtocol.OpenAIResponses;
        if (h.Contains("gemini") || h.Contains("google")) return ApiProtocol.Gemini;
        return ApiProtocol.OpenAIChat;
    }

    /// <summary>The auth scheme an API key of this protocol set is conventionally sent with.</summary>
    public static string AuthFor(IReadOnlyCollection<ImportEndpoint> endpoints)
    {
        if (endpoints.Count > 0 && endpoints.All(e => e.Protocol == ApiProtocol.Anthropic)) return AuthSchemes.XApiKey;
        if (endpoints.Count > 0 && endpoints.All(e => e.Protocol == ApiProtocol.Gemini)) return AuthSchemes.XGoogApiKey;
        return AuthSchemes.Bearer;
    }

    public static string? Decode(string? json) => json is null ? null : Str(Parse(json));
}

/// <summary>The sources, in display order, plus the clean-up and merge rules every source shares.</summary>
public sealed class ImportSourceRegistry(IReadOnlyList<IImportSource> sources, Func<string, bool> isGatewayUrl)
{
    public IReadOnlyList<IImportSource> Sources => sources;

    public static ImportSourceRegistry CreateDefault(ImportContext ctx) => new(
    [
        new CcSwitchReader(ctx), new AlmaReader(ctx), new ClaudeCodeReader(ctx), new CodexReader(ctx), new MagpieReader(ctx),
    ], ctx.IsGatewayUrl);

    /// <summary>Reads every source; one failing source never hides the others.</summary>
    public IReadOnlyList<ImportSourceResult> ReadAll() => sources.Select(Read).ToList();

    public ImportSourceResult? Read(string id) => sources.FirstOrDefault(s => s.Id == id) is { } s ? Read(s) : null;

    private ImportSourceResult Read(IImportSource source)
    {
        ImportSourceResult result;
        try { result = source.Read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ForeignDbException or InvalidOperationException)
        {
            return new ImportSourceResult { Id = source.Id, Name = source.Name, Found = true, Error = e.Message };
        }
        foreach (var item in result.Items) Finalize(item);
        var merged = Merge(result.Items);
        result.Items.Clear();
        result.Items.AddRange(merged);
        return result;
    }

    /// <summary>Normalizes URLs / models / key and decides whether the candidate can be imported at all.</summary>
    private void Finalize(ImportCandidate c)
    {
        c.ApiKey = string.IsNullOrWhiteSpace(c.ApiKey) ? null : c.ApiKey.Trim();
        if (c.ApiKey?.Any(char.IsControl) == true) { c.ApiKey = null; c.IgnoredFields.Add("key"); }
        c.Models = c.Models.Select(m => m.Trim()).Where(m => m.Length is > 0 and <= 512 && !m.Any(char.IsControl))
            .Distinct(StringComparer.Ordinal).Take(2000).ToList();
        if (c.SkipReason is not null) return;

        var seen = new HashSet<ApiProtocol>();
        var cleaned = new List<ImportEndpoint>();
        foreach (var endpoint in c.Endpoints)
        {
            if (string.IsNullOrWhiteSpace(endpoint.BaseUrl)) continue;
            var url = EndpointNormalizer.Normalize(endpoint.BaseUrl);
            if (url is null) { c.SkipReason = ImportSkipReasons.InvalidBaseUrl; return; }
            if (seen.Add(endpoint.Protocol)) cleaned.Add(new ImportEndpoint(endpoint.Protocol, url));
        }
        c.Endpoints = cleaned;
        if (cleaned.Count == 0) c.SkipReason = ImportSkipReasons.NoBaseUrl;
        else if (cleaned.Any(e => isGatewayUrl(e.BaseUrl))) c.SkipReason = ImportSkipReasons.PointsToAstra;
    }

    /// <summary>
    /// The same relay configured twice in one source (same key, same host: e.g. CC Switch's claude and codex entries)
    /// becomes one provider with several endpoints. Entries that would collide on a protocol stay separate.
    /// </summary>
    internal static List<ImportCandidate> Merge(IReadOnlyList<ImportCandidate> items)
    {
        var result = new List<ImportCandidate>();
        foreach (var item in items)
        {
            var target = item.SkipReason is not null || item.ApiKey is null ? null : result.FirstOrDefault(r =>
                r.SkipReason is null && r.ApiKey == item.ApiKey && r.Source == item.Source &&
                string.Equals(EndpointNormalizer.Host(r.Endpoints[0].BaseUrl), EndpointNormalizer.Host(item.Endpoints[0].BaseUrl), StringComparison.Ordinal)
                && !item.Endpoints.Any(e => r.Endpoints.Any(x => x.Protocol == e.Protocol))
                && r.Headers.Count == item.Headers.Count && item.Headers.All(h => r.Headers.TryGetValue(h.Key, out var v) && v == h.Value));
            if (target is null) { result.Add(item); continue; }
            target.Endpoints.AddRange(item.Endpoints);
            target.Models = target.Models.Concat(item.Models).Distinct(StringComparer.Ordinal).ToList();
            target.Off = target.Off && item.Off;
            target.Website ??= item.Website;
            target.Proxy ??= item.Proxy;
            target.IgnoredFields = target.IgnoredFields.Concat(item.IgnoredFields).Distinct().ToList();
            if (item.FromApp is not null && target.FromApp?.Split(", ").Contains(item.FromApp) != true)
                target.FromApp = target.FromApp is null ? item.FromApp : $"{target.FromApp}, {item.FromApp}";
            target.AuthScheme = ImportJson.AuthFor(target.Endpoints);
        }
        return result;
    }
}

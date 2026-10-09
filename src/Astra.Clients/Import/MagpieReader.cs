using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>
/// Magpie: <c>${XDG_CONFIG_HOME:-~/.config}/magpie/providers.json</c> (plain text). Each enabled key becomes its own
/// candidate (Astra's provider has one key); routing, rate limits, balance and fallback settings are not imported.
/// Entries without any endpoint are Magpie's per-subscription model picks and are ignored.
/// </summary>
public sealed class MagpieReader(ImportContext ctx) : IImportSource
{
    public string Id => ImportSourceIds.Magpie;
    public string Name => "Magpie";

    public ImportSourceResult Read()
    {
        var dir = ctx.Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : ctx.Env.Combine(".config");
        var file = Path.Combine(dir, "magpie", "providers.json");
        var result = new ImportSourceResult { Id = Id, Name = Name, Path = file };
        var text = ctx.Env.ReadTextOrNull(file);
        if (text is null) return result;
        result.Found = true;
        if (ImportJson.Parse(text) is not JsonObject root || root["providers"] is not JsonArray providers)
        {
            result.Error = "providers.json is not a Magpie provider file";
            return result;
        }
        foreach (var node in providers)
            if (node is JsonObject p) AddProvider(result, p);
        return result;
    }

    private void AddProvider(ImportSourceResult result, JsonObject p)
    {
        var id = ImportJson.Str(p["id"]);
        if (id is null) return;
        var endpoints = new List<ImportEndpoint>();
        void Add(ApiProtocol protocol, string field)
        {
            if (ImportJson.Str(p[field]) is { } url) endpoints.Add(new ImportEndpoint(protocol, url));
        }
        Add(ApiProtocol.OpenAIChat, "chat");
        Add(ApiProtocol.OpenAIResponses, "responses");
        Add(ApiProtocol.Anthropic, "anthropic");
        Add(ApiProtocol.Gemini, "gemini");
        if (endpoints.Count == 0) return;

        var name = ImportJson.Str(p["name"]) ?? id;
        var off = ImportJson.Truthy(p["off"]) || ImportJson.Truthy(p["hidden"]);
        var models = ImportJson.ModelIds(p["models"]);
        var headers = ImportJson.Headers(p["headers"]);
        var proxy = ProxyOf(ImportJson.Str(p["proxy"]));

        // (key, label, protocol limit, off) for the main key and each saved one.
        var keys = new List<(string? Key, string? Label, string? Protocol, bool Off)>();
        var main = ImportJson.Str(p["key"]);
        if (main is not null || p["keys"] is not JsonArray { Count: > 0 })
            keys.Add((main, null, ImportJson.Str(p["keyProtocol"]), false));
        if (p["keys"] is JsonArray extra)
        {
            var index = 0;
            foreach (var k in extra.OfType<JsonObject>())
            {
                index++;
                if (ImportJson.Str(k["key"]) is not { } value) continue;
                keys.Add((value, ImportJson.Str(k["name"]) ?? $"key {index}", ImportJson.Str(k["protocol"]), ImportJson.Truthy(k["off"])));
            }
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var (key, label, limit, keyOff) = keys[i];
            var c = new ImportCandidate
            {
                Ref = i == 0 ? $"{Id}:{id}" : $"{Id}:{id}#{i}", Source = Id,
                Name = label is null ? name : $"{name} · {label}", FromApp = ImportJson.Str(p["preset"]),
                ApiKey = key, Off = off || keyOff, Models = [.. models], Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
                Proxy = proxy, Website = ImportJson.Str(p["website"]),
            };
            c.Endpoints.AddRange(limit is null ? endpoints : endpoints.Where(e => MatchesLimit(e.Protocol, limit)));
            if (c.Endpoints.Count == 0) c.SkipReason = ImportSkipReasons.UnsupportedProtocol;
            c.AuthScheme = ImportJson.AuthFor(c.Endpoints);
            result.Items.Add(c);
        }
    }

    private static bool MatchesLimit(ApiProtocol protocol, string limit) =>
        ApiProtocols.TryParse(limit, out var wanted) && wanted == protocol;

    /// <summary>Magpie's proxy field: "" follows the global one, "direct" none, "host:port" means http.</summary>
    private static string? ProxyOf(string? proxy) => proxy switch
    {
        null or "direct" => null,
        _ when proxy.Contains("://", StringComparison.Ordinal) => proxy,
        _ => "http://" + proxy,
    };
}

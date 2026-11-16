using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>
/// Magpie: <c>${XDG_CONFIG_HOME:-~/.config}/magpie/providers.json</c> (plain text). A provider's keys (<c>key</c> plus
/// <c>keys[]</c> with name / protocol / off / weight) stay together as one candidate with a key list; routing, rate limits, balance and fallback settings are not imported.
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

        // (key, name, protocol limit, off, weight) for the main key and each saved one.
        var keys = new List<(string? Key, string? Label, string? Protocol, bool Off, int? Weight)>();
        var main = ImportJson.Str(p["key"]);
        if (main is not null || p["keys"] is not JsonArray { Count: > 0 })
            keys.Add((main, null, ImportJson.Str(p["keyProtocol"]), false, null));
        if (p["keys"] is JsonArray extra)
        {
            var index = 0;
            foreach (var k in extra.OfType<JsonObject>())
            {
                index++;
                if (ImportJson.Str(k["key"]) is not { } value) continue;
                var weight = k["weight"] is JsonValue wv && wv.TryGetValue<int>(out var w) && w is >= 1 and <= ProviderAccount.MaxKeyWeight ? w : (int?)null;
                keys.Add((value, ImportJson.Str(k["name"]) ?? $"key {index}", ImportJson.Str(k["protocol"]), ImportJson.Truthy(k["off"]), weight));
            }
        }

        var distinct = keys.Where(k => k.Key is not null).DistinctBy(k => k.Key).ToList();
        var first = distinct.FirstOrDefault(k => !k.Off) is { Key: not null } on ? on : distinct.FirstOrDefault();
        var many = distinct.Count > 1;
        var single = many || keys.Count == 0 ? default : keys[0];
        var c = new ImportCandidate
        {
            Ref = $"{Id}:{id}", Source = Id, Name = name, FromApp = ImportJson.Str(p["preset"]),
            ApiKey = many ? first.Key : single.Key, Off = off || (many ? distinct.All(k => k.Off) : single.Off),
            Models = [.. models], Headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            Proxy = proxy, Website = ImportJson.Str(p["website"]),
        };
        if (many)
        {
            // several keys: one provider with a key list, each key keeping the protocol it was made for
            c.Keys.AddRange(distinct.Select(k => new ImportKey(k.Key!, k.Label, k.Protocol, k.Off, k.Weight)));
            c.Endpoints.AddRange(endpoints);
        }
        else
            c.Endpoints.AddRange(single.Protocol is null ? endpoints : endpoints.Where(e => MatchesLimit(e.Protocol, single.Protocol)));
        if (c.Endpoints.Count == 0) c.SkipReason = ImportSkipReasons.UnsupportedProtocol;
        c.AuthScheme = ImportJson.AuthFor(c.Endpoints);
        result.Items.Add(c);
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

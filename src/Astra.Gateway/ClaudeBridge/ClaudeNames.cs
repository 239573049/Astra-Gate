using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// How the caller's tools are named for the <c>claude</c> program. Ported from magpie's Claude subscription bridge (MIT,
/// <c>bridgeToolName</c> / <c>objectSchema</c>): the caller's tools are offered to <c>claude</c> as an MCP server called
/// <see cref="ServerName"/>, which <c>claude</c> calls <c>mcp__astra__&lt;name&gt;</c>.
/// </summary>
public static class ClaudeNames
{
    /// <summary>The MCP server the run's tools belong to (the helper's <c>serverInfo.name</c>, the key in <c>--mcp-config</c>).</summary>
    public const string ServerName = "astra";

    /// <summary>What <c>claude</c> puts before an MCP tool's name.</summary>
    public const string Prefix = "mcp__" + ServerName + "__";

    // claude keeps [A-Za-z0-9_-] of an MCP tool's name, and a name over 64 characters with the prefix is refused.
    private const int MaxName = 52; // 64 less the length of Prefix
    private const int HashLength = 12;

    /// <summary>
    /// The name the run is offered a tool under. One <c>claude</c> would keep as it is (only <c>[A-Za-z0-9_-]</c>, at most 51 long, not
    /// ending like a name made here) stays; any other is cut, made of those characters and given a hash of the whole name: unique
    /// (<c>a.b</c> and <c>a_b</c> stay apart) and the same in every run, so a name in the history is the one the run was offered.
    /// </summary>
    public static string BridgeName(string name)
    {
        if (name.Length <= MaxName && name.All(Valid) && !EndsLikeMade(name)) return name;
        var kept = new StringBuilder();
        foreach (var c in name)
        {
            if (Valid(c)) kept.Append(c);
            else if (kept.Length == 0 || kept[^1] != '_') kept.Append('_');
        }
        var head = kept.Length > MaxName - HashLength - 1 ? kept.ToString(0, MaxName - HashLength - 1) : kept.ToString();
        return head + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..HashLength].ToLowerInvariant();
    }

    private static bool Valid(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';

    private static bool EndsLikeMade(string name) =>
        name.Length > HashLength + 1 && name[^(HashLength + 1)] == '_'
        && name[^HashLength..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// A tool's input schema folded to an object root: <c>claude</c> leaves out an MCP tool whose schema has <c>oneOf</c>,
    /// <c>anyOf</c> or <c>allOf</c> at its root. The variants' fields are merged and a field every variant requires stays required.
    /// </summary>
    public static JsonObject ObjectSchema(JsonNode? schema)
    {
        if (schema is not JsonObject root) return Empty();
        var variants = new List<JsonObject>();
        foreach (var key in (string[])["oneOf", "anyOf", "allOf"])
            if (root[key] is JsonArray list)
                variants.AddRange(list.OfType<JsonObject>());
        if (variants.Count == 0)
        {
            var copy = (JsonObject)root.DeepClone();
            copy["type"] = "object";
            if (copy["properties"] is not JsonObject) copy["properties"] = new JsonObject();
            return copy;
        }
        var properties = new JsonObject();
        if (root["properties"] is JsonObject own)
            foreach (var (k, v) in own) properties[k] = v?.DeepClone();
        HashSet<string>? required = null;
        foreach (var variant in variants)
        {
            if (variant["properties"] is JsonObject props)
                foreach (var (k, v) in props)
                    if (!properties.ContainsKey(k)) properties[k] = v?.DeepClone();
            var req = variant["required"] is JsonArray r ? r.Select(n => n?.GetValue<string>() ?? "").ToHashSet() : [];
            required = required is null ? req : [.. required.Intersect(req)];
        }
        var result = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required is { Count: > 0 })
            result["required"] = new JsonArray(required.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        return result;
    }

    private static JsonObject Empty() => new() { ["type"] = "object", ["properties"] = new JsonObject() };
}

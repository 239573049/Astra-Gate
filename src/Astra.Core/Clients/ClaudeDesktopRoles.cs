using System.Text.Json.Nodes;

namespace Astra.Core.Clients;

/// <summary>
/// Claude Desktop only accepts Claude role model ids (claude-sonnet-* / claude-opus-* / claude-haiku-*), so for
/// this one client the gateway maps the role to a provider model (plan §7.5, the only model-mapping exception).
/// The map lives in <c>clients.extra_json</c> as <c>{"roleMap":{"sonnet":"…","opus":"…","haiku":"…"}}</c>.
/// </summary>
public static class ClaudeDesktopRoles
{
    public const string ExtrasKey = "roleMap";

    /// <summary>Roles in fallback order: an unmapped role uses the first mapped one of this list.</summary>
    public static readonly IReadOnlyList<string> Roles = ["sonnet", "opus", "haiku"];

    /// <summary>The role id advertised on /v1/models for each role (the latest model of that family in the seed).</summary>
    public static string AdvertisedId(string role) => role switch
    {
        "opus" => "claude-opus-5-5",
        "haiku" => "claude-haiku-4-5",
        _ => "claude-sonnet-5-5",
    };

    /// <summary>The non-empty role → model entries of an extras object, in <see cref="Roles"/> order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(JsonObject? extras)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (extras?[ExtrasKey] is not JsonObject map) return result;
        foreach (var role in Roles)
        {
            if (map[role] is JsonValue v && v.TryGetValue<string>(out var model) && !string.IsNullOrWhiteSpace(model))
                result.Add(new(role, model.Trim()));
        }
        return result;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return [];
        try
        {
            return Parse(JsonNode.Parse(extraJson) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The role of a Claude model id (claude-sonnet-4-5, claude-3-5-sonnet-20241022, claude-opus-5-5[1m] …), or null
    /// when the id is not a Claude role id.
    /// </summary>
    public static string? RoleOf(string model)
    {
        var id = model.Trim().ToLowerInvariant();
        if (!id.StartsWith("claude-", StringComparison.Ordinal)) return null;
        foreach (var part in id.Split('-', '[', ']', '.', '@', ':'))
        {
            if (Roles.Contains(part)) return part;
        }
        return null;
    }

    /// <summary>
    /// The provider model for a requested id: the mapped model of its role, else the first mapped role. Ids that are
    /// not Claude role ids, or an empty map, pass through unchanged.
    /// </summary>
    public static string Map(IReadOnlyList<KeyValuePair<string, string>> map, string requested)
    {
        if (map.Count == 0 || RoleOf(requested) is not { } role) return requested;
        foreach (var (r, model) in map)
        {
            if (r == role) return model;
        }
        return map[0].Value;
    }
}

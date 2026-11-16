using System.Text.Json.Nodes;

namespace Astra.Core.Clients;

/// <summary>
/// A client's optional shortlist of model ids it should see (magpie's "visible" list). It lives in
/// <c>clients.extra_json</c> under <see cref="ExtrasKey"/> as a JSON array of ids as the client sees them (qualified
/// ids included, linked update 23). Unset or empty = no shortlist: the client sees every model it can reach. When set, the
/// model list the gateway answers that client with, and the lists written into its config, hold only those ids; smart
/// routing's virtual model is listed only if the user put it in.
/// </summary>
public static class ModelShortlist
{
    public const string ExtrasKey = "modelShortlist";

    /// <summary>The shortlist of an extras object; null when none is set.</summary>
    public static IReadOnlyList<string>? Of(JsonObject? extras)
    {
        if (extras?[ExtrasKey] is not JsonArray array) return null;
        var ids = Normalize(array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null));
        return ids.Count == 0 ? null : ids;
    }

    /// <summary>The shortlist stored in a client's <c>extra_json</c> text; null when none is set.</summary>
    public static IReadOnlyList<string>? Of(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try { return Of(Json.Deserialize<JsonObject>(extraJson)); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Trimmed, de-duplicated (ordinal), non-empty ids in their given order.</summary>
    public static List<string> Normalize(IEnumerable<string?> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in ids)
            if (id?.Trim() is { Length: > 0 } t && seen.Add(t)) result.Add(t);
        return result;
    }

    /// <summary>Puts (or, for an empty list, removes) the shortlist in an extras object.</summary>
    public static void Set(JsonObject extras, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) extras.Remove(ExtrasKey);
        else extras[ExtrasKey] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]);
    }

    /// <summary>Whether <paramref name="id"/> passes a shortlist (always, when there is none).</summary>
    public static bool Allows(IReadOnlyList<string>? shortlist, string id) => shortlist is null || shortlist.Contains(id, StringComparer.Ordinal);
}

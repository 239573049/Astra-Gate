using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Clients.Library;

/// <summary>A Skill a registry lists. <see cref="Source"/> is the GitHub <c>owner/repo</c> it comes from.</summary>
public sealed record MarketSkill(string Source, string SkillId, string Name, int Installs);

/// <summary>
/// The Skills market (the registry idea is magpie's, MIT: <c>library/market.go</c> searches skills.sh's <c>/api/search</c>). The address is a setting
/// because skills.sh is a third-party service Astra does not run: the default is skills.sh, and a company can point it at a registry of its own that
/// answers the same <c>GET /api/search?q=&amp;limit=</c> shape. Only the search is used (no page scraping); a Skill is installed from its GitHub
/// repository through <see cref="SkillGit"/> like any Git source, so the same address checks and the same ownership rules apply.
/// </summary>
public static partial class SkillMarket
{
    public const string DefaultUrl = "https://www.skills.sh";
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    [GeneratedRegex(@"^[\w.-]+/[\w.-]+$")]
    private static partial Regex SourcePattern();

    /// <summary>Why a registry address is not acceptable, or null: an absolute http(s) address with no credentials.</summary>
    public static string? CheckUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
            ? null : "The registry address must be an http(s) address without credentials";

    public static bool IsSource(string source) => SourcePattern().IsMatch(source);

    /// <summary>The Git address of a market Skill's repository.</summary>
    public static string RepoUrl(string source) => "https://github.com/" + source;

    /// <summary>Parses a search answer: <c>{ "skills": [ { source, skillId, name, installs } ] }</c>; entries with a bad source or no id are dropped.</summary>
    public static IReadOnlyList<MarketSkill> Parse(string json)
    {
        var list = new List<MarketSkill>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SkillLibraryException("The registry answered something that is not a list of Skills", SkillFailure.Invalid);
        }
        if (root?["skills"] is not JsonArray skills) throw new SkillLibraryException("The registry answered something that is not a list of Skills", SkillFailure.Invalid);
        foreach (var n in skills)
        {
            if (n is not JsonObject o) continue;
            var source = o["source"] is JsonValue s && s.TryGetValue<string>(out var src) ? src : "";
            var id = o["skillId"] is JsonValue i && i.TryGetValue<string>(out var sid) ? sid : "";
            if (!IsSource(source) || !SkillLibrary.IsValidName(id)) continue;
            var name = o["name"] is JsonValue nm && nm.TryGetValue<string>(out var nv) && nv.Length > 0 ? nv : id;
            var installs = o["installs"] is JsonValue c && c.TryGetValue<int>(out var cv) ? cv : 0;
            list.Add(new MarketSkill(source, id, name, installs));
        }
        return list;
    }

    public static async Task<IReadOnlyList<MarketSkill>> SearchAsync(HttpClient http, string registry, string query, CancellationToken ct)
    {
        if (CheckUrl(registry) is { } why) throw new SkillLibraryException(why, SkillFailure.Invalid);
        query = query.Trim();
        if (query.Length < 2) return [];
        var url = registry.TrimEnd('/') + "/api/search?q=" + Uri.EscapeDataString(query) + "&limit=100";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new SkillLibraryException($"The registry answered {(int)response.StatusCode}", SkillFailure.Invalid);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new SkillLibraryException("The registry's answer is too large", SkillFailure.Invalid);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes) throw new SkillLibraryException("The registry's answer is too large", SkillFailure.Invalid);
            }
            return Parse(System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new SkillLibraryException("The registry could not be reached", SkillFailure.Invalid);
        }
    }
}

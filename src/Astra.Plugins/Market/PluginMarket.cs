using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Plugins.Store;

namespace Astra.Plugins.Market;

/// <summary>A plugin the market suggests: magpie-community's registry entry, with what npm says of it now.</summary>
/// <param name="Kind">provider (an OpenCode provider plugin), middleware or agent.</param>
/// <param name="Providers">OpenCode's ids of the providers it signs in to.</param>
/// <param name="Replaces">The built-in subscription (in magpie) it does the work of.</param>
/// <param name="Summary">By language: en, zh.</param>
/// <param name="Latest">npm's newest version; null when npm didn't say.</param>
public sealed record MarketListing(
    string Package, string Name, string? Icon, string Kind, List<string> Providers, string? Replaces,
    Dictionary<string, string> Summary, string? Latest, string? Description, string? License);

/// <summary>A plugin found by a search: on npm, or on GitHub by its magpie-plugin topic.</summary>
/// <param name="Spec">What adding it installs: the npm package, or github:owner/repo.</param>
/// <param name="Source">npm or github.</param>
public sealed record MarketSearchResult(string Spec, string Name, string? Description, string? Version, string Source, string Kind, int Stars);

/// <summary>
/// The plugin market (magpie's internal/plugin/market.go): magpie-community's registry.json, fetched at most every six
/// hours (else the copy fetched last, else the one built into Astra), each listing with npm's newest version; and
/// searches of npm and of GitHub's <c>magpie-plugin</c> topic for the rest. <c>ASTRA_PLUGIN_MARKET</c> moves the
/// registry; "off" keeps to the built-in copy.
/// </summary>
public sealed class PluginMarket(AstraPaths paths, Func<HttpClient> http, Func<string, string?>? getEnv = null)
{
    public const string RegistryUrl = "https://raw.githubusercontent.com/magpie-community/plugins/main/registry.json";
    private const string NpmRegistry = "https://registry.npmjs.org";
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan SearchFresh = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, JsonObject? Info)> _npm = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, List<MarketSearchResult> Results)> _searches = new();
    private (DateTimeOffset At, List<JsonObject> List)? _registry;

    private string CacheFile => Path.Combine(paths.PluginMarketDir, "registry.json");

    /// <summary>The market's listings, each with npm's word on it.</summary>
    public async Task<List<MarketListing>> ListAsync(bool refresh = false, CancellationToken ct = default)
    {
        var entries = await RegistryAsync(refresh, ct);
        var infos = await NpmInfoAsync(entries.Select(e => Str(e, "package")!).ToList(), refresh, ct);
        return entries.Select(e =>
        {
            var pkg = Str(e, "package")!;
            var info = infos.GetValueOrDefault(pkg);
            var summary = new Dictionary<string, string>();
            if (e["summary"] is JsonObject s)
                foreach (var (lang, text) in s)
                    if (text is JsonValue v && v.TryGetValue<string>(out var t)) summary[lang] = t;
            return new MarketListing(pkg, Str(e, "name") ?? pkg, Str(e, "icon"), Str(e, "kind") ?? "provider",
                (e["providers"] as JsonArray)?.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToList() ?? [],
                Str(e, "replaces"), summary, Str(info, "version"), Str(info, "description"), Str(info, "license"));
        }).ToList();
    }

    /// <summary>Plugins found by <paramref name="query"/> on npm (OpenCode, magpie and pi packages) and on GitHub's magpie-plugin topic.</summary>
    public async Task<List<MarketSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (_searches.TryGetValue(query, out var hit) && DateTimeOffset.UtcNow - hit.At < SearchFresh) return hit.Results;
        var results = new List<MarketSearchResult>();
        var npm = SearchNpmAsync(query, ct);
        var github = SearchGitHubAsync(query, ct);
        results.AddRange(await npm);
        results.AddRange(await github);
        _searches[query] = (DateTimeOffset.UtcNow, results);
        return results;
    }

    private async Task<List<JsonObject>> RegistryAsync(bool refresh, CancellationToken ct)
    {
        if (!refresh && _registry is { } r && DateTimeOffset.UtcNow - r.At < Fresh) return r.List;
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh && _registry is { } again && DateTimeOffset.UtcNow - again.At < Fresh) return again.List;
            var source = (getEnv ?? Environment.GetEnvironmentVariable)("ASTRA_PLUGIN_MARKET") is { Length: > 0 } s ? s : RegistryUrl;
            if (source != "off")
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var text = await http().GetStringAsync(source, timeout.Token);
                    if (Parse(text) is { Count: > 0 } fetched)
                    {
                        Directory.CreateDirectory(paths.PluginMarketDir);
                        await File.WriteAllTextAsync(CacheFile, text, ct);
                        _registry = (DateTimeOffset.UtcNow, fetched);
                        return fetched;
                    }
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
                {
                    // the copy fetched last, else the built-in one; asked again in a minute
                }
            }
            var fallback = (File.Exists(CacheFile) ? Parse(await File.ReadAllTextAsync(CacheFile, ct)) : null) ?? Parse(BuiltIn()) ?? [];
            _registry = (DateTimeOffset.UtcNow - Fresh + TimeSpan.FromMinutes(1), fallback);
            return fallback;
        }
        finally
        {
            _lock.Release();
        }
    }

    // magpie-community's own entries alone, each a valid npm package.
    private static List<JsonObject>? Parse(string text)
    {
        try
        {
            return (JsonNode.Parse(text)?["plugins"] as JsonArray)?.OfType<JsonObject>()
                .Where(e => Str(e, "package") is { } pkg && PluginSpec.IsPackageName(pkg) && e["community"] is JsonValue c && c.GetValueKind() == JsonValueKind.True)
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuiltIn()
    {
        using var s = typeof(PluginMarket).Assembly.GetManifestResourceStream("Astra.Plugins.Market.market.json")!;
        return new StreamReader(s).ReadToEnd();
    }

    // npm's latest manifest of each package, six at a time, eight seconds for all; kept six hours.
    private async Task<Dictionary<string, JsonObject?>> NpmInfoAsync(List<string> packages, bool refresh, CancellationToken ct)
    {
        using var all = CancellationTokenSource.CreateLinkedTokenSource(ct);
        all.CancelAfter(TimeSpan.FromSeconds(8));
        using var gate = new SemaphoreSlim(6);
        var tasks = packages.Select(async pkg =>
        {
            if (!refresh && _npm.TryGetValue(pkg, out var c) && DateTimeOffset.UtcNow - c.At < Fresh) return (pkg, c.Info);
            await gate.WaitAsync(all.Token);
            try
            {
                var text = await http().GetStringAsync($"{NpmRegistry}/{pkg.Replace("/", "%2F")}/latest", all.Token);
                var info = JsonNode.Parse(text) as JsonObject;
                _npm[pkg] = (DateTimeOffset.UtcNow, info);
                return (pkg, info);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                return (pkg, _npm.TryGetValue(pkg, out var stale) ? stale.Info : null);
            }
            finally
            {
                gate.Release();
            }
        });
        try
        {
            return (await Task.WhenAll(tasks)).ToDictionary(x => x.pkg, x => x.Item2);
        }
        catch (OperationCanceledException)
        {
            return packages.ToDictionary(p => p, p => _npm.TryGetValue(p, out var c) ? c.Info : null);
        }
    }

    private async Task<List<MarketSearchResult>> SearchNpmAsync(string query, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var text = await http().GetStringAsync($"{NpmRegistry}/-/v1/search?size=25&text={Uri.EscapeDataString(query + " opencode")}", timeout.Token);
            return (JsonNode.Parse(text)?["objects"] as JsonArray ?? []).OfType<JsonObject>().Select(o => o["package"] as JsonObject)
                .OfType<JsonObject>()
                .Select(p => new MarketSearchResult(Str(p, "name") ?? "", Str(p, "name") ?? "", Str(p, "description"), Str(p, "version"), "npm",
                    KindOfKeywords(p["keywords"] as JsonArray), 0))
                .Where(r => r.Spec.Length > 0)
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return [];
        }
    }

    private async Task<List<MarketSearchResult>> SearchGitHubAsync(string query, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var q = Uri.EscapeDataString($"{query} topic:magpie-plugin archived:false fork:false");
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/search/repositories?q={q}&sort=stars&per_page=30");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", "Astra");
            if ((getEnv ?? Environment.GetEnvironmentVariable)("GITHUB_TOKEN") is { Length: > 0 } token)
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var response = await http().SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return [];
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            return (JsonNode.Parse(text)?["items"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(r => new MarketSearchResult("github:" + Str(r, "full_name"), Str(r, "name") ?? "", Str(r, "description"), null, "github",
                    KindOfKeywords(r["topics"] as JsonArray), r["stargazers_count"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0))
                .Where(r => r.Spec.Length > "github:".Length)
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return [];
        }
    }

    // What the package's keywords (or a repository's topics) say it is.
    private static string KindOfKeywords(JsonArray? keywords)
    {
        var words = keywords?.Select(k => k?.ToString() ?? "").ToHashSet() ?? [];
        return words.Contains("magpie-middleware") ? "middleware"
            : words.Contains("magpie-agent") ? "agent"
            : words.Contains("pi-package") ? "pi"
            : "provider";
    }

    private static string? Str(JsonNode? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s ? s : null;
}

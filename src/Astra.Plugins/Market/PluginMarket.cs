using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Plugins.Packages;
using Astra.Plugins.Store;

namespace Astra.Plugins.Market;

/// <summary>A plugin the market suggests: one entry of the registry (the docs site's: community list plus what Astra published).</summary>
/// <param name="Kind">provider (an OpenCode provider plugin), middleware, agent or pi.</param>
/// <param name="Providers">OpenCode's ids of the providers it signs in to.</param>
/// <param name="Replaces">The built-in subscription it does the work of.</param>
/// <param name="Summary">By language: en, zh.</param>
/// <param name="Latest">The newest version the registry knows; null when it didn't say.</param>
/// <param name="Spec">What adding it installs when that is not the package name (github:owner/repo); null = the package.</param>
/// <param name="Origin">astra (published on the docs site) or upstream (the community registry, astragate-community/astra-gate-plugins).</param>
/// <param name="MinAstraVersion">The oldest Astra that can run it; null = any.</param>
/// <param name="Tarballs">The versions the market hosts itself, newest first; empty = installed from npm (or <paramref name="Spec"/>).</param>
public sealed record MarketListing(
    string Package, string Name, string? Icon, string Kind, List<string> Providers, string? Replaces,
    Dictionary<string, string> Summary, string? Latest, string? Description, string? License,
    string? Spec, string Origin, string? Author, string? Homepage, string? Repository, List<string> Tags,
    string? MinAstraVersion, List<MarketTarball> Tarballs);

/// <summary>One version of a plugin the market hosts: where to fetch it and the hash it must have.</summary>
/// <param name="Url">Absolute (resolved against the registry's address).</param>
/// <param name="Sha256">Lower-case hex.</param>
/// <param name="Sha512">Base64; null when the registry gave none.</param>
public sealed record MarketTarball(string Version, string Url, string Sha256, string? Sha512, long Size);

/// <summary>A plugin found by a search: one of the market's own, or on npm, or on GitHub by its magpie-plugin topic.</summary>
/// <param name="Spec">What adding it installs: the npm package, or github:owner/repo.</param>
/// <param name="Source">astra (listed in the market), npm or github.</param>
public sealed record MarketSearchResult(string Spec, string Name, string? Description, string? Version, string Source, string Kind, int Stars);

/// <summary>
/// The plugin market. Everything comes from one registry — by default the Astra site's
/// (<see cref="DefaultUrl"/>), which lists the community's plugins (astragate-community/astra-gate-plugins), those published on the site and the
/// packages it hosts — fetched at most every six hours (else the copy fetched last, else the one built into
/// Astra), and searched through the site too. Which registry: <c>ASTRA_PLUGIN_MARKET</c> (<c>off</c> keeps to
/// the cached and built-in copies), else the PluginMarketUrl setting (empty = the same), else the default.
/// </summary>
public sealed class PluginMarket(
    AstraPaths paths, Func<HttpClient> http, Func<string, string?>? getEnv = null, Func<string?>? configuredUrl = null)
{
    public const string DefaultUrl = "https://astra-gate.si/api/plugin-market/registry.json";
    private const string RegistryFile = "registry.json";
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(6);
    private static readonly TimeSpan SearchFresh = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Source, List<MarketSearchResult> Results)> _searches = new();
    private (DateTimeOffset At, string? Source, List<MarketListing> List)? _registry;

    private string CacheFile => Path.Combine(paths.PluginMarketDir, RegistryFile);

    /// <summary>
    /// The registry address in use, or null when the market is kept to its cached and built-in copies.
    /// An address that isn't http(s) is not one: the next source down the list is used.
    /// </summary>
    public string? Source
    {
        get
        {
            var env = (getEnv ?? Environment.GetEnvironmentVariable)("ASTRA_PLUGIN_MARKET")?.Trim();
            if (env == "off") return null;
            if (!string.IsNullOrEmpty(env) && IsHttp(env)) return env;
            var configured = configuredUrl?.Invoke();
            if (configured is null) return DefaultUrl;
            configured = configured.Trim();
            return configured.Length == 0 ? null : IsHttp(configured) ? configured : DefaultUrl;
        }
    }

    /// <summary>The market's listings.</summary>
    public async Task<List<MarketListing>> ListAsync(bool refresh = false, CancellationToken ct = default) =>
        (await RegistryAsync(refresh, ct)).List;

    /// <summary>
    /// Fetches the registry now. Unlike <see cref="ListAsync"/> a registry that can't be fetched is an error, not a fall back
    /// to the cached copy: an update that can't ask the market must say so rather than find nothing newer. Does nothing
    /// when the market is kept to its local copies.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (Source is not null) await RegistryAsync(true, ct, strict: true);
    }

    /// <summary>One listing by package name; null when the market has none.</summary>
    public async Task<MarketListing?> FindAsync(string package, CancellationToken ct = default) =>
        (await ListAsync(false, ct)).FirstOrDefault(l => l.Package == package);

    /// <summary>
    /// The version of <paramref name="package"/> the market hosts (the newest when <paramref name="version"/> is null);
    /// null when it hosts none, or not that one.
    /// </summary>
    public async Task<(MarketListing Listing, MarketTarball Tarball)?> FindHostedAsync(
        string package, string? version = null, CancellationToken ct = default)
    {
        if (await FindAsync(package, ct) is not { } listing) return null;
        var tarball = version is null ? listing.Tarballs.FirstOrDefault() : listing.Tarballs.FirstOrDefault(t => t.Version == version);
        return tarball is null ? null : (listing, tarball);
    }

    /// <summary>Whether an Astra at <paramref name="current"/> is new enough for a plugin that needs <paramref name="min"/> (null = any).</summary>
    public static bool MeetsMinVersion(string? min, string current) => min is null || !PluginInstaller.IsNewer(min, current);

    private static readonly Lazy<List<MarketListing>> BuiltInListings = new(() => Parse(BuiltIn(), null) ?? []);

    /// <summary>
    /// The icon name the built-in list gives the plugin serving provider <paramref name="providerId"/> (magpie's icon
    /// names, e.g. <c>workbuddy-color</c>): the listing that names the provider, else the one for the package
    /// <paramref name="spec"/> installs (a plugin may serve providers its listing doesn't name, e.g. qoder-cn); null
    /// when neither gives an icon. Plugins rarely give their providers a picture of their own, so this is the fallback
    /// the provider rows show.
    /// </summary>
    public static string? BuiltInIcon(string providerId, string? spec = null)
    {
        var withIcon = BuiltInListings.Value.Where(l => l.Icon is not null).ToList();
        return (withIcon.FirstOrDefault(l => l.Providers.Contains(providerId))
                ?? (spec is null ? null : withIcon.FirstOrDefault(l => SameListing(spec, l.Package))))
            ?.Icon;
    }

    // The package spec installs is the listing's, or the same plugin under its former name (magpie-community's
    // zed-auth and the community repository's): their short names are the same.
    private static bool SameListing(string spec, string package)
    {
        var at = spec.LastIndexOf('@');
        var name = at > 0 ? spec[..at] : spec;
        return name == package || PluginSpec.ShortName(name) == PluginSpec.ShortName(package) && name.EndsWith("-auth", StringComparison.Ordinal) == package.EndsWith("-auth", StringComparison.Ordinal);
    }

    /// <summary>
    /// Plugins found by <paramref name="query"/>: the registry's host searches its own entries, npm (OpenCode, magpie and pi
    /// packages) and GitHub's magpie-plugin topic. Nothing without a registry address that has a search beside it.
    /// </summary>
    public async Task<List<MarketSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        var source = Source;
        if (source is null || SearchUrl(source, query) is not { } url) return [];
        if (_searches.TryGetValue(query, out var hit) && hit.Source == source && DateTimeOffset.UtcNow - hit.At < SearchFresh) return hit.Results;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var results = ParseSearch(await http().GetStringAsync(url, timeout.Token));
            _searches[query] = (DateTimeOffset.UtcNow, source, results);
            return results;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return [];
        }
    }

    // …/registry.json has its search at …/search; any other address has none we know of.
    internal static string? SearchUrl(string source, string query) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.AbsolutePath.EndsWith("/" + RegistryFile, StringComparison.Ordinal)
            ? new Uri(uri, "search").GetLeftPart(UriPartial.Path) + "?q=" + Uri.EscapeDataString(query)
            : null;

    internal static List<MarketSearchResult> ParseSearch(string text) =>
        (JsonNode.Parse(text) as JsonArray ?? []).OfType<JsonObject>()
            .Select(o => new MarketSearchResult(Str(o, "spec") ?? "", Str(o, "name") ?? Str(o, "spec") ?? "", Str(o, "description"), Str(o, "version"),
                Str(o, "source") is "astra" or "npm" or "github" ? Str(o, "source")! : "npm", KindOf(Str(o, "kind")),
                o["stars"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0))
            .Where(r => r.Spec.Length > 0)
            .ToList();

    private async Task<(DateTimeOffset At, string? Source, List<MarketListing> List)> RegistryAsync(bool refresh, CancellationToken ct, bool strict = false)
    {
        var source = Source;
        if (!refresh && _registry is { } r && r.Source == source && DateTimeOffset.UtcNow - r.At < Fresh) return r;
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh && _registry is { } again && again.Source == source && DateTimeOffset.UtcNow - again.At < Fresh) return again;
            var baseUri = source is null ? null : new Uri(source);
            if (source is not null)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var text = await http().GetStringAsync(source, timeout.Token);
                    if (Parse(text, baseUri) is { Count: > 0 } fetched)
                    {
                        Directory.CreateDirectory(paths.PluginMarketDir);
                        await File.WriteAllTextAsync(CacheFile, text, ct);
                        _registry = (DateTimeOffset.UtcNow, source, fetched);
                        return _registry.Value;
                    }
                    if (strict) throw new InvalidDataException($"{source} has no plugins, or isn't a plugin registry");
                }
                catch (OperationCanceledException) when (strict && !ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"{source} didn't answer in time");
                }
                catch (Exception e) when (!strict && e is HttpRequestException or OperationCanceledException or IOException)
                {
                    // the copy fetched last, else the built-in one; asked again in a minute
                }
            }
            var fallback = (File.Exists(CacheFile) ? Parse(await File.ReadAllTextAsync(CacheFile, ct), baseUri) : null) ?? Parse(BuiltIn(), baseUri) ?? [];
            _registry = (DateTimeOffset.UtcNow - Fresh + TimeSpan.FromMinutes(1), source, fallback);
            return _registry.Value;
        }
        finally
        {
            _lock.Release();
        }
    }

    // Each entry a valid npm package. Whatever the host lists is taken (it chose what to list); hosted tarballs need a
    // usable address (https, or http on this machine) and a sha256, or the version isn't offered.
    internal static List<MarketListing>? Parse(string text, Uri? baseUri)
    {
        try
        {
            return (JsonNode.Parse(text)?["plugins"] as JsonArray)?.OfType<JsonObject>()
                .Where(e => Str(e, "package") is { } pkg && PluginSpec.IsPackageName(pkg))
                .Select(e => ToListing(e, baseUri))
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MarketListing ToListing(JsonObject e, Uri? baseUri)
    {
        var pkg = Str(e, "package")!;
        var summary = new Dictionary<string, string>();
        if (e["summary"] is JsonObject s)
            foreach (var (lang, text) in s)
                if (text is JsonValue v && v.TryGetValue<string>(out var t)) summary[lang] = t;
        var tarballs = (e["tarballs"] as JsonArray)?.OfType<JsonObject>().Select(t => ToTarball(t, baseUri)).OfType<MarketTarball>().ToList() ?? [];
        tarballs.Sort((a, b) => PluginInstaller.IsNewer(a.Version, b.Version) ? -1 : PluginInstaller.IsNewer(b.Version, a.Version) ? 1 : 0);
        return new MarketListing(pkg, Str(e, "name") ?? pkg, Str(e, "icon"), KindOf(Str(e, "kind")),
            StringList(e["providers"]), Str(e, "replaces"), summary, Str(e, "latest"), Str(e, "description"), Str(e, "license"),
            Str(e, "spec") is { } spec && PluginSpec.IsGit(spec) ? spec : null,
            Str(e, "origin") == "astra" ? "astra" : "upstream", Str(e, "author"), HttpUrl(Str(e, "homepage")), HttpUrl(Str(e, "repository")),
            StringList(e["tags"]), Str(e, "minAstraVersion"), tarballs);
    }

    private static MarketTarball? ToTarball(JsonObject t, Uri? baseUri)
    {
        if (Str(t, "version") is not { } version || Str(t, "url") is not { } url || Str(t, "sha256") is not { } sha256) return null;
        // "/api/…" alone would parse as an absolute file: address on Unix, so only an http(s) one counts as absolute
        Uri? resolved;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") resolved = absolute;
        else if (baseUri is null || !Uri.TryCreate(baseUri, url, out resolved)) return null;
        if (resolved.Scheme != Uri.UriSchemeHttps && !(resolved.IsLoopback && resolved.Scheme == Uri.UriSchemeHttp)) return null;
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return null;
        var size = t["size"] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
        return new MarketTarball(version, resolved.AbsoluteUri, sha256.ToLowerInvariant(), Str(t, "sha512"), size);
    }

    private static string KindOf(string? kind) => kind is "middleware" or "agent" or "pi" ? kind : "provider";

    private static List<string> StringList(JsonNode? n) =>
        (n as JsonArray)?.Select(p => p?.ToString() ?? "").Where(p => p.Length > 0).ToList() ?? [];

    private static string? HttpUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null;

    private static bool IsHttp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";

    private static string BuiltIn()
    {
        using var s = typeof(PluginMarket).Assembly.GetManifestResourceStream("Astra.Plugins.Market.market.json")!;
        return new StreamReader(s).ReadToEnd();
    }

    private static string? Str(JsonNode? o, string key) =>
        o?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is { Length: > 0 } s ? s : null;
}

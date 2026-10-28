namespace Astra.Api.Contracts;

// The plugin market, jobs and host as the admin API serves them. The records live in Astra.Plugins (Contracts cannot reference it),
// so these mirror them field for field — same names, same order, so the JSON on the wire is byte-identical — the way
// MiddlewareStateDto / BunStatusDto mirror theirs (plan avalonia-client §19). PluginEndpoints maps one to the other.

/// <summary>
/// A plugin the market suggests: one entry of the registry. Mirrors <c>Astra.Plugins.Market.MarketListing</c>.
/// </summary>
/// <param name="Kind">provider, middleware, agent or pi.</param>
/// <param name="Summary">By language: en, zh.</param>
/// <param name="Origin">astra (published on the docs site) or upstream (the community registry).</param>
/// <param name="MinAstraVersion">The oldest Astra that can run it; null = any.</param>
public sealed record MarketListingDto(
    string Package, string Name, string? Icon, string Kind, List<string> Providers, string? Replaces,
    Dictionary<string, string> Summary, string? Latest, string? Description, string? License,
    string? Spec, string Origin, string? Author, string? Homepage, string? Repository, List<string> Tags,
    string? MinAstraVersion, List<MarketTarballDto> Tarballs);

/// <summary>One version of a plugin the market hosts. Mirrors <c>Astra.Plugins.Market.MarketTarball</c>.</summary>
public sealed record MarketTarballDto(string Version, string Url, string Sha256, string? Sha512, long Size);

/// <summary>A plugin found by a search. Mirrors <c>Astra.Plugins.Market.MarketSearchResult</c>.</summary>
/// <param name="Spec">What adding it installs: the npm package, or github:owner/repo.</param>
/// <param name="Source">astra (listed in the market), npm or github.</param>
public sealed record MarketSearchResultDto(string Spec, string Name, string? Description, string? Version, string Source, string Kind, int Stars);

/// <summary>A plugin job as the page reads it. Mirrors <c>Astra.Plugins.Packages.PluginJob</c>.</summary>
/// <param name="Action">add, update, remove, runtime or import.</param>
/// <param name="Target">The spec (or name) it acts on; "bun" for the runtime.</param>
/// <param name="State">running, succeeded or failed.</param>
/// <param name="Result">What it produced: the spec an add saved, for instance.</param>
public sealed record PluginJobDto(
    string Id, string Action, string Target, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    List<string> Log, string? Error, string? Result);

/// <summary>
/// How one plugin fared when the host loaded it. Mirrors <c>Astra.Plugins.Host.PluginHostLoaded</c>.
/// </summary>
/// <param name="Provides">The provider ids it signs in to.</param>
/// <param name="ServedBy">Of those, the ones another plugin serves instead (id to that plugin's spec).</param>
public sealed record PluginHostLoadedDto(string Spec, string? Error, List<string> Provides, Dictionary<string, string> ServedBy);

/// <summary>A message a plugin showed the user. Mirrors <c>Astra.Plugins.Host.PluginHostMessage</c>.</summary>
/// <param name="Variant">info, success, warning or error.</param>
public sealed record PluginHostMessageDto(DateTimeOffset At, string Variant, string Title, string Message);

/// <summary>GET /api/plugins/host: the plugin host as the Plugins page shows it. Mirrors <c>Astra.Plugins.Host.PluginHostStatus</c>.</summary>
/// <param name="StartError">Why the last start failed.</param>
/// <param name="GoneReason">Why the host stopped, when it did on its own.</param>
/// <param name="Recent">What plugins showed lately, newest first.</param>
public sealed record PluginHostStatusDto(bool Running, List<PluginHostLoadedDto> Plugins, string? StartError, string? GoneReason, List<PluginHostMessageDto> Recent);

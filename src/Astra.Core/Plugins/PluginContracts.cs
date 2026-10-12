namespace Astra.Core.Plugins;

/// <summary>
/// One installed plugin (table <c>plugins</c>): magpie's plugins.json entry. The list's order is
/// <see cref="SortOrder"/>, which is also the gateway middleware chain's order.
/// </summary>
public sealed class PluginEntry
{
    /// <summary>How it was added: an npm package (name, name@version), a git repository
    /// (github:owner/repo, git+https://…) or an absolute path to a file or folder.</summary>
    public string Spec { get; set; } = "";

    /// <summary>Its package name (the file name for a path), which remove/off/options also find it by.</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>Its options as a JSON object: a middleware's <c>ctx.options</c>, an OpenCode plugin's second argument.</summary>
    public string? OptionsJson { get; set; }

    /// <summary>Where it came from: <see cref="PluginSources"/>.</summary>
    public string? Source { get; set; }

    /// <summary>The version installed, read from its package.json; null for a file without one.</summary>
    public string? InstalledVersion { get; set; }

    public DateTimeOffset AddedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Values of <see cref="PluginEntry.Source"/>.</summary>
public static class PluginSources
{
    public const string Market = "market";

    /// <summary>A package the plugin market hosts itself: updates ask the market for newer versions, not npm.</summary>
    public const string MarketPackage = "market-package";
    public const string Npm = "npm";
    public const string GitHub = "github";
    public const string Git = "git";
    public const string Path = "path";
    public const string MagpieImport = "magpie-import";
}

/// <summary>What a plugin is: a package can be several at once (a provider with middleware, say).</summary>
public static class PluginKinds
{
    /// <summary>An OpenCode plugin with auth/provider hooks, run in the Bun plugin host.</summary>
    public const string Provider = "provider";

    /// <summary>magpie gateway middleware (package.json magpie.middleware, or a *.middleware.js file), run in process.</summary>
    public const string Middleware = "middleware";

    /// <summary>A magpie agent (package.json magpie.agent, or a *.agent.js file): a client Astra can wire up.</summary>
    public const string Agent = "agent";

    /// <summary>A pi coding agent extension, loaded by the host through pi's own loader.</summary>
    public const string Pi = "pi";
}

/// <summary>The installed-plugin list, implemented by the data layer.</summary>
public interface IPluginListStore
{
    /// <summary>Every plugin, in the user's order.</summary>
    Task<IReadOnlyList<PluginEntry>> ListAsync(CancellationToken ct = default);

    Task<PluginEntry?> GetAsync(string spec, CancellationToken ct = default);

    /// <summary>Adds a plugin at the end of the list, or replaces the one with the same spec in place.</summary>
    Task AddAsync(PluginEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Puts <paramref name="entry"/> where <paramref name="oldSpec"/> was — same order, and the
    /// provider picks that named the old spec now name the new one (magpie keeps a plugin's picks when
    /// it is re-added under a new version or spec).
    /// </summary>
    Task ReplaceAsync(string oldSpec, PluginEntry entry, CancellationToken ct = default);

    Task<bool> SetEnabledAsync(string spec, bool enabled, CancellationToken ct = default);

    /// <summary>Sets the options (a JSON object), or clears them with null.</summary>
    Task<bool> SetOptionsAsync(string spec, string? optionsJson, CancellationToken ct = default);

    Task<bool> SetInstalledVersionAsync(string spec, string? version, CancellationToken ct = default);

    /// <summary>Removes the plugin; its provider picks go with it.</summary>
    Task<bool> RemoveAsync(string spec, CancellationToken ct = default);

    /// <summary>Puts the listed specs first, in that order; the rest keep their order after them.</summary>
    Task ReorderAsync(IReadOnlyList<string> specs, CancellationToken ct = default);

    /// <summary>The plugin picked for each provider id more than one plugin serves (magpie's "prefer").</summary>
    Task<IReadOnlyDictionary<string, string>> ListPreferencesAsync(CancellationToken ct = default);

    Task SetPreferenceAsync(string providerId, string spec, CancellationToken ct = default);
}

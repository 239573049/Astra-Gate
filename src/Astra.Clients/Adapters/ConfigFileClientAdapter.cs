using Astra.Clients.Config;
using Astra.Core.Clients;

namespace Astra.Clients.Adapters;

/// <summary>
/// Plumbing for the adapters that write whole keys (or owned array rows) into a few known files: a <see cref="ConfigChange"/>
/// of one key, the removals that undo everything this client recorded, and the "enabled" test on one anchor key.
/// </summary>
public abstract class ConfigFileClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ClientAdapterBase(env, store)
{
    /// <summary>Sets one key of <paramref name="file"/> (format from its extension) to <paramref name="valueJson"/>.</summary>
    protected ConfigChange SetKey(string file, string keyPath, string valueJson, ConfigChangeKind kind = ConfigChangeKind.Key)
    {
        var format = FormatOfPath(file);
        return new ConfigChange(file, format, keyPath, CurrentValue(file, format, keyPath, kind), valueJson, kind);
    }

    /// <summary>Removes one key of <paramref name="file"/>, or null when it is not there.</summary>
    protected ConfigChange? RemoveKey(string file, string keyPath, ConfigChangeKind kind = ConfigChangeKind.Key)
    {
        var format = FormatOfPath(file);
        return CurrentValue(file, format, keyPath, kind) is { } current ? new ConfigChange(file, format, keyPath, current, null, kind) : null;
    }

    /// <summary>
    /// A removal for every key this client recorded that is still present, in recording order. Restore then puts back what the
    /// file held before (or drops the key) for each one the user has not changed since.
    /// </summary>
    protected List<ConfigChange> TrackedRemovals(Func<string, string, bool>? include = null)
    {
        var changes = new List<ConfigChange>();
        foreach (var entry in Store.List(Kind))
        {
            if (include is not null && !include(entry.FilePath, entry.KeyPath)) continue;
            var kind = IsTableKeyPath(entry.KeyPath) ? ConfigChangeKind.Table : ConfigChangeKind.Key;
            if (RemoveKey(entry.FilePath, entry.KeyPath, kind) is { } change) changes.Add(change);
        }
        return changes;
    }

    /// <summary>Whether the key Astra recorded is still in the file exactly as Astra wrote it.</summary>
    protected bool Applied(string file, string keyPath) => StillOurs(file, FormatOfPath(file), keyPath);

    /// <summary>The first absolute path among the named environment variables (a leading <c>~</c> stands for home).</summary>
    protected string? AbsoluteEnv(params string[] names)
    {
        foreach (var name in names)
        {
            var value = Env.GetEnvironmentVariable(name)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = Env.HomeDirectory + value[1..];
            if (Path.IsPathRooted(value)) return value.TrimEnd('/', '\\');
        }
        return null;
    }

    /// <summary>The user's config directory: <c>XDG_CONFIG_HOME</c> when set (Linux), <c>~/.config</c> otherwise.</summary>
    protected string XdgConfigHome =>
        Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Env.Combine(".config");

    /// <summary>The roaming application-data folder of Windows (<c>%APPDATA%</c>).</summary>
    protected string AppData =>
        Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } app ? app : Env.Combine("AppData", "Roaming");

    /// <summary>
    /// Where a desktop app keeps its data: <c>~/Library/Application Support/&lt;name&gt;</c> on macOS, <c>%APPDATA%\&lt;name&gt;</c> on
    /// Windows and <c>$XDG_CONFIG_HOME/&lt;name&gt;</c> elsewhere (the layout Electron, Tauri and Rust's <c>dirs</c> use).
    /// </summary>
    protected string AppSupportDir(params string[] names) => Env.Os switch
    {
        "osx" => Path.Combine([Env.HomeDirectory, "Library", "Application Support", .. names]),
        "windows" => Path.Combine([AppData, .. names]),
        _ => Path.Combine([XdgConfigHome, .. names]),
    };

    /// <summary>The URL of the gateway's <c>/v1</c> root.</summary>
    protected static string V1(EnableContext ctx) => ctx.GatewayBaseUrl.TrimEnd('/') + "/v1";

    /// <summary>A macOS app bundle among the machine's application folders, or null.</summary>
    protected string? FindApp(params string[] names) =>
        Env.ApplicationDirectories.SelectMany(a => names.Select(n => Path.Combine(a, n))).FirstOrDefault(Env.DirectoryExists);
}

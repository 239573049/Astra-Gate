using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core.Clients;

namespace Astra.Clients.Adapters;

/// <summary>
/// The shared plumbing of the file-backed Coexist clients added from magpie (batch 3: Reasonix, Mister Morph, jcode, Mistral Vibe,
/// Empryo, nanobot, miniQ, Paimon, Cline, Grok App, AtomCode, Snow CLI, Ante, Craft Agents, Tuanjie Cowork). An adapter writes keys,
/// tables or array entries and the disable plan is simply "give back everything the state store recorded": the applier skips a value
/// the user changed since (drift) and restores the original (or its absence) of the rest, byte for byte.
/// </summary>
public abstract class FileClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ClientAdapterBase(env, store)
{
    /// <summary>The name of the provider every one of these adapters writes.</summary>
    public const string ProviderId = "astra";

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    /// <summary>The file holding the key whose recorded value tells whether the client is enabled.</summary>
    protected abstract string MarkerFile { get; }

    /// <summary>The key (or table, or array-entry selector) whose recorded value tells whether the client is enabled.</summary>
    protected abstract string MarkerKey { get; }

    /// <summary>Warnings shown while the client is enabled.</summary>
    protected virtual IReadOnlyList<string> EnabledWarnings => [];

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var entries = Store.List(Kind).OrderBy(e => e.FilePath, StringComparer.Ordinal).ThenBy(e => e.KeyPath, StringComparer.Ordinal).ToList();
        // Fields first, then the tables Astra created (so a table goes whole once its fields are back).
        foreach (var entry in entries.Where(e => !IsTableKeyPath(e.KeyPath)))
        {
            var format = FormatOfPath(entry.FilePath);
            var current = CurrentValue(entry.FilePath, format, entry.KeyPath);
            // A value Astra removed (recorded with no applied value) has no current value but is still given back.
            if (current is not null || !entry.OriginalAbsent)
                changes.Add(new ConfigChange(entry.FilePath, format, entry.KeyPath, current, null));
        }
        foreach (var entry in entries.Where(e => IsTableKeyPath(e.KeyPath) && e.OriginalAbsent))
        {
            var format = FormatOfPath(entry.FilePath);
            if (CurrentValue(entry.FilePath, format, entry.KeyPath, ConfigChangeKind.Table) is { } current)
                changes.Add(new ConfigChange(entry.FilePath, format, entry.KeyPath, current, null, ConfigChangeKind.Table));
        }
        return BuildPlan(changes);
    }

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var isTable = IsTableKeyPath(MarkerKey);
        var enabled = Store.Get(Kind, MarkerFile, MarkerKey) is not null
            && (isTable
                ? CurrentValue(MarkerFile, FormatOfPath(MarkerFile), MarkerKey, ConfigChangeKind.Table) is not null
                : StillOurs(MarkerFile, FormatOfPath(MarkerFile), MarkerKey));
        return BuildStatus(detection, enabled, enabled ? EnabledWarnings : []);
    }

    // ---------------------------------------------------------------- change builders

    protected ConfigChange JsonSet(string file, string keyPath, JsonNode value) =>
        new(file, ConfigFileFormat.Json, keyPath, CurrentValue(file, ConfigFileFormat.Json, keyPath), value.ToJsonString());

    protected ConfigChange JsonSet(string file, string keyPath, string value) =>
        new(file, ConfigFileFormat.Json, keyPath, CurrentValue(file, ConfigFileFormat.Json, keyPath), JsonString(value));

    protected ConfigChange YamlSet(string file, string keyPath, JsonNode value) =>
        new(file, ConfigFileFormat.Yaml, keyPath, CurrentValue(file, ConfigFileFormat.Yaml, keyPath), value.ToJsonString());

    protected ConfigChange EnvSet(string file, string key, string value) =>
        new(file, ConfigFileFormat.Env, key, CurrentValue(file, ConfigFileFormat.Env, key), JsonString(value));

    /// <summary>A TOML key set to raw TOML source (<paramref name="raw"/>).</summary>
    protected ConfigChange TomlSet(string file, string keyPath, string raw) =>
        new(file, ConfigFileFormat.Toml, keyPath, CurrentValue(file, ConfigFileFormat.Toml, keyPath), JsonString(raw));

    /// <summary>Takes out a key the client holds today (null when there is none); a restore puts it back.</summary>
    protected ConfigChange? Drop(string file, ConfigFileFormat format, string keyPath) =>
        CurrentValue(file, format, keyPath) is { } current ? new ConfigChange(file, format, keyPath, current, null) : null;

    /// <summary>An array-of-tables selector change: <paramref name="entries"/> replace every entry the selector names.</summary>
    protected ConfigChange TomlEntries(string file, string selector, JsonArray entries) =>
        new(file, ConfigFileFormat.Toml, selector, CurrentValue(file, ConfigFileFormat.Toml, selector, ConfigChangeKind.Table),
            entries.ToJsonString(), ConfigChangeKind.Table);

    /// <summary>
    /// Creates the TOML table with all fields, or, when it already exists, refreshes only the differing fields. A null field is
    /// optional and currently not wanted: it is left out of a new table, and taken out of an existing one only when Astra put it there.
    /// </summary>
    protected IEnumerable<ConfigChange> TableChanges(string file, string tablePath, IReadOnlyList<(string Key, string? Raw)> fields)
    {
        if (CurrentValue(file, ConfigFileFormat.Toml, tablePath, ConfigChangeKind.Table) is null)
        {
            var items = new JsonObject();
            foreach (var (key, raw) in fields)
                if (raw is not null) items[key] = raw;
            yield return new ConfigChange(file, ConfigFileFormat.Toml, tablePath, null, items.ToJsonString(), ConfigChangeKind.Table);
            yield break;
        }
        var tableIsOurs = Store.Get(Kind, file, tablePath)?.OriginalAbsent == true;
        foreach (var (key, raw) in fields)
        {
            var path = $"{tablePath}.{key}";
            var before = CurrentValue(file, ConfigFileFormat.Toml, path);
            if (raw is null)
            {
                if (before is not null && (tableIsOurs || Store.Get(Kind, file, path) is not null))
                    yield return new ConfigChange(file, ConfigFileFormat.Toml, path, before, null);
            }
            else if (!string.Equals(before, raw, StringComparison.Ordinal))
                yield return new ConfigChange(file, ConfigFileFormat.Toml, path, before, JsonString(raw));
        }
    }

    /// <summary>A TOML table of raw values, or null when it is not wanted.</summary>
    protected static string Raw(string value) => TomlValueText.ForString(value);

    protected static string Num(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    protected static string Gateway(EnableContext ctx) => ctx.GatewayBaseUrl.TrimEnd('/') + "/v1";

    /// <summary>Whether <paramref name="id"/> can be the value of an array-element selector (<see cref="JsoncEditor.Selector"/>).</summary>
    protected static bool CanSelect(string key, string id)
    {
        try
        {
            _ = JsoncEditor.Selector((key, id));
            return true;
        }
        catch (EditorException)
        {
            return false;
        }
    }

    /// <summary>
    /// The alias of every model id: <paramref name="prefix"/> plus the id with everything but letters, digits, '_' and '-' turned into
    /// '-' (a TOML bare key; the editor writes no quoted table names).
    /// </summary>
    protected static Dictionary<string, string> Aliases(string prefix, IEnumerable<string> ids, string keep = "")
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var sb = new System.Text.StringBuilder(prefix);
            foreach (var c in id) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' || keep.Contains(c) ? c : '-');
            var alias = sb.ToString();
            for (var n = 2; !taken.Add(alias); n++) alias = $"{sb}-{n}";
            aliases[id] = alias;
        }
        return aliases;
    }

    /// <summary>Home-relative or absolute: a leading <c>~</c> in an environment value is the home directory.</summary>
    protected string ExpandHome(string value)
    {
        if (value.StartsWith('~') && (value.Length == 1 || value[1] is '/' or '\\'))
            return value.Length == 1 ? Env.HomeDirectory : Path.Combine(Env.HomeDirectory, value[2..]);
        return value;
    }
}

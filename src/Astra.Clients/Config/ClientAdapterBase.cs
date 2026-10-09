using Astra.Clients.Editing;
using Astra.Core.Clients;

namespace Astra.Clients.Config;

/// <summary>Shared plumbing for client adapters: config file IO, plan building with diffs, and inspection.</summary>
public abstract class ClientAdapterBase : IClientAdapter
{
    protected ClientAdapterBase(ClientEnvironment env, IClientConfigStateStore store)
    {
        Env = env;
        Store = store;
    }

    protected ClientEnvironment Env { get; }

    protected IClientConfigStateStore Store { get; }

    public abstract string Kind { get; }

    public abstract ClientMode Mode { get; }

    public abstract ClientAvailability Availability { get; }

    public virtual string? UnavailableReason => null;

    public abstract ClientDetection Detect();

    public abstract IReadOnlyList<string> ConfigPaths();

    public abstract ConfigChangePlan PlanEnable(EnableContext ctx);

    public abstract ConfigChangePlan PlanDisable();

    public abstract ConfigChangePlan PlanPurge();

    public abstract ClientStatus Inspect();

    // ---------------------------------------------------------------- helpers for subclasses

    /// <summary>The primary config file path (first existing candidate, else the first candidate).</summary>
    protected string PrimaryConfigPath(IReadOnlyList<string> candidates) =>
        candidates.FirstOrDefault(Env.FileExists) ?? candidates[0];

    protected (string Text, bool Bom) ReadFile(string path) => Env.ReadTextWithBom(path) ?? ("", false);

    /// <summary>Builds the plan and computes a unified diff per affected file by simulating the changes.</summary>
    protected ConfigChangePlan BuildPlan(IReadOnlyList<ConfigChange> changes)
    {
        var diffs = new List<FileDiff>();
        var warnings = new List<string>();
        foreach (var group in changes.GroupBy(c => c.File))
        {
            var ops = ConfigEditorOps.Create(group.First().Format, ReadFile(group.Key).Text);
            var before = ops.Text;
            foreach (var change in group) ops.Apply(change, warnings);
            if (ops.Text != before)
                diffs.Add(new FileDiff(group.Key, DiffTool.Unified(group.Key, before, ops.Text)));
        }
        return new ConfigChangePlan(Kind, changes, diffs);
    }

    /// <summary>Current value of a key through the same machinery the applier uses.</summary>
    protected string? CurrentValue(string file, ConfigFileFormat format, string keyPath, ConfigChangeKind kind = ConfigChangeKind.Key)
    {
        var ops = ConfigEditorOps.Create(format, ReadFile(file).Text);
        return ops.Read(new ConfigChange(file, format, keyPath, null, null, kind));
    }

    /// <summary>TOML key paths that were recorded as table entries (subclasses with table entries override this).</summary>
    protected virtual bool IsTableKeyPath(string keyPath) => false;

    /// <summary>The file format of a config file, derived from its name.</summary>
    protected static ConfigFileFormat FormatOfPath(string path) =>
        path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) ? ConfigFileFormat.Toml
        : path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
            ? ConfigFileFormat.Yaml
        : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)
            ? ConfigFileFormat.Json
            : path.Contains(".env", StringComparison.Ordinal) ? ConfigFileFormat.Env
            : ConfigFileFormat.Json;

    /// <summary>Builds the status card: enabled as given, drifted keys from state comparisons.</summary>
    protected ClientStatus BuildStatus(ClientDetection detection, bool enabled, IReadOnlyList<string> warnings)
    {
        var drifted = new List<string>();
        var appliedKeys = new List<string>();
        foreach (var entry in Store.List(Kind))
        {
            var format = FormatOfPath(entry.FilePath);
            var kind = IsTableKeyPath(entry.KeyPath) ? ConfigChangeKind.Table : ConfigChangeKind.Key;
            var current = CurrentValue(entry.FilePath, format, entry.KeyPath, kind);
            var matches = ConfigValueCodec.EqualsValue(format, current, entry.AppliedValueJson);
            if (matches) appliedKeys.Add(entry.KeyPath);
            else drifted.Add(entry.KeyPath);
        }
        return new ClientStatus
        {
            ClientKind = Kind,
            Detection = detection,
            Enabled = enabled,
            DriftedKeys = drifted,
            AppliedKeys = appliedKeys,
            Warnings = warnings,
        };
    }

    /// <summary>Shorthand for a .NET string as JSON text.</summary>
    protected static string JsonString(string value) => Astra.Core.Json.EncodeString(value);

    /// <summary>
    /// The bound provider's models from the "models" extra (<c>{"…": {"id": …, "name": …}}</c>), in order and
    /// without duplicates; the selected model is appended when the list does not contain it.
    /// </summary>
    protected static IReadOnlyList<(string Id, string? Name)> ModelEntries(EnableContext ctx)
    {
        var result = new List<(string Id, string? Name)>();
        foreach (var (_, value) in ctx.ExtraObject("models") ?? new System.Text.Json.Nodes.JsonObject())
        {
            if (value is not System.Text.Json.Nodes.JsonObject m) continue;
            var id = (m["id"] as System.Text.Json.Nodes.JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            if (string.IsNullOrEmpty(id) || result.Any(e => e.Id == id)) continue;
            var name = (m["name"] as System.Text.Json.Nodes.JsonValue)?.TryGetValue(out string? n) == true ? n : null;
            result.Add((id, string.IsNullOrEmpty(name) ? null : name));
        }
        if (!string.IsNullOrEmpty(ctx.Model) && result.All(e => e.Id != ctx.Model)) result.Add((ctx.Model, null));
        return result;
    }

    /// <summary>
    /// Per-model capability hints from the "models" extra (<c>contextWindow</c>, <c>maxOutputTokens</c>, <c>vision</c>,
    /// <c>reasoning</c>), keyed by model id; ids without hints are absent.
    /// </summary>
    protected static IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> ModelHints(EnableContext ctx)
    {
        var hints = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.Ordinal);
        foreach (var (_, value) in ctx.ExtraObject("models") ?? new System.Text.Json.Nodes.JsonObject())
        {
            if (value is System.Text.Json.Nodes.JsonObject m && (m["id"] as System.Text.Json.Nodes.JsonValue)?.TryGetValue(out string? id) == true
                && !string.IsNullOrEmpty(id))
                hints.TryAdd(id, m);
        }
        return hints;
    }

    /// <summary>A positive integer hint (e.g. <c>contextWindow</c>), or null when missing or not a positive number.</summary>
    protected static long? PositiveLong(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var l) && l > 0 ? l : null;

    /// <summary>A boolean hint (e.g. <c>vision</c>), or null when missing or not a boolean.</summary>
    protected static bool? BoolHint(System.Text.Json.Nodes.JsonNode? node) =>
        node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out bool b) ? b : null;

    /// <summary>True when Astra recorded a value for the key and the file still holds exactly that value (not edited since).</summary>
    protected bool StillOurs(string file, ConfigFileFormat format, string keyPath, ConfigChangeKind kind = ConfigChangeKind.Key)
    {
        var entry = Store.Get(Kind, file, keyPath);
        return entry is not null && ConfigValueCodec.EqualsValue(format, CurrentValue(file, format, keyPath, kind), entry.AppliedValueJson);
    }

    /// <summary>The string a JSON value text holds, or null when it is absent or not a JSON string.</summary>
    protected static string? StringOf(string? jsonText)
    {
        if (jsonText is null) return null;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(jsonText) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string? s) ? s : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

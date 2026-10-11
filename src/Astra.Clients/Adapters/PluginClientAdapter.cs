using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Clients.Editing;
using Astra.Core.Clients;
using Astra.Core.Plugins;

namespace Astra.Clients.Adapters;

/// <summary>
/// A client a plugin adds (a magpie agent plugin): the plugin names one file, its format and the key of the
/// model; its connect returns the other keys to write (where the gateway is, its key). Astra does every write,
/// through <see cref="ClientConfigApplier"/>, so what the user had under each key is kept and put back exactly
/// as for a built-in client. Disabling needs no plugin code — it is planned from the recorded state alone, so
/// <c>restore-all</c> works offline and after the plugin is gone (<paramref name="agents"/> is then null and the
/// descriptor comes from the client row's snapshot).
/// </summary>
public sealed class PluginClientAdapter(
    ClientEnvironment env, IClientConfigStateStore store, AgentPluginDescriptor agent, IAgentPluginCatalog? agents)
    : ClientAdapterBase(env, store)
{
    /// <summary>The descriptor this adapter was built from (kept in the client row for offline restores).</summary>
    public AgentPluginDescriptor Agent => agent;

    public override string Kind => ClientKinds.PluginPrefix + agent.Id;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => agents is not null ? ClientAvailability.Available : ClientAvailability.ComingSoon;

    public override string? UnavailableReason =>
        agents is null ? $"The plugin that adds {agent.Name} ({agent.Spec}) isn't installed or is switched off: it can only be restored." : null;

    /// <summary>The key of the model in the file, as the plugin names it.</summary>
    public string ModelKeyPath => agent.Model;

    /// <summary>The file the plugin names, with ~ and $VAR expanded against this user's home ($HOME is the home).</summary>
    public string ConfigFile => Expand(agent.Config);

    private ConfigFileFormat Format => agent.Format switch
    {
        "yaml" => ConfigFileFormat.Yaml,
        "env" => ConfigFileFormat.Env,
        _ => ConfigFileFormat.Json,
    };

    public override ClientDetection Detect()
    {
        if (agent.Bin is { Length: > 0 } bin && Env.FindTool(bin) is { } exe) return ClientDetection.Found(null, $"executable {exe}");
        if (agent.Dir is { Length: > 0 } dir && Env.DirectoryExists(Expand(dir))) return ClientDetection.Found(null, $"folder {Expand(dir)}");
        var file = ConfigFile;
        return Env.FileExists(file) ? ClientDetection.Found(null, $"config file {file}") : ClientDetection.NotFound($"no {file}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    private List<string> _stale = [];

    /// <summary>
    /// The model (as the plugin spells it: its prefix and the model id) and connect's keys. A key written before
    /// that connect no longer returns is left to <see cref="PlanRestoreStale"/>, which puts back what the user had.
    /// </summary>
    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        if (agents is null) throw new EditorException(UnavailableReason!);
        var file = ConfigFile;
        RequireWritable(file);
        var models = ModelEntries(ctx);
        var hints = ModelHints(ctx);
        var model = ctx.Model ?? (models.Count > 0 ? models[0].Id : null);
        var list = new JsonArray();
        JsonObject? chosen = null;
        foreach (var (id, name) in models)
        {
            var m = MagpieModel(id, name, hints.GetValueOrDefault(id));
            if (id == model) chosen = (JsonObject)m.DeepClone();
            list.Add((JsonNode)m);
        }
        var input = new JsonObject
        {
            ["gateway"] = new JsonObject
            {
                ["url"] = ctx.GatewayBaseUrl.TrimEnd('/'),
                ["v1"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
                ["key"] = ctx.LocalKey,
            },
            ["model"] = chosen ?? (model is null ? null : MagpieModel(model, null, null)),
            ["models"] = list,
            ["agent"] = new JsonObject { ["id"] = agent.Id, ["config"] = file },
        };

        JsonObject? keys;
        try
        {
            keys = agents.Connect(agent.Id, input);
        }
        catch (InvalidOperationException e)
        {
            throw new EditorException($"{agent.Spec}: {e.Message}");
        }

        var changes = new List<ConfigChange>();
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (model is not null)
        {
            changes.Add(Change(file, agent.Model, JsonValue.Create((agent.Prefix ?? "") + model)!));
            wanted.Add(agent.Model);
        }
        foreach (var (key, value) in keys ?? [])
        {
            if (key == agent.Model || !wanted.Add(key)) continue;
            changes.Add(Change(file, key, value));
        }
        _stale = Store.List(Kind).Where(s => !(s.FilePath == file && wanted.Contains(s.KeyPath))).Select(s => s.KeyPath).ToList();
        return BuildPlan(changes);
    }

    /// <summary>Puts back what the user had under every key Astra wrote, as recorded.</summary>
    public override ConfigChangePlan PlanDisable() =>
        BuildPlan(Store.List(Kind).Select(s => new ConfigChange(s.FilePath, Format, s.KeyPath,
            CurrentValue(s.FilePath, Format, s.KeyPath), s.OriginalAbsent ? null : s.OriginalValueJson)).ToList());

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var file = ConfigFile;
        var drifted = new List<string>();
        var applied = new List<string>();
        foreach (var entry in Store.List(Kind))
        {
            var matches = ConfigValueCodec.EqualsValue(Format, CurrentValue(entry.FilePath, Format, entry.KeyPath), entry.AppliedValueJson);
            (matches ? applied : drifted).Add(entry.KeyPath);
        }
        var modelState = Store.Get(Kind, file, agent.Model);
        var enabled = modelState is not null && applied.Contains(agent.Model);
        var warnings = new List<string>();
        if (agent.Notice is { Length: > 0 } notice) warnings.Add(notice);
        if (agent.Library is not null)
            warnings.Add("Its plugin also names where it reads instructions, MCP servers and skills; Astra doesn't write those.");
        return new ClientStatus
        {
            ClientKind = Kind,
            Detection = detection,
            Enabled = enabled,
            DriftedKeys = drifted,
            AppliedKeys = applied,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// The keys Astra wrote before that the last <see cref="PlanEnable"/>'s connect no longer returned: restored
    /// (with <see cref="ClientConfigApplier.Disable"/>, byte-exact and skipping any the user changed) once that
    /// plan is applied, as magpie puts back a key its plugin stopped asking for.
    /// </summary>
    public ConfigChangePlan PlanRestoreStale() =>
        BuildPlan(Store.List(Kind).Where(s => _stale.Contains(s.KeyPath)).Select(s => new ConfigChange(s.FilePath, Format, s.KeyPath,
            CurrentValue(s.FilePath, Format, s.KeyPath), s.OriginalAbsent ? null : s.OriginalValueJson)).ToList());

    // A value as the file's format writes it: JSON and YAML take JSON text; an env file takes the raw text,
    // JSON-quoted in the plan (a value that isn't a string is written as its JSON, null as empty).
    private ConfigChange Change(string file, string key, JsonNode? value)
    {
        string after;
        if (Format == ConfigFileFormat.Env)
        {
            var raw = value switch
            {
                null => "",
                JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
                _ => value.ToJsonString(),
            };
            if (raw.Contains('\n') || raw.Contains('\r')) throw new EditorException($"{agent.Spec}: {key} has more than one line, which a .env file can't hold");
            after = JsonString(DotEnvEditor.FormatValue(raw));
        }
        else
        {
            after = value?.ToJsonString() ?? "null";
        }
        return new ConfigChange(file, Format, key, CurrentValue(file, Format, key), after);
    }

    // A model as magpie's connect is given it: {id, name, context, output, images, efforts}.
    private static JsonObject MagpieModel(string id, string? name, JsonObject? hint) => new()
    {
        ["id"] = id,
        ["name"] = name ?? id,
        ["context"] = Number(hint?["contextWindow"]),
        ["output"] = Number(hint?["maxOutputTokens"]),
        ["images"] = hint?["vision"] is JsonValue v && v.TryGetValue<bool>(out var images) && images,
        ["efforts"] = new JsonArray(),
    };

    private static long Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && long.TryParse(v.ToJsonString(), out var n) ? n : 0;

    // A plugin names the file it takes over, so it may only be a config of the user's own: under their home, and
    // never Astra's data, their keys or their shell's start-up files.
    private static readonly string[] Guarded = [".astra", ".ssh", ".gnupg", ".aws", ".kube", ".docker", ".config/gh"];
    private static readonly string[] GuardedFiles = [".bashrc", ".bash_profile", ".profile", ".zshrc", ".zprofile", ".zshenv", ".gitconfig", ".netrc"];

    private void RequireWritable(string file)
    {
        var home = Path.GetFullPath(Env.HomeDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var astraHome = Env.GetEnvironmentVariable("ASTRA_HOME") is { Length: > 0 } ah ? Path.GetFullPath(ah).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar : null;
        string? why = null;
        if (!file.StartsWith(home, comparison)) why = "it is outside your home folder";
        else if (astraHome is not null && file.StartsWith(astraHome, comparison)) why = "it is Astra's own data";
        else
        {
            var rel = file[home.Length..].Replace(Path.DirectorySeparatorChar, '/');
            if (Guarded.Any(g => rel.Equals(g, comparison) || rel.StartsWith(g + "/", comparison)) || GuardedFiles.Any(f => rel.Equals(f, comparison)))
                why = "it holds credentials or start-up settings";
        }
        if (why is not null) throw new EditorException($"{agent.Name} would write {file}, which a plugin may not take over: {why}");
    }

    private string Expand(string path)
    {
        var p = System.Text.RegularExpressions.Regex.Replace(path, @"\$(\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}|(?<n>[A-Za-z_][A-Za-z0-9_]*))",
            m => m.Groups["n"].Value == "HOME" ? Env.HomeDirectory : Env.GetEnvironmentVariable(m.Groups["n"].Value) ?? "");
        if (p == "~") return Env.HomeDirectory;
        if (p.StartsWith("~/", StringComparison.Ordinal)) p = Env.Combine(p[2..]);
        return Path.GetFullPath(p);
    }
}

using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Reasonix Studio 2.x and its native CLI (Coexist) share <c>config.toml</c> and <c>.env</c> in <c>REASONIX_HOME</c>, else
/// <c>%APPDATA%\reasonix</c> on Windows and <c>~/.reasonix</c> elsewhere. Astra owns one <c>[[providers]]</c> entry named <c>astra</c>
/// (<c>kind = "openai"</c>, <c>base_url</c> the gateway's <c>/v1</c>, <c>api_key_env</c> naming <c>ASTRA_REASONIX_API_KEY</c>, the bound
/// provider's models with per-model <c>model_overrides</c> for vision / context window / output limit) and the key itself in
/// <c>.env</c> under that name; the model in use is <c>default_model = "astra/&lt;id&gt;"</c>. A <c>[desktop] provider_access</c> list the
/// user keeps gets <c>"astra"</c> appended (Studio hides providers that are not in it). The Planner model and the effort are the user's
/// own choices in Reasonix and are left alone. Verified against magpie's wiring (Reasonix Studio 2.x, native 1.39.x / 2.x CLI) only.
/// </summary>
public sealed class ReasonixClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    public const string KeyVariable = "ASTRA_REASONIX_API_KEY";

    private const string Selector = "providers[name=astra]";

    private const string AccessPath = "desktop.provider_access";

    private string Dir
    {
        get
        {
            if (Env.GetEnvironmentVariable("REASONIX_HOME") is { Length: > 0 } home) return home;
            if (Env.Os == "windows" && Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } app) return Path.Combine(app, "reasonix");
            return Env.Combine(".reasonix");
        }
    }

    private string ConfigFile => Path.Combine(Dir, "config.toml");

    private string EnvFile => Path.Combine(Dir, ".env");

    public override string Kind => ClientKinds.Reasonix;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => Selector;

    protected override bool IsTableKeyPath(string keyPath) => keyPath == Selector;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Reasonix reads its settings at start-up: start a new Reasonix process to load this. A project or session model can still override the Executor and Planner."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("reasonix");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no reasonix executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile, EnvFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var hints = ModelHints(ctx);
        var entries = ModelEntries(ctx);
        var models = string.Join(", ", entries.Select(e => Raw(e.Id)));
        var overrides = new List<string>();
        foreach (var (id, _) in entries)
        {
            var info = hints.GetValueOrDefault(id);
            var parts = new List<string>();
            if (BoolHint(info?["reasoning"]) == false) parts.Add("reasoning_protocol = \"none\"");
            if (BoolHint(info?["vision"]) is { } vision) parts.Add("vision = " + (vision ? "true" : "false"));
            if (PositiveLong(info?["contextWindow"]) is { } context) parts.Add("context_window = " + Num(context));
            if (PositiveLong(info?["maxOutputTokens"]) is { } output) parts.Add("max_output_tokens = " + Num(output));
            if (parts.Count > 0) overrides.Add($"{Raw(id)} = {{ {string.Join(", ", parts)} }}");
        }
        var selected = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries.Count > 0 ? entries[0].Id : null;
        var entry = new JsonObject
        {
            ["name"] = Raw(ProviderId),
            ["kind"] = Raw("openai"),
            ["base_url"] = Raw(Gateway(ctx)),
            ["api_key_env"] = Raw(KeyVariable),
            ["models"] = $"[{models}]",
        };
        if (selected is not null) entry["default"] = Raw(selected);
        entry["reasoning_protocol"] = Raw("openai");
        entry["no_proxy"] = "true";
        if (overrides.Count > 0) entry["model_overrides"] = $"{{ {string.Join(", ", overrides)} }}";

        var changes = new List<ConfigChange> { TomlEntries(file, Selector, new JsonArray(entry)), EnvSet(EnvFile, KeyVariable, ctx.LocalKey) };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(TomlSet(file, "default_model", Raw($"{ProviderId}/{ctx.Model}")));
        if (CurrentValue(file, ConfigFileFormat.Toml, AccessPath) is { } access && AppendToArray(access, ProviderId) is { } widened)
            changes.Add(TomlSet(file, AccessPath, widened));
        return BuildPlan(changes);
    }

    /// <summary>The raw TOML array <paramref name="array"/> with <paramref name="value"/> added; null when it holds it already or is no array.</summary>
    public static string? AppendToArray(string array, string value)
    {
        var text = array.Trim();
        if (!text.StartsWith('[') || !text.EndsWith(']')) return null;
        var quoted = Raw(value);
        var inner = text[1..^1].Trim().TrimEnd(',').TrimEnd();
        if (inner.Contains(quoted, StringComparison.Ordinal)) return null;
        return inner.Length == 0 ? $"[{quoted}]" : $"[{inner}, {quoted}]";
    }
}

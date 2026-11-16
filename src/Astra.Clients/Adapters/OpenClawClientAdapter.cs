using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// OpenClaw (openclaw/openclaw, a personal assistant that answers on WhatsApp, Telegram, Discord and the rest; npm <c>openclaw</c>;
/// Coexist). One <c>openclaw.json</c>: <c>OPENCLAW_CONFIG_PATH</c>, else <c>openclaw.json</c> in <c>OPENCLAW_STATE_DIR</c>, else in
/// <c>~/.openclaw-&lt;profile&gt;</c> for a named <c>OPENCLAW_PROFILE</c>, else in <c>~/.openclaw</c> — where <c>~</c> is <c>OPENCLAW_HOME</c>
/// when set. Astra adds <c>models.providers.astra</c> (<c>baseUrl</c> the gateway's <c>/v1</c>, a literal <c>apiKey</c>,
/// <c>api: openai-completions</c>, one entry per model with <c>input</c>, <c>reasoning</c> and <c>contextWindow</c>; the schema is strict, so
/// only those keys are written) and, when a model is chosen, makes <c>astra/&lt;model&gt;</c> the primary of <c>agents.defaults.model</c> (a ref or
/// <c>{primary, fallbacks}</c>; the fallbacks stay) and adds <c>astra/*</c> to a non-empty <c>agents.defaults.modelPolicy.allow</c>. OpenClaw takes
/// the change without a restart, but a chat already pinned to a model keeps it until <c>/model default</c>. Not written: a per-agent model, an
/// <c>$include</c>-split config and the pre-2026.8.1 <c>agents.defaults.models</c> allow-list (its keys contain dots). The file is read as JSONC; JSON5
/// with bare keys is refused. Verified against magpie's wiring only.
/// </summary>
public sealed partial class OpenClawClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>What OpenClaw is told of a model whose context window is not known.</summary>
    public const int DefaultContextWindow = 128000;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*$", RegexOptions.IgnoreCase)]
    private static partial Regex ProfilePattern();

    private string Home
    {
        get
        {
            var h = Env.GetEnvironmentVariable("OPENCLAW_HOME")?.Trim();
            return string.IsNullOrEmpty(h) || h is "undefined" or "null" ? Env.HomeDirectory : Tilde(h);
        }
    }

    private string Tilde(string p)
    {
        if (p == "~") return Env.HomeDirectory;
        if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal)) return Path.Combine(Env.HomeDirectory, p[2..]);
        return p;
    }

    private string StateDir
    {
        get
        {
            var home = Home;
            var dir = Path.Combine(home, ".openclaw");
            if (Env.GetEnvironmentVariable("OPENCLAW_PROFILE")?.Trim() is { Length: > 0 } profile
                && !profile.Equals("default", StringComparison.OrdinalIgnoreCase) && profile.Length <= 64 && ProfilePattern().IsMatch(profile))
                dir = Path.Combine(home, ".openclaw-" + profile);
            if (Env.GetEnvironmentVariable("OPENCLAW_STATE_DIR")?.Trim() is { Length: > 0 } state) dir = Tilde(state);
            return dir;
        }
    }

    private string ConfigFile =>
        Env.GetEnvironmentVariable("OPENCLAW_CONFIG_PATH")?.Trim() is { Length: > 0 } path ? Tilde(path) : Path.Combine(StateDir, "openclaw.json");

    private static string ProviderPath => $"models.providers.{ProviderId}";

    private const string ModelPath = "agents.defaults.model";

    private const string AllowPath = "agents.defaults.modelPolicy.allow";

    public override string Kind => ClientKinds.OpenClaw;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = StateDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"state directory {dir}");
        var exe = Env.FindOnPath("openclaw");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no openclaw executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, _) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            models.AddNode(new JsonObject
            {
                ["id"] = id,
                ["name"] = id,
                ["reasoning"] = BoolHint(info?["reasoning"]) ?? false,
                ["input"] = BoolHint(info?["vision"]) == true ? new JsonArray("text", "image") : new JsonArray("text"),
                ["contextWindow"] = PositiveLong(info?["contextWindow"]) ?? DefaultContextWindow,
            });
        }
        var provider = new JsonObject
        {
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["apiKey"] = ctx.LocalKey,
            ["api"] = "openai-completions",
            ["models"] = models,
        };
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, ProviderPath, CurrentValue(file, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            var reference = ProviderId + "/" + ctx.Model;
            var current = CurrentValue(file, ConfigFileFormat.Json, ModelPath);
            var model = JsonNode.Parse(current ?? "null") is JsonObject existing
                ? existing.DeepClone().AsObject()
                : null;
            string after;
            if (model is not null)
            {
                model["primary"] = reference;
                after = model.ToJsonString();
            }
            else
            {
                after = JsonString(reference);
            }
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ModelPath, current, after));

            if (JsonNode.Parse(CurrentValue(file, ConfigFileFormat.Json, AllowPath) ?? "null") is JsonArray { Count: > 0 } allow
                && !allow.Any(a => a is JsonValue v && v.TryGetValue(out string? s) && (s == "*" || s == reference || s == ProviderId + "/*")))
            {
                var widened = (JsonArray)allow.DeepClone();
                widened.AddNode(JsonValue.Create(ProviderId + "/*"));
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, AllowPath, allow.ToJsonString(), widened.ToJsonString()));
            }
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var entry in Store.List(Kind))
            if (CurrentValue(entry.FilePath, ConfigFileFormat.Json, entry.KeyPath) is { } current)
                changes.Add(new ConfigChange(entry.FilePath, ConfigFileFormat.Json, entry.KeyPath, current, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildStatus(detection, enabled, enabled
            ? ["OpenClaw takes this up without a restart. A chat that already has a model keeps it: send /model default in that chat to move it onto Astra."]
            : []);
    }
}

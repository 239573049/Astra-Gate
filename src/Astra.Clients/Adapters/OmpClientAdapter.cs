using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// omp / oh-my-pi (github.com/can1357/oh-my-pi, a fork of Pi that runs on Bun; Coexist): adds <c>providers.astra</c>
/// (<c>api: openai-completions</c>, <c>baseUrl</c>, a literal <c>apiKey</c>, the bound provider's models) to
/// <c>models.yml</c> and, when a model is chosen, sets <c>modelRoles.default</c> to <c>astra/&lt;id&gt;</c> in <c>config.yml</c>, both
/// in the agent directory <c>~/.omp/agent</c> (<c>PI_CODING_AGENT_DIR</c> overrides it; named profiles are not handled).
/// Schema verified against omp.sh/docs/providers and the project README (2026-10); <c>apiKey</c> is looked up as an
/// environment variable name first and used literally otherwise. The <c>.yaml</c> spelling of both files is honored when it
/// already exists. omp's own providers stay untouched.
/// <para>
/// omp resolves <c>api</c> and <c>baseUrl</c> per model, so a model whose native protocol is Responses, Anthropic or Gemini
/// carries its own <c>api</c> (and base, see <see cref="PiAiModels"/>). The provider sets <c>auth: apiKey</c>: without it omp
/// shapes every <c>anthropic-messages</c> request like Claude Code (its fingerprint headers and system blocks), which the
/// gateway would take for Claude Code itself. Limits Astra does not know are left out, so omp takes them from its own
/// catalog for a known model id.
/// </para>
/// </summary>
public sealed class OmpClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    public const string ModelRefPrefix = ProviderId + "/";

    private string AgentDir =>
        Env.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } dir ? dir : Env.Combine(".omp", "agent");

    private string ModelsFile => Existing("models");

    private string ConfigFile => Existing("config");

    private static string ProviderPath => $"providers.{ProviderId}";

    private const string DefaultRolePath = "modelRoles.default";

    public override string Kind => ClientKinds.Omp;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = AgentDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("omp");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no omp executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ModelsFile, ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var baseUrl = PiAiModels.BaseUrlFor(ctx.GatewayBaseUrl, ApiProtocol.OpenAIChat);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var model = new JsonObject { ["id"] = id, ["name"] = name ?? id };
            if (protocols[id] != ApiProtocol.OpenAIChat)
            {
                model["api"] = PiAiModels.ApiOf(protocols[id]);
                var own = PiAiModels.BaseUrlFor(ctx.GatewayBaseUrl, protocols[id]);
                if (own != baseUrl) model["baseUrl"] = own;
            }
            PiAiModels.AddHints(model, hints.GetValueOrDefault(id));
            // omp rejects an output limit that leaves no room for input.
            if (PositiveLong(model["contextWindow"]) is { } context && PositiveLong(model["maxTokens"]) is { } maxTokens && maxTokens >= context)
                model["maxTokens"] = Math.Max(1, context / 4);
            models.AddNode(model);
        }
        var provider = new JsonObject
        {
            ["baseUrl"] = baseUrl,
            ["api"] = "openai-completions",
            ["apiKey"] = ctx.LocalKey,
            ["auth"] = "apiKey",
        };
        if (models.Count > 0) provider["models"] = models;

        var changes = new List<ConfigChange>
        {
            new(ModelsFile, ConfigFileFormat.Yaml, ProviderPath, CurrentValue(ModelsFile, ConfigFileFormat.Yaml, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, DefaultRolePath,
                CurrentValue(ConfigFile, ConfigFileFormat.Yaml, DefaultRolePath), JsonString(ModelRefPrefix + ctx.Model)));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(ModelsFile, ConfigFileFormat.Yaml, ProviderPath);
        if (providerBefore is not null)
            changes.Add(new ConfigChange(ModelsFile, ConfigFileFormat.Yaml, ProviderPath, providerBefore, null));
        // A model the user picked afterwards (another provider) is theirs and stays.
        var role = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, DefaultRolePath);
        if (Store.Get(Kind, ConfigFile, DefaultRolePath) is not null
            && StringOf(role)?.StartsWith(ModelRefPrefix, StringComparison.Ordinal) == true)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, DefaultRolePath, role, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), StillOurs(ModelsFile, ConfigFileFormat.Yaml, ProviderPath), []);

    /// <summary>The existing <c>.yml</c> / <c>.yaml</c> file of a name (the <c>.yml</c> one wins), else the <c>.yml</c> path.</summary>
    private string Existing(string name)
    {
        var yml = Path.Combine(AgentDir, name + ".yml");
        var yaml = Path.Combine(AgentDir, name + ".yaml");
        return !Env.FileExists(yml) && Env.FileExists(yaml) ? yaml : yml;
    }
}

using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Pencil (pen.dev, the design canvas whose agent runs models; Coexist): a custom provider of one's own goes in
/// <c>~/.pencil/models.json</c> in Pi's format. Astra adds <c>providers.astra</c> (<c>api: openai-completions</c> at the gateway's
/// <c>/v1</c>, a literal key, and the bound provider's models, each with every field Pencil writes for one: Pi's reasoning /
/// input / limit hints, a zero cost and an empty <c>compat</c>). Pencil's settings offer a custom provider on OpenAI Completions,
/// OpenAI Responses or Anthropic Messages for the provider as a whole, so every model goes over Completions, which the gateway
/// translates. The model is picked in Pencil's agent composer, not in a file, so Astra only decides whether its models are in that
/// picker; Pencil reads the file at start-up. Verified against magpie's wiring (and docs.pen.dev's authentication page) only.
/// </summary>
public sealed class PencilClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string ProviderId = "astra";

    private const long DefaultContextWindow = 128_000;
    private const long DefaultMaxTokens = 16_384;

    private string Dir => Env.Combine(".pencil");

    private string ModelsFile => Path.Combine(Dir, "models.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.Pencil;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.DirectoryExists(Dir)) return ClientDetection.Found(null, $"config directory {Dir}");
        // No command is looked up: `pencil` and `pen` are common names. The Mac app is found by its bundle (named pen.dev since the rename).
        var app = FindApp("Pencil.app", "pen.dev.app");
        return app is not null ? ClientDetection.Found(null, $"application {app}") : ClientDetection.NotFound($"no {Dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ModelsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var model = new JsonObject { ["id"] = id };
            if (name is not null) model["name"] = name;
            PiAiModels.AddHints(model, hints.GetValueOrDefault(id));
            model["input"] ??= new JsonArray("text");
            var context = PositiveLong(model["contextWindow"]) ?? DefaultContextWindow;
            model["contextWindow"] = context;
            model["maxTokens"] ??= Math.Min(DefaultMaxTokens, context);
            model["cost"] = new JsonObject { ["input"] = 0, ["output"] = 0, ["cacheRead"] = 0, ["cacheWrite"] = 0 };
            model["compat"] = new JsonObject();
            models.AddNode(model);
        }
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["baseUrl"] = V1(ctx),
            ["api"] = "openai-completions",
            ["apiKey"] = ctx.LocalKey,
            ["models"] = models,
        };
        return BuildPlan([SetKey(ModelsFile, ProviderPath, provider.ToJsonString())]);
    }

    public override ConfigChangePlan PlanDisable() => BuildPlan(TrackedRemovals());

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), Applied(ModelsFile, ProviderPath), []);
}

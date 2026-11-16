using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Cline's CLI (3.x, npm <c>cline</c>; Coexist) keeps its providers in <c>data/settings/providers.json</c> under <c>CLINE_DIR</c>
/// (<c>~/.cline</c> by default; <c>CLINE_DATA_DIR</c> moves <c>data</c>): <c>{"version":1,"lastUsedProvider":"&lt;id&gt;","modes":{},
/// "providers":{"&lt;id&gt;":{"settings":{provider, apiKey, model, baseUrl, headers, reasoning},"tokenSource":"manual"}}}</c>; a session runs the
/// model of <c>lastUsedProvider</c>. A provider of one's own added to <c>models.json</c> is refused at session start (cline/cline#14180),
/// so Astra takes Cline's built-in <c>openai-compatible</c> provider, which lists models from the gateway's <c>/v1/models</c>, makes it
/// <c>lastUsedProvider</c>, and puts the bound provider's models in <c>models.json</c> beside it (the entry Cline's own migration writes:
/// <c>{provider:{name,baseUrl,defaultModelId}, models:{…}}</c>), which takes the place of the built-in list. What those keys held is
/// recorded and put back. Cline's requests name only the AI SDK, so a <c>User-Agent: cline</c> header says they are Cline's.
/// <para>
/// Not written: the VS Code extension's state (<c>globalState.json</c> / <c>secrets.json</c>, which it consults first), Cline's desktop
/// app (it keeps its pick in a web view's localStorage), the entry's <c>updatedAt</c> (adapters have no clock) and the reasoning effort
/// (the user's own choice). Verified against magpie's wiring (Cline CLI 3.x) only.
/// </para>
/// </summary>
public sealed class ClineClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string Slot = "openai-compatible";

    private static string ProviderPath => $"providers.{Slot}";

    private string Dir => Env.GetEnvironmentVariable("CLINE_DIR") is { Length: > 0 } dir ? dir : Env.Combine(".cline");

    private string DataDir => Env.GetEnvironmentVariable("CLINE_DATA_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Dir, "data");

    private string ProvidersFile => Path.Combine(DataDir, "settings", "providers.json");

    private string ModelsFile => Path.Combine(DataDir, "settings", "models.json");

    public override string Kind => ClientKinds.Cline;

    protected override string MarkerFile => ProvidersFile;

    protected override string MarkerKey => ProviderPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Cline reads its provider as a session starts: open sessions keep the model they have, new ones use this. Reload VS Code's window for its extension (not configured here)."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("cline");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no cline executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProvidersFile, ModelsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var entries = ModelEntries(ctx);
        var model = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries.FirstOrDefault().Id;
        // Cline needs a model to start on: with none there is nothing to write.
        if (string.IsNullOrEmpty(model)) return BuildPlan([]);
        var v1 = Gateway(ctx);
        var hints = ModelHints(ctx);
        var models = new JsonObject();
        foreach (var (id, name) in entries)
        {
            var info = hints.GetValueOrDefault(id);
            var capabilities = new JsonArray("streaming", "tools");
            if (BoolHint(info?["vision"]) == true) capabilities.AddNode("images");
            if (BoolHint(info?["reasoning"]) == true) capabilities.AddNode("reasoning");
            var entry = new JsonObject { ["id"] = id, ["name"] = name ?? id, ["capabilities"] = capabilities };
            if (PositiveLong(info?["contextWindow"]) is { } context) entry["contextWindow"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } output) entry["maxTokens"] = output;
            models[id] = entry;
        }
        var listing = new JsonObject
        {
            ["provider"] = new JsonObject { ["name"] = "Astra", ["baseUrl"] = v1, ["defaultModelId"] = model },
            ["models"] = models,
        };
        var provider = new JsonObject
        {
            ["settings"] = new JsonObject
            {
                ["provider"] = Slot,
                ["apiKey"] = ctx.LocalKey,
                ["model"] = model,
                ["baseUrl"] = v1,
                ["headers"] = new JsonObject { ["User-Agent"] = "cline" },
            },
            ["tokenSource"] = "manual",
        };

        var changes = new List<ConfigChange>();
        // Cline refuses a file without its envelope: a file Astra makes carries the one Cline writes itself.
        if (!Env.FileExists(ProvidersFile))
        {
            changes.Add(JsonSet(ProvidersFile, "version", JsonValue.Create(1)!));
            changes.Add(JsonSet(ProvidersFile, "modes", new JsonObject()));
        }
        changes.Add(JsonSet(ProvidersFile, ProviderPath, provider));
        changes.Add(JsonSet(ProvidersFile, "lastUsedProvider", Slot));
        if (!Env.FileExists(ModelsFile)) changes.Add(JsonSet(ModelsFile, "version", JsonValue.Create(1)!));
        changes.Add(JsonSet(ModelsFile, ProviderPath, listing));
        return BuildPlan(changes);
    }
}

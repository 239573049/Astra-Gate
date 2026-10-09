using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Zed (Coexist): adds <c>language_models.openai_compatible.astra</c> — <c>api_url</c> and the bound provider's models as
/// <c>available_models</c> — to <c>settings.json</c> and, when a model is chosen, selects it with
/// <c>agent.default_model</c> = <c>{provider: "astra", model}</c>. The file is <c>~/.config/zed/settings.json</c> on macOS,
/// <c>$XDG_CONFIG_HOME/zed/settings.json</c> on Linux and <c>%APPDATA%\Zed\settings.json</c> on Windows (verified against
/// zed.dev docs and crates/paths, 2026-10; Zed has no config-directory variable). The provider object is owned as a whole.
/// <b>Zed never reads an API key from settings.json</b> (its docs: "Do not put API keys in settings.json"; keys live in the
/// system keychain, or in the <c>ASTRA_API_KEY</c> environment variable), so the token cannot be written: <see cref="Inspect"/>
/// always warns that it must be pasted into Zed's provider settings once. That the <c>agent.default_model</c> provider is the
/// configured provider id follows from Zed's provider-id semantics; the docs show no verbatim example for it.
/// </summary>
public sealed class ZedClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>Context window used when the model catalog does not know it (Zed requires <c>max_tokens</c>).</summary>
    public const long DefaultContextWindow = 128_000;

    /// <summary>Shown on the Zed card at all times; never contains the token itself.</summary>
    public const string TokenWarning =
        "Zed never reads API keys from settings.json. Open Zed's agent settings, choose the Astra provider and paste an Astra "
        + "token from the Tokens page once (or set the ASTRA_API_KEY environment variable before starting Zed).";

    private string ConfigDir => Env.Os switch
    {
        "windows" => Path.Combine(
            Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData ? appData : Env.Combine("AppData", "Roaming"), "Zed"),
        "linux" => Path.Combine(
            Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Env.Combine(".config"), "zed"),
        _ => Env.Combine(".config", "zed"),
    };

    private string ConfigFile => Path.Combine(ConfigDir, "settings.json");

    private static string ProviderPath => $"language_models.openai_compatible.{ProviderId}";

    private const string DefaultModelPath = "agent.default_model";

    public override string Kind => ClientKinds.Zed;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("zed") ?? Env.FindOnPath("zeditor");
        if (exe is not null) return ClientDetection.Found(null, $"executable {exe}");
        var app = Env.ApplicationDirectories.Select(a => Path.Combine(a, "Zed.app")).FirstOrDefault(Env.DirectoryExists);
        return app is not null ? ClientDetection.Found(null, $"application {app}") : ClientDetection.NotFound($"no {dir} and no zed executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var model = new JsonObject
            {
                ["name"] = id,
                ["display_name"] = name ?? id,
                ["max_tokens"] = PositiveLong(info?["contextWindow"]) ?? DefaultContextWindow,
            };
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxOutput) model["max_output_tokens"] = maxOutput;
            model["capabilities"] = new JsonObject
            {
                ["tools"] = true,
                ["images"] = BoolHint(info?["vision"]) ?? false,
                ["parallel_tool_calls"] = false,
                ["prompt_cache_key"] = false,
            };
            models.AddNode(model);
        }
        var provider = new JsonObject { ["api_url"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1" };
        if (models.Count > 0) provider["available_models"] = models;

        var changes = new List<ConfigChange>
        {
            new(ConfigFile, ConfigFileFormat.Json, ProviderPath, CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            var selection = new JsonObject { ["provider"] = ProviderId, ["model"] = ctx.Model };
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, DefaultModelPath,
                CurrentValue(ConfigFile, ConfigFileFormat.Json, DefaultModelPath), selection.ToJsonString()));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        if (providerBefore is not null)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, providerBefore, null));
        // A model the user selected afterwards (another provider) is theirs and stays.
        if (Store.Get(Kind, ConfigFile, DefaultModelPath) is not null
            && CurrentValue(ConfigFile, ConfigFileFormat.Json, DefaultModelPath) is { } selected && SelectsAstra(selected))
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, DefaultModelPath, selected, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath), [TokenWarning]);

    private static bool SelectsAstra(string selectionJson)
    {
        try
        {
            return JsonNode.Parse(selectionJson) is JsonObject o && (o["provider"] as JsonValue)?.TryGetValue(out string? p) == true && p == ProviderId;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// QwenPaw (agentscope-ai/QwenPaw, formerly CoPaw; command <c>qwenpaw</c>; Coexist). Everything is read from its working folder:
/// <c>QWENPAW_WORKING_DIR</c>, else <c>~/.copaw</c> when a legacy one is there, else <c>~/.qwenpaw</c>; its secret folder is
/// <c>QWENPAW_SECRET_DIR</c> (environment or the working folder's <c>.env</c>), else <c>&lt;working folder&gt;.secret</c>. Two files are written.
/// The provider is <c>&lt;secret&gt;/providers/custom/astra.json</c>, a custom provider as QwenPaw's console "Add provider" saves one: <c>id</c>,
/// <c>name</c>, <c>base_url</c> (the gateway's <c>/v1</c>), <c>api_key</c>, <c>chat_model: OpenAIChatModel</c> (Chat Completions) and one entry of
/// <c>extra_models</c> per bound model; the keys QwenPaw fills in itself (<c>enabled</c>, <c>generate_kwargs</c>, …) are added only when absent. QwenPaw
/// encrypts a plaintext <c>api_key</c> at its next start (<c>ENC:…</c>), which Astra does not count as drift and does not overwrite until the
/// key rotates. The model is the <c>active_model</c> (<c>{provider_id, model}</c>) of the default agent's <c>agent.json</c> in its workspace
/// (<c>agents.profiles.default.workspace_dir</c> of <c>config.json</c>, else <c>workspaces/default</c>), written only when that file exists and the
/// agent's <c>backend</c> is <c>qwenpaw</c>; other agents are the user's to switch in QwenPaw's console. QwenPaw reads the provider files once at
/// start, so it must be restarted. A symlinked working folder is not resolved (QwenPaw names the secret folder after where it leads).
/// Verified against magpie's wiring only.
/// </summary>
public sealed class QwenPawClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private const string Encrypted = "ENC:";

    private string Tilde(string p)
    {
        if (p == "~") return Env.HomeDirectory;
        if (p.StartsWith("~/", StringComparison.Ordinal) || p.StartsWith("~\\", StringComparison.Ordinal)) return Path.Combine(Env.HomeDirectory, p[2..]);
        return p;
    }

    private string WorkingDir
    {
        get
        {
            if (Env.GetEnvironmentVariable("QWENPAW_WORKING_DIR")?.Trim() is { Length: > 0 } dir) return Tilde(dir);
            var legacy = Env.Combine(".copaw");
            return Env.DirectoryExists(legacy) ? legacy : Env.Combine(".qwenpaw");
        }
    }

    private string SecretDir
    {
        get
        {
            var work = WorkingDir;
            var dir = Env.GetEnvironmentVariable("QWENPAW_SECRET_DIR")?.Trim();
            if (string.IsNullOrEmpty(dir)) dir = DotEnvValue(Path.Combine(work, ".env"), "QWENPAW_SECRET_DIR");
            return string.IsNullOrEmpty(dir) ? work + ".secret" : Tilde(dir);
        }
    }

    /// <summary>The value of <paramref name="key"/> in a <c>.env</c> file as python-dotenv reads it (last one wins); empty when absent.</summary>
    private string DotEnvValue(string file, string key)
    {
        var text = Env.ReadTextOrNull(file);
        if (text is null) return "";
        var value = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].Trim();
            var eq = line.IndexOf('=');
            if (eq < 0 || line[..eq].Trim() != key) continue;
            var v = line[(eq + 1)..].Trim();
            if (v.Length >= 2 && (v[0] == '"' || v[0] == '\''))
            {
                var end = v.IndexOf(v[0], 1);
                if (end >= 0) v = v[1..end];
            }
            else if (v.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0)
            {
                v = v[..hash].Trim();
            }
            value = v;
        }
        return value;
    }

    private string ProviderFile => Path.Combine(SecretDir, "providers", "custom", ProviderId + ".json");

    private string AgentWorkspace
    {
        get
        {
            var work = WorkingDir;
            if (Env.ReadTextOrNull(Path.Combine(work, "config.json")) is { } text)
            {
                try
                {
                    if (JsonNode.Parse(text) is JsonObject root
                        && root["agents"]?["profiles"]?["default"]?["workspace_dir"] is JsonValue v && v.TryGetValue(out string? dir)
                        && !string.IsNullOrWhiteSpace(dir))
                        return Tilde(dir.Trim());
                }
                catch (JsonException)
                {
                }
            }
            return Path.Combine(work, "workspaces", "default");
        }
    }

    private string AgentFile => Path.Combine(AgentWorkspace, "agent.json");

    public override string Kind => ClientKinds.QwenPaw;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var work = WorkingDir;
        if (Env.DirectoryExists(work)) return ClientDetection.Found(null, $"working folder {work}");
        var exe = Env.FindOnPath("qwenpaw");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {work} and no qwenpaw executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProviderFile, AgentFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ProviderFile;
        var hints = ModelHints(ctx);
        var changes = new List<ConfigChange>();
        void Set(string key, string valueJson, bool onlyWhenAbsent = false)
        {
            var current = CurrentValue(file, ConfigFileFormat.Json, key);
            if (onlyWhenAbsent && current is not null) return;
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, key, current, valueJson));
        }

        var extra = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var vision = BoolHint(info?["vision"]) == true;
            var model = new JsonObject
            {
                ["id"] = id,
                ["name"] = name ?? id,
                ["source"] = "user",
                ["supports_image"] = vision,
                ["supports_multimodal"] = vision,
                ["supports_tool_calling"] = true,
            };
            if (PositiveLong(info?["contextWindow"]) is { } window and >= 1000)
            {
                model["max_input_length"] = window;
                model["max_input_length_configured"] = true;
            }
            if (PositiveLong(info?["maxOutputTokens"]) is { } output) model["max_output_length"] = output;
            extra.AddNode(model);
        }
        Set("id", JsonString(ProviderId));
        Set("name", JsonString("Astra"));
        Set("base_url", JsonString(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1"));
        // QwenPaw encrypts a plaintext key at its next start; that stays until the key rotates.
        var currentKey = StringOf(CurrentValue(file, ConfigFileFormat.Json, "api_key"));
        var keepEncrypted = currentKey is not null && currentKey.StartsWith(Encrypted, StringComparison.Ordinal)
            && Store.Get(Kind, file, "api_key")?.AppliedValueJson == JsonString(ctx.LocalKey);
        if (!keepEncrypted) Set("api_key", JsonString(ctx.LocalKey));
        Set("chat_model", JsonString("OpenAIChatModel"));
        Set("is_custom", "true");
        Set("enabled", "true", onlyWhenAbsent: true);
        Set("support_model_discovery", "false", onlyWhenAbsent: true);
        Set("support_connection_check", "false", onlyWhenAbsent: true);
        Set("models", "[]", onlyWhenAbsent: true);
        Set("generate_kwargs", "{}", onlyWhenAbsent: true);
        Set("custom_headers", "{}", onlyWhenAbsent: true);
        Set("snapshot_schema_version", "2", onlyWhenAbsent: true);
        Set("extra_models", extra.ToJsonString());

        if (!string.IsNullOrEmpty(ctx.Model) && Env.FileExists(AgentFile) && AgentUsesQwenPaw())
        {
            var current = CurrentValue(AgentFile, ConfigFileFormat.Json, "active_model");
            var slot = JsonNode.Parse(current ?? "null") is JsonObject existing ? existing.DeepClone().AsObject() : new JsonObject();
            slot["provider_id"] = ProviderId;
            slot["model"] = ctx.Model;
            changes.Add(new ConfigChange(AgentFile, ConfigFileFormat.Json, "active_model", current, slot.ToJsonString()));
        }
        return BuildPlan(changes);
    }

    private bool AgentUsesQwenPaw() =>
        StringOf(CurrentValue(AgentFile, ConfigFileFormat.Json, "backend")) is not { Length: > 0 } backend || backend.Trim() == "qwenpaw";

    private static bool EncryptedKey(string? current) =>
        StringOf(current) is { } s && s.StartsWith(Encrypted, StringComparison.Ordinal);

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        keyPath == "api_key" && EncryptedKey(current) || base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var entry in Store.List(Kind))
        {
            if (CurrentValue(entry.FilePath, ConfigFileFormat.Json, entry.KeyPath) is not { } current) continue;
            changes.Add(new ConfigChange(entry.FilePath, ConfigFileFormat.Json, entry.KeyPath, current, null)
            {
                StillOurs = entry.KeyPath == "api_key"
                    ? (now, applied) => EncryptedKey(now) || ConfigValueCodec.EqualsValue(ConfigFileFormat.Json, now, applied)
                    : null,
            });
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ProviderFile, ConfigFileFormat.Json, "base_url");
        var warnings = new List<string>();
        if (enabled)
        {
            warnings.Add("QwenPaw reads its provider files once, at start-up: restart QwenPaw to use this (until then a turn on an Astra model fails with \"Provider 'astra' not found\").");
            if (!Env.FileExists(AgentFile))
                warnings.Add($"QwenPaw has no {AgentFile} yet: start QwenPaw once so it writes its agents' settings, then enable again to pick Astra's model for the default agent.");
            else if (!AgentUsesQwenPaw())
                warnings.Add("The default agent runs on another backend, which does not use QwenPaw's models: switch it to QwenPaw's own backend.");
        }
        return BuildStatus(detection, enabled, warnings);
    }
}

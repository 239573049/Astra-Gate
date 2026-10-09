using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>
/// CC Switch: <c>~/.cc-switch/cc-switch.db</c> (table <c>providers</c>, one row per provider and app type), or the
/// legacy <c>~/.cc-switch/config.json</c>. Queries go by column name and tolerate missing optional columns, because
/// CC Switch migrates its schema often.
/// </summary>
public sealed class CcSwitchReader(ImportContext ctx) : IImportSource
{
    private const string Table = "providers";
    private static readonly string[] Required = ["id", "app_type", "name", "settings_config"];

    public string Id => ImportSourceIds.CcSwitch;
    public string Name => "CC Switch";

    public ImportSourceResult Read()
    {
        var db = ctx.Env.Combine(".cc-switch", "cc-switch.db");
        var legacy = ctx.Env.Combine(".cc-switch", "config.json");
        if (ctx.Env.FileExists(db)) return ReadDatabase(db);
        if (ctx.Env.FileExists(legacy)) return ReadLegacy(legacy);
        return new ImportSourceResult { Id = Id, Name = Name, Path = db, Found = false };
    }

    private ImportSourceResult ReadDatabase(string path)
    {
        var result = new ImportSourceResult { Id = Id, Name = Name, Path = path, Found = true };
        using var db = ctx.Db.Open(path);
        if (!db.HasTable(Table) || Required.Any(c => !db.Columns(Table).Contains(c, StringComparer.OrdinalIgnoreCase)))
        {
            result.Error = $"Unsupported CC Switch database layout (user_version {db.UserVersion}): table 'providers' lacks the expected columns";
            return result;
        }
        foreach (var row in db.Rows(Table))
        {
            var appType = row["app_type"] ?? "";
            var id = row["id"] ?? "";
            var settings = ImportJson.Parse(row["settings_config"]);
            var candidate = FromSettings(appType, id, row["name"] ?? id, row.GetValueOrDefault("website_url"), settings);
            if (candidate is not null) result.Items.Add(candidate);
        }
        return result;
    }

    private ImportSourceResult ReadLegacy(string path)
    {
        var result = new ImportSourceResult { Id = Id, Name = Name, Path = path, Found = true };
        if (ImportJson.Parse(ctx.Env.ReadTextOrNull(path)) is not JsonObject root)
        {
            result.Error = "config.json is not valid JSON";
            return result;
        }
        foreach (var (appType, app) in root)
        {
            if (app?["providers"] is not JsonObject providers) continue;
            foreach (var (id, node) in providers)
            {
                if (node is not JsonObject p) continue;
                var candidate = FromSettings(appType, id, ImportJson.Str(p["name"]) ?? id, ImportJson.Str(p["websiteUrl"]), p["settingsConfig"]);
                if (candidate is not null) result.Items.Add(candidate);
            }
        }
        return result;
    }

    private ImportCandidate? FromSettings(string appType, string id, string name, string? website, JsonNode? settings)
    {
        var c = new ImportCandidate { Ref = $"{Id}:{appType}:{id}", Source = Id, Name = name, FromApp = appType, Website = HttpOnly(website) };
        if (settings is not JsonObject s) { c.SkipReason = ImportSkipReasons.NoBaseUrl; return c; }
        switch (appType)
        {
            case "claude" or "claude-desktop": Claude(c, s); break;
            case "codex": Codex(c, s); break;
            case "gemini": Gemini(c, s); break;
            case "opencode" or "openclaw" or "pi" or "hermes": Generic(c, s); break;
            default: return null; // an app type this version of Astra does not know
        }
        return c;
    }

    private static void Claude(ImportCandidate c, JsonObject s)
    {
        var env = s["env"] as JsonObject;
        var baseUrl = ImportJson.Str(env, "ANTHROPIC_BASE_URL");
        var token = ImportJson.Str(env, "ANTHROPIC_AUTH_TOKEN");
        var apiKey = ImportJson.Str(env, "ANTHROPIC_API_KEY");
        if (baseUrl is null && token is null && apiKey is null) { c.SkipReason = ImportSkipReasons.OfficialLogin; return; }
        c.Endpoints.Add(new ImportEndpoint(ApiProtocol.Anthropic, baseUrl ?? "https://api.anthropic.com"));
        c.ApiKey = token ?? apiKey;
        c.AuthScheme = token is not null ? AuthSchemes.Bearer : AuthSchemes.XApiKey;
        foreach (var slot in ClaudeModelVars)
            if (ImportJson.Str(env, slot) is { } model) c.Models.Add(model);
    }

    internal static readonly string[] ClaudeModelVars =
    [
        "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_SMALL_FAST_MODEL",
    ];

    private static void Codex(ImportCandidate c, JsonObject s)
    {
        var configText = ImportJson.Str(s["config"]);
        var config = CodexConfig.Parse(configText);
        var apiKey = ImportJson.Str(s["auth"]?["OPENAI_API_KEY"]);
        var selected = config?.SelectedProvider();
        if (selected is null && apiKey is null && config?.TopString("openai_base_url") is null)
        {
            c.SkipReason = ImportSkipReasons.OfficialLogin;
            return;
        }
        var baseUrl = selected?.BaseUrl ?? config?.TopString("openai_base_url") ?? "https://api.openai.com/v1";
        var protocol = selected?.WireApi == "chat" ? ApiProtocol.OpenAIChat : ApiProtocol.OpenAIResponses;
        c.Endpoints.Add(new ImportEndpoint(protocol, baseUrl));
        c.ApiKey = selected?.BearerToken ?? apiKey;
        if (config?.TopString("model") is { } model) c.Models.Add(model);
        if (selected is not null) foreach (var (k, v) in selected.Headers) c.Headers[k] = v;
    }

    private static void Gemini(ImportCandidate c, JsonObject s)
    {
        var env = s["env"] as JsonObject;
        var apiKey = ImportJson.Str(env, "GEMINI_API_KEY", "GOOGLE_API_KEY");
        var baseUrl = ImportJson.Str(env, "GOOGLE_GEMINI_BASE_URL");
        if (apiKey is null && baseUrl is null) { c.SkipReason = ImportSkipReasons.OfficialLogin; return; }
        c.Endpoints.Add(new ImportEndpoint(ApiProtocol.Gemini, baseUrl ?? "https://generativelanguage.googleapis.com/v1beta"));
        c.ApiKey = apiKey;
        c.AuthScheme = AuthSchemes.XGoogApiKey;
        if (ImportJson.Str(env, "GEMINI_MODEL") is { } model) c.Models.Add(model);
    }

    /// <summary>opencode / openclaw / pi / hermes: the connection fields sit at the top level or under <c>options</c>.</summary>
    private static void Generic(ImportCandidate c, JsonObject s)
    {
        var options = s["options"] as JsonObject;
        var baseUrl = ImportJson.Str(options, "baseUrl", "baseURL", "base_url") ?? ImportJson.Str(s, "baseUrl", "baseURL", "base_url");
        if (baseUrl is null) { c.SkipReason = ImportSkipReasons.NoBaseUrl; return; }
        var protocol = ImportJson.ProtocolFromHint(ImportJson.Str(s, "api", "npm", "type") ?? ImportJson.Str(options, "api", "npm", "type"));
        c.Endpoints.Add(new ImportEndpoint(protocol, baseUrl));
        c.ApiKey = ImportJson.Str(options, "apiKey", "api_key") ?? ImportJson.Str(s, "apiKey", "api_key");
        c.AuthScheme = ImportJson.AuthFor(c.Endpoints);
        c.Models = ImportJson.ModelIds(s["models"] ?? options?["models"]);
        foreach (var (k, v) in ImportJson.Headers(options?["headers"] ?? s["headers"])) c.Headers[k] = v;
    }

    private static string? HttpOnly(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null;
}

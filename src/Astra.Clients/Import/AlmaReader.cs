using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>
/// Alma: table <c>providers</c> of <c>&lt;UserConfigDir&gt;/alma/chat_threads.db</c>. Login-based types are skipped;
/// built-in API types with an empty base URL fall back to the vendor's well-known endpoint, which the server then
/// matches to the Astra template by host. The layout is reconstructed from Magpie's importer, not from a real Alma
/// database; columns are looked up by name and missing optional ones tolerated.
/// </summary>
public sealed class AlmaReader(ImportContext ctx) : IImportSource
{
    private const string Table = "providers";

    /// <summary>Provider types that are logins (OAuth / CLI sessions), not API keys.</summary>
    private static readonly HashSet<string> LoginTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "acp", "copilot", "claude-subscription", "codex-subscription", "gemini-cli", "antigravity", "cursor",
    };

    private static readonly Dictionary<string, (ApiProtocol Protocol, string BaseUrl)> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openai"] = (ApiProtocol.OpenAIChat, "https://api.openai.com/v1"),
        ["anthropic"] = (ApiProtocol.Anthropic, "https://api.anthropic.com/v1"),
        ["google"] = (ApiProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta"),
        ["gemini"] = (ApiProtocol.Gemini, "https://generativelanguage.googleapis.com/v1beta"),
        ["deepseek"] = (ApiProtocol.OpenAIChat, "https://api.deepseek.com/v1"),
        ["openrouter"] = (ApiProtocol.OpenAIChat, "https://openrouter.ai/api/v1"),
        ["moonshot"] = (ApiProtocol.OpenAIChat, "https://api.moonshot.cn/v1"),
        ["xai"] = (ApiProtocol.OpenAIChat, "https://api.x.ai/v1"),
        ["mistral"] = (ApiProtocol.OpenAIChat, "https://api.mistral.ai/v1"),
        ["groq"] = (ApiProtocol.OpenAIChat, "https://api.groq.com/openai/v1"),
        ["together"] = (ApiProtocol.OpenAIChat, "https://api.together.xyz/v1"),
        ["fireworks"] = (ApiProtocol.OpenAIChat, "https://api.fireworks.ai/inference/v1"),
        ["siliconflow"] = (ApiProtocol.OpenAIChat, "https://api.siliconflow.com/v1"),
        ["ollama"] = (ApiProtocol.OpenAIChat, "http://127.0.0.1:11434/v1"),
        ["lmstudio"] = (ApiProtocol.OpenAIChat, "http://127.0.0.1:1234/v1"),
    };

    public string Id => ImportSourceIds.Alma;
    public string Name => "Alma";

    public ImportSourceResult Read()
    {
        var path = DatabasePath();
        var result = new ImportSourceResult { Id = Id, Name = Name, Path = path };
        if (!ctx.Env.FileExists(path)) return result;
        result.Found = true;
        using var db = ctx.Db.Open(path);
        if (!db.HasTable(Table) || !db.Columns(Table).Contains("id", StringComparer.OrdinalIgnoreCase))
        {
            result.Error = "Unsupported Alma database layout: table 'providers' not found";
            return result;
        }
        foreach (var row in db.Rows(Table))
        {
            var id = row["id"] ?? "";
            var type = row.GetValueOrDefault("type") ?? "";
            var c = new ImportCandidate
            {
                Ref = $"{Id}:{id}", Source = Id, Name = row.GetValueOrDefault("name") is { Length: > 0 } n ? n : id,
                FromApp = type, ApiKey = row.GetValueOrDefault("api_key"),
                Off = row.GetValueOrDefault("enabled") is "0" or "false",
                Headers = ImportJson.Headers(ImportJson.Parse(row.GetValueOrDefault("custom_headers"))),
                Models = ImportJson.ModelIds(ImportJson.Parse(row.GetValueOrDefault("models"))),
            };
            if (LoginTypes.Contains(type)) { c.SkipReason = ImportSkipReasons.OfficialLogin; result.Items.Add(c); continue; }

            var builtIn = BuiltIn.TryGetValue(type, out var known) ? known : (ApiProtocol.OpenAIChat, "");
            var baseUrl = row.GetValueOrDefault("base_url") is { Length: > 0 } b ? b : builtIn.Item2;
            var format = row.GetValueOrDefault("api_format")?.ToLowerInvariant();
            var protocol = format switch
            {
                "anthropic" => ApiProtocol.Anthropic,
                "gemini" => ApiProtocol.Gemini,
                "openai-responses" or "responses" => ApiProtocol.OpenAIResponses,
                _ when row.GetValueOrDefault("is_response_api") is "1" or "true" => ApiProtocol.OpenAIResponses,
                _ => builtIn.Item1,
            };
            c.Endpoints.Add(new ImportEndpoint(protocol, baseUrl));
            c.AuthScheme = ImportJson.AuthFor(c.Endpoints);
            result.Items.Add(c);
        }
        return result;
    }

    /// <summary><c>UserConfigDir/alma/chat_threads.db</c>: Application Support on macOS, %APPDATA% on Windows, XDG config on Linux.</summary>
    private string DatabasePath()
    {
        var env = ctx.Env;
        var root = env.Os switch
        {
            "osx" => env.Combine("Library", "Application Support"),
            "windows" => env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData ? appData : env.Combine("AppData", "Roaming"),
            _ => env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : env.Combine(".config"),
        };
        return Path.Combine(root, "alma", "chat_threads.db");
    }
}

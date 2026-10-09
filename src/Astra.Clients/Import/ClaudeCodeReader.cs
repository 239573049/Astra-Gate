using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Import;
using Astra.Core.Models;

namespace Astra.Clients.Import;

/// <summary>
/// Claude Code: <c>env.*</c> of <c>$CLAUDE_CONFIG_DIR/settings.json</c> (default <c>~/.claude/settings.json</c>, JSONC).
/// When Astra has taken the client over, the values in the file are Astra's own; the user's originals are in
/// <c>client_config_state</c> and are read from there so Astra never imports itself.
/// </summary>
public sealed class ClaudeCodeReader(ImportContext ctx) : IImportSource
{
    public string Id => ImportSourceIds.ClaudeCode;
    public string Name => "Claude Code";

    public ImportSourceResult Read()
    {
        var dir = ctx.Env.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } d ? d : ctx.Env.Combine(".claude");
        var file = Path.Combine(dir, "settings.json");
        var result = new ImportSourceResult { Id = Id, Name = Name, Path = file };
        var text = ctx.Env.ReadTextOrNull(file);
        if (text is null) return result;
        result.Found = true;
        if (ImportJson.Parse(text) is not JsonObject root)
        {
            result.Error = "settings.json is not valid JSON";
            return result;
        }
        var env = root["env"] as JsonObject;
        string? Effective(string key) => EffectiveValue(file, env, key);

        var baseUrl = Effective("ANTHROPIC_BASE_URL");
        var token = Effective("ANTHROPIC_AUTH_TOKEN");
        var apiKey = Effective("ANTHROPIC_API_KEY");
        // Neither a gateway nor a key: Claude Code is signed in with an Anthropic account, which is not importable.
        if (baseUrl is null && token is null && apiKey is null) return result;

        var c = new ImportCandidate
        {
            Ref = $"{Id}:default", Source = Id, Name = "Claude Code", FromApp = "claude-code",
            ApiKey = token ?? apiKey, AuthScheme = token is not null ? AuthSchemes.Bearer : AuthSchemes.XApiKey,
        };
        c.Endpoints.Add(new ImportEndpoint(ApiProtocol.Anthropic, baseUrl ?? "https://api.anthropic.com"));
        foreach (var slot in CcSwitchReader.ClaudeModelVars)
            if (Effective(slot) is { } model) c.Models.Add(model);
        if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)) c.Name = $"Claude Code · {u.Host}";
        result.Items.Add(c);
        return result;
    }

    /// <summary>The user's own value of <c>env.{key}</c>: the recorded original while Astra's value is still in place.</summary>
    private string? EffectiveValue(string file, JsonObject? env, string key)
    {
        var current = ImportJson.Str(env?[key]);
        var entry = ctx.State.Get(ClientKinds.ClaudeCode, file, $"env.{key}");
        if (entry is null) return current;
        var applied = ImportJson.Decode(entry.AppliedValueJson);
        if (applied != current) return current; // the user changed it since: their value wins
        return entry.OriginalAbsent ? null : ImportJson.Decode(entry.OriginalValueJson);
    }
}

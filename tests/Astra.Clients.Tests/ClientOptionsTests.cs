using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Core.Clients;

namespace Astra.Clients.Tests;

/// <summary>Client-specific options (<see cref="ClientOptions"/>): what the Claude Code and Codex adapters write for them.</summary>
public class ClientOptionsTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;

    public ClientOptionsTests() => _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);

    public void Dispose() => _home.Dispose();

    private static JsonObject Extras(JsonObject options, JsonObject? models = null) =>
        new() { ["models"] = models, ["options"] = options };

    private JsonNode ClaudeEnv()
    {
        var path = Path.Combine(_home.Env.Combine(".claude"), "settings.json");
        return JsonNode.Parse(_home.ReadFile(path))!["env"]!;
    }

    [Fact]
    public void ClaudeCode_Context1M_AppendsSuffix_ExceptHaikuAndKnownSmallWindows()
    {
        var adapter = new ClaudeCodeClientAdapter(_home.Env, _store);
        var models = new JsonObject
        {
            ["ANTHROPIC_MODEL"] = "claude-sonnet-4-5",
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = "glm-small[1m]",
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = "claude-sonnet-4-5[1m]",
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = "claude-haiku-4-5",
        };
        var ctx = new EnableContext
        {
            GatewayBaseUrl = TestGateway.BaseUrl, LocalKey = "astra-claude-x",
            Extras = Extras(new JsonObject { ["context1m"] = true }, models),
            ModelsWithoutContext1M = new HashSet<string> { "glm-small[1m]" },
        };

        _applier.Apply(adapter.PlanEnable(ctx));

        var env = ClaudeEnv();
        Assert.Equal("claude-sonnet-4-5[1m]", env["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Equal("glm-small[1m]", env["ANTHROPIC_DEFAULT_OPUS_MODEL"]!.GetValue<string>()); // untouched, not doubled
        Assert.Equal("claude-sonnet-4-5[1m]", env["ANTHROPIC_DEFAULT_SONNET_MODEL"]!.GetValue<string>()); // already has it
        Assert.Equal("claude-haiku-4-5", env["ANTHROPIC_DEFAULT_HAIKU_MODEL"]!.GetValue<string>());
    }

    [Fact]
    public void ClaudeCode_WithoutOptions_LeavesModelsAndEnvAlone()
    {
        var adapter = new ClaudeCodeClientAdapter(_home.Env, _store);
        var models = new JsonObject { ["ANTHROPIC_MODEL"] = "claude-sonnet-4-5" };

        _applier.Apply(adapter.PlanEnable(TestGateway.Context("astra-claude-x", null, Extras(new JsonObject { ["cacheTtl"] = "1h" }, models))));

        var env = ClaudeEnv();
        Assert.Equal("claude-sonnet-4-5", env["ANTHROPIC_MODEL"]!.GetValue<string>());
        Assert.Null(env["MAX_THINKING_TOKENS"]);
        Assert.Null(env["DISABLE_PROMPT_CACHING"]);
    }

    [Fact]
    public void ClaudeCode_EnvOptions_AreWrittenAndRemovedOnDisable()
    {
        var adapter = new ClaudeCodeClientAdapter(_home.Env, _store);
        var options = new JsonObject { ["maxThinkingTokens"] = 16000, ["disablePromptCaching"] = true };

        _applier.Apply(adapter.PlanEnable(TestGateway.Context("astra-claude-x", null, Extras(options))));
        var env = ClaudeEnv();
        Assert.Equal("16000", env["MAX_THINKING_TOKENS"]!.GetValue<string>());
        Assert.Equal("1", env["DISABLE_PROMPT_CACHING"]!.GetValue<string>());

        // The settings file did not exist before the takeover, so restoring removes it again.
        _applier.Disable(adapter.PlanDisable());
        Assert.False(File.Exists(Path.Combine(_home.Env.Combine(".claude"), "settings.json")));
    }

    [Fact]
    public void Codex_Options_AreWrittenAsTopLevelKeysAndRestoredOnDisable()
    {
        var adapter = new CodexClientAdapter(_home.Env, _store);
        var file = Path.Combine(_home.Env.Combine(".codex"), "config.toml");
        _home.WriteFile(file, "model_reasoning_effort = \"low\"\n");
        var options = new JsonObject { ["contextWindow"] = 400000, ["autoCompactTokenLimit"] = 300000, ["reasoningEffort"] = "high" };

        _applier.Apply(adapter.PlanEnable(TestGateway.Context("astra-codex-x", null, Extras(options))));
        var text = _home.ReadFile(file);
        Assert.Contains("model_context_window = 400000", text);
        Assert.Contains("model_auto_compact_token_limit = 300000", text);
        Assert.Contains("model_reasoning_effort = \"high\"", text);

        _applier.Disable(adapter.PlanDisable());
        var restored = _home.ReadFile(file);
        Assert.DoesNotContain("model_context_window", restored);
        Assert.DoesNotContain("model_auto_compact_token_limit", restored);
        Assert.Contains("model_reasoning_effort = \"low\"", restored); // the user's own value comes back
    }
}

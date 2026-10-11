using System.Text.Json.Nodes;
using Astra.Clients.Adapters;
using Astra.Clients.Config;
using Astra.Core.Plugins;

namespace Astra.Clients.Tests;

/// <summary>Clients a plugin adds (magpie agent plugins): written and put back by Astra, in each file format.</summary>
public sealed class PluginClientAdapterTests : IDisposable
{
    private readonly TestHome _home = new();
    private readonly InMemoryClientConfigStateStore _store = new();
    private readonly ClientConfigApplier _applier;

    public PluginClientAdapterTests() => _applier = new ClientConfigApplier(_store, _home.Env, _home.BackupRoot);

    public void Dispose() => _home.Dispose();

    /// <summary>A plugin's agent whose connect returns what <see cref="Keys"/> holds now.</summary>
    private sealed class FakeAgents(AgentPluginDescriptor agent) : IAgentPluginCatalog
    {
        public JsonObject? Keys { get; set; }
        public JsonObject? LastInput { get; private set; }
        public long Generation => 1;
        public IReadOnlyList<AgentPluginDescriptor> List() => [agent];
        public IReadOnlyDictionary<string, string> Errors => new Dictionary<string, string>();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        public JsonObject? Connect(string agentId, JsonObject input)
        {
            LastInput = input;
            return (JsonObject?)Keys?.DeepClone();
        }
    }

    private static AgentPluginDescriptor Aider() => new()
    {
        Spec = "@magpie-community/agent-aider@latest", Id = "aider", Name = "Aider", Config = "~/.aider.conf.yml",
        Format = "yaml", Model = "model", Prefix = "openai/", Bin = "aider",
    };

    private static JsonObject Models() => new()
    {
        ["gpt-5"] = new JsonObject { ["id"] = "gpt-5", ["name"] = "GPT-5", ["contextWindow"] = 400000, ["vision"] = true },
        ["glm"] = new JsonObject { ["id"] = "glm", ["name"] = "GLM" },
    };

    [Theory]
    [InlineData("~/.ssh/config")]
    [InlineData("~/.zshrc")]
    [InlineData("~/.astra/config.json")]
    [InlineData("/etc/agent.yml")]
    public void An_agent_may_not_take_over_credentials_startup_files_or_files_outside_home(string config)
    {
        var agent = Aider();
        agent.Config = config;
        var adapter = new PluginClientAdapter(_home.Env, _store, agent, new FakeAgents(agent) { Keys = [] });

        var e = Assert.Throws<Astra.Clients.Editing.EditorException>(() =>
            adapter.PlanEnable(TestGateway.Context("sk-astra-x.plugin-aider", "gpt-5", new JsonObject { ["models"] = Models() })));
        Assert.Contains("may not take over", e.Message);
    }

    [Fact]
    public void Yaml_agent_writes_the_model_and_connect_keys_and_restores_the_file()
    {
        var file = _home.File(".aider.conf.yml");
        const string original = "# my aider settings\nmodel: gpt-4o\ndark-mode: true\n";
        _home.WriteFile(file, original);
        var agents = new FakeAgents(Aider())
        {
            Keys = new JsonObject { ["openai-api-base"] = "http://127.0.0.1:17321/v1", ["openai-api-key"] = "sk-astra-x.plugin-aider", ["show-model-warnings"] = false },
        };
        var adapter = new PluginClientAdapter(_home.Env, _store, Aider(), agents);

        var plan = adapter.PlanEnable(TestGateway.Context("sk-astra-x.plugin-aider", "gpt-5", new JsonObject { ["models"] = Models() }));
        _applier.Apply(plan);

        var written = _home.ReadFile(file);
        Assert.Contains("# my aider settings", written);
        Assert.Contains("model: \"openai/gpt-5\"", written);
        Assert.Contains("openai-api-base: \"http://127.0.0.1:17321/v1\"", written);
        Assert.Contains("show-model-warnings: false", written);
        Assert.Contains("dark-mode: true", written);
        Assert.True(adapter.Inspect().Enabled);
        Assert.Equal(file, agents.LastInput!["agent"]!["config"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:17321/v1", agents.LastInput["gateway"]!["v1"]!.GetValue<string>());
        Assert.Equal("""{"id":"gpt-5","name":"GPT-5","context":400000,"output":0,"images":true,"efforts":[]}""", agents.LastInput["model"]!.ToJsonString());
        Assert.Equal(2, agents.LastInput["models"]!.AsArray().Count);

        _applier.Disable(adapter.PlanDisable());

        Assert.Equal(original, _home.ReadFile(file));
        Assert.False(adapter.Inspect().Enabled);
    }

    [Fact]
    public void A_key_connect_stops_returning_gets_back_what_was_there()
    {
        var file = _home.File(".aider.conf.yml");
        _home.WriteFile(file, "model: gpt-4o\nedit-format: diff\n");
        var agents = new FakeAgents(Aider()) { Keys = new JsonObject { ["edit-format"] = "whole", ["openai-api-key"] = "k" } };
        var adapter = new PluginClientAdapter(_home.Env, _store, Aider(), agents);
        _applier.Apply(adapter.PlanEnable(TestGateway.Context("k", "gpt-5")));
        Assert.Contains("edit-format: \"whole\"", _home.ReadFile(file));

        agents.Keys = new JsonObject { ["openai-api-key"] = "k" };
        _applier.Apply(adapter.PlanEnable(TestGateway.Context("k", "gpt-5")));
        _applier.Disable(adapter.PlanRestoreStale());

        // the value the user had (a key-level YAML restore writes it as a quoted scalar)
        Assert.Contains("edit-format: \"diff\"", _home.ReadFile(file));
        Assert.DoesNotContain(_store.List(adapter.Kind), s => s.KeyPath == "edit-format");
        _applier.Disable(adapter.PlanDisable());
        Assert.Equal("model: gpt-4o\nedit-format: diff\n", _home.ReadFile(file));
    }

    [Fact]
    public void Json_agent_with_dotted_keys_and_a_file_that_was_not_there()
    {
        var agent = new AgentPluginDescriptor { Spec = "x", Id = "j.v2", Name = "J", Config = "$HOME/.j/settings.json", Format = "json", Model = "llm.model" };
        var agents = new FakeAgents(agent) { Keys = new JsonObject { ["llm.baseUrl"] = "http://g/v1", ["llm.retries"] = 3 } };
        var adapter = new PluginClientAdapter(_home.Env, _store, agent, agents);
        var file = _home.File(".j", "settings.json");

        _applier.Apply(adapter.PlanEnable(TestGateway.Context("k", "glm")));

        var json = JsonNode.Parse(_home.ReadFile(file))!;
        Assert.Equal("glm", json["llm"]!["model"]!.GetValue<string>());
        Assert.Equal(3, json["llm"]!["retries"]!.GetValue<int>());
        Assert.Equal("plugin-j.v2", adapter.Kind);

        _applier.Disable(adapter.PlanDisable());
        Assert.False(_home.FileExists(file));
    }

    [Fact]
    public void Env_agent_writes_non_strings_as_json()
    {
        var agent = new AgentPluginDescriptor { Spec = "x", Id = "e", Name = "E", Config = "~/.e/.env", Format = "env", Model = "E_MODEL" };
        var agents = new FakeAgents(agent) { Keys = new JsonObject { ["E_KEY"] = "k", ["E_FLAGS"] = new JsonArray("a"), ["E_EMPTY"] = null } };
        var adapter = new PluginClientAdapter(_home.Env, _store, agent, agents);
        var file = _home.File(".e", ".env");
        _home.WriteFile(file, "OTHER=1\n");

        _applier.Apply(adapter.PlanEnable(TestGateway.Context("k", "m")));

        var text = _home.ReadFile(file);
        Assert.Contains("E_MODEL=m", text);
        Assert.Contains("E_KEY=k", text);
        Assert.Contains("E_FLAGS=\"[\\\"a\\\"]\"", text);
        Assert.Contains("E_EMPTY=\"\"", text);
        Assert.Contains("OTHER=1", text);
        _applier.Disable(adapter.PlanDisable());
        Assert.Equal("OTHER=1\n", _home.ReadFile(file));
    }

    [Fact]
    public void Without_its_plugin_it_can_only_be_restored()
    {
        var file = _home.File(".aider.conf.yml");
        _home.WriteFile(file, "model: gpt-4o\n");
        var agents = new FakeAgents(Aider()) { Keys = new JsonObject { ["openai-api-key"] = "k" } };
        _applier.Apply(new PluginClientAdapter(_home.Env, _store, Aider(), agents).PlanEnable(TestGateway.Context("k", "gpt-5")));

        // the plugin is gone: the registry rebuilds the client from the descriptor its row kept
        var registry = ClientAdapterRegistry.CreateDefault(_home.Env, _store, remembered: [Aider()]);
        var offline = registry.Require("plugin-aider");

        Assert.Equal(ClientAvailability.ComingSoon, offline.Availability);
        Assert.Throws<Astra.Clients.Editing.EditorException>(() => offline.PlanEnable(TestGateway.Context("k", "gpt-5")));
        _applier.RestoreAll([offline], purge: false);
        Assert.Equal("model: gpt-4o\n", _home.ReadFile(file));
    }

    [Fact]
    public void A_failing_connect_is_an_editor_error_naming_the_plugin()
    {
        var agents = new ThrowingAgents(Aider());
        var adapter = new PluginClientAdapter(_home.Env, _store, Aider(), agents);

        var e = Assert.Throws<Astra.Clients.Editing.EditorException>(() => adapter.PlanEnable(TestGateway.Context("k", "gpt-5")));
        Assert.Equal("@magpie-community/agent-aider@latest: connect: took too long", e.Message);
    }

    private sealed class ThrowingAgents(AgentPluginDescriptor agent) : IAgentPluginCatalog
    {
        public long Generation => 1;
        public IReadOnlyList<AgentPluginDescriptor> List() => [agent];
        public IReadOnlyDictionary<string, string> Errors => new Dictionary<string, string>();
        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;
        public JsonObject? Connect(string agentId, JsonObject input) => throw new InvalidOperationException("connect: took too long");
    }
}

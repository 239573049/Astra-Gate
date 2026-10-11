using System.Text.Json.Nodes;
using Astra.Plugins.Agents;

namespace Astra.Plugins.Tests;

/// <summary>magpie's agent plugins (internal/agentplug): the agent export as magpie checks it, and connect.</summary>
public class AgentPluginCatalogTests
{
    private static async Task<(AgentPluginCatalog Catalog, TestPlugins Plugins)> CatalogAsync(params (string Name, string Code)[] agents)
    {
        var plugins = new TestPlugins();
        foreach (var (name, code) in agents)
            await plugins.AddAsync(plugins.Folder(name, ("package.json", $$$"""{"name":"{{{name}}}","magpie":{"agent":"./a.agent.js"}}"""), ("a.agent.js", code)));
        var catalog = new AgentPluginCatalog(plugins.Catalog, (_, _) => { });
        await catalog.RefreshAsync();
        return (catalog, plugins);
    }

    [Fact]
    public async Task Magpies_aider_plugin_loads_and_connects()
    {
        var plugins = new TestPlugins();
        await plugins.AddAsync(plugins.Fixture("agent-aider"));
        var catalog = new AgentPluginCatalog(plugins.Catalog, (_, _) => { });
        await catalog.RefreshAsync();

        var aider = Assert.Single(catalog.List());
        Assert.Equal("aider", aider.Id);
        Assert.Equal("Aider", aider.Name);
        Assert.Equal("~/.aider.conf.yml", aider.Config);
        Assert.Equal("yaml", aider.Format);
        Assert.Equal("model", aider.Model);
        Assert.Equal("openai/", aider.Prefix);
        Assert.Equal(["aider", "litellm"], aider.Ua);
        Assert.True(aider.HasConnect);

        var keys = catalog.Connect("aider", new JsonObject
        {
            ["gateway"] = new JsonObject { ["url"] = "http://127.0.0.1:17321", ["v1"] = "http://127.0.0.1:17321/v1", ["key"] = "sk-astra-x.plugin-aider" },
            ["model"] = new JsonObject { ["id"] = "gpt-5" },
            ["models"] = new JsonArray(),
            ["agent"] = new JsonObject { ["id"] = "aider", ["config"] = "/home/u/.aider.conf.yml" },
        });

        Assert.Equal("""{"openai-api-base":"http://127.0.0.1:17321/v1","openai-api-key":"sk-astra-x.plugin-aider","show-model-warnings":false}""",
            keys!.ToJsonString());
    }

    [Theory]
    [InlineData("export const agent = { id: 'Bad', config: 'x.json', model: 'm' }", "agent.id \"Bad\": lower-case letters, digits, '.', '_' and '-' only")]
    [InlineData("export const agent = { id: 'a', model: 'm' }", "agent.config names no file")]
    [InlineData("export const agent = { id: 'a', config: 'x.json' }", "agent.model names no key")]
    [InlineData("export const agent = { id: 'a', config: '~/.x/config.toml', model: 'm' }", "agent.config \"~/.x/config.toml\": say its agent.format (json, yaml or env)")]
    [InlineData("export const agent = { id: 'a', config: 'x', format: 'toml', model: 'm' }", "agent.format \"toml\": json, yaml or env")]
    [InlineData("export const agent = { id: 'a', config: 'x.json', model: 'm', library: { mcp: '~/m.json' } }", "agent.library.mcp \"~/m.json\": say its agent.library.mcpFormat")]
    [InlineData("export const agent = { id: 'codex', config: 'x.json', model: 'm' }", "agent id \"codex\" is Astra's own client's")]
    [InlineData("export const nothing = 1", "exports no agent object")]
    public async Task Bad_agents_say_why(string code, string error)
    {
        var (catalog, _) = await CatalogAsync(("bad", code));

        Assert.Empty(catalog.List());
        Assert.StartsWith(error, Assert.Single(catalog.Errors.Values));
    }

    [Fact]
    public async Task Format_comes_from_the_file_name_and_default_export_works()
    {
        var (catalog, _) = await CatalogAsync(
            ("env", "export default { agent: { id: 'e', config: '~/.config/e/.env.local', model: 'E_MODEL' } }"),
            ("json", "export const agent = { id: 'j.v2', config: '$HOME/.j/settings.jsonc', model: 'llm.model' }"));

        var list = catalog.List();
        Assert.Equal(["env", "json"], list.Select(a => a.Format));
        Assert.All(list, a => Assert.False(a.HasConnect));
        Assert.Equal("e", list[0].Name);
    }

    [Fact]
    public async Task Two_plugins_with_one_id_keep_the_first()
    {
        var (catalog, _) = await CatalogAsync(
            ("first", "export const agent = { id: 'same', config: 'a.json', model: 'm' }"),
            ("second", "export const agent = { id: 'same', config: 'b.json', model: 'm' }"));

        Assert.Equal("a.json", Assert.Single(catalog.List()).Config);
        Assert.Equal("agent id \"same\" is first's too", Assert.Single(catalog.Errors.Values));
    }

    [Theory]
    [InlineData("return 42", "connect returned neither an object nor nothing")]
    [InlineData("return ['a']", "connect returned no object of keys")]
    [InlineData("throw new Error('no gateway')", "connect: Error: no gateway")]
    [InlineData("while (true) {}", "connect: took too long")]
    [InlineData("return Promise.reject('nope')", "connect rejected: nope")]
    public async Task Connect_failures_say_why(string body, string error)
    {
        var (catalog, _) = await CatalogAsync(("c", $"export const agent = {{ id: 'c', config: 'c.json', model: 'm' }}\nexport function connect() {{ {body} }}"));

        var e = Assert.Throws<InvalidOperationException>(() => catalog.Connect("c", new JsonObject()));
        Assert.Equal(error, e.Message);
    }

    [Fact]
    public async Task Async_connect_and_nothing_returned()
    {
        var (catalog, _) = await CatalogAsync(
            ("a", "export const agent = { id: 'a', config: 'a.json', model: 'm' }\nexport async function connect({ gateway }) { return { url: gateway.v1 } }"),
            ("n", "export const agent = { id: 'n', config: 'n.json', model: 'm' }\nexport function connect() {}"));

        Assert.Equal("""{"url":"v1"}""", catalog.Connect("a", new JsonObject { ["gateway"] = new JsonObject { ["v1"] = "v1" } })!.ToJsonString());
        Assert.Null(catalog.Connect("n", new JsonObject()));
    }
}

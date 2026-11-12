using System.Text.Json.Nodes;
using Astra.Plugins.Agents;
using Astra.Plugins.V2;

namespace Astra.Plugins.Tests.V2;

/// <summary>v2 clients (design/plugin-v2 08 §3): listed as agent descriptors beside v1 agents, connect runs in the plugin.</summary>
public class V2AgentCatalogTests
{
    [Fact]
    public async Task A_v2_client_is_an_agent_descriptor_and_connect_runs_in_its_plugin()
    {
        await using var t = new V2TestHost();
        var catalog = new V2AgentCatalog(new AgentPluginCatalog(t.Plugins.Catalog, (_, _) => { }), t.Host);
        await t.AddAsync("acme.cli", new JsonObject
        {
            ["main"] = "main.js", ["permissions"] = new JsonArray("clients"),
            ["contributes"] = new JsonObject
            {
                ["clients"] = new JsonArray(new JsonObject { ["id"] = "acme-cli", ["name"] = "Acme CLI", ["config"] = "~/.acme/config.json", ["model"] = "model", ["bin"] = "acme" }),
            },
        }, [("main.js", """
            import { definePlugin, clients } from 'astra';
            export default definePlugin({
              activate() { clients.register('acme-cli', { connect: (input) => ({ 'gateway.url': input.gateway.v1, 'gateway.key': input.gateway.key }) }); },
            });
            """)]);
        var before = catalog.Generation;
        await catalog.RefreshAsync();

        var d = Assert.Single(catalog.List());
        Assert.Equal("acme-cli", d.Id);
        Assert.Equal("~/.acme/config.json", d.Config);
        Assert.Equal("json", d.Format);
        Assert.NotEqual(before, catalog.Generation);
        var keys = catalog.Connect("acme-cli", new JsonObject { ["gateway"] = new JsonObject { ["v1"] = "http://127.0.0.1:1/v1", ["key"] = "sk-x" } })!;
        Assert.Equal("http://127.0.0.1:1/v1", keys["gateway.url"]!.GetValue<string>());
        Assert.Equal("sk-x", keys["gateway.key"]!.GetValue<string>());
        Assert.Throws<InvalidOperationException>(() => catalog.Connect("nope", new JsonObject()));
    }
}

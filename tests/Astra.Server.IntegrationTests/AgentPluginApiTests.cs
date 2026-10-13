using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Plugins;
using Astra.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>A magpie agent plugin end to end: listed as a client, wired up, used through the gateway, put back.</summary>
public class AgentPluginApiTests
{
    // the community's agent-aider, as published (internal/plugin/testdata in magpie)
    private const string Aider = """
        export const agent = {
          id: "aider",
          name: "Aider",
          bin: "aider",
          config: "~/.aider.conf.yml",
          model: "model",
          prefix: "openai/",
          ua: ["aider", "litellm"],
          notice: "Aider reads its settings when it starts: restart it to use the new model.",
        }

        export function connect({ gateway }) {
          return {
            "openai-api-base": gateway.v1,
            "openai-api-key": gateway.key,
            "show-model-warnings": false,
          }
        }
        """;

    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    [Fact]
    public async Task Aider_is_listed_wired_used_and_restored()
    {
        await using var gw = await GatewayFixture.StartAsync("plugin-aider", ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var dir = Path.Combine(gw.Host.Root, "plugin-src", "agent-aider");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), """{"name":"@astragate-community/agent-aider","version":"0.1.0","magpie":{"agent":"./aider.agent.js"}}""");
        File.WriteAllText(Path.Combine(dir, "aider.agent.js"), Aider);
        await gw.Host.Db.Plugins.AddAsync(new PluginEntry { Spec = dir, Name = "@astragate-community/agent-aider" });
        gw.Host.App.Services.GetRequiredService<PluginCatalog>().Invalidate();
        var config = Path.Combine(gw.Host.ClientHome, ".aider.conf.yml");
        Directory.CreateDirectory(gw.Host.ClientHome);
        File.WriteAllText(config, "dark-mode: true\n");

        var plugins = await gw.Host.GetJsonAsync("/api/plugins");
        Assert.Equal("plugin-aider", plugins["plugins"]![0]!["agent"]!["kind"]!.GetValue<string>());
        var listed = (await gw.Host.GetJsonAsync("/api/clients")).AsArray().Single(c => c!["kind"]!.GetValue<string>() == "plugin-aider")!;
        Assert.Equal("Aider", listed["name"]!.GetValue<string>());
        Assert.Contains("restart it", listed["warnings"]!.ToJsonString());

        var enabled = await gw.Host.SendAsync(HttpMethod.Post, "/api/clients/plugin-aider/enable", new { providerId = gw.Provider.Id, model = "gpt-5" });
        Assert.True(enabled.Status == HttpStatusCode.OK, enabled.Body?.ToJsonString());
        var written = File.ReadAllText(config);
        Assert.Contains("model: \"openai/gpt-5\"", written);
        Assert.Contains($"openai-api-key: \"{gw.Key}\"", written);
        Assert.Contains("openai-api-base: \"http://", written);
        Assert.Contains("dark-mode: true", written);
        Assert.Equal("enabled", enabled.Body!["status"]!.GetValue<string>());

        // the key it was given is a working gateway key that names it
        var response = await gw.PostAsync("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("plugin-aider", (await gw.RecordOfAsync(response)).ClientKind);

        var disabled = await gw.Host.SendAsync(HttpMethod.Post, "/api/clients/plugin-aider/disable");
        Assert.Equal(HttpStatusCode.OK, disabled.Status);
        Assert.Equal("dark-mode: true\n", File.ReadAllText(config));

        // removing the plugin while its client is wired puts the config back first
        await gw.Host.SendAsync(HttpMethod.Post, "/api/clients/plugin-aider/enable", new { providerId = gw.Provider.Id, model = "gpt-5" });
        Assert.Contains("openai/gpt-5", File.ReadAllText(config));
        var job = await gw.Host.SendAsync(HttpMethod.Delete, $"/api/plugins?spec={Uri.EscapeDataString(dir)}");
        Assert.Equal(HttpStatusCode.OK, job.Status);
        Assert.Equal("dark-mode: true\n", File.ReadAllText(config));
    }
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Tools;
using Astra.Gateway.Mcp;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>The built-in tools: the admin API (keys as masks), the built-in MCP server behind a token's binding, and running a tool for the CLI.</summary>
public sealed class BuiltInToolsTests
{
    private const string Tavily = """{"results":[{"title":"Magpie facts","url":"https://birds.example/magpie","content":"Magpies are clever corvids."}]}""";

    private static void FakeSearchApi(TestHost host)
    {
        var handler = new FakeUpstream(_ => FakeUpstream.Json(Tavily));
        host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => handler;
    }

    private static async Task AddTavilyAsync(TestHost host, string key = "tvly-secret-key-1")
    {
        var (add, _) = await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "tavily" });
        Assert.Equal(HttpStatusCode.OK, add);
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis/tavily/keys", new { key });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task The_built_in_server_is_listed_and_can_only_be_switched_not_changed_or_removed()
    {
        await using var host = await TestHost.StartAsync();
        var (_, servers) = await host.SendAsync(HttpMethod.Get, "/api/mcp/servers");
        var builtIn = servers!.AsArray().Single(s => s!["slug"]!.GetValue<string>() == "astra")!;
        Assert.Equal(McpBuiltIn.Id, builtIn["id"]!.GetValue<string>());
        Assert.Equal("none", builtIn["authType"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Delete, $"/api/mcp/servers/{McpBuiltIn.Id}")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{McpBuiltIn.Id}", new { url = "https://evil.example/" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{McpBuiltIn.Id}/test")).Status);
        var (status, off) = await host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{McpBuiltIn.Id}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(off!["enabled"]!.GetValue<bool>());
        // A user's own server cannot take the slug.
        var (created, _) = await host.SendAsync(HttpMethod.Post, "/api/mcp/servers", new { name = "Mine", slug = "astra", url = "https://mcp.example/" });
        Assert.Equal(HttpStatusCode.Conflict, created);
    }

    [Fact]
    public async Task Search_keys_are_masked_ordered_switched_and_never_returned()
    {
        await using var host = await TestHost.StartAsync();
        await AddTavilyAsync(host, "tvly-secret-key-1");
        var (_, withSecond) = await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis/tavily/keys", new { key = "tvly-secret-key-2" });
        var text = withSecond!.ToJsonString();
        Assert.DoesNotContain("tvly-secret-key-1", text);
        Assert.DoesNotContain("secret-key-2", text);
        var api = withSecond["search"]!["apis"]![0]!;
        Assert.Equal("Tavily", api["name"]!.GetValue<string>());
        Assert.True(api["canReadQuota"]!.GetValue<bool>());
        var keys = api["keys"]!.AsArray();
        Assert.Equal(2, keys.Count);
        Assert.StartsWith("tvl", keys[0]!["masked"]!.GetValue<string>());
        Assert.DoesNotContain("secret", keys[0]!["masked"]!.GetValue<string>());
        // Stored protected, not as typed.
        var stored = await host.Db.Settings.GetAsync<ToolsSettings>(ToolsSettings.StorageKey);
        Assert.DoesNotContain("tvly-secret-key-1", Astra.Core.Json.Serialize(stored));

        // The same key twice, and a wrong-looking one, are refused.
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis/tavily/keys", new { key = "tvly-secret-key-1" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis/tavily/keys", new { key = "has space" })).Status);

        var firstId = keys[0]!["id"]!.GetValue<string>();
        var secondId = keys[1]!["id"]!.GetValue<string>();
        var (_, reordered) = await host.SendAsync(HttpMethod.Put, "/api/tools/search/apis/tavily/keys", new { order = new[] { secondId, firstId } });
        Assert.Equal(secondId, reordered!["search"]!["apis"]![0]!["keys"]![0]!["id"]!.GetValue<string>());
        var (_, switched) = await host.SendAsync(HttpMethod.Patch, $"/api/tools/search/apis/tavily/keys/{firstId}", new { enabled = false });
        Assert.False(switched!["search"]!["apis"]![0]!["keys"]![1]!["enabled"]!.GetValue<bool>());
        var (_, pick) = await host.SendAsync(HttpMethod.Patch, "/api/tools/search/apis/tavily", new { pick = "order", enabled = false });
        Assert.Equal("order", pick!["search"]!["apis"]![0]!["pick"]!.GetValue<string>());
        Assert.False(pick["search"]!["apis"]![0]!["enabled"]!.GetValue<bool>()); // off keeps the keys
        Assert.Equal(2, pick["search"]!["apis"]![0]!["keys"]!.AsArray().Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, "/api/tools/search/apis/tavily", new { pick = "random" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Patch, "/api/tools/search/apis/exa", new { enabled = true })).Status);

        var (_, removed) = await host.SendAsync(HttpMethod.Delete, $"/api/tools/search/apis/tavily/keys/{firstId}");
        Assert.Single(removed!["search"]!["apis"]![0]!["keys"]!.AsArray());
    }

    [Fact]
    public async Task Apis_are_added_once_reordered_and_a_searxng_needs_its_address()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "searxng" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "bing" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "searxng", url = "ftp://x" })).Status);
        await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "searxng", url = "http://127.0.0.1:8888" });
        await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "brave" });
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis", new { vendor = "brave" })).Status);
        var (_, order) = await host.SendAsync(HttpMethod.Put, "/api/tools/search/apis", new { order = new[] { "brave", "searxng" } });
        Assert.Equal(["brave", "searxng"], order!["search"]!["apis"]!.AsArray().Select(a => a!["vendor"]!.GetValue<string>()));
        var available = order["search"]!["available"]!.AsArray().Select(a => a!["vendor"]!.GetValue<string>()).ToList();
        Assert.Contains("tavily", available);
        Assert.DoesNotContain("brave", available);
    }

    [Fact]
    public async Task Settings_are_clamped_and_validated()
    {
        await using var host = await TestHost.StartAsync();
        var (_, s) = await host.SendAsync(HttpMethod.Put, "/api/tools/settings", new
        {
            search = new { onBehalf = true, mode = "always", resultCount = 99 },
            fetch = new { maxChars = 5, timeoutSeconds = 1000 },
            image = new { model = " gpt-image-1 ", size = "1024x1024" },
        });
        Assert.True(s!["search"]!["onBehalf"]!.GetValue<bool>());
        Assert.Equal("always", s["search"]!["mode"]!.GetValue<string>());
        Assert.Equal(20, s["search"]!["resultCount"]!.GetValue<int>());
        Assert.Equal(1000, s["fetch"]!["maxChars"]!.GetValue<int>());
        Assert.Equal(120, s["fetch"]!["timeoutSeconds"]!.GetValue<int>());
        Assert.False(s["fetch"]!["allowPrivate"]!.GetValue<bool>());
        Assert.Equal("gpt-image-1", s["image"]!["model"]!.GetValue<string>());
        Assert.EndsWith("/mcp/astra", s["mcpEndpoint"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, "/api/tools/settings", new { search = new { mode = "sometimes" } })).Status);
    }

    [Fact]
    public async Task The_test_button_asks_one_api_and_a_failure_says_why()
    {
        await using var host = await TestHost.StartAsync();
        var refuse = false;
        host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory =
            _ => new FakeUpstream(_ => refuse ? FakeUpstream.Json("""{"detail":"bad key"}""", HttpStatusCode.Unauthorized) : FakeUpstream.Json(Tavily));
        await AddTavilyAsync(host);
        var (_, ok) = await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis/tavily/test", new { });
        Assert.True(ok!["ok"]!.GetValue<bool>());
        Assert.Equal("Magpie facts", ok["firstTitle"]!.GetValue<string>());

        refuse = true;
        var (_, bad) = await host.SendAsync(HttpMethod.Post, "/api/tools/search/apis/tavily/test", new { });
        Assert.False(bad!["ok"]!.GetValue<bool>());
        Assert.Contains("bad key", bad["error"]!.GetValue<string>());
        Assert.DoesNotContain("tvly-secret-key-1", bad.ToJsonString());
        // The key that was refused now shows as resting.
        var (_, settings) = await host.SendAsync(HttpMethod.Get, "/api/tools/settings");
        Assert.Equal("turned away", settings!["search"]!["apis"]![0]!["keys"]![0]!["resting"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_token_reaches_the_built_in_tools_only_when_bound_and_an_unbound_tool_looks_unknown()
    {
        await using var f = await McpFixture.StartAsync();
        FakeSearchApi(f.Host);
        await AddTavilyAsync(f.Host);
        var (unboundId, unboundKey) = await f.TokenForAsync("Unbound", f.ModernId);
        var (boundId, boundKey) = await f.TokenForAsync("Bound", McpBuiltIn.Id);
        _ = unboundId;
        _ = boundId;

        Assert.DoesNotContain("astra__web_fetch", await f.ToolNamesAsync(key: unboundKey));
        var names = await f.ToolNamesAsync(key: boundKey);
        Assert.Equal(["astra__web_fetch", "astra__web_search"], names.OrderBy(n => n, StringComparer.Ordinal).ToList());
        Assert.Equal(["web_fetch", "web_search"], (await f.ToolNamesAsync("/mcp/astra", boundKey)).OrderBy(n => n, StringComparer.Ordinal).ToList()); // the one-server view is unprefixed
        var (hidden, _) = await f.CallAsync("astra__web_fetch", key: unboundKey);
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);

        var (_, unbound) = await f.CallAsync("astra__web_search", key: unboundKey);
        var (_, unknown) = await f.CallAsync("nowhere__web_search", key: unboundKey);
        Assert.Equal(-32602, unbound!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(unbound["error"]!["message"]!.GetValue<string>().Replace("astra__web_search", "X"), unknown!["error"]!["message"]!.GetValue<string>().Replace("nowhere__web_search", "X"));

        var (_, searched) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/call", new JsonObject
        {
            ["name"] = "astra__web_search", ["arguments"] = new JsonObject { ["query"] = "magpie" },
        }), boundKey, McpFixture.ModernHeaders("tools/call", "astra__web_search"));
        var text = searched!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Magpies are clever corvids.", text);
        Assert.Contains("https://birds.example/magpie", text);
        Assert.NotEqual(true, searched["result"]!["isError"]?.GetValue<bool>());
    }

    [Fact]
    public async Task Web_fetch_over_mcp_refuses_private_addresses_as_a_tool_error()
    {
        await using var f = await McpFixture.StartAsync();
        var (_, key) = await f.TokenForAsync("Fetcher", McpBuiltIn.Id);
        foreach (var url in new[] { "http://127.0.0.1:1/", "http://169.254.169.254/latest/meta-data/", "http://localhost/" })
        {
            var (_, body) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/call", new JsonObject
            {
                ["name"] = "astra__web_fetch", ["arguments"] = new JsonObject { ["url"] = url },
            }), key, McpFixture.ModernHeaders("tools/call", "astra__web_fetch"));
            Assert.True(body!["result"]!["isError"]!.GetValue<bool>(), url);
            Assert.Contains("private", body["result"]!["content"]![0]!["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Running_tools_for_the_cli_lists_them_and_reports_unknown_ones()
    {
        await using var host = await TestHost.StartAsync();
        var (_, list) = await host.SendAsync(HttpMethod.Get, "/api/tools");
        var tools = list!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>(), t => t!);
        Assert.False(tools["web_search"]["available"]!.GetValue<bool>());
        Assert.Equal("no search API is on", tools["web_search"]["reason"]!.GetValue<string>());
        Assert.True(tools["web_fetch"]["available"]!.GetValue<bool>());
        Assert.False(tools["generate_image"]["available"]!.GetValue<bool>());

        var (missing, _) = await host.SendAsync(HttpMethod.Post, "/api/tools/run", new { name = "nope" });
        Assert.Equal(HttpStatusCode.NotFound, missing);
        var (_, fetch) = await host.SendAsync(HttpMethod.Post, "/api/tools/run", new { name = "web_fetch", args = new { url = "http://127.0.0.1:1/" } });
        Assert.True(fetch!["isError"]!.GetValue<bool>());
        var (_, noArgs) = await host.SendAsync(HttpMethod.Post, "/api/tools/run", new { name = "web_fetch" });
        Assert.True(noArgs!["isError"]!.GetValue<bool>());
        Assert.Contains("url is required", noArgs["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Generate_image_goes_through_the_images_route_as_the_given_token_and_comes_back_as_an_image()
    {
        const string b64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ";
        var big = string.Concat(Enumerable.Repeat(b64, 400)); // far over the 1 MiB-less default of a plain internal call? (kept small but > 16 KB)
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => FakeUpstream.Json($$$"""{"created":1,"data":[{"b64_json":"{{{big}}}"}],"usage":{"total_tokens":10,"input_tokens":5,"output_tokens":5}}"""));
        await gw.Host.App.Services.GetRequiredService<ToolsSettingsService>().UpdateAsync(s => { s.Image.Model = "gpt-image-1"; s.Image.Size = "512x512"; });

        var (_, list) = await gw.Host.SendAsync(HttpMethod.Get, "/api/tools");
        Assert.True(list!.AsArray().Single(t => t!["name"]!.GetValue<string>() == "generate_image")!["available"]!.GetValue<bool>());

        var (status, run) = await gw.Host.SendAsync(HttpMethod.Post, "/api/tools/run", new { name = "generate_image", args = new { prompt = "a magpie, watercolor" }, token = gw.Key });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(run!["isError"]!.GetValue<bool>());
        var block = run["content"]![0]!;
        Assert.Equal("image", block["type"]!.GetValue<string>());
        Assert.Equal("image/png", block["mimeType"]!.GetValue<string>());
        Assert.Equal(big, block["data"]!.GetValue<string>());
        var sent = Assert.Single(gw.Upstream.Requests);
        Assert.EndsWith("/images/generations", sent.Url.AbsolutePath);
        Assert.Equal("Bearer sk-upstream-secret", sent.Headers["authorization"]);
        Assert.Equal("gpt-image-1", sent.Json["model"]!.GetValue<string>());
        Assert.Equal("512x512", sent.Json["size"]!.GetValue<string>());
        Assert.Equal("a magpie, watercolor", sent.Json["prompt"]!.GetValue<string>());

        // Without a usable token the images route's own rule applies: the call is refused, not made as nobody.
        var (_, refused) = await gw.Host.SendAsync(HttpMethod.Post, "/api/tools/run", new { name = "generate_image", args = new { prompt = "x" }, token = "sk-not-astra" });
        Assert.True(refused!["isError"]!.GetValue<bool>());
        Assert.Single(gw.Upstream.Requests);
    }
}

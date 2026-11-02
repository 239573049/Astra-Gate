using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Connecting this machine to a unified gateway (<c>/api/connect</c>): the gateway becomes a local provider that holds the employee's key,
/// clients are bound to it, and leaving puts everything back. The gateway itself is a stub here.
/// </summary>
public class ConnectApiTests
{
    private const string Gateway = "https://astra.example.com";
    private const string EmployeeKey = "sk-astra-employee-secret-0001";
    private const string Original = "# user config\nmodel_provider = \"openai\"\nmodel = \"original-model\"\n";

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>A gateway that knows one join code and one key; <paramref name="calls"/> records "METHOD path bearer".</summary>
    private static Task<TestHost> StartAsync(List<string> calls) => TestHost.StartAsync(builder =>
        builder.Services.AddHttpClient(Options.DefaultName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add($"{request.Method} {path} {request.Headers.Authorization?.Parameter}");
            if (path == "/v1/astra/join/exchange")
            {
                var code = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["token"]!.GetValue<string>();
                return code == "ij_good" ? Json($$"""{"key":"{{EmployeeKey}}","name":"王小明"}""")
                    : Json("""{"error":{"message":"这个接入链接已经被使用过","type":"invite_used"}}""", HttpStatusCode.Gone);
            }
            if (request.Headers.Authorization?.Parameter != EmployeeKey) return Json("""{"error":{"message":"无效的 Astra 令牌"}}""", HttpStatusCode.Unauthorized);
            return path switch
            {
                "/v1/astra/key" => Json("""{"name":"王小明","limits":[]}"""),
                "/v1/astra/connect-context" => Json("""{"models":[{"id":"gpt-5"},{"id":"claude-sonnet-4-6"}]}"""),
                _ => Json("{}", HttpStatusCode.NotFound),
            };
        })));

    [Fact]
    public async Task Joining_with_a_key_makes_a_provider_that_speaks_every_protocol_to_the_gateway()
    {
        var calls = new List<string>();
        await using var host = await StartAsync(calls);

        var (status, joined) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway + "/v1/", key = EmployeeKey });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Gateway, joined!["gatewayUrl"]!.GetValue<string>());
        Assert.Equal("王小明", joined["tokenName"]!.GetValue<string>());
        Assert.False(joined["insecure"]!.GetValue<bool>());
        Assert.Equal(["gpt-5", "claude-sonnet-4-6"], joined["models"]!.AsArray().Select(m => m!.GetValue<string>()));

        var providers = await host.GetJsonAsync("/api/providers");
        var provider = providers.AsArray().Single()!;
        Assert.Equal(joined["providerId"]!.GetValue<string>(), provider["id"]!.GetValue<string>());
        Assert.Equal(["openai-chat", "openai-responses", "anthropic", "gemini"],
            provider["endpoints"]!.AsArray().Select(e => e!["protocol"]!.GetValue<string>()));
        Assert.Contains(provider["endpoints"]!.AsArray(), e => e!["baseUrl"]!.GetValue<string>() == $"{Gateway}/v1beta");
        // The key is kept (encrypted) but never shown.
        Assert.DoesNotContain(EmployeeKey, providers.ToJsonString());
        Assert.True(provider["hasApiKey"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Joining_with_a_link_code_exchanges_it_and_uses_the_returned_key()
    {
        var calls = new List<string>();
        await using var host = await StartAsync(calls);

        var (status, joined) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, token = "ij_good" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("王小明", joined!["tokenName"]!.GetValue<string>());
        Assert.Contains($"GET /v1/astra/connect-context {EmployeeKey}", calls);
        Assert.DoesNotContain(EmployeeKey, (await host.GetJsonAsync("/api/connect/status")).ToJsonString().Replace("\"keyInfo\"", ""));
    }

    [Fact]
    public async Task A_dead_link_or_a_wrong_key_creates_nothing_and_says_which()
    {
        await using var host = await StartAsync([]);

        var (dead, deadBody) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, token = "ij_used" });
        Assert.Equal(HttpStatusCode.Gone, dead);
        Assert.Contains("已经被使用", deadBody!["error"]!.GetValue<string>());

        var (wrong, _) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, key = "sk-astra-wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong);

        var (both, _) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway });
        Assert.Equal(HttpStatusCode.BadRequest, both);
        var (badUrl, _) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = "ftp://x", key = EmployeeKey });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl);

        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());
        Assert.False((await host.GetJsonAsync("/api/connect/status"))["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Joining_again_replaces_the_key_instead_of_adding_a_second_provider()
    {
        await using var host = await StartAsync([]);
        await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, key = EmployeeKey });

        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, token = "ij_good" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Single((await host.GetJsonAsync("/api/providers")).AsArray());
    }

    [Fact]
    public async Task Applying_binds_the_client_to_the_gateway_with_a_local_token_and_leaving_puts_it_back()
    {
        await using var host = await StartAsync([]);
        var config = Path.Combine(host.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, Original);
        var (_, joined) = await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, key = EmployeeKey });

        var (status, applied) = await host.SendAsync(HttpMethod.Post, "/api/connect/apply", new { clients = new[] { "codex" } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(applied!["results"]![0]!["ok"]!.GetValue<bool>(), applied.ToJsonString());
        var written = await File.ReadAllTextAsync(config);
        Assert.DoesNotContain(EmployeeKey, written); // the client holds a local token, never the gateway's key
        Assert.Contains("127.0.0.1", written);
        var state = await host.GetJsonAsync("/api/connect/status");
        Assert.Equal(["codex"], state["clients"]!.AsArray().Select(c => c!.GetValue<string>()));

        var (leaveStatus, left) = await host.SendAsync(HttpMethod.Post, "/api/connect/leave");

        Assert.Equal(HttpStatusCode.OK, leaveStatus);
        Assert.Equal(["codex"], left!["disabled"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.Equal(Original, await File.ReadAllTextAsync(config));
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());
        Assert.False((await host.GetJsonAsync("/api/connect/status"))["connected"]!.GetValue<bool>());
        Assert.NotNull(joined);
    }

    [Fact]
    public async Task A_client_with_other_providers_keeps_them_when_the_gateway_leaves()
    {
        await using var host = await StartAsync([]);
        var (_, own) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Mine", endpoints = new[] { new { protocol = "openai-responses", baseUrl = "https://api.example.com/v1" } }, apiKey = "sk-own",
        });
        var config = Path.Combine(host.ClientHome, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, Original);
        await host.SendAsync(HttpMethod.Post, "/api/clients/codex/enable", new { providerId = own!["id"]!.GetValue<string>() });
        await host.SendAsync(HttpMethod.Post, "/api/connect/join", new { gatewayUrl = Gateway, key = EmployeeKey });
        await host.SendAsync(HttpMethod.Post, "/api/connect/apply", new { clients = new[] { "codex" } });

        var (_, left) = await host.SendAsync(HttpMethod.Post, "/api/connect/leave");

        Assert.Empty(left!["disabled"]!.AsArray());
        var info = await host.GetJsonAsync("/api/clients");
        var codex = info.AsArray().Single(c => c!["kind"]!.GetValue<string>() == "codex")!;
        Assert.True(codex["enabled"]!.GetValue<bool>());
        Assert.Equal(own["id"]!.GetValue<string>(), codex["providerId"]!.GetValue<string>());
    }
}

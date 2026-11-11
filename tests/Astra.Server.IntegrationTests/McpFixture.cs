using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Tokens;
using Astra.Gateway.Mcp;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// An Astra server with the MCP gateway in front of fake upstreams of both eras: <c>modern</c> (stateless, 2026-07-28) and <c>legacy</c>
/// (initialize-based, with a session) — and, on request, <c>auth</c>, an OAuth-protected one. A token "Agent" is bound to modern and legacy.
/// </summary>
public sealed class McpFixture : IAsyncDisposable
{
    public required TestHost Host { get; init; }
    public required FakeMcpUpstream Modern { get; init; }
    public required FakeMcpUpstream Legacy { get; init; }
    public FakeMcpUpstream? Auth { get; init; }
    public required string ModernId { get; init; }
    public required string LegacyId { get; init; }
    public required string TokenId { get; init; }
    public required string Token { get; init; }

    public static async Task<McpFixture> StartAsync(bool enabled = true, bool withAuth = false, bool badIssuer = false)
    {
        var modern = await FakeMcpUpstream.StartAsync("modern.test", stateless: true);
        var legacy = await FakeMcpUpstream.StartAsync("legacy.test", stateless: false);
        var auth = withAuth ? await FakeMcpUpstream.StartAsync("auth.test", stateless: true, oauth: true, badIssuer: badIssuer) : null;
        var host = await TestHost.StartAsync();

        var byHost = new Dictionary<string, HttpMessageHandler> { ["modern.test"] = modern.CreateHandler(), ["legacy.test"] = legacy.CreateHandler() };
        if (auth is not null) byHost["auth.test"] = auth.CreateHandler();
        host.App.Services.GetRequiredService<McpUpstreamPool>().HandlerFactory = () => new FakeMcpRouter(byHost);

        if (enabled) await host.SendAsync(HttpMethod.Put, "/api/mcp/settings", new { enabled = true });
        var modernId = await CreateServerAsync(host, "Modern", modern.Url);
        var legacyId = await CreateServerAsync(host, "Legacy", legacy.Url);
        var (_, token) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Agent" });
        var tokenId = token!["id"]!.GetValue<string>();
        await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{tokenId}", new { mcpServers = new[] { modernId, legacyId } });
        var (_, secret) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{tokenId}/reveal");

        return new McpFixture
        {
            Host = host, Modern = modern, Legacy = legacy, Auth = auth, ModernId = modernId, LegacyId = legacyId,
            TokenId = tokenId, Token = secret!["token"]!.GetValue<string>(),
        };
    }

    public static async Task<string> CreateServerAsync(TestHost host, string name, string url, object? more = null)
    {
        var body = new JsonObject { ["name"] = name, ["url"] = url };
        if (more is not null)
            foreach (var (key, value) in JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(more, TestHost.JsonOptions))!.AsObject().ToList())
            {
                body.Remove(key);
                body[key] = value?.DeepClone();
            }
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/mcp/servers", body);
        Assert.True(status == HttpStatusCode.OK, $"create {name}: {status} {created}");
        return created!["id"]!.GetValue<string>();
    }

    /// <summary>A new token bound to <paramref name="serverIds"/>; returns its key.</summary>
    public async Task<(string Id, string Key)> TokenForAsync(string name, params string[] serverIds)
    {
        var (_, token) = await Host.SendAsync(HttpMethod.Post, "/api/tokens", new { name });
        var id = token!["id"]!.GetValue<string>();
        await Host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { mcpServers = serverIds });
        var (_, secret) = await Host.SendAsync(HttpMethod.Post, $"/api/tokens/{id}/reveal");
        return (id, secret!["token"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- requests

    public static JsonObject Rpc(string method, JsonObject? parameters = null, int id = 1) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    /// <summary>A modern (2026-07-28) request: protocol version and capabilities in <c>params._meta</c>.</summary>
    public static JsonObject ModernRpc(string method, JsonObject? parameters = null, string version = "2026-07-28", int id = 1)
    {
        var p = parameters ?? new JsonObject();
        p["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = version, ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject() };
        return Rpc(method, p, id);
    }

    /// <summary>Headers a modern request carries on HTTP.</summary>
    public static Action<HttpRequestMessage> ModernHeaders(string method, string? name = null, string version = "2026-07-28") => request =>
    {
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", version);
        request.Headers.TryAddWithoutValidation("Mcp-Method", method);
        if (name is not null) request.Headers.TryAddWithoutValidation("Mcp-Name", name);
    };

    /// <summary>POSTs to the MCP gateway as <paramref name="key"/> (the fixture's token by default; pass "" for none).</summary>
    public async Task<(HttpResponseMessage Response, JsonNode? Body)> PostAsync(string path, JsonNode body, string? key = null,
        Action<HttpRequestMessage>? edit = null, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType) };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        var bearer = key ?? Token;
        if (bearer.Length > 0) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        edit?.Invoke(request);
        var response = await Host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.Length > 0 && response.Content.Headers.ContentType?.MediaType == "application/json" ? JsonNode.Parse(text) : null);
    }

    /// <summary>A modern <c>tools/list</c> of <paramref name="path"/>; the tool names, in the order the gateway gave them.</summary>
    public async Task<List<string>> ToolNamesAsync(string path = "/mcp", string? key = null)
    {
        var (response, body) = await PostAsync(path, ModernRpc("tools/list"), key, ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. body!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>())];
    }

    public Task<(HttpResponseMessage Response, JsonNode? Body)> CallAsync(string name, string path = "/mcp", string? key = null, string message = "hi") =>
        PostAsync(path, ModernRpc("tools/call", new JsonObject { ["name"] = name, ["arguments"] = new JsonObject { ["message"] = message } }), key,
            ModernHeaders("tools/call", name));

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Modern.DisposeAsync();
        await Legacy.DisposeAsync();
        if (Auth is not null) await Auth.DisposeAsync();
    }
}

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Astra.Server.IntegrationTests;

/// <summary>Tools of the fake upstream MCP servers; both servers offer the same names, which is what makes the gateway prefix them.</summary>
[McpServerToolType]
public sealed class FakeTools
{
    [McpServerTool(Name = "echo"), Description("Echoes the message back.")]
    public static string Echo(string message) => "echo: " + message;

    [McpServerTool(Name = "fail"), Description("Always fails.")]
    public static string Fail() => throw new McpException("the tool failed on purpose");
}

/// <summary>
/// Real upstream MCP servers (the official SDK's server) on <see cref="TestServer"/>, one per era: stateless is the modern revision
/// (2026-07-28), stateful is the initialize-based one with a session (2025-11-25) — and, optionally, an OAuth-protected one with a
/// miniature authorization server that records what it was asked.
/// </summary>
public sealed class FakeMcpUpstream : IAsyncDisposable
{
    private FakeMcpUpstream(WebApplication app, string host)
    {
        App = app;
        Host = host;
    }

    public WebApplication App { get; }
    public string Host { get; }
    public string Url => $"http://{Host}/mcp";

    /// <summary>What the miniature authorization server saw (authorize / token / registration requests), in order.</summary>
    public ConcurrentQueue<string> Seen { get; } = new();

    public HttpMessageHandler CreateHandler() => App.GetTestServer().CreateHandler();

    public static async Task<FakeMcpUpstream> StartAsync(string host, bool stateless, bool oauth = false, bool badIssuer = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = stateless).WithTools<FakeTools>();
        var app = builder.Build();
        var upstream = new FakeMcpUpstream(app, host);
        var origin = $"http://{host}";
        if (oauth)
        {
            app.MapGet("/.well-known/oauth-protected-resource", () => new JsonObject
            {
                ["resource"] = origin + "/mcp", ["authorization_servers"] = new JsonArray(origin), ["scopes_supported"] = new JsonArray("tools"),
            });
            app.MapGet("/.well-known/oauth-authorization-server", () => new JsonObject
            {
                ["issuer"] = origin, ["authorization_endpoint"] = origin + "/authorize", ["token_endpoint"] = origin + "/token",
                ["registration_endpoint"] = origin + "/register", ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["authorization_response_iss_parameter_supported"] = true, ["grant_types_supported"] = new JsonArray("authorization_code", "refresh_token"),
            });
            app.MapPost("/register", async (HttpContext c) =>
            {
                var body = await JsonNode.ParseAsync(c.Request.Body);
                upstream.Seen.Enqueue("register application_type=" + body?["application_type"]);
                return Results.Json(new JsonObject { ["client_id"] = "astra-test", ["redirect_uris"] = body?["redirect_uris"]?.DeepClone(), ["token_endpoint_auth_method"] = "none" },
                    statusCode: 201);
            });
            app.MapGet("/authorize", (HttpContext c) =>
            {
                var q = c.Request.Query;
                upstream.Seen.Enqueue($"authorize resource={q["resource"]} method={q["code_challenge_method"]} challenge={(q["code_challenge"].Count > 0 ? "yes" : "no")}");
                var iss = badIssuer ? "http://evil.example" : origin;
                return Results.Redirect($"{q["redirect_uri"]}?code=the-code&state={Uri.EscapeDataString(q["state"]!)}&iss={Uri.EscapeDataString(iss)}");
            });
            app.MapPost("/token", async (HttpContext c) =>
            {
                var form = await c.Request.ReadFormAsync();
                upstream.Seen.Enqueue($"token grant={form["grant_type"]} resource={form["resource"]} verifier={(form["code_verifier"].Count > 0 ? "yes" : "no")}");
                return Results.Json(new JsonObject
                {
                    ["access_token"] = "access-" + form["grant_type"], ["token_type"] = "Bearer", ["expires_in"] = 3600, ["refresh_token"] = "refresh-1", ["scope"] = "tools",
                });
            });
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Path.StartsWithSegments("/mcp") && !ctx.Request.Headers.Authorization.ToString().StartsWith("Bearer access-", StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{origin}/.well-known/oauth-protected-resource\", scope=\"tools\"";
                    return;
                }
                await next();
            });
        }
        app.MapMcp("/mcp");
        await app.StartAsync();
        return upstream;
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>Sends each request to the fake upstream whose host name it names (an unknown host is "connection refused").</summary>
public sealed class FakeMcpRouter(IReadOnlyDictionary<string, HttpMessageHandler> byHost) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!byHost.TryGetValue(request.RequestUri!.Host, out var handler)) throw new HttpRequestException("No such host (test)");
        return new HttpMessageInvoker(handler, disposeHandler: false).SendAsync(request, cancellationToken);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Server.Hosting;
using Astra.Server.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Listening beyond loopback (0.0.0.0, the settings page's LAN access): this machine stays as open as in loopback mode,
/// other machines — and anything that only looks local — need a session for /api and a token for the gateway.
/// </summary>
public sealed class NetworkAccessTests
{
    private const string LanHost = "192.168.1.10:17321";
    private const string PeerHeader = "X-Test-Peer";

    /// <summary>TestServer connections have no peer address: this sets it from a test-only header, ahead of the guard.</summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, inner) =>
            {
                if (IPAddress.TryParse(ctx.Request.Headers[PeerHeader].ToString(), out var peer)) ctx.Connection.RemoteIpAddress = peer;
                return inner(ctx);
            });
            next(app);
        };
    }

    private static async Task<TestHost> StartAsync(bool listenOnAll = true)
    {
        var host = await TestHost.StartAsync(b => b.Services.AddSingleton<IStartupFilter, PeerFromHeader>());
        if (listenOnAll)
        {
            var options = host.App.Services.GetRequiredService<ServerOptions>();
            options.Host = "0.0.0.0";
            options.AdminPasswordHash = PasswordHasher.Hash("correct-horse");
        }
        return host;
    }

    /// <summary>One request with a chosen peer address and Host.</summary>
    private static async Task<(HttpStatusCode Status, JsonNode? Body, string? SetCookie)> SendFrom(TestHost host, string peer,
        string hostHeader, HttpMethod method, string path, object? body = null, (string Name, string Value)? header = null, string? cookie = null)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Host = hostHeader;
        req.Headers.Add(PeerHeader, peer);
        if (header is { } h) req.Headers.Add(h.Name, h.Value);
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        if (body is not null) req.Content = JsonContent.Create(body, options: TestHost.JsonOptions);
        var res = await host.Client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try
        {
            json = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
        }
        return (res.StatusCode, json, res.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.Single() : null);
    }

    [Fact]
    public async Task This_machine_needs_no_login_while_listening_on_all_interfaces()
    {
        await using var host = await StartAsync();

        var status = await SendFrom(host, "127.0.0.1", "127.0.0.1:17321", HttpMethod.Get, "/api/auth/status");
        Assert.False(status.Body!["required"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.OK, (await SendFrom(host, "127.0.0.1", "localhost:17321", HttpMethod.Get, "/api/settings")).Status);
        Assert.Equal(HttpStatusCode.OK, (await SendFrom(host, "::ffff:127.0.0.1", "127.0.0.1:17321", HttpMethod.Get, "/api/settings")).Status);
    }

    [Fact]
    public async Task Other_machines_need_a_session_and_a_token()
    {
        await using var host = await StartAsync();
        await SendFrom(host, "127.0.0.1", "127.0.0.1:17321", HttpMethod.Patch, "/api/settings", new { requireGatewayKey = false });

        var status = await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Get, "/api/auth/status");
        Assert.True(status.Body!["required"]!.GetValue<bool>());
        Assert.False(status.Body["signedIn"]!.GetValue<bool>());
        Assert.False(status.Body["remote"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Get, "/api/settings")).Status);
        // The key switch opens the gateway to this machine only.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Get, "/v1/models")).Status);
        Assert.NotEqual(HttpStatusCode.Unauthorized, (await SendFrom(host, "127.0.0.1", "127.0.0.1:17321", HttpMethod.Get, "/v1/models")).Status);
    }

    [Fact]
    public async Task Rebound_domains_and_proxied_requests_do_not_count_as_this_machine()
    {
        await using var host = await StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendFrom(host, "127.0.0.1", "attacker.example.com", HttpMethod.Get, "/api/settings")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendFrom(host, "127.0.0.1", "127.0.0.1:17321", HttpMethod.Get, "/api/settings", header: ("X-Forwarded-For", "198.51.100.4"))).Status);
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED would have turned a forged "X-Forwarded-For: 127.0.0.1" into this.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendFrom(host, "127.0.0.1", "localhost", HttpMethod.Get, "/api/settings", header: ("X-Original-For", "172.17.0.1:51234"))).Status);
    }

    [Fact]
    public async Task Listen_address_is_saved_to_config_json_and_needs_a_password_beyond_loopback()
    {
        await using var host = await StartAsync(listenOnAll: false);
        var paths = host.App.Services.GetRequiredService<AstraPaths>();

        var listen = await host.GetJsonAsync("/api/server/listen");
        Assert.Equal("127.0.0.1", listen["host"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", listen["configuredHost"]!.GetValue<string>());
        Assert.False(listen["passwordSet"]!.GetValue<bool>());
        Assert.Empty(listen["networkUrls"]!.AsArray());

        var (noPassword, error) = await host.SendAsync(HttpMethod.Put, "/api/server/listen", new { host = "0.0.0.0" });
        Assert.Equal(HttpStatusCode.Conflict, noPassword);
        Assert.Contains("password", error!["error"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", ServerOptions.Load(paths).Host);

        var (bad, _) = await host.SendAsync(HttpMethod.Put, "/api/server/listen", new { host = "my machine" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);

        await host.SendAsync(HttpMethod.Post, "/api/auth/password", new { password = "correct-horse" });
        var (ok, saved) = await host.SendAsync(HttpMethod.Put, "/api/server/listen", new { host = "0.0.0.0" });
        Assert.Equal(HttpStatusCode.OK, ok);
        // The running server keeps its address until it restarts.
        Assert.Equal("127.0.0.1", saved!["host"]!.GetValue<string>());
        Assert.Equal("0.0.0.0", saved["configuredHost"]!.GetValue<string>());
        var stored = ServerOptions.Load(paths);
        Assert.Equal("0.0.0.0", stored.Host);
        Assert.True(PasswordHasher.Verify("correct-horse", stored.AdminPasswordHash));

        var (back, _) = await host.SendAsync(HttpMethod.Put, "/api/server/listen", new { host = "127.0.0.1" });
        Assert.Equal(HttpStatusCode.OK, back);
        Assert.Equal("127.0.0.1", ServerOptions.Load(paths).Host);
    }

    [Fact]
    public async Task Only_this_machine_changes_the_listen_address_or_the_password()
    {
        await using var host = await StartAsync();
        var login = await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Post, "/api/auth/login", new { password = "correct-horse" });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var cookie = login.SetCookie!.Split(';')[0];

        Assert.Equal(HttpStatusCode.OK, (await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Get, "/api/settings", cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Put, "/api/server/listen", new { host = "127.0.0.1" }, cookie: cookie)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendFrom(host, "192.168.1.20", LanHost, HttpMethod.Post, "/api/auth/password", new { password = "another-password" }, cookie: cookie)).Status);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Server.Api;
using Astra.Server.Hosting;
using Astra.Server.Security;
using Astra.Server.Tunnel;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Remote access through a Cloudflare Tunnel: what requests that came through Cloudflare may do (the tunnel process
/// itself is not started here — the guard only looks at the request).
/// </summary>
public sealed class RemoteAccessTests
{
    private static HttpRequestMessage Remote(HttpMethod method, string url, object? body = null, bool https = true)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("Cf-Connecting-IP", "203.0.113.7");
        req.Headers.Add("Cf-Ray", "8a1b2c3d4e5f-LAX");
        if (https) req.Headers.Add("Cf-Visitor", "{\"scheme\":\"https\"}");
        if (body is not null) req.Content = JsonContent.Create(body, options: TestHost.JsonOptions);
        return req;
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> SendRemote(
        TestHost host, HttpMethod method, string url, object? body = null)
    {
        var res = await host.Client.SendAsync(Remote(method, url, body));
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try
        {
            json = string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return (res.StatusCode, json, res);
    }

    private static void SetPassword(TestHost host, string password) =>
        host.App.Services.GetRequiredService<ServerOptions>().AdminPasswordHash = PasswordHasher.Hash(password);

    [Fact]
    public async Task Remote_requests_reach_only_the_gateway_by_default()
    {
        await using var host = await TestHost.StartAsync();

        var status = await SendRemote(host, HttpMethod.Get, "/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, status.Status);
        Assert.True(status.Body!["required"]!.GetValue<bool>());
        Assert.False(status.Body["signedIn"]!.GetValue<bool>());
        Assert.True(status.Body["remote"]!.GetValue<bool>());
        Assert.True(status.Body["adminBlocked"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.OK, (await SendRemote(host, HttpMethod.Get, "/api/health")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendRemote(host, HttpMethod.Get, "/api/settings")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendRemote(host, HttpMethod.Get, "/api/tunnel")).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendRemote(host, HttpMethod.Post, "/api/auth/login", new { password = "x" })).Status);

        // The same server, asked locally, is as open as ever.
        var local = await host.GetJsonAsync("/api/auth/status");
        Assert.False(local["required"]!.GetValue<bool>());
        Assert.False(local["remote"]!.GetValue<bool>());
        await host.GetJsonAsync("/api/settings");
    }

    [Fact]
    public async Task Remote_gateway_calls_need_a_token_even_with_the_key_check_off()
    {
        await using var host = await TestHost.StartAsync();
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { requireGatewayKey = false });

        var remote = await SendRemote(host, HttpMethod.Get, "/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, remote.Status);

        var local = await host.Client.GetAsync("/v1/models");
        Assert.NotEqual(HttpStatusCode.Unauthorized, local.StatusCode);
    }

    [Fact]
    public async Task Remote_admin_needs_the_switch_a_password_and_a_session()
    {
        await using var host = await TestHost.StartAsync();

        // The switch wants a password first.
        var (refused, error) = await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { allowAdmin = true });
        Assert.Equal(HttpStatusCode.Conflict, refused);
        Assert.Contains("password", error!["error"]!.GetValue<string>());

        SetPassword(host, "correct-horse");
        var (ok, tunnel) = await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { allowAdmin = true });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.True(tunnel!["allowAdmin"]!.GetValue<bool>());

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendRemote(host, HttpMethod.Get, "/api/settings")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendRemote(host, HttpMethod.Post, "/api/auth/login", new { password = "nope-nope" })).Status);

        var login = await SendRemote(host, HttpMethod.Post, "/api/auth/login", new { password = "correct-horse" });
        Assert.Equal(HttpStatusCode.OK, login.Status);
        var cookie = Assert.Single(login.Response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);

        using var signedIn = Remote(HttpMethod.Get, "/api/settings");
        signedIn.Headers.Add("Cookie", cookie.Split(';')[0]);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.SendAsync(signedIn)).StatusCode);
    }

    [Fact]
    public async Task Remote_callers_cannot_shut_down_or_set_the_password()
    {
        await using var host = await TestHost.StartAsync();
        SetPassword(host, "correct-horse");
        await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { allowAdmin = true });

        Assert.Equal(HttpStatusCode.Forbidden, (await SendRemote(host, HttpMethod.Post, "/api/admin/shutdown")).Status);

        var login = await SendRemote(host, HttpMethod.Post, "/api/auth/login", new { password = "correct-horse" });
        using var req = Remote(HttpMethod.Post, "/api/auth/password", new { password = "another-password" });
        req.Headers.Add("Cookie", login.Response.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.Client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Admin_password_can_be_set_locally_and_lands_in_config_json()
    {
        await using var host = await TestHost.StartAsync();

        var (tooShort, _) = await host.SendAsync(HttpMethod.Post, "/api/auth/password", new { password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort);

        var (ok, _) = await host.SendAsync(HttpMethod.Post, "/api/auth/password", new { password = "correct-horse" });
        Assert.Equal(HttpStatusCode.OK, ok);

        var stored = ServerOptions.Load(new AstraPaths(host.Root));
        Assert.True(PasswordHasher.Verify("correct-horse", stored.AdminPasswordHash));
        Assert.True(PasswordHasher.Verify("correct-horse", host.App.Services.GetRequiredService<ServerOptions>().AdminPasswordHash));
        var status = await host.GetJsonAsync("/api/tunnel");
        Assert.True(status["checks"]!["passwordSet"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Foreign_hosts_are_still_refused_while_no_tunnel_runs()
    {
        await using var host = await TestHost.StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        req.Headers.Host = "attacker.example.com";
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await host.Client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Tunnel_settings_keep_the_token_write_only_and_validate_starting()
    {
        await using var host = await TestHost.StartAsync();

        var (bad, _) = await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { hostname = "not a host" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);

        var (_, named) = await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { mode = "named", hostname = "https://Astra.Example.com/path" });
        Assert.Equal("astra.example.com", named!["hostname"]!.GetValue<string>());
        Assert.False(named["hasToken"]!.GetValue<bool>());
        Assert.NotNull(named["checks"]!["blocker"]);

        var (noToken, error) = await host.SendAsync(HttpMethod.Post, "/api/tunnel/start");
        Assert.Equal(HttpStatusCode.Conflict, noToken);
        Assert.Contains("token", error!["error"]!.GetValue<string>());

        const string token = "eyJhIjoidGVzdC10dW5uZWwtdG9rZW4ifQ";
        var (_, withToken) = await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { token });
        Assert.True(withToken!["hasToken"]!.GetValue<bool>());
        Assert.DoesNotContain(token, withToken.ToJsonString());
        Assert.DoesNotContain(token, (await host.GetJsonAsync("/api/tunnel")).ToJsonString());
        Assert.DoesNotContain(token, (await host.GetJsonAsync("/api/settings")).ToJsonString());

        var (_, cleared) = await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { token = "" });
        Assert.False(cleared!["hasToken"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("2026-10-13T17:39:01Z INF |  https://electoral-friendship-courtesy-mtv.trycloudflare.com    |", "https://electoral-friendship-courtesy-mtv.trycloudflare.com")]
    [InlineData("2026-10-13T17:39:01Z INF Requesting new quick Tunnel on trycloudflare.com...", null)]
    public void Quick_tunnel_address_is_read_from_the_banner(string line, string? url) =>
        Assert.Equal(url, TunnelService.ParseQuickUrl(line));

    [Fact]
    public void Error_lines_keep_their_message()
    {
        Assert.Equal("Unauthorized: Invalid tunnel secret", TunnelService.ErrorLine("2026-10-13T17:39:01Z ERR Unauthorized: Invalid tunnel secret"));
        Assert.Null(TunnelService.ErrorLine("2026-10-13T17:39:01Z INF Registered tunnel connection connIndex=0"));
    }

    [Theory]
    [InlineData("astra.example.com", "astra.example.com")]
    [InlineData("https://Astra.Example.com/v1", "astra.example.com")]
    [InlineData(" astra.example.com ", "astra.example.com")]
    [InlineData("localhost", null)]
    [InlineData("203.0.113.7", null)]
    [InlineData("not a host", null)]
    public void Hostnames_are_normalized(string raw, string? expected) =>
        Assert.Equal(expected, TunnelEndpoints.NormalizeHostname(raw));

    [Fact]
    public void Containers_are_detected_from_the_dotnet_image_variable()
    {
        Assert.True(TunnelService.InContainer(n => n == "DOTNET_RUNNING_IN_CONTAINER" ? "true" : null));
        Assert.False(TunnelService.InContainer(_ => null));
    }

    [Fact]
    public void This_machine_has_a_cloudflared_build()
    {
        // CI runs on macOS / Linux / Windows x64 or arm64, all of which have one.
        Assert.NotNull(CloudflaredRuntime.Asset(out var why));
        Assert.Null(why);
    }
}

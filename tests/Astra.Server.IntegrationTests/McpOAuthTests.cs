using System.Net;
using System.Text.Json.Nodes;
using Astra.Core.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// OAuth upstreams (plan mcp-gateway §4.1) against a miniature authorization server that records what it was asked. The admin's browser
/// is played by the test: it opens the authorization address and brings the code back to Astra's callback.
/// </summary>
public class McpOAuthTests
{
    private static async Task<(McpFixture F, string ServerId)> StartAsync(bool badIssuer = false)
    {
        var f = await McpFixture.StartAsync(withAuth: true, badIssuer: badIssuer);
        var id = await McpFixture.CreateServerAsync(f.Host, "Secure", f.Auth!.Url, new { authType = "oauth" });
        await f.Host.SendAsync(HttpMethod.Patch, $"/api/tokens/{f.TokenId}", new { mcpServers = new[] { f.ModernId, f.LegacyId, id } });
        return (f, id);
    }

    /// <summary>The browser: follows the authorization address to the authorization server, which redirects to <c>redirect_uri</c> with a code.</summary>
    private static async Task<(string State, string Query)> BrowserAsync(McpFixture f, string authorizationUrl)
    {
        using var browser = new HttpClient(f.Auth!.CreateHandler());
        using var response = await browser.GetAsync(authorizationUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = response.Headers.Location!.Query;
        return (System.Web.HttpUtility.ParseQueryString(query)["state"]!, query);
    }

    private static async Task<string> StartFlowAsync(McpFixture f, string serverId)
    {
        var (status, started) = await f.Host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{serverId}/oauth/start");
        Assert.Equal(HttpStatusCode.OK, status);
        return started!["authorizationUrl"]!.GetValue<string>();
    }

    [Fact]
    public async Task An_Unauthorized_Server_Is_Left_Out_And_Says_Why()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        // No interactive flow from a data-plane request: the server is simply not usable yet.
        Assert.Equal(["legacy__echo", "legacy__fail", "modern__echo", "modern__fail"], await f.ToolNamesAsync());
        var server = await f.Host.GetJsonAsync($"/api/mcp/servers/{id}");
        Assert.Equal("needs_auth", server["status"]!.GetValue<string>());
        Assert.False(server["oauth"]!["authorized"]!.GetValue<bool>());

        var (_, call) = await f.CallAsync("secure__echo");
        Assert.True(call!["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("授权", call["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.DoesNotContain("auth.test", call.ToJsonString());
        Assert.Equal("needs_auth", (await f.Host.GetJsonAsync("/api/mcp/calls")).AsArray()[0]!["errorCategory"]!.GetValue<string>());
        Assert.Empty(f.Auth!.Seen); // the data plane never touched the authorization server
    }

    [Fact]
    public async Task The_Admin_Authorizes_Once_And_Every_Bound_Token_Can_Then_Use_The_Server()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        var authorizationUrl = await StartFlowAsync(f, id);
        Assert.Contains("state=", authorizationUrl);

        var (state, query) = await BrowserAsync(f, authorizationUrl);
        var callback = await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query);
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Equal("no-store", callback.Headers.CacheControl?.ToString());
        Assert.Contains("no-referrer", callback.Headers.GetValues("Referrer-Policy"));
        Assert.DoesNotContain(state, await callback.Content.ReadAsStringAsync()); // the page repeats nothing from the address bar

        Assert.Contains("secure__echo", await f.ToolNamesAsync());
        var (_, call) = await f.CallAsync("secure__echo", message: "authorized");
        Assert.Equal("echo: authorized", call!["result"]!["content"]![0]!["text"]!.GetValue<string>());

        // What the protocol requires of a client: a native client registration, PKCE (S256), and the resource on both requests.
        var seen = f.Auth!.Seen.ToList();
        Assert.Contains("register application_type=native", seen);
        Assert.Contains($"authorize resource={f.Auth.Url} method=S256 challenge=yes", seen);
        Assert.Contains($"token grant=authorization_code resource={f.Auth.Url} verifier=yes", seen);

        var server = await f.Host.GetJsonAsync($"/api/mcp/servers/{id}");
        Assert.Equal("ok", server["status"]!.GetValue<string>());
        Assert.True(server["oauth"]!["authorized"]!.GetValue<bool>());
        Assert.Equal("http://auth.test", server["oauth"]!["issuer"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tokens_Are_Stored_Protected_And_Never_Leave_The_Server()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        var (_, query) = await BrowserAsync(f, await StartFlowAsync(f, id));
        await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query);

        var row = (await f.Host.Db.Mcp.GetOAuthAsync(id))!;
        Assert.DoesNotContain("access-authorization_code", row.TokensEnc);
        Assert.DoesNotContain("refresh-1", row.TokensEnc);
        var plain = JsonNode.Parse(f.Host.App.Services.GetRequiredService<ISecretProtector>().Unprotect(row.TokensEnc))!;
        Assert.Equal("access-authorization_code", plain["accessToken"]!.GetValue<string>());
        Assert.Equal("refresh-1", plain["refreshToken"]!.GetValue<string>());
        // The client registration and the authorization server travel with the tokens: credentials are bound to who issued them.
        Assert.Equal("astra-test", plain["clientId"]!.GetValue<string>());
        Assert.Equal("http://auth.test", plain["authorizationServer"]!.GetValue<string>());

        var everything = (await f.Host.GetJsonAsync("/api/mcp/servers")).ToJsonString() + (await f.Host.GetJsonAsync($"/api/mcp/servers/{id}")).ToJsonString();
        Assert.DoesNotContain("access-authorization_code", everything);
        Assert.DoesNotContain("refresh-1", everything);
        Assert.DoesNotContain("astra-test", everything);
    }

    [Fact]
    public async Task An_Authorization_Response_From_The_Wrong_Issuer_Is_Rejected_Before_The_Code_Is_Spent()
    {
        var (f, id) = await StartAsync(badIssuer: true);
        await using var _ = f;
        var (_, query) = await BrowserAsync(f, await StartFlowAsync(f, id));

        var callback = await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query);
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.DoesNotContain(f.Auth!.Seen, line => line.StartsWith("token ", StringComparison.Ordinal)); // the token endpoint was never called
        Assert.DoesNotContain("Bearer access", await callback.Content.ReadAsStringAsync());

        var server = await f.Host.GetJsonAsync($"/api/mcp/servers/{id}");
        Assert.NotEqual("ok", server["status"]!.GetValue<string>());
        Assert.False(server["oauth"]!["authorized"]!.GetValue<bool>());
        Assert.DoesNotContain("secure__echo", await f.ToolNamesAsync());
    }

    [Fact]
    public async Task The_State_Is_Single_Use_And_Unknown_States_Are_Refused()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        var (_, query) = await BrowserAsync(f, await StartFlowAsync(f, id));

        Assert.Equal(HttpStatusCode.OK, (await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query)).StatusCode); // replayed
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Host.Client.GetAsync("/api/mcp/oauth/callback?code=x&state=never-issued")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Host.Client.GetAsync("/api/mcp/oauth/callback")).StatusCode);
    }

    [Fact]
    public async Task An_Authorization_Error_Is_Not_Shown_And_Leaves_The_Server_Unauthorized()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        var (state, _) = await BrowserAsync(f, await StartFlowAsync(f, id));

        var callback = await f.Host.Client.GetAsync($"/api/mcp/oauth/callback?error=access_denied&error_description=Look%20at%20me&state={state}");
        Assert.Equal(HttpStatusCode.BadRequest, callback.StatusCode);
        Assert.DoesNotContain("Look at me", await callback.Content.ReadAsStringAsync());
        Assert.False((await f.Host.GetJsonAsync($"/api/mcp/servers/{id}"))["oauth"]!["authorized"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Authorizing_Is_Only_For_OAuth_Servers_And_Tokens_Can_Be_Forgotten()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Host.SendAsync(HttpMethod.Post, $"/api/mcp/servers/{f.ModernId}/oauth/start")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Host.SendAsync(HttpMethod.Post, "/api/mcp/servers/nope/oauth/start")).Status);

        var (_, query) = await BrowserAsync(f, await StartFlowAsync(f, id));
        await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query);
        Assert.Contains("secure__echo", await f.ToolNamesAsync());

        var (status, forgotten) = await f.Host.SendAsync(HttpMethod.Delete, $"/api/mcp/servers/{id}/oauth");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("needs_auth", forgotten!["status"]!.GetValue<string>());
        Assert.False(forgotten["oauth"]!["authorized"]!.GetValue<bool>());
        Assert.Null(await f.Host.Db.Mcp.GetOAuthAsync(id));
        Assert.DoesNotContain("secure__echo", await f.ToolNamesAsync());
    }

    [Fact]
    public async Task Pointing_An_Authorized_Server_At_Another_Address_Forgets_Its_Tokens()
    {
        var (f, id) = await StartAsync();
        await using var _ = f;
        var (_, query) = await BrowserAsync(f, await StartFlowAsync(f, id));
        await f.Host.Client.GetAsync("/api/mcp/oauth/callback" + query);
        Assert.NotNull(await f.Host.Db.Mcp.GetOAuthAsync(id));

        await f.Host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{id}", new { url = f.Modern.Url });
        Assert.Null(await f.Host.Db.Mcp.GetOAuthAsync(id)); // credentials are never sent to an address they were not granted for
    }
}

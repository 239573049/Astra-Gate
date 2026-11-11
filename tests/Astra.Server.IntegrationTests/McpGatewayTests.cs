using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// The MCP gateway's data plane (<c>/mcp</c>, <c>/mcp/{slug}</c>, plan mcp-gateway): both protocol eras on the way in, both on the way out,
/// per-token visibility, and the failure modes of an upstream. The upstreams are real MCP servers (the official SDK's), not canned replies.
/// </summary>
public class McpGatewayTests
{
    private static string Text(JsonNode? result) => string.Concat(result!["content"]!.AsArray().Select(c => c!["text"]!.GetValue<string>()));

    // ---------------------------------------------------------------- the two eras

    [Fact]
    public async Task A_Modern_Client_Sees_Every_Bound_Server_Prefixed_In_A_Fixed_Order()
    {
        await using var f = await McpFixture.StartAsync();
        var (response, body) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list"), edit: McpFixture.ModernHeaders("tools/list"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["legacy__echo", "legacy__fail", "modern__echo", "modern__fail"], body!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        var result = body["result"]!;
        // A modern result says what it is and is never cached: the list is cut to the caller's token.
        Assert.Equal("complete", result["resultType"]!.GetValue<string>());
        Assert.Equal(0, result["ttlMs"]!.GetValue<long>());
        Assert.Equal("private", result["cacheScope"]!.GetValue<string>());
        Assert.Equal("astra-gateway", result["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!.GetValue<string>());
        // The same list again, in the same order (clients and prompt caches rely on it).
        Assert.Equal(await f.ToolNamesAsync(), await f.ToolNamesAsync());
    }

    [Fact]
    public async Task A_Modern_Call_Reaches_The_Right_Upstream_Whatever_Era_It_Speaks_And_Is_Recorded()
    {
        await using var f = await McpFixture.StartAsync();
        foreach (var name in new[] { "modern__echo", "legacy__echo" })
        {
            var (response, body) = await f.CallAsync(name, message: name);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("echo: " + name, Text(body!["result"]));
            Assert.Equal("complete", body["result"]!["resultType"]!.GetValue<string>());
        }
        var (_, failed) = await f.CallAsync("modern__fail");
        Assert.True(failed!["result"]!["isError"]!.GetValue<bool>());

        var calls = (await f.Host.GetJsonAsync("/api/mcp/calls")).AsArray();
        Assert.Equal(3, calls.Count);
        var byTool = calls.Select(c => c!).ToList();
        Assert.Contains(byTool, c => c["serverSlug"]!.GetValue<string>() == "legacy" && c["tool"]!.GetValue<string>() == "echo" && c["ok"]!.GetValue<bool>());
        // The log holds who, what and how long — the upstream's own tool name, the era of the caller; never the arguments or the reply.
        var fail = byTool.Single(c => c["tool"]!.GetValue<string>() == "fail");
        Assert.False(fail["ok"]!.GetValue<bool>());
        Assert.Equal("tool", fail["errorCategory"]!.GetValue<string>());
        Assert.All(byTool, c => Assert.Equal("modern", c["frontEra"]!.GetValue<string>()));
        Assert.All(byTool, c => Assert.Equal("Agent", c["tokenName"]!.GetValue<string>()));
        Assert.DoesNotContain("echo: ", calls.ToJsonString());
    }

    [Fact]
    public async Task A_Legacy_Client_Initializes_Without_A_Session_And_Gets_Legacy_Shaped_Results()
    {
        await using var f = await McpFixture.StartAsync();
        var init = McpFixture.Rpc("initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "t", ["version"] = "1" } });
        var (response, body) = await f.PostAsync("/mcp", init);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("2025-06-18", body!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(body["result"]!["capabilities"]!["tools"]);
        Assert.False(response.Headers.Contains("Mcp-Session-Id")); // the spec lets a server issue none: nothing to remember
        Assert.Null(body["result"]!["resultType"]);

        var (accepted, none) = await f.PostAsync("/mcp", new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Null(none);

        var (_, ping) = await f.PostAsync("/mcp", McpFixture.Rpc("ping", id: 2));
        Assert.Empty(ping!["result"]!.AsObject());

        // After the handshake a legacy client names its version in a header, never in _meta: still the legacy shape.
        var (_, list) = await f.PostAsync("/mcp", McpFixture.Rpc("tools/list", id: 3), edit: r => r.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18"));
        Assert.Equal(4, list!["result"]!["tools"]!.AsArray().Count);
        Assert.Null(list["result"]!["resultType"]);
        Assert.Null(list["result"]!["ttlMs"]);
        Assert.Null(list["result"]!["cacheScope"]);

        var (_, call) = await f.PostAsync("/mcp", McpFixture.Rpc("tools/call", new JsonObject { ["name"] = "legacy__echo", ["arguments"] = new JsonObject { ["message"] = "x" } }, id: 4));
        Assert.Equal("echo: x", Text(call!["result"]));
        Assert.Null(call["result"]!["resultType"]);
        Assert.Equal("legacy", (await f.Host.GetJsonAsync("/api/mcp/calls")).AsArray().Single()!["frontEra"]!.GetValue<string>());
    }

    [Fact]
    public async Task Initialize_Falls_Back_To_The_Newest_Legacy_Version_For_One_It_Does_Not_Know()
    {
        await using var f = await McpFixture.StartAsync();
        var (_, body) = await f.PostAsync("/mcp", McpFixture.Rpc("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" }));
        Assert.Equal("2025-11-25", body!["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Discover_And_Ping_Belong_To_The_Era_That_Has_Them()
    {
        await using var f = await McpFixture.StartAsync();
        var (_, discover) = await f.PostAsync("/mcp", McpFixture.ModernRpc("server/discover"), edit: McpFixture.ModernHeaders("server/discover"));
        var versions = discover!["result"]!["supportedVersions"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
        Assert.Equal("2026-07-28", versions[0]);
        Assert.Contains("2025-11-25", versions);
        Assert.NotNull(discover["result"]!["capabilities"]!["tools"]);

        var (_, modernPing) = await f.PostAsync("/mcp", McpFixture.ModernRpc("ping"), edit: McpFixture.ModernHeaders("ping")); // removed in 2026-07-28
        Assert.Equal(-32601, modernPing!["error"]!["code"]!.GetValue<int>());
        var (_, legacyDiscover) = await f.PostAsync("/mcp", McpFixture.Rpc("server/discover"));
        Assert.Equal(-32601, legacyDiscover!["error"]!["code"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- modern validation

    [Theory]
    [InlineData("no-version-header")]
    [InlineData("method-mismatch")]
    [InlineData("name-missing")]
    [InlineData("name-mismatch")]
    public async Task A_Modern_Request_Whose_Headers_Disagree_With_Its_Body_Is_Refused(string flaw)
    {
        await using var f = await McpFixture.StartAsync();
        var call = new JsonObject { ["name"] = "modern__echo", ["arguments"] = new JsonObject { ["message"] = "x" } };
        var (response, body) = flaw switch
        {
            "no-version-header" => await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list"), edit: r => r.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list")),
            "method-mismatch" => await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list"), edit: McpFixture.ModernHeaders("tools/call")),
            "name-missing" => await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/call", call), edit: McpFixture.ModernHeaders("tools/call")),
            _ => await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/call", call), edit: McpFixture.ModernHeaders("tools/call", "modern__other")),
        };
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32020, body!["error"]!["code"]!.GetValue<int>()); // HeaderMismatch
        Assert.Empty((await f.Host.GetJsonAsync("/api/mcp/calls")).AsArray()); // nothing was executed
    }

    [Fact]
    public async Task A_Modern_Request_Without_Client_Capabilities_Is_Refused()
    {
        await using var f = await McpFixture.StartAsync();
        var request = McpFixture.Rpc("tools/list", new JsonObject { ["_meta"] = new JsonObject { ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28" } });
        var (response, body) = await f.PostAsync("/mcp", request, edit: McpFixture.ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32602, body!["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_Unsupported_Version_Is_Answered_With_The_Versions_That_Are_Supported()
    {
        await using var f = await McpFixture.StartAsync();
        var (response, body) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list", version: "2099-01-01"),
            edit: McpFixture.ModernHeaders("tools/list", version: "2099-01-01"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(-32022, body!["error"]!["code"]!.GetValue<int>());
        Assert.Equal("2099-01-01", body["error"]!["data"]!["requested"]!.GetValue<string>());
        Assert.Contains("2026-07-28", body["error"]!["data"]!["supported"]!.AsArray().Select(v => v!.GetValue<string>()));
    }

    // ---------------------------------------------------------------- the door

    [Fact]
    public async Task The_Gateway_Is_Off_Until_It_Is_Switched_On()
    {
        await using var f = await McpFixture.StartAsync(enabled: false);
        var (response, _) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list"), edit: McpFixture.ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var (single, _) = await f.PostAsync("/mcp/modern", McpFixture.ModernRpc("tools/list"), edit: McpFixture.ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.NotFound, single.StatusCode);
    }

    [Fact]
    public async Task Only_A_Valid_Enabled_Token_In_A_Header_Gets_In()
    {
        await using var f = await McpFixture.StartAsync();
        var list = McpFixture.ModernRpc("tools/list");
        var headers = McpFixture.ModernHeaders("tools/list");

        var (none, _) = await f.PostAsync("/mcp", list, key: "", edit: headers);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("Bearer", none.Headers.WwwAuthenticate.ToString());
        var (wrong, _) = await f.PostAsync("/mcp", list, key: "sk-astra-" + new string('x', 32), edit: headers);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        // A token in the URL ends up in logs: not accepted there.
        var (query, _) = await f.PostAsync("/mcp?key=" + f.Token, list, key: "", edit: headers);
        Assert.Equal(HttpStatusCode.Unauthorized, query.StatusCode);
        var (apiKey, _) = await f.PostAsync("/mcp", list, key: "", edit: r => { headers(r); r.Headers.TryAddWithoutValidation("x-api-key", f.Token); });
        Assert.Equal(HttpStatusCode.OK, apiKey.StatusCode);

        await f.Host.SendAsync(HttpMethod.Patch, $"/api/tokens/{f.TokenId}", new { enabled = false });
        var (disabled, _) = await f.PostAsync("/mcp", list, edit: headers);
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
    }

    [Fact]
    public async Task Only_Post_With_Json_Is_Accepted_And_The_Body_Is_Bounded()
    {
        await using var f = await McpFixture.StartAsync();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, "/mcp");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + f.Token);
            var response = await f.Host.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode); // no standalone stream, no session to delete
            Assert.Contains("POST", response.Content.Headers.Allow);
        }
        var (text, _) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list"), contentType: "text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, text.StatusCode);
        var (accept, _) = await f.PostAsync("/mcp", McpFixture.ModernRpc("tools/list"), edit: r => { r.Headers.Remove("Accept"); r.Headers.TryAddWithoutValidation("Accept", "text/html"); });
        Assert.Equal(HttpStatusCode.NotAcceptable, accept.StatusCode);

        var huge = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list", ["pad"] = new string('x', 4 * 1024 * 1024 + 16) };
        var (large, _) = await f.PostAsync("/mcp", huge, edit: McpFixture.ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);

        var (batch, batchBody) = await f.PostAsync("/mcp", new JsonArray(McpFixture.Rpc("ping")));
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        Assert.Equal(-32600, batchBody!["error"]!["code"]!.GetValue<int>());

        using var broken = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{not json", Encoding.UTF8, "application/json") };
        broken.Headers.TryAddWithoutValidation("Authorization", "Bearer " + f.Token);
        var parse = await f.Host.Client.SendAsync(broken);
        Assert.Equal(HttpStatusCode.BadRequest, parse.StatusCode);
        Assert.Contains("-32700", await parse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_Browser_Origin_Must_Be_Allowed_Explicitly()
    {
        await using var f = await McpFixture.StartAsync();
        var list = McpFixture.ModernRpc("tools/list");
        Action<HttpRequestMessage> From(string origin) => r => { McpFixture.ModernHeaders("tools/list")(r); r.Headers.TryAddWithoutValidation("Origin", origin); };

        // No Origin header (every non-browser client) is fine; a browser origin that is not listed — "null" included — is not.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.PostAsync("/mcp", list, edit: From("https://evil.example"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.PostAsync("/mcp", list, edit: From("null"))).Response.StatusCode);

        await f.Host.SendAsync(HttpMethod.Put, "/api/mcp/settings", new { allowedOrigins = new[] { "https://app.example" } });
        Assert.Equal(HttpStatusCode.OK, (await f.PostAsync("/mcp", list, edit: From("https://app.example"))).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.PostAsync("/mcp", list, edit: From("https://evil.example"))).Response.StatusCode);
    }

    // ---------------------------------------------------------------- who sees what

    [Fact]
    public async Task A_Token_Sees_Only_The_Servers_Bound_To_It_And_An_Unbound_Tool_Looks_Like_An_Unknown_One()
    {
        await using var f = await McpFixture.StartAsync();
        var (_, key) = await f.TokenForAsync("Only legacy", f.LegacyId);
        var (_, nothing) = await f.TokenForAsync("Nothing");

        Assert.Equal(["legacy__echo", "legacy__fail"], await f.ToolNamesAsync(key: key));
        Assert.Empty(await f.ToolNamesAsync(key: nothing)); // a token cannot reach MCP until servers are bound to it

        var (unbound, unboundBody) = await f.CallAsync("modern__echo", key: key);
        var (unknown, unknownBody) = await f.CallAsync("nowhere__echo", key: key);
        Assert.Equal(HttpStatusCode.OK, unbound.StatusCode);
        Assert.Equal(-32602, unboundBody!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(-32602, unknownBody!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(unboundBody["error"]!["message"]!.GetValue<string>().Replace("modern__echo", "X"), unknownBody["error"]!["message"]!.GetValue<string>().Replace("nowhere__echo", "X"));
        Assert.Null((await f.CallAsync("echo", key: key)).Body!["result"]); // no server part: unknown in the aggregate view
    }

    [Fact]
    public async Task The_Single_Server_View_Has_Unprefixed_Names_And_Hides_Unbound_Servers()
    {
        await using var f = await McpFixture.StartAsync();
        Assert.Equal(["echo", "fail"], await f.ToolNamesAsync("/mcp/modern"));
        var (_, body) = await f.CallAsync("echo", "/mcp/legacy", message: "direct");
        Assert.Equal("echo: direct", Text(body!["result"]));

        var (_, key) = await f.TokenForAsync("Only legacy", f.LegacyId);
        var (hidden, _) = await f.PostAsync("/mcp/modern", McpFixture.ModernRpc("tools/list"), key, McpFixture.ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var (missing, _) = await f.PostAsync("/mcp/nowhere", McpFixture.ModernRpc("tools/list"), edit: McpFixture.ModernHeaders("tools/list"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_Disabled_Server_Disappears_And_A_Deleted_One_Leaves_Its_Id_Behind_Harmlessly()
    {
        await using var f = await McpFixture.StartAsync();
        await f.Host.SendAsync(HttpMethod.Patch, $"/api/mcp/servers/{f.ModernId}", new { enabled = false });
        Assert.Equal(["legacy__echo", "legacy__fail"], await f.ToolNamesAsync());

        await f.Host.SendAsync(HttpMethod.Delete, $"/api/mcp/servers/{f.LegacyId}");
        Assert.Empty(await f.ToolNamesAsync());
        var token = (await f.Host.GetJsonAsync($"/api/tokens/{f.TokenId}"));
        Assert.Contains(f.LegacyId, token["mcpServers"]!.AsArray().Select(i => i!.GetValue<string>())); // skipped at read time, dropped when the token is next saved
    }

    // ---------------------------------------------------------------- an upstream that is not there

    [Fact]
    public async Task One_Broken_Upstream_Does_Not_Take_The_Others_Down_And_Its_Address_Never_Leaks()
    {
        await using var f = await McpFixture.StartAsync();
        var goneId = await McpFixture.CreateServerAsync(f.Host, "Gone", "http://gone.test/secret-path/mcp");
        await f.Host.SendAsync(HttpMethod.Patch, $"/api/tokens/{f.TokenId}", new { mcpServers = new[] { f.ModernId, f.LegacyId, goneId } });

        Assert.Equal(["legacy__echo", "legacy__fail", "modern__echo", "modern__fail"], await f.ToolNamesAsync());
        var (_, call) = await f.CallAsync("gone__echo");
        Assert.True(call!["result"]!["isError"]!.GetValue<bool>()); // "the tool did not run", in MCP's own convention
        Assert.DoesNotContain("gone.test", call.ToJsonString());
        Assert.DoesNotContain("secret-path", call.ToJsonString());

        var server = await f.Host.GetJsonAsync($"/api/mcp/servers/{goneId}");
        Assert.Equal("unreachable", server["status"]!.GetValue<string>());
        Assert.Equal("unreachable", (await f.Host.GetJsonAsync("/api/mcp/calls")).AsArray()[0]!["errorCategory"]!.GetValue<string>());
    }
}

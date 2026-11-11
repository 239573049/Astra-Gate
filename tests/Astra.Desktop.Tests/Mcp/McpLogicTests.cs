using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Mcp;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Tests.Overview;

namespace Astra.Desktop.Tests.Mcp;

/// <summary>The MCP page's logic that needs no window: the bodies it sends and the token draft's MCP binding.</summary>
public sealed class McpLogicTests
{
    [Fact]
    public void A_server_body_carries_headers_only_for_header_authentication_and_trims_what_was_typed()
    {
        var none = McpApi.ServerBody(" GitHub ", " ", " https://mcp.example.com/mcp ", "http", "none", true, 60,
            [new("Authorization", "Bearer x")]);
        Assert.Equal("GitHub", (string?)none["name"]);
        Assert.Equal("https://mcp.example.com/mcp", (string?)none["url"]);
        Assert.Null(none["slug"]);      // blank = derived by the server
        Assert.Null(none["headers"]);   // other kinds carry none, even if some were typed before switching
        Assert.Equal(60, (int?)none["timeoutSeconds"]);
        Assert.True((bool?)none["enabled"]);

        var header = McpApi.ServerBody("A", "gh", "https://a.example/mcp", "sse", "header", false, 5,
            [new("Authorization", "Bearer x"), new(" X-Kept ", null), new("  ", "ignored")]);
        Assert.Equal("gh", (string?)header["slug"]);
        Assert.Equal("sse", (string?)header["transport"]);
        var headers = header["headers"]!.AsObject();
        Assert.Equal("Bearer x", (string?)headers["Authorization"]);
        Assert.True(headers.ContainsKey("X-Kept"));
        Assert.Null(headers["X-Kept"]);   // null = keep what the server stored
        Assert.Equal(2, headers.Count);   // a header without a name is left out
    }

    [Fact]
    public void Origins_are_read_one_per_line_without_blanks()
    {
        Assert.Equal(["https://a.example", "http://localhost:3000"], McpApi.ParseOrigins(" https://a.example \n\n http://localhost:3000\r\n"));
        Assert.Empty(McpApi.ParseOrigins(null));
        Assert.Empty(McpApi.ParseOrigins("  \n "));
    }

    [Fact]
    public void A_token_draft_keeps_its_bound_servers_and_sends_only_those_that_still_exist()
    {
        var draft = TokenSettingsDraft.Of(Dtos.Token() with { McpServers = ["live", "deleted", "also-live"] });
        Assert.Equal(["live", "deleted", "also-live"], draft.McpServers);

        var patch = draft.ToPatch([], ["live", "also-live", "never-bound"]);
        Assert.Equal(["live", "also-live"], patch["mcpServers"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void The_servers_are_left_out_of_the_patch_until_their_list_has_loaded()
    {
        var draft = TokenSettingsDraft.Of(Dtos.Token() with { McpServers = ["live"] });
        // Not sent at all (not sent empty): a slow response must never clear what is bound.
        Assert.False(draft.ToPatch([]).ContainsKey("mcpServers"));
        Assert.Empty(draft.ToPatch([], []).ToJsonObjectOf("mcpServers"));
    }
}

internal static class McpTestExtensions
{
    public static IEnumerable<string?> ToJsonObjectOf(this JsonObject patch, string key) => patch[key]!.AsArray().Select(n => (string?)n);
}

using Astra.Core;
using Astra.Core.Mcp;
using Astra.Core.Tokens;
using Astra.Data.Tests;
using Microsoft.Data.Sqlite;

namespace Astra.Data;

public class McpRepositoryTests
{
    private static McpServerRecord Server(string id, string slug, DateTimeOffset now) => new()
    {
        Id = id, Slug = slug, Name = slug.ToUpperInvariant(), Transport = McpTransports.Http, Url = "https://mcp.example.com/mcp",
        AuthType = McpAuthTypes.Header, HeadersEnc = "enc:headers", TimeoutSeconds = 30, CreatedAt = now, UpdatedAt = now,
    };

    [Fact]
    public async Task A_Server_Is_Saved_Read_Back_And_Updated()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Mcp.InsertAsync(Server("s1", "github", now));

        var read = (await db.Mcp.GetAsync("s1"))!;
        Assert.Equal("github", read.Slug);
        Assert.Equal(McpAuthTypes.Header, read.AuthType);
        Assert.Equal("enc:headers", read.HeadersEnc);
        Assert.Equal(30, read.TimeoutSeconds);
        Assert.True(read.Enabled);
        Assert.Equal(McpServerStatuses.Unknown, read.Status);
        Assert.Null(read.CheckedAt);
        Assert.Equal("s1", (await db.Mcp.GetBySlugAsync("github"))!.Id);

        read.Name = "GitHub"; read.Enabled = false; read.UpdatedAt = now.AddMinutes(1);
        await db.Mcp.UpdateAsync(read);
        var again = (await db.Mcp.GetAsync("s1"))!;
        Assert.Equal("GitHub", again.Name);
        Assert.False(again.Enabled);
    }

    [Fact]
    public async Task Status_Is_A_Cache_That_Leaves_The_Settings_Stamp_Alone()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Mcp.InsertAsync(Server("s1", "github", now));
        var at = now.AddMinutes(5);
        await db.Mcp.UpdateStatusAsync("s1", McpServerStatuses.NeedsAuth, "authorize again", "2025-11-25", at);

        var read = (await db.Mcp.GetAsync("s1"))!;
        Assert.Equal(McpServerStatuses.NeedsAuth, read.Status);
        Assert.Equal("authorize again", read.StatusMessage);
        Assert.Equal("2025-11-25", read.ProtocolVersion);
        Assert.NotNull(read.CheckedAt);
        // updated_at keys the gateway's connection cache: only a settings change moves it.
        Assert.Equal(now.ToUnixTimeMilliseconds(), read.UpdatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Slugs_Are_Unique_And_The_Taken_Check_Ignores_The_Server_Itself()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Mcp.InsertAsync(Server("s1", "github", now));

        Assert.True(await db.Mcp.SlugTakenAsync("github", null));
        Assert.False(await db.Mcp.SlugTakenAsync("github", "s1"));
        Assert.False(await db.Mcp.SlugTakenAsync("notion", null));
        await Assert.ThrowsAsync<SqliteException>(() => db.Mcp.InsertAsync(Server("s2", "github", now)));
    }

    [Fact]
    public async Task Listing_By_Ids_Skips_Servers_That_Are_Gone()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Mcp.InsertAsync(Server("s1", "zeta", now));
        await db.Mcp.InsertAsync(Server("s2", "alpha", now));
        await db.Mcp.InsertAsync(Server("s3", "mid", now));

        var found = await db.Mcp.ListByIdsAsync(["s1", "s2", "deleted"]);
        Assert.Equal(["alpha", "zeta"], found.Select(s => s.Slug));
        Assert.Empty(await db.Mcp.ListByIdsAsync([]));
    }

    [Fact]
    public async Task Deleting_A_Server_Takes_Its_OAuth_State_With_It_But_Not_Its_Call_Log()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Mcp.InsertAsync(Server("s1", "github", now));
        await db.Mcp.SaveOAuthAsync(new McpOAuthRecord { ServerId = "s1", TokensEnc = "enc:1", Issuer = "https://as.example", Scope = "tools", UpdatedAt = now });
        await db.Mcp.SaveOAuthAsync(new McpOAuthRecord { ServerId = "s1", TokensEnc = "enc:2", Issuer = "https://as.example", Scope = "tools files", UpdatedAt = now });
        await db.Mcp.InsertCallAsync(new McpCallRecord { At = now, TokenId = "t", ServerId = "s1", ServerSlug = "github", Tool = "search", DurationMs = 12, Ok = true });

        var oauth = (await db.Mcp.GetOAuthAsync("s1"))!;
        Assert.Equal("enc:2", oauth.TokensEnc); // saving again replaces
        Assert.Equal("tools files", oauth.Scope);

        Assert.True(await db.Mcp.DeleteAsync("s1"));
        Assert.Null(await db.Mcp.GetOAuthAsync("s1"));
        Assert.Single(await db.Mcp.ListCallsAsync(10));
        Assert.False(await db.Mcp.DeleteAsync("s1"));
    }

    [Fact]
    public async Task Calls_Are_Listed_Newest_First_Filtered_And_Pruned()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        McpCallRecord Call(string token, string tool, DateTimeOffset at, bool ok) => new()
        {
            At = at, TokenId = token, ServerId = "s1", ServerSlug = "github", Tool = tool, DurationMs = 5, Ok = ok,
            ErrorCategory = ok ? null : McpErrorCategories.Timeout, FrontEra = "modern",
        };
        await db.Mcp.InsertCallAsync(Call("a", "old", now.AddDays(-40), true));
        await db.Mcp.InsertCallAsync(Call("a", "one", now.AddMinutes(-2), true));
        await db.Mcp.InsertCallAsync(Call("b", "two", now.AddMinutes(-1), false));

        var all = await db.Mcp.ListCallsAsync(10);
        Assert.Equal(["two", "one", "old"], all.Select(c => c.Tool));
        Assert.False(all[0].Ok);
        Assert.Equal(McpErrorCategories.Timeout, all[0].ErrorCategory);
        Assert.Equal("modern", all[0].FrontEra);
        Assert.Equal(["one", "old"], (await db.Mcp.ListCallsAsync(10, tokenId: "a")).Select(c => c.Tool));
        Assert.Single(await db.Mcp.ListCallsAsync(1));

        Assert.Equal(1, await db.Mcp.PruneCallsAsync(now.AddDays(-McpLimits.CallRetentionDays)));
        Assert.Equal(2, (await db.Mcp.ListCallsAsync(10)).Count);
    }

    [Fact]
    public async Task A_Token_Keeps_Its_Bound_Servers()
    {
        var db = await TestDb.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        await db.Tokens.InsertAsync(new TokenRecord { Id = "t1", Name = "Team", McpJson = Json.Serialize(new List<string> { "s1", "s2" }), CreatedAt = now, UpdatedAt = now });

        var read = (await db.Tokens.GetAsync("t1"))!;
        Assert.Equal(["s1", "s2"], read.McpServerIds());
        read.McpJson = null;
        Assert.True(await db.Tokens.UpdateAsync(read));
        Assert.Empty((await db.Tokens.GetAsync("t1"))!.McpServerIds());
    }
}

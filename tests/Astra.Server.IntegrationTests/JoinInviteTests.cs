using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Tokens;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// One-time join links: an admin makes a short-lived code for an employee's token, and the link's holder exchanges it once for the key
/// (<c>POST /v1/astra/join/exchange</c>). The link never holds the key; a used, expired or revoked code is dead.
/// </summary>
public class JoinInviteTests
{
    private static async Task<(string TokenId, string Key)> NewTokenAsync(TestHost host, string name = "王小明")
    {
        var (_, created) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name });
        var id = created!["id"]!.GetValue<string>();
        var (_, secret) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{id}/reveal");
        return (id, secret!["token"]!.GetValue<string>());
    }

    private static async Task<(string Id, string Code, string Link)> NewInviteAsync(TestHost host, string tokenId, int? ttl = null)
    {
        var (status, invite) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{tokenId}/invites", ttl is null ? null : new { ttlMinutes = ttl });
        Assert.True(status == HttpStatusCode.OK, invite?.ToJsonString());
        var link = invite!["link"]!.GetValue<string>();
        var code = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        return (invite["id"]!.GetValue<string>(), code, link);
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> ExchangeAsync(TestHost host, string code) =>
        host.SendAsync(HttpMethod.Post, "/v1/astra/join/exchange", new { token = code });

    [Fact]
    public async Task A_link_carries_the_gateway_and_a_code_but_never_the_key()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, key) = await NewTokenAsync(host);

        var (_, code, link) = await NewInviteAsync(host, tokenId);

        Assert.StartsWith("astra://join?gateway=", link);
        Assert.StartsWith(TokenInvites.CodePrefix, code);
        Assert.DoesNotContain(key, link);
    }

    [Fact]
    public async Task The_code_is_exchanged_once_for_the_tokens_own_key()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, key) = await NewTokenAsync(host, "王小明");
        var (_, code, _) = await NewInviteAsync(host, tokenId);

        var (status, body) = await ExchangeAsync(host, code);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(key, body!["key"]!.GetValue<string>());
        Assert.Equal("王小明", body["name"]!.GetValue<string>());

        var (again, error) = await ExchangeAsync(host, code);
        Assert.Equal(HttpStatusCode.Gone, again);
        Assert.Equal("invite_used", error!["error"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Two_simultaneous_redeemers_cannot_both_get_the_key()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await NewTokenAsync(host);
        var (_, code, _) = await NewInviteAsync(host, tokenId);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ExchangeAsync(host, code)));

        Assert.Equal(1, results.Count(r => r.Status == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.Status != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.Gone, r.Status));
    }

    [Fact]
    public async Task An_expired_revoked_or_unknown_code_gets_nothing()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await NewTokenAsync(host);

        var expiredCode = TokenInvites.NewCode();
        await host.Db.TokenInvites.InsertAsync(new TokenInvite
        {
            Id = Ulid.NewUlid(), TokenId = tokenId, CodeHash = TokenInvites.Hash(expiredCode),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-15),
        });
        var (expired, expiredBody) = await ExchangeAsync(host, expiredCode);
        Assert.Equal(HttpStatusCode.Gone, expired);
        Assert.Equal("invite_expired", expiredBody!["error"]!["type"]!.GetValue<string>());

        var (id, revokedCode, _) = await NewInviteAsync(host, tokenId);
        var (revoke, _) = await host.SendAsync(HttpMethod.Delete, $"/api/tokens/{tokenId}/invites/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke);
        var (revoked, revokedBody) = await ExchangeAsync(host, revokedCode);
        Assert.Equal(HttpStatusCode.Gone, revoked);
        Assert.Equal("invite_revoked", revokedBody!["error"]!["type"]!.GetValue<string>());

        var (unknown, _) = await ExchangeAsync(host, TokenInvites.NewCode());
        Assert.Equal(HttpStatusCode.NotFound, unknown);
        var (empty, _) = await host.SendAsync(HttpMethod.Post, "/v1/astra/join/exchange", new { });
        Assert.Equal(HttpStatusCode.NotFound, empty);
    }

    [Fact]
    public async Task A_disabled_token_is_refused_and_the_code_stays_unused()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await NewTokenAsync(host);
        var (_, code, _) = await NewInviteAsync(host, tokenId);
        await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{tokenId}", new { enabled = false });

        var (status, _) = await ExchangeAsync(host, code);
        Assert.Equal(HttpStatusCode.Forbidden, status);

        await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{tokenId}", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, (await ExchangeAsync(host, code)).Status);
    }

    [Fact]
    public async Task The_admin_sees_each_codes_state_and_where_it_was_used()
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await NewTokenAsync(host);
        var (_, used, _) = await NewInviteAsync(host, tokenId);
        await NewInviteAsync(host, tokenId, ttl: 60);
        await ExchangeAsync(host, used);

        var list = (await host.GetJsonAsync($"/api/tokens/{tokenId}/invites")).AsArray();

        Assert.Equal(["pending", "used"], list.Select(i => i!["status"]!.GetValue<string>()).Order());
        Assert.NotNull(list.Single(i => i!["status"]!.GetValue<string>() == "used")!["usedAt"]);
        Assert.DoesNotContain("ij_", list.ToJsonString()); // the code itself is never listed
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task The_lifetime_has_bounds(int ttl)
    {
        await using var host = await TestHost.StartAsync();
        var (tokenId, _) = await NewTokenAsync(host);

        var (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{tokenId}/invites", new { ttlMinutes = ttl });

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}

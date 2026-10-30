using Astra.Core.Models;
using Astra.Data.Tests;

namespace Astra.Data;

public class ProviderAccountStoreTests
{
    private static ProviderAccount Sample(string id, string providerId, string status = AccountStatus.Active) => new()
    {
        Id = id,
        ProviderId = providerId,
        DisplayName = "me@example.com",
        AccountEmail = "me@example.com",
        Plan = "claude_pro",
        AccessTokenEnc = "enc-access",
        RefreshTokenEnc = "enc-refresh",
        ExpiresAtUtc = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero),
        Status = status,
        LastRefreshAtUtc = new DateTimeOffset(2026, 10, 6, 11, 0, 0, TimeSpan.Zero),
        CreatedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Insert_Get_Update_List_Delete_Round_Trip()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Claude", AuthScheme = AuthSchemes.OAuthSubscription });
        await db.Accounts.InsertAsync(Sample("acc-1", "p1"));

        var got = await db.Accounts.GetAsync("acc-1");
        Assert.NotNull(got);
        Assert.Equal("p1", got.ProviderId);
        Assert.Equal("me@example.com", got.DisplayName);
        Assert.Equal("claude_pro", got.Plan);
        Assert.Equal("enc-access", got.AccessTokenEnc);
        Assert.Equal("enc-refresh", got.RefreshTokenEnc);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), got.ExpiresAtUtc);
        Assert.Equal(AccountStatus.Active, got.Status);

        got.Plan = "claude_max";
        got.Status = AccountStatus.Expired;
        await db.Accounts.UpdateAsync(got);
        var updated = await db.Accounts.GetAsync("acc-1");
        Assert.Equal("claude_max", updated!.Plan);
        Assert.Equal(AccountStatus.Expired, updated.Status);
        Assert.True(updated.UpdatedAt > updated.CreatedAt);

        await db.Accounts.InsertAsync(Sample("acc-2", "p1", AccountStatus.Revoked));
        Assert.Equal(2, (await db.Accounts.ListAsync("p1")).Count);
        Assert.Empty(await db.Accounts.ListAsync("other"));

        Assert.True(await db.Accounts.DeleteAsync("acc-2"));
        Assert.Null(await db.Accounts.GetAsync("acc-2"));
        Assert.False(await db.Accounts.DeleteAsync("acc-2"));
    }

    [Fact]
    public async Task Kind_Source_And_Proxy_Round_Trip()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Kimi", AuthScheme = AuthSchemes.OAuthSubscription });
        var key = Sample("acc-key", "p1");
        key.Kind = AccountKinds.ApiKey;
        key.Source = AccountSources.Batch;
        key.HttpProxy = "socks5://127.0.0.1:1080";
        await db.Accounts.InsertAsync(key);
        await db.Accounts.InsertAsync(Sample("acc-oauth", "p1"));

        var got = (await db.Accounts.GetAsync("acc-key"))!;
        Assert.Equal(AccountKinds.ApiKey, got.Kind);
        Assert.Equal(AccountSources.Batch, got.Source);
        Assert.Equal("socks5://127.0.0.1:1080", got.HttpProxy);

        // Defaults for rows written without them (and for every account older than the columns).
        var oauth = (await db.Accounts.GetAsync("acc-oauth"))!;
        Assert.Equal(AccountKinds.OAuth, oauth.Kind);
        Assert.Null(oauth.Source);
        Assert.Null(oauth.HttpProxy);

        // UpdateAsync leaves the proxy alone; it has its own writer.
        got.Plan = "andante";
        await db.Accounts.UpdateAsync(got);
        Assert.Equal("socks5://127.0.0.1:1080", (await db.Accounts.GetAsync("acc-key"))!.HttpProxy);
        Assert.True(await db.Accounts.SetHttpProxyAsync("acc-key", null));
        Assert.Null((await db.Accounts.GetAsync("acc-key"))!.HttpProxy);
    }

    [Fact]
    public async Task UpdateTokens_Rotates_Access_Keeps_Refresh_When_Absent_And_Reactives()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Claude", AuthScheme = AuthSchemes.OAuthSubscription });
        await db.Accounts.InsertAsync(Sample("acc-1", "p1", AccountStatus.Expired));

        var expires = new DateTimeOffset(2026, 10, 6, 13, 0, 0, TimeSpan.Zero);
        Assert.True(await db.Accounts.UpdateTokensAsync("acc-1", "enc-new-access", null, expires));
        var got = await db.Accounts.GetAsync("acc-1");
        Assert.Equal("enc-new-access", got!.AccessTokenEnc);
        Assert.Equal("enc-refresh", got.RefreshTokenEnc); // rotation without a new refresh token keeps the old one
        Assert.Equal(expires, got.ExpiresAtUtc);
        Assert.Equal(AccountStatus.Active, got.Status);
        Assert.NotNull(got.LastRefreshAtUtc);

        Assert.True(await db.Accounts.UpdateTokensAsync("acc-1", "enc-newer", "enc-new-refresh", expires));
        var rotated = await db.Accounts.GetAsync("acc-1");
        Assert.Equal("enc-new-refresh", rotated!.RefreshTokenEnc);
    }

    [Fact]
    public async Task SetStatus_And_Cascade_On_Provider_Delete()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Claude", AuthScheme = AuthSchemes.OAuthSubscription });
        await db.Accounts.InsertAsync(Sample("acc-1", "p1"));

        Assert.True(await db.Accounts.SetStatusAsync("acc-1", AccountStatus.Revoked));
        Assert.Equal(AccountStatus.Revoked, (await db.Accounts.GetAsync("acc-1"))!.Status);

        await db.Providers.DeleteAsync("p1");
        Assert.Null(await db.Accounts.GetAsync("acc-1")); // FK cascade
    }
}

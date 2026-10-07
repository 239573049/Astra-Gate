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
    public async Task ListExpiring_Returns_Active_Accounts_Inside_The_Window()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Claude", AuthScheme = AuthSchemes.OAuthSubscription });

        var soon = Sample("acc-soon", "p1");
        soon.ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        var late = Sample("acc-late", "p1");
        late.ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(5);
        var expired = Sample("acc-expired", "p1");
        expired.ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
        expired.Status = AccountStatus.Expired;
        var noExpiry = Sample("acc-noexp", "p1");
        noExpiry.ExpiresAtUtc = null;
        await db.Accounts.InsertAsync(soon);
        await db.Accounts.InsertAsync(late);
        await db.Accounts.InsertAsync(expired);
        await db.Accounts.InsertAsync(noExpiry);

        var due = await db.Accounts.ListExpiringAsync(TimeSpan.FromMinutes(30));
        Assert.Equal(["acc-soon"], due.Select(a => a.Id).ToList());
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

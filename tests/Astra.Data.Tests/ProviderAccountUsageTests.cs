using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Data.Tests;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Astra.Data;

public class ProviderAccountUsageTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 18, 0, 0, 0, TimeSpan.FromHours(8));
    private static readonly DateTimeOffset To = From.AddDays(1);

    private static RequestRecord Request(string id, string? account, DateTimeOffset at, long input, long output, long cost) => new()
    {
        Id = id, ProviderId = "p", AccountId = account, StartedAtUtc = at, InboundProtocol = "openai-chat",
        TotalInputTokens = input, TotalOutputTokens = output, CostNanoUsd = cost,
    };

    private static async Task<AstraDatabase> StartAsync(string scheme = AuthSchemes.OAuthSubscription)
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p", Name = "Provider", AuthScheme = scheme });
        if (AuthSchemes.UsesAccounts(scheme))
            foreach (var id in new[] { "oauth", "key", "disabled", "unused" })
                await db.Accounts.InsertAsync(new ProviderAccount
                {
                    Id = id, ProviderId = "p", DisplayName = id, Enabled = id != "disabled",
                    Kind = id == "key" ? AccountKinds.ApiKey : AccountKinds.OAuth,
                });
        return db;
    }

    [Theory]
    [InlineData(AuthSchemes.OAuthSubscription)]
    [InlineData(AuthSchemes.Plugin)]
    public async Task Accounts_Have_Separate_Totals_And_Local_Day_Usage_Including_Disabled_And_Unused(string scheme)
    {
        var db = await StartAsync(scheme);
        var current = Request("today", "oauth", From, 1000, 100, 11_000_000);
        current.CacheReadTokens = 600; // already included in input
        current.ReasoningTokens = 40; // already included in output
        var failed = Request("failed", "oauth", To.AddMilliseconds(-1), 5, 0, 50);
        failed.Status = RequestStatus.ClientCancelled; // partial usage still belongs to the account
        var refused = Request("refused", "oauth", From.AddHours(1), 0, 0, 0);
        refused.ErrorType = Astra.Core.Tokens.TokenLimits.RefusedErrorType;
        await db.Requests.InsertBatchAsync([
            Request("old", "oauth", From.AddMilliseconds(-1), 100, 20, 1_000_000), current, failed,
            Request("key-today", "key", From.AddMinutes(1), 2000, 200, 22_000_000),
            Request("tomorrow", "key", To, 3, 4, 7),
            Request("disabled-history", "disabled", From.AddDays(-1), 500, 0, 5000),
            Request("no-account", null, From, 999, 999, 999), refused,
            new RequestRecord { Id = "other-provider", ProviderId = "other", AccountId = "oauth", StartedAtUtc = From, InboundProtocol = "openai-chat", TotalInputTokens = 999 },
        ]);

        var usage = (await db.Requests.ProviderAccountUsageAsync("p", From, To)).ToDictionary(u => u.AccountId);
        Assert.Equal(4, usage.Count);
        var oauth = usage["oauth"];
        Assert.Equal(3, oauth.TotalRequests);
        Assert.Equal(1105, oauth.TotalInputTokens);
        Assert.Equal(120, oauth.TotalOutputTokens);
        Assert.Equal(12_000_050, oauth.TotalCostNanoUsd);
        Assert.Equal(2, oauth.TodayRequests);
        Assert.Equal(1005, oauth.TodayInputTokens);
        Assert.Equal(100, oauth.TodayOutputTokens);
        Assert.Equal(11_000_050, oauth.TodayCostNanoUsd);
        Assert.Equal(2, usage["key"].TotalRequests);
        Assert.Equal(1, usage["key"].TodayRequests);
        Assert.Equal(2200, usage["key"].TodayInputTokens + usage["key"].TodayOutputTokens);
        Assert.Equal(22_000_000, usage["key"].TodayCostNanoUsd);
        Assert.Equal(1, usage["disabled"].TotalRequests);
        Assert.Equal(0, usage["disabled"].TodayRequests);
        Assert.Equal(0, usage["unused"].TotalRequests);
        Assert.Equal(0, usage["unused"].TodayCostNanoUsd);

        // Cleaning old logs never reduces lifetime usage; current-day records still supply today's numbers.
        await db.Requests.DeleteOlderThanAsync(From);
        var retained = (await db.Requests.ProviderAccountUsageAsync("p", From, To)).ToDictionary(u => u.AccountId);
        Assert.Equal(oauth.TotalRequests, retained["oauth"].TotalRequests);
        Assert.Equal(oauth.TotalCostNanoUsd, retained["oauth"].TotalCostNanoUsd);
        Assert.Equal(oauth.TodayRequests, retained["oauth"].TodayRequests);
        Assert.Equal(1, retained["disabled"].TotalRequests);
    }

    [Fact]
    public async Task Provider_Api_Key_Has_Its_Own_Zero_Or_Accumulated_Usage()
    {
        var db = await StartAsync(AuthSchemes.Bearer);
        var empty = Assert.Single(await db.Requests.ProviderAccountUsageAsync("p", From, To));
        Assert.Equal("", empty.AccountId);
        Assert.Equal(0, empty.TotalRequests);
        await db.Requests.InsertBatchAsync([
            Request("old", null, From.AddDays(-1), 100, 50, 123),
            Request("today", null, From, 200, 80, 456),
        ]);
        var usage = Assert.Single(await db.Requests.ProviderAccountUsageAsync("p", From, To));
        Assert.Equal(2, usage.TotalRequests);
        Assert.Equal(579, usage.TotalCostNanoUsd);
        Assert.Equal(1, usage.TodayRequests);
        Assert.Equal(280, usage.TodayInputTokens + usage.TodayOutputTokens);
        Assert.Empty(await db.Requests.ProviderAccountUsageAsync("missing", From, To));
    }

    [Fact]
    public async Task A_Failed_Insert_Rolls_Back_The_Request_And_Its_Account_Counters()
    {
        var db = await StartAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Requests.InsertBatchAsync([
            Request("duplicate", "oauth", From, 100, 10, 1000),
            Request("duplicate", "key", From, 200, 20, 2000),
        ]));
        Assert.Null(await db.Requests.GetAsync("duplicate"));
        Assert.All(await db.Requests.ProviderAccountUsageAsync("p", From, To), u => Assert.Equal(0, u.TotalRequests));
    }

    [Fact]
    public async Task Migration_Backfills_The_Original_Per_Account_Bills_Once()
    {
        var db = await StartAsync();
        await db.Requests.InsertBatchAsync([
            Request("oauth-history", "oauth", From.AddDays(-1), 100, 20, 123),
            Request("key-history", "key", From, 200, 40, 456),
        ]);
        await using (var conn = await db.Factory.OpenAsync())
            // Back to just before 0019 — which means undoing 0020_request_routing too: only migrations above the
            // highest recorded version are pending.
            await conn.ExecuteAsync("""
                ALTER TABLE requests DROP COLUMN routing_json;
                DELETE FROM schema_version WHERE version = 20;
                DROP TABLE provider_account_usage_totals;
                DELETE FROM schema_version WHERE version = 19;
                """);
        await db.Migrations.MigrateAsync();
        await db.Migrations.MigrateAsync();
        var usage = (await db.Requests.ProviderAccountUsageAsync("p", From, To)).ToDictionary(u => u.AccountId);
        Assert.Equal(1, usage["oauth"].TotalRequests);
        Assert.Equal(123, usage["oauth"].TotalCostNanoUsd);
        Assert.Equal(456, usage["key"].TotalCostNanoUsd);
        Assert.Equal(240, usage["key"].TotalInputTokens + usage["key"].TotalOutputTokens);
    }
}

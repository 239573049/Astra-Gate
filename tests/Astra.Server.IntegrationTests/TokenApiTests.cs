using System.Net;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Microsoft.AspNetCore.TestHost;

namespace Astra.Server.IntegrationTests;

public class TokenApiTests
{
    [Fact]
    public async Task Default_Token_Exists_With_A_Key_And_Is_Never_Echoed()
    {
        await using var host = await TestHost.StartAsync();
        var list = (await host.GetJsonAsync("/api/tokens")).AsArray();
        var token = Assert.Single(list)!;
        Assert.Equal(TokenIds.Default, token["id"]!.GetValue<string>());
        Assert.True(token["isDefault"]!.GetValue<bool>());
        Assert.True(token["enabled"]!.GetValue<bool>());
        var masked = token["keyMasked"]!.GetValue<string>();
        Assert.StartsWith("sk-astra-", masked);
        Assert.EndsWith("…", masked);

        var (status, secret) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{TokenIds.Default}/reveal");
        Assert.Equal(HttpStatusCode.OK, status);
        var plaintext = secret!["token"]!.GetValue<string>();
        Assert.StartsWith(masked.TrimEnd('…'), plaintext);
        Assert.DoesNotContain(plaintext, list.ToJsonString());

        // Revealing is a mutation: without the admin header it is refused.
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/tokens/{TokenIds.Default}/reveal");
        request.Headers.Add("X-Astra-Admin", "0");
        using var bare = host.App.GetTestClient();
        var refused = await bare.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task Create_Update_Validate_And_Delete()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Scripts", providerId = "missing" });
        Assert.Equal(HttpStatusCode.NotFound, status);

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = " Scripts " });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();
        Assert.Equal("Scripts", body["name"]!.GetValue<string>());
        Assert.False(body["isDefault"]!.GetValue<bool>());
        Assert.Null(body["providerId"]);

        var provider = new Astra.Core.Models.Provider { Id = "p-direct", Name = "Direct" };
        await host.Db.Providers.InsertAsync(provider);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { name = "Renamed", enabled = false, providerId = "p-direct" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Renamed", body!["name"]!.GetValue<string>());
        Assert.False(body["enabled"]!.GetValue<bool>());
        Assert.Equal("p-direct", body["providerId"]!.GetValue<string>());
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new Dictionary<string, object?> { ["providerId"] = null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["providerId"]);
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { enabled = "yes" });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        var before = (await host.Db.Tokens.GetAsync(id))!.KeyHash;
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/tokens/{id}/reset");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body!["rewritten"]!.AsArray());
        Assert.NotEqual(before, (await host.Db.Tokens.GetAsync(id))!.KeyHash);

        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/tokens/{id}");
        Assert.Equal(HttpStatusCode.OK, status);
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/tokens/{id}");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Single((await host.GetJsonAsync("/api/tokens")).AsArray());
    }

    [Fact]
    public async Task A_Provider_Pool_Is_Validated_And_Stored_Canonically()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var (pid, enabled) in new[] { ("pa", true), ("pb", true), ("pc", true), ("off", false) })
            await host.Db.Providers.InsertAsync(new Astra.Core.Models.Provider
            {
                Id = pid, Name = pid.ToUpperInvariant(), Enabled = enabled,
                Endpoints = [new Astra.Core.Models.ProviderEndpoint { BaseUrl = $"https://{pid}.test/v1" }],
            });
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Team" });
        var id = body!["id"]!.GetValue<string>();

        object Pool(params object[] members) => new { pool = new { strategy = "weighted", sticky = "off", maxAttempts = 2, members } };

        // Two members: stored as the pool, the first one also as the direct provider (an older binary still routes).
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}",
            Pool(new { providerId = "pa", weight = 3 }, new { providerId = "pb", tier = 2 }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pa", body!["providerId"]!.GetValue<string>());
        var pool = body["pool"]!;
        Assert.Equal("weighted", pool["strategy"]!.GetValue<string>());
        Assert.Equal("off", pool["sticky"]!.GetValue<string>());
        Assert.Equal(2, pool["maxAttempts"]!.GetValue<int>());
        Assert.Equal(["pa", "pb"], pool["members"]!.AsArray().Select(m => m!["providerId"]!.GetValue<string>()));
        Assert.Equal(3, pool["members"]![0]!["weight"]!.GetValue<int>());
        Assert.Equal(2, pool["members"]![1]!["tier"]!.GetValue<int>());
        Assert.Equal("healthy", pool["members"]![0]!["health"]!["state"]!.GetValue<string>());
        var stored = (await host.Db.Tokens.GetAsync(id))!;
        Assert.Equal(["pa", "pb"], stored.Pool()!.Members.Select(m => m.ProviderId));
        Assert.Equal("pa", stored.ProviderId);
        Assert.Equal(body.ToJsonString(), (await host.GetJsonAsync($"/api/tokens/{id}")).ToJsonString());

        // Invalid pools are refused.
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa" }, new { providerId = "missing" }))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa" }, new { providerId = "off" }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa" }, new { providerId = "pa" }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa", weight = 0 }))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}",
            new { pool = new { strategy = "random", members = new[] { new { providerId = "pa" } } } })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}",
            new { pool = new { members = new[] { new { providerId = "pa" } } }, providerId = "pa" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { pool = "pa" })).Status);

        // A member that got disabled may stay; a deleted one shows as missing.
        var pb = (await host.Db.Providers.GetAsync("pb"))!;
        pb.Enabled = false;
        await host.Db.Providers.UpdateAsync(pb);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa" }, new { providerId = "pb" }, new { providerId = "pc" }));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("disabled", body!["pool"]!["members"]![1]!["health"]!["state"]!.GetValue<string>());
        await host.Db.Providers.DeleteIfUnboundAsync("pc");
        var members = (await host.GetJsonAsync($"/api/tokens/{id}"))["pool"]!["members"]!.AsArray();
        Assert.True(members[2]!["missing"]!.GetValue<bool>());
        Assert.Equal("missing", members[2]!["health"]!["state"]!.GetValue<string>());

        // One member is just the direct provider; none clears direct calls; a providerId patch drops the pool.
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa" }));
        Assert.Null(body!["pool"]);
        Assert.Equal("pa", body["providerId"]!.GetValue<string>());
        await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool(new { providerId = "pa" }, new { providerId = "pb" }));
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", new { providerId = "pa" });
        Assert.Null(body!["pool"]);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{id}", Pool());
        Assert.Null(body!["pool"]);
        Assert.Null(body["providerId"]);

        // Created with a pool (a disabled provider cannot join a new one).
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Pooled", pool = new { members = new[] { new { providerId = "pa" }, new { providerId = "pb" } } } })).Status);
        pb.Enabled = true;
        await host.Db.Providers.UpdateAsync(pb);
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Pooled", pool = new { members = new[] { new { providerId = "pa" }, new { providerId = "pb" } } } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("failover", body!["pool"]!["strategy"]!.GetValue<string>());
        Assert.Equal("session", body["pool"]!["sticky"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stats_Report_Today_And_Lifetime_Usage()
    {
        await using var host = await TestHost.StartAsync();
        var now = DateTimeOffset.UtcNow.AddMilliseconds(-50);
        await host.Db.Requests.InsertBatchAsync(
        [
            Record(now, RequestStatus.Success, cost: 2_500_000_000, input: 1000, cacheRead: 250, output: 300, generationMs: 1500),
            Record(now, RequestStatus.Success, cost: 500_000_000, input: 1000, cacheRead: 750, output: 100, generationMs: 500),
            Record(now, RequestStatus.UpstreamError, cost: 0, input: 0, cacheRead: 0, output: 0, generationMs: null),
            Record(now.AddDays(-3), RequestStatus.Success, cost: 1_000_000_000, input: 2000, cacheRead: 0, output: 400, generationMs: 4000),
        ]);

        var token = Assert.Single((await host.GetJsonAsync("/api/tokens")).AsArray())!;
        var today = token["today"]!;
        Assert.Equal(3m, today["costUsd"]!.GetValue<decimal>());
        Assert.Equal(3, today["requests"]!.GetValue<long>());
        Assert.Equal(2000, today["inputTokens"]!.GetValue<long>());
        Assert.Equal(400, today["outputTokens"]!.GetValue<long>());
        Assert.Equal(2400, today["totalTokens"]!.GetValue<long>());
        Assert.Equal(0.5, today["cacheHitRate"]!.GetValue<double>());
        Assert.Equal(200.0, today["tps"]!.GetValue<double>()); // (300 + 100) / 2 s

        var total = token["total"]!;
        Assert.Equal(4m, total["costUsd"]!.GetValue<decimal>());
        Assert.Equal(4, total["requests"]!.GetValue<long>());
        Assert.Equal(4800, total["totalTokens"]!.GetValue<long>());
        Assert.Equal(0.25, total["cacheHitRate"]!.GetValue<double>());
        Assert.Equal(800.0 / 6, total["tps"]!.GetValue<double>(), 6);
        Assert.NotNull(token["lastUsedAt"]);

        // Filters on the request log and the stats summary.
        var page = await host.GetJsonAsync($"/api/requests?token={TokenIds.Default}");
        Assert.Equal(4, page["total"]!.GetValue<long>());
        Assert.Equal(TokenIds.Default, page["items"]![0]!["tokenId"]!.GetValue<string>());
        var summary = await host.GetJsonAsync("/api/stats/summary?range=today&token=nope");
        Assert.Equal(0, summary["requests"]!.GetValue<long>());
        var series = (await host.GetJsonAsync("/api/stats/timeseries?range=7d&by=token")).AsArray();
        Assert.All(series, p => Assert.Equal(token["name"]!.GetValue<string>(), p!["key"]!.GetValue<string>()));
    }

    private static RequestRecord Record(DateTimeOffset at, string status, long cost, long input, long cacheRead, long output, long? generationMs) => new()
    {
        StartedAtUtc = at,
        ClientKind = "codex",
        TokenId = TokenIds.Default,
        TokenName = "snapshot",
        InboundProtocol = "openai-responses",
        Status = status,
        CostNanoUsd = cost,
        TotalInputTokens = input,
        CacheReadTokens = cacheRead,
        TotalOutputTokens = output,
        GenerationMs = generationMs,
    };
}

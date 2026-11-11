using System.Net;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Requests;

namespace Astra.Server.IntegrationTests;

/// <summary>GET /api/stats/daily-bill: one local day of spending per provider and model (the overview's "daily bill").</summary>
public class DailyBillTests
{
    private static RequestRecord Request(DateTimeOffset at, string? providerId, string? providerName, string model, long costNanos,
        string status = RequestStatus.Success, string? systemModel = null) => new()
    {
        Id = Ulid.NewUlid(),
        StartedAtUtc = at,
        ClientKind = "codex",
        InboundProtocol = "openai-chat",
        RequestedModel = model,
        SystemModelId = systemModel,
        ProviderId = providerId,
        ProviderName = providerName,
        Status = status,
        TotalInputTokens = 1000,
        TotalOutputTokens = 100,
        CostNanoUsd = costNanos,
        UsageSource = "reported",
        BillingTraceJson = "[]",
    };

    [Fact]
    public async Task A_local_day_is_billed_per_provider_and_model_most_expensive_first()
    {
        await using var host = await TestHost.StartAsync();
        // The current name wins over the one logged with the requests; a deleted provider keeps its logged name.
        await host.Db.Providers.InsertAsync(new Provider
        {
            Id = "p-cheap", Name = "Cheap Co", Endpoints = [new ProviderEndpoint { BaseUrl = "https://cheap.test/v1" }],
        });
        // 2026-01-15 in UTC+8 is [2026-01-14T16:00Z, 2026-01-15T16:00Z).
        var start = new DateTimeOffset(2026, 1, 14, 16, 0, 0, TimeSpan.Zero);
        await host.Db.Requests.InsertBatchAsync(
        [
            Request(start.AddMinutes(30), "p-pricey", "Pricey", "model-b", 1_000_000),
            Request(start.AddHours(3), "p-pricey", "Pricey", "requested-a", 3_000_000, systemModel: "model-a"),
            Request(start.AddHours(5), "p-cheap", "Old name", "model-c", 250_000),
            Request(start.AddHours(23).AddMinutes(59), "p-cheap", "Old name", "model-c", 250_000, RequestStatus.UpstreamError),
            // Never reached a provider: not on the bill.
            Request(start.AddHours(6), null, null, "model-z", 0, RequestStatus.GatewayError),
            // The local days before and after.
            Request(start.AddMinutes(-1), "p-pricey", "Pricey", "model-a", 9_000_000),
            Request(start.AddDays(1), "p-pricey", "Pricey", "model-a", 9_000_000),
        ]);

        var bill = await host.GetJsonAsync("/api/stats/daily-bill?date=2026-01-15&tzOffset=480");

        Assert.Equal("2026-01-15", bill["date"]!.GetValue<string>());
        Assert.Equal(480, bill["tzOffsetMinutes"]!.GetValue<int>());
        Assert.Equal(start, bill["fromUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(start.AddDays(1), bill["toUtc"]!.GetValue<DateTimeOffset>()); // a past day runs to its end
        Assert.Equal(0.0045m, bill["costUsd"]!.GetValue<decimal>());
        Assert.Equal(4, bill["requests"]!.GetValue<long>());
        Assert.Equal(3, bill["successRequests"]!.GetValue<long>());
        Assert.Equal(4000, bill["inputTokens"]!.GetValue<long>());
        Assert.Equal(400, bill["outputTokens"]!.GetValue<long>());

        var providers = bill["providers"]!.AsArray();
        Assert.Equal(["Pricey", "Cheap Co"], providers.Select(p => p!["name"]!.GetValue<string>()));
        var pricey = providers[0]!;
        Assert.Equal("p-pricey", pricey["providerId"]!.GetValue<string>());
        Assert.Equal(0.004m, pricey["costUsd"]!.GetValue<decimal>());
        Assert.Equal(2, pricey["requests"]!.GetValue<long>());
        // The catalog model names the line; the dearer model comes first.
        Assert.Equal(["model-a", "model-b"], pricey["models"]!.AsArray().Select(m => m!["model"]!.GetValue<string>()));
        var cheap = Assert.Single(providers[1]!["models"]!.AsArray())!;
        Assert.Equal("model-c", cheap["model"]!.GetValue<string>());
        Assert.Equal(2, cheap["requests"]!.GetValue<long>());
        Assert.Equal(0.0005m, cheap["costUsd"]!.GetValue<decimal>());
        Assert.Equal(2000, cheap["inputTokens"]!.GetValue<long>());
        Assert.Equal(200, cheap["outputTokens"]!.GetValue<long>());
    }

    [Fact]
    public async Task Today_runs_to_now_and_an_empty_day_has_no_providers()
    {
        await using var host = await TestHost.StartAsync();
        var before = DateTimeOffset.UtcNow;

        var bill = await host.GetJsonAsync("/api/stats/daily-bill?tzOffset=0");

        Assert.Equal(before.ToString("yyyy-MM-dd"), bill["date"]!.GetValue<string>());
        Assert.Equal(new DateTimeOffset(before.UtcDateTime.Date, TimeSpan.Zero), bill["fromUtc"]!.GetValue<DateTimeOffset>());
        var to = bill["toUtc"]!.GetValue<DateTimeOffset>();
        Assert.True(to >= before && to <= DateTimeOffset.UtcNow, $"toUtc {to:O} is not now");
        Assert.Equal(0m, bill["costUsd"]!.GetValue<decimal>());
        Assert.Empty(bill["providers"]!.AsArray());
    }

    [Fact]
    public async Task A_malformed_date_is_refused()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Get, "/api/stats/daily-bill?date=15-01-2026");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("yyyy-MM-dd", body!["error"]!.GetValue<string>());
    }
}

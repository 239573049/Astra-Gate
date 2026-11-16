using System.Net;
using Astra.Core.Requests;
using Astra.Data;

namespace Astra.Server.IntegrationTests;

/// <summary>Display currency settings (manual rates, no network) and the request log's context-window view.</summary>
public sealed class DisplayCurrencyAndContextTests
{
    [Fact]
    public async Task Currency_settings_need_a_rate_and_keep_money_in_usd()
    {
        await using var host = await TestHost.StartAsync();
        var initial = await host.GetJsonAsync("/api/settings");
        Assert.Equal("USD", initial["displayCurrency"]!.GetValue<string>());

        // A currency without a rate cannot be the display currency.
        var (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { displayCurrency = "CNY" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("USD", (await host.GetJsonAsync("/api/settings"))["displayCurrency"]!.GetValue<string>());

        (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { currencyRates = new { CNY = "0" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, var body) = await host.SendAsync(HttpMethod.Patch, "/api/settings",
            new { currencyRates = new { cny = "7.2", JPY = "150" }, displayCurrency = "cny" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("CNY", body!["displayCurrency"]!.GetValue<string>());
        Assert.Equal("7.2", body["currencyRates"]!["CNY"]!.GetValue<string>());

        // Back to USD keeps the table.
        (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { displayCurrency = "USD" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("150", body!["currencyRates"]!["JPY"]!.GetValue<string>());
    }

    [Fact]
    public void Alert_messages_read_cost_budgets_in_the_display_currency_while_the_observation_stays_usd()
    {
        var observed = new System.Text.Json.Nodes.JsonObject { ["metric"] = "cost", ["period"] = "day", ["usedPercent"] = 90, ["used"] = 9, ["limit"] = 10 };
        var condition = new System.Text.Json.Nodes.JsonObject { ["value"] = 80 };
        var cny = Astra.Core.Currency.Currencies.Resolve("CNY", new Dictionary<string, string> { ["CNY"] = "7.2" });
        var plain = Astra.Server.Alerts.AlertMessages.Format("en", Astra.Core.Alerts.AlertEventIds.TokenUsageHigh, "firing", "t1", observed, condition);
        var converted = Astra.Server.Alerts.AlertMessages.Format("en", Astra.Core.Alerts.AlertEventIds.TokenUsageHigh, "firing", "t1", observed, condition, cny);
        Assert.Contains("(9 / 10)", plain); // USD install: as before
        Assert.Contains("(¥64.80 / ¥72.00)", converted);
    }

    [Fact]
    public async Task Request_dtos_carry_the_context_window_fill_and_the_prompt_split()
    {
        await using var host = await TestHost.StartAsync();
        const string id = "01TESTCONTEXT000000000000A";
        const string legacy = "01TESTCONTEXT000000000000B";
        await host.Db.Requests.InsertBatchAsync(
        [
            new RequestRecord
            {
                Id = id, StartedAtUtc = DateTimeOffset.UtcNow, InboundProtocol = "anthropic", RequestedModel = "claude-sonnet-4-6",
                Status = RequestStatus.Success, TotalInputTokens = 50_000, CacheReadTokens = 40_000, TotalOutputTokens = 800,
                ContextJson = """{"window":200000,"max_output":64000,"shares":{"system":0.1,"tools":0.2,"history":0.6,"latest":0.1}}""",
            },
            new RequestRecord
            {
                Id = legacy, StartedAtUtc = DateTimeOffset.UtcNow, InboundProtocol = "openai-chat", RequestedModel = "gpt-5",
                Status = RequestStatus.Success, TotalInputTokens = 10,
            },
        ]);

        var detail = await host.GetJsonAsync($"/api/requests/{id}");
        var context = detail["context"]!;
        Assert.Equal(200_000, context["window"]!.GetValue<long>());
        Assert.Equal(0.25, context["fill"]!.GetValue<double>(), 3);
        Assert.Equal(40_000, context["cacheReadTokens"]!.GetValue<long>());
        Assert.Equal(64_000, context["maxOutput"]!.GetValue<long>());
        Assert.Equal(30_000, context["parts"]!["history"]!.GetValue<long>());
        Assert.Equal(10_000, context["parts"]!["tools"]!.GetValue<long>());

        var page = await host.GetJsonAsync("/api/requests");
        var item = page["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == id)!;
        Assert.Equal(0.25, item["context"]!["fill"]!.GetValue<double>(), 3);

        // Rows logged before the column existed expose nothing.
        Assert.Null((await host.GetJsonAsync($"/api/requests/{legacy}"))["context"]);
    }
}

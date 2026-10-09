using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public class GatewayCacheWriteTests
{
    [Theory]
    [InlineData(ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIResponses, false)]
    [InlineData(ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIResponses, true)]
    [InlineData(ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat, false)]
    [InlineData(ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat, true)]
    [InlineData(ApiProtocol.OpenAIChat, ApiProtocol.OpenAIChat, false)]
    [InlineData(ApiProtocol.OpenAIChat, ApiProtocol.OpenAIChat, true)]
    [InlineData(ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, false)]
    [InlineData(ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, true)]
    public async Task Cache_Writes_Reach_Clients_Logs_And_Billing(ApiProtocol upstream, ApiProtocol inbound, bool stream)
    {
        var responses = inbound == ApiProtocol.OpenAIResponses;
        await using var gw = await GatewayFixture.StartAsync(responses ? ClientKinds.Codex : ClientKinds.OpenCode,
            upstream, _ => UpstreamResponse(upstream, stream));
        await SetPricingAsync(gw);
        var request = JsonNode.Parse(responses
            ? """{"model":"gpt-5","input":"hi"}"""
            : """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""")!.AsObject();
        request["stream"] = stream;
        if (!responses && stream) request["stream_options"] = new JsonObject { ["include_usage"] = true };
        var response = await gw.PostAsync(responses ? "/v1/responses" : "/v1/chat/completions", request.ToJsonString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var clientUsage = stream
            ? SseParser.ParseAll(text).Where(e => e.Data != "[DONE]").Select(e => JsonNode.Parse(e.Data)!)
                .Select(b => responses ? b["response"]?["usage"] : b["usage"]).Last(u => u is not null)!
            : JsonNode.Parse(text)!["usage"]!;
        Assert.Equal(9_290, clientUsage[responses ? "input_tokens_details" : "prompt_tokens_details"]!["cache_write_tokens"]!.GetValue<long>());
        Assert.Equal(101_519, clientUsage[responses ? "input_tokens" : "prompt_tokens"]!.GetValue<long>());
        Assert.Equal(102_431, clientUsage["total_tokens"]!.GetValue<long>());

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(upstream == inbound, record.Passthrough);
        await AssertLoggedUsageAsync(gw, record, upstream);
    }

    [Theory]
    [InlineData(ApiProtocol.OpenAIResponses, false)]
    [InlineData(ApiProtocol.OpenAIResponses, true)]
    [InlineData(ApiProtocol.OpenAIChat, false)]
    [InlineData(ApiProtocol.OpenAIChat, true)]
    public async Task Connection_Test_Logs_Cache_Writes(ApiProtocol protocol, bool stream)
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, protocol, _ => UpstreamResponse(protocol, stream));
        await SetPricingAsync(gw);
        var result = await gw.Host.TestAsync(gw.Provider.Id, new { modelId = "gpt-5", stream });
        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        await gw.Host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await gw.Host.Db.Requests.QueryAsync(new RequestQuery { ProviderId = gw.Provider.Id });
        var record = await gw.Host.Db.Requests.GetAsync(Assert.Single(records.Items).Id);
        Assert.NotNull(record);
        await AssertLoggedUsageAsync(gw, record, protocol);
    }

    private static Task SetPricingAsync(GatewayFixture gw) => gw.Host.Db.Providers.InsertModelAsync(new ProviderModel
    {
        ProviderId = gw.Provider.Id,
        ModelId = "gpt-5",
        SystemModelId = "gpt-5",
        Overrides = new ModelOverrides
        {
            Pricing = new PricingSchedule
            {
                Base = new PriceSet
                {
                    [TokenTypes.Input] = 1m, [TokenTypes.CacheRead] = 0.1m, [TokenTypes.CacheWrite5m] = 2m,
                    [TokenTypes.Output] = 3m, [TokenTypes.Reasoning] = 4m,
                },
            },
        },
    });

    private static async Task AssertLoggedUsageAsync(GatewayFixture gw, RequestRecord record, ApiProtocol protocol)
    {
        var raw = Fixture(protocol)["usage"]!;
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal("reported", record.UsageSource);
        Assert.Equal(101_519, record.TotalInputTokens);
        Assert.Equal(912, record.TotalOutputTokens);
        Assert.Equal(92_226, record.CacheReadTokens);
        Assert.Equal(9_290, record.CacheWriteTokens);
        Assert.Equal(516, record.ReasoningTokens);
        Assert.Equal(3, Assert.Single(record.UsageItems, i => i.TokenType == TokenTypes.Input).Tokens);
        var write = Assert.Single(record.UsageItems, i => i.TokenType == TokenTypes.CacheWrite5m);
        Assert.Equal(9_290, write.Tokens);
        Assert.Equal(18_580_000, write.CostNanoUsd);
        Assert.Equal(31_057_600, record.CostNanoUsd);
        Assert.True(JsonNode.DeepEquals(raw, JsonNode.Parse(record.UsageRawJson!)));
        Assert.Contains("cache_write_tokens", record.BillingDescription);

        var page = await gw.Host.GetJsonAsync("/api/requests");
        var summary = Assert.Single(page["items"]!.AsArray())!;
        Assert.Equal(9_290, summary["cacheWriteTokens"]!.GetValue<long>());
        var detail = await gw.Host.GetJsonAsync($"/api/requests/{record.Id}");
        Assert.Equal(9_290, detail["cacheWriteTokens"]!.GetValue<long>());
        Assert.True(JsonNode.DeepEquals(raw, detail["usageRaw"]));
        var item = Assert.Single(detail["usageItems"]!.AsArray(), i => i!["tokenType"]!.GetValue<string>() == TokenTypes.CacheWrite5m)!;
        Assert.Equal(9_290, item["tokens"]!.GetValue<long>());
        Assert.Equal(18_580_000, item["costNanoUsd"]!.GetValue<long>());
        var totals = await gw.Host.GetJsonAsync("/api/stats/summary?range=7d");
        Assert.Equal(101_519, totals["inputTokens"]!.GetValue<long>());
        Assert.Equal(9_290, totals["cacheWriteTokens"]!.GetValue<long>());
    }

    private static JsonObject Fixture(ApiProtocol protocol) => JsonNode.Parse(File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", protocol == ApiProtocol.OpenAIResponses ? "responses" : "chat",
        "response-cache-write.json")))!.AsObject();

    private static HttpResponseMessage UpstreamResponse(ApiProtocol protocol, bool stream)
    {
        var body = Fixture(protocol);
        if (!stream) return FakeUpstream.Json(body.ToJsonString());
        if (protocol == ApiProtocol.OpenAIResponses)
        {
            var terminal = new JsonObject { ["type"] = "response.completed", ["response"] = body };
            return FakeUpstream.Sse($"event: response.completed\ndata: {terminal.ToJsonString()}\n\n");
        }
        body["object"] = "chat.completion.chunk";
        body["choices"] = new JsonArray();
        var content = body.DeepClone().AsObject();
        content.Remove("usage");
        content["choices"] = JsonNode.Parse("""[{"index":0,"delta":{"role":"assistant","content":"OK"},"finish_reason":"stop"}]""");
        return FakeUpstream.Sse($"data: {content.ToJsonString()}\n\ndata: {body.ToJsonString()}\n\ndata: [DONE]\n\n");
    }
}

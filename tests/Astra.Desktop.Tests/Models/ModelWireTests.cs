using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Models;

/// <summary>
/// What the models pages put on the wire and read back, through the real <see cref="ApiClient"/>: the request bodies are the web's
/// (camelCase fields, snake_case pricing JSON, prices as exact decimal numbers) and every type resolves from generated JSON metadata
/// (no reflection fallback, which would throw under Native AOT).
/// </summary>
public class ModelWireTests
{
    private sealed class Recorder(string answer = "{}", HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body, bool Admin)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken), request.Headers.Contains("X-Astra-Admin")));
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }

    private static (ApiClient Api, Recorder Calls) Client(string answer = "{}")
    {
        var recorder = new Recorder(answer);
        return (new ApiClient("http://fixture", recorder), recorder);
    }

    private const string ModelAnswer = """
        {"id":"gpt-4.1","displayName":"GPT-4.1","vendor":"openai","family":null,"aliases":["a"],"contextWindow":1047576,"maxOutputTokens":null,
         "capabilities":{"vision":true},"pricing":{"currency":"USD","unit":"per_1m_tokens","base":{"input":0.000003,"output":15}},
         "source":"seed","userModifiedFields":["pricing"],"enabled":true,"providerPriceKeys":["openai"],"updatedAt":"2026-10-06T12:00:00Z",
         "providerPrices":[{"priceKey":"openai","upstreamModelId":null,"pricing":{"base":{"input":1e-7}},"source":"seed","userModified":false,"updatedAt":"2026-10-06T12:00:00Z"}],
         "providerOverrides":[]}
        """;

    [Fact]
    public async Task Detail_response_keeps_pricing_numbers_exact()
    {
        var (api, _) = Client(ModelAnswer);
        var detail = await api.GetAsync<ModelDetailDto>(ModelCatalog.DetailPath("gpt-4.1"));
        Assert.Equal(0.000003m, PriceNumber.FromNode(detail!.Pricing!["base"]!["input"]));
        Assert.Equal(0.0000001m, PriceNumber.FromNode(detail.ProviderPrices[0].Pricing!["base"]!["input"]));
        Assert.Equal("$0.000003 / $15", PriceFormat.Summary(detail.Pricing));
        Assert.True(detail.Capabilities.Vision);
        Assert.Equal(["pricing"], detail.UserModifiedFields);
    }

    [Fact]
    public async Task Saving_a_model_puts_the_form_as_the_web_does()
    {
        var (api, calls) = Client(ModelAnswer);
        var form = ModelForm.From((await api.GetAsync<ModelDetailDto>("/x"))!);
        calls.Calls.Clear();
        form = form with { DisplayName = "GPT 4.1", AliasText = "a, b" };
        form = form with { Pricing = PricingDocument.FromNode(form.Pricing)!.WithBasePrice("input", 0.000004m).ToNode() };

        await api.PutAsync<ModelInput, ModelDto>(ModelCatalog.DetailPath("gpt-4.1"), form.ToInput());

        var call = Assert.Single(calls.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal("/api/models/gpt-4.1", call.Path);
        Assert.True(call.Admin);
        var body = JsonNode.Parse(call.Body!)!;
        Assert.Equal("GPT 4.1", body["displayName"]!.GetValue<string>());
        Assert.Equal("a", body["aliases"]![0]!.GetValue<string>());
        Assert.Equal("b", body["aliases"]![1]!.GetValue<string>());
        Assert.Equal(1047576, body["contextWindow"]!.GetValue<long>());
        Assert.True(body["capabilities"]!["vision"]!.GetValue<bool>());
        // pricing stays in storage format: snake_case keys, decimals written exactly
        Assert.Contains("\"input\":0.000004", body["pricing"]!["base"]!.ToJsonString());
        Assert.Contains("\"output\":15", body["pricing"]!["base"]!.ToJsonString());
    }

    [Fact]
    public async Task Creating_a_model_posts_the_web_body()
    {
        var (api, calls) = Client(ModelAnswer);
        var draft = new CreateModelDraft { Id = "my-model-1", Vendor = "acme", ContextWindow = 128_000, InputPrice = 0.000003m, OutputPrice = 15m };
        await api.PostAsync<ModelInput, ModelDto>("/api/models", draft.ToInput()!);
        var body = JsonNode.Parse(Assert.Single(calls.Calls).Body!)!;
        Assert.Equal("my-model-1", body["id"]!.GetValue<string>());
        Assert.Equal("my-model-1", body["displayName"]!.GetValue<string>());
        Assert.Equal("acme", body["vendor"]!.GetValue<string>());
        Assert.Equal("""{"currency":"USD","unit":"per_1m_tokens","base":{"input":0.000003,"output":15}}""", body["pricing"]!.ToJsonString());
    }

    [Fact]
    public async Task Provider_price_put_and_field_reset_and_sync_apply_bodies()
    {
        var (api, calls) = Client("{}");
        await api.PutAsync<ModelPriceInput, ModelPriceDto>("/api/models/m/prices/openai", new ModelPriceInput("up-id", JsonNode.Parse("""{"base":{"input":1.5}}""")!));
        await api.PostAsync("/api/models/m/reset-field", new ResetFieldInput("contextWindow"));
        await api.PostAsync<SyncApplyRequest, SyncApplyResult>("/api/models/sync/apply", new SyncApplyRequest { ChangeIds = ["c1", "c2"] });
        Assert.Equal("""{"upstreamModelId":"up-id","pricing":{"base":{"input":1.5}}}""", calls.Calls[0].Body);
        Assert.Equal("""{"field":"contextWindow"}""", calls.Calls[1].Body);
        Assert.Equal("""{"changeIds":["c1","c2"]}""", calls.Calls[2].Body);
    }

    [Fact]
    public async Task Billing_request_and_result_round_trip()
    {
        const string answer = """
            {"items":[{"tokenType":"input","tokens":1000000,"isPerCall":false,"baseUnitPrice":0.000003,"unitPrice":0.000003,"multiplier":1,"multiplierSources":[],"tier":"base","pricedAs":"input","costNanos":3,"note":null}],
             "totalNanos":3,"totalUsd":0.000000003,"priced":true,"trace":[],"description":"d","pricingSource":"system_default","priceKey":null}
            """;
        var (api, calls) = Client(answer);
        var request = BillingSimulation.BuildRequest("gpt-4.1", null, "", BillingSimulation.DefaultTokens(), "flex", new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), "en");
        var result = await api.PostAsync<BillingSimulateRequest, BillingResultDto>("/api/billing/simulate", request);

        var body = JsonNode.Parse(Assert.Single(calls.Calls).Body!)!;
        Assert.Equal("gpt-4.1", body["modelId"]!.GetValue<string>());
        Assert.Equal(1_000_000, body["usage"]!["tokens"]!["input"]!.GetValue<long>());
        Assert.Equal("flex", body["usage"]!["serviceTier"]!.GetValue<string>());
        Assert.Equal("en", body["locale"]!.GetValue<string>());
        Assert.StartsWith("2026-10-26T12:00:00", body["requestTimeUtc"]!.GetValue<string>());
        Assert.Equal(0.000003m, result!.Items[0].UnitPrice);
        Assert.Equal(3, result.TotalNanos);
        Assert.Equal("$0.000003", BillingSimulation.Rows(result)[0].UnitPriceText);
        Assert.Equal("$0.000000003", BillingSimulation.Rows(result)[0].CostText);
    }

    [Fact]
    public async Task Sync_preview_and_lists_decode()
    {
        var (api, _) = Client("""{"source":"models.dev","fetchedAt":"2026-10-26T12:00:00Z","changes":[{"id":"c1","modelId":"m","kind":"price","priceKey":"openai","before":{"pricing":{"base":{"input":1}}},"after":{"pricing":{"base":{"input":2}}},"skippedBecauseUserModified":false}]}""");
        var preview = await api.PostAsync<SyncPreviewDto>("/api/models/sync/preview");
        var change = Assert.Single(preview!.Changes);
        Assert.Equal(["base.input"], ModelSync.DiffLines(change.Before, change.After).Select(l => l.Path));

        var (listApi, _) = Client("[" + ModelAnswer + "]");
        var list = await listApi.GetAsync<List<ModelDto>>("/api/models");
        Assert.Equal("gpt-4.1", Assert.Single(list!).Id);
        var (keysApi, _) = Client("""[{"priceKey":"openai","label":"OpenAI","templateIds":["openai"]}]""");
        Assert.Equal("openai", Assert.Single((await keysApi.GetAsync<List<PriceKeyInfo>>("/api/price-keys"))!).PriceKey);
    }

    [Fact]
    public async Task Every_type_resolves_from_generated_metadata_without_the_reflection_fallback()
    {
        var (api, _) = Client(ModelAnswer);
        await api.GetAsync<ModelDetailDto>("/x");
        await Client("[]").Api.GetAsync<List<ModelDto>>("/x");
        await api.PutAsync<ModelInput, ModelDto>("/x", ModelForm.From(new ModelDto { Id = "a" }).ToInput());
        await api.PostAsync<ModelInput, ModelDto>("/x", new ModelInput());
        await api.PutAsync<ModelPriceInput, ModelPriceDto>("/x", new ModelPriceInput(null, new JsonObject()));
        await api.PostAsync("/x", new ResetFieldInput("family"));
        await api.PostAsync<SyncApplyRequest, SyncApplyResult>("/x", new SyncApplyRequest());
        await api.PostAsync<SyncPreviewDto>("/x");
        await Client("[]").Api.GetAsync<List<PriceKeyInfo>>("/x");
        await api.PostAsync<BillingSimulateRequest, BillingResultDto>("/x", new BillingSimulateRequest());
        // (other tests in the run may register throw-away types; only the ones these pages use matter)
        Assert.DoesNotContain(JsonContexts.FallbackTypes, t => t.Namespace is "Astra.Api.Contracts" or "Astra.Core.Models" or "Astra.Core.Billing");
    }

    [Fact]
    public async Task A_failed_call_carries_the_servers_message()
    {
        var api = new ApiClient("http://fixture", new Recorder("""{"error":"Model 'x' already exists"}""", HttpStatusCode.Conflict));
        var e = await Assert.ThrowsAsync<ApiException>(() => api.PostAsync<ModelInput, ModelDto>("/api/models", new ModelInput { Id = "x" }));
        Assert.Equal(409, e.Status);
        Assert.Equal("Model 'x' already exists", e.Message);
    }
}

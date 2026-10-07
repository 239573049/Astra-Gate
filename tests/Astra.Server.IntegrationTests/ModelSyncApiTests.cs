using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Data;
using Astra.Server.Api;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// /api/models/sync/preview + /apply against a TestServer with a fake "model-sync" primary handler —
/// no real upstream request ever leaves the process, the database lives in a temp dir.
/// </summary>
public class ModelSyncApiTests
{
    // A small models.dev catalog: an updated seed model (deepseek), a new model (grok-9), a user-owned
    // model (my-model), third-party prices (openrouter / siliconflow) and non-chat models that must be skipped.
    private const string CatalogJson = """
        {
          "openai": {
            "models": {
              "my-model": {
                "id": "my-model", "name": "My Model",
                "modalities": { "input": ["text"], "output": ["text"] },
                "tool_call": true,
                "limit": { "context": 128000, "output": 4096 },
                "cost": { "input": 1.5, "output": 6, "cache_read": 0.15 }
              }
            }
          },
          "deepseek": {
            "models": {
              "deepseek-v4-flash": {
                "id": "deepseek-v4-flash", "name": "DeepSeek V4 Flash Updated", "family": "deepseek-flash",
                "modalities": { "input": ["text"], "output": ["text"] },
                "tool_call": true, "reasoning": true, "structured_output": true,
                "limit": { "context": 2000000, "output": 393216 },
                "cost": { "input": 0.2, "output": 0.9, "cache_read": 0.004, "cache_write": 0.3 }
              },
              "deepseek-embedding-lite": {
                "id": "deepseek-embedding-lite",
                "modalities": { "input": ["text"], "output": [] },
                "cost": { "input": 0.01, "output": 0.01 }
              },
              "deepseek-audio-x": {
                "id": "deepseek-audio-x",
                "modalities": { "input": ["audio"], "output": ["audio"] },
                "cost": { "input": 1, "output": 2 }
              }
            }
          },
          "xai": {
            "models": {
              "grok-9": {
                "id": "grok-9", "name": "Grok 9", "family": "grok",
                "modalities": { "input": ["text", "image"], "output": ["text"] },
                "tool_call": true, "reasoning": true, "structured_output": true,
                "limit": { "context": 256000, "output": 32768 },
                "cost": { "input": 2, "output": 8 }
              }
            }
          },
          "openrouter": {
            "models": {
              "deepseek/deepseek-v4-flash": {
                "id": "deepseek/deepseek-v4-flash", "canonical_model_id": "deepseek/deepseek-v4-flash",
                "modalities": { "output": ["text"] },
                "cost": { "input": 0.014, "output": 1.3 }
              },
              "xai/grok-9": {
                "id": "xai/grok-9", "canonical_model_id": "xai/grok-9",
                "modalities": { "output": ["text"] },
                "cost": { "input": 2.5, "output": 9 }
              }
            }
          },
          "siliconflow": {
            "models": {
              "deepseek-ai/DeepSeek-V4-Flash-0928": {
                "id": "deepseek-ai/DeepSeek-V4-Flash-0928",
                "modalities": { "output": ["text"] },
                "cost": { "input": 0.21, "cache_read": 0.015, "output": 0.7 }
              }
            }
          },
          "alibaba": {
            "models": {
              "llama-4-scout": {
                "id": "llama-4-scout",
                "modalities": { "output": ["text"] },
                "cost": { "input": 0.1, "output": 0.4 }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task Preview_diffs_modelsdev_against_the_catalog_without_writing()
    {
        var handler = FakeCatalogHandler.Json(CatalogJson);
        await using var host = await StartAsync(handler);
        await CreateUserModelAsync(host);

        var (status, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["https://models.dev/api.json"], handler.Requests);
        Assert.Equal("https://models.dev/api.json", preview!["source"]!.GetValue<string>());
        Assert.True(DateTimeOffset.Parse(preview["fetchedAt"]!.GetValue<string>()) > DateTimeOffset.UtcNow.AddMinutes(-5));

        var changes = preview["changes"]!.AsArray();
        var ids = changes.Select(c => c!["id"]!.GetValue<string>()).ToList();
        var prefix = ids[0][..ids[0].LastIndexOf(':')];
        Assert.All(ids, id => Assert.StartsWith(prefix + ":", id));
        Assert.Equal(ids.Distinct().Count(), ids.Count);

        // field kind: before/after are plain values; the pricing schedule keeps the seed rules
        var pricing = Change(preview, "deepseek-v4-flash", "field", field: "pricing");
        Assert.Equal(0.15m, pricing["before"]!["base"]!["input"]!.GetValue<decimal>());
        Assert.Equal(0.2m, pricing["after"]!["base"]!["input"]!.GetValue<decimal>());
        Assert.Equal(2, pricing["after"]!["time_windows"]!.AsArray().Count);
        Assert.Contains("off-peak", pricing["after"]!["notes"]!.GetValue<string>());
        Assert.False(pricing["skippedBecauseUserModified"]!.GetValue<bool>());
        Assert.NotNull(Change(preview, "deepseek-v4-flash", "field", field: "displayName"));
        Assert.NotNull(Change(preview, "deepseek-v4-flash", "field", field: "contextWindow"));

        // price kind: {upstreamModelId, pricing} on both sides, merged base keeps stored keys
        var openrouter = Change(preview, "deepseek-v4-flash", "price", priceKey: "openrouter");
        Assert.Equal(0.0134m, openrouter["before"]!["pricing"]!["base"]!["input"]!.GetValue<decimal>());
        Assert.Equal(0.014m, openrouter["after"]!["pricing"]!["base"]!["input"]!.GetValue<decimal>());
        Assert.Equal(0.0134m, openrouter["after"]!["pricing"]!["base"]!["cache_read"]!.GetValue<decimal>());
        Assert.NotNull(Change(preview, "deepseek-v4-flash", "price", priceKey: "siliconflow"));

        // new_model kind: before absent, after describes the model including its provider prices
        var newModel = Change(preview, "grok-9", "new_model");
        Assert.Null(newModel["before"]);
        Assert.Equal("xai", newModel["after"]!["vendor"]!.GetValue<string>());
        Assert.NotNull(newModel["after"]!["providerPrices"]!["openrouter"]);

        // the user-owned model is listed, but every change is flagged
        Assert.All(changes.Where(c => c!["modelId"]!.GetValue<string>() == "my-model"),
            c => Assert.True(c!["skippedBecauseUserModified"]!.GetValue<bool>()));
        Assert.True(Change(preview, "my-model", "field", field: "pricing")["skippedBecauseUserModified"]!.GetValue<bool>());

        // only chat text models: embedding/audio models and non-qwen alibaba ids never appear
        Assert.DoesNotContain(changes, c => c!["modelId"]!.GetValue<string>().Contains("embedding"));
        Assert.DoesNotContain(changes, c => c!["modelId"]!.GetValue<string>() == "deepseek-audio-x");
        Assert.DoesNotContain(changes, c => c!["modelId"]!.GetValue<string>() == "llama-4-scout");

        // preview writes nothing
        Assert.Equal(0.15m, (await host.Db.Models.GetAsync("deepseek-v4-flash"))!.Pricing!.Base["input"]);
        Assert.Null(await host.Db.Models.GetAsync("grok-9"));
    }

    [Fact]
    public async Task Apply_writes_selected_changes_atomically_and_preserves_pricing_rules()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        await CreateUserModelAsync(host);

        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var ids = new[]
        {
            IdOf(Change(preview!, "deepseek-v4-flash", "field", field: "pricing")),
            IdOf(Change(preview!, "deepseek-v4-flash", "price", priceKey: "openrouter")),
            IdOf(Change(preview!, "grok-9", "new_model")),
        };

        var (status, applied) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = ids });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, applied!["applied"]!.GetValue<int>());

        // Seed rules survive; sync ownership protects confirmed values from a later seed upgrade.
        var flash = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.NotNull(flash);
        Assert.Equal("sync", flash!.Source);
        Assert.Empty(flash.UserModifiedFields);
        Assert.Equal(0.2m, flash.Pricing!.Base["input"]);
        Assert.Equal(0.9m, flash.Pricing.Base["output"]);
        Assert.Equal(0.004m, flash.Pricing.Base["cache_read"]);
        Assert.Equal(0.3m, flash.Pricing.Base["cache_write_5m"]);
        Assert.Equal(2, flash.Pricing.TimeWindows!.Count);
        Assert.Contains("off-peak", flash.Pricing.Notes);
        Assert.Equal("DeepSeek V4 Flash", flash.DisplayName); // unselected change stays untouched

        // third-party price updated in place: upstream id and user flags untouched
        var openrouter = await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "openrouter");
        Assert.NotNull(openrouter);
        Assert.Equal(0.014m, openrouter!.Pricing.Base["input"]);
        Assert.Equal(0.0134m, openrouter.Pricing.Base["cache_read"]);
        Assert.Equal(1.3m, openrouter.Pricing.Base["output"]);
        Assert.Equal("deepseek/deepseek-v4-flash-0731", openrouter.UpstreamModelId);
        Assert.False(openrouter.UserModified);
        Assert.Equal("sync", openrouter.Source);

        // new model written together with its provider prices (model row before price rows)
        var grok = await host.Db.Models.GetAsync("grok-9");
        Assert.NotNull(grok);
        Assert.Equal("sync", grok!.Source);
        Assert.Equal("xai", grok.Vendor);
        Assert.Equal(256000, grok.ContextWindow);
        Assert.True(grok.Capabilities.Vision == true);
        Assert.True(grok.Capabilities.Reasoning == true);
        Assert.True(grok.Capabilities.StructuredOutput == true);
        Assert.Equal(2m, grok.Pricing!.Base["input"]);
        var grokPrice = await host.Db.Models.GetPriceAsync("grok-9", "openrouter");
        Assert.NotNull(grokPrice);
        Assert.Equal("sync", grokPrice!.Source);
        Assert.False(grokPrice.UserModified);
        Assert.Equal("xai/grok-9", grokPrice.UpstreamModelId);
        Assert.Equal(2.5m, grokPrice.Pricing.Base["input"]);
        Assert.Equal(9m, grokPrice.Pricing.Base["output"]);

        // the new model's prices are queryable per price key
        Assert.Contains(await host.Db.Models.ListPricesAsync("grok-9"), p => p.PriceKey == "openrouter");
    }

    [Fact]
    public async Task Apply_dedupes_ids_and_returns_zero_for_empty_selection()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var id = IdOf(Change(preview!, "deepseek-v4-flash", "price", priceKey: "siliconflow"));

        var (status, empty) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, empty!["applied"]!.GetValue<int>());
        Assert.Equal(0.22m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);

        (status, var applied) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { id, id } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, applied!["applied"]!.GetValue<int>());
        Assert.Equal(0.21m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);

        // repeating the same (already applied) change is rejected instead of double-applying
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { id } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(0.21m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);
    }

    [Fact]
    public async Task Apply_rejects_unknown_ids_mixed_previews_and_expired_previews()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var (_, first) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var (_, second) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var firstId = IdOf(first!["changes"]!.AsArray()[0]!);
        var secondId = IdOf(second!["changes"]!.AsArray()[0]!);
        Assert.NotEqual(firstId, secondId);

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { "not-a-change-id" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(body!["error"]);

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { firstId, secondId } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(body!["error"]);

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { $"{firstId[..firstId.LastIndexOf(':')]}:9999" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { "01NOBODYKNOWS:0" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(body!["error"]);

        // an expired preview is refused with a clear message
        host.Service.SnapshotTtl = TimeSpan.FromMinutes(-1);
        var (_, expired) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var expiredId = IdOf(expired!["changes"]!.AsArray()[0]!);
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { expiredId } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("expired", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_rejects_stale_changes_and_writes_nothing_on_conflict()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var staleId = IdOf(Change(preview!, "deepseek-v4-flash", "field", field: "contextWindow"));
        var priceId = IdOf(Change(preview!, "deepseek-v4-flash", "price", priceKey: "siliconflow"));

        // another writer moves the model after the preview
        var model = await host.Db.Models.GetAsync("deepseek-v4-flash");
        model!.ContextWindow = 555;
        await host.Db.Models.UpdateAsync(model);

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { staleId, priceId } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.NotNull(body!["error"]);
        Assert.Contains(body["details"]!.AsArray(), d => d!["changeId"]!.GetValue<string>() == staleId);

        // atomic: the still-valid price change was not applied either
        Assert.Equal(555, (await host.Db.Models.GetAsync("deepseek-v4-flash"))!.ContextWindow);
        Assert.Equal(0.22m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);
    }

    [Fact]
    public async Task Apply_never_writes_user_owned_models_fields_or_prices()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        await CreateUserModelAsync(host);

        // (a) a manually submitted change id of a user-owned model
        var (_, first) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var userModelChange = IdOf(Change(first!, "my-model", "field", field: "pricing"));
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { userModelChange } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(3.0m, (await host.Db.Models.GetAsync("my-model"))!.Pricing!.Base["input"]);

        // (b) a user-modified field of a seed model
        var flash = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.NotNull(flash);
        flash!.DisplayName = "My Flash";
        flash.UserModifiedFields.Add("display_name");
        await host.Db.Models.UpdateAsync(flash);
        var (_, second) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var renamed = Change(second!, "deepseek-v4-flash", "field", field: "displayName");
        Assert.True(renamed["skippedBecauseUserModified"]!.GetValue<bool>());
        Assert.Equal("My Flash", renamed["before"]!.GetValue<string>());
        Assert.Equal("DeepSeek V4 Flash Updated", renamed["after"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { IdOf(renamed) } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("My Flash", (await host.Db.Models.GetAsync("deepseek-v4-flash"))!.DisplayName);

        // (c) a user-modified price row
        var siliconflowRow = await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow");
        Assert.NotNull(siliconflowRow);
        siliconflowRow!.UserModified = true;
        siliconflowRow.Pricing = new Core.Billing.PricingSchedule
        {
            Base = new() { ["input"] = 0.5m, ["output"] = 1.0m },
        };
        await host.Db.Models.UpsertPriceAsync(siliconflowRow);
        var (_, third) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var siliconflow = Change(third!, "deepseek-v4-flash", "price", priceKey: "siliconflow");
        Assert.True(siliconflow["skippedBecauseUserModified"]!.GetValue<bool>());
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { IdOf(siliconflow) } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(0.5m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);
    }

    [Fact]
    public async Task Upstream_failures_return_json_502_or_504_never_200_or_500()
    {
        var handler = new FakeCatalogHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        await using var host = await StartAsync(handler);

        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("boom", Encoding.UTF8, "text/plain") };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "500");

        handler.Respond = () => throw new HttpRequestException("dial tcp: connection refused");
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "connection refused");

        handler.Respond = () => throw new TaskCanceledException("timed out");
        await AssertPreviewError(host, HttpStatusCode.GatewayTimeout, "timed out");

        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("<html>not json</html>", Encoding.UTF8, "text/html") };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "not valid JSON");

        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "JSON object");

        // failed previews left no snapshot behind: apply only knows empty selections
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { "whatever:0" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(body!["error"]);
    }

    [Fact]
    public async Task Preview_keeps_at_most_eight_snapshots_and_refetches_upstream()
    {
        var handler = FakeCatalogHandler.Json(CatalogJson);
        await using var host = await StartAsync(handler);

        JsonNode? lastPreview = null;
        string firstId = "";
        for (var i = 1; i <= 9; i++)
        {
            var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
            var id = IdOf(preview!["changes"]!.AsArray()[0]!);
            if (i == 1) firstId = id;
            if (i == 9) lastPreview = preview;
        }
        Assert.Equal(9, handler.Requests.Count);

        // the oldest snapshot was evicted
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { firstId } });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // the newest one still applies
        var grokId = IdOf(Change(lastPreview!, "grok-9", "new_model"));
        (status, var applied) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { grokId } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, applied!["applied"]!.GetValue<int>());
        Assert.NotNull(await host.Db.Models.GetAsync("grok-9"));
    }

    [Fact]
    public async Task Capabilities_apply_round_trips_camelcase_api_and_snake_case_storage()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));

        // drift the seed model without marking anything user-modified
        var flash = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.NotNull(flash);
        flash!.Capabilities.PromptCache = false;
        flash.Capabilities.StructuredOutput = false;
        await host.Db.Models.UpdateAsync(flash);

        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var capabilities = Change(preview!, "deepseek-v4-flash", "field", field: "capabilities");
        Assert.False(capabilities["before"]!["promptCache"]!.GetValue<bool>());
        Assert.False(capabilities["before"]!["structuredOutput"]!.GetValue<bool>());
        Assert.True(capabilities["after"]!["promptCache"]!.GetValue<bool>());
        Assert.True(capabilities["after"]!["structuredOutput"]!.GetValue<bool>());

        var (status, applied) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply",
            new { changeIds = new[] { IdOf(capabilities) } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, applied!["applied"]!.GetValue<int>());

        var stored = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.NotNull(stored);
        Assert.Equal("sync", stored!.Source);
        Assert.True(stored.Capabilities.PromptCache == true);
        Assert.True(stored.Capabilities.StructuredOutput == true);
        Assert.True(stored.Capabilities.Reasoning == true); // the untouched capabilities survive the merge
        Assert.True(stored.Capabilities.Vision == true);
        Assert.True(stored.Capabilities.Tools == true);

        // storage JSON keeps the snake_case field names
        await using var conn = await host.Db.Factory.OpenAsync();
        var json = await conn.ExecuteScalarAsync<string>(
            "SELECT capabilities_json FROM models WHERE id = @id", new { id = "deepseek-v4-flash" });
        var raw = JsonNode.Parse(json!)!.AsObject();
        Assert.True(raw["prompt_cache"]!.GetValue<bool>());
        Assert.True(raw["structured_output"]!.GetValue<bool>());
        Assert.True(raw["reasoning"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Apply_recreates_a_deleted_thirdparty_price_with_the_catalog_upstream_id()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        Assert.True(await host.Db.Models.DeletePriceAsync("deepseek-v4-flash", "siliconflow"));

        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var siliconflow = Change(preview!, "deepseek-v4-flash", "price", priceKey: "siliconflow");
        Assert.Null(siliconflow["before"]); // the row is gone; sync offers to create it fresh
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash-0928", siliconflow["after"]!["upstreamModelId"]!.GetValue<string>());

        var (status, applied) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply",
            new { changeIds = new[] { IdOf(siliconflow) } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, applied!["applied"]!.GetValue<int>());

        var price = await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow");
        Assert.NotNull(price);
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash-0928", price!.UpstreamModelId); // the catalog id, not the deleted seed's -0731
        Assert.Equal(0.21m, price.Pricing.Base["input"]);
        Assert.Equal(0.015m, price.Pricing.Base["cache_read"]);
        Assert.Equal(0.7m, price.Pricing.Base["output"]);
        Assert.Equal("sync", price.Source);
        Assert.False(price.UserModified);
        Assert.Equal("seed", (await host.Db.Models.GetAsync("deepseek-v4-flash"))!.Source);

        // the recreated row is queryable through the real AstraApp pipeline
        var list = (await host.GetJsonAsync("/api/models?search=deepseek-v4-flash")).AsArray();
        var flash = list.Single(m => m!["id"]!.GetValue<string>() == "deepseek-v4-flash")!;
        Assert.Contains("siliconflow", flash["providerPriceKeys"]!.AsArray().Select(k => k!.GetValue<string>()));
        var detail = await host.GetJsonAsync("/api/models/deepseek-v4-flash");
        var apiPrice = detail["providerPrices"]!.AsArray().Single(p => p!["priceKey"]!.GetValue<string>() == "siliconflow")!;
        Assert.Equal("deepseek-ai/DeepSeek-V4-Flash-0928", apiPrice["upstreamModelId"]!.GetValue<string>());
        Assert.Equal(0.21m, apiPrice["pricing"]!["base"]!["input"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task Forced_seed_reimport_spares_sync_owned_fields_and_prices()
    {
        // (a) an applied model field: the sync-owned model is skipped entirely — no seed value and no
        // seed price is written under it.
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply",
            new { changeIds = new[] { IdOf(Change(preview!, "deepseek-v4-flash", "field", field: "displayName")) } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("DeepSeek V4 Flash Updated", (await host.Db.Models.GetAsync("deepseek-v4-flash"))!.DisplayName);

        var result = await host.Db.Seed.ImportAsync(force: true);
        Assert.False(result.Skipped);
        Assert.Equal(0, result.InsertedModels);
        Assert.Equal(1, result.SkippedModels); // deepseek-v4-flash is sync-owned now
        Assert.Equal(Core.Seed.SeedCatalog.Current.Models.Count - 1, result.UpdatedModels);
        Assert.Equal(Core.Seed.SeedCatalog.Current.Models.Where(m => m.Id != "deepseek-v4-flash").Sum(m => m.ProviderPricing.Count),
            result.UpsertedPrices); // none of the sync model's seed prices were re-upserted

        var flash = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.Equal("DeepSeek V4 Flash Updated", flash!.DisplayName); // the applied value survived the re-import
        Assert.Equal("sync", flash.Source);
        Assert.Equal(0.15m, flash.Pricing!.Base["input"]); // unselected fields stay seed-owned
        Assert.Equal(0.22m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);

        // (b) a fresh host: only the third-party price is applied (the model stays seed-owned); the
        // sync-owned price row survives a forced re-import untouched.
        await using var host2 = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var originalPrice = (await host2.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!;
        var (_, preview2) = await host2.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        (status, _) = await host2.SendAsync(HttpMethod.Post, "/api/models/sync/apply",
            new { changeIds = new[] { IdOf(Change(preview2!, "deepseek-v4-flash", "price", priceKey: "siliconflow")) } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("seed", (await host2.Db.Models.GetAsync("deepseek-v4-flash"))!.Source);

        var reimport = await host2.Db.Seed.ImportAsync(force: true);
        Assert.False(reimport.Skipped);
        Assert.Equal(1, reimport.SkippedPrices); // the sync-owned siliconflow row
        var price = await host2.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow");
        Assert.NotNull(price);
        Assert.Equal("sync", price!.Source);
        Assert.Equal(originalPrice.UpstreamModelId, price.UpstreamModelId);
        Assert.Equal(0.21m, price.Pricing.Base["input"]);
        Assert.Equal(0.015m, price.Pricing.Base["cache_read"]);
        Assert.Equal(0.7m, price.Pricing.Base["output"]);
        Assert.False(price.UserModified);
        Assert.Equal("seed", (await host2.Db.Models.GetAsync("deepseek-v4-flash"))!.Source);
    }

    [Fact]
    public async Task Concurrent_applies_of_one_new_model_change_land_exactly_once()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var grokId = IdOf(Change(preview!, "grok-9", "new_model"));

        var results = await Task.WhenAll(
            host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { grokId } }),
            host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { grokId } }));

        // exactly one winner and one conflict — never a 500
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict },
            results.Select(r => r.Status).OrderBy(s => s).ToArray());
        Assert.Equal(1, results.Single(r => r.Status == HttpStatusCode.OK).Body!["applied"]!.GetValue<int>());
        Assert.NotNull(results.Single(r => r.Status == HttpStatusCode.Conflict).Body!["error"]);

        // and the model plus its prices exist exactly once
        await using var conn = await host.Db.Factory.OpenAsync();
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM models WHERE id = 'grok-9'"));
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM model_prices WHERE model_id = 'grok-9'"));
        var grok = await host.Db.Models.GetAsync("grok-9");
        Assert.Equal("sync", grok!.Source);
        Assert.Equal(2.5m, (await host.Db.Models.GetPriceAsync("grok-9", "openrouter"))!.Pricing.Base["input"]);
    }

    [Fact]
    public async Task ApplySyncBatchAsync_stale_price_expectation_blocks_the_whole_batch()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var model = await host.Db.Models.GetAsync("deepseek-v4-flash");
        var price = await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow");
        Assert.NotNull(model);
        Assert.NotNull(price);

        // immutable pre-drift snapshots, cloned the same way the service builds its expectations
        var expectedModel = Json.Deserialize<Core.Models.SystemModel>(Json.Serialize(model))!;
        var expectedPrice = Json.Deserialize<Core.Models.ModelPrice>(Json.Serialize(price))!;

        // another writer flips the price's user flag after the snapshots were taken
        price!.UserModified = true;
        await host.Db.Models.UpsertPriceAsync(price); // UpdatedAt already set, so it stays

        var update = Json.Deserialize<Core.Models.SystemModel>(Json.Serialize(expectedModel))!;
        update.DisplayName = "Should Never Land";
        var staleWrite = Json.Deserialize<Core.Models.ModelPrice>(Json.Serialize(expectedPrice))!;
        staleWrite.UserModified = false;
        staleWrite.Pricing.Base["input"] = 9.99m;

        var applied = await host.Db.Models.ApplySyncBatchAsync(
            [], [update], [staleWrite],
            new Dictionary<string, Core.Models.SystemModel?> { [expectedModel.Id] = expectedModel },
            new Dictionary<(string ModelId, string PriceKey), Core.Models.ModelPrice?>
            {
                [(expectedPrice.ModelId, expectedPrice.PriceKey)] = expectedPrice,
            });
        Assert.False(applied); // the atomic stale guard rejects the whole batch

        var dbModel = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.Equal("DeepSeek V4 Flash", dbModel!.DisplayName);
        Assert.Equal(1000000, dbModel.ContextWindow);
        var dbPrice = await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow");
        Assert.NotNull(dbPrice);
        Assert.True(dbPrice!.UserModified);
        Assert.Equal("seed", dbPrice.Source);
        Assert.Equal(0.22m, dbPrice.Pricing.Base["input"]);
    }

    [Fact]
    public async Task Malformed_catalogs_fail_preview_with_502_and_write_nothing()
    {
        var handler = new FakeCatalogHandler(() => new HttpResponseMessage(HttpStatusCode.OK));
        await using var host = await StartAsync(handler);

        // a root without any official model collections
        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"error":"oops"}""", Encoding.UTF8, "application/json") };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "collections");

        // an official provider whose "models" member is not an object
        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"deepseek":{"models":[]}}""", Encoding.UTF8, "application/json") };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "collections");

        // a negative basic price — next to a valid model that must not be written either
        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "deepseek": { "models": { "deepseek-v4-flash": { "id": "deepseek-v4-flash", "name": "Updated",
                    "modalities": { "output": ["text"] }, "cost": { "input": 0.2, "output": 0.9 } } } },
                  "xai": { "models": { "grok-neg": { "id": "grok-neg",
                    "modalities": { "output": ["text"] }, "cost": { "input": -1, "output": 2 } } } }
                }
                """, Encoding.UTF8, "application/json"),
        };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "invalid base prices");

        // a model id containing a control character
        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"xai":{"models":{"bad\u0001id":{"id":"bad\u0001id",
                 "modalities":{"output":["text"]},"cost":{"input":1,"output":2}}}}}
                """, Encoding.UTF8, "application/json"),
        };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "invalid model ID");

        // non-positive token limits
        handler.Respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"xai":{"models":{"grok-limits":{"id":"grok-limits","modalities":{"output":["text"]},
                 "limit":{"context":0,"output":4096},"cost":{"input":1,"output":2}}}}}
                """, Encoding.UTF8, "application/json"),
        };
        await AssertPreviewError(host, HttpStatusCode.BadGateway, "invalid token limits");

        // none of the malformed previews touched the database
        var flash = await host.Db.Models.GetAsync("deepseek-v4-flash");
        Assert.Equal("seed", flash!.Source);
        Assert.Equal("DeepSeek V4 Flash", flash.DisplayName);
        Assert.Equal(0.15m, flash.Pricing!.Base["input"]);
        Assert.Equal(0.22m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);
        Assert.Null(await host.Db.Models.GetAsync("grok-9"));
    }

    [Fact]
    public async Task Apply_rejects_missing_null_and_malformed_change_ids_without_writing()
    {
        await using var host = await StartAsync(FakeCatalogHandler.Json(CatalogJson));
        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        var realId = IdOf(preview!["changes"]!.AsArray()[0]!);
        var snapshotId = realId[..realId.LastIndexOf(':')];

        var rejected = new List<(HttpStatusCode Status, JsonNode? Body)>
        {
            await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { }), // changeIds missing
            await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply"), // no request body at all
            await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = (string?)null }),
            await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new string?[] { null } }),
            await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { "   " } }),
            // same snapshot and index, but not the canonical id
            await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = new[] { $"{snapshotId}:00" } }),
        };
        Assert.All(rejected, r =>
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.Status);
            Assert.NotNull(r.Body!["error"]);
        });

        // an empty selection is a valid no-op
        var (status, empty) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/apply", new { changeIds = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, empty!["applied"]!.GetValue<int>());

        // nothing above touched the database
        Assert.Equal(0.22m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);
        Assert.Equal("DeepSeek V4 Flash", (await host.Db.Models.GetAsync("deepseek-v4-flash"))!.DisplayName);
        Assert.Null(await host.Db.Models.GetAsync("grok-9"));
    }

    [Fact]
    public async Task Sync_endpoints_require_the_admin_header_before_any_upstream_call()
    {
        var handler = FakeCatalogHandler.Json(CatalogJson);
        await using var host = await StartAsync(handler);
        using var anon = host.App.GetTestClient(); // like a foreign origin: no X-Astra-Admin default header

        var preview = await anon.PostAsync("/api/models/sync/preview",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.Contains("X-Astra-Admin",
            JsonNode.Parse(await preview.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());

        var apply = await anon.PostAsync("/api/models/sync/apply",
            new StringContent("""{"changeIds":["does-not-matter:0"]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, apply.StatusCode);

        // the real security pipeline stopped both requests before the sync service fetched anything
        Assert.Empty(handler.Requests);
        Assert.Equal(0.22m, (await host.Db.Models.GetPriceAsync("deepseek-v4-flash", "siliconflow"))!.Pricing.Base["input"]);
    }

    // ----- helpers -----

    private static async Task<SyncTestHost> StartAsync(FakeCatalogHandler handler)
    {
        var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ModelSyncService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler));
        return new SyncTestHost(host);
    }

    private static async Task CreateUserModelAsync(SyncTestHost host)
    {
        // source=user models (and their prices/fields) are protected from sync — written straight
        // through the repository, which is the same state the sync service re-reads on preflight.
        var now = DateTimeOffset.UtcNow;
        await host.Db.Models.InsertAsync(new Core.Models.SystemModel
        {
            Id = "my-model",
            DisplayName = "Mine",
            Vendor = "me",
            Source = "user",
            Pricing = new Core.Billing.PricingSchedule { Base = new() { ["input"] = 3.0m, ["output"] = 15.0m } },
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static JsonNode Change(JsonNode preview, string modelId, string kind, string? field = null, string? priceKey = null) =>
        preview["changes"]!.AsArray().Single(c =>
            c!["modelId"]!.GetValue<string>() == modelId
            && c["kind"]!.GetValue<string>() == kind
            && (field is null || c["field"]?.GetValue<string>() == field)
            && (priceKey is null || c["priceKey"]?.GetValue<string>() == priceKey))!;

    private static string IdOf(JsonNode change) => change["id"]!.GetValue<string>();

    private static async Task AssertPreviewError(SyncTestHost host, HttpStatusCode expected, string fragment)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/sync/preview");
        Assert.Equal(expected, status);
        Assert.NotNull(body!["error"]);
        Assert.Contains(fragment, body["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SyncTestHost(TestHost host) : IAsyncDisposable
    {
        public WebApplication App => host.App;
        public AstraDatabase Db => host.Db;
        public HttpClient Client => host.Client;
        public string Root => host.Root;
        public ModelSyncService Service => host.App.Services.GetRequiredService<ModelSyncService>();
        public Task<JsonNode> GetJsonAsync(string url) => host.GetJsonAsync(url);
        public Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string url, object? body = null) => host.SendAsync(method, url, body);
        public ValueTask DisposeAsync() => host.DisposeAsync();
    }

    internal sealed class FakeCatalogHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Respond { get; set; } = respond;
        public List<string> Requests { get; } = [];

        public static FakeCatalogHandler Json(string content) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? "");
            try
            {
                return Task.FromResult(Respond());
            }
            catch (Exception ex)
            {
                return Task.FromException<HttpResponseMessage>(ex);
            }
        }
    }
}

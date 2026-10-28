using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.ProviderDetail;
using Astra.Desktop.Tests.Plugins;

namespace Astra.Desktop.Tests.ProviderDetail;

/// <summary>Shared builders of provider-detail DTOs from the wire JSON (the shapes the server sends).</summary>
internal static class PdData
{
    public static ProviderDto Provider(Action<JsonObject>? change = null)
    {
        var o = new JsonObject
        {
            ["id"] = "prov_openai", ["templateId"] = "openai", ["templateVersion"] = 1, ["templateUpdateAvailable"] = false, ["name"] = "OpenAI", ["icon"] = "openai",
            ["category"] = "official",
            ["endpoints"] = new JsonArray(
                new JsonObject { ["protocol"] = "openai-chat", ["baseUrl"] = "https://api.openai.com/v1", ["fullUrl"] = false },
                new JsonObject { ["protocol"] = "openai-responses", ["baseUrl"] = "https://api.openai.com/v1", ["fullUrl"] = false }),
            ["preferredUpstreamProtocols"] = new JsonArray("openai-responses", "openai-chat"),
            ["authScheme"] = "bearer", ["hasApiKey"] = true, ["apiKeyMasked"] = "sk-…a1b2", ["extraHeaders"] = new JsonObject { ["X-A"] = "1" }, ["httpProxy"] = null,
            ["priceMultiplier"] = 1, ["priceKey"] = "openai", ["adapterId"] = "openai", ["settings"] = new JsonObject(), ["enabled"] = true, ["sortOrder"] = 0,
            ["notes"] = null, ["website"] = null, ["modelCount"] = 2, ["boundClients"] = new JsonArray("codex"),
            ["createdAt"] = "2026-09-16T12:00:00Z", ["updatedAt"] = "2026-10-24T12:00:00Z",
            ["quotaConfig"] = new JsonObject
            {
                ["enabled"] = false, ["template"] = null, ["suggestedTemplate"] = "anthropic", ["effectiveTemplate"] = null, ["intervalMinutes"] = null,
                ["effectiveIntervalMinutes"] = 30, ["timeoutSec"] = 15, ["baseUrl"] = null, ["params"] = new JsonObject(), ["secrets"] = new JsonArray("accessToken"),
                ["request"] = null, ["extract"] = null,
            },
            ["quota"] = null, ["quotaCheckedAtUtc"] = null,
        };
        change?.Invoke(o);
        return JsonSerializer.Deserialize(o, JsonContexts.Info<ProviderDto>(Json.Api))!;
    }

    public static ProviderModelDto Model(long id, string modelId, bool enabled = true, int sort = 0, Action<JsonObject>? change = null)
    {
        var o = new JsonObject
        {
            ["id"] = id, ["providerId"] = "prov_openai", ["modelId"] = modelId, ["systemModelId"] = modelId, ["enabled"] = enabled, ["sortOrder"] = sort,
            ["overrides"] = new JsonObject(),
            ["effective"] = new JsonObject { ["displayName"] = modelId.ToUpperInvariant(), ["contextWindow"] = 128000, ["maxOutputTokens"] = 8192, ["capabilities"] = new JsonObject() },
            ["origins"] = new JsonObject { ["displayName"] = "inherited", ["contextWindow"] = "overridden", ["maxOutputTokens"] = "unset" },
            ["pricingSource"] = "provider_price", ["priceKey"] = "openai", ["multiplier"] = 1,
            ["effectivePricing"] = new JsonObject { ["currency"] = "USD", ["unit"] = "per_1m_tokens", ["base"] = new JsonObject { ["input"] = 2, ["output"] = 8 } },
        };
        change?.Invoke(o);
        return JsonSerializer.Deserialize(o, JsonContexts.Info<ProviderModelDto>(Json.Api))!;
    }
}

[Collection("Loc")]
public sealed class ProviderSettingsFormTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public ProviderSettingsFormTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    [Fact]
    public void A_fresh_form_is_clean_and_patches_nothing()
    {
        var f = ProviderSettingsForm.From(PdData.Provider());
        Assert.False(f.IsDirty(f));
        Assert.Empty(f.Patch(f));
        Assert.Equal(["openai-responses", "openai-chat"], f.Preferred);
        Assert.Equal("price:openai", f.PriceKeyChoice);
    }

    [Fact]
    public void The_patch_holds_only_changed_fields_and_a_typed_key()
    {
        var saved = ProviderSettingsForm.From(PdData.Provider());
        var edited = (saved with { Name = "Team", ApiKey = "sk-new" }).WithHeader(0, "X-A", "2");
        Assert.True(edited.IsDirty(saved));
        var patch = edited.Patch(saved);
        Assert.Equal(["name", "extraHeaders", "apiKey"], patch.Select(kv => kv.Key).ToArray());
        Assert.Equal("{\"X-A\":\"2\"}", patch["extraHeaders"]!.ToJsonString());
    }

    [Fact]
    public void Headers_with_a_blank_name_are_not_sent_and_adding_replaces_case_insensitively()
    {
        var saved = ProviderSettingsForm.From(PdData.Provider());
        var blank = saved with { Headers = [.. saved.Headers, ("  ", "x")] };
        Assert.False(blank.IsDirty(saved));
        var added = saved.AddHeader("x-a", "9");
        Assert.Equal([("x-a", "9")], added.Headers);
    }

    [Fact]
    public void Preferred_keeps_pick_order_and_reports_protocols_without_an_endpoint()
    {
        Assert.Equal(["openai-responses", "openai-chat", "gemini"], ProviderSettingsForm.ReorderPreferred(["openai-responses", "openai-chat"], ["openai-chat", "openai-responses", "gemini"]));
        Assert.Equal(["openai-chat"], ProviderSettingsForm.ReorderPreferred(["openai-responses", "openai-chat"], ["openai-chat"]));
        Assert.Equal("2. OpenAI Chat", ProviderSettingsForm.ChipLabel(["openai-responses", "openai-chat"], "openai-chat"));
        Assert.Equal("Gemini", ProviderSettingsForm.ChipLabel(["openai-chat"], "gemini"));
        var f = ProviderSettingsForm.From(PdData.Provider()) with { Preferred = ["gemini", "openai-chat"] };
        Assert.Equal(["gemini"], f.PreferredMissing);
    }

    [Theory]
    [InlineData("inherit", null)]
    [InlineData("price:", "")]
    [InlineData("price:deepseek", "deepseek")]
    public void Price_key_choices_map_to_the_wire(string choice, string? key) => Assert.Equal(key, ProviderSettingsForm.PriceKeyOf(choice));

    [Theory]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData(" http://localhost:11434 ", true)]
    [InlineData("ftp://x", false)]
    [InlineData("https://has space", false)]
    public void Endpoint_addresses_must_be_http(string url, bool ok) => Assert.Equal(ok, ProviderSettingsForm.ValidBaseUrl(url));

    [Theory]
    [InlineData("X-Custom-Header", true)]
    [InlineData("bad header", false)]
    [InlineData("", false)]
    public void Header_names_are_letters_digits_dashes(string name, bool ok) => Assert.Equal(ok, ProviderSettingsForm.ValidHeaderName(name));
}

[Collection("Loc")]
public sealed class ProviderModelDraftTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public ProviderModelDraftTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    [Fact]
    public void A_fresh_draft_is_clean_and_reads_origins_like_the_web()
    {
        var d = ProviderModelDraft.From(PdData.Model(1, "gpt-4.1"));
        Assert.False(d.IsDirty);
        Assert.False(d.CustomPrice);
        Assert.Equal(FieldOrigin.Inherited, d.OriginOf("displayName"));
        Assert.Equal(FieldOrigin.Inherited, d.OriginOf("contextWindow")); // a server "overridden" with no draft value reads as inherited
        Assert.Equal(FieldOrigin.Unset, d.OriginOf("maxOutputTokens"));
        Assert.Equal(FieldOrigin.Overridden, d.With("maxOutputTokens", JsonValue.Create(10)).OriginOf("maxOutputTokens"));
    }

    [Fact]
    public void Custom_price_starts_from_the_effective_price_and_off_sends_null()
    {
        var d = ProviderModelDraft.From(PdData.Model(1, "gpt-4.1")).WithCustomPrice(true);
        Assert.True(d.IsDirty);
        Assert.Equal(2, d.Effective()["pricing"]!["base"]!["input"]!.GetValue<int>());
        var off = d.WithCustomPrice(false);
        Assert.False(off.IsDirty);
        Assert.Null(off.Body()["overrides"]!["pricing"]);
    }

    [Fact]
    public void Custom_price_without_an_effective_price_starts_empty_usd()
    {
        var d = ProviderModelDraft.From(PdData.Model(1, "x", change: o => o["effectivePricing"] = null)).WithCustomPrice(true);
        Assert.Equal("{\"currency\":\"USD\",\"unit\":\"per_1m_tokens\",\"base\":{\"input\":null,\"output\":null}}", d.Get("pricing")!.ToJsonString());
    }

    [Fact]
    public void The_body_carries_the_link_and_an_empty_link_unlinks()
    {
        var d = ProviderModelDraft.From(PdData.Model(1, "gpt-4.1")) with { Link = "" };
        Assert.True(d.IsDirty);
        var body = d.Body();
        Assert.Null(body["systemModelId"]);
        Assert.True(body.ContainsKey("systemModelId"));
        Assert.Equal("{\"pricing\":null}", body["overrides"]!.ToJsonString());
    }
}

[Collection("Loc")]
public sealed class QuotaFormTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public QuotaFormTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    // The wire shape GET /api/provider-quota/templates sends (Contracts QuotaTemplateDto, camelCase).
    private static readonly List<QuotaTemplateDto> Templates = Astra.Core.Json.DeserializeApi<List<QuotaTemplateDto>>(JsonNode.Parse("""
        [{"id":"anthropic","name":"Anthropic","kind":"balance","appliesTo":[],"hosts":[],"params":[],"request":{"method":"GET","url":"{{origin}}/v1/usage","auth":"provider","headers":{"x-a":"1"}},"extract":{"remaining":"balance"}},
         {"id":"newapi","name":"New API","kind":"balance","appliesTo":["newapi"],"hosts":[],"params":[{"name":"userId","secret":false,"required":true},{"name":"accessToken","secret":true,"required":false}],"request":{"method":"POST","url":"{{origin}}/api","auth":"none","headers":{}},"extract":{}}]
        """))!;

    [Fact]
    public void Templates_read_from_the_contracts_dto()
    {
        Assert.Equal(2, Templates.Count);
        Assert.Equal("1", Templates[0].Request.Headers["x-a"]);
        Assert.True(Templates[1].Params[1].Secret);
        Assert.Equal("newapi", Templates[1].AppliesTo[0]);
    }

    [Fact]
    public void A_saved_config_becomes_an_auto_form()
    {
        var f = QuotaForm.From(PdData.Provider().QuotaConfig);
        Assert.Equal(QuotaForm.Auto, f.Template);
        Assert.Equal(15, f.TimeoutSec);
        Assert.Equal("{}", f.Headers);
        Assert.True(f.Same(QuotaForm.From(PdData.Provider().QuotaConfig)));
        Assert.Equal("Anthropic", f.Chosen(Templates, "anthropic")!.Name);
    }

    [Fact]
    public void Switching_to_custom_starts_from_the_template_in_effect()
    {
        var f = QuotaForm.From(PdData.Provider().QuotaConfig).WithTemplate(QuotaForm.Custom, Templates, "anthropic");
        Assert.Equal("{{origin}}/v1/usage", f.Url);
        Assert.Contains("\"remaining\": \"balance\"", f.Extract);
        Assert.Equal(QuotaForm.CustomParams, f.ParamsFor(Templates, "anthropic"));
        var generic = QuotaForm.From(PdData.Provider().QuotaConfig).WithTemplate(QuotaForm.Custom, [], null);
        Assert.Equal("{{baseUrl}}/user/balance", generic.Url);
        Assert.Contains("$const", generic.Extract);
    }

    [Fact]
    public void The_input_trims_params_drops_empty_secrets_and_checks_custom_json()
    {
        var f = QuotaForm.From(PdData.Provider().QuotaConfig).WithTemplate("newapi", Templates, null).WithParam("userId", " 42 ").WithSecret("accessToken", " ");
        var body = f.ToInput(out var error)!;
        Assert.Null(error);
        Assert.Equal("newapi", body["template"]!.GetValue<string>());
        Assert.Equal("{\"userId\":\"42\"}", body["params"]!.ToJsonString());
        Assert.Equal("{}", body["secrets"]!.ToJsonString());
        Assert.False(body.ContainsKey("request"));

        var bad = f.WithTemplate(QuotaForm.Custom, Templates, null) with { Url = "x", Headers = "[1]" };
        Assert.Null(bad.ToInput(out error));
        Assert.Contains("Headers", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Template_options_name_the_suggestion_and_end_with_custom()
    {
        var options = QuotaForm.TemplateOptions(Templates, "anthropic");
        Assert.Equal(QuotaForm.Auto, options[0].Value);
        Assert.Contains("Anthropic", options[0].Label);
        Assert.Equal(QuotaForm.Custom, options[^1].Value);
        Assert.Equal(4, options.Count);
    }
}

[Collection("Loc")]
public sealed class ProviderDetailLogicTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public ProviderDetailLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    [Fact]
    public void Model_ids_split_on_whitespace_and_commas_without_duplicates()
        => Assert.Equal(["a", "b", "c"], ProviderDetailLogic.ParseModelIds(" a\nb, c ,a\t"));

    [Fact]
    public void The_model_map_keeps_string_entries_in_order()
    {
        var settings = JsonNode.Parse("{\"model_map\":{\"x\":\"y\",\"\":\"z\",\"n\":1,\"e\":\"\",\"a\":\"b\"}}") as JsonObject;
        var map = ProviderDetailLogic.ModelMapOf(settings);
        Assert.Equal(["x", "a"], map.Select(kv => kv.Key));
        Assert.Equal(["x", "a", "new"], ProviderDetailLogic.WithMapping(map, "new", "t").Keys);
        Assert.Equal(["a"], ProviderDetailLogic.WithoutMapping(map, "x").Keys);
        Assert.Empty(ProviderDetailLogic.ModelMapOf(new JsonObject { ["model_map"] = new JsonArray() }));
    }

    [Fact]
    public void A_409_names_the_bound_clients()
    {
        var e = new ApiException(409, "bound", JsonNode.Parse("{\"boundClients\":[\"codex\",\"claude-code\"]}"));
        var text = ProviderDetailLogic.DeleteError(e);
        Assert.Contains("Codex", text);
        Assert.Equal("boom", ProviderDetailLogic.DeleteError(new ApiException(500, "boom")));
    }

    [Fact]
    public void Model_search_matches_id_or_display_name()
    {
        var list = new[] { PdData.Model(1, "gpt-4.1"), PdData.Model(2, "o4-mini", change: o => o["effective"]!["displayName"] = "Reasoner") };
        Assert.Single(ProviderDetailLogic.FilterModels(list, "reason"));
        Assert.Equal(2, ProviderDetailLogic.FilterModels(list, "").Count);
    }

    [Fact]
    public void Subtitle_and_tabs_follow_the_provider()
    {
        Assert.Equal("openai · " + Loc.T("providers.priceKeyShort", ("key", "openai")), ProviderDetailLogic.Subtitle(PdData.Provider()));
        Assert.Equal(Loc.T("providers.tab.apiKey"), ProviderDetailLogic.AccountsTabLabel(PdData.Provider()));
        Assert.Equal(Loc.T("providers.tab.accounts"), ProviderDetailLogic.AccountsTabLabel(PdData.Provider(o => o["authScheme"] = "plugin")));
        Assert.False(TemplateSwitchLogic.CanSwitch(PdData.Provider(o => o["authScheme"] = "oauth-subscription")));
        Assert.True(TemplateSwitchLogic.OffersAlternatives(PdData.Provider(), 401));
        Assert.False(TemplateSwitchLogic.OffersAlternatives(PdData.Provider(), 500));
        Assert.True(ProviderDetailLogic.UpToDate(new TemplateUpdateDto(3, 3, [])));
        Assert.False(ProviderDetailLogic.UpToDate(new TemplateUpdateDto(1, 3, [])));
    }

    [Fact]
    public void Relative_times_follow_intl_numeric_auto()
    {
        var now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
        Assert.Equal("in 3 hours", QuotaTimes.ResetsIn("2026-10-26T15:00:00Z", now, "en"));
        Assert.Equal("3小时后", QuotaTimes.ResetsIn("2026-10-26T15:00:00Z", now, "zh"));
        Assert.Equal("后天", QuotaTimes.ResetsIn("2026-10-28T12:00:00Z", now, "zh"));
        Assert.Equal("in 1 minute", QuotaTimes.ResetsIn("2026-10-26T12:00:10Z", now, "en"));
        Assert.Null(QuotaTimes.ResetsIn("2026-10-26T11:00:00Z", now, "en"));
        Assert.Equal("just now", QuotaTimes.UpdatedAgo(now.AddSeconds(-30), now, "en", "just now"));
        Assert.Equal("12 minutes ago", QuotaTimes.UpdatedAgo(now.AddMinutes(-12), now, "en", "x"));
        Assert.Equal("2小时前", QuotaTimes.UpdatedAgo(now.AddHours(-2), now, "zh", "x"));
    }
}

public sealed class ProviderTestSessionTests
{
    private static SseMessage M(string ev, string data) => new(ev, data);

    [Fact]
    public void Events_fold_into_the_run()
    {
        var run = new TestRun("m", RunStatus.Running)
            .Apply(M("headers", "{\"httpStatus\":200,\"httpMs\":100,\"contentType\":\"text/event-stream\",\"headers\":{\"a\":\"1\",\"n\":2,\"z\":null}}"))
            .Apply(M("delta", "{\"kind\":\"reasoning\",\"text\":\"think\"}"))
            .Apply(M("delta", "{\"kind\":\"text\",\"text\":\"O\"}"))
            .Apply(M("delta", "{\"text\":\"K\"}"))
            .Apply(M("other", "{}"));
        Assert.Equal("OK", run.Text);
        Assert.Equal("think", run.Reasoning);
        Assert.Equal(200, run.HttpStatus);
        Assert.Equal([new("a", "1"), new KeyValuePair<string, string>("n", "2")], run.Headers);
        Assert.False(run.IsDone);
        var done = run.Apply(M("done", "{\"ok\":true,\"httpStatus\":200,\"httpMs\":100,\"ttftMs\":250,\"totalMs\":300,\"text\":\"OK!\",\"raw\":\"r\"}"));
        Assert.Equal(RunStatus.Success, done.Status);
        Assert.Equal("OK!", done.Text);
        Assert.Equal(150, done.FirstWait);
        Assert.Equal(RunStatus.Failed, run.Apply(M("done", "{\"ok\":false,\"error\":\"401\"}")).Status);
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(412.4, "412 ms")]
    [InlineData(9999.6, "10000 ms")]
    [InlineData(12345.0, "12.3 s")]
    public void FormatMs_matches_the_web(double? ms, string expected) => Assert.Equal(expected, ProviderTestSession.FormatMs(ms));

    private static async IAsyncEnumerable<SseMessage> Answer(JsonObject request, [EnumeratorCancellation] CancellationToken ct, int delayMs = 0, bool ok = true)
    {
        yield return M("headers", "{\"httpStatus\":200,\"httpMs\":10}");
        if (delayMs > 0) await Task.Delay(delayMs, ct);
        yield return M("done", $"{{\"ok\":{(ok ? "true" : "false")},\"text\":\"{request["modelId"]}\",\"totalMs\":20}}");
    }

    [Fact]
    public async Task A_single_run_streams_to_success_and_reports_after_run()
    {
        var afterRuns = 0;
        var s = new ProviderTestSession(PdData.Provider(), (r, ct) => Answer(r, ct), () => afterRuns++);
        Assert.Equal("openai-responses", s.Protocol);
        s.SetModels([PdData.Model(1, "b", enabled: false), PdData.Model(2, "a", sort: 5)]);
        Assert.Equal("a", s.Model);
        Assert.Equal(["a", "b"], s.Known);
        await s.StartSingleAsync();
        Assert.Equal(RunStatus.Success, s.Single!.Status);
        Assert.Equal("a", s.Single.Text);
        Assert.False(s.Busy);
        Assert.True(afterRuns >= 1);
    }

    [Fact]
    public void The_request_uses_the_configuration()
    {
        var s = new ProviderTestSession(PdData.Provider(), (r, ct) => Answer(r, ct)) { Prompt = "  ", Stream = false };
        s.SetMaxTokens(99999);
        Assert.Equal(ProviderTestSession.MaxTokensLimit, s.MaxTokens);
        s.SetMaxTokens(null);
        var body = s.RequestFor("m");
        Assert.Equal("{\"modelId\":\"m\",\"protocol\":\"openai-responses\",\"stream\":false,\"prompt\":null,\"maxOutputTokens\":256}", body.ToJsonString());
    }

    [Fact]
    public async Task A_batch_runs_three_at_a_time_and_stop_cancels_the_rest()
    {
        var inFlight = 0;
        var peak = 0;
        var gate = new TaskCompletionSource();
        async IAsyncEnumerable<SseMessage> Slow(JsonObject r, [EnumeratorCancellation] CancellationToken ct)
        {
            var n = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, n);
            yield return M("headers", "{\"httpStatus\":200}");
            await gate.Task.WaitAsync(ct);
            Interlocked.Decrement(ref inFlight);
            yield return M("done", "{\"ok\":true}");
        }
        var s = new ProviderTestSession(PdData.Provider(), Slow);
        var ids = Enumerable.Range(1, 6).Select(i => "m" + i).ToList();
        var task = s.StartBatchAsync(ids);
        await WaitUntil(() => inFlight == 3);
        Assert.True(s.Busy);
        s.StopBatch();
        gate.SetResult();
        await task;
        Assert.Equal(3, peak);
        Assert.Equal(3, s.Succeeded);
        Assert.Equal(3, ids.Count(id => s.Batch[id].Status == RunStatus.Cancelled));
        Assert.Equal(6, s.Done);
        Assert.False(s.Busy);
    }

    [Fact]
    public async Task Reset_cancels_in_flight_runs_and_clears_results()
    {
        var started = new TaskCompletionSource();
        async IAsyncEnumerable<SseMessage> Hang(JsonObject r, [EnumeratorCancellation] CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            yield break;
        }
        var s = new ProviderTestSession(PdData.Provider(), Hang) { Model = "m" };
        var run = s.StartSingleAsync();
        await started.Task;
        s.Toggle("x", true);
        s.Reset();
        await run;
        Assert.Null(s.Single);
        Assert.Empty(s.Selected);
        Assert.False(s.Busy);
    }

    [Fact]
    public void Batch_list_helpers_follow_the_web()
    {
        var s = new ProviderTestSession(PdData.Provider(), (r, ct) => Answer(r, ct));
        s.SetModels([PdData.Model(1, "gpt-4.1"), PdData.Model(2, "o4-mini")]);
        Assert.Equal(false, s.AllSelected);
        s.Toggle("gpt-4.1", true);
        Assert.Null(s.AllSelected);
        s.Search = "GPT-4.1";
        Assert.False(s.CanAdd);
        s.Search = "custom-x";
        Assert.True(s.CanAdd);
        s.AddTyped("custom-x");
        Assert.Equal("", s.Search);
        Assert.Equal(["gpt-4.1", "o4-mini", "custom-x"], s.Known);
        s.SelectAll(true);
        Assert.Equal(true, s.AllSelected);
        s.Search = "mini";
        Assert.Equal(["o4-mini"], s.Visible);
        Assert.Equal("providers.testDialog.status.idle", ProviderTestSession.StatusKey(null));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

public sealed class ProviderDetailApiTests
{
    [Fact]
    public async Task Writes_use_the_web_routes_and_bodies()
    {
        var provider = JsonSerializer.Serialize(PdData.Provider(), JsonContexts.Info<ProviderDto>(Json.Api));
        var http = new StubHttp()
            .On("PUT /api/providers/prov_openai/model-map", provider)
            .On("POST /api/providers/prov_openai/models", "[]")
            .On("PATCH /api/providers/prov_openai/models/7", JsonSerializer.Serialize(PdData.Model(7, "x"), JsonContexts.Info<ProviderModelDto>(Json.Api)))
            .On("POST /api/providers/prov_openai/template-switch", provider)
            .On("DELETE /api/providers/prov_openai", "", 204);
        using var api = new ApiClient("http://x", http);
        using var queries = new QueryClient();
        var pd = new ProviderDetailApi(queries, api, "prov_openai");
        await pd.SetModelMapAsync(new Dictionary<string, string> { ["a"] = "b" });
        await pd.AddModelsAsync(["m1", "m2"]);
        await pd.UpdateModelAsync(7, new JsonObject { ["enabled"] = false });
        await pd.SwitchTemplateAsync("zhipu-coding", null);
        await pd.DeleteAsync();
        Assert.Equal("{\"map\":{\"a\":\"b\"}}", http.Requests[0].Body);
        Assert.Equal("{\"modelIds\":[\"m1\",\"m2\"]}", http.Requests[1].Body);
        Assert.Equal("{\"enabled\":false}", http.Requests[2].Body);
        Assert.Equal("{\"templateId\":\"zhipu-coding\"}", http.Requests[3].Body);
        Assert.All(http.Requests, r => Assert.Equal("1", r.Admin));
        Assert.False(pd.RemoteModels.Enabled);
        Assert.False(pd.TemplateUpdate.Enabled);
    }

    [Fact]
    public void Every_key_is_registered_in_the_catalog()
    {
        string[] keys =
        [
            "providers.tab.models", "providers.tab.accounts", "providers.tab.apiKey", "providers.tab.settings", "providers.searchModels", "providers.addModels",
            "providers.fetchRemote", "providers.noModels", "providers.noModelsDetail", "providers.modelsFooter", "providers.unlinked", "providers.modelMap.title",
            "providers.modelMap.footer", "providers.modelMap.add", "providers.modelMap.addDetail", "providers.modelMap.from", "providers.modelMap.to",
            "providers.modelMap.toPlaceholder", "providers.modelMap.needModels", "providers.modelMap.missingTarget", "providers.modelMap.remove", "providers.modelMap.saved",
            "providers.addModelsDetail", "providers.modelIds", "providers.addSelected", "providers.modelsAdded", "providers.fetchRemoteDetail", "providers.linksTo",
            "providers.noLink", "providers.added", "origin.overridden", "origin.inherited", "origin.unset", "origin.reset", "providers.removeModelShort",
            "providers.removeModel", "providers.linkedModel", "providers.linkedModelHint", "providers.overrides", "providers.price", "providers.customPrice",
            "providers.customPriceHint", "providers.inheritedPriceHint", "pricingSource.none", "providers.section.auth", "providers.apiKeyStored", "providers.apiKey",
            "providers.noKey", "providers.replaceKey", "providers.usage.title", "providers.usage.error", "providers.usage.hint", "providers.usage.dayHint",
            "providers.section.basic", "providers.name", "providers.notes", "providers.authScheme", "providers.section.endpoints", "providers.endpointsFooter",
            "providers.baseUrl", "providers.fullUrl", "providers.addEndpoint", "providers.preferred", "providers.preferredHint", "providers.preferredMissing",
            "providers.section.billing", "providers.billingFooter", "providers.priceKey", "providers.priceKeyHint", "providers.priceKeyDefault", "providers.priceKeyNone",
            "providers.multiplier", "providers.multiplierHint", "providers.section.advanced", "providers.proxy", "providers.proxyHint", "providers.headers",
            "providers.headerName", "providers.headerValue", "providers.addHeader", "providers.adapterSettings", "providers.section.info", "providers.adapter",
            "providers.boundClients", "common.unsaved", "common.revert", "providers.addEndpointDetail", "providers.protocol", "providers.fullUrlHint", "providers.baseUrlHint",
            "providers.addHeaderDetail", "common.invalidJson", "providers.test", "providers.duplicate", "providers.templateUpdate", "providers.templateSwitch",
            "providers.deleteConfirm", "providers.deleteDetail", "providers.deleted", "providers.deleteBound", "providers.templateVersions", "providers.templateApplied",
            "providers.templateUpToDate", "providers.priceKeyShort", "providers.templateSwitchDetail", "providers.templateSwitchTarget", "providers.templateSwitchCurrent",
            "providers.variant", "providers.variantDefault", "providers.templateSwitchEndpoints", "providers.templateSwitched", "providers.keyAlternative.title",
            "providers.keyAlternative.rejected", "providers.keyAlternative.switch", "providers.templateSwitchConfirm", "providers.templateSwitchConfirmDetail",
            "providers.quota.title", "providers.quota.footer", "providers.quota.enable", "providers.quota.enableHint", "providers.quota.template",
            "providers.quota.templateAuto", "providers.quota.templateNone", "providers.quota.custom", "providers.quota.secretStored", "providers.quota.paramsHint",
            "providers.quota.method", "providers.quota.url", "providers.quota.auth", "providers.quota.auth.provider", "providers.quota.auth.none", "providers.quota.urlHint",
            "providers.quota.headers", "providers.quota.extract", "providers.quota.extractHint", "providers.quota.interval", "providers.quota.intervalHint",
            "providers.quota.intervalEvery", "providers.quota.intervalOff", "providers.quota.timeout", "providers.quota.baseUrl", "providers.quota.baseUrlHint",
            "providers.quota.test", "providers.quota.testDesc", "providers.quota.fetch", "providers.quota.testOk", "providers.quota.testFailed",
            "providers.quota.requestUrl", "providers.quota.httpStatus", "providers.quota.extracted", "providers.quota.rawResponse", "providers.quota.never",
            "providers.quota.failed", "providers.quota.lastGood", "providers.quota.invalid", "providers.quota.fetchedAt", "providers.quota.resets",
            "providers.quota.invalidJson", "tray.justNow", "providers.testDialog.title", "providers.testDialog.subtitle", "providers.testDialog.realRequest",
            "providers.testDialog.stop", "providers.testDialog.retest", "providers.testDialog.start", "providers.testDialog.stopRest", "providers.testDialog.testN",
            "providers.testDialog.modeSingle", "providers.testDialog.modeBatch", "providers.testDialog.config", "providers.testDialog.model",
            "providers.testDialog.savedCredentials", "providers.testDialog.progress", "providers.testDialog.progressDone", "providers.testDialog.modelPlaceholder",
            "providers.testDialog.showModels", "providers.testDialog.noModels", "providers.testDialog.protocol", "providers.testDialog.stream",
            "providers.testDialog.maxTokens", "providers.testDialog.prompt", "providers.testDialog.promptHint", "providers.testDialog.metricsHelp",
            "providers.testDialog.metrics.http", "providers.testDialog.metrics.firstWait", "providers.testDialog.metrics.ttft", "providers.testDialog.metrics.total",
            "providers.testDialog.reply", "providers.testDialog.preview", "providers.testDialog.raw", "providers.testDialog.headers", "providers.testDialog.headersEmpty",
            "providers.testDialog.rawNote", "providers.testDialog.rawTruncated", "providers.testDialog.reasoning", "providers.testDialog.noText",
            "providers.testDialog.emptyTitle", "providers.testDialog.emptyDetail", "providers.testDialog.selected", "providers.testDialog.searchModels",
            "providers.testDialog.clearSelection", "providers.testDialog.ownFilter", "providers.testDialog.addModel", "providers.testDialog.statusColumn",
            "providers.testDialog.view", "providers.testDialog.status.idle", "providers.testDialog.status.queued", "providers.testDialog.status.running",
            "providers.testDialog.status.success", "providers.testDialog.status.failed", "providers.testDialog.status.cancelled", "common.copied",
        ];
        Assert.Equal("", string.Join(", ", keys.Where(k => !Loc.Has(k))));
    }
}

using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Models;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Tests.Providers;

[Collection("Loc")]
public sealed class ProviderLogicTests : IDisposable
{
    private readonly string _saved = Loc.Language;

    public ProviderLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    [Theory]
    [InlineData(1234L, false, "1,234")]
    [InlineData(9999L, true, "9,999")]
    [InlineData(12345L, true, "12.3K")]
    [InlineData(8_100_000L, true, "8.1M")]
    [InlineData(999_950L, true, "1M")]
    public void FormatTokens_matches_the_web(long n, bool compact, string expected) => Assert.Equal(expected, ProviderFormat.FormatTokens(n, compact));

    [Theory]
    [InlineData("0", "$0")]
    [InlineData("12.3456", "$12.35")]
    [InlineData("0.021349", "$0.0213")]
    [InlineData("0.009", "$0.009")]
    public void FormatUsd_keeps_three_significant_digits_below_a_dollar(string usd, string expected)
        => Assert.Equal(expected, ProviderFormat.FormatUsd(decimal.Parse(usd, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void HostOf_returns_host_or_the_text()
    {
        Assert.Equal("api.openai.com", ProviderFormat.HostOf("https://api.openai.com/v1"));
        Assert.Equal("localhost:11434", ProviderFormat.HostOf("http://localhost:11434/v1"));
        Assert.Equal("not a url", ProviderFormat.HostOf("not a url"));
        Assert.Null(ProviderFormat.HostOf(null));
    }

    [Fact]
    public void LastDays_is_oldest_first()
    {
        var days = ProviderFormat.LastDays(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), 7);
        Assert.Equal(7, days.Count);
        Assert.True(string.CompareOrdinal(days[0], days[6]) < 0);
    }

    [Fact]
    public void Usage_buckets_by_provider_name_and_ignores_other_days()
    {
        string[] days = ["2026-10-24", "2026-10-25", "2026-10-26"];
        var map = ProviderUsageBuckets.Build(
        [
            new TimeseriesPointDto("2026-10-25", "Anthropic", 1.5m, 4, 10),
            new TimeseriesPointDto("2026-10-26", "Anthropic", 0.5m, 6, 10),
            new TimeseriesPointDto("2026-10-01", "Anthropic", 9m, 99, 10),
            new TimeseriesPointDto("2026-10-26", "OpenAI", 1m, 1, 1),
        ], days);
        Assert.Equal([0, 4, 6], map["Anthropic"].Requests);
        Assert.Equal(2m, map["Anthropic"].CostUsd);
        Assert.Equal(10, map["Anthropic"].Total);
        Assert.Equal(1, map["OpenAI"].Total);
    }

    private static ProviderDto Provider(string auth = "bearer", bool hasKey = true, bool enabled = true, string? priceKey = "openai", decimal mult = 1, bool update = false, string[]? clients = null) => new(
        "p1", "openai", 1, update, "OpenAI", "openai", "official",
        [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://api.openai.com/v1" }, new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = "https://api.openai.com/v1" }],
        [ApiProtocol.OpenAIChat], auth, hasKey, hasKey ? "sk-…a1b2" : null, [], null, mult, priceKey, null, [], enabled, 0, null, null, 4,
        clients ?? [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
        new ProviderQuotaConfigDto(false, null, null, null, null, 30, 15, null, [], [], null, null), null, null);

    [Fact]
    public void Card_shows_credential_state_and_price()
    {
        var card = ProviderCardModel.From(Provider(mult: 1.5m), null);
        Assert.Equal("OpenAI Chat / OpenAI Responses", card.Description);
        Assert.Equal("sk-…a1b2", card.Credential);
        Assert.True(card.CredentialMono);
        Assert.Equal(CardState.Active, card.State);
        Assert.Equal("api.openai.com", card.Host);
        Assert.Equal(Loc.T("providers.priceKeyShort", ("key", "openai")) + " · ×1.5", card.Pricing);
    }

    [Fact]
    public void Card_states_follow_the_web()
    {
        Assert.Equal(CardState.Disabled, ProviderCardModel.From(Provider(enabled: false), null).State);
        var missing = ProviderCardModel.From(Provider(hasKey: false), null);
        Assert.Equal(CardState.NoKey, missing.State);
        Assert.True(missing.MissingKey);
        Assert.Equal(Loc.T("providers.noKey"), missing.Credential);
        Assert.False(ProviderCardModel.From(Provider(auth: "none", hasKey: false), null).MissingKey);
        Assert.Equal(Loc.T("providers.card.subscription"), ProviderCardModel.From(Provider(auth: "oauth-subscription", hasKey: false), null).Credential);
        Assert.Equal(Loc.T("providers.card.plugin"), ProviderCardModel.From(Provider(auth: "plugin", hasKey: false), null).Credential);
        Assert.Null(ProviderCardModel.From(Provider(priceKey: null), null).Pricing);
    }

    [Fact]
    public void Card_collapses_bound_clients_into_a_count()
    {
        string[] many = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];
        var card = ProviderCardModel.From(Provider(clients: many), null);
        Assert.Equal(8, card.VisibleClients.Count);
        Assert.Equal(2, card.OverflowClients.Count);
        var withUpdate = ProviderCardModel.From(Provider(clients: many, update: true), null);
        Assert.Equal(5, withUpdate.VisibleClients.Count);
    }

    [Fact]
    public void Providers_decode_from_the_shared_list_json()
    {
        var json = JsonNode.Parse("""
            [{"id":"x","templateId":null,"templateVersion":null,"templateUpdateAvailable":false,"name":"X","icon":null,"category":"custom",
              "endpoints":[{"protocol":"anthropic","baseUrl":"https://h/v1"}],"preferredUpstreamProtocols":["anthropic"],"authScheme":"x-api-key",
              "hasApiKey":false,"apiKeyMasked":null,"extraHeaders":{},"httpProxy":null,"priceMultiplier":1,"priceKey":null,"adapterId":null,
              "settings":{},"enabled":true,"sortOrder":0,"notes":null,"website":null,"modelCount":0,"boundClients":[],
              "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
              "quotaConfig":{"enabled":false,"template":null,"suggestedTemplate":null,"effectiveTemplate":null,"intervalMinutes":null,
              "effectiveIntervalMinutes":30,"timeoutSec":15,"baseUrl":null,"params":{},"secrets":[],"request":null,"extract":null},
              "quota":null,"quotaCheckedAtUtc":null}]
            """)!.AsArray();
        var list = ProviderQueries.ParseProviders(json);
        Assert.Single(list);
        Assert.Equal("anthropic", list[0].Endpoints[0].Protocol.ToId());
    }

    [Fact]
    public void Quota_snapshot_reads_the_plan_label_and_the_invalid_message()
    {
        var snap = QuotaSnapshot.Parse(JsonNode.Parse("""
            {"fetchedAtUtc":"2026-10-26T11:48:00Z","isValid":false,"invalidMessage":"key disabled","planLabel":"Pro","plans":[]}
            """)!.AsObject())!;
        Assert.Equal("Pro", snap.PlanLabel);
        Assert.Equal("key disabled", snap.InvalidMessage);
        Assert.False(snap.IsValid);
    }

    [Fact]
    public void Quota_summary_and_failure()
    {
        var snap = QuotaSnapshot.Parse(JsonNode.Parse("""
            {"fetchedAtUtc":"2026-10-26T11:48:00Z","isValid":true,"plans":[{"unit":"USD","remaining":42.18,"total":100,"used":57.82,"usedPercent":57.82}],
             "error":null,"errorCode":null,"errorAtUtc":null}
            """)!.AsObject());
        Assert.Contains("$42.18", QuotaText.Summary(snap));
        Assert.Contains("58", QuotaText.Summary(snap));
        Assert.Null(QuotaText.Failure(snap));

        var old = new QuotaSnapshot(DateTimeOffset.Parse("2026-10-26T12:00:00Z"), true, [], "boom", "timeout", DateTimeOffset.Parse("2026-10-26T11:00:00Z"));
        Assert.Null(QuotaText.Failure(old)); // the error is older than the last good fetch: already recovered
        var fresh = old with { ErrorAt = DateTimeOffset.Parse("2026-10-26T12:30:00Z") };
        Assert.Equal(Loc.T("providers.quota.error.timeout"), QuotaText.FailureText(QuotaText.Failure(fresh)!.Value));
        Assert.Null(QuotaText.Summary(null));
        Assert.Equal(Loc.T("providers.quota.invalid"), QuotaText.Summary(new QuotaSnapshot(null, false, [], null, null, null)));
    }

    [Fact]
    public void Quota_amount_formats_currency_and_credits()
    {
        Assert.Equal("$0.0042", QuotaText.FormatAmount(0.0042, "USD"));
        Assert.Equal("1,200 credits", QuotaText.FormatAmount(1200, "credits"));
        Loc.Language = "zh";
        Assert.Equal("US$42.18", QuotaText.FormatAmount(42.18, "USD"));
        Assert.Equal("5小时", QuotaText.WindowLength(300));
        Assert.Equal("7天", QuotaText.WindowLength(10080));
    }

    private const string TemplatesJson = """
        [{"id":"openai","name":"OpenAI","category":"official","icon":"openai","apiKeyUrl":"https://k","endpoints":[{"protocol":"openai-chat","baseUrl":"https://api.openai.com/v1"},{"protocol":"openai-responses","baseUrl":"https://api.openai.com/v1"}],
          "preferredUpstreamProtocols":["openai-responses","openai-chat"],"authScheme":"bearer","requiresApiKey":true,"models":["a","b"],"priceKey":"openai","keyAlternatives":["plan"]},
         {"id":"plan","name":"Claude 订阅（Pro/Max）","category":"subscription","endpoints":[],"preferredUpstreamProtocols":[],"authScheme":"oauth-subscription","requiresApiKey":false,"models":[]},
         {"id":"v","name":"Var","category":"cn","endpoints":[{"protocol":"anthropic","baseUrl":"https://v/a"}],"preferredUpstreamProtocols":["anthropic"],"authScheme":"x-api-key","requiresApiKey":true,"models":[],
          "variants":[{"id":"intl","name":"Intl","priceKey":"v-intl","endpointsOverride":[{"protocol":"openai-chat","baseUrl":"https://v/i"}]}],"defaultHeaders":{"x-a":"1"},"defaultPriceMultiplier":0.5}]
        """;

    private static List<ProviderTemplateInfo> Templates() => ProviderTemplates.Parse(JsonNode.Parse(TemplatesJson)!.AsArray());

    [Fact]
    public void Templates_parse_and_group_in_category_order()
    {
        var all = Templates();
        Assert.Equal(3, all.Count);
        Assert.True(all[1].IsSubscription);
        Assert.Equal(["official", "subscription", "cn"], ProviderTemplates.Group(all, "").Select(g => g.Category));
        Assert.Equal(["official"], ProviderTemplates.Group(all, " OPEN ").Select(g => g.Category));
        Assert.Equal(("Claude 订阅", "Pro/Max"), ProviderTemplates.SplitPlan("Claude 订阅（Pro/Max）"));
        Assert.Equal(("Plain", null), ProviderTemplates.SplitPlan("Plain"));
    }

    [Fact]
    public void Wizard_pick_template_copies_its_defaults()
    {
        var w = new AddProviderWizard();
        w.Pick(Templates()[0]);
        Assert.True(w.IsDetails);
        Assert.Equal(1, w.StepIndex);
        Assert.Equal("OpenAI", w.Name);
        Assert.Equal("openai", w.PriceKey);
        Assert.Equal(["openai-responses", "openai-chat"], w.Preferred);
        Assert.Equal(2, w.Endpoints.Count);
        Assert.False(w.CanSubmit); // needs a key
        w.ApiKey = " sk-1 ";
        Assert.True(w.CanSubmit);
        Assert.True(w.ShowTemplateModelsHint);
    }

    [Fact]
    public void Wizard_custom_needs_name_and_http_endpoints()
    {
        var w = new AddProviderWizard();
        w.PickCustom();
        Assert.False(w.CanSubmit);
        w.Name = "Mine";
        Assert.False(w.CanSubmit); // empty base url
        w.SetEndpoints([new TemplateEndpoint("openai-chat", "ftp://x")]);
        Assert.False(w.CanSubmit);
        w.SetEndpoints([new TemplateEndpoint("openai-chat", " https://x/v1 ")]);
        Assert.True(w.CanSubmit);
    }

    [Fact]
    public void Wizard_subscription_template_sends_only_identity()
    {
        var w = new AddProviderWizard();
        w.Pick(Templates()[1]);
        Assert.True(w.Accounts);
        Assert.True(w.CanSubmit);
        var body = w.BuildRequest();
        Assert.Equal("plan", (string?)body["templateId"]);
        Assert.False(body.ContainsKey("endpoints"));
        Assert.False(body.ContainsKey("authScheme"));
    }

    [Fact]
    public void Wizard_request_carries_the_more_options()
    {
        var w = new AddProviderWizard();
        w.Pick(Templates()[0]);
        w.ApiKey = "  sk-1 ";
        w.Notes = "  note ";
        w.Proxy = "http://127.0.0.1:7890";
        w.Multiplier = 1.25m;
        w.Models = "m1, m2\nm3，m4";
        w.Headers = [("X-A", "1"), ("  ", "dropped")];
        w.SetEndpoints([new TemplateEndpoint("openai-chat", " https://x/v1 ", true)]);
        var body = w.BuildRequest();
        Assert.Equal("openai", (string?)body["templateId"]);
        Assert.Equal("sk-1", (string?)body["apiKey"]);
        Assert.Null(body["variantId"]);
        Assert.Equal("https://x/v1", (string?)body["endpoints"]![0]!["baseUrl"]);
        Assert.True((bool)body["endpoints"]![0]!["fullUrl"]!);
        Assert.Equal(["openai-chat"], body["preferredUpstreamProtocols"]!.AsArray().Select(x => (string?)x));
        Assert.Equal("1", (string?)body["extraHeaders"]!["X-A"]);
        Assert.Single(body["extraHeaders"]!.AsObject());
        Assert.Equal(["m1", "m2", "m3", "m4"], body["models"]!.AsArray().Select(x => (string?)x));
        Assert.Equal("note", (string?)body["notes"]);
        Assert.Equal(1.25m, (decimal)body["priceMultiplier"]!);
        Assert.False(w.ShowTemplateModelsHint);
    }

    [Fact]
    public void Wizard_variant_switches_endpoints_and_price_key()
    {
        var w = new AddProviderWizard();
        w.Pick(Templates()[2]);
        Assert.Equal(0.5m, w.Multiplier);
        Assert.Equal([("x-a", "1")], w.Headers);
        w.ChooseVariant("intl");
        Assert.Equal("v-intl", w.PriceKey);
        Assert.Equal("openai-chat", w.Endpoints[0].Protocol);
        Assert.Equal(["openai-chat"], w.Preferred); // anthropic dropped out, the new protocol joined
        w.ChooseVariant("");
        Assert.Equal("v", w.PriceKey);
        Assert.Equal("anthropic", w.Endpoints[0].Protocol);
    }

    [Fact]
    public void FollowEndpoints_keeps_priority_and_appends_new_protocols()
    {
        TemplateEndpoint[] before = [new("openai-chat", "a"), new("anthropic", "b")];
        TemplateEndpoint[] after = [new("openai-chat", "a"), new("gemini", "c")];
        Assert.Equal(["openai-chat", "gemini"], AddProviderWizard.FollowEndpoints(["anthropic", "openai-chat"], before, after));
    }

    [Fact]
    public void Wizard_preferred_order_is_the_order_picked()
    {
        var w = new AddProviderWizard();
        w.Pick(Templates()[0]);
        w.SetPreferred(["openai-chat"]);
        w.SetPreferred(["openai-chat", "openai-responses"]);
        Assert.Equal(["openai-chat", "openai-responses"], w.Preferred);
    }

    [Fact]
    public void Wizard_alternatives_resolve_against_the_catalog()
    {
        var all = Templates();
        var w = new AddProviderWizard();
        w.Pick(all[0]);
        Assert.Equal(["plan"], w.Alternatives(all).Select(t => t.Id));
        w.Pick(all[1]);
        Assert.Empty(w.Alternatives(all));
    }

    [Fact]
    public void Wizard_proceed_respects_single_instance_subscriptions_and_plugins()
    {
        var all = Templates();
        var w = new AddProviderWizard { Choice = "plan" };
        Assert.Null(w.Proceed(all, new HashSet<string> { "plan" }, []));
        Assert.False(w.IsDetails);
        Assert.Null(w.Proceed(all, new HashSet<string>(), []));
        Assert.True(w.IsDetails);
        w.Back();
        Assert.False(w.IsDetails);
        w.Choice = ProviderTemplates.PluginChoice + "pp";
        Assert.Equal("pp", w.Proceed(all, [], [Provider() with { Id = "pp" }]));
        w.Choice = ProviderTemplates.CustomChoice;
        w.Proceed(all, [], []);
        Assert.True(w.IsCustom);
    }

    [Fact]
    public void Plugin_providers_skip_absent_plugins_and_filter()
    {
        var present = Provider(auth: "plugin") with { Id = "plugin-a", Name = "Alpha" };
        var absent = present with { Id = "plugin-b", Name = "Beta", Settings = new JsonObject { ["plugin"] = new JsonObject { ["absent"] = true } } };
        var list = ProviderTemplates.PluginProviders([present, absent, Provider()], "");
        Assert.Equal(["plugin-a"], list.Select(p => p.Id));
        Assert.Empty(ProviderTemplates.PluginProviders([present], "zzz"));
        Assert.Equal(["tpl"], ProviderTemplates.AddedSubscriptionTemplateIds([Provider(auth: "oauth-subscription") with { TemplateId = "tpl" }, Provider()]));
    }

    private static ImportCandidateDto Candidate(string status, string name = "N", string? skip = null, ImportExistingDto? existing = null) => new(
        "ref-" + name, "src", name, "App", [new ImportEndpointDto(ApiProtocol.Anthropic, "https://api.example.com/v1")], "bearer", true, "sk-…1", "fp", [], [], null,
        status, existing, false, skip, []);

    [Fact]
    public void Import_selects_only_new_entries_and_never_same_or_skip()
    {
        var src = new ImportSourceDto("src", "Src", "/p", true, null, [Candidate("new", "A"), Candidate("same", "B"), Candidate("sameHost", "C"), Candidate("skip", "D")]);
        var sel = ImportLogic.DefaultSelection([src]);
        Assert.Equal([ImportLogic.Key(src.Items[0])], sel);
        sel.Add(ImportLogic.Key(src.Items[1]));
        sel.Add(ImportLogic.Key(src.Items[3]));
        sel.Add(ImportLogic.Key(src.Items[2]));
        Assert.Equal(["A", "C"], ImportLogic.Chosen([src], sel).Select(c => c.Name));
        Assert.False(ImportLogic.Selectable(src.Items[1]));
        Assert.True(ImportLogic.Selectable(src.Items[2]));
    }

    [Fact]
    public void Import_search_matches_name_or_address_and_hints_explain()
    {
        var c = Candidate("same", "Alpha", existing: new ImportExistingDto("p", "Existing"));
        Assert.True(ImportLogic.Matches(c, "alp"));
        Assert.True(ImportLogic.Matches(c, "EXAMPLE.com"));
        Assert.False(ImportLogic.Matches(c, "zzz"));
        Assert.Equal(Loc.T("import.existing", ("name", "Existing")), ImportLogic.Hint(c));
        Assert.Equal(Loc.T("import.sameHostHint", ("name", "Existing")), ImportLogic.Hint(c with { Status = "sameHost" }));
        Assert.Null(ImportLogic.Hint(Candidate("new")));
        Assert.Equal("Anthropic · api.example.com", ImportLogic.ProtocolsLine(c));
        Assert.Equal(ImportTone.Red, ImportLogic.ToneOf("skip"));
        Assert.Equal(ImportTone.Neutral, ImportLogic.ToneOf("off"));
    }

    [Fact]
    public void Import_done_text_mentions_skips()
    {
        var one = new ImportedProviderDto("a", "A");
        Assert.Equal(Loc.T("import.done", ("count", 1)), ImportLogic.DoneText(new ImportResultDto([one], [])));
        Assert.Equal(Loc.T("import.doneSkipped", ("count", 1), ("skipped", 1)), ImportLogic.DoneText(new ImportResultDto([one], [new ImportSkippedDto("r", "x")])));
    }

    [Fact]
    public void Every_catalog_key_the_page_uses_exists()
    {
        foreach (var key in ProvidersKeys.All) Assert.True(Loc.Has(key), key);
    }
}

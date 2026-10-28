using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Alerts;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Settings;

public class FormsTests
{
    internal static SettingsDto Settings(string proxyMode = "system", string? proxyUrl = null, string? user = null, bool hasPassword = false, string? bypass = null,
        string? market = null, string? zone = null, int? retention = 90)
        => new("zh", false, 7, retention, new EffortBudgets { Low = 2048, Medium = 8192, High = 24576 }, 120, "stable", true, 17321, "127.0.0.1", "/d",
            "http://127.0.0.1:17321", 30, 30, proxyMode, proxyUrl, user, hasPassword, bypass, true, market, "https://astra-gate.si/api/plugin-market", zone, "UTC", "default");

    [Fact]
    public void Json_patch_serializes_like_the_api_expects_with_explicit_nulls()
    {
        var patch = JsonPatch.Of(("requestRetentionDays", null), ("debugBodies", true), ("proxyUrl", "http://x:1"), ("n", 5));
        var json = JsonSerializer.Serialize(patch, JsonContexts.Info<JsonObject>(Json.Api));
        Assert.Equal("""{"requestRetentionDays":null,"debugBodies":true,"proxyUrl":"http://x:1","n":5}""", json);
    }

    [Fact]
    public void Proxy_custom_waits_for_an_address_before_it_is_saved()
    {
        var s = Settings();
        var form = new ProxyForm(s);
        Assert.Null(form.ChangeMode("custom", s));
        Assert.Equal("custom", form.Mode);
        Assert.False(form.CanSave(s, false));
        form.Url = " http://127.0.0.1:7890 ";
        Assert.True(form.CanSave(s, false));
        Assert.False(form.CanSave(s, true));
        var body = form.SaveBody();
        Assert.Equal("custom", (string?)body["proxyMode"]);
        Assert.Equal("http://127.0.0.1:7890", (string?)body["proxyUrl"]);
        Assert.Null(body["proxyUsername"]);
        Assert.False(body.ContainsKey("proxyPassword"));
        form.Password = "secret";
        Assert.Equal("secret", (string?)form.SaveBody()["proxyPassword"]);
    }

    [Fact]
    public void Proxy_system_and_direct_apply_at_once_and_custom_with_an_address_too()
    {
        var saved = Settings(proxyMode: "custom", proxyUrl: "http://p:1");
        var form = new ProxyForm(saved);
        Assert.Equal("direct", (string?)form.ChangeMode("direct", saved)!["proxyMode"]);
        Assert.Equal("custom", (string?)form.ChangeMode("custom", saved)!["proxyMode"]);
    }

    [Fact]
    public void Proxy_form_resyncs_only_the_fields_that_changed_on_the_server()
    {
        var a = Settings(proxyMode: "custom", proxyUrl: "http://p:1");
        var form = new ProxyForm(a) { Password = "typed", Bypass = "typed.lan" };
        form.Sync(a, a); // nothing changed: typing survives
        Assert.Equal("typed", form.Password);
        form.Sync(a, Settings(proxyMode: "custom", proxyUrl: "http://q:2"));
        Assert.Equal("http://q:2", form.Url);
        Assert.Equal("", form.Password);
        Assert.Equal("", form.Bypass);
    }

    [Fact]
    public void Market_switch_keeps_to_local_copies_reset_goes_back_to_the_site()
    {
        Assert.Equal("", (string?)MarketForm.OfflineBody(true)["pluginMarketUrl"]);
        Assert.Null(MarketForm.OfflineBody(false)["pluginMarketUrl"]);
        Assert.True(MarketForm.Offline(Settings(market: "")));
        Assert.False(MarketForm.Offline(Settings()));
        var s = Settings(market: "https://m.example/api");
        var form = new MarketForm(s);
        Assert.False(form.CanSave(s, false));
        form.Url = "https://other.example/api ";
        Assert.True(form.CanSave(s, false));
        Assert.Equal("https://other.example/api", (string?)form.SaveBody()["pluginMarketUrl"]);
        form.Url = " ";
        Assert.Null(form.SaveBody()["pluginMarketUrl"]);
        Assert.True(MarketForm.CanReset(s));
        Assert.False(MarketForm.CanReset(Settings()));
    }

    [Fact]
    public void Time_zone_options_add_the_saved_zone_once()
    {
        Assert.Equal(GeneralLogic.UsageTimeZones.Length, GeneralLogic.ZoneOptions(Settings()).Count);
        Assert.Contains("Africa/Cairo", GeneralLogic.ZoneOptions(Settings(zone: "Africa/Cairo")));
        Assert.Equal(GeneralLogic.UsageTimeZones.Length, GeneralLogic.ZoneOptions(Settings(zone: "UTC")).Count);
    }

    [Fact]
    public void Effort_patch_sends_all_three_levels_and_keeps_an_emptied_one()
    {
        var b = new EffortBudgets { Low = 1, Medium = 2, High = 3 };
        var patch = GeneralLogic.EffortPatch(b, "medium", 5000);
        var o = patch["effortBudgets"]!.AsObject();
        Assert.Equal((1, 5000, 3), ((int)o["low"]!, (int)o["medium"]!, (int)o["high"]!));
        Assert.Equal(2, (int)GeneralLogic.EffortPatch(b, "medium", null)["effortBudgets"]!["medium"]!);
    }

    [Fact]
    public void Retention_switch_and_remote_url()
    {
        Assert.Equal(90, GeneralLogic.RetentionFor(true));
        Assert.Null(GeneralLogic.RetentionFor(false));
        var t = TunnelLogicTests.Status(state: "connected", publicUrl: "https://a.trycloudflare.com");
        Assert.Equal("https://a.trycloudflare.com/v1", GeneralLogic.RemoteUrl(t));
        Assert.Null(GeneralLogic.RemoteUrl(TunnelLogicTests.Status(publicUrl: "https://a.trycloudflare.com")));
        Assert.Equal("Brotli (br)", GeneralLogic.CompressionLabel("br"));
    }

    [Fact]
    public void Tray_prefs_edit_one_block_or_the_title_without_touching_the_rest()
    {
        var prefs = TrayPrefs.Default;
        var next = TrayLogic.WithSection(prefs, TraySection.CompareYesterday, true);
        Assert.True(TrayLogic.IsOn(next, TraySection.CompareYesterday));
        Assert.False(TrayLogic.IsOn(prefs, TraySection.CompareYesterday));
        Assert.Equal(TrayLogic.IsOn(prefs, TraySection.Quota), TrayLogic.IsOn(next, TraySection.Quota));
        Assert.Equal(TrayTitleMode.Cost, TrayLogic.WithTitle(prefs, TrayTitleMode.Cost).Title);
        Assert.Equal("settings.tray.section.topModel.hint", TrayLogic.SectionHint(TraySection.TopModel).Key);
        Assert.Equal("settings.tray.title.alerts", TrayLogic.TitleLabel(TrayTitleMode.Alerts).Key);
        Assert.DoesNotContain(TraySection.Clients, TrayLogic.UsageSections);
        foreach (var s in Enum.GetValues<TraySection>())
        {
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(TrayLogic.SectionLabel(s).Key), TrayLogic.SectionLabel(s).Key);
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(TrayLogic.SectionHint(s).Key));
        }
        foreach (var m in TrayLogic.TitleModes)
        {
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(TrayLogic.TitleLabel(m).Key));
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(TrayLogic.TitleHint(m).Key));
        }
    }
}

public class WebhookDraftTests
{
    private static WebhookDto Hook(string format = "generic", bool hasSecret = true, params string[] headers)
        => new("w1", "Ops", "https://h.example/x", format, true, 10, true, hasSecret, [.. headers], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void A_new_webhook_starts_generic_signed_enabled_with_a_ten_second_timeout()
    {
        var d = new WebhookDraft(null);
        Assert.True(d.IsNew && d.Signed && d.Enabled && d.UseProxy);
        Assert.Equal("generic", d.Format);
        Assert.Equal(10, d.TimeoutSec);
        Assert.False(d.CanSave);
        d.Name = "Ops";
        d.Url = "https://x";
        Assert.True(d.CanSave);
        Assert.True(d.SignedVisible);
        Assert.False(d.BotSecretVisible);
    }

    [Fact]
    public void Create_body_for_generic_sends_signed_and_null_headers_when_none()
    {
        var d = new WebhookDraft(null) { Name = " Ops ", Url = " https://x " };
        var body = d.CreateBody();
        Assert.Equal("Ops", (string?)body["name"]);
        Assert.Equal("generic", (string?)body["format"]);
        Assert.True((bool)body["signed"]!);
        Assert.True(body.ContainsKey("headers") && body["headers"] is null);
        Assert.False(body.ContainsKey("secret"));
        d.Signed = false;
        Assert.False((bool)d.CreateBody()["signed"]!);
    }

    [Fact]
    public void Bot_formats_send_a_typed_secret_clear_it_when_unsigned_and_keep_it_when_empty()
    {
        var d = new WebhookDraft(null) { Name = "n", Url = "u" };
        d.ChangeFormat("feishu");
        Assert.True(d.Signed && d.BotSecretVisible);
        Assert.False(d.CreateBody().ContainsKey("secret")); // nothing typed
        d.BotSecret = " SECabc ";
        Assert.Equal("SECabc", (string?)d.CreateBody()["secret"]);
        Assert.False(d.CreateBody().ContainsKey("signed"));
        d.Signed = false;
        Assert.Equal("", (string?)d.CreateBody()["secret"]);
    }

    [Fact]
    public void Update_body_only_carries_what_changed_about_format_and_signing()
    {
        var d = new WebhookDraft(Hook());
        var body = d.UpdateBody();
        Assert.False(body.ContainsKey("format"));
        Assert.False(body.ContainsKey("signed")); // still signed
        d.Signed = false;
        Assert.False((bool)d.UpdateBody()["signed"]!);
        d.ChangeFormat("slack");
        var changed = d.UpdateBody();
        Assert.Equal("slack", (string?)changed["format"]);
        Assert.False(changed.ContainsKey("signed"));
        Assert.Throws<InvalidOperationException>(() => new WebhookDraft(null).UpdateBody());
    }

    [Fact]
    public void Stored_headers_are_kept_by_name_and_new_ones_added()
    {
        var d = new WebhookDraft(Hook(headers: "X-Token"));
        Assert.Single(d.Headers);
        Assert.True(d.Headers[0].Stored);
        d.Headers.Add(new HeaderDraft("X-New", "v", false));
        d.Headers.Add(new HeaderDraft("  ", "ignored", false));
        var headers = d.UpdateBody()["headers"]!.AsObject();
        Assert.Equal(["X-Token", "X-New"], headers.Select(kv => kv.Key));
        Assert.Equal("", (string?)headers["X-Token"]);
    }

    [Fact]
    public void Format_change_resets_the_signed_switch_by_the_web_rule()
    {
        var d = new WebhookDraft(Hook("feishu", hasSecret: true));
        d.ChangeFormat("generic");
        Assert.True(d.Signed);
        d.ChangeFormat("feishu");
        Assert.True(d.Signed && d.BotSecretKept is not null);
        d.ChangeFormat("dingtalk");
        Assert.True(d.Signed && d.BotSecretKept is null);
        d.ChangeFormat("wecom");
        Assert.False(d.Signed);
        Assert.False(d.SignedVisible);
    }

    [Fact]
    public void Badges_flag_disabled_other_formats_and_unsigned()
    {
        Assert.Empty(WebhookLogic.Badges(Hook()));
        var badges = WebhookLogic.Badges(Hook("feishu", hasSecret: false) with { Enabled = false });
        Assert.Equal(["orange", "accent", "neutral"], badges.Select(b => b.Tone));
        Assert.Equal(["accent"], WebhookLogic.Badges(Hook("slack", hasSecret: false)).Select(b => b.Tone));
    }

    [Fact]
    public void Test_toast_and_delivery_text()
    {
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        WebhookDeliveryDto D(string status, int? http = null, string? error = null, DateTimeOffset? next = null)
            => new("d1", "w1", null, "test", status, 2, next, http, error, null, null, t, null);
        Assert.False(WebhookLogic.TestToast(D("success", 200)).IsError);
        Assert.True(WebhookLogic.TestToast(D("failed", 500, "boom")).IsError);
        Assert.Equal("green", WebhookLogic.StatusTone("success"));
        Assert.Equal("orange", WebhookLogic.StatusTone("pending"));
        Assert.Equal("red", WebhookLogic.StatusTone("failed"));
        Assert.Equal("HTTP 500 boom", WebhookLogic.Outcome(D("failed", 500, "boom")));
        Assert.Equal("HTTP 200", WebhookLogic.Outcome(D("success", 200, "ignored")));
        Assert.Equal("alerts.delivery.next", WebhookLogic.Attempts(D("pending", next: t), "en").Key);
        Assert.Equal("alerts.delivery.attempts", WebhookLogic.Attempts(D("failed"), "en").Key);
        Assert.Equal("alerts.webhooks.test", WebhookLogic.EventName("test").Key);
    }
}

public class AlertLogicTests
{
    private static readonly AlertEventDto Balance = new("provider.balance.low", "provider",
        [new("metric", "choice", ["remaining", "usedPercent"], null, false), new("value", "number", null, null, true), new("plan", "text", null, null, false)]);
    private static readonly AlertEventDto Quota = new("account.quota.high", "account", [new("value", "number", null, 90, false)]);

    private static WebhookDto Hook(string id) => new(id, id, "u", "generic", true, 10, true, false, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Defaults_take_the_first_choice_the_default_number_and_empty_text()
    {
        var d = AlertLogic.Defaults(Balance);
        Assert.Equal("remaining", d["metric"]);
        Assert.Null(d["value"]);
        Assert.Equal("", d["plan"]);
        Assert.Equal(90m, AlertLogic.Defaults(Quota)["value"]);
    }

    [Fact]
    public void A_new_rule_starts_on_the_first_event_with_every_webhook_and_a_one_hour_cooldown()
    {
        var draft = new RuleDraft(null, [Balance, Quota], [Hook("a"), Hook("b")]);
        Assert.Equal(Balance.Id, draft.EventId);
        Assert.Equal(["a", "b"], draft.WebhookIds);
        Assert.Equal(60m, draft.Cooldown);
        Assert.False(draft.OnlySelected);
        Assert.False(draft.CanSave);
        draft.Name = "Low balance";
        Assert.True(draft.CanSave);
    }

    [Fact]
    public void Body_drops_empty_conditions_floors_minutes_and_nulls_all_subjects()
    {
        var draft = new RuleDraft(null, [Balance, Quota], [Hook("a")]) { Name = " R ", Repeat = 15.7m, Cooldown = -3 };
        draft.Condition["value"] = 12.5m;
        draft.Condition["plan"] = "";
        var body = draft.Body();
        Assert.Equal("R", (string?)body["name"]);
        var cond = body["condition"]!.AsObject();
        Assert.Equal(["metric", "value"], cond.Select(kv => kv.Key));
        Assert.Equal(12.5m, (decimal)cond["value"]!);
        Assert.Equal(15L, (long)body["repeatMinutes"]!);
        Assert.Equal(0L, (long)body["cooldownMinutes"]!);
        Assert.Null(body["subjects"]);
        draft.OnlySelected = true;
        draft.Subjects.Add("p1");
        Assert.Equal(["p1"], draft.Body()["subjects"]!.AsArray().Select(n => (string?)n));
        draft.Repeat = 0;
        Assert.Null(draft.Body()["repeatMinutes"]);
    }

    [Fact]
    public void Integral_numbers_are_sent_as_integers()
    {
        var o = AlertLogic.ConditionBody(new Dictionary<string, object?> { ["value"] = 90m });
        Assert.Equal("""{"value":90}""", o.ToJsonString());
    }

    [Fact]
    public void Editing_loads_the_saved_condition_over_the_defaults_and_changing_the_event_resets_it()
    {
        var rule = new AlertRuleDto("r1", "R", true, Quota.Id, ["acc1"], new JsonObject { ["value"] = 75 }, true, 30, 10, ["a"], 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var draft = new RuleDraft(rule, [Balance, Quota], [Hook("a"), Hook("b")]);
        Assert.Equal(75m, draft.Condition["value"]);
        Assert.True(draft.OnlySelected);
        Assert.Equal(["acc1"], draft.Subjects);
        Assert.Equal(["a"], draft.WebhookIds);
        Assert.Equal(30m, draft.Repeat);
        draft.ChangeEvent(Balance.Id, [Balance, Quota]);
        Assert.Equal("remaining", draft.Condition["metric"]);
        Assert.Empty(draft.Subjects);
    }

    [Fact]
    public void Subject_options_by_type()
    {
        var providers = JsonNode.Parse("""[{"id":"p1","name":"OpenAI","authScheme":"api-key"},{"id":"s1","name":"Claude Max","authScheme":"oauth-subscription"},{"id":"pl","name":"Plug","authScheme":"plugin"}]""")!.AsArray();
        var tokens = new List<TokenDto>();
        var accounts = new Dictionary<string, List<ProviderAccountDto>>
        {
            ["s1"] = [new ProviderAccountDto("a1", "s1", "Work", null, null, "active", null, null, null, DateTimeOffset.UnixEpoch)],
        };
        Assert.Equal([new Option("p1", "OpenAI")], AlertLogic.SubjectOptions("provider", providers, tokens, accounts));
        Assert.Equal([new Option("a1", "Claude Max / Work")], AlertLogic.SubjectOptions("account", providers, tokens, accounts));
        Assert.Empty(AlertLogic.SubjectOptions(null, providers, tokens, accounts));
        var withOrphan = AlertLogic.WithOrphans([new Option("p1", "OpenAI")], ["p1", "gone"]);
        Assert.Equal(["p1", "gone"], withOrphan.Select(o => o.Value));
    }

    [Fact]
    public void Every_server_event_has_catalog_texts()
    {
        foreach (var id in AlertEvents.All.Select(e => e.Id))
        {
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(AlertLogic.EventName(id).Key), id);
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(AlertLogic.EventHint(id).Key), id);
        }
        foreach (var p in AlertEvents.All.SelectMany(e => e.Params))
            Assert.True(Astra.Desktop.Core.I18n.Loc.Has(AlertLogic.ParamLabel(p.Name).Key), p.Name);
    }
}

public class JsonTextTests
{
    [Fact]
    public void Payloads_are_shown_as_indented_json_and_nothing_as_null()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"event":"test","n":[1,2],"name":"数据"}""");
        var text = Astra.Desktop.Core.Settings.JsonText.Indented(node);
        Assert.Contains("\n  \"event\": \"test\"", text.Replace("\r\n", "\n"));
        Assert.Contains("数据", text); // not escaped
        Assert.Equal("null", Astra.Desktop.Core.Settings.JsonText.Indented(null));
    }

    [Fact]
    public void The_remote_warning_is_asked_until_it_was_accepted_once()
    {
        var store = new Astra.Desktop.Core.Settings.RemoteAckStore();
        Assert.False(store.Acknowledged);
        store.Acknowledge();
        Assert.True(store.Acknowledged);
    }
}

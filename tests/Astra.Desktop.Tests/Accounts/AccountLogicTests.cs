using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Tests.Accounts;

[Collection("Loc")]
public sealed class AccountLogicTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _saved = Loc.Language;

    public AccountLogicTests() => Loc.Language = "en";

    public void Dispose() => Loc.Language = _saved;

    internal static ProviderAccountDto Account(string id = "a1", string name = "alice", string status = "active", bool enabled = true, DateTimeOffset? cooldown = null,
        string kind = "oauth", string? plan = "Max 20x", JsonNode? quota = null, bool current = false, string? keyMasked = null, string? email = null, string? error = null,
        string? source = "login", DateTimeOffset? expires = null)
        => new(id, "p1", name, email, plan, status, expires, null, quota, Now.AddDays(-10), null, enabled, current, 0, cooldown, error, kind, source, null, keyMasked);

    [Fact]
    public void When_uses_the_display_zone_unless_a_zone_is_given()
    {
        var saved = AccountRules.DisplayZone;
        try
        {
            var at = new DateTimeOffset(2026, 10, 26, 13, 20, 0, TimeSpan.Zero);
            AccountRules.DisplayZone = TimeZoneInfo.Utc;
            Assert.Contains("13:20", AccountRules.When(at));
            var plus8 = TimeZoneInfo.CreateCustomTimeZone("plus8", TimeSpan.FromHours(8), "plus8", "plus8");
            Assert.Contains("21:20", AccountRules.When(at, plus8));
        }
        finally
        {
            AccountRules.DisplayZone = saved;
        }
    }

    [Fact]
    public void State_follows_the_web_rules()
    {
        Assert.Equal(AccountState.Disabled, AccountRules.StateOf(Account(enabled: false, status: "expired"), Now));
        Assert.Equal(AccountState.Dead, AccountRules.StateOf(Account(status: "revoked"), Now));
        Assert.Equal(AccountState.Cooling, AccountRules.StateOf(Account(cooldown: Now.AddMinutes(5)), Now));
        Assert.Equal(AccountState.Usable, AccountRules.StateOf(Account(cooldown: Now.AddMinutes(-5)), Now));
    }

    [Fact]
    public void Names_fall_back_in_the_web_order()
    {
        Assert.Equal("alice", AccountRules.NameOf(Account()));
        Assert.Equal("a@x", AccountRules.NameOf(Account(name: "", email: "a@x", keyMasked: "sk…1")));
        Assert.Equal("sk…1", AccountRules.NameOf(Account(name: "", keyMasked: "sk…1")));
        Assert.Equal("a1", AccountRules.NameOf(Account(name: "")));
        Assert.Equal("sk…1", AccountRules.TitleOf(Account(name: "", email: "a@x", keyMasked: "sk…1")));
    }

    [Theory]
    [InlineData("Max 20x", false, 47, 124, 246)]
    [InlineData("ChatGPT Plus", false, 52, 195, 143)]
    [InlineData("Team", false, 130, 100, 230)]
    [InlineData("Max", true, 110, 115, 135)]
    [InlineData(null, false, 110, 115, 135)]
    public void Plan_family_colors(string? plan, bool isKey, byte r, byte g, byte b) => Assert.Equal((r, g, b), AccountRules.PlanRgb(plan, isKey));

    [Fact]
    public void Money_and_counts_match_format_ts()
    {
        Assert.Equal("$1.84", AccountRules.Nanos(1_843_200_000, true));
        Assert.Equal("$1.5K", AccountRules.Nanos(1_523_300_000_000, true));
        Assert.Equal("$1,523.30", AccountRules.Nanos(1_523_300_000_000, false));
        Assert.Equal("$0", AccountRules.Nanos(0, true));
        Assert.Equal("2.4M", AccountRules.Count(2_412_300, true));
        Assert.Equal("9,999", AccountRules.Count(9_999, true));
        Assert.Equal("2,412,300", AccountRules.Count(2_412_300, false));
    }

    private static JsonNode Quota(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void Quota_strip_has_session_and_weekly_else_credits()
    {
        var q = AccountQuota.Parse(Quota("""{"session":{"usedPercent":69.5,"windowMinutes":300},"weekly":{"usedPercent":91},"credits":{"usedPercent":10}}"""));
        var bars = QuotaViews.Strip(q, TimeZoneInfo.Utc);
        Assert.Equal(["session", "weekly"], bars.Select(b => b.Key));
        Assert.Equal("5h", bars[0].Label);
        Assert.Equal(70, bars[0].Percent); // Math.round(69.5) = 70
        Assert.Equal(TextTone.Warning, bars[0].Tone);
        Assert.Equal(7, bars[0].Lit);
        Assert.Equal(TextTone.Danger, bars[1].Tone);

        var copilot = AccountQuota.Parse(Quota("""{"credits":{"usedPercent":40},"entitlement":300}"""));
        var c = Assert.Single(QuotaViews.Strip(copilot));
        Assert.Equal(Loc.T("providers.accounts.quotaShortPremium"), c.Label);
        Assert.Empty(QuotaViews.Strip(AccountQuota.Parse(Quota("""{"session":{}}"""))));
        Assert.False(AccountQuota.Parse(Quota("""{"session":{}}"""))!.HasUsage);
    }

    [Fact]
    public void Quota_card_lists_windows_balance_and_copilot_counts()
    {
        var q = AccountQuota.Parse(Quota("""
            {"fetchedAtUtc":"2026-10-26T11:00:00Z","session":{"usedPercent":120,"windowMinutes":300,"resetsAtUtc":"2026-10-26T15:00:00Z"},
             "credits":{"usedPercent":5,"prepaidBalance":12.5},"entitlement":300,"remaining":120,"overageUsed":3,"overagePermitted":false}
            """))!;
        var card = QuotaViews.Card(q, TimeZoneInfo.Utc);
        Assert.Equal(Loc.T("providers.subscription.quotaFetchedAt", ("when", "10/26/2026, 11:00:00 AM")), card.Caption);
        Assert.Equal(Loc.T("providers.subscription.quotaBalance", ("balance", "12.5")), card.Balance);
        Assert.Equal(100, card.Bars[0].Percent);
        Assert.Equal(5, card.Bars[0].Hours);
        Assert.Equal(Loc.T("providers.subscription.quotaWindowPremium"), card.Bars[1].Label);
        Assert.Contains("180", card.Counts);
        Assert.Contains(Loc.T("providers.subscription.quotaOverageOff"), card.Counts);
        Assert.Empty(QuotaViews.Card(AccountQuota.Parse(Quota("{}"))!).Bars);
    }

    [Fact]
    public void Card_meta_prefers_problems()
    {
        var cooling = AccountCards.Card(Account(cooldown: Now.AddHours(1)), 0, 1, Now, TimeZoneInfo.Utc);
        Assert.Equal(TextTone.Warning, cooling.MetaTone);
        Assert.True(cooling.ShowMeta);
        Assert.True(cooling.HasEdge);

        var dead = AccountCards.Card(Account(status: "expired", error: "invalid_grant"), 0, 1, Now);
        Assert.Equal("invalid_grant", dead.Meta);
        Assert.Empty(dead.Quota);

        var key = AccountCards.Card(Account(kind: "api_key", keyMasked: "sk…9", plan: null), 0, 1, Now);
        Assert.Equal("sk…9", key.Meta);
        Assert.Equal(Loc.T("providers.accounts.kind.api_key"), key.Headline);

        // An account with a quota keeps the expiry line hidden (the strip says enough).
        var withQuota = AccountCards.Card(Account(expires: Now.AddDays(3), quota: Quota("""{"session":{"usedPercent":3}}""")), 0, 1, Now);
        Assert.False(withQuota.ShowMeta);
    }

    [Fact]
    public void Menu_entries_and_their_availability()
    {
        var menu = AccountCards.Menu(Account(current: true), AccountState.Usable, 0, 3);
        Assert.Equal(["activate", "toggle", "up", "down", "quota", "refresh", "relogin", "remove"], menu.Select(m => m.Id));
        Assert.False(menu.Single(m => m.Id == "activate").Enabled);
        Assert.False(menu.Single(m => m.Id == "up").Enabled);
        Assert.True(menu.Single(m => m.Id == "down").Enabled);
        Assert.False(menu.Single(m => m.Id == "relogin").Enabled);

        var key = AccountCards.Menu(Account(kind: "api_key"), AccountState.Usable, 0, 1);
        Assert.Equal(["activate", "toggle", "quota", "remove"], key.Select(m => m.Id));
        Assert.True(key.Last().Destructive);
    }

    [Fact]
    public void Visible_filters_and_searches_keeping_the_order()
    {
        var list = new[]
        {
            Account("a", "alice", plan: "Max"), Account("b", "bob", enabled: false), Account("c", "carol", cooldown: Now.AddHours(1), plan: "Team"),
        };
        Assert.Equal(["c"], AccountCards.Visible(list, AccountFilter.Cooling, null, Now).Select(v => v.Account.Id));
        Assert.Equal(2, AccountCards.Visible(list, AccountFilter.All, "TEAM", Now).Single().Index);
        Assert.Equal([AccountFilter.All, AccountFilter.Usable, AccountFilter.Cooling, AccountFilter.Disabled], AccountCards.Chips(list, Now).Select(c => c.Filter));
        Assert.Equal(["b", "a", "c"], AccountCards.Swap(list, 0, 1));
        Assert.Null(AccountCards.Swap(list, 2, 1));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("tok1\ntok2, tok3;tok4\n\n# comment\n", 4)]
    [InlineData("\"tid=abc;exp=1;sku=x\"", 1)]
    [InlineData("""[{"a":1},"tok"," ",3]""", 2)]
    [InlineData("""{"claudeAiOauth":{}}""", 1)]
    [InlineData("{\"a\":1}\n{\"b\":2}\ntok", 3)]
    [InlineData("[not json", 2)]
    public void Import_entries_count_like_the_server(string text, int count) => Assert.Equal(count, AccountImport.CountEntries(text));

    [Fact]
    public void Import_rules()
    {
        var info = new AccountImportInfoDto(true, true, ["claude-refresh-token", "zai-api-key"], 2);
        Assert.Equal(["zai-api-key"], AccountImport.Formats(info, ImportMode.ApiKey));
        Assert.True(AccountImport.TakesRefreshTokens(info, ImportMode.Batch));
        Assert.False(AccountImport.TakesRefreshTokens(info, ImportMode.ApiKey));
        Assert.False(AccountImport.CanRun(3, info));
        var req = AccountImport.Request("x", ImportMode.ApiKey, false, "  ");
        Assert.True(req.AsApiKey);
        Assert.Null(req.DryRun);
        Assert.Null(req.HttpProxy);
        Assert.Equal("accent", AccountImport.StatusTone("ready"));
        var item = new BatchImportItemDto(1, "s", "imported", "warn", Account(name: "bob"));
        Assert.Equal("bob · warn", AccountImport.RowText(item));
        Assert.Equal("dup", AccountImport.RowText(new BatchImportItemDto(2, "s", "duplicate", "dup", Account())));
    }

    [Fact]
    public void Login_answers_are_read_from_any_shape()
    {
        var device = LoginStart.Parse(JsonNode.Parse("""{"mode":"device","state":"s","userCode":"AB-CD","verificationUrl":"https://v","interval":1}""")!.AsObject());
        Assert.True(device.Polls);
        Assert.Equal(TimeSpan.FromSeconds(3), device.PollEvery);
        Assert.Equal(TimeSpan.FromSeconds(5), LoginStart.Parse(JsonNode.Parse("""{"mode":"cli","state":"s","authorizeUrl":"u","interval":0}""")!.AsObject()).PollEvery);
        Assert.False(LoginStart.Parse(JsonNode.Parse("""{"mode":"pkce","state":"s","authorizeUrl":"u"}""")!.AsObject()).Polls);

        var done = LoginPoll.Parse(JsonNode.Parse("""{"status":"done","account":""" + Json.SerializeApi(Account(name: "new")) + "}")!.AsObject());
        Assert.True(done.Done);
        Assert.Equal("new", done.Account!.DisplayName);
        Assert.True(LoginPoll.Parse(JsonNode.Parse("""{"status":"slow_down"}""")!.AsObject()).Pending);
        Assert.Equal("expired", LoginPoll.Parse(JsonNode.Parse("""{"status":"expired"}""")!.AsObject()).FailureText);
    }

    [Fact]
    public void Reset_cards_and_plugin_answers_parse_leniently()
    {
        var list = ResetCreditList.Parse(JsonNode.Parse("""{"credits":[{"id":"1","status":"available","reset_type":"weekly"},{"id":"2","status":"redeemed","title":"T"}]}""")!.AsObject());
        Assert.Equal(1, list.AvailableCount);
        Assert.Equal("weekly", list.Credits[0].Name);
        Assert.True(list.Credits[1].Spent);

        var prompt = PluginPrompt.Parse(JsonNode.Parse("""{"type":"select","key":"r","message":"m","options":[{"label":"A","value":"a"}]}"""));
        Assert.Equal("a", prompt!.InitialAnswer);
        Assert.Null(PluginPrompt.Parse(null));

        var usage = PluginUsage.Parse(JsonNode.Parse("""{"plan":"Pro","windows":[{"name":"d","used":34},{"name":"m","amount":12,"limit":50,"unit":"req","resetsAt":1764547200}]}""")!.AsObject());
        Assert.Equal("34%", PluginAccountsModel.WindowText(usage.Windows[0]));
        Assert.StartsWith("12 / 50 req · ", PluginAccountsModel.WindowText(usage.Windows[1], TimeZoneInfo.Utc));
        Assert.Contains("12/01/2025", PluginAccountsModel.WindowText(usage.Windows[1], TimeZoneInfo.Utc));
    }

    [Fact]
    public void Policy_group_rules()
    {
        var claude = new SubscriptionPolicyDto("any", "manual", true, false);
        Assert.True(SubscriptionPolicyRules.Visible(claude));
        Assert.Equal("providers.subscription.policy.anyWarning", SubscriptionPolicyRules.WarningKey(claude));
        Assert.Equal("providers.subscription.policy.mimicWarning", SubscriptionPolicyRules.WarningKey(claude with { MimicClaudeCode = true }));
        Assert.Null(SubscriptionPolicyRules.WarningKey(claude with { ClientPolicy = "claude-code-only" }));
        Assert.False(SubscriptionPolicyRules.Visible(new SubscriptionPolicyDto("any", "manual", false, false)));
        Assert.True(SubscriptionPolicyRules.Visible(new SubscriptionPolicyDto("claude-code-only", "manual", false, false)));
    }

    [Fact]
    public void Every_catalog_key_the_views_read_exists()
    {
        string[] fixedKeys =
        [
            "providers.accounts.statTotal", "providers.accounts.statUsable", "providers.accounts.statCooling", "providers.accounts.statDead",
            "providers.accounts.providerProxy", "providers.accounts.proxyFallbackSystem", "providers.accounts.search", "providers.accounts.filterLabel",
            "providers.accounts.batchImport", "providers.accounts.add", "providers.accounts.addLogin", "providers.accounts.addLoginProxy",
            "providers.accounts.addToken", "providers.accounts.addApiKey", "providers.accounts.noMatch", "providers.accounts.selected",
            "providers.accounts.clearSelection", "providers.accounts.bulkDone", "providers.accounts.bulkDeleteConfirm", "providers.accounts.bulkDeleteDetail",
            "providers.accounts.loginProxyTitle", "providers.accounts.loginProxyDesc", "providers.accounts.loginProxyStart", "providers.accounts.select",
            "providers.accounts.proxyBadge", "providers.accounts.order", "providers.accounts.signedOut", "providers.accounts.neverExpires",
            "providers.accounts.expiresAt", "providers.accounts.renamed", "providers.accounts.proxySaved", "providers.accounts.detail.identity",
            "providers.accounts.detail.name", "providers.accounts.detail.proxy", "providers.accounts.detail.proxyHint", "providers.accounts.detail.quota",
            "providers.accounts.detail.credentials", "providers.accounts.detail.status", "providers.accounts.detail.source", "providers.accounts.detail.key",
            "providers.accounts.detail.expires", "providers.accounts.detail.lastRefresh", "providers.accounts.detail.order", "providers.accounts.detail.created",
            "providers.accounts.detail.lastError", "providers.accounts.import.desc", "providers.accounts.import.formats", "providers.accounts.import.proxy",
            "providers.accounts.import.rotateWarning", "providers.accounts.import.count", "providers.accounts.import.preview", "providers.accounts.import.run",
            "providers.accounts.import.summary", "providers.accounts.import.previewSummary", "providers.accounts.import.label",
            "providers.accounts.import.apiKeyLabel", "providers.accounts.import.batchTitle", "providers.accounts.import.tokenTitle",
            "providers.accounts.import.apiKeyTitle", "providers.accounts.quotaShortSession", "providers.accounts.quotaShortWeekly",
            "providers.accounts.quotaShortPremium", "providers.accounts.quotaShortCredits", "providers.accounts.kind.oauth", "providers.accounts.kind.api_key",
            "providers.subscription.footer", "providers.subscription.none", "providers.subscription.login", "providers.subscription.loginDesc",
            "providers.subscription.loginFailed", "providers.subscription.loginOk", "providers.subscription.pasteTitle", "providers.subscription.pasteDetail",
            "providers.subscription.openAuthorize", "providers.subscription.pastePlaceholder", "providers.subscription.pasteCode",
            "providers.subscription.pasteSubmit", "providers.subscription.waiting", "providers.subscription.waitingDetail",
            "providers.subscription.doneAuthorize", "providers.subscription.cliDetail", "providers.subscription.deviceCode",
            "providers.subscription.visitPage", "providers.subscription.checkNow", "providers.subscription.checkNowHint",
            "providers.subscription.stillWaiting", "providers.subscription.needsVerification", "providers.subscription.importCodex",
            "providers.subscription.importCopilot", "providers.subscription.importOk", "providers.subscription.localCodexFound",
            "providers.subscription.unknownAccount", "providers.subscription.localCopilotFound", "providers.subscription.localCopilotHint",
            "providers.subscription.activate", "providers.subscription.activated", "providers.subscription.enable", "providers.subscription.disable",
            "providers.subscription.disabled", "providers.subscription.moveUp", "providers.subscription.moveDown", "providers.subscription.quotaRefresh",
            "providers.subscription.quotaUnsupported", "providers.subscription.refreshToken", "providers.subscription.tokenRefreshed",
            "providers.subscription.relogin", "providers.subscription.logout", "providers.subscription.logoutConfirm", "providers.subscription.current",
            "providers.subscription.cooldown", "providers.subscription.quotaResets", "providers.subscription.quotaWindowSession",
            "providers.subscription.quotaWindowWeekly", "providers.subscription.quotaWindowPremium", "providers.subscription.quotaWindowCredits",
            "providers.subscription.quotaBalance", "providers.subscription.quotaPremiumCount", "providers.subscription.quotaOverage",
            "providers.subscription.quotaOverageOff", "providers.subscription.quotaFetchedAt", "providers.subscription.quotaNoWindows",
            "providers.subscription.policy.title", "providers.subscription.policy.footer", "providers.subscription.policy.claudeCodeOnly",
            "providers.subscription.policy.claudeCodeOnlyDetail", "providers.subscription.policy.mimic", "providers.subscription.policy.mimicDetail",
            "providers.subscription.policy.mimicWarning", "providers.subscription.policy.anyWarning", "providers.subscription.policy.switchMode",
            "providers.subscription.policy.manual", "providers.subscription.policy.failover", "providers.subscription.policy.balanced",
            "providers.subscription.policy.manualDetail", "providers.subscription.policy.failoverDetail", "providers.subscription.policy.balancedDetail",
            "providers.subscription.credits.title", "providers.subscription.credits.footer", "providers.subscription.credits.unavailable",
            "providers.subscription.credits.none", "providers.subscription.credits.expires", "providers.subscription.credits.redeemedAt",
            "providers.subscription.credits.notInPlan", "providers.subscription.credits.consume", "providers.subscription.credits.consumeConfirm",
            "providers.subscription.credits.consumeDetail", "providers.subscription.credits.consumed", "providers.subscription.credits.consumeFailed",
            "providers.subscription.credits.availableCount", "providers.subscription.credits.status.available",
            "providers.subscription.credits.status.redeemed", "providers.subscription.credits.status.expired",
            "providers.subscription.status.active", "providers.subscription.status.expired", "providers.subscription.status.revoked",
            "providers.usage.title", "providers.usage.today", "providers.usage.total", "providers.usage.requests", "providers.usage.tokens",
            "providers.usage.cost", "providers.usage.hint", "providers.usage.dayHint", "providers.usage.error",
            "pluginAccounts.noPlugin", "pluginAccounts.apiKey", "pluginAccounts.browser", "pluginAccounts.empty", "pluginAccounts.emptyDetail",
            "pluginAccounts.checkin", "pluginAccounts.checkinDetail", "pluginAccounts.checkinOn", "pluginAccounts.checkinNow", "pluginAccounts.credit",
            "pluginAccounts.streak", "pluginAccounts.checkinNever", "pluginAccounts.signOutConfirm", "pluginAccounts.signOut", "pluginAccounts.current",
            "pluginAccounts.active", "pluginAccounts.lapsed", "pluginAccounts.usage", "pluginAccounts.lapsedDetail", "pluginAccounts.plan",
            "pluginAccounts.until", "pluginAccounts.resets", "pluginAccounts.signedIn", "pluginAccounts.signInTitle", "pluginAccounts.continue",
            "pluginAccounts.key", "pluginAccounts.signIn", "pluginAccounts.openPage", "pluginAccounts.deviceCode", "pluginAccounts.openAgain",
            "pluginAccounts.code", "pluginAccounts.waiting", "pluginAccounts.failed",
            "common.more", "common.save", "common.delete",
        ];
        foreach (var key in fixedKeys) Assert.True(Loc.Has(key), key);
        foreach (var f in AccountCards.Filters) Assert.True(Loc.Has(f.Key), f.Key);
        foreach (var s in new[] { "imported", "updated", "duplicate", "invalid_format", "failed", "ready" }) Assert.True(Loc.Has(AccountImport.StatusKey(s)), s);
        foreach (var s in new[] { "login", "local", "token", "batch" }) Assert.True(Loc.Has("providers.accounts.source." + s), s);
        foreach (var o in new[] { "claimed", "done", "ineligible", "inactive", "captcha", "failed" }) Assert.True(Loc.Has("pluginAccounts.outcome." + o), o);
    }
}

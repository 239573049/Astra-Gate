using System.Net;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Server.IntegrationTests;

/// <summary>The warm-up of 5-hour windows (linked update 36): the schedule against a fake clock, the skip rules and the admin surface.</summary>
public class WarmupTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeSender : IWarmupSender
    {
        public List<string> Sent { get; } = [];
        public WarmupOutcome Outcome { get; set; } = WarmupOutcome.Ok;

        public Task<WarmupOutcome> SendAsync(Provider provider, ProviderAccount account, CancellationToken ct)
        {
            Sent.Add(account.Id);
            return Task.FromResult(Outcome);
        }
    }

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal);

    // ---------------------------------------------------------------- the clock

    [Fact]
    public void A_Daily_Slot_Is_Due_Once_Within_The_Grace_And_Never_After()
    {
        var plan = WarmupSchedule.PlanOf("daily", ["06:00"], []);
        Assert.False(WarmupSchedule.DailyDue(plan, null, At("2026-11-16T05:59:00Z"), Utc)); // not yet
        Assert.True(WarmupSchedule.DailyDue(plan, null, At("2026-11-16T06:00:00Z"), Utc));
        Assert.True(WarmupSchedule.DailyDue(plan, At("2026-11-15T06:01:00Z"), At("2026-11-16T06:20:00Z"), Utc)); // yesterday's run is not today's
        Assert.False(WarmupSchedule.DailyDue(plan, At("2026-11-16T06:01:00Z"), At("2026-11-16T06:20:00Z"), Utc)); // already served
        // a restart long after the slot does not catch up: the window the day wanted is gone
        Assert.False(WarmupSchedule.DailyDue(plan, null, At("2026-11-16T09:00:00Z"), Utc));
        Assert.True(WarmupSchedule.DailyDue(plan, null, At("2026-11-16T06:29:00Z"), Utc)); // but within the grace it does
    }

    [Fact]
    public void Weekdays_And_Several_Times_Pick_The_Slots()
    {
        // 2026-11-16 is a Monday
        var plan = WarmupSchedule.PlanOf("daily", ["06:00", "11:05"], [1, 3]); // Monday and Wednesday
        Assert.True(WarmupSchedule.DailyDue(plan, null, At("2026-11-16T11:10:00Z"), Utc));
        Assert.False(WarmupSchedule.DailyDue(plan, null, At("2026-11-17T06:10:00Z"), Utc)); // Tuesday
        Assert.Equal(At("2026-11-16T11:05:00Z"), WarmupSchedule.NextSlot(plan, At("2026-11-16T07:00:00Z"), Utc));
        Assert.Equal(At("2026-11-18T06:00:00Z"), WarmupSchedule.NextSlot(plan, At("2026-11-16T12:00:00Z"), Utc)); // Wednesday
    }

    [Fact]
    public void The_Times_Are_In_The_Zone_Not_In_Utc()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test", "test");
        var plan = WarmupSchedule.PlanOf("daily", ["06:00"], []);
        Assert.Equal(At("2026-11-15T22:00:00Z"), WarmupSchedule.LatestSlot(plan, At("2026-11-16T01:00:00Z"), zone));
    }

    [Fact]
    public void Auto_Warms_Right_After_The_Window_Reset_And_Not_Before_Or_Twice()
    {
        var reset = At("2026-11-16T10:00:00Z");
        Assert.False(WarmupSchedule.AutoDue(reset, null, At("2026-11-16T09:59:00Z")));
        Assert.True(WarmupSchedule.AutoDue(reset, At("2026-11-16T04:30:00Z"), At("2026-11-16T10:01:00Z")));
        Assert.False(WarmupSchedule.AutoDue(reset, At("2026-11-16T10:02:00Z"), At("2026-11-16T10:30:00Z"))); // served since the reset
        // no window in the snapshot: at most one a window
        Assert.True(WarmupSchedule.AutoDue(null, null, At("2026-11-16T10:00:00Z")));
        Assert.False(WarmupSchedule.AutoDue(null, At("2026-11-16T08:00:00Z"), At("2026-11-16T10:00:00Z")));
        Assert.True(WarmupSchedule.AutoDue(null, At("2026-11-16T04:00:00Z"), At("2026-11-16T10:00:00Z")));
    }

    // ---------------------------------------------------------------- the service

    private sealed class Rig : IAsyncDisposable
    {
        public required TestHost Host { get; init; }
        public required FakeClock Clock { get; init; }
        public required FakeSender Sender { get; init; }
        public required WarmupService Service { get; init; }
        public required Provider Claude { get; init; }
        public required Provider Codex { get; init; }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    private static async Task<Rig> StartAsync(string now = "2026-11-16T06:00:00Z")
    {
        var host = await TestHost.StartAsync();
        var clock = new FakeClock(At(now));
        var sender = new FakeSender();
        var settings = host.App.Services.GetRequiredService<SettingsService>();
        var service = new WarmupService(host.Db, settings, sender, clock, NullLogger<WarmupService>.Instance);
        var claude = new Provider
        {
            Id = "claude", Name = "Claude", TemplateId = SubscriptionSupport.ClaudeSubscriptionKey, AuthScheme = AuthSchemes.OAuthSubscription,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://api.anthropic.com/v1" }],
        };
        var codex = new Provider
        {
            Id = "codex", Name = "ChatGPT", TemplateId = "openai-subscription", AuthScheme = AuthSchemes.OAuthSubscription,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = "https://chatgpt.com/backend-api/codex" }],
        };
        await host.Db.Providers.InsertAsync(claude);
        await host.Db.Providers.InsertAsync(codex);
        return new Rig { Host = host, Clock = clock, Sender = sender, Service = service, Claude = claude, Codex = codex };
    }

    private static async Task<ProviderAccount> AddAsync(Rig rig, string providerId, string id, Action<ProviderAccount>? tweak = null)
    {
        var protector = rig.Host.App.Services.GetRequiredService<ISecretProtector>();
        var account = new ProviderAccount
        {
            Id = id, ProviderId = providerId, DisplayName = id, AccessTokenEnc = protector.Protect("token"), RefreshTokenEnc = protector.Protect("refresh"),
            ExpiresAtUtc = rig.Clock.Now.AddHours(2), Status = AccountStatus.Active,
        };
        tweak?.Invoke(account);
        await rig.Host.Db.Accounts.InsertAsync(account);
        return account;
    }

    private static Task EnableAsync(Rig rig, Action<WarmupSettings>? tweak = null) =>
        rig.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(s =>
        {
            s.UsageTimeZone = "UTC";
            s.Warmup.Enabled = true;
            s.Warmup.Times = ["06:00"];
            tweak?.Invoke(s.Warmup);
        });

    [Fact]
    public async Task Nothing_Is_Warmed_While_The_Switch_Is_Off()
    {
        await using var rig = await StartAsync();
        await AddAsync(rig, "claude", "c1");
        Assert.Empty(await rig.Service.RunDueAsync(default));
        Assert.Empty(rig.Sender.Sent);
    }

    [Fact]
    public async Task A_Due_Slot_Warms_Each_Account_Once_And_The_Outcome_Is_Kept()
    {
        await using var rig = await StartAsync();
        await EnableAsync(rig);
        await AddAsync(rig, "claude", "c1");
        await AddAsync(rig, "codex", "x1");

        var runs = await rig.Service.RunDueAsync(default);
        Assert.Equal(["c1", "x1"], runs.Select(r => r.AccountId).Order());
        Assert.All(runs, r => Assert.Equal("ok", r.Result));
        // the same minute again, and the next ones: served already
        Assert.Empty(await rig.Service.RunDueAsync(default));
        rig.Clock.Now = At("2026-11-16T06:05:00Z");
        Assert.Empty(await rig.Service.RunDueAsync(default));
        Assert.Equal(2, rig.Sender.Sent.Count);

        var accounts = await rig.Service.ListAsync(default);
        var c1 = accounts.Single(a => a.AccountId == "c1");
        Assert.Equal("ok", c1.LastResult);
        Assert.Equal(At("2026-11-16T06:00:00Z"), c1.LastAt);
        Assert.Equal(At("2026-11-17T06:00:00Z"), c1.NextAt);
    }

    [Fact]
    public async Task A_Restart_Within_The_Grace_Catches_Up_And_A_Restart_After_It_Does_Not()
    {
        await using var rig = await StartAsync("2026-11-16T06:20:00Z"); // the machine woke 20 minutes after the slot
        await EnableAsync(rig);
        await AddAsync(rig, "claude", "c1");
        Assert.Single(await rig.Service.RunDueAsync(default));

        await using var late = await StartAsync("2026-11-16T08:00:00Z");
        await EnableAsync(late);
        await AddAsync(late, "claude", "c1");
        Assert.Empty(await late.Service.RunDueAsync(default));
    }

    [Fact]
    public async Task Disabled_Lapsed_And_Cooling_Accounts_Are_Skipped_With_A_Reason_And_No_Request()
    {
        await using var rig = await StartAsync();
        await EnableAsync(rig);
        await AddAsync(rig, "claude", "off", a => a.Enabled = false);
        await AddAsync(rig, "claude", "revoked", a => a.Status = AccountStatus.Revoked);
        await AddAsync(rig, "claude", "lapsed", a => a.ExpiresAtUtc = rig.Clock.Now.AddMinutes(-5));
        await AddAsync(rig, "claude", "cooling");
        await rig.Host.Db.Accounts.SetCooldownAsync("cooling", rig.Clock.Now.AddHours(1), "429 rate limit");

        var runs = await rig.Service.RunDueAsync(default);
        Assert.Equal(4, runs.Count);
        Assert.All(runs, r => Assert.Equal("skipped", r.Result));
        Assert.Contains("lapsed", runs.Single(r => r.AccountId == "lapsed").Message);
        Assert.Contains("renewed by the account's next request", runs.Single(r => r.AccountId == "lapsed").Message);
        Assert.Empty(rig.Sender.Sent); // nothing was sent, and nothing was renewed
    }

    [Fact]
    public async Task A_Failure_Is_Kept_On_The_Account_And_Never_Thrown()
    {
        await using var rig = await StartAsync();
        await EnableAsync(rig);
        await AddAsync(rig, "claude", "c1");
        rig.Sender.Outcome = new WarmupOutcome("failed", "HTTP 429");
        var run = Assert.Single(await rig.Service.RunDueAsync(default));
        Assert.Equal("failed", run.Result);
        var account = (await rig.Host.Db.Accounts.GetAsync("c1"))!;
        Assert.Equal("failed", account.Extra["warmup"]!["last"]!["result"]!.GetValue<string>());
        Assert.Equal(AccountStatus.Active, account.Status); // the account is not marked
        Assert.Null(account.CooldownUntilUtc);
    }

    [Fact]
    public async Task An_Account_Can_Follow_Its_Own_Times_Or_Opt_Out_And_A_Family_Can_Be_Left_Out()
    {
        await using var rig = await StartAsync("2026-11-16T07:00:00Z");
        await EnableAsync(rig, w => w.Codex = false);
        await AddAsync(rig, "claude", "mine", a => a.Extra["warmup"] = new JsonObject { ["times"] = new JsonArray(JsonValue.Create("07:00")) });
        await AddAsync(rig, "claude", "global"); // 06:00: an hour ago, past the grace
        await AddAsync(rig, "claude", "never", a => a.Extra["warmup"] = new JsonObject { ["mode"] = "off", ["times"] = new JsonArray(JsonValue.Create("07:00")) });
        await AddAsync(rig, "codex", "x1", a => a.Extra["warmup"] = new JsonObject { ["times"] = new JsonArray(JsonValue.Create("07:00")) });

        var runs = await rig.Service.RunDueAsync(default);
        Assert.Equal(["mine"], runs.Select(r => r.AccountId));
        var list = await rig.Service.ListAsync(default);
        Assert.False(list.Single(a => a.AccountId == "x1").Warmed); // the family is left out
        Assert.False(list.Single(a => a.AccountId == "never").Warmed);
        Assert.Equal("off", list.Single(a => a.AccountId == "never").Mode);
    }

    [Fact]
    public async Task Warm_Up_Now_Sends_Whatever_The_Schedule_Says_But_Not_To_A_Lapsed_Account()
    {
        await using var rig = await StartAsync("2026-11-16T15:00:00Z");
        await EnableAsync(rig); // no slot is near
        await AddAsync(rig, "claude", "c1");
        await AddAsync(rig, "claude", "lapsed", a => a.ExpiresAtUtc = rig.Clock.Now.AddMinutes(-1));
        Assert.Equal("ok", (await rig.Service.WarmNowAsync("c1", default)).Result);
        Assert.Equal("skipped", (await rig.Service.WarmNowAsync("lapsed", default)).Result);
        Assert.Equal(["c1"], rig.Sender.Sent);
    }

    [Fact]
    public async Task A_Claude_Bridge_Account_Is_Warmed_Without_A_Token()
    {
        await using var rig = await StartAsync();
        await EnableAsync(rig);
        await AddAsync(rig, "claude", "b1", a =>
        {
            a.Kind = AccountKinds.ClaudeBridge;
            a.AccessTokenEnc = null;
            a.RefreshTokenEnc = null;
            a.ExpiresAtUtc = null;
        });
        Assert.Equal("ok", Assert.Single(await rig.Service.RunDueAsync(default)).Result);
    }

    [Theory]
    [InlineData("claude", new[] { "claude-opus-5-5", "claude-haiku-5-5", "claude-sonnet-5" }, "claude-haiku-5-5")]
    [InlineData("codex", new[] { "gpt-6-sol", "gpt-6-luna", "gpt-5.5" }, "gpt-6-luna")]
    [InlineData("codex", new[] { "gpt-6-sol", "gpt-5.5-mini" }, "gpt-5.5-mini")]
    [InlineData("codex", new[] { "gpt-6-sol" }, "gpt-6-sol")]
    public void The_Cheapest_Model_Is_Picked_By_Name(string family, string[] models, string expected) =>
        Assert.Equal(expected, WarmupSender.PickModel(family, models));

    // ---------------------------------------------------------------- the admin surface

    [Fact]
    public async Task The_Settings_And_An_Accounts_Own_Plan_Are_Validated_And_Persist()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Get, "/api/warmup");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body!["enabled"]!.GetValue<bool>()); // off by default
        Assert.Equal("daily", body["mode"]!.GetValue<string>());

        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/warmup", new { times = new[] { "6am" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/warmup", new { weekdays = new[] { 9 } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, body) = await host.SendAsync(HttpMethod.Put, "/api/warmup", new { enabled = true, mode = "auto", times = new[] { "11:05", "06:00" }, weekdays = new[] { 1, 2 }, codex = false });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["enabled"]!.GetValue<bool>());
        Assert.Equal(["06:00", "11:05"], body["times"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray());
        Assert.False(body["codex"]!.GetValue<bool>());
        Assert.Equal(AppSettings.StorageKey, "app");
        var settings = host.App.Services.GetRequiredService<SettingsService>().Current.Warmup;
        Assert.Equal("auto", settings.Mode);

        var provider = new Provider
        {
            Id = "claude", Name = "Claude", TemplateId = SubscriptionSupport.ClaudeSubscriptionKey, AuthScheme = AuthSchemes.OAuthSubscription,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://api.anthropic.com/v1" }],
        };
        await host.Db.Providers.InsertAsync(provider);
        await host.Db.Accounts.InsertAsync(new ProviderAccount { Id = "c1", ProviderId = "claude", DisplayName = "c1", Status = AccountStatus.Active });

        (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/warmup/accounts/c1", new { mode = "sometimes" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/warmup/accounts/c1", new { mode = "daily", times = new[] { "09:30" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("daily", body!["mode"]!.GetValue<string>());
        Assert.Equal("09:30", body["times"]![0]!.GetValue<string>());
        // an account of a provider that is not warmed
        await host.Db.Providers.InsertAsync(new Provider { Id = "other", Name = "Other", Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://x/v1" }] });
        await host.Db.Accounts.InsertAsync(new ProviderAccount { Id = "o1", ProviderId = "other", DisplayName = "o1", Status = AccountStatus.Active });
        (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/warmup/accounts/o1", new { mode = "off" });
        Assert.Equal(HttpStatusCode.Conflict, status);
    }
}

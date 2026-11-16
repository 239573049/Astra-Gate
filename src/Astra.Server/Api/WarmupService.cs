using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Microsoft.Extensions.Hosting;

namespace Astra.Server.Api;

/// <summary>What one warm-up came to.</summary>
/// <param name="Result"><c>ok</c>, <c>skipped</c> or <c>failed</c>.</param>
public sealed record WarmupOutcome(string Result, string? Message = null)
{
    public static WarmupOutcome Ok { get; } = new("ok");
}

/// <summary>Sends the cheapest request that starts an account's window (the real one: <see cref="WarmupSender"/>).</summary>
public interface IWarmupSender
{
    Task<WarmupOutcome> SendAsync(Provider provider, ProviderAccount account, CancellationToken ct);
}

/// <summary>
/// Warm-up of 5-hour windows (linked update 36): for each Claude and ChatGPT (Codex) subscription account that is switched on for it, a tiny
/// request at the time the user picked each day (or right after the previous window reset, "auto"). It uses the account's own credentials and
/// proxy; it never fails loudly (the outcome is kept on the account, <c>extra.warmup.last</c>, and logged as a request marked as a warm-up);
/// it skips a disabled, revoked or cooling account, and — nothing renews tokens in the background (memory subscription-refresh-on-use) — one whose
/// token has lapsed, saying so. What is due is worked out from settings and the last run, so a restart changes nothing.
/// </summary>
public sealed class WarmupService(AstraDatabase db, SettingsService settings, IWarmupSender sender, TimeProvider clock, ILogger<WarmupService> logger)
{
    public const string ClaudeFamily = "claude";
    public const string CodexFamily = "codex";
    public const string Inherit = "inherit";

    /// <summary>The family a provider's accounts are warmed as; null = not a warmed provider.</summary>
    public static string? FamilyOf(Provider provider) => SubscriptionSupport.ProviderKeyOf(provider) switch
    {
        SubscriptionSupport.ClaudeSubscriptionKey => ClaudeFamily,
        "openai-subscription" => CodexFamily,
        _ => null,
    };

    // ------------------------------------------------------------------ settings

    public WarmupSettingsDto GetSettings()
    {
        var w = settings.Current.Warmup;
        return new WarmupSettingsDto(w.Enabled, w.Mode, [.. w.Times], [.. w.Weekdays], w.Claude, w.Codex, WarmupSchedule.ZoneOf(settings.Current).Id);
    }

    public async Task<WarmupSettingsDto> UpdateSettingsAsync(WarmupSettingsPatch patch, CancellationToken ct)
    {
        if (patch.Mode is { } mode && !WarmupSettings.Modes.Contains(mode)) throw new AdminApiException(400, "mode must be daily, auto or off");
        if (patch.Times is { } times)
        {
            if (times.Count > 8) throw new AdminApiException(400, "at most 8 times a day");
            if (times.Any(t => !WarmupSchedule.TryParseTime(t, out _))) throw new AdminApiException(400, "a time of day must look like 06:00");
        }
        if (patch.Weekdays is { } days && days.Any(d => d is < 0 or > 6)) throw new AdminApiException(400, "weekdays are 0 (Sunday) to 6 (Saturday)");
        await settings.UpdateAsync(s =>
        {
            var w = s.Warmup;
            if (patch.Enabled is { } e) w.Enabled = e;
            if (patch.Mode is { } m) w.Mode = m;
            if (patch.Times is { } t) w.Times = t.Distinct().Order().ToList();
            if (patch.Weekdays is { } d) w.Weekdays = d.Distinct().Order().ToList();
            if (patch.Claude is { } c) w.Claude = c;
            if (patch.Codex is { } x) w.Codex = x;
        }, ct);
        return GetSettings();
    }

    // ------------------------------------------------------------------ plans and state

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<string>? Strings(JsonObject? o, string key) =>
        o?[key] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).ToList() : null;

    private static List<int>? Ints(JsonObject? o, string key) =>
        o?[key] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1).Where(i => i >= 0).ToList() : null;

    /// <summary>The account's own mode: <c>inherit</c> unless it says <c>off</c>, <c>daily</c> or <c>auto</c>.</summary>
    internal static string ModeOf(ProviderAccount account) =>
        Str(account.Extra["warmup"] as JsonObject, "mode") is { } m && (m == WarmupSettings.ModeOff || WarmupSettings.Modes.Contains(m)) ? m : Inherit;

    /// <summary>The plan the account follows, or null when it is not warmed (global switch, its family or its own mode says no).</summary>
    internal WarmupPlan? PlanOf(Provider provider, ProviderAccount account)
    {
        var w = settings.Current.Warmup;
        if (!w.Enabled || FamilyOf(provider) is not { } family) return null;
        if (family == ClaudeFamily ? !w.Claude : !w.Codex) return null;
        var own = account.Extra["warmup"] as JsonObject;
        var mode = ModeOf(account);
        if (mode == WarmupSettings.ModeOff) return null;
        if (mode == Inherit) mode = w.Mode;
        if (mode == WarmupSettings.ModeOff) return null;
        var times = Strings(own, "times") is { Count: > 0 } t ? t : w.Times;
        var days = Ints(own, "weekdays") is { Count: > 0 } d ? d : w.Weekdays;
        return WarmupSchedule.PlanOf(mode, times, days);
    }

    private static (DateTimeOffset? At, string? Result, string? Message) LastOf(ProviderAccount account)
    {
        var last = (account.Extra["warmup"] as JsonObject)?["last"] as JsonObject;
        return (last?["at"] is JsonValue v && v.TryGetValue<DateTimeOffset>(out var at) ? at : null, Str(last, "result"), Str(last, "message"));
    }

    /// <summary>When the account's 5-hour window resets, as its last quota snapshot says (the Claude and Codex snapshots both carry it).</summary>
    private static DateTimeOffset? WindowResetOf(ProviderAccount account) =>
        account.Extra["quota"]?["session"]?["resetsAtUtc"] is JsonValue v && v.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>Why this account cannot be warmed now, or null when it can.</summary>
    internal static string? SkipReason(Provider provider, ProviderAccount account, DateTimeOffset now)
    {
        if (!provider.Enabled) return "provider switched off";
        if (!account.Enabled) return "account switched off";
        if (account.Status != AccountStatus.Active) return $"account {account.Status}";
        if (account.CooldownUntilUtc > now) return "account cooling down";
        // nothing renews tokens in the background: a lapsed one waits for the account's next real request
        if (account.Kind == AccountKinds.OAuth && !SubscriptionTokenService.IsFresh(account, now))
            return "token lapsed (it is renewed by the account's next request, not by the warm-up)";
        if (account.Kind is not (AccountKinds.OAuth or AccountKinds.ClaudeBridge)) return "not a login account";
        return null;
    }

    // ------------------------------------------------------------------ the schedule

    /// <summary>Warms every account that is due now. Called every minute by <see cref="WarmupWorker"/>; never throws for an account.</summary>
    public async Task<List<WarmupRunDto>> RunDueAsync(CancellationToken ct)
    {
        var runs = new List<WarmupRunDto>();
        if (!settings.Current.Warmup.Enabled) return runs;
        var now = clock.GetUtcNow();
        var zone = WarmupSchedule.ZoneOf(settings.Current);
        foreach (var provider in await db.Providers.ListAsync(ct))
        {
            if (FamilyOf(provider) is null) continue;
            foreach (var account in await db.Accounts.ListAsync(provider.Id, ct))
            {
                if (PlanOf(provider, account) is not { } plan) continue;
                var (last, _, _) = LastOf(account);
                var due = plan.Mode == WarmupSettings.ModeAuto
                    ? WarmupSchedule.AutoDue(WindowResetOf(account), last, now)
                    : WarmupSchedule.DailyDue(plan, last, now, zone);
                if (!due) continue;
                runs.Add(await WarmAsync(provider, account, now, ct));
            }
        }
        return runs;
    }

    /// <summary>"Warm up now": the same request, whatever the schedule says (the skip rules still apply).</summary>
    public async Task<WarmupRunDto> WarmNowAsync(string accountId, CancellationToken ct)
    {
        var account = await db.Accounts.GetAsync(accountId, ct) ?? throw new AdminApiException(404, "no such account");
        var provider = await db.Providers.GetAsync(account.ProviderId, ct) ?? throw new AdminApiException(404, "no such provider");
        if (FamilyOf(provider) is null) throw new AdminApiException(409, "only Claude and ChatGPT (Codex) subscription accounts are warmed");
        return await WarmAsync(provider, account, clock.GetUtcNow(), ct);
    }

    private async Task<WarmupRunDto> WarmAsync(Provider provider, ProviderAccount account, DateTimeOffset now, CancellationToken ct)
    {
        WarmupOutcome outcome;
        if (SkipReason(provider, account, now) is { } skip)
        {
            outcome = new WarmupOutcome("skipped", skip);
        }
        else
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromMinutes(3));
                outcome = await sender.SendAsync(provider, account, deadline.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // never loud: it is logged, kept on the account and left at that
                outcome = new WarmupOutcome("failed", e.Message);
            }
        }
        if (outcome.Result == "failed") logger.LogWarning("Warm-up of {Provider} / {Account} failed: {Message}", provider.Name, account.DisplayName, outcome.Message);
        else logger.LogInformation("Warm-up of {Provider} / {Account}: {Result} {Message}", provider.Name, account.DisplayName, outcome.Result, outcome.Message);
        await RecordAsync(account.Id, outcome, now, ct);
        return new WarmupRunDto(account.Id, outcome.Result, outcome.Message, now);
    }

    private async Task RecordAsync(string accountId, WarmupOutcome outcome, DateTimeOffset at, CancellationToken ct)
    {
        // read again right before writing: the quota sync writes the same column
        var fresh = await db.Accounts.GetAsync(accountId, ct);
        if (fresh is null) return;
        var node = fresh.Extra["warmup"] as JsonObject ?? [];
        var last = new JsonObject { ["at"] = at.ToString("o"), ["result"] = outcome.Result };
        if (outcome.Message is not null) last["message"] = outcome.Message;
        node["last"] = last;
        fresh.Extra["warmup"] = node;
        await db.Accounts.UpdateAsync(fresh, ct);
    }

    // ------------------------------------------------------------------ the accounts

    public async Task<List<WarmupAccountDto>> ListAsync(CancellationToken ct)
    {
        var list = new List<WarmupAccountDto>();
        var now = clock.GetUtcNow();
        var zone = WarmupSchedule.ZoneOf(settings.Current);
        foreach (var provider in await db.Providers.ListAsync(ct))
        {
            if (FamilyOf(provider) is not { } family) continue;
            foreach (var account in await db.Accounts.ListAsync(provider.Id, ct))
            {
                if (account.Kind is not (AccountKinds.OAuth or AccountKinds.ClaudeBridge)) continue;
                var own = account.Extra["warmup"] as JsonObject;
                var plan = PlanOf(provider, account);
                var (at, result, message) = LastOf(account);
                var next = plan is { Mode: WarmupSettings.ModeDaily } ? WarmupSchedule.NextSlot(plan, now, zone) : null;
                list.Add(new WarmupAccountDto(provider.Id, provider.Name, account.Id, account.DisplayName, family, ModeOf(account), Strings(own, "times"), Ints(own, "weekdays"),
                    at, result, message, next, plan is not null));
            }
        }
        return list;
    }

    public async Task<WarmupAccountDto> PatchAccountAsync(string accountId, WarmupAccountPatch patch, CancellationToken ct)
    {
        if (patch.Mode is { } mode && mode != Inherit && !WarmupSettings.Modes.Contains(mode)) throw new AdminApiException(400, "mode must be inherit, daily, auto or off");
        if (patch.Times is { } times && times.Any(t => !WarmupSchedule.TryParseTime(t, out _))) throw new AdminApiException(400, "a time of day must look like 06:00");
        if (patch.Weekdays is { } days && days.Any(d => d is < 0 or > 6)) throw new AdminApiException(400, "weekdays are 0 (Sunday) to 6 (Saturday)");
        var account = await db.Accounts.GetAsync(accountId, ct) ?? throw new AdminApiException(404, "no such account");
        var provider = await db.Providers.GetAsync(account.ProviderId, ct) ?? throw new AdminApiException(404, "no such provider");
        if (FamilyOf(provider) is null) throw new AdminApiException(409, "only Claude and ChatGPT (Codex) subscription accounts are warmed");
        var node = account.Extra["warmup"] as JsonObject ?? [];
        if (patch.Mode is { } m)
        {
            if (m == Inherit) node.Remove("mode");
            else node["mode"] = m;
        }
        if (patch.Times is { } t)
        {
            if (t.Count == 0) node.Remove("times");
            else node["times"] = new JsonArray(t.Distinct().Order().Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        }
        if (patch.Weekdays is { } d)
        {
            if (d.Count == 0) node.Remove("weekdays");
            else node["weekdays"] = new JsonArray(d.Distinct().Order().Select(x => (JsonNode)JsonValue.Create(x)).ToArray());
        }
        account.Extra["warmup"] = node;
        await db.Accounts.UpdateAsync(account, ct);
        return (await ListAsync(ct)).First(a => a.AccountId == accountId);
    }
}

/// <summary>The real sender: Claude bridge accounts through the <c>claude</c> program, the others over the network with the account's own token.</summary>
public sealed class WarmupSender(AstraDatabase db, ProviderProbe probe, ClaudeBridgeAdmin bridge, GatewayHttpClients http, IRequestSink sink) : IWarmupSender
{
    public async Task<WarmupOutcome> SendAsync(Provider provider, ProviderAccount account, CancellationToken ct)
    {
        var models = (await db.Providers.ListModelsAsync(provider.Id, ct)).Where(m => m.Enabled).Select(m => m.ModelId).ToList();
        var family = WarmupService.FamilyOf(provider);
        var model = PickModel(family, models);
        if (account.IsClaudeBridge)
        {
            var record = new RequestRecord
            {
                Id = Ulid.NewUlid(), StartedAtUtc = DateTimeOffset.UtcNow, ClientKind = ProviderProbe.WarmupClientKind, TokenName = "Warm-up", ProviderId = provider.Id,
                ProviderName = provider.Name, AccountId = account.Id, InboundProtocol = "anthropic", UpstreamProtocol = "anthropic", Passthrough = true,
                RequestedModel = "haiku", UpstreamModel = "haiku", UserAgent = "Astra warm-up (claude)", Status = RequestStatus.GatewayError,
            };
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await bridge.AskAsync(account, "haiku", "hi", ct);
                record.Status = RequestStatus.Success;
                return WarmupOutcome.Ok;
            }
            catch (Astra.Gateway.ClaudeBridge.ClaudeBridgeException e)
            {
                record.ErrorType = e.ErrorType;
                record.ErrorMessage = e.Message;
                record.HttpStatus = e.Status;
                return new WarmupOutcome("failed", e.Message);
            }
            finally
            {
                record.TotalMs = started.ElapsedMilliseconds;
                await sink.EnqueueAsync(record);
            }
        }
        if (model is null) return new WarmupOutcome("skipped", "the provider has no enabled model");
        var client = http.For(account.HttpProxy is { Length: > 0 } proxy ? proxy : provider.HttpProxy);
        var error = await probe.WarmAsync(provider, account, model, client, ct);
        return error is null ? WarmupOutcome.Ok : new WarmupOutcome("failed", error);
    }

    /// <summary>The cheapest model the provider has enabled, by name: a Claude one is Haiku, a Codex one a mini / nano / luna.</summary>
    internal static string? PickModel(string? family, IReadOnlyList<string> models)
    {
        string[] cheap = family == WarmupService.ClaudeFamily ? ["haiku"] : ["nano", "mini", "luna"];
        foreach (var word in cheap)
            if (models.FirstOrDefault(m => m.Contains(word, StringComparison.OrdinalIgnoreCase)) is { } hit) return hit;
        return models.FirstOrDefault();
    }
}

/// <summary>Looks at the warm-up schedule once a minute (after a short start-up delay).</summary>
public sealed class WarmupWorker(WarmupService warmup, ILogger<WarmupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            do
            {
                try
                {
                    await warmup.RunDueAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogWarning(e, "Warm-up round failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }
}

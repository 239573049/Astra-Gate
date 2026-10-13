using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Astra.Core;
using Astra.Core.Alerts;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Astra.Server.Hosting;

namespace Astra.Server.Alerts;

/// <summary>
/// The alert engine (plan: alerts-webhooks-token-limits §3): takes <see cref="AlertSignal"/>s (from the limiter, and from
/// its own sweep over providers, subscription accounts and limited tokens every <see cref="SweepInterval"/>), evaluates
/// every enabled rule of the signal's subject type and runs the per rule × subject state machine:
/// <list type="bullet">
/// <item>ok → firing: notify, unless the subject resolved less than <c>cooldown_minutes</c> ago (flapping);</item>
/// <item>firing → firing: notify again every <c>repeat_minutes</c> (when set);</item>
/// <item>firing → ok: notify "resolved" when the rule asks for it and the firing was notified.</item>
/// </list>
/// A condition the observation cannot decide keeps the state. A notification becomes one pending delivery per target
/// webhook (<see cref="WebhookDispatcher"/>). Signals are queued and evaluated on one background reader, so reporting
/// never blocks; with no enabled rule for a subject type, its signals are dropped on the spot.
/// </summary>
public sealed class AlertEngine(
    AstraDatabase db,
    SettingsService settings,
    ServerOptions server,
    TokenUsageLedger ledger,
    WebhookDispatcher dispatcher,
    ILogger<AlertEngine> logger,
    TimeProvider? timeProvider = null) : BackgroundService, IAlertSignals
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Channel<AlertSignal> _queue = Channel.CreateBounded<AlertSignal>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(string Rule, string Subject), AlertStateRecord> _states = new();
    private volatile List<CompiledRule>? _rules;

    private sealed record CompiledRule(AlertRuleRecord Rule, AlertEventDescriptor Event, JsonObject Condition, HashSet<string>? Subjects);

    public void Report(AlertSignal signal)
    {
        // Loaded rules and none for this subject type: nothing could fire.
        if (_rules is { } rules && !rules.Any(r => r.Event.SubjectType == signal.SubjectType)) return;
        _queue.Writer.TryWrite(signal);
    }

    /// <summary>Rules or webhooks changed: reload rules and states before the next signal.</summary>
    public void Invalidate() => _rules = null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = Task.Run(() => ReadAsync(stoppingToken), stoppingToken);
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "告警巡检出错");
                }
                await Task.Delay(SweepInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        _queue.Writer.TryComplete();
        await reader.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private async Task ReadAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var signal in _queue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await ProcessAsync(signal, CancellationToken.None);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "告警信号 {Type}/{Subject} 处理出错", signal.SubjectType, signal.SubjectId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Reports what every subject looks like right now, from stored data only (no upstream call): providers' balance
    /// snapshots, subscription accounts' status / quota / sync failures, and the budgets of tokens with limits. Processes
    /// the signals before returning (tests call this directly).
    /// </summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        var rules = await RulesAsync(ct);
        var types = rules.Select(r => r.Event.SubjectType).ToHashSet();
        if (types.Count == 0) return;
        var signals = new List<AlertSignal>();
        if (types.Contains(AlertSubjectTypes.Provider) || types.Contains(AlertSubjectTypes.Account))
        {
            var providers = await db.Providers.ListAsync(ct);
            if (types.Contains(AlertSubjectTypes.Provider))
                foreach (var p in providers.Where(p => !AuthSchemes.UsesAccounts(p.AuthScheme) && p.Quota is not null))
                    signals.Add(new AlertSignal(AlertSubjectTypes.Provider, p.Id, p.Name, new JsonObject { ["quota"] = p.Quota!.DeepClone() }));
            if (types.Contains(AlertSubjectTypes.Account))
            {
                var names = providers.ToDictionary(p => p.Id, p => p.Name);
                foreach (var a in await db.Accounts.ListAsync(ct: ct))
                {
                    if (!a.Enabled || !names.TryGetValue(a.ProviderId, out var providerName)) continue;
                    var sync = a.Extra[SubscriptionQuotaSync.SyncKey] as JsonObject;
                    signals.Add(new AlertSignal(AlertSubjectTypes.Account, a.Id, AccountName(a, providerName), new JsonObject
                    {
                        ["status"] = a.Status,
                        ["quota"] = a.Extra["quota"]?.DeepClone(),
                        ["failures"] = SubscriptionQuotaSync.Failures(a),
                        ["error"] = sync?["error"]?.DeepClone(),
                    }));
                }
            }
        }
        if (types.Contains(AlertSubjectTypes.Token))
        {
            foreach (var token in await db.Tokens.ListAsync(ct))
            {
                if (token.Limits() is not { Budgets.Count: > 0 } limits) continue;
                signals.Add(new AlertSignal(AlertSubjectTypes.Token, token.Id, token.Name,
                    TokenLimiter.Observation(await ledger.StatusAsync(token.Id, limits, ct))));
            }
        }
        foreach (var signal in signals) await ProcessAsync(signal, ct);
    }

    public static string AccountName(ProviderAccount account, string providerName) =>
        string.IsNullOrWhiteSpace(account.DisplayName) ? $"{providerName} / {account.Id}" : $"{providerName} / {account.DisplayName}";

    /// <summary>Evaluates one signal against the matching rules (serialized: the state machine is not re-entrant).</summary>
    public async Task ProcessAsync(AlertSignal signal, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var notified = false;
            foreach (var compiled in await RulesAsync(ct))
            {
                if (compiled.Event.SubjectType != signal.SubjectType) continue;
                if (compiled.Subjects is { } subjects && !subjects.Contains(signal.SubjectId)) continue;
                if (AlertConditions.Evaluate(compiled.Rule.Event, compiled.Condition, signal.Observation) is not { } evaluation) continue;
                notified |= await StepAsync(compiled, signal, evaluation, ct);
            }
            if (notified) dispatcher.Wake();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> StepAsync(CompiledRule compiled, AlertSignal signal, AlertEvaluation evaluation, CancellationToken ct)
    {
        var rule = compiled.Rule;
        var now = _clock.GetUtcNow();
        var key = (rule.Id, signal.SubjectId);
        var known = _states.TryGetValue(key, out var state);
        state ??= new AlertStateRecord { RuleId = rule.Id, SubjectId = signal.SubjectId, State = AlertStates.Ok, ChangedAt = DateTimeOffset.MinValue };
        state.SubjectName = signal.SubjectName;
        string? notify = null;
        var repeat = false;
        var changed = false;

        if (evaluation.Firing && state.State == AlertStates.Ok)
        {
            var flapping = known && state.LastNotifiedAt is not null && now - state.ChangedAt < TimeSpan.FromMinutes(rule.CooldownMinutes);
            state.State = AlertStates.Firing;
            state.ChangedAt = now;
            changed = true;
            if (!flapping) notify = "firing";
        }
        else if (evaluation.Firing)
        {
            var last = state.LastNotifiedAt is { } n && n >= state.ChangedAt ? n : (DateTimeOffset?)null;
            if (rule.RepeatMinutes is { } every and > 0 && last is { } sent && now - sent >= TimeSpan.FromMinutes(every))
            {
                notify = "firing";
                repeat = true;
            }
        }
        else if (state.State == AlertStates.Firing)
        {
            // Only a firing that was itself notified gets a "resolved".
            var wasNotified = state.LastNotifiedAt is { } n && n >= state.ChangedAt;
            state.State = AlertStates.Ok;
            state.ChangedAt = now;
            changed = true;
            if (rule.NotifyResolved && wasNotified) notify = "resolved";
        }

        state.ValueJson = evaluation.Observed.ToJsonString();
        if (notify is not null) state.LastNotifiedAt = now;
        _states[key] = state;
        if (changed || notify is not null) await db.Alerts.UpsertStateAsync(state, ct);
        if (notify is null) return false;

        var s = settings.Current;
        var message = AlertMessages.Format(s.Locale, rule.Event, notify, signal.SubjectName, evaluation.Observed, compiled.Condition);
        foreach (var webhookId in rule.WebhookIds)
        {
            var id = Ulid.NewUlid();
            await db.Alerts.InsertDeliveryAsync(new WebhookDeliveryRecord
            {
                Id = id,
                WebhookId = webhookId,
                RuleId = rule.Id,
                Event = rule.Event,
                Status = WebhookDeliveryStatus.Pending,
                PayloadJson = WebhookDispatcher.Payload(id, rule.Event, notify, repeat, now, rule, signal.SubjectType, signal.SubjectId,
                    signal.SubjectName, compiled.Condition, evaluation.Observed, message, server.GatewayBaseUrl),
                CreatedAt = now,
            }, ct);
        }
        logger.LogInformation("告警 {Rule}：{Message}", rule.Name, message);
        return rule.WebhookIds.Count > 0;
    }

    private async Task<List<CompiledRule>> RulesAsync(CancellationToken ct)
    {
        if (_rules is { } cached) return cached;
        var compiled = new List<CompiledRule>();
        foreach (var rule in await db.Alerts.ListRulesAsync(ct))
        {
            if (!rule.Enabled || AlertEvents.Find(rule.Event) is not { } descriptor) continue;
            JsonObject condition;
            try
            {
                condition = AlertConditions.Normalize(rule.Event, rule.ConditionJson is null ? null : JsonNode.Parse(rule.ConditionJson) as JsonObject);
            }
            catch (ArgumentException e)
            {
                logger.LogWarning("告警规则 {Rule} 的条件无效，已跳过：{Error}", rule.Name, e.Message);
                continue;
            }
            var subjects = rule.SubjectsJson is null ? null : Json.Deserialize<List<string>>(rule.SubjectsJson)?.ToHashSet();
            compiled.Add(new CompiledRule(rule, descriptor, condition, subjects));
        }
        _states.Clear();
        foreach (var state in await db.Alerts.ListStatesAsync(ct)) _states[(state.RuleId, state.SubjectId)] = state;
        _rules = compiled;
        return compiled;
    }
}

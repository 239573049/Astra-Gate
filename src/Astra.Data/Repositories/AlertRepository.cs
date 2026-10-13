using Dapper;
using Astra.Core.Alerts;

namespace Astra.Data.Repositories;

/// <summary>Row of <c>alert_rule_webhooks</c> (namespace level: Dapper.AOT does not intercept private nested row types).</summary>
internal sealed class AlertRuleWebhookRow
{
    public string RuleId { get; set; } = "";
    public string WebhookId { get; set; } = "";
}

/// <summary>
/// Webhooks, alert rules, alert states and webhook deliveries (migration 0014; plan: alerts-webhooks-token-limits §3).
/// </summary>
public sealed class AlertRepository
{
    private readonly SqliteConnectionFactory _factory;

    public AlertRepository(SqliteConnectionFactory factory) => _factory = factory;

    private const string WebhookSelect = """
        SELECT id, name, url, format, secret_enc, headers_enc, enabled, timeout_sec, use_proxy, created_at, updated_at FROM webhooks
        """;

    private const string RuleSelect = """
        SELECT id, name, enabled, event, subjects_json, condition_json, notify_resolved, repeat_minutes, cooldown_minutes,
               created_at, updated_at
        FROM alert_rules
        """;

    private const string StateSelect = """
        SELECT rule_id, subject_id, subject_name, state, value_json, changed_at, last_notified_at FROM alert_states
        """;

    private const string DeliverySelect = """
        SELECT id, webhook_id, rule_id, event, status, attempts, next_attempt_at, http_status, error, response_snippet,
               payload_json, created_at, finished_at
        FROM webhook_deliveries
        """;

    // ------------------------------------------------------------------ webhooks

    public async Task<IReadOnlyList<WebhookRecord>> ListWebhooksAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<WebhookRecord>($"{WebhookSelect} ORDER BY created_at, id")).ToList();
    }

    public async Task<WebhookRecord?> GetWebhookAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WebhookRecord>($"{WebhookSelect} WHERE id = @id", new { id });
    }

    public async Task InsertWebhookAsync(WebhookRecord w, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO webhooks(id, name, url, format, secret_enc, headers_enc, enabled, timeout_sec, use_proxy, created_at, updated_at)
            VALUES (@id, @name, @url, @format, @secret_enc, @headers_enc, @enabled, @timeout_sec, @use_proxy, @created_at, @updated_at)
            """, new
        {
            id = w.Id,
            name = w.Name,
            url = w.Url,
            format = w.Format,
            secret_enc = w.SecretEnc,
            headers_enc = w.HeadersEnc,
            enabled = w.Enabled,
            timeout_sec = w.TimeoutSec,
            use_proxy = w.UseProxy,
            created_at = DateTimeOffsetHandler.ToStorage(w.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(w.UpdatedAt),
        });
    }

    public async Task<bool> UpdateWebhookAsync(WebhookRecord w, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE webhooks SET name = @name, url = @url, format = @format, secret_enc = @secret_enc, headers_enc = @headers_enc,
                                enabled = @enabled, timeout_sec = @timeout_sec, use_proxy = @use_proxy, updated_at = @updated_at
            WHERE id = @id
            """, new
        {
            id = w.Id,
            name = w.Name,
            url = w.Url,
            format = w.Format,
            secret_enc = w.SecretEnc,
            headers_enc = w.HeadersEnc,
            enabled = w.Enabled,
            timeout_sec = w.TimeoutSec,
            use_proxy = w.UseProxy,
            created_at = DateTimeOffsetHandler.ToStorage(w.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(w.UpdatedAt),
        }) > 0;
    }

    /// <summary>Deletes a webhook; its rule links and deliveries cascade.</summary>
    public async Task<bool> DeleteWebhookAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM webhooks WHERE id = @id", new { id }) > 0;
    }

    // ------------------------------------------------------------------ rules

    /// <summary>Every rule with its target webhooks, oldest first.</summary>
    public async Task<IReadOnlyList<AlertRuleRecord>> ListRulesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rules = (await conn.QueryAsync<AlertRuleRecord>($"{RuleSelect} ORDER BY created_at, id")).ToList();
        var links = (await conn.QueryAsync<AlertRuleWebhookRow>("SELECT rule_id, webhook_id FROM alert_rule_webhooks ORDER BY rowid")).ToList();
        foreach (var rule in rules) rule.WebhookIds = links.Where(l => l.RuleId == rule.Id).Select(l => l.WebhookId).ToList();
        return rules;
    }

    public async Task<AlertRuleRecord?> GetRuleAsync(string id, CancellationToken ct = default) =>
        (await ListRulesAsync(ct)).FirstOrDefault(r => r.Id == id);

    public async Task InsertRuleAsync(AlertRuleRecord rule, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO alert_rules(id, name, enabled, event, subjects_json, condition_json, notify_resolved, repeat_minutes,
                                    cooldown_minutes, created_at, updated_at)
            VALUES (@id, @name, @enabled, @event_name, @subjects_json, @condition_json, @notify_resolved, @repeat_minutes,
                    @cooldown_minutes, @created_at, @updated_at)
            """, new
        {
            id = rule.Id,
            name = rule.Name,
            enabled = rule.Enabled,
            event_name = rule.Event,
            subjects_json = rule.SubjectsJson,
            condition_json = rule.ConditionJson,
            notify_resolved = rule.NotifyResolved,
            repeat_minutes = rule.RepeatMinutes,
            cooldown_minutes = rule.CooldownMinutes,
            created_at = DateTimeOffsetHandler.ToStorage(rule.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(rule.UpdatedAt),
        }, tx);
        await LinkAsync(conn, tx, rule);
        await tx.CommitAsync(ct);
    }

    /// <summary>Saves a rule and replaces its webhook links. A changed condition, event or subject list drops its states.</summary>
    public async Task<bool> UpdateRuleAsync(AlertRuleRecord rule, bool resetStates, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var updated = await conn.ExecuteAsync("""
            UPDATE alert_rules SET name = @name, enabled = @enabled, event = @event_name, subjects_json = @subjects_json,
                                   condition_json = @condition_json, notify_resolved = @notify_resolved,
                                   repeat_minutes = @repeat_minutes, cooldown_minutes = @cooldown_minutes, updated_at = @updated_at
            WHERE id = @id
            """, new
        {
            id = rule.Id,
            name = rule.Name,
            enabled = rule.Enabled,
            event_name = rule.Event,
            subjects_json = rule.SubjectsJson,
            condition_json = rule.ConditionJson,
            notify_resolved = rule.NotifyResolved,
            repeat_minutes = rule.RepeatMinutes,
            cooldown_minutes = rule.CooldownMinutes,
            created_at = DateTimeOffsetHandler.ToStorage(rule.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(rule.UpdatedAt),
        }, tx) > 0;
        if (updated)
        {
            await conn.ExecuteAsync("DELETE FROM alert_rule_webhooks WHERE rule_id = @id", new { id = rule.Id }, tx);
            await LinkAsync(conn, tx, rule);
            if (resetStates) await conn.ExecuteAsync("DELETE FROM alert_states WHERE rule_id = @id", new { id = rule.Id }, tx);
        }
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<bool> DeleteRuleAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM alert_rules WHERE id = @id", new { id }) > 0;
    }

    private static async Task LinkAsync(System.Data.Common.DbConnection conn, System.Data.Common.DbTransaction tx, AlertRuleRecord rule)
    {
        foreach (var webhookId in rule.WebhookIds.Distinct())
            await conn.ExecuteAsync("INSERT INTO alert_rule_webhooks(rule_id, webhook_id) VALUES (@rule_id, @webhook_id)",
                new { rule_id = rule.Id, webhook_id = webhookId }, tx);
    }

    // ------------------------------------------------------------------ states

    public async Task<IReadOnlyList<AlertStateRecord>> ListStatesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<AlertStateRecord>($"{StateSelect} ORDER BY changed_at DESC")).ToList();
    }

    public async Task UpsertStateAsync(AlertStateRecord s, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        // The rule may have been deleted meanwhile: then there is nothing to attach the state to.
        await conn.ExecuteAsync("""
            INSERT INTO alert_states(rule_id, subject_id, subject_name, state, value_json, changed_at, last_notified_at)
            SELECT @rule_id, @subject_id, @subject_name, @state, @value_json, @changed_at, @last_notified_at
            WHERE EXISTS (SELECT 1 FROM alert_rules WHERE id = @rule_id)
            ON CONFLICT(rule_id, subject_id) DO UPDATE SET
                subject_name = excluded.subject_name, state = excluded.state, value_json = excluded.value_json,
                changed_at = excluded.changed_at, last_notified_at = excluded.last_notified_at
            """, new
        {
            rule_id = s.RuleId,
            subject_id = s.SubjectId,
            subject_name = s.SubjectName,
            state = s.State,
            value_json = s.ValueJson,
            changed_at = DateTimeOffsetHandler.ToStorage(s.ChangedAt),
            last_notified_at = s.LastNotifiedAt is { } at ? DateTimeOffsetHandler.ToStorage(at) : null,
        });
    }

    public async Task DeleteStateAsync(string ruleId, string subjectId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM alert_states WHERE rule_id = @ruleId AND subject_id = @subjectId", new { ruleId, subjectId });
    }

    // ------------------------------------------------------------------ deliveries

    public async Task InsertDeliveryAsync(WebhookDeliveryRecord d, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO webhook_deliveries(id, webhook_id, rule_id, event, status, attempts, next_attempt_at, http_status, error,
                                           response_snippet, payload_json, created_at, finished_at)
            SELECT @id, @webhook_id, @rule_id, @event_name, @status, @attempts, @next_attempt_at, @http_status, @error,
                   @response_snippet, @payload_json, @created_at, @finished_at
            WHERE EXISTS (SELECT 1 FROM webhooks WHERE id = @webhook_id)
            """, new
        {
            id = d.Id,
            webhook_id = d.WebhookId,
            rule_id = d.RuleId,
            event_name = d.Event,
            status = d.Status,
            attempts = d.Attempts,
            next_attempt_at = d.NextAttemptAt is { } next ? DateTimeOffsetHandler.ToStorage(next) : null,
            http_status = d.HttpStatus,
            error = d.Error,
            response_snippet = d.ResponseSnippet,
            payload_json = d.PayloadJson,
            created_at = DateTimeOffsetHandler.ToStorage(d.CreatedAt),
            finished_at = d.FinishedAt is { } done ? DateTimeOffsetHandler.ToStorage(done) : null,
        });
    }

    public async Task UpdateDeliveryAsync(WebhookDeliveryRecord d, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE webhook_deliveries SET status = @status, attempts = @attempts, next_attempt_at = @next_attempt_at,
                                          http_status = @http_status, error = @error, response_snippet = @response_snippet,
                                          finished_at = @finished_at
            WHERE id = @id
            """, new
        {
            id = d.Id,
            webhook_id = d.WebhookId,
            rule_id = d.RuleId,
            event_name = d.Event,
            status = d.Status,
            attempts = d.Attempts,
            next_attempt_at = d.NextAttemptAt is { } next ? DateTimeOffsetHandler.ToStorage(next) : null,
            http_status = d.HttpStatus,
            error = d.Error,
            response_snippet = d.ResponseSnippet,
            payload_json = d.PayloadJson,
            created_at = DateTimeOffsetHandler.ToStorage(d.CreatedAt),
            finished_at = d.FinishedAt is { } done ? DateTimeOffsetHandler.ToStorage(done) : null,
        });
    }

    public async Task<WebhookDeliveryRecord?> GetDeliveryAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<WebhookDeliveryRecord>($"{DeliverySelect} WHERE id = @id", new { id });
    }

    /// <summary>Pending deliveries whose next attempt is due, oldest first.</summary>
    public async Task<IReadOnlyList<WebhookDeliveryRecord>> ListDueDeliveriesAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<WebhookDeliveryRecord>($"""
            {DeliverySelect}
            WHERE status = 'pending' AND (next_attempt_at IS NULL OR next_attempt_at <= @now)
            ORDER BY created_at LIMIT @limit
            """, new { now = DateTimeOffsetHandler.ToStorage(now), limit })).ToList();
    }

    /// <summary>A webhook's deliveries, newest first.</summary>
    public async Task<IReadOnlyList<WebhookDeliveryRecord>> ListDeliveriesAsync(string webhookId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<WebhookDeliveryRecord>($"""
            {DeliverySelect} WHERE webhook_id = @webhookId ORDER BY created_at DESC, id DESC LIMIT @limit
            """, new { webhookId, limit })).ToList();
    }

    /// <summary>Drops deliveries finished before <paramref name="before"/> and all but the newest <paramref name="keep"/> per webhook.</summary>
    public async Task<int> PruneDeliveriesAsync(DateTimeOffset before, int keep, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var old = await conn.ExecuteAsync("DELETE FROM webhook_deliveries WHERE status <> 'pending' AND created_at < @before",
            new { before = DateTimeOffsetHandler.ToStorage(before) });
        var surplus = await conn.ExecuteAsync("""
            DELETE FROM webhook_deliveries WHERE id IN (
                SELECT id FROM (
                    SELECT id, ROW_NUMBER() OVER (PARTITION BY webhook_id ORDER BY created_at DESC, id DESC) AS n
                    FROM webhook_deliveries)
                WHERE n > @keep)
            """, new { keep });
        return old + surplus;
    }

}

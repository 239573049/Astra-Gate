using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Alerts;
using Astra.Core.Clients;
using Astra.Data;

namespace Astra.Server.Alerts;

/// <summary>
/// Sends webhook deliveries (plan: alerts-webhooks-token-limits §3.5). Deliveries are rows first (<c>pending</c>), so a
/// restart resumes them; each attempt POSTs the payload with <c>X-Astra-Event</c> / <c>-Delivery</c> / <c>-Timestamp</c>
/// and, with a secret, <c>X-Astra-Signature: sha256=hex(HMAC_SHA256(secret, "&lt;timestamp&gt;.&lt;body&gt;"))</c>. Only 2xx
/// counts; redirects are not followed (and count as failures). Up to <see cref="MaxAttempts"/> attempts, then failed.
/// Old deliveries are pruned hourly. Neither secrets nor custom header values are ever logged. Bot formats (Feishu,
/// DingTalk, …) are rendered and signed by <see cref="WebhookFormatter"/> at send time; their 200-with-error-code
/// answers count as failures.
/// </summary>
public sealed class WebhookDispatcher(
    AstraDatabase db,
    ISecretProtector secrets,
    IHttpClientFactory httpClients,
    Gateway.Pipeline.SettingsService settings,
    ILogger<WebhookDispatcher> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    /// <summary>Named client following the global proxy setting (no automatic redirects).</summary>
    public const string HttpClientName = "astra-webhook";

    /// <summary>Named client that never uses a proxy.</summary>
    public const string DirectHttpClientName = "astra-webhook-direct";

    public const int MaxAttempts = 4;
    public const int KeepPerWebhook = 1000;
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    /// <summary>Wait before attempt 2, 3 and 4.</summary>
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private const int MaxSnippetChars = 2048;
    private const int Concurrency = 4;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);
    private static readonly string UserAgent = $"astra/{typeof(WebhookDispatcher).Assembly.GetName().Version?.ToString(3) ?? "0"}";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>New deliveries are waiting: send them now rather than on the next idle tick.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // already signalled
            }
        }
    }

    /// <summary>A fresh webhook secret (shown once).</summary>
    public static string NewSecret() => "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>The signature header value for a body sent at <paramref name="timestamp"/> (unix seconds).</summary>
    public static string Sign(string secret, long timestamp, string body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}"))).ToLowerInvariant();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeliverDueAsync(stoppingToken);
                if (_clock.GetUtcNow() - _lastPrune > TimeSpan.FromHours(1))
                {
                    _lastPrune = _clock.GetUtcNow();
                    await db.Alerts.PruneDeliveriesAsync(_lastPrune - KeepFor, KeepPerWebhook, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Webhook 投递出错（下轮重试）");
            }

            try
            {
                await _wake.WaitAsync(Idle, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Sends every due pending delivery (a few at a time). Returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct)
    {
        var due = await db.Alerts.ListDueDeliveriesAsync(_clock.GetUtcNow(), 50, ct);
        if (due.Count == 0) return 0;
        var hooks = (await db.Alerts.ListWebhooksAsync(ct)).ToDictionary(h => h.Id);
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = ct },
            async (delivery, token) => await AttemptAsync(delivery, hooks.GetValueOrDefault(delivery.WebhookId), retry: true, token));
        return due.Count;
    }

    /// <summary>
    /// One attempt; the outcome is saved on the delivery. Without <paramref name="retry"/> (test sends) a failure is final.
    /// </summary>
    public async Task<WebhookDeliveryRecord> AttemptAsync(WebhookDeliveryRecord delivery, WebhookRecord? hook, bool retry, CancellationToken ct)
    {
        delivery.Attempts++;
        var now = _clock.GetUtcNow();
        if (hook is not { Enabled: true })
        {
            Finish(delivery, WebhookDeliveryStatus.Failed, null, hook is null ? "webhook deleted" : "webhook disabled", null, now);
            await db.Alerts.UpdateDeliveryAsync(delivery, CancellationToken.None);
            return delivery;
        }

        int? status = null;
        string? error = null;
        string? snippet = null;
        try
        {
            using var request = BuildRequest(hook, delivery, now);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(hook.TimeoutSec, 1, 60)));
            var client = httpClients.CreateClient(hook.UseProxy ? HttpClientName : DirectHttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            snippet = await ReadSnippetAsync(response, timeout.Token);
            error = response.IsSuccessStatusCode ? WebhookFormatter.Rejection(hook.Format, snippet) : $"HTTP {status}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = "timeout";
        }
        catch (HttpRequestException e)
        {
            error = e.Message;
        }
        catch (InvalidOperationException e)
        {
            // A header value the HTTP stack refuses (or a malformed URL): retrying cannot help.
            error = e.Message;
            retry = false;
        }

        if (error is null)
            Finish(delivery, WebhookDeliveryStatus.Success, status, null, snippet, now);
        else if (retry && delivery.Attempts < MaxAttempts)
        {
            delivery.Status = WebhookDeliveryStatus.Pending;
            delivery.HttpStatus = status;
            delivery.Error = error;
            delivery.ResponseSnippet = snippet;
            delivery.NextAttemptAt = now + Backoff[Math.Min(delivery.Attempts - 1, Backoff.Length - 1)];
        }
        else
            Finish(delivery, WebhookDeliveryStatus.Failed, status, error, snippet, now);
        if (error is not null) logger.LogInformation("Webhook {Webhook} 投递 {Delivery} 失败（第 {Attempt} 次）：{Error}", hook.Name, delivery.Id, delivery.Attempts, error);
        await db.Alerts.UpdateDeliveryAsync(delivery, CancellationToken.None);
        return delivery;
    }

    private HttpRequestMessage BuildRequest(WebhookRecord hook, WebhookDeliveryRecord delivery, DateTimeOffset now)
    {
        var secret = Secret(hook);
        var (url, body) = WebhookFormatter.Render(hook.Format, hook.Url, delivery.PayloadJson, secret, now, settings.Current.Locale);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in CustomHeaders(hook)) request.Headers.TryAddWithoutValidation(name, value);
        var timestamp = now.ToUnixTimeSeconds();
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.Add(ProductInfoHeaderValue.Parse(UserAgent));
        request.Headers.TryAddWithoutValidation("X-Astra-Event", delivery.Event);
        request.Headers.TryAddWithoutValidation("X-Astra-Delivery", delivery.Id);
        request.Headers.TryAddWithoutValidation("X-Astra-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        if (hook.Format == WebhookFormats.Generic && secret is not null)
            request.Headers.TryAddWithoutValidation("X-Astra-Signature", Sign(secret, timestamp, body));
        return request;
    }

    /// <summary>The decrypted secret; null when there is none (or the key ring lost it).</summary>
    public string? Secret(WebhookRecord hook)
    {
        if (hook.SecretEnc is null) return null;
        try
        {
            return secrets.Unprotect(hook.SecretEnc);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The decrypted custom headers (empty when none or unreadable).</summary>
    public Dictionary<string, string> CustomHeaders(WebhookRecord hook)
    {
        if (hook.HeadersEnc is null) return [];
        try
        {
            return Json.Deserialize<Dictionary<string, string>>(secrets.Unprotect(hook.HeadersEnc)) ?? [];
        }
        catch (CryptographicException)
        {
            return [];
        }
    }

    private static async Task<string?> ReadSnippetAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[MaxSnippetChars];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
            return read == 0 ? null : new string(buffer, 0, read);
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return null;
        }
    }

    private static void Finish(WebhookDeliveryRecord d, string status, int? httpStatus, string? error, string? snippet, DateTimeOffset now)
    {
        d.Status = status;
        d.HttpStatus = httpStatus;
        d.Error = error;
        d.ResponseSnippet = snippet;
        d.NextAttemptAt = null;
        d.FinishedAt = now;
    }

    /// <summary>The payload every delivery carries (camelCase); <paramref name="id"/> is the delivery's id.</summary>
    public static string Payload(string id, string eventId, string status, bool repeat, DateTimeOffset at, AlertRuleRecord? rule,
        string subjectType, string subjectId, string subjectName, JsonObject condition, JsonObject observed, string message, string baseUrl)
    {
        var body = new JsonObject
        {
            ["id"] = id,
            ["event"] = eventId,
            ["status"] = status,
            ["repeat"] = repeat,
            ["occurredAt"] = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            ["rule"] = rule is null ? null : new JsonObject { ["id"] = rule.Id, ["name"] = rule.Name },
            ["subject"] = new JsonObject { ["type"] = subjectType, ["id"] = subjectId, ["name"] = subjectName },
            ["condition"] = condition.DeepClone(),
            ["observed"] = observed.DeepClone(),
            ["message"] = message,
            ["astra"] = new JsonObject { ["version"] = UserAgent["astra/".Length..], ["url"] = baseUrl },
        };
        return body.ToJsonString(Gateway.Protocol.GatewayJson.Options);
    }
}

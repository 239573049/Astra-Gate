using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Core.Routing;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>/api/requests, /api/stats and /api/settings.</summary>
public static class RequestEndpoints
{




    /// <summary>
    /// How much of each captured body the request detail carries. A part can be 8 MB (BodyStore.MaxBytes), and the
    /// detail used to send all five whole: tens of MB of JSON per open, which the UI then parsed, pretty-printed and
    /// highlighted.
    /// </summary>
    internal const int BodyPreviewBytes = 64 * 1024;



    /// <summary>Status reported for a request the gateway is still handling (never stored).</summary>
    public const string PendingStatus = "pending";










    public static void MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/requests", async (HttpContext http, AstraDatabase db, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var query = new RequestQuery
            {
                From = ParseTime(q["from"]),
                To = ParseTime(q["to"]),
                ClientKind = Str(q["client"]),
                TokenId = Str(q["token"]),
                ProviderId = Str(q["provider"]),
                Model = Str(q["model"]),
                Status = Str(q["status"]),
                Remote = Origin(q["origin"]),
                Page = int.TryParse(q["page"], out var page) ? Math.Max(1, page) : 1,
                PageSize = int.TryParse(q["pageSize"], out var size) ? Math.Clamp(size, 1, 500) : 50,
            };
            var result = await db.Requests.QueryAsync(query, ct);
            return Results.Ok(new RequestPageDto(result.Items.Select(ToSummary).ToList(), result.Total, result.Page, result.PageSize, result.TotalCostNanoUsd));
        });

        // Live request log (server-sent events): the in-flight snapshot first, then changes as they happen (coalesced
        // per request, a batch every LiveRequestFeed.FlushInterval). The feed only records while such a stream is open.
        // A comment line every 15 s keeps idle connections (and proxies in front of them) open.
        app.MapGet("/api/requests/live", async (HttpContext ctx, LiveRequestFeed live, IHostApplicationLifetime lifetime) =>
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, lifetime.ApplicationStopping);
            var ct = cts.Token;
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            var gate = new SemaphoreSlim(1, 1);
            using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var beats = Task.Run(async () =>
            {
                while (await heartbeat.WaitForNextTickAsync(ct)) await WriteAsync(": ping\n\n");
            }, ct);
            try
            {
                await WriteAsync(": connected\n\n");
                // One write and flush per batch; a batch is serialized once and shared by every viewer.
                await foreach (var batch in live.SubscribeAsync(ct))
                    await WriteAsync(batch.Format(FormatLiveBatch));
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Viewer left or the server is stopping.
            }
            finally
            {
                await cts.CancelAsync();
                try { await beats; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
            }
            return Results.Empty;

            async Task WriteAsync(string text)
            {
                await gate.WaitAsync(ct);
                try
                {
                    await ctx.Response.WriteAsync(text, ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                finally
                {
                    gate.Release();
                }
            }
        });

        static string FormatLiveBatch(LiveRequestBatch batch)
        {
            var sb = new StringBuilder();
            foreach (var e in batch.Events)
                sb.Append(SseWriter.Format(new SseEvent(null, Json.SerializeApi(new RequestLiveEventDto(e.Type, ToSummary(e.Record, e.InFlight, live: true))))));
            return sb.ToString();
        }

        // A request still in flight (or not yet written) is served from the live feed, so its detail opens right away.
        app.MapGet("/api/requests/{id}", async (string id, AstraDatabase db, BodyStore bodies, LiveRequestFeed live, CancellationToken ct) =>
            live.Find(id) is { } e ? Results.Ok(ToDetail(e.Record, bodies, e.InFlight))
            : await db.Requests.GetAsync(id, ct) is { } r ? Results.Ok(ToDetail(r, bodies)) : ModelEndpoints.NotFound(id));

        // One whole captured part as text, streamed from its file (the detail carries only the start of each).
        app.MapGet("/api/requests/{id}/bodies/{part}", async (string id, string part, AstraDatabase db, BodyStore bodies,
            LiveRequestFeed live, CancellationToken ct) =>
        {
            if (!BodyStore.Parts.Contains(part)) return ModelEndpoints.NotFound(part);
            var bodyRef = live.Find(id) is { } e ? e.Record.BodyRef : (await db.Requests.GetAsync(id, ct))?.BodyRef;
            var file = string.IsNullOrEmpty(bodyRef) ? null : bodies.FileFor(bodyRef, part);
            return file is null ? ModelEndpoints.NotFound($"{id}/{part}") : Results.File(file, "text/plain; charset=utf-8");
        });

        // Stats accept optional `client` (client kind) and `token` (token id) filters; omitted means every request.
        app.MapGet("/api/stats/summary", async (string? range, string? client, string? token, AstraDatabase db, CancellationToken ct) =>
        {
            var (name, from, to) = Range(range);
            var s = await db.Requests.SummaryAsync(from, to, Str(client), Str(token), ct);
            return Results.Ok(new StatsSummaryDto(name, Money.FromNanos(s.TotalCostNanoUsd), s.TotalRequests, s.SuccessRate,
                s.TotalInputTokens, s.TotalOutputTokens, s.TotalCacheReadTokens, s.TotalCacheWriteTokens, s.TotalReasoningTokens, s.AvgTtftMs));
        });

        // tzOffset: the viewer's UTC offset in minutes (e.g. 480 for UTC+8), so buckets follow local days/hours.
        app.MapGet("/api/stats/timeseries", async (string? range, string? groupBy, string? by, string? client, string? token, int? tzOffset,
            AstraDatabase db, CancellationToken ct) =>
        {
            var (_, from, to) = Range(range);
            var bucket = groupBy is "hour" ? "hour" : "day";
            var dim = by is "provider" or "client" or "token" ? by : "model";
            var offset = Math.Clamp(tzOffset ?? 0, -14 * 60, 14 * 60);
            var points = await db.Requests.TimeseriesAsync(from, to, bucket, dim, Str(client), offset, Str(token), ct);
            return Results.Ok(points.Select(p => new TimeseriesPointDto(p.Bucket, p.Label ?? p.GroupKey ?? "unknown",
                Money.FromNanos(p.TotalCostNanoUsd), p.Requests, p.TotalInputTokens + p.TotalOutputTokens)).ToList());
        });

        app.MapGet("/api/stats/top-models", async (string? range, int? limit, string? client, string? token, AstraDatabase db, CancellationToken ct) =>
        {
            var (_, from, to) = Range(range);
            var top = await db.Requests.TopModelsAsync(from, to, Math.Clamp(limit ?? 10, 1, 100), Str(client), Str(token), ct);
            return Results.Ok(top.Select(t => new TopModelDto(t.ModelId, Money.FromNanos(t.TotalCostNanoUsd), t.Requests,
                t.TotalInputTokens + t.TotalOutputTokens, t.TotalInputTokens, t.TotalCacheReadTokens)).ToList());
        });

        // Per provider (a token's pool members, issue #2): answered requests, success, cost, TTFT and failovers.
        app.MapGet("/api/stats/providers", async (string? range, string? client, string? token, AstraDatabase db, CancellationToken ct) =>
        {
            var (_, from, to) = Range(range);
            var names = (await db.Providers.ListAsync(ct)).ToDictionary(p => p.Id, p => p.Name);
            var stats = await db.Requests.ProviderStatsAsync(from, to, Str(client), Str(token), ct);
            return Results.Ok(stats.Select(p => new ProviderStatDto(p.ProviderId, names.GetValueOrDefault(p.ProviderId) ?? p.ProviderName,
                p.Requests, p.SuccessRequests, p.Requests == 0 ? 0 : (double)p.SuccessRequests / p.Requests, Money.FromNanos(p.CostNanoUsd),
                p.InputTokens, p.OutputTokens, p.AvgTtftMs, p.FailoversOut, p.FailoversIn)).ToList());
        });

        // Activity heatmap: request counts per local day over a fixed window ("days", 30-730), newest day inclusive.
        app.MapGet("/api/stats/activity-heatmap", async (int? days, string? client, string? token, int? tzOffset,
            AstraDatabase db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.Now;
            var startOfToday = new DateTimeOffset(now.Date, now.Offset);
            var window = Math.Clamp(days ?? 365, 30, 730);
            var offset = Math.Clamp(tzOffset ?? 0, -14 * 60, 14 * 60);
            var activity = await db.Requests.DailyActivityAsync(
                startOfToday.AddDays(-(window - 1)).ToUniversalTime(), Str(client), offset, Str(token), ct);
            return Results.Ok(activity.Select(a => new DailyActivityDto(a.Day, a.Requests)).ToList());
        });

        // Live RPM / TPM for the request-log header: everything started in the trailing window ("window" seconds,
        // 10-600, default 60) — persisted rows plus in-flight requests (they are not stored yet) — normalized to
        // per minute. Filters mirror /api/requests.
        app.MapGet("/api/stats/rate", async (HttpContext http, AstraDatabase db, LiveRequestFeed live, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var windowSeconds = int.TryParse(q["window"], out var w) ? Math.Clamp(w, 10, 600) : 60;
            var now = DateTimeOffset.Now;
            var query = new RequestQuery
            {
                From = now.AddSeconds(-windowSeconds),
                To = now,
                ClientKind = Str(q["client"]),
                TokenId = Str(q["token"]),
                ProviderId = Str(q["provider"]),
                Model = Str(q["model"]),
                Status = Str(q["status"]),
                Remote = Origin(q["origin"]),
            };
            var rate = await db.Requests.RateAsync(query, ct);
            var requests = rate.Requests;
            if (string.IsNullOrEmpty(query.Status))
            {
                // In-flight rows count toward RPM the moment they arrive (they are visible in the live table above;
                // the feed only tracks them while that table is open).
                // They have no final status yet, so a status filter skips them entirely.
                requests += live.InFlightSnapshot().Count(r =>
                    r.StartedAtUtc >= query.From && MatchesWindowFilters(r, query));
            }
            var scale = 60.0 / windowSeconds;
            return Results.Ok(new RateStatsDto(requests * scale, rate.Tokens * scale,
                rate.InputTokens == 0 ? null : (double)rate.CacheReadTokens / rate.InputTokens, windowSeconds));
        });

        app.MapGet("/api/settings", (SettingsService settings, ServerOptions server, AstraPaths paths) =>
            Results.Ok(ToSettings(settings.Current, server, paths)));

        app.MapPatch("/api/settings", async (JsonObject patch, SettingsService settings, ServerOptions server, AstraPaths paths,
            ISecretProtector secrets, CancellationToken ct) =>
        {
            string? error = null;
            var next = await settings.UpdateAsync(s =>
            {
                var (previousProxyMode, previousProxyUrl) = (s.ProxyMode, s.ProxyUrl);
                foreach (var (key, value) in patch)
                {
                    switch (key)
                    {
                        case "locale":
                            var locale = value?.GetValue<string>();
                            if (locale is "zh" or "en") s.Locale = locale;
                            else error = "locale must be zh or en";
                            break;
                        case "requireGatewayKey": s.RequireGatewayKey = value?.GetValue<bool>() ?? true; break;
                        case "debugBodies": s.DebugBodies = value?.GetValue<bool>() ?? false; break;
                        case "traceContent": s.TraceContent = value?.GetValue<bool>() ?? false; break;
                        case "bodyRetentionDays": s.BodyRetentionDays = Math.Clamp(value?.GetValue<int>() ?? 7, 1, 3650); break;
                        case "requestRetentionDays":
                            s.RequestRetentionDays = value is null ? null : Math.Clamp(value.GetValue<int>(), 1, 36500);
                            break;
                        case "effortBudgets":
                            if (value is not null) s.EffortBudgets = Json.DeserializeApi<EffortBudgets>(value) ?? s.EffortBudgets;
                            break;
                        case "streamIdleTimeoutSec": s.StreamIdleTimeoutSec = Math.Clamp(value?.GetValue<int>() ?? 300, 10, 3600); break;
                        case "updateChannel":
                            if (UpdateChannels.Parse(value?.GetValue<string>()) is { } channel) s.UpdateChannel = channel;
                            else error = "updateChannel must be stable or preview";
                            break;
                        case "updateAutoCheck": s.UpdateAutoCheck = value?.GetValue<bool>() ?? true; break;
                        case "updateFeedUrl":
                            // JSON null resets to the built-in feed; "" disables checks entirely.
                            var feedUrl = value?.GetValue<string>();
                            s.UpdateFeedUrl = feedUrl is null ? null : feedUrl.Trim() is { Length: 0 } ? "" : feedUrl.Trim();
                            break;
                        case "pluginMarketUrl":
                            // JSON null resets to the Astra site's registry; "" keeps the market to its cached and built-in copies.
                            var marketUrl = value?.GetValue<string>()?.Trim();
                            if (marketUrl is null) s.PluginMarketUrl = null;
                            else if (marketUrl.Length == 0) s.PluginMarketUrl = "";
                            else if (Uri.TryCreate(marketUrl, UriKind.Absolute, out var market) && market.Scheme is "http" or "https")
                                s.PluginMarketUrl = marketUrl;
                            else error = "pluginMarketUrl must be an http(s) address";
                            break;
                        case "quotaAutoIntervalMinutes":
                            // 0 turns the background balance / quota refresh off; providers may override it.
                            s.QuotaAutoIntervalMinutes = Math.Clamp(value?.GetValue<int>() ?? 30, 0, 1440);
                            break;
                        case "subscriptionQuotaIntervalMinutes":
                            // 0 turns the background refresh of subscription accounts off; otherwise at least the minimum.
                            var minutes = value?.GetValue<int>() ?? 30;
                            s.SubscriptionQuotaIntervalMinutes = minutes <= 0 ? 0 : Math.Clamp(minutes, SubscriptionQuotaSync.MinIntervalMinutes, 1440);
                            break;
                        case "usageTimeZone":
                            // Token budgets' days / weeks / months; null / "" = the process's local zone.
                            var zone = value?.GetValue<string>()?.Trim();
                            if (string.IsNullOrEmpty(zone)) s.UsageTimeZone = null;
                            else if (TimeZoneHelper.TryFind(zone, out _)) s.UsageTimeZone = zone;
                            else error = $"Unknown time zone {zone}";
                            break;
                        case "requestCompression":
                            var compression = value?.GetValue<string>();
                            if (compression is not null && UpstreamCompression.Overrides.Contains(compression)) s.RequestCompression = compression;
                            else error = "requestCompression must be default, zstd, gzip, br, deflate or none";
                            break;
                        case "proxyMode":
                            var mode = value?.GetValue<string>();
                            if (mode is SystemProxy.ModeSystem or SystemProxy.ModeCustom or SystemProxy.ModeDirect) s.ProxyMode = mode;
                            else error = "proxyMode must be system, custom or direct";
                            break;
                        case "proxyUrl":
                            // A pasted user:pass@ moves into the credential fields; only scheme://host:port is kept.
                            if (value?.GetValue<string>()?.Trim() is not { Length: > 0 } rawUrl) s.ProxyUrl = null;
                            else if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5")
                                     || proxy.Host.Length == 0)
                                error = "Invalid HTTP/SOCKS proxy URL";
                            else
                            {
                                s.ProxyUrl = proxy.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
                                if (proxy.UserInfo.Length > 0 && !patch.ContainsKey("proxyUsername"))
                                {
                                    var parts = proxy.UserInfo.Split(':', 2);
                                    s.ProxyUsername = Uri.UnescapeDataString(parts[0]);
                                    s.ProxyPasswordProtected = parts.Length > 1 ? secrets.Protect(Uri.UnescapeDataString(parts[1])) : null;
                                }
                            }
                            break;
                        case "proxyUsername":
                            s.ProxyUsername = value?.GetValue<string>()?.Trim() is { Length: > 0 } user ? user : null;
                            if (s.ProxyUsername is null) s.ProxyPasswordProtected = null;
                            break;
                        case "proxyPassword":
                            // Write-only: null / "" clears it; reads only report hasProxyPassword.
                            s.ProxyPasswordProtected = value?.GetValue<string>() is { Length: > 0 } password ? secrets.Protect(password) : null;
                            break;
                        case "proxyBypass":
                            s.ProxyBypass = value?.GetValue<string>()?.Trim() is { Length: > 0 } bypass ? bypass : null;
                            break;
                        // read-only fields are ignored (changed via CLI / config.json)
                    }
                }
                if (s.ProxyMode == SystemProxy.ModeCustom && s.ProxyUrl is null)
                {
                    // Keep the previous (valid) proxy choice instead of saving a custom mode with nothing to use.
                    (s.ProxyMode, s.ProxyUrl) = (previousProxyMode, previousProxyUrl);
                    error ??= "proxyUrl is required when proxyMode is custom";
                }
            }, ct);
            return error is null ? Results.Ok(ToSettings(next, server, paths)) : ModelEndpoints.Bad(error);
        });
    }

    private static SettingsDto ToSettings(AppSettings s, ServerOptions server, AstraPaths paths) => new(
        s.Locale, s.DebugBodies, s.BodyRetentionDays, s.RequestRetentionDays, s.EffortBudgets, s.StreamIdleTimeoutSec,
        UpdateChannels.Normalize(s.UpdateChannel), s.UpdateAutoCheck,
        server.Port, server.Host, paths.Root, server.GatewayBaseUrl, s.QuotaAutoIntervalMinutes, s.SubscriptionQuotaIntervalMinutes,
        s.ProxyMode, s.ProxyUrl, s.ProxyUsername, !string.IsNullOrEmpty(s.ProxyPasswordProtected), s.ProxyBypass,
        s.RequireGatewayKey, s.PluginMarketUrl, Astra.Plugins.Market.PluginMarket.DefaultUrl, s.UsageTimeZone,
        UsagePeriods.Zone(s.UsageTimeZone).Id, s.RequestCompression, s.TraceContent);

    /// <summary>
    /// In-memory mirror of the list filters (<see cref="RequestRepository.RequestWhereSql"/>) for in-flight rows:
    /// same client / token / provider any-of match (comma-separated lists) and case-insensitive model contains-match,
    /// without the LIKE escaping.
    /// </summary>
    private static bool MatchesWindowFilters(RequestRecord r, RequestQuery f) =>
        AnyOf(f.ClientKind, r.ClientKind)
        && AnyOf(f.TokenId, r.TokenId)
        && AnyOf(f.ProviderId, r.ProviderId)
        && (string.IsNullOrWhiteSpace(f.Model) || ModelContains(r, f.Model.Trim()))
        && (f.Remote is null || r.Remote == f.Remote);

    /// <summary>The <c>origin</c> filter: "remote" | "local"; anything else means both.</summary>
    private static bool? Origin(string? value) => value switch { "remote" => true, "local" => false, _ => null };

    /// <summary>True when the list filter is unset, or <paramref name="value"/> is one of its comma-separated entries.</summary>
    private static bool AnyOf(string? list, string? value) =>
        string.IsNullOrEmpty(list) || (value is not null && list.Split(',').Contains(value));

    private static bool ModelContains(RequestRecord r, string term) =>
        Contains(r.RequestedModel, term) || Contains(r.UpstreamModel, term)
        || Contains(r.SystemModelId, term) || Contains(r.ResponseModel, term);

    private static bool Contains(string? s, string term) =>
        s?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

    internal static RequestSummaryDto ToSummary(RequestRecord r) => ToSummary(r, inFlight: false);

    /// <summary>
    /// <paramref name="live"/>: a live-feed event — it carries the attempts, the cause and the phases (kept after the request
    /// finished, until its row is written, so a view does not lose them in between); the stage only while in flight.
    /// </summary>
    private static RequestSummaryDto ToSummary(RequestRecord r, bool inFlight, bool live = false) => new(
        r.Id, r.StartedAtUtc, r.ClientKind, r.ProviderId, r.ProviderName, r.InboundProtocol, r.UpstreamProtocol, r.Passthrough,
        r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.SystemModelId, r.Stream, inFlight ? PendingStatus : r.Status, r.HttpStatus, r.TtftMs, r.TotalMs, r.OutputTps,
        r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens,
        r.CostNanoUsd, r.UsageSource,
        r.ReasoningEffort, r.ReasoningMode, r.ReasoningBudgetTokens,
        r.TokenId, r.TokenName, r.AccountId, r.AccountName, r.Remote, r.ClientIp,
        r.SessionKey, inFlight ? r.LiveStage : null, r.TurnId, r.CallKind, r.ParentRequestId, inFlight || live ? r.LivePhases : null, RoutingOf(r),
        live ? r.LiveAttempts : null, live ? r.LiveCause : null);

    private static RequestDetailDto ToDetail(RequestRecord r, BodyStore bodies, bool inFlight = false)
    {
        BodiesDto? captured = null;
        if (!string.IsNullOrEmpty(r.BodyRef))
        {
            var bodyRef = r.BodyRef;
            BodyPreview? Part(string part) => bodies.ReadPreview(bodyRef, part, BodyPreviewBytes);
            var (clientRequest, upstreamRequest, headers, upstreamResponse, clientResponse) = (Part(BodyStore.ClientRequest),
                Part(BodyStore.UpstreamRequest), Part(BodyStore.UpstreamRequestHeaders), Part(BodyStore.UpstreamResponse),
                Part(BodyStore.ClientResponse));
            captured = new BodiesDto(clientRequest?.Text, upstreamRequest?.Text, headers?.Text, upstreamResponse?.Text, clientResponse?.Text,
                new BodySizesDto(clientRequest?.Size, upstreamRequest?.Size, headers?.Size, upstreamResponse?.Size, clientResponse?.Size),
                BodyPreviewBytes);
        }
        return new RequestDetailDto(
            r.Id, r.StartedAtUtc, r.ClientKind, r.ProviderId, r.ProviderName, r.InboundProtocol, r.UpstreamProtocol, r.Passthrough,
            r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.SystemModelId, r.Stream, inFlight ? PendingStatus : r.Status, r.HttpStatus,
            r.TtftMs, r.TotalMs,
            r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens,
            r.CostNanoUsd, r.UsageSource,
            r.ServiceTier, r.ErrorType, r.ErrorMessage, r.UpstreamRequestId, r.TtfbMs, r.GenerationMs, r.OutputTps,
            ParseNode(r.UsageRawJson), ParseNode(r.PricingSnapshotJson), r.PricingSource, r.PriceKey,
            r.BillingTraceJson is null ? [] : JsonSerializer.Deserialize(r.BillingTraceJson, JsonContexts.Info<List<BillingTraceStep>>(Json.Storage)) ?? [],
            r.BillingDescription,
            (inFlight ? [] : r.UsageItems).Select(i => new UsageItemDto(i.TokenType, i.Tokens, i.IsPerCall, i.UnitPrice, i.BaseUnitPrice, i.TierApplied,
                i.PricedAs, i.MultipliersJson is null ? null : JsonSerializer.Deserialize(i.MultipliersJson, JsonContexts.Info<List<string>>(Json.Storage)),
                i.CostNanoUsd, i.Note)).ToList(),
            r.UserAgent, captured, r.PrivacyJson is null ? null : Json.Deserialize<PrivacyReport>(r.PrivacyJson),
            r.ReasoningEffort, r.ReasoningMode, r.ReasoningBudgetTokens,
            r.TokenId, r.TokenName, r.AccountId, r.AccountName, r.Remote, r.ClientIp, RoutingOf(r));
    }

    /// <summary>Smart routing's decision for a request to the virtual model; null for every other request.</summary>
    private static RoutingReport? RoutingOf(RequestRecord r) => r.RoutingJson is null ? null : Json.Deserialize<RoutingReport>(r.RoutingJson);

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    internal static (string Name, DateTimeOffset From, DateTimeOffset To) Range(string? range)
    {
        var now = DateTimeOffset.Now;
        var startOfToday = new DateTimeOffset(now.Date, now.Offset);
        return range switch
        {
            "7d" => ("7d", startOfToday.AddDays(-6).ToUniversalTime(), now.ToUniversalTime()),
            "30d" => ("30d", startOfToday.AddDays(-29).ToUniversalTime(), now.ToUniversalTime()),
            "90d" => ("90d", startOfToday.AddDays(-89).ToUniversalTime(), now.ToUniversalTime()),
            "all" => ("all", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), now.ToUniversalTime()),
            _ => ("today", startOfToday.ToUniversalTime(), now.ToUniversalTime()),
        };
    }

    internal static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : null;

    internal static string? Str(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

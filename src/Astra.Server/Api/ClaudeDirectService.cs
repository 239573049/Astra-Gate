using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Astra.Server.Hosting;

namespace Astra.Server.Api;


/// <summary>
/// Direct Claude Code mode: the official client talks to Anthropic itself with a subscription account Astra
/// already holds, instead of going through the gateway. Astra stays the only owner of the refresh token and
/// hands the launcher a short-lived access token. Statistics are opt-in, per account, and arrive as OTLP logs
/// from the client; nothing here goes through <see cref="GatewayPipeline"/>, and the user's own Claude
/// credentials and settings are never read or written.
/// </summary>
public sealed class ClaudeDirectService(
    AstraDatabase db, ServerOptions server, ISecretProtector protector, SubscriptionTokenService tokens)
{
    private const string StateKey = "claude_direct";

    /// <summary>
    /// Claude Code cannot refresh a token it was handed, so a launch starts with at least this much life left;
    /// a longer session has to be restarted once the token expires.
    /// </summary>
    private static readonly TimeSpan MinTokenLife = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ClaudeDirectRequestRepository _requests = new(db.Factory);

    public async Task<ClaudeDirectStateDto> StateAsync(CancellationToken ct) => await ViewAsync(await LoadAsync(ct), ct);

    public async Task<ClaudeDirectStateDto> SelectAsync(ClaudeDirectSelectInput input, CancellationToken ct)
    {
        if (input.Mode is not ("gateway" or "direct")) throw new AdminApiException(400, "Unknown connection mode.");
        await _writes.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            var accountId = input.AccountId ?? Text(state["account_id"]);
            if (input.AccountId is not null || input.TelemetryEnabled is not null || input.Mode == "direct")
            {
                var (_, account) = await FindAsync(accountId, ct);
                if (input.Mode == "direct")
                {
                    RequireUsable(account);
                    // Gateway takeover writes ANTHROPIC_BASE_URL / ANTHROPIC_AUTH_TOKEN into Claude's own settings, which
                    // would outrank the subscription token: the two modes cannot be on together.
                    if ((await db.Clients.GetAsync(ClientKinds.ClaudeCode, ct))?.Enabled == true)
                        throw new AdminApiException(409, "Disable the gateway takeover of Claude Code first; it overrides direct mode.");
                }
                if (input.TelemetryEnabled is { } enabled)
                {
                    var settings = AccountSettings(state, account.Id);
                    settings["telemetry"] = enabled;
                    // Revoking consent immediately rejects old exporters, even if consent is granted again later.
                    if (!enabled) settings.Remove("collector_secret");
                }
                state["account_id"] = account.Id;
            }
            state["mode"] = input.Mode;
            await db.Settings.SetAsync(StateKey, state, ct);
            return await ViewAsync(state, ct);
        }
        finally { _writes.Release(); }
    }

    public async Task<ClaudeDirectLaunchDto> PrepareAsync(ClaudeDirectPrepareInput input, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            if (Text(state["mode"]) != "direct")
                throw new AdminApiException(409, "Select subscription direct mode first. Use claude for your default configuration.");
            var (provider, found) = await FindAsync(input.AccountId ?? Text(state["account_id"]), ct);
            RequireUsable(found);
            var config = SubscriptionSupport.EffectiveConfig(provider);

            string accessToken;
            DateTimeOffset expiresAt;
            try
            {
                var account = found;
                if (account.ExpiresAtUtc is not { } expiry || expiry < DateTimeOffset.UtcNow + MinTokenLife)
                {
                    try { account = await tokens.RefreshAsync(account, config, force: true, ct); }
                    // A network failure changes nothing; keep going while the stored token is still usable for a while.
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested
                                              && account.ExpiresAtUtc is { } left && left > DateTimeOffset.UtcNow.AddMinutes(2)) { }
                }
                accessToken = await tokens.GetValidAccessTokenAsync(account, config, ct);
                expiresAt = (await db.Accounts.GetAsync(account.Id, ct))?.ExpiresAtUtc ?? account.ExpiresAtUtc ?? DateTimeOffset.UtcNow.AddHours(1);
            }
            catch (SubscriptionAuthException e)
            {
                throw new AdminApiException(409, e.Message);
            }
            catch (HttpRequestException)
            {
                throw new AdminApiException(502, "Could not refresh the subscription token; check the network or proxy and try again.");
            }

            var settings = AccountSettings(state, found.Id);
            var telemetry = settings["telemetry"]?.GetValue<bool>() == true;
            string? secret = null;
            if (telemetry)
            {
                if (Text(settings["collector_secret"]) is { } encrypted) secret = protector.Unprotect(encrypted);
                else
                {
                    secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    settings["collector_secret"] = protector.Protect(secret);
                    await db.Settings.SetAsync(StateKey, state, ct);
                }
            }
            return new(found.Id, found.DisplayName, expiresAt, LaunchEnv(found.Id, accessToken, telemetry, secret));
        }
        finally { _writes.Release(); }
    }

    public async Task<List<ClaudeDirectRequest>> RequestsAsync(string id, CancellationToken ct)
    {
        await FindAsync(id, ct);
        return await _requests.ListAsync(id, ct: ct);
    }

    public async Task<bool> AuthorizedAsync(string id, string token, CancellationToken ct)
    {
        if (token.Length != 64 || !ValidId(id)) return false;
        var settings = (await LoadAsync(ct))["accounts"]?[id] as JsonObject;
        if (settings?["telemetry"]?.GetValue<bool>() != true || Text(settings["collector_secret"]) is not { } encrypted)
            return false;
        var expected = protector.Unprotect(encrypted);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    public async Task IngestAsync(string id, string token, JsonObject payload, CancellationToken ct)
    {
        await _writes.WaitAsync(ct);
        try
        {
            // Recheck inside the write lock so a concurrent consent revocation cannot race ingestion.
            if (!await AuthorizedAsync(id, token, ct)) throw new AdminApiException(401, "Invalid collector credential.");
            await _requests.InsertAsync(ClaudeDirectTelemetry.Parse(id, payload), ct);
        }
        finally { _writes.Release(); }
    }

    private Dictionary<string, string> LaunchEnv(string id, string accessToken, bool telemetry, string? secret)
    {
        var env = new Dictionary<string, string>
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = accessToken,
            // Anything else the shell or a settings file might carry must not outrank the subscription token.
            ["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com",
            ["ANTHROPIC_API_KEY"] = "", ["ANTHROPIC_AUTH_TOKEN"] = "",
            ["CLAUDE_CODE_USE_BEDROCK"] = "0", ["CLAUDE_CODE_USE_VERTEX"] = "0", ["CLAUDE_CODE_USE_FOUNDRY"] = "0",
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = telemetry ? "1" : "0",
            ["CLAUDE_CODE_ENHANCED_TELEMETRY_BETA"] = "0", ["ENABLE_ENHANCED_TELEMETRY_BETA"] = "0",
            ["ENABLE_BETA_TRACING_DETAILED"] = "0", ["BETA_TRACING_ENDPOINT"] = "",
            ["OTEL_LOGS_EXPORTER"] = telemetry ? "otlp" : "none",
            ["OTEL_METRICS_EXPORTER"] = "none", ["OTEL_TRACES_EXPORTER"] = "none",
            ["OTEL_LOG_USER_PROMPTS"] = "0", ["OTEL_LOG_ASSISTANT_RESPONSES"] = "0",
            ["OTEL_LOG_TOOL_DETAILS"] = "0", ["OTEL_LOG_TOOL_CONTENT"] = "0", ["OTEL_LOG_RAW_API_BODIES"] = "0",
        };
        if (telemetry)
        {
            env["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"] = "http/json";
            env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = $"{server.LocalUrl}/telemetry/claude/{id}/v1/logs";
            env["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = $"Authorization=Bearer {secret}";
        }
        return env;
    }

    private async Task<JsonObject> LoadAsync(CancellationToken ct)
    {
        // Settings written by the earlier native-profile design have no "accounts" map and are simply ignored.
        var state = await db.Settings.GetAsync<JsonObject>(StateKey, ct) ?? new JsonObject();
        if (state["accounts"] is not JsonObject) state["accounts"] = new JsonObject();
        if (Text(state["mode"]) is not ("gateway" or "direct")) state["mode"] = "gateway";
        return state;
    }

    private static JsonObject AccountSettings(JsonObject state, string id)
    {
        var accounts = state["accounts"]!.AsObject();
        if (accounts[id] is not JsonObject settings) accounts[id] = settings = new JsonObject();
        return settings;
    }

    private async Task<List<(Provider Provider, ProviderAccount Account)>> ClaudeAccountsAsync(CancellationToken ct)
    {
        var result = new List<(Provider, ProviderAccount)>();
        foreach (var provider in await db.Providers.ListAsync(ct))
        {
            if (!SubscriptionSupport.IsClaudeSubscription(provider)) continue;
            foreach (var account in await db.Accounts.ListAsync(provider.Id, ct)) result.Add((provider, account));
        }
        return result;
    }

    private async Task<(Provider Provider, ProviderAccount Account)> FindAsync(string? id, CancellationToken ct)
    {
        foreach (var pair in await ClaudeAccountsAsync(ct))
            if (pair.Account.Id == id) return pair;
        throw new AdminApiException(404, "Choose one of the Claude subscription accounts already logged in to Astra.");
    }

    private static void RequireUsable(ProviderAccount account)
    {
        if (!account.Enabled) throw new AdminApiException(409, "This account is disabled in Astra.");
        if (account.Status != AccountStatus.Active)
            throw new AdminApiException(409, "This account needs to be logged in again in Astra (Providers → Claude subscription).");
    }

    private async Task<ClaudeDirectStateDto> ViewAsync(JsonObject state, CancellationToken ct)
    {
        var accounts = (await ClaudeAccountsAsync(ct)).Select(pair => new ClaudeDirectAccountDto(
            pair.Account.Id, pair.Account.DisplayName, pair.Account.AccountEmail, pair.Account.Plan, pair.Account.Status,
            pair.Account.Enabled, state["accounts"]?[pair.Account.Id]?["telemetry"]?.GetValue<bool>() == true)).ToList();
        var selected = Text(state["account_id"]);
        return new(Text(state["mode"]) ?? "gateway", accounts.Any(a => a.Id == selected) ? selected : null, accounts);
    }

    private static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>Whitelists statistics from OTLP logs; prompts, bodies, tool arguments and error text are discarded.</summary>
public static class ClaudeDirectTelemetry
{
    public static List<ClaudeDirectRequest> Parse(string accountId, JsonObject payload)
    {
        var result = new List<ClaudeDirectRequest>();
        var count = 0;
        if (payload["resourceLogs"] is not JsonArray resources) throw new AdminApiException(400, "Expected OTLP resourceLogs.");
        foreach (var resource in resources.OfType<JsonObject>())
        {
            var resourceAttributes = Attributes(resource["resource"]?["attributes"]);
            if (resource["scopeLogs"] is not JsonArray scopes) continue;
            foreach (var scope in scopes.OfType<JsonObject>())
            {
                if (scope["logRecords"] is not JsonArray logs) continue;
                foreach (var log in logs.OfType<JsonObject>())
                {
                    if (++count > 500) throw new AdminApiException(413, "At most 500 telemetry events per batch.");
                    var attributes = new Dictionary<string, string>(resourceAttributes);
                    foreach (var pair in Attributes(log["attributes"])) attributes[pair.Key] = pair.Value;
                    string? Get(string key) => attributes.GetValueOrDefault(key);
                    var name = Get("event.name") ?? Scalar(log["body"]);
                    if (name?.StartsWith("claude_code.", StringComparison.Ordinal) == true) name = name[12..];
                    if (name is not ("api_request" or "api_error")) continue;
                    var session = Limited(Get("session.id"), 128);
                    var model = Limited(Get("model"), 200);
                    var account = Limited(Get("user.account_uuid"), 128);
                    if (session is null || model is null) continue;
                    DateTimeOffset at;
                    if (!DateTimeOffset.TryParse(Get("event.timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at))
                    {
                        if (!long.TryParse(log["timeUnixNano"]?.ToString(), CultureInfo.InvariantCulture, out var nanos) || nanos <= 0) continue;
                        at = DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
                    }
                    var now = DateTimeOffset.UtcNow;
                    if (at < now.AddDays(-90)) continue;
                    // A clock running ahead must not lose events: clamp instead of dropping.
                    if (at > now) at = now;
                    var identity = Limited(Get("request_id"), 200) ?? Limited(Get("client_request_id"), 200);
                    // A process can resume the same session and restart event.sequence: the timestamp makes the fallback unique.
                    identity ??= $"{at:O}:{Get("event.sequence")}";
                    var key = Json.Serialize(new JsonArray(accountId, session, account, name, identity));
                    var input = Count(Get("input_tokens"));
                    var output = Count(Get("output_tokens"));
                    var cacheRead = Count(Get("cache_read_tokens"));
                    var cacheCreate = Count(Get("cache_creation_tokens"));
                    if (input is null || output is null || cacheRead is null || cacheCreate is null) continue;
                    var duration = Number(Get("duration_ms"), 86_400_000);
                    long? cost = null;
                    if (Number(Get("cost_usd_micros"), 1_000_000_000_000) is { } micros) cost = micros * 1000;
                    else if (decimal.TryParse(Get("cost_usd"), NumberStyles.Float, CultureInfo.InvariantCulture, out var usd) && usd is >= 0 and <= 1_000_000)
                        cost = Money.ToNanos(usd);
                    result.Add(new ClaudeDirectRequest
                    {
                        Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
                        // The table column is named profile_id; it holds the Astra subscription account id.
                        ProfileId = accountId, SessionId = session, AccountUuid = account, Model = model, OccurredAtUtc = at,
                        Status = name == "api_request" ? "success" : "error", InputTokens = input.Value,
                        OutputTokens = output.Value, CacheReadTokens = cacheRead.Value, CacheCreationTokens = cacheCreate.Value,
                        DurationMs = duration, EstimatedCostNanoUsd = cost,
                    });
                }
            }
        }
        return result;
    }

    private static Dictionary<string, string> Attributes(JsonNode? node)
    {
        var values = new Dictionary<string, string>();
        if (node is not JsonArray array) return values;
        foreach (var item in array.OfType<JsonObject>())
            if (item["key"] is JsonValue key && key.TryGetValue<string>(out var text) && Scalar(item["value"]) is { } value)
                values[text] = value;
        return values;
    }

    private static string? Scalar(JsonNode? value) => value is JsonObject obj
        ? (obj["stringValue"] ?? obj["intValue"] ?? obj["doubleValue"])?.ToString() : null;
    private static string? Limited(string? value, int max) => string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl) ? null : value;
    private static long? Count(string? value) => value is null ? 0 : Number(value, 1_000_000_000);
    private static long? Number(string? value, long max) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        && number >= 0 && number <= max ? (long)number : null;
}

/// <summary>Local-only control and independently authenticated, bounded OTLP log ingestion.</summary>
public static class ClaudeDirectEndpoints
{
    public static void MapClaudeDirectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/clients/claude-code/direct").AddEndpointFilter<AdminApiErrorFilter>();
        group.AddEndpointFilter(async (context, next) =>
        {
            RequireLocal(context.HttpContext);
            // /prepare returns an access token: nothing here may be cached.
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", async (ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.StateAsync(ct)));
        group.MapPut("", async (ClaudeDirectSelectInput input, ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.SelectAsync(input, ct)));
        group.MapPost("/prepare", async (ClaudeDirectPrepareInput input, ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.PrepareAsync(input, ct)));
        group.MapGet("/accounts/{id}/requests", async (string id, ClaudeDirectService service, CancellationToken ct) =>
            Results.Ok((await service.RequestsAsync(id, ct)).Select(ToDto).ToList()));

        app.MapPost("/telemetry/claude/{id}/v1/logs", async (string id, HttpContext ctx, ClaudeDirectService service, CancellationToken ct) =>
        {
            RequireLocal(ctx);
            ctx.Response.Headers.CacheControl = "no-store";
            var authorization = ctx.Request.Headers.Authorization.ToString();
            var token = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
            if (!await service.AuthorizedAsync(id, token, ct)) return Results.Unauthorized();
            if (!ctx.Request.HasJsonContentType() || ctx.Request.Headers.ContentEncoding.Count > 0)
                return Results.StatusCode(415);
            const int maximum = 1_048_576;
            if (ctx.Request.ContentLength > maximum) return Results.StatusCode(413);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                while ((read = await ctx.Request.Body.ReadAsync(buffer, deadline.Token)) > 0)
                {
                    if (body.Length + read > maximum) return Results.StatusCode(413);
                    body.Write(buffer, 0, read);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.StatusCode(408);
            }
            JsonObject payload;
            try { payload = JsonNode.Parse(body.ToArray()) as JsonObject ?? throw new JsonException(); }
            catch (JsonException) { throw new AdminApiException(400, "Invalid OTLP JSON."); }
            try { await service.IngestAsync(id, token, payload, ct); }
            catch (InvalidOperationException) { throw new AdminApiException(400, "Invalid OTLP structure."); }
            return Results.Ok(new JsonObject());
        }).AddEndpointFilter<AdminApiErrorFilter>();
    }

    // The stored row (Astra.Data) mapped onto its wire contract, so the desktop client can read it without Astra.Data.
    private static ClaudeDirectRequestDto ToDto(ClaudeDirectRequest r) => new(
        r.Id, r.ProfileId, r.SessionId, r.AccountUuid, r.Model, r.OccurredAtUtc, r.Status,
        r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheCreationTokens, r.DurationMs, r.EstimatedCostNanoUsd);

    private static void RequireLocal(HttpContext ctx)
    {
        var address = ctx.Connection.RemoteIpAddress;
        // Off-loopback only a caller the security middleware judged local (loopback peer and Host, no proxy) qualifies.
        if (!RequestOrigin.IsLocal(ctx) || address is null || !IPAddress.IsLoopback(address))
            throw new AdminApiException(403, "Direct Claude Code mode and its telemetry are only available on this machine.");
    }
}

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>A native Claude login directory; no subscription credentials cross the admin API.</summary>
public sealed record ClaudeDirectProfileDto(string Id, string Name, bool TelemetryEnabled, string ConfigDirectory);
/// <summary>Selection for the Astra launcher, independent of the user's default Claude installation.</summary>
public sealed record ClaudeDirectStateDto(string Mode, string? ProfileId, List<ClaudeDirectProfileDto> Profiles);
/// <summary>Creates an isolated native login, not an imported gateway OAuth account.</summary>
public sealed record ClaudeDirectCreateInput(string Name);
/// <summary>Updates the launch selection and optional statistics consent for the selected profile.</summary>
public sealed record ClaudeDirectSelectInput(string Mode, string? ProfileId, bool? TelemetryEnabled);
/// <summary>Prepares the official CLI without starting a process on the server.</summary>
public sealed record ClaudeDirectPrepareInput(string? ProfileId, string Action);
/// <summary>Only local paths are returned; the telemetry credential stays in an owner-only launch file.</summary>
public sealed record ClaudeDirectLaunchDto(string ProfileId, string ConfigDirectory, string SettingsFile);

/// <summary>
/// Owns isolated Claude profiles and opt-in statistics. Never reads or writes Claude's native credentials,
/// default settings, or project files. Model traffic goes directly to Anthropic, not through GatewayPipeline.
/// </summary>
public sealed class ClaudeDirectService(AstraDatabase db, AstraPaths paths, ServerOptions server, ISecretProtector protector)
{
    private const string StateKey = "claude_direct";
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ClaudeDirectRequestRepository _requests = new(db.Factory);

    public async Task<ClaudeDirectStateDto> StateAsync(CancellationToken ct) => View(await LoadAsync(ct));

    public async Task<ClaudeDirectStateDto> CreateAsync(ClaudeDirectCreateInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 80 || input.Name.Any(char.IsControl))
            throw new AdminApiException(400, "Profile name must contain 1–80 characters without control characters.");
        await _writes.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            var profiles = state["profiles"]!.AsArray();
            if (profiles.Count >= 20) throw new AdminApiException(409, "At most 20 native Claude profiles are supported.");
            var id = Guid.NewGuid().ToString("N");
            // The directory is created only for an explicit action; listing never touches the user's home.
            EnsureDirectory(id);
            profiles.Add((JsonNode)new JsonObject { ["id"] = id, ["name"] = input.Name.Trim(), ["telemetry"] = false });
            state["profile_id"] ??= JsonValue.Create(id);
            await db.Settings.SetAsync(StateKey, state, ct);
            return View(state);
        }
        finally { _writes.Release(); }
    }

    public async Task<ClaudeDirectStateDto> SelectAsync(ClaudeDirectSelectInput input, CancellationToken ct)
    {
        if (input.Mode is not ("gateway" or "direct")) throw new AdminApiException(400, "Unknown connection mode.");
        await _writes.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            var id = input.ProfileId ?? Text(state["profile_id"]);
            if (input.Mode == "direct" || input.TelemetryEnabled is not null || input.ProfileId is not null)
            {
                var profile = RequireProfile(state, id);
                if (input.TelemetryEnabled is { } enabled)
                {
                    profile["telemetry"] = enabled;
                    // Revoking consent immediately rejects old exporters, even if consent is granted again later.
                    if (!enabled) profile.Remove("collector_secret");
                }
            }
            state["mode"] = input.Mode;
            state["profile_id"] = id;
            await db.Settings.SetAsync(StateKey, state, ct);
            return View(state);
        }
        finally { _writes.Release(); }
    }

    public async Task<ClaudeDirectLaunchDto> PrepareAsync(ClaudeDirectPrepareInput input, CancellationToken ct)
    {
        if (input.Action is not ("login" or "status" or "run")) throw new AdminApiException(400, "Unknown launch action.");
        await _writes.WaitAsync(ct);
        try
        {
            var state = await LoadAsync(ct);
            if (input.Action == "run" && Text(state["mode"]) != "direct")
                throw new AdminApiException(409, "Select subscription direct mode first. Use claude for your default configuration.");
            var profile = RequireProfile(state, input.ProfileId ?? Text(state["profile_id"]));
            var id = Text(profile["id"])!;
            var dir = EnsureDirectory(id);
            var enabled = input.Action == "run" && profile["telemetry"]?.GetValue<bool>() == true;
            string? secret = null;
            if (enabled)
            {
                if (Text(profile["collector_secret"]) is not { } encrypted)
                {
                    secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                    profile["collector_secret"] = protector.Protect(secret);
                    await db.Settings.SetAsync(StateKey, state, ct);
                }
                else secret = protector.Unprotect(encrypted);
            }
            var settings = LaunchSettings(id, enabled, secret);
            var file = Path.Combine(dir, "astra-launch.json");
            EnsureNotLink(file);
            // This is an Astra-owned overlay, not settings.json: native preferences and credentials stay untouched.
            var temporary = Path.Combine(dir, $".astra-launch-{Guid.NewGuid():N}.tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temporary, options))
                {
                    var bytes = Encoding.UTF8.GetBytes(Json.SerializeApi(settings));
                    await stream.WriteAsync(bytes, ct);
                }
                File.Move(temporary, file, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(id, dir, file);
        }
        finally { _writes.Release(); }
    }

    public async Task<List<ClaudeDirectRequest>> RequestsAsync(string id, CancellationToken ct)
    {
        RequireProfile(await LoadAsync(ct), id);
        return await _requests.ListAsync(id, ct: ct);
    }

    public async Task<bool> AuthorizedAsync(string id, string token, CancellationToken ct)
    {
        if (token.Length != 64 || !ValidId(id)) return false;
        var state = await LoadAsync(ct);
        var profile = state["profiles"]!.AsArray().OfType<JsonObject>().FirstOrDefault(p => Text(p["id"]) == id);
        if (profile?["telemetry"]?.GetValue<bool>() != true || Text(profile["collector_secret"]) is not { } encrypted)
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
            var records = ClaudeDirectTelemetry.Parse(id, payload);
            await _requests.InsertAsync(records, ct);
        }
        finally { _writes.Release(); }
    }

    private JsonObject LaunchSettings(string id, bool enabled, string? secret)
    {
        var env = new JsonObject
        {
            ["ANTHROPIC_BASE_URL"] = "https://api.anthropic.com",
            ["ANTHROPIC_API_KEY"] = "", ["ANTHROPIC_AUTH_TOKEN"] = "", ["CLAUDE_CODE_OAUTH_TOKEN"] = "",
            ["CLAUDE_CODE_USE_BEDROCK"] = "0", ["CLAUDE_CODE_USE_VERTEX"] = "0", ["CLAUDE_CODE_USE_FOUNDRY"] = "0",
            ["CLAUDE_CODE_ENABLE_TELEMETRY"] = enabled ? "1" : "0",
            ["CLAUDE_CODE_ENHANCED_TELEMETRY_BETA"] = "0", ["ENABLE_ENHANCED_TELEMETRY_BETA"] = "0",
            ["ENABLE_BETA_TRACING_DETAILED"] = "0", ["BETA_TRACING_ENDPOINT"] = "",
            ["OTEL_LOGS_EXPORTER"] = enabled ? "otlp" : "none",
            ["OTEL_METRICS_EXPORTER"] = "none", ["OTEL_TRACES_EXPORTER"] = "none",
            ["OTEL_LOG_USER_PROMPTS"] = "0", ["OTEL_LOG_ASSISTANT_RESPONSES"] = "0",
            ["OTEL_LOG_TOOL_DETAILS"] = "0", ["OTEL_LOG_TOOL_CONTENT"] = "0", ["OTEL_LOG_RAW_API_BODIES"] = "0",
        };
        if (enabled)
        {
            env["OTEL_EXPORTER_OTLP_LOGS_PROTOCOL"] = "http/json";
            env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"] = $"{server.LocalUrl}/telemetry/claude/{id}/v1/logs";
            env["OTEL_EXPORTER_OTLP_LOGS_HEADERS"] = $"Authorization=Bearer {secret}";
            env["OTEL_RESOURCE_ATTRIBUTES"] = $"astra.profile_id={id}";
        }
        return new JsonObject { ["forceLoginMethod"] = "claudeai", ["env"] = env };
    }

    private Task<JsonObject?> StoredAsync(CancellationToken ct) => db.Settings.GetAsync<JsonObject>(StateKey, ct);
    private async Task<JsonObject> LoadAsync(CancellationToken ct) => await StoredAsync(ct)
        ?? new JsonObject { ["mode"] = "gateway", ["profiles"] = new JsonArray() };

    private ClaudeDirectStateDto View(JsonObject state) => new(Text(state["mode"]) ?? "gateway", Text(state["profile_id"]),
        state["profiles"]!.AsArray().OfType<JsonObject>().Select(p => new ClaudeDirectProfileDto(
            Text(p["id"])!, Text(p["name"])!, p["telemetry"]?.GetValue<bool>() == true, ProfileDirectory(Text(p["id"])!))).ToList());

    private static JsonObject RequireProfile(JsonObject state, string? id) =>
        state["profiles"]!.AsArray().OfType<JsonObject>().FirstOrDefault(p => Text(p["id"]) == id)
        ?? throw new AdminApiException(404, "Choose an existing native Claude profile.");

    private string ProfileDirectory(string id)
    {
        if (!ValidId(id)) throw new AdminApiException(400, "Invalid profile id.");
        return Path.GetFullPath(Path.Combine(paths.ClaudeProfilesDir, id));
    }

    private string EnsureDirectory(string id)
    {
        var dir = ProfileDirectory(id);
        // Refuse symlink redirection; a managed profile must never replace the user's default .claude directory.
        EnsureNotLink(paths.Root);
        EnsureNotLink(paths.ClaudeProfilesDir);
        EnsureNotLink(dir);
        Directory.CreateDirectory(dir);
        AstraPaths.RestrictToOwner(paths.ClaudeProfilesDir);
        AstraPaths.RestrictToOwner(dir);
        return dir;
    }

    private static void EnsureNotLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new AdminApiException(409, "Native Claude profile paths must not be symbolic links.");
    }

    private static bool ValidId(string id) => id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>Whitelists statistics from OTLP logs; prompts, bodies, tool arguments and error text are discarded.</summary>
public static class ClaudeDirectTelemetry
{
    public static List<ClaudeDirectRequest> Parse(string profileId, JsonObject payload)
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
                    var timestamp = Get("event.timestamp");
                    if (session is null || model is null) continue;
                    DateTimeOffset at;
                    if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at))
                    {
                        if (!long.TryParse(log["timeUnixNano"]?.ToString(), CultureInfo.InvariantCulture, out var nanos) || nanos <= 0) continue;
                        at = DateTimeOffset.FromUnixTimeMilliseconds(nanos / 1_000_000);
                    }
                    if (at < DateTimeOffset.UtcNow.AddDays(-90) || at > DateTimeOffset.UtcNow.AddMinutes(5)) continue;
                    var identity = Limited(Get("request_id"), 200) ?? Limited(Get("client_request_id"), 200);
                    // A process can resume the same session and restart event.sequence: timestamp is mandatory for fallback.
                    identity ??= $"{at:O}:{Get("event.sequence")}";
                    var key = Json.Serialize(new JsonArray(profileId, session, account, name, identity));
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
                        ProfileId = profileId, SessionId = session, AccountUuid = account, Model = model, OccurredAtUtc = at,
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
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", async (ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.StateAsync(ct)));
        group.MapPost("/profiles", async (ClaudeDirectCreateInput input, ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.CreateAsync(input, ct)));
        group.MapPut("", async (ClaudeDirectSelectInput input, ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.SelectAsync(input, ct)));
        group.MapPost("/prepare", async (ClaudeDirectPrepareInput input, ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.PrepareAsync(input, ct)));
        group.MapGet("/profiles/{id}/requests", async (string id, ClaudeDirectService service, CancellationToken ct) => Results.Ok(await service.RequestsAsync(id, ct)));

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

    private static void RequireLocal(HttpContext ctx)
    {
        var server = ctx.RequestServices.GetRequiredService<ServerOptions>();
        var address = ctx.Connection.RemoteIpAddress;
        if (!server.IsLoopback || address is null || !IPAddress.IsLoopback(address))
            throw new AdminApiException(403, "Native Claude profiles and telemetry require a loopback server on this machine.");
    }
}

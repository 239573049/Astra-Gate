using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Astra.Providers.Templates;
using Microsoft.AspNetCore.WebUtilities;

namespace Astra.Server.Api;

public sealed record ProviderTestDto(bool Ok, long LatencyMs, int? HttpStatus, ApiProtocol Protocol, string? Model, string? Error);
public sealed record RemoteModelDto(string Id, string? DisplayName, string? LinkedSystemModelId, bool AlreadyAdded);

/// <summary>Small authenticated connection tests and remote model discovery. Never forwards a local client key.</summary>
public sealed class ProviderProbe(AstraDatabase db, IHttpClientFactory http, UpstreamAuthResolver auth,
    EffectiveModelResolver models, SettingsService settings, UsageWriter writer)
{
    public const string HttpClientName = "astra-provider-probe";
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxModels = 2000;
    private const int MaxPages = 20;

    public async Task<ProviderTestDto> TestAsync(string providerId, string? modelId, CancellationToken ct)
    {
        var p = await ProviderEndpoints.RequireAsync(db, providerId, ct);
        modelId ??= (await db.Providers.ListModelsAsync(p.Id, ct)).FirstOrDefault(m => m.Enabled)?.ModelId;
        if (string.IsNullOrWhiteSpace(modelId)) throw new AdminApiException(400, "Add a model or provide modelId first");
        modelId = modelId.Trim();
        if (modelId.Length > 512 || modelId.Any(char.IsControl)) throw new AdminApiException(400, "Invalid model ID");
        var endpoint = PreferredEndpoint(p);
        var (_, effective) = await models.ResolveByModelIdAsync(p, modelId, ct);
        var watch = Stopwatch.StartNew();
        var record = new RequestRecord
        {
            Id = Ulid.NewUlid(), StartedAtUtc = DateTimeOffset.UtcNow, ProviderId = p.Id, ProviderName = p.Name,
            InboundProtocol = endpoint.Protocol.ToId(), UpstreamProtocol = endpoint.Protocol.ToId(), Passthrough = true,
            RequestedModel = modelId, UpstreamModel = modelId, SystemModelId = effective.SystemModelId,
            UserAgent = "Astra connection test", Status = RequestStatus.GatewayError,
        };
        NormalizedUsage? usage = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var client = CreateClient(p);
            using var request = await RequestAsync(p, HttpMethod.Post, RequestUrl(endpoint, modelId), endpoint.Protocol, deadline.Token);
            request.Content = new StringContent(PingBody(endpoint.Protocol, modelId).ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            record.TtfbMs = watch.ElapsedMilliseconds;
            record.HttpStatus = (int)response.StatusCode;
            var text = await ReadResponseAsync(response, deadline.Token);
            var body = TryObject(text);
            var reportedModel = endpoint.Protocol == ApiProtocol.Gemini
                ? Text(body?["modelVersion"]) ?? Text(body?["model_version"])
                : Text(body?["model"]);
            record.ResponseModel = string.IsNullOrWhiteSpace(reportedModel) ? null : reportedModel;
            var ok = response.IsSuccessStatusCode && body is not null && body["error"] is null;
            record.Status = ok ? RequestStatus.Success : RequestStatus.UpstreamError;
            if (!ok) record.ErrorMessage = body is null && response.IsSuccessStatusCode ? "Upstream returned invalid JSON" : ErrorMessage(body, response.StatusCode, p, request);
            var raw = body?[endpoint.Protocol == ApiProtocol.Gemini ? "usageMetadata" : "usage"] as JsonObject;
            if (raw is not null)
            {
                usage = endpoint.Protocol switch
                {
                    ApiProtocol.OpenAIChat => UsageNormalizer.FromOpenAIChat(raw),
                    ApiProtocol.OpenAIResponses => UsageNormalizer.FromOpenAIResponses(raw),
                    ApiProtocol.Anthropic => UsageNormalizer.FromAnthropic(raw),
                    _ => UsageNormalizer.FromGemini(raw),
                };
                record.UsageSource = "reported";
                record.UsageRawJson = raw.ToJsonString();
            }
            return new ProviderTestDto(ok, watch.ElapsedMilliseconds, record.HttpStatus, endpoint.Protocol, modelId, record.ErrorMessage);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or SubscriptionAuthException or CryptographicException or AdminApiException or IOException)
        {
            record.Status = ct.IsCancellationRequested ? RequestStatus.ClientCancelled : RequestStatus.GatewayError;
            record.ErrorType = ex.GetType().Name;
            record.ErrorMessage = ex switch
            {
                SubscriptionAuthException => "Subscription account is missing or unusable; sign in again",
                CryptographicException => "Stored API key could not be decrypted; replace it",
                AdminApiException api => api.Message,
                OperationCanceledException => ct.IsCancellationRequested ? "Connection test cancelled" : "Connection test timed out",
                _ => "Could not connect to the upstream provider",
            };
            return new ProviderTestDto(false, watch.ElapsedMilliseconds, record.HttpStatus, endpoint.Protocol, modelId, record.ErrorMessage);
        }
        finally
        {
            record.TotalMs = watch.ElapsedMilliseconds;
            RequestBilling.Apply(record, usage, effective.Pricing, settings.Current.Locale);
            writer.Enqueue(record);
        }
    }

    public async Task<IReadOnlyList<RemoteModelDto>> ModelsAsync(string providerId, CancellationToken ct)
    {
        var p = await ProviderEndpoints.RequireAsync(db, providerId, ct);
        var snapshot = Json.Deserialize<ProviderTemplate>(p.TemplateSnapshotJson);
        var spec = snapshot?.ModelListEndpoint;
        var endpoint = spec is not null && p.EndpointFor(spec.Protocol) is { FullUrl: false } declared ? declared :
            p.PreferredUpstreamProtocols.Select(p.EndpointFor).FirstOrDefault(e => e is { FullUrl: false }) ?? p.Endpoints.FirstOrDefault(e => !e.FullUrl);
        if (endpoint is null) throw new AdminApiException(400, "A full request URL has no discoverable models URL; add a base URL or model IDs manually");
        var suffix = spec?.Protocol == endpoint.Protocol ? spec.Path :
            endpoint.Protocol == ApiProtocol.Anthropic && !new Uri(endpoint.BaseUrl).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? "/v1/models" : "/models";
        var url = AppendPath(endpoint.BaseUrl, suffix);
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var client = CreateClient(p);
            for (var page = 0; page < MaxPages; page++)
            {
                using var request = await RequestAsync(p, HttpMethod.Get, url, endpoint.Protocol, deadline.Token);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                var body = TryObject(await ReadResponseAsync(response, deadline.Token));
                if (!response.IsSuccessStatusCode) throw new AdminApiException(502, ErrorMessage(body, response.StatusCode, p, request), new { httpStatus = (int)response.StatusCode });
                if (body is null) throw InvalidList();
                var rows = body["data"] as JsonArray ?? body["models"] as JsonArray ?? throw InvalidList();
                foreach (var row in rows)
                {
                    if (row is not JsonObject item) throw InvalidList();
                    var id = Text(item["id"]) ?? Text(item["name"]);
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)) throw InvalidList();
                    if (endpoint.Protocol == ApiProtocol.Gemini && id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..];
                    if (id.Length == 0) throw InvalidList();
                    found[id] = Text(item["displayName"]) ?? Text(item["display_name"]);
                    if (found.Count > MaxModels) throw new AdminApiException(502, "Upstream model list exceeds the supported limit");
                }
                var next = Text(body["nextPageToken"]);
                var hasMore = body["has_more"] is JsonValue more && more.TryGetValue<bool>(out var b) && b;
                if (body["nextPageToken"] is not null && next is null) throw InvalidList();
                if (body["has_more"] is not null && (body["has_more"] is not JsonValue flag || !flag.TryGetValue<bool>(out _))) throw InvalidList();
                var cursor = !string.IsNullOrEmpty(next) ? next : hasMore ? Text(body["last_id"]) ?? Text((rows.LastOrDefault() as JsonObject)?["id"]) : null;
                if (hasMore && string.IsNullOrEmpty(cursor)) throw InvalidList();
                if (string.IsNullOrEmpty(cursor)) break;
                if (!cursors.Add(cursor)) throw new AdminApiException(502, "Upstream model pagination did not advance");
                if (page == MaxPages - 1) throw new AdminApiException(502, "Upstream model list has too many pages");
                url = WithQuery(url, !string.IsNullOrEmpty(next) ? "pageToken" : endpoint.Protocol == ApiProtocol.Anthropic ? "after_id" : "after", cursor);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AdminApiException(504, "Upstream model list request timed out"); }
        catch (HttpRequestException)
        { throw new AdminApiException(502, "Could not connect to the upstream provider"); }
        catch (IOException)
        { throw new AdminApiException(502, "Could not read the upstream model list"); }
        catch (SubscriptionAuthException)
        { throw new AdminApiException(409, "Subscription account is missing or unusable; sign in again"); }
        catch (CryptographicException)
        { throw new AdminApiException(409, "Stored API key could not be decrypted; replace it"); }
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var key = p.PriceKey ?? p.TemplateId;
        var index = key is null ? null : await db.Models.GetUpstreamIdIndexAsync(key, ct);
        var existing = (await db.Providers.ListModelsAsync(p.Id, ct)).Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
        return found.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new RemoteModelDto(x.Key, x.Value,
            ModelIdMatcher.Match(x.Key, candidates, index), existing.Contains(x.Key))).ToList();
    }

    private async Task<HttpRequestMessage> RequestAsync(Provider p, HttpMethod method, string url, ApiProtocol protocol, CancellationToken ct)
    {
        var credentials = await auth.ResolveAsync(p.Id, null, ct) ?? UpstreamAuth.None;
        if (credentials.QueryName is { } q && credentials.QueryValue is { } value) url = WithQuery(url, q, value);
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        foreach (var (key, val) in p.ExtraHeaders) request.Headers.TryAddWithoutValidation(key, val);
        if (protocol == ApiProtocol.Anthropic && !request.Headers.Contains("anthropic-version"))
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        if (credentials.HeaderName is { } header)
        {
            request.Headers.Remove(header);
            request.Headers.TryAddWithoutValidation(header, credentials.HeaderValue);
        }
        return request;
    }

    private HttpClient CreateClient(Provider provider)
    {
        if (provider.HttpProxy is null) return http.CreateClient(HttpClientName);
        return new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy(provider.HttpProxy), UseProxy = true, AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15), AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal static ProviderEndpoint PreferredEndpoint(Provider p) =>
        p.PreferredUpstreamProtocols.Select(p.EndpointFor).FirstOrDefault(e => e is not null)
        ?? p.Endpoints.FirstOrDefault() ?? throw new AdminApiException(400, "Provider has no endpoint");

    internal static string RequestUrl(ProviderEndpoint endpoint, string model)
    {
        if (endpoint.FullUrl) return endpoint.BaseUrl;
        var path = endpoint.Protocol switch
        {
            ApiProtocol.OpenAIChat => "/chat/completions",
            ApiProtocol.OpenAIResponses => "/responses",
            ApiProtocol.Anthropic => new Uri(endpoint.BaseUrl).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? "/messages" : "/v1/messages",
            _ => "/models/" + Uri.EscapeDataString(model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model) + ":generateContent",
        };
        return AppendPath(endpoint.BaseUrl, path);
    }

    private static string AppendPath(string baseUrl, string path)
    {
        var builder = new UriBuilder(baseUrl) { Path = new Uri(baseUrl).AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/') };
        return builder.Uri.AbsoluteUri;
    }

    private static string WithQuery(string url, string key, string value)
    {
        var builder = new UriBuilder(url);
        var query = QueryHelpers.ParseQuery(builder.Query).Where(x => x.Key != key).ToDictionary(x => x.Key, x => (string?)x.Value.ToString());
        query[key] = value;
        builder.Query = QueryHelpers.AddQueryString("", query).TrimStart('?');
        return builder.Uri.AbsoluteUri;
    }

    private static JsonObject PingBody(ApiProtocol protocol, string model) => protocol switch
    {
        ApiProtocol.OpenAIResponses => new JsonObject { ["model"] = model, ["input"] = "Say OK.", ["max_output_tokens"] = 16, ["store"] = false },
        ApiProtocol.OpenAIChat => new JsonObject
        {
            ["model"] = model,
            [model.StartsWith("gpt-5", StringComparison.Ordinal) || model.StartsWith("o1", StringComparison.Ordinal) ||
                model.StartsWith("o3", StringComparison.Ordinal) || model.StartsWith("o4", StringComparison.Ordinal) ? "max_completion_tokens" : "max_tokens"] = 16,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Say OK." }),
        },
        ApiProtocol.Gemini => new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = "Say OK." }) }),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 16 },
        },
        _ => new JsonObject
        {
            ["model"] = model, ["max_tokens"] = 16,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "Say OK." }),
        },
    };

    private static JsonObject? TryObject(string body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new AdminApiException(502, "Upstream response exceeds 4 MiB");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new AdminApiException(502, "Upstream response exceeds 4 MiB");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ErrorMessage(JsonObject? body, HttpStatusCode status, Provider provider, HttpRequestMessage request)
    {
        var message = Text((body?["error"] as JsonObject)?["message"]) ?? Text(body?["error"]) ?? Text(body?["message"]);
        var result = $"Upstream returned HTTP {(int)status}" + (message is null ? "" : ": " + message);
        // Some upstreams echo credentials in error messages. Neither API responses nor request logs may store them.
        var secrets = provider.ExtraHeaders.Values.ToList();
        foreach (var header in request.Headers.Where(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                     h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in header.Value)
            {
                secrets.Add(value);
                if (header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.IndexOf(' ') is var space && space >= 0)
                    secrets.Add(value[(space + 1)..]);
            }
        }
        foreach (var query in QueryHelpers.ParseQuery(request.RequestUri?.Query))
            if (query.Key.Contains("key", StringComparison.OrdinalIgnoreCase) || query.Key.Contains("token", StringComparison.OrdinalIgnoreCase))
                foreach (var value in query.Value) if (value is not null) secrets.Add(value);
        foreach (var secret in secrets.Where(s => s.Length > 0).Distinct().OrderByDescending(s => s.Length))
        {
            result = result.Replace(secret, "[redacted]", StringComparison.Ordinal);
            result = result.Replace(Uri.EscapeDataString(secret), "[redacted]", StringComparison.Ordinal);
        }
        return result.Length <= 1000 ? result : result[..1000];
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static AdminApiException InvalidList() => new(502, "Upstream returned an invalid model list");
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.Caching.Memory;

namespace Astra.Server.Relay;

/// <summary>
/// Model capabilities as the caller's own credential sees them: the template's model list, fetched with that credential
/// and kept briefly per credential and address (keyed by its SHA-256, never by the credential). A row's per-model
/// endpoints decide which upstream protocol the model is sent over; its limits are what a translated request's output
/// budget defaults to. A list that fails or is malformed fails the request, and nothing is cached for it.
/// </summary>
internal sealed class RelayCatalog(RelayOptions options, GatewayHttpClients http) : IDisposable
{
    private const int MaxBytes = 4 * 1024 * 1024;
    private const int MaxModels = 2000;

    private static readonly IReadOnlyDictionary<string, ProviderModel> NoModels = new Dictionary<string, ProviderModel>();

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = options.ModelCacheEntries });

    /// <summary>The caller's models by id; empty when the template has no model list.</summary>
    /// <exception cref="GatewayException">The upstream refused or failed the list, sent too much, stalled, or sent no list.</exception>
    public async Task<IReadOnlyDictionary<string, ProviderModel>> GetAsync(RelayCaller caller, CancellationToken ct)
    {
        if (caller.ModelListUrl is not { } url) return NoModels;
        if (_cache.TryGetValue(caller.CacheKey, out IReadOnlyDictionary<string, ProviderModel>? cached) && cached is not null) return cached;
        using var request = caller.Request(HttpMethod.Get, url, perRequest: false);
        caller.ModelList?.ApplyHeaders(request);
        using var response = await http.For(caller.Provider).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new GatewayException((int)response.StatusCode,
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "authentication_error" : "api_error",
                $"上游模型列表获取失败（HTTP {(int)response.StatusCode}）。");
        var body = await RelayHttp.ReadLimitedAsync(await response.Content.ReadAsStreamAsync(ct), MaxBytes,
            options.UpstreamIdleTimeoutSeconds, "模型列表", ct);
        try
        {
            var models = Parse(body.Memory, caller.Provider);
            _cache.Set(caller.CacheKey, models, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(options.ModelCacheSeconds),
            });
            return models;
        }
        finally
        {
            body.Release();
        }
    }

    /// <summary>
    /// An OpenAI / Anthropic style list (<c>data[].id</c>) or a Gemini one (<c>models[].name</c>). Every row must be an
    /// object with a distinct id; <c>supported_endpoints</c> names a row's protocols and <c>capabilities.limits</c> its
    /// context and output limits, when the upstream reports them.
    /// </summary>
    private static IReadOnlyDictionary<string, ProviderModel> Parse(ReadOnlyMemory<byte> bytes, Provider provider)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(bytes.Span);
        }
        catch (JsonException)
        {
            throw Invalid();
        }
        if (root is not JsonObject list) throw Invalid();
        var gemini = list["data"] is null && list["models"] is not null;
        if ((gemini ? list["models"] : list["data"]) is not JsonArray rows || rows.Count > MaxModels) throw Invalid();
        var models = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row is not JsonObject item || Text(item[gemini ? "name" : "id"]) is not { } id) throw Invalid();
            if (gemini && id.StartsWith("models/", StringComparison.Ordinal)) id = id["models/".Length..];
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512) throw Invalid();
            var limits = Object(Object(item["capabilities"])?["limits"]);
            var model = new ProviderModel
            {
                ProviderId = provider.Id,
                ModelId = id,
                UpstreamProtocols = ProviderProbe.SupportedProtocols(item) ?? [],
                Overrides = new ModelOverrides
                {
                    ContextWindow = Positive(limits?["max_context_window_tokens"]),
                    MaxOutputTokens = Positive(limits?["max_output_tokens"]),
                },
            };
            if (!models.TryAdd(id, model)) throw Invalid();
        }
        return models;
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Positive(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<long>(out var number) && number > 0 ? number : null;

    private static JsonObject? Object(JsonNode? value) => value switch
    {
        null => null,
        JsonObject o => o,
        _ => throw Invalid(),
    };

    private static GatewayException Invalid() => new(502, "api_error", "上游返回的模型列表格式不正确。");

    public void Dispose() => _cache.Dispose();
}

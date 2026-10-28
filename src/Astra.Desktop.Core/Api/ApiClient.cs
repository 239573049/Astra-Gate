using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Astra.Core;

namespace Astra.Desktop.Core.Api;

/// <summary>
/// The typed admin-API client every page uses (plan avalonia-client; port of <c>api()</c> / <c>apiText</c> /
/// <c>apiStream</c> / <c>apiEventStream</c> in web/src/api/client.ts).
/// <list type="bullet">
/// <item>Every call except GET carries <c>X-Astra-Admin: 1</c> (SecurityMiddleware refuses mutating /api calls without it).</item>
/// <item>JSON goes through <see cref="Json.Api"/> with source-generated metadata only (<see cref="JsonContexts"/>); a body or
/// result type must be registered in its assembly's context (Astra.Api.Contracts for DTOs), like on the server.</item>
/// <item>Failures become <see cref="ApiException"/>; cancellation by the caller stays an <see cref="OperationCanceledException"/>
/// (web: AbortError), a timeout is an <see cref="ApiException"/> with status 0.</item>
/// <item>The base URL is settable: the server port can drift at startup, so the shell calls <see cref="SetPort"/> from
/// <c>runtime.json</c>'s value and later requests follow (a request already sent keeps its URL).</item>
/// </list>
/// Differences from web: no cookie jar (a loopback desktop needs no session; off-machine servers are not supported by the
/// desktop shell), and a default <see cref="Timeout"/> (fetch has none).
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private string _baseUrl;

    /// <param name="baseUrl">Origin such as <c>http://127.0.0.1:5678</c> (a trailing slash is trimmed).</param>
    /// <param name="handler">Test seam; null uses a default <see cref="SocketsHttpHandler"/>.</param>
    public ApiClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _baseUrl = Normalize(baseUrl);
        _http = handler is null ? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan } : new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _ownsHttp = true;
    }

    /// <summary>Per-request timeout of non-streaming calls (and of a stream's time to its first byte of headers).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Current origin; assignable when the port drifts.</summary>
    public string BaseUrl
    {
        get => Volatile.Read(ref _baseUrl);
        set => Volatile.Write(ref _baseUrl, Normalize(value));
    }

    /// <summary>Points the client at <c>http://127.0.0.1:{port}</c>.</summary>
    public void SetPort(int port) => BaseUrl = $"http://127.0.0.1:{port}";

    private static string Normalize(string url) => url.TrimEnd('/');

    // ---------- helpers mirroring qs / enc ----------

    /// <summary>URL-encodes a path segment (web: <c>enc</c>).</summary>
    public static string Enc(string segment) => Uri.EscapeDataString(segment);

    /// <summary>
    /// Builds a query string like web's <c>qs</c>: null and empty values are skipped, bools are lowercase, numbers use the
    /// invariant culture. Returns "" or "?a=b&amp;c=d".
    /// </summary>
    public static string Qs(params (string Key, object? Value)[] parameters)
    {
        var sb = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            var text = value switch
            {
                null => null,
                bool b => b ? "true" : "false",
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (string.IsNullOrEmpty(text)) continue;
            sb.Append(sb.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(text));
        }
        return sb.ToString();
    }

    // ---------- JSON calls ----------

    /// <summary>GET and decode the JSON answer as <typeparamref name="T"/> (204 / empty body yield default).</summary>
    public Task<T?> GetAsync<T>(string path, CancellationToken ct = default, JsonTypeInfo<T>? info = null) =>
        SendJsonAsync<T>(HttpMethod.Get, path, null, info, ct);

    /// <summary>POST a JSON body and decode the answer.</summary>
    public Task<TRes?> PostAsync<TReq, TRes>(string path, TReq body, CancellationToken ct = default, JsonTypeInfo<TReq>? requestInfo = null, JsonTypeInfo<TRes>? info = null) =>
        SendJsonAsync<TRes>(HttpMethod.Post, path, Encode(body, requestInfo), info, ct);

    /// <summary>POST without a body and decode the answer.</summary>
    public Task<TRes?> PostAsync<TRes>(string path, CancellationToken ct = default, JsonTypeInfo<TRes>? info = null) =>
        SendJsonAsync<TRes>(HttpMethod.Post, path, null, info, ct);

    /// <summary>POST a JSON body and ignore the answer's content.</summary>
    public Task PostAsync<TReq>(string path, TReq body, CancellationToken ct = default, JsonTypeInfo<TReq>? requestInfo = null) =>
        SendRawAsync(HttpMethod.Post, path, Encode(body, requestInfo), ct);

    /// <summary>POST without a body, ignoring the answer's content.</summary>
    public Task PostAsync(string path, CancellationToken ct = default) => SendRawAsync(HttpMethod.Post, path, null, ct);

    /// <summary>PUT a JSON body and decode the answer.</summary>
    public Task<TRes?> PutAsync<TReq, TRes>(string path, TReq body, CancellationToken ct = default, JsonTypeInfo<TReq>? requestInfo = null, JsonTypeInfo<TRes>? info = null) =>
        SendJsonAsync<TRes>(HttpMethod.Put, path, Encode(body, requestInfo), info, ct);

    /// <summary>PUT a JSON body, ignoring the answer's content.</summary>
    public Task PutAsync<TReq>(string path, TReq body, CancellationToken ct = default, JsonTypeInfo<TReq>? requestInfo = null) =>
        SendRawAsync(HttpMethod.Put, path, Encode(body, requestInfo), ct);

    /// <summary>PATCH a JSON body and decode the answer.</summary>
    public Task<TRes?> PatchAsync<TReq, TRes>(string path, TReq body, CancellationToken ct = default, JsonTypeInfo<TReq>? requestInfo = null, JsonTypeInfo<TRes>? info = null) =>
        SendJsonAsync<TRes>(HttpMethod.Patch, path, Encode(body, requestInfo), info, ct);

    /// <summary>DELETE, ignoring the answer's content.</summary>
    public Task DeleteAsync(string path, CancellationToken ct = default) => SendRawAsync(HttpMethod.Delete, path, null, ct);

    /// <summary>DELETE and decode the answer.</summary>
    public Task<TRes?> DeleteAsync<TRes>(string path, CancellationToken ct = default, JsonTypeInfo<TRes>? info = null) =>
        SendJsonAsync<TRes>(HttpMethod.Delete, path, null, info, ct);

    /// <summary>GET a text resource without JSON parsing (web: <c>apiText</c>), so a JSON body stays the exact text the server sent.</summary>
    public async Task<string> GetTextAsync(string path, CancellationToken ct = default)
    {
        var (_, text) = await ExecuteAsync(HttpMethod.Get, path, null, "text/plain", ct);
        return text;
    }

    private static string? Encode<T>(T body, JsonTypeInfo<T>? info) =>
        body is null ? null : System.Text.Json.JsonSerializer.Serialize(body, info ?? JsonContexts.Info<T>(Json.Api));

    private async Task<T?> SendJsonAsync<T>(HttpMethod method, string path, string? body, JsonTypeInfo<T>? info, CancellationToken ct)
    {
        var (status, text) = await ExecuteAsync(method, path, body, "application/json", ct);
        if (status == 204 || string.IsNullOrWhiteSpace(text)) return default;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(text, info ?? JsonContexts.Info<T>(Json.Api));
        }
        catch (JsonException e)
        {
            throw new ApiException(status, "Unreadable response: " + e.Message, null, e);
        }
    }

    private async Task SendRawAsync(HttpMethod method, string path, string? body, CancellationToken ct) =>
        await ExecuteAsync(method, path, body, "application/json", ct);

    /// <summary>The one request path: headers, timeout, network and error mapping. Returns the status and body text of a success.</summary>
    private async Task<(int Status, string Text)> ExecuteAsync(HttpMethod method, string path, string? body, string accept, CancellationToken ct)
    {
        using var request = CreateRequest(method, path, body, accept);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var text = status == 204 ? "" : await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw FromBody(status, text);
            return (status, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException(0, "Request timed out");
        }
        catch (HttpRequestException e)
        {
            throw new ApiException(0, string.IsNullOrEmpty(e.Message) ? "Network error" : e.Message, null, e);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string? body, string accept)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (method != HttpMethod.Get) request.Headers.Add("X-Astra-Admin", "1");
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// Maps an error answer: <c>{ "error", "details" }</c> (AdminApiErrorFilter / ErrorOnlyDto), a JSON string, or any text;
    /// "HTTP n" when the body says nothing.
    /// </summary>
    internal static ApiException FromBody(int status, string text)
    {
        JsonNode? node = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { node = JsonNode.Parse(text); }
            catch (JsonException) { node = null; }
        }
        string? message = null;
        JsonNode? details = null;
        if (node is JsonObject obj)
        {
            if (obj["error"] is JsonValue ev && ev.TryGetValue<string>(out var s)) message = s;
            details = obj["details"]?.DeepClone();
        }
        else if (node is JsonValue v && v.TryGetValue<string>(out var str)) message = str;
        else if (node is null && !string.IsNullOrWhiteSpace(text)) message = text;
        return new ApiException(status, string.IsNullOrEmpty(message) ? $"HTTP {status}" : message, details);
    }

    // ---------- server-sent events ----------

    /// <summary>
    /// GETs an event stream (web: <c>apiEventStream</c>, e.g. <c>/api/requests/live</c>) and yields its frames until the
    /// server closes it or <paramref name="ct"/> cancels. A refusal before the stream starts is an <see cref="ApiException"/>;
    /// so is a connection that breaks mid-stream (status 0). <paramref name="onOpen"/> fires once the server accepted it.
    /// </summary>
    public IAsyncEnumerable<SseMessage> StreamAsync(string path, CancellationToken ct = default, Action? onOpen = null) =>
        ReadFramesAsync(HttpMethod.Get, path, null, onOpen, ct);

    /// <summary>
    /// POSTs a JSON body and reads the answer as an event stream (web: <c>apiStream</c>, the provider test). Failures before
    /// the stream starts (validation, auth) arrive as JSON and are reported like any other call.
    /// </summary>
    public IAsyncEnumerable<SseMessage> StreamPostAsync<TReq>(string path, TReq body, CancellationToken ct = default, Action? onOpen = null, JsonTypeInfo<TReq>? requestInfo = null) =>
        ReadFramesAsync(HttpMethod.Post, path, Encode(body, requestInfo), onOpen, ct);

    /// <summary>
    /// <see cref="StreamAsync(string, CancellationToken, Action?)"/> with each frame's data decoded as <typeparamref name="T"/>.
    /// Frames whose data is not valid <typeparamref name="T"/> JSON are skipped (the web code ignores objects without the
    /// fields it needs the same way).
    /// </summary>
    public async IAsyncEnumerable<T> StreamAsync<T>(string path, [EnumeratorCancellation] CancellationToken ct = default, Action? onOpen = null, JsonTypeInfo<T>? info = null)
    {
        var typeInfo = info ?? JsonContexts.Info<T>(Json.Api);
        await foreach (var frame in StreamAsync(path, ct, onOpen).WithCancellation(ct).ConfigureAwait(false))
            if (TryDecode(frame, typeInfo, out var value)) yield return value;
    }

    /// <summary>POST variant of <see cref="StreamAsync{T}"/>.</summary>
    public async IAsyncEnumerable<TEvent> StreamPostAsync<TReq, TEvent>(string path, TReq body, [EnumeratorCancellation] CancellationToken ct = default, Action? onOpen = null, JsonTypeInfo<TReq>? requestInfo = null, JsonTypeInfo<TEvent>? info = null)
    {
        var typeInfo = info ?? JsonContexts.Info<TEvent>(Json.Api);
        await foreach (var frame in StreamPostAsync(path, body, ct, onOpen, requestInfo).WithCancellation(ct).ConfigureAwait(false))
            if (TryDecode(frame, typeInfo, out var value)) yield return value;
    }

    /// <summary>
    /// A feed that stays up: reconnects with backoff (see <see cref="SseReconnectOptions"/>) whenever the stream ends or
    /// fails, until <paramref name="ct"/> cancels (then <see cref="OperationCanceledException"/>). Port of the loop in
    /// web/src/api/liveRequests.ts; the store reset on a drop is the caller's, through <see cref="SseReconnectOptions.OnDrop"/>.
    /// </summary>
    public async IAsyncEnumerable<T> StreamReconnectingAsync<T>(string path, SseReconnectOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default, JsonTypeInfo<T>? info = null)
    {
        options ??= new SseReconnectOptions();
        var delay = options.InitialDelay;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (options.Gate is not null) await options.Gate(ct).ConfigureAwait(false);
            var opened = false;
            Exception? failure = null;
            var enumerator = StreamAsync<T>(path, ct, () => { opened = true; options.OnOpen?.Invoke(); }, info).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool has;
                    try { has = await enumerator.MoveNextAsync().ConfigureAwait(false); }
                    catch (Exception e) when (!ct.IsCancellationRequested) { failure = e; break; }
                    if (!has) break;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            options.OnDrop?.Invoke(failure);
            if (opened) delay = options.InitialDelay;
            await Task.Delay(delay, options.Time, ct).ConfigureAwait(false);
            var doubled = TimeSpan.FromTicks(delay.Ticks * 2);
            delay = doubled > options.MaxDelay ? options.MaxDelay : doubled;
        }
    }

    private static bool TryDecode<T>(SseMessage frame, JsonTypeInfo<T> info, out T value)
    {
        try
        {
            var v = System.Text.Json.JsonSerializer.Deserialize(frame.Data, info);
            if (v is not null) { value = v; return true; }
        }
        catch (JsonException) { }
        value = default!;
        return false;
    }

    private async IAsyncEnumerable<SseMessage> ReadFramesAsync(HttpMethod method, string path, string? body, Action? onOpen, [EnumeratorCancellation] CancellationToken ct)
    {
        var (response, stream) = await OpenStreamAsync(method, path, body, ct).ConfigureAwait(false);
        try
        {
            onOpen?.Invoke();
            var decoder = Encoding.UTF8.GetDecoder();
            var parser = new SseParser();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            while (true)
            {
                var n = await ReadChunkAsync(stream, bytes, ct).ConfigureAwait(false);
                var final = n == 0;
                var count = decoder.GetChars(bytes, 0, n, chars, 0, flush: final);
                foreach (var message in parser.Feed(new string(chars, 0, count))) yield return message;
                if (final) yield break;
            }
        }
        finally
        {
            stream.Dispose();
            response.Dispose();
        }
    }

    private static async Task<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        try { return await stream.ReadAsync(buffer, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            throw new ApiException(0, string.IsNullOrEmpty(e.Message) ? "Network error" : e.Message, null, e);
        }
    }

    private async Task<(HttpResponseMessage Response, Stream Body)> OpenStreamAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        var request = CreateRequest(method, path, body, "text/event-stream");
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headerTimeout.CancelAfter(Timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(headerTimeout.Token).ConfigureAwait(false);
                throw FromBody((int)response.StatusCode, text);
            }
            var stream = await response.Content.ReadAsStreamAsync(headerTimeout.Token).ConfigureAwait(false);
            return (response, stream);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            response?.Dispose();
            throw new ApiException(0, "Request timed out");
        }
        catch (HttpRequestException e)
        {
            response?.Dispose();
            throw new ApiException(0, string.IsNullOrEmpty(e.Message) ? "Network error" : e.Message, null, e);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>Disposes the underlying <see cref="HttpClient"/>.</summary>
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

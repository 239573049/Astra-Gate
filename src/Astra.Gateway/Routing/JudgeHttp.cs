using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Routing;

/// <summary>Judge service addresses: <c>{base}/v1/{path}</c>, without doubling a <c>/v1</c> the base already ends with.</summary>
internal static class JudgeUrls
{
    public static string For(string baseUrl, string path)
    {
        var uri = new Uri(baseUrl);
        var trimmed = uri.AbsolutePath.TrimEnd('/');
        var version = trimmed.EndsWith("/v1", StringComparison.Ordinal) ? "" : "/v1";
        return new UriBuilder(uri) { Path = $"{trimmed}{version}/{path}", Query = "", Fragment = "" }.Uri.AbsoluteUri;
    }
}

/// <summary>
/// The one POST every dialect makes (plan §5.3). It goes through the judge's own client
/// (<see cref="GatewayHttpClients.ForJudge"/>: long-lived, HTTP/2 when offered), so it honours the outbound proxy
/// setting, never follows a redirect (the key is not handed to another host) and carries no trace context. The deadline
/// is this call's own; the client has none.
/// </summary>
internal static class JudgeHttp
{
    private const int MaxResponseBytes = 1024 * 1024;
    private const int MaxMessageChars = 200;

    /// <summary>
    /// Posts <paramref name="body"/> and returns the answer's text. The deadline is <see cref="JudgeEndpoint.Timeout"/>,
    /// or that plus <see cref="JudgeEndpoint.ConnectAllowance"/> when the call has to open its connection first.
    /// </summary>
    public static async Task<string> PostAsync(GatewayHttpClients clients, string url, JudgeEndpoint endpoint, JsonObject body, CancellationToken ct)
    {
        var json = new ArrayBufferWriter<byte>(1024);
        GatewayJson.WriteTo(body, json);
        using var content = new ByteArrayContent(json.WrittenSpan.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
            // HTTP/2 where the service offers it: concurrent judgements share the one warm connection.
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(endpoint.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);

        var timeout = endpoint.Timeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var connecting = 0;
        request.Options.Set(GatewayHttpClients.NewConnection, () =>
        {
            if (Interlocked.Exchange(ref connecting, 1) == 1) return;
            try
            {
                deadline.CancelAfter(timeout + JudgeEndpoint.ConnectAllowance);
            }
            catch (ObjectDisposedException)
            {
                // the call is already over
            }
            endpoint.OnNewConnection?.Invoke();
        });
        try
        {
            using var response = await clients.ForJudge().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var text = await ReadBodyAsync(response, deadline.Token);
            if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode, text);
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var waited = Volatile.Read(ref connecting) == 1 ? timeout + JudgeEndpoint.ConnectAllowance : timeout;
            throw new RoutingJudgeException(JudgeFailure.Timeout,
                $"no answer within {waited.TotalMilliseconds:0} ms{(waited > timeout ? " (including opening the connection)" : "")}");
        }
        catch (HttpRequestException e)
        {
            throw new RoutingJudgeException(JudgeFailure.Network, e.Message);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var all = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (all.Length + read > MaxResponseBytes)
                    throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer is larger than 1 MiB", (int)response.StatusCode);
                all.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return Encoding.UTF8.GetString(all.GetBuffer(), 0, (int)all.Length);
    }

    private static RoutingJudgeException Failure(HttpStatusCode status, string body)
    {
        var code = (int)status;
        var kind = code switch
        {
            401 or 403 => JudgeFailure.Auth,
            429 => JudgeFailure.RateLimited,
            >= 500 => JudgeFailure.Overloaded,
            _ => JudgeFailure.BadRequest,
        };
        var said = Said(body);
        return new RoutingJudgeException(kind, said is null ? $"the judge answered {code}" : $"the judge answered {code}: {said}", code);
    }

    /// <summary>What an error body says, as one short line: its <c>error.message</c> / <c>error</c> / <c>message</c>, else its start.</summary>
    private static string? Said(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        string? said = null;
        try
        {
            // "error" is an object in some APIs ({"error":{"message":…}}) and plain text in others ({"error":"…"}).
            if (JsonNode.Parse(body) is JsonObject o)
                said = JudgeParsing.Text((o["error"] as JsonObject)?["message"]) ?? JudgeParsing.Text(o["error"]) ?? JudgeParsing.Text(o["message"]);
        }
        catch (JsonException)
        {
            // not JSON: fall back to the raw start of the body
        }
        said ??= body;
        said = new string(said.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        if (said.Length == 0) return null;
        return said.Length <= MaxMessageChars ? said : said[..MaxMessageChars] + "…";
    }
}

/// <summary>Reading what a judge answered: tolerant of number and string forms, strict about the choice.</summary>
internal static class JudgeParsing
{
    public static JsonNode Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text) ?? throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer is empty");
        }
        catch (JsonException)
        {
            throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer is not JSON");
        }
    }

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var number) ? number : null;

    /// <summary>A token count: a non-negative whole number, else 0 (usage is informational and may be missing).</summary>
    public static int Count(JsonNode? node) => Number(node) is { } n && n >= 0 && n <= int.MaxValue ? (int)n : 0;

    /// <summary>
    /// How concentrated the probability on the likeliest option is: 1 when it holds all of it, 0 when it is spread evenly
    /// (TypeSafe's definition for a Choice question: (p_max − 1/n) / (1 − 1/n)).
    /// </summary>
    public static double Concentration(IReadOnlyDictionary<string, double> probabilities, int options)
    {
        if (probabilities.Count == 0 || options < 2) return 0;
        var top = probabilities.Values.Max();
        return Math.Clamp((top - 1.0 / options) / (1.0 - 1.0 / options), 0, 1);
    }

    /// <summary>
    /// Checks the answer against the question and builds the verdict: the choice must be one of the options, and a
    /// confidence the service did not give is worked out from the probabilities (which then must be there).
    /// </summary>
    public static JudgeVerdict Verdict(
        JudgeQuestion question, string? choice, Dictionary<string, double> probabilities, double? confidence,
        string? model, int inputTokens, int outputTokens)
    {
        if (choice is null || !question.Choices.Any(c => c.Id == choice))
            throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer is not one of the grades");
        if (confidence is null && probabilities.Count == 0)
            throw new RoutingJudgeException(JudgeFailure.BadResponse, "the answer has neither a confidence nor probabilities");
        var sure = Math.Clamp(confidence ?? Concentration(probabilities, question.Choices.Count), 0, 1);
        return new JudgeVerdict(choice, sure, probabilities, model, inputTokens, outputTokens);
    }
}

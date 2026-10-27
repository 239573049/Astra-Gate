using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Server.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Astra.Server.IntegrationTests;

/// <summary>One request the relay sent upstream.</summary>
internal sealed record RelaySent(Uri Uri, string? Authorization, byte[] Body, IReadOnlyDictionary<string, string> Headers)
{
    public string Text => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// An upstream with a model list that names each model's endpoints, and answers for the three generation protocols,
/// the embeddings and the token counting endpoint. Records everything it is sent.
/// </summary>
internal sealed class RelayUpstream : HttpMessageHandler
{
    internal const string Catalog = """
        {"data":[
          {"id":"gpt-5","supported_endpoints":["/chat/completions","/responses"]},
          {"id":"chat-only","supported_endpoints":["/chat/completions"]},
          {"id":"claude-sonnet-4.5","supported_endpoints":["/v1/messages"]},
          {"id":"gpt-5.6-sol","supported_endpoints":["/responses"]}
        ]}
        """;

    public ConcurrentQueue<RelaySent> Requests { get; } = new();
    public Func<RelaySent, CancellationToken, Task<HttpResponseMessage>>? Respond { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var sent = new RelaySent(request.RequestUri!, request.Headers.Authorization?.ToString(),
            request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct),
            request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
                .ToDictionary(pair => pair.Key, pair => string.Join(", ", pair.Value), StringComparer.OrdinalIgnoreCase));
        Requests.Enqueue(sent);
        return Respond is not null ? await Respond(sent, ct) : Default(sent);
    }

    public static HttpResponseMessage Default(RelaySent sent)
    {
        if (sent.Uri.AbsolutePath is "/models" or "/v1/models") return Json(Catalog);
        var model = JsonNode.Parse(sent.Text)?["model"]?.GetValue<string>() ?? "served-model";
        if (sent.Uri.AbsolutePath.EndsWith("/messages/count_tokens", StringComparison.Ordinal))
            return Json("""{"input_tokens":7}""");
        if (sent.Uri.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal))
            return Json("""{"object":"list","model":"served-model","data":[{"embedding":[0.1,0.2]}]}""");
        if (sent.Uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
            return Json($$$"""{"id":"msg_1","type":"message","role":"assistant","model":"{{{model}}}","content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}""");
        if (sent.Uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal))
            return Json($$$"""{"id":"resp_1","object":"response","status":"completed","model":"{{{model}}}","output":[{"type":"message","role":"assistant","id":"msg_1","content":[{"type":"output_text","text":"hello","annotations":[]}]}],"usage":{"input_tokens":2,"output_tokens":1,"total_tokens":3}}""");
        return Json($$$"""{"id":"chat_1","object":"chat.completion","model":"{{{model}}}","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}""");
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
}

/// <summary>
/// <c>astra-server relay</c> on TestServer in front of a <see cref="RelayUpstream"/>. The default template has every
/// generation protocol, a model list with per-model endpoints, plan variants and client headers.
/// </summary>
internal sealed class RelayHost(WebApplication app, HttpClient client, RelayUpstream upstream) : IAsyncDisposable
{
    public const string FullTemplate = "github-copilot-subscription";
    public const string ChatBody = """{"model":"gpt-5","messages":[{"role":"user","content":"Hi"}]}""";

    public WebApplication App { get; } = app;
    public HttpClient Client { get; } = client;
    public RelayUpstream Upstream { get; } = upstream;

    public static async Task<RelayHost> StartAsync(RelayUpstream? upstream = null, Action<RelayOptions>? configure = null,
        string template = FullTemplate)
    {
        upstream ??= new RelayUpstream();
        var options = new RelayOptions { Provider = template };
        configure?.Invoke(options);
        options.Validate();
        var app = RelayApp.Create(options, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
        }, () => upstream);
        await app.StartAsync();
        return new RelayHost(app, app.GetTestClient(), upstream);
    }

    public async Task<HttpResponseMessage> SendAsync(string path = "/v1/chat/completions", string? body = ChatBody,
        string? token = "gho_test_alice", Dictionary<string, string[]>? headers = null, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(body is null && content is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (headers is not null)
            foreach (var (name, values) in headers) request.Headers.TryAddWithoutValidation(name, values);
        request.Content = content ?? (body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"));
        return await Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

using System.Net;
using System.Text;

namespace Astra.Desktop.Tests.Plugins;

/// <summary>An <see cref="HttpMessageHandler"/> answering by "METHOD /path" (query ignored unless the route has one), recording what it was asked.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, (int Status, string Body)>> _routes = new(StringComparer.Ordinal);

    public List<Seen> Requests { get; } = [];

    public sealed record Seen(string Method, string PathAndQuery, string Body, string? Admin);

    public StubHttp On(string route, string body, int status = 200) => On(route, _ => (status, body));

    public StubHttp On(string route, Func<HttpRequestMessage, (int Status, string Body)> answer)
    {
        _routes[route] = answer;
        return this;
    }

    public int Count(string method, string path) => Requests.Count(r => r.Method == method && r.PathAndQuery.StartsWith(path, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var url = request.RequestUri!;
        lock (Requests) Requests.Add(new Seen(request.Method.Method, url.PathAndQuery, body, request.Headers.TryGetValues("X-Astra-Admin", out var v) ? v.FirstOrDefault() : null));
        if (!_routes.TryGetValue($"{request.Method.Method} {url.PathAndQuery}", out var answer) && !_routes.TryGetValue($"{request.Method.Method} {url.AbsolutePath}", out answer))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no route\"}", Encoding.UTF8, "application/json") };
        var (status, text) = answer(request);
        return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

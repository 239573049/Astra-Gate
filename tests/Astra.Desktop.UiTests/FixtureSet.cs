using System.Net;
using System.Text;
using System.Text.Json;

namespace Astra.Desktop.UiTests;

/// <summary>
/// The deterministic admin-API data set under <c>tests/fixtures/ui/&lt;set&gt;</c> (copied beside the test binaries). The route
/// table <c>index.json</c> is the same one scripts/visual/capture-web.mjs replays through Playwright, so both clients render
/// the same data. <see cref="Handler"/> serves it to an <c>ApiClient</c> (<c>new ApiClient("http://fixture", set.Handler())</c>).
/// </summary>
public sealed class FixtureSet
{
    private readonly string _dir;
    private readonly List<Route> _routes = [];

    private sealed record Route(string Method, string Path, Dictionary<string, string>? Query, string File);

    private FixtureSet(string dir)
    {
        _dir = dir;
        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "index.json")));
        Now = DateTimeOffset.Parse(index.RootElement.GetProperty("now").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        foreach (var r in index.RootElement.GetProperty("routes").EnumerateArray())
        {
            Dictionary<string, string>? query = null;
            if (r.TryGetProperty("query", out var q))
            {
                query = q.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
            }
            _routes.Add(new Route(r.GetProperty("method").GetString()!, r.GetProperty("path").GetString()!, query, r.GetProperty("file").GetString()!));
        }
    }

    /// <summary>The frozen "now" of every scene (UTC); a page that shows relative times or "today" must take it from here.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>Per-path replacement bodies for a state (empty list, error): <c>Override("/api/tokens", "[]")</c>.</summary>
    public Dictionary<string, (int Status, string Body)> Overrides { get; } = new(StringComparer.Ordinal);

    /// <summary>Paths the code asked for that no fixture answers (the handler returned 404); a test can assert it is empty.</summary>
    public List<string> Unmatched { get; } = [];

    public static FixtureSet Load(string set = "default")
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", set);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"fixture set '{set}' not found at {dir}");
        return new FixtureSet(dir);
    }

    public FixtureSet Override(string path, string body, int status = 200)
    {
        Overrides[path] = (status, body);
        return this;
    }

    /// <summary>The body for a request, or null when no fixture matches.</summary>
    public (int Status, string Body)? Answer(string method, Uri url)
    {
        if (method == "GET" && Overrides.TryGetValue(url.AbsolutePath, out var o)) return o;
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        foreach (var r in _routes)
        {
            if (r.Method != method || r.Path != url.AbsolutePath) continue;
            if (r.Query is not null && !r.Query.All(kv => query[kv.Key] == kv.Value)) continue;
            return (200, File.ReadAllText(Path.Combine(_dir, r.File)));
        }
        return null;
    }

    public HttpMessageHandler Handler()
    {
        VisualRecorder.Track(this);
        return new FixtureHandler(this);
    }

    private sealed class FixtureHandler(FixtureSet set) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            // The live request feed stays disconnected, as in the web capture. Not as a stream that opens and ends: an opened stream
            // resets the reconnect backoff to 1 s, so every second a drop invalidated the request queries and rebuilt the table, and on a
            // loaded machine the rebuilds outran the dispatcher and the capture never settled. A refused stream backs off to 15 s.
            if (url.AbsolutePath == "/api/requests/live")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{\"error\":\"no live feed in fixtures\"}", Encoding.UTF8, "application/json") });
            var hit = set.Answer(request.Method.Method, url);
            if (hit is null)
            {
                lock (set.Unmatched) set.Unmatched.Add($"{request.Method} {url.AbsolutePath}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"no fixture\"}", Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)hit.Value.Status) { Content = new StringContent(hit.Value.Body, Encoding.UTF8, "application/json") });
        }
    }
}

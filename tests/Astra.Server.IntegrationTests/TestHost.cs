using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Gateway.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Astra.Data;
using Astra.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Astra.Server.IntegrationTests;

/// <summary>One event of a streamed provider test.</summary>
public sealed record TestEvent(string Event, JsonNode Data);

/// <summary>The outcome of a provider test call: a JSON error, or the streamed events plus the "done" payload.</summary>
public sealed record TestResult(HttpStatusCode Status, IReadOnlyList<TestEvent> Events, JsonNode? Body, string? Text)
{
    /// <summary>The "done" payload; null when the call failed validation before the stream started.</summary>
    public JsonNode? Done => Body;

    public IReadOnlyList<string> Names => Events.Select(e => e.Event).ToList();
}

/// <summary>A Astra server on TestServer with a fresh data directory.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private TestHost(string root, WebApplication app, AstraDatabase db)
    {
        Root = root;
        App = app;
        Db = db;
        Client = app.GetTestClient();
        Client.DefaultRequestHeaders.Add("X-Astra-Admin", "1");
    }

    public string Root { get; }
    public string ClientHome => Path.Combine(Root, "client-home");
    public WebApplication App { get; }
    public AstraDatabase Db { get; }
    public HttpClient Client { get; }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<TestHost> StartAsync(Action<WebApplicationBuilder>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "astra-it-" + Guid.NewGuid().ToString("N"));
        var paths = new AstraPaths(root);
        var db = await AstraDatabase.InitializeAsync(paths);
        var app = AstraApp.Create(new AstraApp.BuildOptions
        {
            Paths = paths,
            Server = new ServerOptions(),
            Database = db,
            WriteRuntimeFile = false,
            ConfigureBuilder = b =>
            {
                b.WebHost.UseTestServer();
                // Even read-only inspection uses the fake home; tests never touch the user's client configs.
                b.Services.AddSingleton(new ClientEnvironment(Path.Combine(root, "client-home"), "osx"));
                configure?.Invoke(b);
            },
        });
        await app.StartAsync();
        return new TestHost(root, app, db);
    }

    public async Task<JsonNode> GetJsonAsync(string url)
    {
        var res = await Client.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"GET {url} -> {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    public async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        var res = await Client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text));
    }

    /// <summary>
    /// Runs a provider connection test. A streamed run (<c>text/event-stream</c>) comes back as its events in order,
    /// with <see cref="TestResult.Body"/> holding the "done" payload; a validation failure comes back as JSON.
    /// </summary>
    public async Task<TestResult> TestAsync(string providerId, object? body = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/providers/{providerId}/test");
        if (body is not null) req.Content = JsonContent.Create(body, options: JsonOptions);
        using var res = await Client.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        if (res.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            return new TestResult(res.StatusCode, [],
                string.IsNullOrEmpty(text) ? null : JsonNode.Parse(text), string.IsNullOrEmpty(text) ? null : text);
        }
        var events = SseParser.ParseAll(text).Select(e => new TestEvent(e.Event ?? "", JsonNode.Parse(e.Data)!)).ToList();
        return new TestResult(res.StatusCode, events, events.LastOrDefault(e => e.Event == "done")?.Data, text);
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        Client.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

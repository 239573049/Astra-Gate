using System.Text.Json.Nodes;
using Astra.Core.Plugins;
using Astra.Plugins.V2;

namespace Astra.Plugins.Tests.V2;

/// <summary>A v2 host over an in-memory plugin list and data store, with fake server services.</summary>
internal sealed class V2TestHost : IAsyncDisposable
{
    public V2TestHost()
    {
        Plugins = new TestPlugins();
        Services = new FakeV2Services();
        Host = new PluginV2Host(Plugins.Catalog, Services);
    }

    public TestPlugins Plugins { get; }
    public FakeV2Services Services { get; }
    public PluginV2Host Host { get; }

    /// <summary>A v2 plugin folder: astra.plugin.json from <paramref name="manifest"/> plus files; added, granted and loaded.</summary>
    public async Task<string> AddAsync(string id, JsonObject manifest, (string Path, string Text)[] files, bool grant = true, string? options = null)
    {
        manifest["manifestVersion"] ??= 2;
        manifest["id"] ??= id;
        manifest["version"] ??= "2.0.0";
        manifest["name"] ??= id;
        var all = files.Append((Path: "astra.plugin.json", Text: manifest.ToJsonString())).ToArray();
        var dir = Plugins.Folder(id, all);
        if (grant)
            await Services.Data.SetGrantsAsync(id, PluginV2Manifest.Strings(manifest["permissions"]));
        await Plugins.Store.AddAsync(new PluginEntry { Spec = dir, Name = id, OptionsJson = options, Enabled = true, Source = PluginSources.Path });
        Plugins.Catalog.Invalidate();
        await Host.RefreshAsync();
        return dir;
    }

    public static JsonObject Client(string locale = "en") => new()
    {
        ["shell"] = "web", ["ui"] = "1.0", ["locale"] = locale, ["theme"] = "light", ["width"] = "regular", ["surface"] = "page",
    };

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}

internal sealed class FakeV2Services : IPluginV2Services
{
    public string HostVersion { get; set; } = "0.15.0";
    public MemoryPluginDataStore Data { get; } = new();
    IPluginDataStore IPluginV2Services.Data => Data;
    public List<(string Plugin, string Title, string? Body)> Notifications { get; } = [];
    public List<string> Lines { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }

    public string Protect(string plaintext) => "enc:" + plaintext;

    public string Unprotect(string protectedValue) => protectedValue[4..];

    public HttpClient Http() => new(new Fake(this));

    public void Notify(string pluginId, string title, string? body, string tone)
    {
        lock (Notifications) Notifications.Add((pluginId, title, body));
    }

    public ValueTask<JsonNode?> QueryAsync(string what, JsonNode? args, CancellationToken ct) =>
        ValueTask.FromResult<JsonNode?>(new JsonObject { ["what"] = what });

    public void Log(string pluginId, string level, string message)
    {
        lock (Lines) Lines.Add($"{pluginId} {level}: {message}");
    }

    private sealed class Fake(FakeV2Services owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(owner.Handler?.Invoke(request) ?? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Plugins.Host;
using Astra.Plugins.Runtime;

namespace Astra.Plugins.Tests;

/// <summary>A test that needs Bun: skipped when neither ASTRA_TEST_BUN nor bun on PATH gives one.</summary>
public sealed class BunFactAttribute : FactAttribute
{
    public BunFactAttribute()
    {
        if (BunPath is null) Skip = "needs Bun (ASTRA_TEST_BUN or bun on PATH)";
    }

    public static string? BunPath { get; } = Find();

    private static string? Find()
    {
        if (Environment.GetEnvironmentVariable("ASTRA_TEST_BUN") is { Length: > 0 } b && File.Exists(b)) return b;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "bun.exe" : "bun");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }
}

/// <summary>magpie's own host.js running magpie's fake OpenCode plugin, for real.</summary>
[Collection("bun host")]
public sealed class PluginHostBunTests : IAsyncDisposable
{
    private readonly TestPlugins _plugins = new();
    private readonly HttpListener _vendor = new();
    private readonly List<(string Path, string? Auth, string Body)> _seen = [];
    private PluginHost? _host;

    private async Task<PluginHost> HostAsync()
    {
        var port = FreePort();
        _vendor.Prefixes.Add($"http://127.0.0.1:{port}/");
        _vendor.Start();
        _ = Task.Run(ServeVendorAsync);
        // the fake plugin sends its requests to $FAKE_BASE, as its loader's baseURL
        Environment.SetEnvironmentVariable("FAKE_BASE", $"http://127.0.0.1:{port}/v1");
        await _plugins.AddAsync(_plugins.Fixture("fake"));
        var bun = new BunRuntime(_plugins.Paths, () => new HttpClient(), name => name == "ASTRA_BUN" ? BunFactAttribute.BunPath : null);
        _host = new PluginHost(_plugins.Catalog, bun, new HostAssets(_plugins.Paths), _plugins.Paths, null, (_, _) => { });
        return _host;
    }

    [BunFact]
    public async Task Lists_signs_in_and_fetches_through_the_plugin()
    {
        var host = await HostAsync();

        var providers = (await host.CallAsync("providers", new JsonObject { ["proxies"] = new JsonObject() }))!.AsArray();
        var fake = providers.Single(p => p!["id"]!.GetValue<string>() == "fakeco")!;
        Assert.Equal("FakeCo", fake["name"]!.GetValue<string>());
        Assert.False(fake["signedIn"]!.GetValue<bool>());
        var models = fake["models"]!.AsArray().ToDictionary(m => m!["id"]!.GetValue<string>(), m => m!["npm"]?.GetValue<string>());
        Assert.Equal("@ai-sdk/anthropic", models["fake-claude"]);
        Assert.Equal(["api", "oauth"], fake["methods"]!.AsArray().Select(m => m!["type"]!.GetValue<string>()));
        Assert.Contains(host.Status.Plugins, p => p.Provides.Contains("fakeco"));

        var saved = (await host.CallAsync("apiKey", new JsonObject
        {
            ["provider"] = "fakeco", ["method"] = 0, ["inputs"] = new JsonObject(), ["key"] = "k-123", ["account"] = "new",
        }))!;
        Assert.True(saved["ok"]!.GetValue<bool>());
        var account = saved["account"]!.GetValue<string>();
        Assert.True(File.Exists(_plugins.Paths.PluginAuthFile));

        using var response = await host.FetchAsync(new HostFetchRequest(
            "fakeco", account, "fake-1", "@ai-sdk/openai-compatible", $"{Environment.GetEnvironmentVariable("FAKE_BASE")}/chat/completions", "POST",
            new Dictionary<string, string> { ["content-type"] = "application/json", ["x-magpie-work-dir"] = "%2Ftmp%2Fproj" },
            Encoding.UTF8.GetBytes("""{"model":"fake-1","stream":true,"messages":[]}"""), "conv-1", null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal("data: {\"n\":1}\n\ndata: {\"n\":2}\n\ndata: [DONE]\n\n", text);
        var seen = Assert.Single(_seen);
        Assert.Equal("/v1/chat/completions", seen.Path);
        Assert.Equal("Bearer k-123", seen.Auth);
        Assert.Contains("\"model\":\"fake-1\"", seen.Body);

        await host.CallAsync("signOut", new JsonObject { ["provider"] = "fakeco", ["account"] = account });
        var after = (await host.CallAsync("providers", new JsonObject()))!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "fakeco")!;
        Assert.False(after["signedIn"]!.GetValue<bool>());
    }

    [BunFact]
    public async Task Prompts_and_a_pasted_code_sign_in()
    {
        var host = await HostAsync();

        var first = (await host.CallAsync("prompt", new JsonObject { ["provider"] = "fakeco", ["method"] = 1, ["inputs"] = new JsonObject() }))!;
        Assert.Equal("where", first["prompt"]!["key"]!.GetValue<string>());
        var second = (await host.CallAsync("prompt", new JsonObject { ["provider"] = "fakeco", ["method"] = 1, ["inputs"] = new JsonObject { ["where"] = "work" } }))!;
        Assert.Equal("team", second["prompt"]!["key"]!.GetValue<string>());
        var invalid = (await host.CallAsync("validate", new JsonObject { ["provider"] = "fakeco", ["method"] = 1, ["key"] = "team", ["value"] = "" }))!;
        Assert.Equal("Required", invalid["error"]!.GetValue<string>());

        var auth = (await host.CallAsync("authorize", new JsonObject
        {
            ["provider"] = "fakeco", ["method"] = 1, ["inputs"] = new JsonObject { ["where"] = "work", ["team"] = "dev" }, ["account"] = "new",
        }))!;
        Assert.Equal("code", auth["method"]!.GetValue<string>());
        Assert.StartsWith("https://fake.invalid/auth?where=work", auth["url"]!.GetValue<string>());

        var done = (await host.CallAsync("callback", new JsonObject { ["session"] = auth["session"]!.DeepClone(), ["code"] = "good" }))!;
        Assert.True(done["ok"]!.GetValue<bool>());
        var listed = (await host.CallAsync("providers", new JsonObject()))!.AsArray().Single(p => p!["id"]!.GetValue<string>() == "fakeco")!;
        Assert.Equal("dev@fake", listed["accounts"]![0]!["accountId"]!.GetValue<string>());

        var usage = (await host.CallAsync("usage", new JsonObject { ["provider"] = "fakeco", ["account"] = done["account"]!.DeepClone() }))!;
        Assert.Equal("Fake Pro", usage["plan"]!.GetValue<string>());
    }

    [BunFact]
    public async Task The_gateways_transport_reaches_the_vendor_through_the_plugin()
    {
        var host = await HostAsync();
        var providers = new PluginProviders(host);
        var saved = (await host.CallAsync("apiKey", new JsonObject
        {
            ["provider"] = "fakeco", ["method"] = 0, ["inputs"] = new JsonObject(), ["key"] = "k-9", ["account"] = "new",
        }))!;
        var handler = new PluginFetchHandler(providers);
        var told = new List<(string, string, string, int)>();
        handler.SignIn += (p, a, s, st) => told.Add((p, a, s, st));
        using var client = new HttpClient(handler);

        Assert.Equal(Core.ApiProtocol.Anthropic, PluginProviders.ProtocolOf("fakeco", (await providers.ListAsync()).Single(p => p.Id == "fakeco").Model("fake-claude")!));
        using var request = new HttpRequestMessage(HttpMethod.Post, PluginFetchHandler.BaseFor("fakeco", Core.ApiProtocol.OpenAIChat) + "/chat/completions")
        {
            Content = new StringContent("""{"model":"fake-1","stream":true}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-astra-should-not-leave");
        request.Headers.TryAddWithoutValidation(PluginFetchHandler.ConversationHeader, "conv-7");
        request.Options.Set(PluginFetchHandler.AccountOption, saved["account"]!.GetValue<string>());

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data: [DONE]", await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(_seen);
        Assert.Equal("/v1/chat/completions", seen.Path);
        Assert.Equal("Bearer k-9", seen.Auth);
        Assert.Equal(("fakeco", saved["account"]!.GetValue<string>(), "", 200), Assert.Single(told));
    }

    private async Task ServeVendorAsync()
    {
        while (_vendor.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _vendor.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
            lock (_seen) _seen.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["authorization"], body));
            ctx.Response.ContentType = "text/event-stream";
            var bytes = Encoding.UTF8.GetBytes("data: {\"n\":1}\n\ndata: {\"n\":2}\n\ndata: [DONE]\n\n");
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        if (_vendor.IsListening) _vendor.Stop();
        _vendor.Close();
    }
}

[CollectionDefinition("bun host", DisableParallelization = true)]
public sealed class BunHostCollection;

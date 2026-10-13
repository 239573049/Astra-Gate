using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Plugins;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Astra.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>A test that needs Bun: skipped when neither ASTRA_TEST_BUN nor bun on PATH gives one.</summary>
public sealed class BunFactAttribute : FactAttribute
{
    public BunFactAttribute()
    {
        if (BunPath is null) Skip = "needs Bun (ASTRA_TEST_BUN or bun on PATH)";
    }

    public static string? BunPath { get; } =
        Environment.GetEnvironmentVariable("ASTRA_TEST_BUN") is { Length: > 0 } b && File.Exists(b) ? b
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "bun.exe" : "bun")).FirstOrDefault(File.Exists);
}

[CollectionDefinition("provider plugins", DisableParallelization = true)]
public sealed class ProviderPluginCollection;

/// <summary>An OpenCode provider plugin (magpie's fake one) served through Astra's gateway, end to end on real Bun.</summary>
[Collection("provider plugins")]
public sealed class ProviderPluginGatewayTests : IAsyncDisposable
{
    private readonly HttpListener _vendor = new();
    private readonly List<(string Path, string? Auth, string Body)> _seen = [];

    [BunFact]
    public async Task A_plugins_provider_is_synced_signed_in_to_and_used_by_a_client()
    {
        var port = FreePort();
        _vendor.Prefixes.Add($"http://127.0.0.1:{port}/");
        _vendor.Start();
        _ = Task.Run(ServeVendorAsync);
        Environment.SetEnvironmentVariable("FAKE_BASE", $"http://127.0.0.1:{port}/v1");
        Environment.SetEnvironmentVariable("ASTRA_BUN", BunFactAttribute.BunPath);
        try
        {
            await using var host = await TestHost.StartAsync();
            var plugin = Path.Combine(host.Root, "plugin-src", "fake");
            Directory.CreateDirectory(plugin);
            File.Copy(Path.Combine(RepoRoot(), "tests", "Astra.Plugins.Tests", "Fixtures", "magpie", "fake", "index.js"), Path.Combine(plugin, "index.js"));
            await host.Db.Plugins.AddAsync(new PluginEntry { Spec = plugin, Name = "fake" });
            host.App.Services.GetRequiredService<PluginCatalog>().Invalidate();

            var synced = await host.SendAsync(HttpMethod.Post, "/api/plugin-providers/plugin-fakeco/sync");
            Assert.Equal(HttpStatusCode.OK, synced.Status);
            var provider = (await host.Db.Providers.GetAsync("plugin-fakeco"))!;
            Assert.Equal(AuthSchemes.Plugin, provider.AuthScheme);
            Assert.Equal("FakeCo", provider.Name);
            var models = (await host.Db.Providers.ListModelsAsync(provider.Id)).ToDictionary(m => m.ModelId);
            Assert.Equal([ApiProtocol.Anthropic], models["fake-claude"].UpstreamProtocols);
            Assert.Equal([ApiProtocol.OpenAIChat], models["fake-1"].UpstreamProtocols);
            Assert.Equal([ApiProtocol.Gemini], models["fake-gemini"].UpstreamProtocols);

            var methods = (await host.GetJsonAsync("/api/plugin-providers/plugin-fakeco/methods")).AsArray();
            Assert.Equal("api", methods[0]!["type"]!.GetValue<string>());
            var signedIn = await host.SendAsync(HttpMethod.Post, "/api/plugin-providers/plugin-fakeco/api-key", new { method = 0, key = "vendor-key-1" });
            Assert.True(signedIn.Status == HttpStatusCode.OK, signedIn.Body?.ToJsonString());
            var account = Assert.Single(await host.Db.Accounts.ListAsync(provider.Id));
            Assert.Equal(AccountKinds.Plugin, account.Kind);
            Assert.Equal(account.Id, signedIn.Body!["account"]!.GetValue<string>());

            // the provider page's model list is the plugin's, not an upstream /models over plugin://
            var remote = (await host.GetJsonAsync("/api/providers/plugin-fakeco/remote-models")).AsArray();
            Assert.Contains(remote, m => m!["id"]!.GetValue<string>() == "fake-claude"
                                         && m["upstreamProtocols"]![0]!.GetValue<string>() == "anthropic");

            // a client bound to it: the plugin's fetch carries its sign-in to the vendor
            var protector = host.App.Services.GetRequiredService<ISecretProtector>();
            var token = protector.Unprotect((await host.Db.Tokens.GetAsync(TokenIds.Default))!.KeyEnc!);
            await host.Db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.OpenCode, Enabled = true, TokenId = TokenIds.Default });
            await host.Db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.OpenCode, ProviderId = provider.Id });
            var key = GatewayTokens.ForClient(token, ClientKinds.OpenCode);

            var chat = await PostAsync(host, key, "/v1/chat/completions",
                """{"model":"fake-1","messages":[{"role":"system","content":"Working directory: /x"},{"role":"user","content":"hi"}]}""");
            Assert.True(chat.StatusCode == HttpStatusCode.OK, await chat.Content.ReadAsStringAsync());
            Assert.Contains("\"content\":\"OK\"", await chat.Content.ReadAsStringAsync());
            var counted = await PostAsync(host, key, "/v1/messages/count_tokens", """{"model":"fake-claude","messages":[{"role":"user","content":"hi"}]}""");
            Assert.True(counted.StatusCode == HttpStatusCode.OK, await counted.Content.ReadAsStringAsync());
            Assert.Contains("input_tokens", await counted.Content.ReadAsStringAsync());
            var first = _seen[^1];
            Assert.Equal("/v1/chat/completions", first.Path);
            Assert.Equal("Bearer vendor-key-1", first.Auth);
            Assert.DoesNotContain(token, first.Body);

            // a model the plugin speaks Anthropic's API to: the chat request is translated on the way
            var translated = await PostAsync(host, key, "/v1/chat/completions", """{"model":"fake-claude","messages":[{"role":"user","content":"hi"}]}""");
            Assert.True(translated.StatusCode == HttpStatusCode.OK, await translated.Content.ReadAsStringAsync());
            Assert.Equal("/v1/messages", _seen[^1].Path);
            Assert.Contains("\"content\":\"OK\"", await translated.Content.ReadAsStringAsync());

            var signOut = await host.SendAsync(HttpMethod.Post, $"/api/plugin-providers/plugin-fakeco/accounts/{account.Id}/sign-out");
            Assert.Equal(HttpStatusCode.OK, signOut.Status);
            Assert.Empty(await host.Db.Accounts.ListAsync(provider.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASTRA_BUN", null);
        }
    }

    [BunFact]
    public async Task Installing_or_switching_off_a_provider_plugin_syncs_its_providers_by_itself()
    {
        Environment.SetEnvironmentVariable("ASTRA_BUN", BunFactAttribute.BunPath);
        try
        {
            await using var host = await TestHost.StartAsync();
            var plugin = Path.Combine(host.Root, "plugin-src", "fake");
            Directory.CreateDirectory(plugin);
            File.Copy(Path.Combine(RepoRoot(), "tests", "Astra.Plugins.Tests", "Fixtures", "magpie", "fake", "index.js"), Path.Combine(plugin, "index.js"));
            await host.Db.Plugins.AddAsync(new PluginEntry { Spec = plugin, Name = "fake" });
            host.App.Services.GetRequiredService<PluginCatalog>().Invalidate();

            // no explicit sync: the plugin list changing is enough
            var provider = await WaitForAsync(async () => await host.Db.Providers.GetAsync("plugin-fakeco"), p => p is { Enabled: true });
            Assert.Equal(AuthSchemes.Plugin, provider!.AuthScheme);

            var off = await host.SendAsync(HttpMethod.Patch, "/api/plugins?spec=" + Uri.EscapeDataString(plugin), new { enabled = false });
            Assert.Equal(HttpStatusCode.OK, off.Status);
            await WaitForAsync(async () => await host.Db.Providers.GetAsync("plugin-fakeco"), p => p is { Enabled: false });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASTRA_BUN", null);
        }
    }

    private static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var value = await read();
            if (done(value)) return value;
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting; last: {value}");
            await Task.Delay(200);
        }
    }

    [BunFact]
    public async Task Daily_check_in_asks_each_account_once_a_day_and_retries_failures_later()
    {
        var presses = Path.Combine(Path.GetTempPath(), "astra-checkin-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("FAKE_CHECKIN", presses);
        Environment.SetEnvironmentVariable("ASTRA_BUN", BunFactAttribute.BunPath);
        try
        {
            await using var host = await TestHost.StartAsync();
            var plugin = Path.Combine(host.Root, "plugin-src", "fake");
            Directory.CreateDirectory(plugin);
            File.Copy(Path.Combine(RepoRoot(), "tests", "Astra.Plugins.Tests", "Fixtures", "magpie", "fake", "index.js"), Path.Combine(plugin, "index.js"));
            await host.Db.Plugins.AddAsync(new PluginEntry { Spec = plugin, Name = "fake" });
            host.App.Services.GetRequiredService<PluginCatalog>().Invalidate();
            await host.SendAsync(HttpMethod.Post, "/api/plugin-providers/plugin-fakeco/sync");
            foreach (var key in new[] { "ck-claim", "ck-throw", "ck-odd" })
                Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Post, "/api/plugin-providers/plugin-fakeco/api-key", new { method = 0, key })).Status);

            var state = await host.GetJsonAsync("/api/plugin-providers/plugin-fakeco/checkin");
            Assert.True(state["supported"]!.GetValue<bool>());
            Assert.True(state["on"]!.GetValue<bool>()); // on by default
            var checkins = host.App.Services.GetRequiredService<Api.PluginCheckinService>();
            await host.SendAsync(HttpMethod.Put, "/api/plugin-providers/plugin-fakeco/checkin", new { on = false });
            await checkins.RunDueAsync(CancellationToken.None);
            Assert.False(File.Exists(presses)); // nothing while switched off

            await host.SendAsync(HttpMethod.Put, "/api/plugin-providers/plugin-fakeco/checkin", new { on = true });
            await checkins.RunDueAsync(CancellationToken.None);
            await checkins.RunDueAsync(CancellationToken.None); // settled for the day; the failure waits 30 minutes
            Assert.Equal(3, File.ReadAllLines(presses).Length);

            var accounts = (await host.GetJsonAsync("/api/plugin-providers/plugin-fakeco/checkin"))["accounts"]!.AsArray();
            var byOutcome = accounts.Select(a => a!["outcome"]!.GetValue<string>()).Order().ToList();
            Assert.Equal(["claimed", "failed", "failed"], byOutcome);
            var claimed = accounts.Single(a => a!["outcome"]!.GetValue<string>() == "claimed")!;
            Assert.Equal(50, claimed["credit"]!.GetValue<double>());
            Assert.Equal(3, claimed["streak"]!.GetValue<int>());
            // the host itself turns an outcome it doesn't know into a failure that says what came
            Assert.Contains(accounts, a => a!["message"]?.GetValue<string>() == "the plugin answered no outcome (\"yes\")");
            Assert.Contains(accounts, a => a!["message"]?.GetValue<string>()?.Contains("check-in is down") == true);

            // "check in now" asks every account again
            await host.SendAsync(HttpMethod.Post, "/api/plugin-providers/plugin-fakeco/checkin");
            Assert.Equal(6, File.ReadAllLines(presses).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_CHECKIN", null);
            Environment.SetEnvironmentVariable("ASTRA_BUN", null);
            File.Delete(presses);
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(TestHost host, string key, string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await host.Client.SendAsync(request);
    }

    // FakeCo's vendor: a chat completion or an Anthropic message, whichever was asked.
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
            ctx.Response.ContentType = "application/json";
            var answer = ctx.Request.Url!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal)
                ? """{"id":"msg_1","type":"message","role":"assistant","model":"fake-claude","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}"""
                : """{"id":"c1","object":"chat.completion","created":1,"model":"fake-1","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";
            var bytes = Encoding.UTF8.GetBytes(answer);
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

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Astra.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    public ValueTask DisposeAsync()
    {
        if (_vendor.IsListening) _vendor.Stop();
        _vendor.Close();
        return ValueTask.CompletedTask;
    }
}

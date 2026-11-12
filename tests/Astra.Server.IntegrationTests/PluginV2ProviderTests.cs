using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Plugins;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>v2 providers end to end (design/plugin-v2 08 §2): row sync, accounts, the gateway through the plugin's prepare, refresh on 401.</summary>
public class PluginV2ProviderTests
{
    private const string Main = """
        import { definePlugin, commands, providers } from 'astra';
        export default definePlugin({
          activate() {
            providers.register('acme', {
              async prepare(req, account) {
                return { url: 'https://api.acme.example/v1/chat/completions', headers: { authorization: 'Bearer ' + (await account.secret()), 'x-acme-model': req.model } };
              },
              async models() { return [{ id: 'acme-small', protocol: 'chat' }]; },
              async refresh() { return 'expired'; },
            });
            commands.register('login', async () => {
              await providers.accounts.add('acme', { key: 'k1', displayName: 'Ada', secret: 'vendor-secret' });
              return (await providers.accounts.list('acme')).length;
            });
          },
        });
        """;

    private sealed class Upstream : HttpMessageHandler
    {
        public List<(string Url, string? Auth, string Body, string? Model)> Seen { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), await request.Content!.ReadAsStringAsync(ct),
                request.Headers.TryGetValues("x-acme-model", out var m) ? m.First() : null));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent("""{"id":"c1","object":"chat.completion","model":"acme-large","choices":[{"index":0,"message":{"role":"assistant","content":"hi from acme"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}""",
                    Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task A_v2_provider_serves_gateway_requests_through_its_prepare()
    {
        await using var host = await TestHost.StartAsync();
        var upstream = new Upstream();
        host.App.Services.GetRequiredService<PluginsV2.PluginV2HostServices>().TestHandler = upstream;
        var dir = Path.Combine(host.Root, "acme-provider");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), Main);
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), """
            { "manifestVersion": 2, "id": "acme.cloud", "version": "1.0.0", "name": "Acme Cloud", "main": "main.js",
              "activation": ["onStartup"],
              "permissions": ["providers", "net:https://api.acme.example"],
              "contributes": {
                "providers": [{ "id": "acme", "name": "Acme", "protocols": ["chat"], "models": [{ "id": "acme-large", "protocol": "chat", "context": 200000 }] }],
                "commands": [{ "id": "login", "title": "Log in" }]
              } }
            """);
        await host.Db.Plugins.AddAsync(new PluginEntry { Spec = dir, Name = "acme.cloud", Source = PluginSources.Path });
        host.App.Services.GetRequiredService<Astra.Plugins.PluginCatalog>().Invalidate();
        var (granted, _) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.cloud/grant", new { permissions = new[] { "providers", "net:https://api.acme.example" } });
        Assert.Equal(HttpStatusCode.OK, granted);

        // the row and its models: the manifest's and the code's
        var sync = host.App.Services.GetRequiredService<PluginsV2.PluginV2ProviderSync>();
        await sync.SyncAsync();
        var rowId = PluginsV2.PluginV2ProviderSync.RowId("acme.cloud", "acme");
        Assert.Equal("plugin2-acme-cloud-acme", rowId);
        var row = (await host.Db.Providers.GetAsync(rowId))!;
        Assert.Equal(AuthSchemes.Plugin, row.AuthScheme);
        Assert.StartsWith("plugin://v2.acme.cloud.acme", row.Endpoints[0].BaseUrl);
        Assert.Equal(["acme-large", "acme-small"], (await host.Db.Providers.ListModelsAsync(rowId)).Select(m => m.ModelId).Order());

        // an account, signed in by the plugin; its secret never reaches the row
        var (ran, count) = await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.cloud/commands/login", new { });
        Assert.Equal(HttpStatusCode.OK, ran);
        Assert.Equal(1, count!["result"]!.GetValue<int>());
        var account = Assert.Single(await host.Db.Accounts.ListAsync(rowId));
        Assert.Equal("Ada", account.DisplayName);
        Assert.DoesNotContain("vendor-secret", account.Extra.ToJsonString());

        // a client bound to it: the gateway sends through prepare, with the account's credential
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var token = protector.Unprotect((await host.Db.Tokens.GetAsync(TokenIds.Default))!.KeyEnc!);
        await host.Db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.OpenCode, Enabled = true, TokenId = TokenIds.Default });
        await host.Db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.OpenCode, ProviderId = rowId });
        var key = GatewayTokens.ForClient(token, ClientKinds.OpenCode);
        using var chat = await PostAsync(host, key, """{"model":"acme-large","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        Assert.Contains("hi from acme", await chat.Content.ReadAsStringAsync());
        var sent = Assert.Single(upstream.Seen);
        Assert.Equal("https://api.acme.example/v1/chat/completions", sent.Url);
        Assert.Equal("Bearer vendor-secret", sent.Auth);
        Assert.Equal("acme-large", sent.Model);
        Assert.Contains("\"acme-large\"", sent.Body);

        // refused upstream: the plugin's refresh says the sign-in expired, and the account is marked
        upstream.Status = HttpStatusCode.Unauthorized;
        using var refused = await PostAsync(host, key, """{"model":"acme-large","messages":[{"role":"user","content":"hi"}]}""");
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        for (var i = 0; i < 20 && (await host.Db.Accounts.ListAsync(rowId)).Single().Status != AccountStatus.Expired; i++) await Task.Delay(100);
        Assert.Equal(AccountStatus.Expired, (await host.Db.Accounts.ListAsync(rowId)).Single().Status);
    }

    private sealed class FuncHandler(Func<HttpResponseMessage> make) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(make());
    }

    private const string EnvelopeMain = """
        import { definePlugin, commands, providers } from 'astra';
        export default definePlugin({
          activate() {
            providers.register('acme', {
              async prepare(req, account) { return { url: 'https://api.acme.example/stream', headers: { authorization: 'Bearer ' + (await account.secret()) } }; },
              transformResponse: {
                start(res) { if (res.status === 402) return { error: { status: 429, message: 'quota used up' } }; },
                frame(f, ctx) {
                  const text = JSON.parse(f.json.body).t;
                  if (!ctx.request.stream) { ctx.state.text = (ctx.state.text || '') + text; return []; }
                  return { id: 'c1', object: 'chat.completion.chunk', model: ctx.request.model, choices: [{ index: 0, delta: { content: text }, finish_reason: null }] };
                },
                end(ctx) {
                  return ctx.request.stream
                    ? { id: 'c1', object: 'chat.completion.chunk', model: ctx.request.model, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }], usage: { prompt_tokens: 2, completion_tokens: 3, total_tokens: 5 } }
                    : { id: 'c1', object: 'chat.completion', model: ctx.request.model, choices: [{ index: 0, message: { role: 'assistant', content: ctx.state.text }, finish_reason: 'stop' }], usage: { prompt_tokens: 2, completion_tokens: 3, total_tokens: 5 } };
                },
              },
            });
            commands.register('login', async () => { await providers.accounts.add('acme', { key: 'k1', displayName: 'Ada', secret: 'vendor-secret' }); return 1; });
          },
        });
        """;

    [Fact]
    public async Task A_provider_with_a_reply_block_gets_its_envelope_translated_for_streaming_and_plain_requests()
    {
        await using var host = await TestHost.StartAsync();
        var status = HttpStatusCode.OK;
        host.App.Services.GetRequiredService<PluginsV2.PluginV2HostServices>().TestHandler = new FuncHandler(() =>
            new HttpResponseMessage(status)
            {
                Content = new StringContent(string.Concat(new[] { "Hel", "lo" }.Select(t => "data: " + new JsonObject { ["statusCodeValue"] = 200, ["body"] = $"{{\"t\":\"{t}\"}}" }.ToJsonString() + "\n\n")),
                    Encoding.UTF8, "text/event-stream"),
            });
        var dir = Path.Combine(host.Root, "acme-envelope");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "main.js"), EnvelopeMain);
        File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), """
            { "manifestVersion": 2, "id": "acme.cloud", "version": "1.0.0", "name": "Acme Cloud", "main": "main.js",
              "activation": ["onStartup"],
              "permissions": ["providers", "net:https://api.acme.example"],
              "contributes": {
                "providers": [{ "id": "acme", "name": "Acme", "protocols": ["chat"], "reply": { "framing": "sse", "output": "chat" },
                                "models": [{ "id": "acme-large", "protocol": "chat" }] }],
                "commands": [{ "id": "login", "title": "Log in" }]
              } }
            """);
        await host.Db.Plugins.AddAsync(new PluginEntry { Spec = dir, Name = "acme.cloud", Source = PluginSources.Path });
        host.App.Services.GetRequiredService<Astra.Plugins.PluginCatalog>().Invalidate();
        await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.cloud/grant", new { permissions = new[] { "providers", "net:https://api.acme.example" } });
        await host.App.Services.GetRequiredService<PluginsV2.PluginV2ProviderSync>().SyncAsync();
        var rowId = PluginsV2.PluginV2ProviderSync.RowId("acme.cloud", "acme");
        await host.SendAsync(HttpMethod.Post, "/api/plugins/v2/acme.cloud/commands/login", new { });
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var token = protector.Unprotect((await host.Db.Tokens.GetAsync(TokenIds.Default))!.KeyEnc!);
        await host.Db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.OpenCode, Enabled = true, TokenId = TokenIds.Default });
        await host.Db.Clients.SetBindingAsync(new ClientBinding { ClientKind = ClientKinds.OpenCode, ProviderId = rowId });
        var key = GatewayTokens.ForClient(token, ClientKinds.OpenCode);

        using var streamed = await PostAsync(host, key, """{"model":"acme-large","stream":true,"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        var sse = await streamed.Content.ReadAsStringAsync();
        Assert.Contains("\"content\":\"Hel\"", sse);
        Assert.Contains("\"content\":\"lo\"", sse);
        Assert.Contains("[DONE]", sse);

        using var plain = await PostAsync(host, key, """{"model":"acme-large","messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        var body = JsonNode.Parse(await plain.Content.ReadAsStringAsync())!;
        Assert.Equal("Hello", body["choices"]![0]!["message"]!["content"]!.GetValue<string>());

        // the plugin turns the vendor's 402 into a 429 the gateway sees (and classifies for failover)
        status = HttpStatusCode.PaymentRequired;
        using var quota = await PostAsync(host, key, """{"model":"acme-large","messages":[{"role":"user","content":"hi"}]}""");
        Assert.NotEqual(HttpStatusCode.OK, quota.StatusCode);
        Assert.Contains("quota used up", await quota.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> PostAsync(TestHost host, string key, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        return await host.Client.SendAsync(request);
    }

    [Fact]
    public async Task Upgrading_a_v1_provider_plugin_moves_its_bindings_and_pools_and_rollback_moves_them_back()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var id in new[] { "plugin-acme", "plugin2-acme-cloud-acme" })
            await host.Db.Providers.InsertWithModelsAsync(new Provider { Id = id, Name = id, AuthScheme = AuthSchemes.Plugin, Enabled = true, CreatedAt = DateTimeOffset.UtcNow }, []);
        await host.Db.Clients.ReplaceBindingsAsync(ClientKinds.OpenCode, [new ClientBinding { ClientKind = ClientKinds.OpenCode, ProviderId = "plugin-acme" }]);
        var token = (await host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.PoolJson = Json.Serialize(new Astra.Core.Tokens.TokenPool
        {
            Members = [new() { ProviderId = "plugin-acme", AccountId = "old-account" }, new() { ProviderId = "other" }],
        });
        await host.Db.Tokens.UpdateAsync(token);

        var v2 = host.App.Services.GetRequiredService<PluginsV2.PluginV2Service>();
        Assert.Equal(2, await v2.RepointAsync("plugin-acme", "plugin2-acme-cloud-acme", CancellationToken.None));
        var binding = Assert.Single(await host.Db.Clients.ListBindingsAsync(ClientKinds.OpenCode));
        Assert.Equal("plugin2-acme-cloud-acme", binding.ProviderId);
        Assert.Null(binding.AccountId);
        var pool = Astra.Core.Tokens.TokenPool.Parse((await host.Db.Tokens.GetAsync(TokenIds.Default))!.PoolJson)!;
        Assert.Equal(["plugin2-acme-cloud-acme", "other"], pool.Members.Select(m => m.ProviderId));

        await v2.RepointAsync("plugin2-acme-cloud-acme", "plugin-acme", CancellationToken.None);
        Assert.Equal("plugin-acme", Assert.Single(await host.Db.Clients.ListBindingsAsync(ClientKinds.OpenCode)).ProviderId);
    }
}

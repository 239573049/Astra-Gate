using System.Net;
using System.Text;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Providers.Subscription;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>The unified account import (<c>/api/providers/{id}/accounts/import</c>) and per-account proxy settings.</summary>
public class AccountImportTests
{
    private sealed class Upstream(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<TestHost> StartAsync(Upstream upstream) => await TestHost.StartAsync(builder =>
    {
        builder.Services.AddHttpClient(OAuthClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
        builder.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
        builder.Services.AddHttpClient(ProviderProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => upstream);
    });

    private static async Task<string> CreateAsync(TestHost host, string template)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = template });
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task Kimi_Imports_Api_Keys_And_Refresh_Tokens_Side_By_Side()
    {
        const string goodKey = "sk-kimi-good-0123456789abcdef";
        const string badKey = "sk-kimi-bad-0123456789abcdef";
        const string refresh = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJrIn0.refresh";
        var upstream = new Upstream(request =>
        {
            var auth = request.Headers.Authorization?.Parameter;
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/oauth/token" => Json("""{"access_token":"at-kimi","refresh_token":"rt-kimi-2","expires_in":3600}"""),
                "/coding/v1/me" when auth == "at-kimi" => Json("""{"email":"k@example.com","user_level_name":"Allegretto"}"""),
                "/coding/v1/usages" when auth == badKey => Json("""{"error":"unauthorized"}""", HttpStatusCode.Unauthorized),
                "/coding/v1/usages" => Json("""{"usage":{"limit":"100","used":"10","remaining":"90"}}"""),
                _ => Json("{}", HttpStatusCode.NotFound),
            };
        });
        await using var host = await StartAsync(upstream);
        var providerId = await CreateAsync(host, "kimi-subscription");

        var (status, info) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{providerId}/accounts/import-info");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(info!["apiKey"]!.GetValue<bool>());
        Assert.Contains("kimi-api-key", info["formats"]!.AsArray().Select(f => f!.GetValue<string>()));

        var text = $"{goodKey}\n{refresh}\n{goodKey}\n{badKey}\nnope";
        var url = $"/api/providers/{providerId}/accounts/import";

        // Dry run: classification and de-duplication only — nothing leaves the machine, nothing is written.
        (status, var preview) = await host.SendAsync(HttpMethod.Post, url, new { text, dryRun = true });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["ready", "ready", "duplicate", "ready", "invalid_format"],
            preview!["items"]!.AsArray().Select(i => i!["status"]!.GetValue<string>()));
        Assert.Equal(["api_key", "refresh_token", "api_key", "api_key"],
            preview["items"]!.AsArray().Take(4).Select(i => i!["kind"]!.GetValue<string>()));
        Assert.Empty(upstream.Paths);
        Assert.Empty(await host.Db.Accounts.ListAsync(providerId));

        (status, var result) = await host.SendAsync(HttpMethod.Post, url, new { text, httpProxy = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["imported", "imported", "duplicate", "failed", "invalid_format"],
            result!["items"]!.AsArray().Select(i => i!["status"]!.GetValue<string>()));
        Assert.Contains("API Key", result["items"]![3]!["message"]!.GetValue<string>());
        var raw = result.ToJsonString();
        foreach (var secret in new[] { goodKey, badKey, refresh }) Assert.DoesNotContain(secret, raw);

        // The rejected key is not kept; the good key and the refresh-token login are, each with its kind and source.
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        var accounts = await host.Db.Accounts.ListAsync(providerId);
        Assert.Equal(2, accounts.Count);
        var key = accounts.Single(a => a.Kind == AccountKinds.ApiKey);
        Assert.Equal(goodKey, protector.Unprotect(key.AccessTokenEnc!));
        Assert.Null(key.ExpiresAtUtc);
        Assert.Equal(AccountSources.Batch, key.Source);
        var login = accounts.Single(a => a.Kind == AccountKinds.OAuth);
        Assert.Equal("k@example.com", login.AccountEmail);
        Assert.Equal("rt-kimi-2", protector.Unprotect(login.RefreshTokenEnc!));

        // The list shows the key's mask, never the key.
        (status, var list) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{providerId}/accounts");
        var keyDto = list!.AsArray().Single(a => a!["kind"]!.GetValue<string>() == "api_key")!;
        Assert.Equal("sk-k••••cdef", keyDto["keyMasked"]!.GetValue<string>());
        Assert.DoesNotContain(goodKey, list.ToJsonString());

        // Importing the same person again through a new refresh token updates the existing account.
        (status, result) = await host.SendAsync(HttpMethod.Post, url, new { text = refresh + "-other" });
        Assert.Equal("updated", result!["items"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(2, (await host.Db.Accounts.ListAsync(providerId)).Count);

        // A key account has no token to refresh.
        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/provider-accounts/{key.Id}/refresh");
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Claude_Setup_Tokens_Are_Kept_Without_A_Quota_Probe()
    {
        var upstream = new Upstream(_ => Json("{}", HttpStatusCode.Forbidden));
        await using var host = await StartAsync(upstream);
        var providerId = await CreateAsync(host, "claude-subscription");

        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/import",
            new { text = "sk-ant-oat01-setup-token-abcdefghijkl", httpProxy = "http://127.0.0.1:7890" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("imported", result!["items"]![0]!["status"]!.GetValue<string>());
        Assert.Equal("setup_token", result["items"]![0]!["kind"]!.GetValue<string>());

        // The usage endpoint does not accept the setup-token's scope: it is never asked (a 403 would kill the account).
        Assert.DoesNotContain(upstream.Paths, p => p.Contains("usage"));
        var account = Assert.Single(await host.Db.Accounts.ListAsync(providerId));
        Assert.Equal(AccountStatus.Active, account.Status);
        Assert.Null(account.RefreshTokenEnc);
        Assert.True(account.ExpiresAtUtc > DateTimeOffset.UtcNow.AddDays(300));
        Assert.Equal(AccountSources.Token, account.Source);
        Assert.Equal("http://127.0.0.1:7890", account.HttpProxy);

        // Anthropic API keys and API-key mode are refused on a Claude subscription.
        (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/import",
            new { text = "sk-ant-api03-abcdefghijklmnop" });
        Assert.Equal("invalid_format", result!["items"]![0]!["status"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{providerId}/accounts/import",
            new { text = "anything-long-enough", asApiKey = true });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Import_And_Proxy_Settings_Validate_Their_Input()
    {
        await using var host = await StartAsync(new Upstream(_ => Json("{}")));
        var claude = await CreateAsync(host, "claude-subscription");
        var url = $"/api/providers/{claude}/accounts/import";

        var (status, body) = await host.SendAsync(HttpMethod.Post, url, new { text = " \n# nothing\n" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, body) = await host.SendAsync(HttpMethod.Post, url, new { text = "sk-ant-ort01-x", httpProxy = "127.0.0.1:7890" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("代理地址无效", body!["error"]!.GetValue<string>());
        var tooMany = string.Join('\n', Enumerable.Range(0, SubscriptionEndpoints.MaxBatchImport + 1).Select(i => $"sk-ant-ort01-{i:D20}"));
        (status, _) = await host.SendAsync(HttpMethod.Post, url, new { text = tooMany });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers/missing/accounts/import", new { text = "x" });
        Assert.Equal(HttpStatusCode.NotFound, status);

        // A bad proxy never starts a login.
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{claude}/accounts/login", new { httpProxy = "ftp://x" });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // Per-account proxy: set, validated, cleared with "".
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-1", ProviderId = claude, DisplayName = "me", Status = AccountStatus.Active,
        });
        (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/provider-accounts/acc-1", new { httpProxy = "socks5://10.0.0.2:1080" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("socks5://10.0.0.2:1080", body!.AsArray().Single()!["httpProxy"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/provider-accounts/acc-1", new { httpProxy = "nope" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/provider-accounts/acc-1", new { httpProxy = "" });
        Assert.Null(body!.AsArray().Single()!["httpProxy"]);
        Assert.Null((await host.Db.Accounts.GetAsync("acc-1"))!.HttpProxy);

        // Non-subscription providers import nothing.
        await host.Db.Providers.InsertAsync(new Provider { Id = "plain", Name = "Plain", AuthScheme = AuthSchemes.Bearer });
        (status, body) = await host.SendAsync(HttpMethod.Get, "/api/providers/plain/accounts/import-info");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body!["supported"]!.GetValue<bool>());
    }
}

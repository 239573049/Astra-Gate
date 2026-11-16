using System.Net;
using Astra.Core;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;

namespace Astra.Server.IntegrationTests;

/// <summary>The Claude bridge's admin surface (linked update 36): accounts are added opt-in, with the sign-in's own directory and no credential of ours.</summary>
public class ClaudeBridgeApiTests
{
    private static async Task<(TestHost Host, Provider Provider)> StartAsync(bool withProvider = true)
    {
        var host = await TestHost.StartAsync();
        var provider = new Provider
        {
            Id = "claude", Name = "Claude", TemplateId = SubscriptionSupport.ClaudeSubscriptionKey, AuthScheme = AuthSchemes.OAuthSubscription,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://api.anthropic.com/v1" }],
        };
        if (withProvider) await host.Db.Providers.InsertAsync(provider);
        return (host, provider);
    }

    [Fact]
    public async Task An_Account_Is_Added_Opt_In_With_Its_Own_Directory_And_Shows_As_One()
    {
        var (host, provider) = await StartAsync();
        await using var disposeHost = host;
        var (status, body) = await host.SendAsync(HttpMethod.Get, "/api/claude-bridge");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(provider.Id, body!["claudeProviderId"]!.GetValue<string>());
        Assert.Equal("claude-code-only", body["clientPolicy"]!.GetValue<string>()); // the existing default is untouched
        Assert.Equal(6, body["settings"]!["maxRuns"]!.GetValue<int>());

        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/claude-bridge/accounts", new { configDir = "relative/dir" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/claude-bridge/accounts", new { httpProxy = "socks5://127.0.0.1:1080" });
        Assert.Equal(HttpStatusCode.BadRequest, status); // claude takes no SOCKS proxy

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/claude-bridge/accounts", new { displayName = "work", configDir = "/data/work-claude", httpProxy = "http://127.0.0.1:8080" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("claude_bridge", body!["kind"]!.GetValue<string>());
        Assert.Equal("/data/work-claude", body["claudeConfigDir"]!.GetValue<string>());
        Assert.Equal("work", body["displayName"]!.GetValue<string>());
        var stored = (await host.Db.Accounts.GetAsync(body["id"]!.GetValue<string>()))!;
        Assert.True(stored.IsClaudeBridge);
        Assert.Null(stored.AccessTokenEnc); // no credential of ours: the sign-in is claude's own
        Assert.Equal("/data/work-claude", stored.ClaudeBridgeConfigDir);

        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/claude-bridge/accounts/{stored.Id}", new { configDir = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", (await host.Db.Accounts.GetAsync(stored.Id))!.ClaudeBridgeConfigDir);
    }

    [Fact]
    public async Task Without_A_Claude_Subscription_Provider_There_Is_Nothing_To_Add_To()
    {
        var (host, _) = await StartAsync(withProvider: false);
        await using var disposeHost = host;
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/claude-bridge/accounts", new { });
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task The_Settings_Are_Validated_And_The_Program_Path_Must_Exist()
    {
        var (host, _) = await StartAsync();
        await using var disposeHost = host;
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/claude-bridge/settings", new { maxRuns = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await host.SendAsync(HttpMethod.Put, "/api/claude-bridge/settings", new { binaryPath = "/definitely/not/here/claude" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, var body) = await host.SendAsync(HttpMethod.Put, "/api/claude-bridge/settings", new { maxRuns = 3, idleMinutes = 20 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body!["maxRuns"]!.GetValue<int>());
        Assert.Equal(20, body["idleMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_Bridge_Account_Is_Never_Probed_For_Quota_Or_Renewed()
    {
        var (host, provider) = await StartAsync();
        await using var disposeHost = host;
        var account = new ProviderAccount { Id = "b", ProviderId = provider.Id, DisplayName = "b", Kind = AccountKinds.ClaudeBridge, Status = AccountStatus.Active };
        Assert.False(Astra.Providers.Subscription.SubscriptionQuotaService.CanProbe(provider, account));
        Assert.False(Astra.Providers.Subscription.SubscriptionTokenService.IsFresh(account, DateTimeOffset.UtcNow)); // so no background round touches it
        Assert.False(Astra.Providers.Subscription.SubscriptionTokenService.CanRefresh(account));
    }
}

using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>A client bound to several providers falls back along its bindings when one fails (GitHub issue #28).</summary>
public class ClientProviderFailoverTests
{
    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-x","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string Body = """{"model":"gpt-x","messages":[{"role":"user","content":"hi"}]}""";

    private static HttpResponseMessage Down(HttpStatusCode status) =>
        FakeUpstream.Json(new JsonObject { ["error"] = new JsonObject { ["message"] = "down", ["type"] = "server_error" } }.ToJsonString(), status);

    /// <summary>The fixture's provider (upstream.test) bound first, second.test behind it; both serve gpt-x.</summary>
    private static async Task<(GatewayFixture Gw, Provider Second)> StartAsync(Func<FakeUpstream.Seen, HttpResponseMessage> respond)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond);
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        var second = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Second",
            AuthScheme = AuthSchemes.Bearer,
            ApiKeyEnc = protector.Protect("sk-second"),
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://second.test/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await gw.Host.Db.Providers.InsertAsync(second);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "gpt-x" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = second.Id, ModelId = "gpt-x" });
        await gw.Host.Db.Clients.ReplaceBindingsAsync(ClientKinds.OpenCode,
            [new ClientBinding { ProviderId = gw.Provider.Id }, new ClientBinding { ProviderId = second.Id }]);
        return (gw, second);
    }

    private static string[] Hosts(GatewayFixture gw) => gw.Upstream.Requests.Select(r => r.Url.Host).ToArray();

    [Fact]
    public async Task A_Failing_Bound_Provider_Hands_The_Request_To_The_Next_One()
    {
        var (gw, second) = await StartAsync(seen => seen.Url.Host == "upstream.test" ? Down(HttpStatusCode.ServiceUnavailable) : FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["upstream.test", "second.test"], Hosts(gw));
        Assert.Equal("Bearer sk-second", gw.Upstream.Requests[1].Headers["authorization"]);
        Assert.Equal(second.Id, (await gw.RecordOfAsync(response)).ProviderId);
    }

    [Fact]
    public async Task When_Every_Bound_Provider_Fails_The_Last_Error_Is_The_Answer()
    {
        var (gw, second) = await StartAsync(_ => Down(HttpStatusCode.BadGateway));
        await using var _ = gw;

        var response = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["upstream.test", "second.test"], Hosts(gw));
        Assert.Equal(second.Id, (await gw.RecordOfAsync(response)).ProviderId);
    }

    [Fact]
    public async Task A_Bad_Request_Is_Not_Failed_Over()
    {
        var (gw, _) = await StartAsync(_ => Down(HttpStatusCode.BadRequest));
        await using var _gw = gw;

        Assert.Equal(HttpStatusCode.BadRequest, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task With_The_Setting_Off_The_First_Provider_Answers_Whatever_It_Returns()
    {
        var (gw, _) = await StartAsync(seen => seen.Url.Host == "upstream.test" ? Down(HttpStatusCode.ServiceUnavailable) : FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        await gw.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(s => s.ClientProviderFailover = false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        Assert.Equal(["upstream.test"], Hosts(gw));
    }

    [Fact]
    public async Task A_Provider_That_Does_Not_Answer_In_Time_Hands_The_Request_On()
    {
        var (gw, second) = await StartAsync(_ => FakeUpstream.Json(ChatCompletion));
        await using var _ = gw;
        gw.Upstream.Before = (seen, ct) => seen.Url.Host == "upstream.test" ? Task.Delay(TimeSpan.FromSeconds(30), ct) : Task.CompletedTask;
        gw.Provider.Settings[Provider.FirstByteTimeoutKey] = 1;
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);

        var response = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["upstream.test", "second.test"], Hosts(gw));
        Assert.Equal(second.Id, (await gw.RecordOfAsync(response)).ProviderId);
    }

    [Fact]
    public async Task Without_Another_Provider_A_First_Byte_Timeout_Is_A_504()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        gw.Upstream.Before = (_, ct) => Task.Delay(TimeSpan.FromSeconds(30), ct);
        gw.Provider.Settings[Provider.FirstByteTimeoutKey] = 1;
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);

        var response = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }
}

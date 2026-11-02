using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Provider-qualified model ids (GitHub issue #21) through the real gateway: lists, routing, log and settings.</summary>
public class QualifiedModelGatewayTests
{
    private const string ChatCompletion =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-x","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private static string Chat(string model) =>
        new JsonObject { ["model"] = model, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }) }.ToJsonString();

    /// <summary>The fixture's provider ("Fake upstream", alias "fake") and a second one; both serve "shared", only the second "only-second".</summary>
    private static async Task<(GatewayFixture Gw, Provider Second)> StartAsync()
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ChatCompletion));
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        gw.Provider.Settings[Provider.AliasKey] = "fake";
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var second = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Second upstream",
            AuthScheme = AuthSchemes.Bearer,
            ApiKeyEnc = protector.Protect("sk-second-secret"),
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://second.test/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        await gw.Host.Db.Providers.InsertAsync(second);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "shared" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = second.Id, ModelId = "shared" });
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = second.Id, ModelId = "only-second" });
        await gw.Host.Db.Clients.ReplaceBindingsAsync(ClientKinds.OpenCode,
            [new ClientBinding { ProviderId = gw.Provider.Id }, new ClientBinding { ProviderId = second.Id }]);
        return (gw, second);
    }

    private static async Task<List<string>> ModelIdsAsync(GatewayFixture gw)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        var list = JsonNode.Parse(await (await gw.Host.Client.SendAsync(request)).Content.ReadAsStringAsync())!["data"]!.AsArray();
        return list.Select(m => m!["id"]!.GetValue<string>()).ToList();
    }

    [Fact]
    public async Task Off_Nothing_Changes_And_A_Qualified_Id_Is_Just_An_Unknown_Model()
    {
        var (gw, _) = await StartAsync();
        await using var _gw = gw;
        Assert.Equal(["shared", "only-second"], await ModelIdsAsync(gw));
        var response = await gw.PostAsync("/v1/chat/completions", Chat("shared@fake"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // No switch, no split: the id goes upstream as written, to the primary.
        var seen = gw.Upstream.Requests[^1];
        Assert.Equal("upstream.test", seen.Url.Host);
        Assert.Equal("shared@fake", seen.Json["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task On_The_List_Splits_By_Provider_And_A_Qualified_Id_Reaches_That_Provider()
    {
        var (gw, second) = await StartAsync();
        await using var _gw = gw;
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Patch, "/api/settings", new { providerQualifiedModels = true });
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(["shared@fake", "shared@Second-upstream", "only-second@Second-upstream"], await ModelIdsAsync(gw));

        foreach (var (id, host, upstreamModel, providerId) in new[]
                 {
                     ("shared@Second-upstream", "second.test", "shared", second.Id),
                     ("shared@fake", "upstream.test", "shared", gw.Provider.Id),
                     ("only-second@Second-upstream", "second.test", "only-second", second.Id),
                 })
        {
            var response = await gw.PostAsync("/v1/chat/completions", Chat(id));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var seen = gw.Upstream.Requests[^1];
            Assert.Equal(host, seen.Url.Host);
            Assert.Equal(upstreamModel, seen.Json["model"]!.GetValue<string>());
            var record = await gw.RecordOfAsync(response);
            Assert.Equal(providerId, record.ProviderId);
            Assert.Equal(id, record.RequestedModel);
        }

        // A bare id still routes by priority, so configs written before the switch keep working.
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Chat("shared"))).StatusCode);
        Assert.Equal("upstream.test", gw.Upstream.Requests[^1].Url.Host);
    }

    [Fact]
    public async Task A_Provider_Without_The_Model_Answers_With_A_Clear_Error()
    {
        var (gw, _) = await StartAsync();
        await using var _gw = gw;
        await gw.Host.SendAsync(HttpMethod.Patch, "/api/settings", new { providerQualifiedModels = true });
        var before = gw.Upstream.Requests.Count;
        var response = await gw.PostAsync("/v1/chat/completions", Chat("only-second@fake"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var message = await response.Content.ReadAsStringAsync();
        Assert.Contains("Fake upstream", message);
        Assert.Contains("only-second", message);
        Assert.Equal(before, gw.Upstream.Requests.Count);
    }

    [Fact]
    public async Task The_Separator_Is_Configurable_And_Validated()
    {
        var (gw, _) = await StartAsync();
        await using var _gw = gw;
        var (ok, _) = await gw.Host.SendAsync(HttpMethod.Patch, "/api/settings", new { providerQualifiedModels = true, modelProviderSeparator = "::" });
        Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Equal(["shared::fake", "shared::Second-upstream", "only-second::Second-upstream"], await ModelIdsAsync(gw));
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Chat("shared::Second-upstream"))).StatusCode);
        Assert.Equal("second.test", gw.Upstream.Requests[^1].Url.Host);

        var (bad, _) = await gw.Host.SendAsync(HttpMethod.Patch, "/api/settings", new { modelProviderSeparator = "a b" });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        var (settings, body) = await gw.Host.SendAsync(HttpMethod.Get, "/api/settings", null);
        Assert.Equal(HttpStatusCode.OK, settings);
        Assert.Equal("::", body!["modelProviderSeparator"]!.GetValue<string>());
        Assert.True(body!["providerQualifiedModels"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_Alias_Is_Validated_On_The_Provider()
    {
        var (gw, _) = await StartAsync();
        await using var _gw = gw;
        var (ok, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/providers/{gw.Provider.Id}", new { settings = new { alias = "zc" } });
        Assert.Equal(HttpStatusCode.OK, ok);
        var (bad, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/providers/{gw.Provider.Id}", new { settings = new { alias = new string('x', 65) } });
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        var (number, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/providers/{gw.Provider.Id}", new { settings = new { alias = 5 } });
        Assert.Equal(HttpStatusCode.BadRequest, number);
    }
}

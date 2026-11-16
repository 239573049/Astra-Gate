using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Several API keys on one provider (<c>Provider.KeyPool</c>): the key list API, selection, failover, weights, backup.</summary>
public class ProviderKeysTests
{
    private const string Chat =
        """{"id":"c1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}""";

    private const string Body = """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}]}""";

    private static string BodyOf(int n) => $$"""{"model":"gpt-5","messages":[{"role":"user","content":"topic {{n}}"}]}""";

    private static string Bearer(FakeUpstream.Seen seen) => seen.Headers["authorization"].Replace("Bearer ", "");

    private static string[] Used(GatewayFixture gw) => gw.Upstream.Requests.Select(Bearer).ToArray();

    private static Task<GatewayFixture> StartAsync(Func<FakeUpstream.Seen, HttpResponseMessage>? respond = null) =>
        GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond ?? (_ => FakeUpstream.Json(Chat)));

    private static async Task<JsonArray> AddKeyAsync(GatewayFixture gw, object body)
    {
        var (status, list) = await gw.Host.SendAsync(HttpMethod.Post, $"/api/providers/{gw.Provider.Id}/keys", body);
        Assert.Equal(HttpStatusCode.OK, status);
        return list!.AsArray();
    }

    private static async Task SetSwitchModeAsync(GatewayFixture gw, string mode)
    {
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Put, $"/api/providers/{gw.Provider.Id}/subscription-policy", new { switchMode = mode });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Adding_A_Second_Key_Moves_The_Single_Key_Into_The_List_And_Keeps_The_Provider_Row_Mirroring_The_First()
    {
        await using var gw = await StartAsync();
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        var before = (await gw.Host.Db.Providers.GetAsync(gw.Provider.Id))!.ApiKeyEnc;

        var list = await AddKeyAsync(gw, new { key = "sk-second-key-0000", name = "backup", weight = 3 });

        Assert.Equal(2, list.Count);
        Assert.Equal("Key 1", list[0]!["displayName"]!.GetValue<string>());
        Assert.Equal("backup", list[1]!["displayName"]!.GetValue<string>());
        Assert.Equal(3, list[1]!["weight"]!.GetValue<int>());
        Assert.Equal("api_key", list[1]!["kind"]!.GetValue<string>());
        Assert.DoesNotContain("sk-upstream-secret", list.ToJsonString());
        Assert.DoesNotContain("sk-second-key-0000", list.ToJsonString());
        Assert.Contains("0000", list[1]!["keyMasked"]!.GetValue<string>());

        var stored = (await gw.Host.Db.Providers.GetAsync(gw.Provider.Id))!;
        Assert.True(stored.KeyPool);
        Assert.Equal(before, stored.ApiKeyEnc); // the first enabled key, as an older binary reads it
        Assert.Equal("sk-upstream-secret", protector.Unprotect(stored.ApiKeyEnc!));
        Assert.Equal("failover", SubscriptionSupport.SwitchModeOf(stored));

        // the same secret twice is refused; a plain key edit on the provider is too
        var (dup, _) = await gw.Host.SendAsync(HttpMethod.Post, $"/api/providers/{gw.Provider.Id}/keys", new { key = "sk-second-key-0000" });
        Assert.Equal(HttpStatusCode.Conflict, dup);
        var (edit, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/providers/{gw.Provider.Id}", new { apiKey = "sk-x" });
        Assert.Equal(HttpStatusCode.Conflict, edit);
    }

    [Fact]
    public async Task An_Auth_Rejected_Key_Is_Cooled_And_The_Request_Moves_On_Without_The_Client_Seeing_It()
    {
        await using var gw = await StartAsync(seen => Bearer(seen) == "sk-upstream-secret"
            ? FakeUpstream.Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized)
            : FakeUpstream.Json(Chat));
        await AddKeyAsync(gw, new { key = "sk-second-key-0000" });

        var response = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accounts = await gw.Host.Db.Accounts.ListAsync(gw.Provider.Id);
        var first = accounts[0];
        var second = accounts[1];
        Assert.Equal(second.Id, (await gw.RecordOfAsync(response)).AccountId);
        Assert.NotNull(first.CooldownUntilUtc);
        Assert.StartsWith("401 ", first.LastError);

        // the rejected key is skipped from now on
        await gw.PostAsync("/v1/chat/completions", BodyOf(2));
        Assert.Equal(["sk-upstream-secret", "sk-second-key-0000", "sk-second-key-0000"], Used(gw));
    }

    [Fact]
    public async Task A_Rate_Limited_Key_Rests_And_The_Next_Key_Answers()
    {
        await using var gw = await StartAsync(seen => Bearer(seen) == "sk-upstream-secret"
            ? FakeUpstream.Json("""{"error":{"message":"slow down"}}""", HttpStatusCode.TooManyRequests, ("retry-after", "120"))
            : FakeUpstream.Json(Chat));
        await AddKeyAsync(gw, new { key = "sk-second-key-0000" });

        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        var first = (await gw.Host.Db.Accounts.ListAsync(gw.Provider.Id))[0];
        Assert.True(first.CooldownUntilUtc > DateTimeOffset.UtcNow);
        Assert.StartsWith("429 ", first.LastError);
    }

    [Fact]
    public async Task When_Every_Key_Is_Rejected_The_Upstream_Answer_Goes_Back()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized));
        await AddKeyAsync(gw, new { key = "sk-second-key-0000" });

        var response = await gw.PostAsync("/v1/chat/completions", Body);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(2, gw.Upstream.Requests.Count); // each key was tried once, no more
    }

    [Fact]
    public async Task A_Server_Error_Is_Not_Retried_On_Another_Key()
    {
        await using var gw = await StartAsync(_ => FakeUpstream.Json("""{"error":{"message":"boom"}}""", HttpStatusCode.InternalServerError));
        await AddKeyAsync(gw, new { key = "sk-second-key-0000" });

        Assert.Equal(HttpStatusCode.InternalServerError, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task Manual_Mode_Pins_The_Chosen_Key_And_Never_Switches()
    {
        await using var gw = await StartAsync(seen => Bearer(seen) == "sk-second-key-0000"
            ? FakeUpstream.Json("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized)
            : FakeUpstream.Json(Chat));
        var list = await AddKeyAsync(gw, new { key = "sk-second-key-0000" });
        await SetSwitchModeAsync(gw, "manual");
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Post, $"/api/providers/{gw.Provider.Id}/keys/{list[1]!["id"]!.GetValue<string>()}/activate");
        Assert.Equal(HttpStatusCode.OK, status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.PostAsync("/v1/chat/completions", Body)).StatusCode);
        Assert.Equal(["sk-second-key-0000"], Used(gw));
    }

    [Fact]
    public async Task A_Key_Made_For_Another_Protocol_Is_Left_Out_And_The_Provider_Needs_An_Endpoint_For_It()
    {
        await using var gw = await StartAsync();
        var (bad, _) = await gw.Host.SendAsync(HttpMethod.Post, $"/api/providers/{gw.Provider.Id}/keys", new { key = "sk-ant-0000", protocol = "anthropic" });
        Assert.Equal(HttpStatusCode.BadRequest, bad); // the provider has no Anthropic endpoint

        gw.Provider.Endpoints.Add(new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://upstream.test" });
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var list = await AddKeyAsync(gw, new { key = "sk-ant-0000", protocol = "anthropic" });
        Assert.Equal("anthropic", list[1]!["protocol"]!.GetValue<string>());

        // an OpenAI chat request never carries the Anthropic key
        await gw.PostAsync("/v1/chat/completions", Body);
        await gw.PostAsync("/v1/chat/completions", BodyOf(2));
        Assert.All(Used(gw), key => Assert.Equal("sk-upstream-secret", key));
    }

    [Fact]
    public async Task Balanced_Mode_Shares_Requests_By_Weight()
    {
        await using var gw = await StartAsync();
        await AddKeyAsync(gw, new { key = "sk-heavy-0000", weight = 3 });
        await SetSwitchModeAsync(gw, "balanced");

        for (var i = 0; i < 8; i++) Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/chat/completions", BodyOf(i))).StatusCode);

        var heavy = Used(gw).Count(k => k == "sk-heavy-0000");
        var light = Used(gw).Count(k => k == "sk-upstream-secret");
        Assert.Equal(8, heavy + light);
        Assert.True(light >= 1 && heavy >= 2 * light, $"heavy={heavy} light={light}");
    }

    [Fact]
    public async Task Disabling_Or_Removing_Keys_Keeps_The_Provider_Row_In_Step()
    {
        await using var gw = await StartAsync();
        var protector = gw.Host.App.Services.GetRequiredService<ISecretProtector>();
        var list = await AddKeyAsync(gw, new { key = "sk-second-key-0000" });
        var (a, b) = (list[0]!["id"]!.GetValue<string>(), list[1]!["id"]!.GetValue<string>());

        var (status, _) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/providers/{gw.Provider.Id}/keys/{a}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, status);
        var row = (await gw.Host.Db.Providers.GetAsync(gw.Provider.Id))!;
        Assert.Equal("sk-second-key-0000", protector.Unprotect(row.ApiKeyEnc!));

        // order: the second key first brings it back to the front of the failover order
        (status, _) = await gw.Host.SendAsync(HttpMethod.Put, $"/api/providers/{gw.Provider.Id}/keys/order", new { ids = new[] { b, a } });
        Assert.Equal(HttpStatusCode.OK, status);

        (status, _) = await gw.Host.SendAsync(HttpMethod.Delete, $"/api/providers/{gw.Provider.Id}/keys/{b}");
        Assert.Equal(HttpStatusCode.OK, status);
        row = (await gw.Host.Db.Providers.GetAsync(gw.Provider.Id))!;
        Assert.Null(row.ApiKeyEnc); // the one left is off
        (status, _) = await gw.Host.SendAsync(HttpMethod.Delete, $"/api/providers/{gw.Provider.Id}/keys/{a}");
        Assert.Equal(HttpStatusCode.OK, status);
        row = (await gw.Host.Db.Providers.GetAsync(gw.Provider.Id))!;
        Assert.False(row.KeyPool); // an emptied list is a plain provider again
        Assert.Null(row.ApiKeyEnc);
    }

    [Fact]
    public async Task A_Settings_Write_Cannot_Drop_The_Key_List_Flag()
    {
        await using var gw = await StartAsync();
        await AddKeyAsync(gw, new { key = "sk-second-key-0000" });
        var (status, body) = await gw.Host.SendAsync(HttpMethod.Patch, $"/api/providers/{gw.Provider.Id}", new { settings = new { } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["settings"]!["key_pool"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Backup_Round_Trips_The_Key_List_And_A_Version_1_File_Still_Restores()
    {
        JsonObject file;
        await using (var source = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(Chat)))
        {
            await AddKeyAsync(source, new { key = "sk-second-key-0000", name = "backup", weight = 3 });
            var (status, exported) = await source.Host.SendAsync(HttpMethod.Post, "/api/providers/backup/export", new { secrets = "plaintext" });
            Assert.Equal(HttpStatusCode.OK, status);
            file = exported!.AsObject();
            Assert.Equal(2, file["version"]!.GetValue<int>());
            Assert.Equal(2, file["providers"]![0]!["keys"]!.AsArray().Count);
            Assert.Equal("sk-upstream-secret", file["providers"]![0]!["api_key"]!.GetValue<string>()); // a reader without the list still gets one key
        }

        await using var target = await TestHost.StartAsync();
        var (imported, result) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/import", new { file });
        Assert.Equal(HttpStatusCode.OK, imported);
        Assert.Single(result!["added"]!.AsArray());
        var provider = (await target.Db.Providers.ListAsync()).Single();
        Assert.True(provider.KeyPool);
        var keys = (await target.Db.Accounts.ListAsync(provider.Id)).Where(a => a.Kind == AccountKinds.ApiKey).ToList();
        Assert.Equal(["Key 1", "backup"], keys.Select(k => k.DisplayName));
        Assert.Equal(3, keys[1].KeyWeight);
        var protector = target.App.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal("sk-upstream-secret", protector.Unprotect(provider.ApiKeyEnc!));

        // a version-1 file (one api_key, no list) restores into a plain single-key provider
        var v1 = JsonNode.Parse(file.ToJsonString())!.AsObject();
        v1["version"] = 1;
        v1["providers"]![0]!.AsObject().Remove("keys");
        v1["providers"]![0]!["name"] = "Old format";
        await using var legacy = await TestHost.StartAsync();
        (imported, result) = await legacy.SendAsync(HttpMethod.Post, "/api/providers/backup/import", new { file = v1 });
        Assert.Equal(HttpStatusCode.OK, imported);
        var plain = (await legacy.Db.Providers.ListAsync()).Single();
        Assert.False(plain.KeyPool);
        Assert.Equal("sk-upstream-secret", legacy.App.Services.GetRequiredService<ISecretProtector>().Unprotect(plain.ApiKeyEnc!));
    }

    [Fact]
    public async Task Duplicating_A_Provider_Copies_Its_Keys()
    {
        await using var gw = await StartAsync();
        await AddKeyAsync(gw, new { key = "sk-second-key-0000" });
        var (status, copy) = await gw.Host.SendAsync(HttpMethod.Post, $"/api/providers/{gw.Provider.Id}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);
        var keys = (await gw.Host.Db.Accounts.ListAsync(copy!["id"]!.GetValue<string>())).Where(a => a.Kind == AccountKinds.ApiKey).ToList();
        Assert.Equal(2, keys.Count);
    }
}

using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>The smart-routing admin API (plan §5.7): what can be saved, what switches it on, and what never comes back out.</summary>
public class SmartRoutingApiTests
{
    private const string Put = "PUT";

    private static async Task<Provider> SeedModelsAsync(TestHost host, params string[] models)
    {
        var provider = new Provider
        {
            Id = Ulid.NewUlid(), Name = "Models provider",
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://models.test/v1" }],
        };
        await host.Db.Providers.InsertAsync(provider);
        foreach (var model in models) await host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = provider.Id, ModelId = model });
        return provider;
    }

    private static Task<(HttpStatusCode Status, JsonNode? Body)> UpdateAsync(TestHost host, object body) =>
        host.SendAsync(HttpMethod.Put, "/api/smart-routing", body);

    private static object CompleteSetup(bool enabled = true) => new
    {
        enabled,
        judge = new { apiKey = "sk-judge-abcdefghijklmnop" },
        grades = new[]
        {
            new { id = "light", name = "Light", description = "Simple things.", model = "small-model" },
            new { id = "standard", name = "Standard", description = "Everyday things.", model = "mid-model" },
            new { id = "heavy", name = "Heavy", description = "Hard things.", model = "big-model" },
        },
        rules = new { fallbackGrade = "standard" },
    };

    private static string[] ProblemCodes(JsonNode? body) =>
        body!["details"]!["problems"]!.AsArray().Select(p => p!["code"]!.GetValue<string>()).ToArray();

    // ---------------------------------------------------------------- defaults

    [Fact]
    public async Task It_Is_Off_By_Default_And_The_Defaults_Are_Served()
    {
        await using var host = await TestHost.StartAsync();

        var s = await host.GetJsonAsync("/api/smart-routing");

        Assert.False(s["enabled"]!.GetValue<bool>());
        Assert.Equal("astra-auto", s["virtualModel"]!.GetValue<string>());
        Assert.Equal(["light", "standard", "heavy"], s["grades"]!.AsArray().Select(g => g!["id"]!.GetValue<string>()));
        Assert.All(s["grades"]!.AsArray(), g => Assert.Equal("", g!["model"]!.GetValue<string>()));
        Assert.Equal("session", s["rules"]!["mode"]!.GetValue<string>()); // once per session unless told otherwise
        Assert.Equal("standard", s["rules"]!["fallbackGrade"]!.GetValue<string>());
        Assert.Equal("typesafe", s["judge"]!["protocol"]!.GetValue<string>());
        Assert.Null(s["judge"]!["baseUrl"]);
        Assert.Equal("https://api.typesafe.ai", s["judge"]!["effectiveBaseUrl"]!.GetValue<string>());
        Assert.False(s["judge"]!["hasApiKey"]!.GetValue<bool>());
        Assert.False(s["status"]!["breakerOpen"]!.GetValue<bool>());
        Assert.Equal(0, s["status"]!["pins"]!.GetValue<int>());
        Assert.Equal(0, s["status"]!["recent"]!["total"]!.GetValue<int>());
        Assert.Null(s["status"]!["recent"]!["medianJudgeMs"]);
        Assert.Empty(s["status"]!["unreachable"]!.AsArray());
        Assert.Contains("cheapest", s["defaultInstructions"]!.GetValue<string>());
        var protocols = s["protocols"]!.AsArray();
        Assert.Equal(["typesafe", "openai"], protocols.Select(p => p!["id"]!.GetValue<string>()));
        Assert.Equal("jev-latest", protocols[0]!["defaultModel"]!.GetValue<string>());
        Assert.Equal("https://api.openai.com", protocols[1]!["defaultBaseUrl"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- the key

    [Fact]
    public async Task The_Key_Is_Write_Only_Shown_As_A_Mask_And_Never_Stored_In_The_Clear()
    {
        await using var host = await TestHost.StartAsync();

        var put = await UpdateAsync(host, new { judge = new { apiKey = "sk-or-v1-abcdefghijklmnop" } });

        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.True(put.Body!["judge"]!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("sk-…mnop", put.Body["judge"]!["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain("abcdefghijklmnop", put.Body.ToJsonString());
        Assert.DoesNotContain("abcdefghijklmnop", await host.Client.GetStringAsync("/api/smart-routing"));
        var stored = await host.Db.Settings.GetAsync<SmartRoutingSettings>(SmartRoutingSettings.StorageKey);
        Assert.False(string.IsNullOrEmpty(stored!.Judge.ApiKeyProtected));
        Assert.DoesNotContain("abcdefghijklmnop", Json.Serialize(stored));

        // Left out, it stays; "" clears it.
        await UpdateAsync(host, new { rules = new { contextTurns = 1 } });
        Assert.True((await host.GetJsonAsync("/api/smart-routing"))["judge"]!["hasApiKey"]!.GetValue<bool>());
        var cleared = await UpdateAsync(host, new { judge = new { apiKey = "" } });
        Assert.False(cleared.Body!["judge"]!["hasApiKey"]!.GetValue<bool>());
        Assert.Null(cleared.Body["judge"]!["apiKeyMasked"]);
    }

    [Fact]
    public async Task A_Key_With_Spaces_Or_Odd_Characters_Is_Refused_And_The_Saved_One_Stays()
    {
        await using var host = await TestHost.StartAsync();
        await UpdateAsync(host, new { judge = new { apiKey = "sk-good-key-1234567" } });

        foreach (var bad in new[] { "sk bad key", "sk-密钥-1234", "sk-line\nbreak" })
        {
            var refused = await UpdateAsync(host, new { judge = new { apiKey = bad } });
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
            Assert.Equal(["judge_key_invalid"], ProblemCodes(refused.Body));
            Assert.Equal("judge.apiKey", refused.Body!["details"]!["problems"]![0]!["field"]!.GetValue<string>());
        }
        Assert.Equal("sk-…4567", (await host.GetJsonAsync("/api/smart-routing"))["judge"]!["apiKeyMasked"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changing_The_Address_Or_The_Dialect_Drops_The_Saved_Key_Unless_A_New_One_Comes_With_It()
    {
        await using var host = await TestHost.StartAsync();
        await UpdateAsync(host, new { judge = new { apiKey = "sk-typesafe-key-123456" } });

        // The same service under a different spelling keeps its key.
        var same = await UpdateAsync(host, new { judge = new { baseUrl = " https://api.typesafe.ai/ " } });
        Assert.True(same.Body!["judge"]!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("https://api.typesafe.ai", same.Body["judge"]!["baseUrl"]!.GetValue<string>());

        // Another host must not receive it.
        var moved = await UpdateAsync(host, new { judge = new { baseUrl = "https://openrouter.ai/api" } });
        Assert.False(moved.Body!["judge"]!["hasApiKey"]!.GetValue<bool>());

        // …unless the new service's own key is given in the same breath.
        var withKey = await UpdateAsync(host, new { judge = new { baseUrl = "https://openrouter.ai/api/v1", apiKey = "sk-or-v1-0000000000" } });
        Assert.True(withKey.Body!["judge"]!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("sk-…0000", withKey.Body["judge"]!["apiKeyMasked"]!.GetValue<string>());

        // Another dialect starts from its own address and model, and from no key.
        var other = await UpdateAsync(host, new { judge = new { protocol = "openai" } });
        Assert.Equal("openai", other.Body!["judge"]!["protocol"]!.GetValue<string>());
        Assert.Null(other.Body["judge"]!["baseUrl"]);
        Assert.Equal("https://api.openai.com", other.Body["judge"]!["effectiveBaseUrl"]!.GetValue<string>());
        Assert.Equal("", other.Body["judge"]!["model"]!.GetValue<string>());
        Assert.False(other.Body["judge"]!["hasApiKey"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- switching on

    [Fact]
    public async Task It_Cannot_Be_Switched_On_Half_Configured_And_Every_Reason_Comes_With_A_Code_And_A_Field()
    {
        await using var host = await TestHost.StartAsync();

        var refused = await UpdateAsync(host, new { enabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
        var codes = ProblemCodes(refused.Body);
        Assert.Contains("judge_key_missing", codes);
        Assert.Equal(3, codes.Count(c => c == "grade_model_missing"));
        var fields = refused.Body!["details"]!["problems"]!.AsArray().Select(p => p!["field"]!.GetValue<string>()).ToArray();
        Assert.Contains("grades[2].model", fields);
        Assert.Equal(refused.Body["details"]!["problems"]![0]!["message"]!.GetValue<string>(), refused.Body["error"]!.GetValue<string>());
        Assert.False((await host.GetJsonAsync("/api/smart-routing"))["enabled"]!.GetValue<bool>()); // nothing was saved
    }

    [Fact]
    public async Task A_Complete_Setup_Switches_On_And_A_Model_Nobody_Serves_Does_Not()
    {
        await using var host = await TestHost.StartAsync();
        await SeedModelsAsync(host, "small-model", "mid-model", "big-model");

        var on = await UpdateAsync(host, CompleteSetup());
        Assert.Equal(HttpStatusCode.OK, on.Status);
        Assert.True(on.Body!["enabled"]!.GetValue<bool>());
        Assert.Equal("big-model", on.Body["grades"]![2]!["model"]!.GetValue<string>());

        var ghost = await UpdateAsync(host, new { grades = new[]
        {
            new { id = "light", name = "Light", description = "Simple things.", model = "ghost-model" },
            new { id = "standard", name = "Standard", description = "Everyday things.", model = "mid-model" },
            new { id = "heavy", name = "Heavy", description = "Hard things.", model = "big-model" },
        } });
        Assert.Equal(HttpStatusCode.BadRequest, ghost.Status);
        Assert.Equal(["grade_model_unroutable"], ProblemCodes(ghost.Body));
        Assert.Equal("grades[0].model", ghost.Body!["details"]!["problems"]![0]!["field"]!.GetValue<string>());
        Assert.Equal("small-model", (await host.GetJsonAsync("/api/smart-routing"))["grades"]![0]!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Switching_It_Off_Needs_Nothing_And_Keeps_The_Setup()
    {
        await using var host = await TestHost.StartAsync();
        await SeedModelsAsync(host, "small-model", "mid-model", "big-model");
        await UpdateAsync(host, CompleteSetup());

        var off = await UpdateAsync(host, new { enabled = false });

        Assert.False(off.Body!["enabled"]!.GetValue<bool>());
        Assert.Equal("big-model", off.Body["grades"]![2]!["model"]!.GetValue<string>());
        Assert.True(off.Body["judge"]!["hasApiKey"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public async Task Shape_Errors_Are_Refused_Whether_Or_Not_It_Is_On()
    {
        await using var host = await TestHost.StartAsync();

        var mode = await UpdateAsync(host, new { rules = new { mode = "sometimes" } });
        Assert.Equal(HttpStatusCode.BadRequest, mode.Status);
        Assert.Equal(["mode_invalid"], ProblemCodes(mode.Body));

        var range = await UpdateAsync(host, new { rules = new { minConfidence = 1.5, maxChars = 10 }, judge = new { timeoutMs = 5 } });
        Assert.Equal(3, ProblemCodes(range.Body).Count(c => c == "out_of_range"));

        var id = await UpdateAsync(host, new { virtualModel = "has space" });
        Assert.Equal(["virtual_model_invalid"], ProblemCodes(id.Body));

        var url = await UpdateAsync(host, new { judge = new { baseUrl = "ftp://x.test" } });
        Assert.Equal(["judge_url_invalid"], ProblemCodes(url.Body));

        var tooMany = await UpdateAsync(host, new { grades = Enumerable.Range(0, 7).Select(i => new { id = "g" + i, name = "G", description = "d", model = "" }).ToArray() });
        Assert.Contains("grades_count", ProblemCodes(tooMany.Body));
    }

    [Fact]
    public async Task A_Virtual_Model_That_Is_A_Real_Model_Is_A_Conflict()
    {
        await using var host = await TestHost.StartAsync();
        await SeedModelsAsync(host, "mid-model");

        var clash = await UpdateAsync(host, new { virtualModel = "mid-model" });

        Assert.Equal(HttpStatusCode.Conflict, clash.Status);
        Assert.Equal(["virtual_model_conflict"], ProblemCodes(clash.Body));
        Assert.Equal("astra-auto", (await host.GetJsonAsync("/api/smart-routing"))["virtualModel"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await UpdateAsync(host, new { virtualModel = "smart-auto" })).Status);
    }

    [Fact]
    public async Task The_Grades_Can_Be_Replaced_As_A_List()
    {
        await using var host = await TestHost.StartAsync();

        var two = await UpdateAsync(host, new
        {
            grades = new[]
            {
                new { id = "cheap", name = "Cheap", description = "Easy.", model = "" },
                new { id = "strong", name = "Strong", description = "Hard.", model = "" },
            },
            rules = new { fallbackGrade = "strong" },
        });

        Assert.Equal(HttpStatusCode.OK, two.Status);
        Assert.Equal(["cheap", "strong"], two.Body!["grades"]!.AsArray().Select(g => g!["id"]!.GetValue<string>()));
        Assert.Equal("strong", two.Body["rules"]!["fallbackGrade"]!.GetValue<string>());
    }

    // ---------------------------------------------------------------- trying the judge

    private static string JudgeAnswer(string choice = "heavy") =>
        """{"model":"jev-1.13.0","answers":{"grade":{"type":"choice","choice":"CHOICE","confidence":0.8,"probabilities":{"light":0.05,"standard":0.1,"heavy":0.85}}},"usage":{"input_tokens":120,"output_tokens":9}}"""
            .Replace("CHOICE", choice);

    private static FakeUpstream UseJudge(TestHost host, Func<FakeUpstream.Seen, HttpResponseMessage> respond)
    {
        var judge = new FakeUpstream(respond);
        host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => judge;
        return judge;
    }

    [Fact]
    public async Task The_Test_Asks_The_Judge_With_The_Settings_Being_Edited_And_Saves_Nothing()
    {
        await using var host = await TestHost.StartAsync();
        await SeedModelsAsync(host, "small-model", "mid-model", "big-model");
        await UpdateAsync(host, new { grades = new[]
        {
            new { id = "light", name = "Light", description = "Simple things.", model = "small-model" },
            new { id = "standard", name = "Standard", description = "Everyday things.", model = "mid-model" },
            new { id = "heavy", name = "Heavy", description = "Hard things.", model = "big-model" },
        } });
        var judge = UseJudge(host, _ => FakeUpstream.Json(JudgeAnswer()));

        var result = await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new
        {
            text = "Redesign the billing module",
            judge = new { baseUrl = "https://judge.test", apiKey = "sk-draft-123456", model = "jev-1.13.0" },
            grades = new[]
            {
                new { id = "light", name = "Light", description = "DRAFT light.", model = "small-model" },
                new { id = "standard", name = "Standard", description = "DRAFT standard.", model = "mid-model" },
                new { id = "heavy", name = "Heavy", description = "DRAFT heavy.", model = "big-model" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, result.Status);
        var body = result.Body!;
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal(("heavy", "Heavy", "big-model"), (body["grade"]!.GetValue<string>(), body["gradeName"]!.GetValue<string>(), body["model"]!.GetValue<string>()));
        Assert.Equal(0.8, body["confidence"]!.GetValue<double>());
        Assert.True(body["confident"]!.GetValue<bool>());
        Assert.Equal(0.85, body["probabilities"]!["heavy"]!.GetValue<double>());
        Assert.Equal("jev-1.13.0", body["judgeModel"]!.GetValue<string>());
        Assert.Equal((120, 9), (body["inputTokens"]!.GetValue<int>(), body["outputTokens"]!.GetValue<int>()));
        // What a request would have waited: the timeout being edited (the default here), with no connection to open.
        Assert.Equal(2000, body["limitMs"]!.GetValue<long>());
        Assert.False(body["newConnection"]!.GetValue<bool>());

        var seen = Assert.Single(judge.Requests);
        Assert.Equal("https://judge.test/v1/systemone", seen.Url.ToString());
        Assert.Equal("Bearer sk-draft-123456", seen.Headers["authorization"]);
        Assert.Equal("Redesign the billing module", seen.Json["state"]!["request"]!.GetValue<string>());
        Assert.Equal("DRAFT heavy.", seen.Json["questions"]!["grade"]!["criteria"]!["heavy"]!.GetValue<string>()); // the draft's words

        // Nothing of the draft was saved, and no request was logged.
        var saved = await host.GetJsonAsync("/api/smart-routing");
        Assert.Null(saved["judge"]!["baseUrl"]);
        Assert.False(saved["judge"]!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("Simple things.", saved["grades"]![0]!["description"]!.GetValue<string>());
        Assert.Equal(0, (await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery())).Total);
    }

    [Fact]
    public async Task The_Test_Uses_The_Saved_Settings_For_What_The_Draft_Leaves_Out_And_Says_When_The_Answer_Is_Not_Confident_Enough()
    {
        await using var host = await TestHost.StartAsync();
        await UpdateAsync(host, new { judge = new { baseUrl = "https://judge.test", apiKey = "sk-saved-123456" }, rules = new { minConfidence = 0.95 } });
        var judge = UseJudge(host, _ => FakeUpstream.Json(JudgeAnswer("standard")));

        var result = await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new { text = "hmm" });

        Assert.Equal("Bearer sk-saved-123456", Assert.Single(judge.Requests).Headers["authorization"]);
        Assert.True(result.Body!["ok"]!.GetValue<bool>());
        Assert.Equal("standard", result.Body["grade"]!.GetValue<string>());
        Assert.False(result.Body["confident"]!.GetValue<bool>()); // 0.8 < 0.95: a real request would use the fallback grade
        Assert.Null(result.Body["model"]); // this grade has no model yet
    }

    [Fact]
    public async Task A_Judge_That_Fails_Is_A_Result_Not_An_Error()
    {
        await using var host = await TestHost.StartAsync();
        await UpdateAsync(host, new { judge = new { baseUrl = "https://judge.test", apiKey = "sk-saved-123456" } });
        UseJudge(host, _ => FakeUpstream.Json("""{"error":{"message":"overloaded, try later"}}""", HttpStatusCode.ServiceUnavailable));

        var result = await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new { text = "hello" });

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.False(result.Body!["ok"]!.GetValue<bool>());
        Assert.Equal("overloaded", result.Body["failure"]!.GetValue<string>());
        Assert.Contains("overloaded, try later", result.Body["message"]!.GetValue<string>());
        Assert.Null(result.Body["grade"]);
    }

    [Fact]
    public async Task The_Test_Needs_A_Key_A_Text_And_Valid_Settings()
    {
        await using var host = await TestHost.StartAsync();
        UseJudge(host, _ => FakeUpstream.Json(JudgeAnswer()));

        var noKey = await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new { text = "hello" });
        Assert.Equal(HttpStatusCode.BadRequest, noKey.Status);
        Assert.Equal(["judge_key_missing"], ProblemCodes(noKey.Body));

        await UpdateAsync(host, new { judge = new { apiKey = "sk-saved-123456" } });
        var noText = await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new { text = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, noText.Status);

        var badDraft = await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new { text = "hello", judge = new { timeoutMs = 1 } });
        Assert.Equal(HttpStatusCode.BadRequest, badDraft.Status);
        Assert.Equal(["out_of_range"], ProblemCodes(badDraft.Body));
    }

    // ---------------------------------------------------------------- the models a grade can be given

    [Fact]
    public async Task The_Models_Endpoint_Lists_What_A_Grade_Can_Be_Given()
    {
        await using var host = await TestHost.StartAsync();
        var provider = await SeedModelsAsync(host, "small-model", "big-model");
        await host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = provider.Id, ModelId = "switched-off", Enabled = false });

        var models = (await host.GetJsonAsync("/api/smart-routing/models")).AsArray();

        Assert.Equal(["big-model", "small-model"], models.Select(m => m!["id"]!.GetValue<string>()).Order());
        var small = models.Single(m => m!["id"]!.GetValue<string>() == "small-model")!;
        Assert.Equal((provider.Id, "Models provider"), (small["providerId"]!.GetValue<string>(), small["providerName"]!.GetValue<string>()));

        // The same model on a second provider is a second choice: a grade names the provider its requests go to.
        var second = await SeedModelsAsync(host, "small-model");
        var both = (await host.GetJsonAsync("/api/smart-routing/models")).AsArray().Where(m => m!["id"]!.GetValue<string>() == "small-model");
        Assert.Equal(new[] { provider.Id, second.Id }.Order(), both.Select(m => m!["providerId"]!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task A_Grade_Keeps_The_Provider_It_Was_Given_And_Gets_One_When_It_Was_Given_None()
    {
        await using var host = await TestHost.StartAsync();
        var first = await SeedModelsAsync(host, "small-model", "mid-model", "big-model");
        var second = await SeedModelsAsync(host, "small-model");

        var put = await UpdateAsync(host, new
        {
            grades = new object[]
            {
                new { id = "light", name = "Light", description = "Simple things.", model = "small-model", providerId = second.Id },
                new { id = "standard", name = "Standard", description = "Everyday things.", model = "mid-model" }, // none given
                new { id = "heavy", name = "Heavy", description = "Hard things.", model = "big-model" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, put.Status);
        var grades = (await host.GetJsonAsync("/api/smart-routing"))["grades"]!.AsArray();
        Assert.Equal([second.Id, first.Id, first.Id], grades.Select(g => g!["providerId"]!.GetValue<string>()));
    }

    // ---------------------------------------------------------------- the console's other lists

    [Fact]
    public async Task The_Clients_Model_List_Offers_The_Virtual_Model_Only_While_It_Is_On()
    {
        await using var host = await TestHost.StartAsync();
        await SeedModelsAsync(host, "small-model", "mid-model", "big-model");
        Assert.DoesNotContain("astra-auto", (await host.GetJsonAsync("/api/clients/codex/models")).AsArray().Select(m => m!.GetValue<string>()));

        await UpdateAsync(host, CompleteSetup());
        Assert.Contains("astra-auto", (await host.GetJsonAsync("/api/clients/codex/models")).AsArray().Select(m => m!.GetValue<string>()));

        await UpdateAsync(host, new { enabled = false });
        Assert.DoesNotContain("astra-auto", (await host.GetJsonAsync("/api/clients/codex/models")).AsArray().Select(m => m!.GetValue<string>()));
    }

    [Fact]
    public async Task Changes_Reach_The_Gateway_At_Once()
    {
        await using var host = await TestHost.StartAsync();
        await SeedModelsAsync(host, "small-model", "mid-model", "big-model");
        await UpdateAsync(host, CompleteSetup());
        var router = host.App.Services.GetRequiredService<Astra.Gateway.Routing.SmartRouter>();
        Assert.Equal("astra-auto", router.AdvertisedModel);

        await UpdateAsync(host, new { virtualModel = "smart-auto" });
        Assert.Equal("smart-auto", router.AdvertisedModel);
        Assert.True(router.IsVirtual("smart-auto"));
        Assert.False(router.IsVirtual("astra-auto"));
    }
}

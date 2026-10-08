using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Providers.Quota;

namespace Astra.Providers.Tests;

public class ProviderQuotaServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return respond(request);
        }
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static readonly QuotaTemplateCatalog Catalog = new();

    private static (ProviderQuotaService Service, Handler Handler) Service(string body, HttpStatusCode status = HttpStatusCode.OK, string contentType = "application/json")
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        }));
        return (new ProviderQuotaService(new Factory(handler), Catalog, new FakeClock()), handler);
    }

    private static Provider Provider(string? templateId, string baseUrl, string scheme = AuthSchemes.Bearer) => new()
    {
        Id = "p1",
        Name = "P",
        TemplateId = templateId,
        AuthScheme = scheme,
        Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = baseUrl }],
        PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
    };

    private static QuotaQuery Query(Provider p, QuotaConfig? config = null, string? key = "sk-secret-key-1234",
        Dictionary<string, string>? secrets = null) => new(p, config ?? new QuotaConfig { Enabled = true }, key, secrets ?? []);

    [Fact]
    public async Task DeepSeek_Is_Suggested_By_Template_And_Normalized_Per_Currency()
    {
        var (service, handler) = Service("""
            {"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}
            """);
        var result = await service.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1")));

        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.deepseek.com/user/balance", request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret-key-1234", request.Headers.Authorization!.ToString());
        Assert.Equal("deepseek", result.Template);
        var snapshot = result.Snapshot!;
        Assert.Equal(Now.ToString("o"), snapshot["fetchedAtUtc"]!.GetValue<string>());
        Assert.Equal("balance", snapshot["kind"]!.GetValue<string>());
        Assert.True(snapshot["isValid"]!.GetValue<bool>());
        var plan = snapshot["plans"]!.AsArray().Single()!;
        Assert.Equal("CNY", plan["unit"]!.GetValue<string>());
        Assert.Equal(110, plan["remaining"]!.GetValue<double>());
    }

    [Fact]
    public async Task Unavailable_Account_Is_A_Successful_Query_With_IsValid_False()
    {
        var (service, _) = Service("""{"is_available":false,"balance_infos":[{"currency":"CNY","total_balance":"0.00"}]}""");
        var result = await service.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1")));
        Assert.True(result.Ok);
        Assert.False(result.Snapshot!["isValid"]!.GetValue<bool>());
        Assert.Equal(0, result.Snapshot["plans"]![0]!["remaining"]!.GetValue<double>());
    }

    [Fact]
    public async Task New_Api_Uses_Its_Access_Token_And_User_Id_Not_The_Provider_Key()
    {
        var (service, handler) = Service("""{"success":true,"data":{"group":"default","quota":5000000,"used_quota":2500000}}""");
        var provider = Provider("custom-chat", "https://relay.example.com/v1");
        var config = new QuotaConfig { Enabled = true, Params = new() { ["userId"] = "42" } };

        var missing = await service.QueryAsync(Query(provider, config));
        Assert.False(missing.Ok);
        Assert.Equal(QuotaErrors.Config, missing.ErrorCode);
        Assert.Contains("accessToken", missing.Error);
        Assert.Empty(handler.Requests);

        var result = await service.QueryAsync(Query(provider, config, secrets: new() { ["accessToken"] = "sys-token-9999" }));
        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://relay.example.com/api/user/self", request.RequestUri!.ToString());
        Assert.Equal("Bearer sys-token-9999", request.Headers.Authorization!.ToString());
        Assert.Equal("42", request.Headers.GetValues("New-Api-User").Single());
        var plan = result.Snapshot!["plans"]![0]!;
        Assert.Equal(10, plan["remaining"]!.GetValue<double>());
        Assert.Equal(5, plan["used"]!.GetValue<double>());
        Assert.Equal(15, plan["total"]!.GetValue<double>());
        Assert.Equal("default", result.Snapshot["planLabel"]!.GetValue<string>());
    }

    [Fact]
    public async Task Provider_Auth_Scheme_Is_Applied_Like_The_Gateway()
    {
        var (service, handler) = Service("""{"balance":"3"}""");
        var custom = new QuotaConfig
        {
            Enabled = true, Template = QuotaTemplate.CustomId,
            Request = new QuotaRequestSpec { Url = "{{baseUrl}}/balance?u={{user}}" },
            Extract = JsonNode.Parse("""{"remaining":"balance"}""")!.AsObject(),
            Params = new() { ["user"] = "a b" },
        };
        await service.QueryAsync(Query(Provider(null, "https://x.example/v1", AuthSchemes.XApiKey), custom));
        await service.QueryAsync(Query(Provider(null, "https://x.example/v1", AuthSchemes.QueryKey), custom));

        Assert.Equal("sk-secret-key-1234", handler.Requests[0].Headers.GetValues("x-api-key").Single());
        Assert.Equal("https://x.example/v1/balance?u=a%20b", handler.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("https://x.example/v1/balance?u=a%20b&key=sk-secret-key-1234", handler.Requests[1].RequestUri!.AbsoluteUri);
        Assert.Null(handler.Requests[1].Headers.Authorization);
    }

    [Fact]
    public async Task Zhipu_Coding_Plan_Sends_The_Bare_Key_And_Maps_Windows()
    {
        var (service, handler) = Service("""
            {"code":200,"success":true,"data":{"level":"pro","limits":[
              {"type":"TOKENS_LIMIT","unit":3,"number":5,"percentage":12,"nextResetTime":1791417600000},
              {"type":"TOKENS_LIMIT","unit":6,"number":1,"percentage":40,"nextResetTime":1791936000000}]}}
            """);
        var result = await service.QueryAsync(Query(Provider("zhipuai", "https://open.bigmodel.cn/api/paas/v4")));
        Assert.True(result.Ok, result.Error);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://open.bigmodel.cn/api/monitor/usage/quota/limit", request.RequestUri!.ToString());
        Assert.Equal("sk-secret-key-1234", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("plan", result.Snapshot!["kind"]!.GetValue<string>());
        Assert.Equal("pro", result.Snapshot["planLabel"]!.GetValue<string>());
        var plans = result.Snapshot["plans"]!.AsArray();
        Assert.Equal(2, plans.Count);
        Assert.Equal(300, plans[0]!["windowMinutes"]!.GetValue<int>());
        Assert.Equal(12, plans[0]!["usedPercent"]!.GetValue<double>());
        Assert.Equal("2026-10-08T00:00:00.0000000+00:00", plans[0]!["resetsAtUtc"]!.GetValue<string>());
        Assert.Equal(10080, plans[1]!["windowMinutes"]!.GetValue<int>());
    }

    [Fact]
    public async Task SiliconFlow_Unit_Follows_The_Host()
    {
        var (service, handler) = Service("""{"code":20000,"data":{"totalBalance":"8.8"}}""");
        var result = await service.QueryAsync(Query(Provider("siliconflow", "https://api.siliconflow.cn/v1")));
        Assert.Equal("https://api.siliconflow.cn/v1/user/info", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("CNY", result.Snapshot!["plans"]![0]!["unit"]!.GetValue<string>());
        result = await service.QueryAsync(Query(Provider("siliconflow", "https://api.siliconflow.com/v1")));
        Assert.Equal("USD", result.Snapshot!["plans"]![0]!["unit"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(401, QuotaErrors.Unauthorized)]
    [InlineData(403, QuotaErrors.Unauthorized)]
    [InlineData(404, QuotaErrors.NoEndpoint)]
    [InlineData(429, QuotaErrors.RateLimited)]
    [InlineData(502, QuotaErrors.Upstream)]
    public async Task Http_Failures_Are_Classified_And_Never_Echo_The_Key(int status, string code)
    {
        var (service, _) = Service("""{"error":{"message":"bad key sk-secret-key-1234"}}""", (HttpStatusCode)status);
        var result = await service.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1")));
        Assert.False(result.Ok);
        Assert.Equal(code, result.ErrorCode);
        Assert.Equal(status, result.HttpStatus);
        Assert.Contains($"HTTP {status}", result.Error);
        Assert.DoesNotContain("sk-secret-key-1234", result.Error);
        Assert.DoesNotContain("sk-secret-key-1234", result.Raw!.ToJsonString());
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task Query_Key_Is_Redacted_From_The_Reported_Url()
    {
        var (service, _) = Service("""{"nothing":true}""");
        var custom = new QuotaConfig
        {
            Enabled = true, Template = QuotaTemplate.CustomId,
            Request = new QuotaRequestSpec { Url = "{{origin}}/v1/balance" },
            Extract = JsonNode.Parse("""{"remaining":"balance"}""")!.AsObject(),
        };
        var result = await service.QueryAsync(Query(Provider(null, "https://g.example/v1", AuthSchemes.QueryKey), custom));
        Assert.Equal(QuotaErrors.Empty, result.ErrorCode);
        Assert.DoesNotContain("sk-secret-key-1234", result.Url);
        Assert.Contains("key=", result.Url);
    }

    [Fact]
    public async Task Non_Json_Empty_And_Missing_Key_Are_Distinct_Failures()
    {
        var (html, _) = Service("<html>login</html>", contentType: "text/html");
        Assert.Equal(QuotaErrors.NotJson, (await html.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1")))).ErrorCode);

        var (empty, _) = Service("""{"is_available":true,"balance_infos":[]}""");
        Assert.Equal(QuotaErrors.Empty, (await empty.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1")))).ErrorCode);

        var (noKey, handler) = Service("{}");
        var result = await noKey.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1"), key: null));
        Assert.Equal(QuotaErrors.NoKey, result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task No_Template_And_Subscription_Providers_Are_Configuration_Errors()
    {
        var (service, handler) = Service("{}");
        var unknown = await service.QueryAsync(Query(Provider(null, "https://unknown.example/v1")));
        Assert.Equal(QuotaErrors.Config, unknown.ErrorCode);

        var subscription = Provider("claude-subscription", "https://api.anthropic.com/v1", AuthSchemes.OAuthSubscription);
        Assert.Equal(QuotaErrors.Config, (await service.QueryAsync(Query(subscription))).ErrorCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Slow_Upstream_Times_Out_With_The_Configured_Deadline()
    {
        var service = new ProviderQuotaService(new CancellingFactory(), Catalog, new FakeClock());
        var result = await service.QueryAsync(Query(Provider("deepseek", "https://api.deepseek.com/v1"), new QuotaConfig { Enabled = true, TimeoutSec = 1 }));
        Assert.Equal(QuotaErrors.Timeout, result.ErrorCode);
        Assert.Contains("1", result.Error);
    }

    private sealed class CancellingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CancellingHandler());
    }

    /// <summary>Waits until the request's own token is cancelled (the query deadline), then throws like HttpClient does.</summary>
    private sealed class CancellingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public void Suggestion_Prefers_A_Known_Host_Over_The_New_Api_Fallback()
    {
        Assert.Equal("newapi", Catalog.Suggest(Provider("custom-chat", "https://relay.example.com/v1"))?.Id);
        Assert.Equal("deepseek", Catalog.Suggest(Provider("custom-chat", "https://api.deepseek.com/v1"))?.Id);
        Assert.Equal("stepfun", Catalog.Suggest(Provider(null, "https://api.stepfun.com/v1"))?.Id);
        Assert.Equal("minimax-coding", Catalog.Suggest(Provider("minimax", "https://api.minimaxi.com/anthropic"))?.Id);
        Assert.Null(Catalog.Suggest(Provider("ollama", "http://127.0.0.1:11434/v1")));
    }

    [Fact]
    public void Config_Round_Trips_Through_Settings_And_Validates_Custom_Queries()
    {
        var config = new QuotaConfig
        {
            Enabled = true, Template = QuotaTemplate.CustomId, IntervalMinutes = 0, TimeoutSec = 20, BaseUrl = "https://b.example",
            Params = new() { ["userId"] = "7" }, Secrets = new() { ["accessToken"] = "enc:xyz" },
            Request = new QuotaRequestSpec { Url = "{{origin}}/api/user/self", Auth = QuotaAuth.None, Headers = new() { ["New-Api-User"] = "{{userId}}" } },
            Extract = JsonNode.Parse("""{"remaining":"data.quota"}""")!.AsObject(),
        };
        var back = QuotaConfig.From(new JsonObject { ["quota"] = config.ToNode() });
        Assert.True(back.Enabled);
        Assert.Equal(0, back.IntervalMinutes);
        Assert.Equal(20, back.TimeoutSec);
        Assert.Equal("enc:xyz", back.Secrets["accessToken"]);
        Assert.Equal("{{userId}}", back.Request!.Headers["New-Api-User"]);
        Assert.Empty(back.Validate(Catalog));

        Assert.False(QuotaConfig.From(new JsonObject()).Enabled); // off by default
        Assert.NotEmpty((config with { Request = null }).Validate(Catalog));
        Assert.NotEmpty((config with { Template = "nope" }).Validate(Catalog));
        Assert.NotEmpty((config with { Request = new QuotaRequestSpec { Url = "ftp://x" } }).Validate(Catalog));
        Assert.NotEmpty((config with { Request = new QuotaRequestSpec { Url = "{{origin}}/x", Headers = new() { ["Host"] = "x" } } }).Validate(Catalog));
    }
}

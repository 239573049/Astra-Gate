using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Providers;
using Astra.Desktop.Tests.Api;

namespace Astra.Desktop.Tests.Providers;

public sealed class ProviderQueriesTests
{
    private const string Provider = """
        {"id":"p1","templateId":null,"templateVersion":null,"templateUpdateAvailable":false,"name":"X","icon":null,"category":"custom","endpoints":[],
         "preferredUpstreamProtocols":[],"authScheme":"bearer","hasApiKey":false,"apiKeyMasked":null,"extraHeaders":{},"httpProxy":null,"priceMultiplier":1,
         "priceKey":null,"adapterId":null,"settings":{},"enabled":false,"sortOrder":0,"notes":null,"website":null,"modelCount":0,"boundClients":[],
         "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-01T00:00:00Z",
         "quotaConfig":{"enabled":false,"template":null,"suggestedTemplate":null,"effectiveTemplate":null,"intervalMinutes":null,"effectiveIntervalMinutes":30,
         "timeoutSec":15,"baseUrl":null,"params":{},"secrets":[],"request":null,"extract":null},"quota":null,"quotaCheckedAtUtc":null}
        """;

    private static ProviderQueries Make(ApiClient api, out QueryClient queries)
    {
        queries = new QueryClient();
        return new ProviderQueries(queries, api, () => DateTimeOffset.Parse("2026-10-26T12:00:00Z"));
    }

    [Fact]
    public async Task Update_patches_the_provider_with_the_admin_header_and_invalidates_the_lists()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, Provider));
        using var api = new ApiClient(server.Url);
        var q = Make(api, out var queries);
        var list = queries.Query(QueryKeys.Providers, _ => Task.FromResult(new JsonArray()));
        await list.RefetchAsync();
        Assert.False(list.IsStale);

        var written = await q.UpdateAsync("p 1", new JsonObject { ["enabled"] = true });

        Assert.Equal("p1", written!.Id);
        var seen = Assert.Single(server.Requests);
        Assert.Equal("PATCH", seen.Method);
        Assert.Equal("/api/providers/p%201", seen.Url);
        Assert.Equal("1", seen.Admin);
        Assert.Equal("""{"enabled":true}""", seen.Body);
        Assert.True(list.IsStale);
    }

    [Fact]
    public async Task Create_posts_the_wizard_body()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, Provider));
        using var api = new ApiClient(server.Url);
        var q = Make(api, out _);
        var w = new AddProviderWizard();
        w.PickCustom();
        w.Name = "Mine";
        w.SetEndpoints([new TemplateEndpoint("openai-chat", "https://x/v1")]);
        var created = await q.CreateAsync(w.BuildRequest());
        Assert.Equal("p1", created!.Id);
        var seen = Assert.Single(server.Requests);
        Assert.Equal("POST", seen.Method);
        var body = JsonNode.Parse(seen.Body!)!;
        Assert.Equal("Mine", (string?)body["name"]);
        Assert.Equal("https://x/v1", (string?)body["endpoints"]![0]!["baseUrl"]);
    }

    [Fact]
    public async Task Import_sends_source_and_ref_pairs_only()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, """{"added":[{"id":"a","name":"A"}],"skipped":[]}"""));
        using var api = new ApiClient(server.Url);
        var q = Make(api, out _);
        var candidate = new ImportCandidateDto("r1", "cc", "N", null, [], "bearer", true, "sk-…1", null, [], [], null, "new", null, false, null, []);
        var result = await q.ImportAsync([candidate]);
        Assert.Single(result!.Added);
        Assert.Equal("""[{"source":"cc","ref":"r1"}]""", Assert.Single(server.Requests).Body);
    }

    [Fact]
    public async Task A_failed_update_surfaces_the_servers_message()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 409, """{"error":"conflict"}"""));
        using var api = new ApiClient(server.Url);
        var q = Make(api, out _);
        var e = await Assert.ThrowsAsync<ApiException>(() => q.UpdateAsync("p1", new JsonObject()));
        Assert.Equal("conflict", e.Message);
    }

    [Fact]
    public async Task Series_asks_for_seven_days_by_provider_in_local_days()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, "[]"));
        using var api = new ApiClient(server.Url);
        var q = Make(api, out _);
        await q.Series.RefetchAsync();
        var path = Assert.Single(server.Requests).Url;
        Assert.StartsWith("/api/stats/timeseries?", path);
        Assert.Contains("range=7d", path);
        Assert.Contains("groupBy=day", path);
        Assert.Contains("by=provider", path);
        Assert.Contains("tzOffset=", path);
    }

    [Fact]
    public async Task Templates_parse_from_the_endpoint()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, """[{"id":"openai","name":"OpenAI","category":"official","endpoints":[],"authScheme":"bearer","requiresApiKey":true,"models":["a"]}]"""));
        using var api = new ApiClient(server.Url);
        var q = Make(api, out _);
        await q.Templates.RefetchAsync();
        Assert.Equal("openai", Assert.Single(q.Templates.Data!).Id);
    }
}

using System.Net;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

// Fallback tracking is process-global; unrelated parallel tests must not contaminate this guard.
[CollectionDefinition("JSON registration guard", DisableParallelization = true)]
public sealed class JsonRegistrationCollection;

/// <summary>
/// Guards the source-generated JSON metadata: every type that crosses a JSON boundary must be
/// registered in an assembly's <c>JsonSerializerContext</c> (see <see cref="JsonContexts"/>).
///
/// An unregistered type still works on the JIT through the registry's reflection fallback — which is
/// exactly why the mistake is easy to make and invisible until a Native AOT build throws at runtime.
/// This test exercises the admin API and then asserts the fallback was never used.
/// </summary>
[Collection("JSON registration guard")]
public sealed class JsonRegistrationTests
{
    [Fact]
    public async Task AdminApi_payloads_are_all_served_from_generated_metadata()
    {
        // The provider probe's upstream is stubbed so the upstream model list has both a success and a failure shape.
        await using var host = await ClaudeDirectTests.LocalHost(b => b.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new GuardUpstream()));

        // Exercise every response shape we can reach without a provider or an upstream.
        string[] gets =
        [
            "/api/health", "/api/version", "/api/auth/status", "/api/settings", "/api/privacy",
            "/api/privacy/events", "/api/privacy/events/stats", "/api/providers", "/api/clients", "/api/tokens",
            "/api/update/status", "/api/models", "/api/requests", "/api/stats/summary", "/api/stats/timeseries",
            "/api/stats/top-models", "/api/stats/activity-heatmap", "/api/provider-quota/templates",
            "/api/providers/import/sources", "/api/clients/claude-code/direct", "/api/tunnel", "/api/server/autostart",
            "/api/smart-routing", "/api/smart-routing/models",
        ];

        // Reset after startup (seeding/migration run there) so we only observe request-time payloads.
        JsonContexts.ResetFallbackTracking();

        foreach (var url in gets) await host.GetJsonAsync(url);

        // A model round trip: create, patch, read detail, reset a field, delete.
        var created = await host.SendAsync(HttpMethod.Post, "/api/models",
            new { id = "guard-probe-model", displayName = "Guard", vendor = "anthropic" });
        Assert.Equal(HttpStatusCode.OK, created.Status);
        await host.SendAsync(HttpMethod.Patch, "/api/models/guard-probe-model", new { displayName = "Guard 2" });
        await host.GetJsonAsync("/api/models/guard-probe-model");
        await host.SendAsync(HttpMethod.Post, "/api/models/guard-probe-model/reset", new { field = "displayName" });
        await host.SendAsync(HttpMethod.Delete, "/api/models/guard-probe-model");

        // Providers, tokens, client binding, and the validation/error envelopes.
        await host.SendAsync(HttpMethod.Post, "/api/providers", new { name = "Guard", category = "custom" });
        // Provider balance / quota payloads (no upstream: the query fails, which is a payload shape too).
        var quotaProvider = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Quota guard", endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://quota.invalid/v1" } },
            apiKey = "sk-guard-0000", models = Array.Empty<string>(),
        });
        var quotaId = quotaProvider.Body!["id"]!.GetValue<string>();
        await host.GetJsonAsync($"/api/providers/{quotaId}/usage");
        await host.SendAsync(HttpMethod.Put, $"/api/providers/{quotaId}/quota/config", new
        {
            enabled = true, template = "custom", request = new { url = "{{origin}}/balance" }, extract = new { remaining = "balance" },
        });
        await host.GetJsonAsync($"/api/providers/{quotaId}/quota");
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{quotaId}/quota/test", new { timeoutSec = 1 });
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{quotaId}/quota");
        // Provider import: preview (above), commit, and the conflict envelope.
        await host.SendAsync(HttpMethod.Post, "/api/providers/import", new[] { new { source = "magpie", @ref = "missing" } });
        await host.SendAsync(HttpMethod.Put, "/api/clients/codex/binding", new { providerId = "missing" });
        await host.SendAsync(HttpMethod.Put, "/api/clients/codex/bindings", new { bindings = new[] { new { providerId = quotaId } } });
        await host.GetJsonAsync("/api/clients/codex/models");
        // Fetching models from upstream: the list, and an upstream refusal with its error details.
        foreach (var (upstream, expected) in new[] { ("models.guard.invalid", HttpStatusCode.OK), ("denied.guard.invalid", HttpStatusCode.BadGateway) })
        {
            var probed = await host.SendAsync(HttpMethod.Post, "/api/providers", new
            {
                name = "Probe " + upstream, endpoints = new[] { new { protocol = "openai-chat", baseUrl = $"https://{upstream}/v1" } },
                apiKey = "sk-guard-0000", models = Array.Empty<string>(),
            });
            var remote = await host.SendAsync(HttpMethod.Get, $"/api/providers/{probed.Body!["id"]!.GetValue<string>()}/remote-models");
            Assert.Equal(expected, remote.Status);
        }
        // Deleting a provider a client is bound to: the conflict and its details.
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Delete, $"/api/providers/{quotaId}")).Status);
        var guardToken = (await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Guard" })).Body!["id"]!.GetValue<string>();
        // A provider pool (issue #2): its input DTO and the members' health in the token DTO.
        foreach (var id in new[] { "guard-pool-a", "guard-pool-b" })
            await host.Db.Providers.InsertAsync(new Astra.Core.Models.Provider
            {
                Id = id, Name = id, Endpoints = [new Astra.Core.Models.ProviderEndpoint { BaseUrl = "https://pool.test/v1" }],
            });
        await host.SendAsync(HttpMethod.Patch, $"/api/tokens/{guardToken}", new
        {
            pool = new { strategy = "weighted", members = new[] { new { providerId = "guard-pool-a", weight = 2 }, new { providerId = "guard-pool-b", weight = 1 } } },
        });
        await host.GetJsonAsync($"/api/tokens/{guardToken}");
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { locale = "en" });
        await host.SendAsync(HttpMethod.Post, "/api/privacy/dry-run", new { text = "hello" });
        await host.SendAsync(HttpMethod.Post, "/api/auth/login", new { password = "wrong" });
        await host.SendAsync(HttpMethod.Put, "/api/tunnel", new { mode = "named", hostname = "astra.example.com" });
        await host.SendAsync(HttpMethod.Post, "/api/tunnel/start");
        await host.SendAsync(HttpMethod.Put, "/api/server/autostart", new { enabled = false });
        // Smart routing: a saved update, a refusal with its problems, and trying the judge (an answer, then a failure).
        host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => new GuardUpstream();
        await host.SendAsync(HttpMethod.Put, "/api/smart-routing",
            new { judge = new { baseUrl = "https://judge.guard.invalid", apiKey = "sk-guard-0000" }, rules = new { mode = "turn" } });
        await host.SendAsync(HttpMethod.Put, "/api/smart-routing", new { enabled = true });
        await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test", new { text = "redesign the billing module" });
        await host.SendAsync(HttpMethod.Post, "/api/smart-routing/test",
            new { text = "hello", judge = new { baseUrl = "https://down.guard.invalid", apiKey = "sk-guard-0000" } });
        await host.SendAsync(HttpMethod.Delete, "/api/provider-accounts/missing");
        await host.SendAsync(HttpMethod.Get, "/api/models/missing");
        await host.SendAsync(HttpMethod.Get, "/api/nope");

        // Plugins: a local middleware (no network), its job, options, order, pick, export and removal.
        var pluginDir = Directory.CreateTempSubdirectory("astra-guard-plugin-").FullName;
        File.WriteAllText(Path.Combine(pluginDir, "package.json"), """{"name":"guard-mw","version":"1.0.0","magpie":{"middleware":"./g.middleware.js","options":{"a":1}}}""");
        File.WriteAllText(Path.Combine(pluginDir, "g.middleware.js"), "export function onRequest(b) { return b }");
        await host.GetJsonAsync("/api/plugins");
        await host.GetJsonAsync("/api/plugins/runtime");
        var addJob = await host.SendAsync(HttpMethod.Post, "/api/plugins", new { spec = pluginDir });
        var jobId = addJob.Body!["id"]!.GetValue<string>();
        for (var i = 0; i < 50 && (await host.GetJsonAsync($"/api/plugins/jobs/{jobId}"))!["state"]!.GetValue<string>() == "running"; i++)
            await Task.Delay(50);
        var spec = Uri.EscapeDataString(pluginDir);
        await host.SendAsync(HttpMethod.Patch, $"/api/plugins?spec={spec}", new { options = new { a = 2 } });
        await host.SendAsync(HttpMethod.Put, "/api/plugins/order", new { specs = new[] { pluginDir } });
        await host.SendAsync(HttpMethod.Post, "/api/plugins/prefer", new { spec = pluginDir, provider = "guard" });
        await host.GetJsonAsync("/api/plugins/export");
        await host.SendAsync(HttpMethod.Delete, $"/api/plugins?spec={spec}");
        await host.SendAsync(HttpMethod.Get, "/api/plugins/jobs/missing");

        var claude = await ClaudeDirectTests.AddClaudeProviderAsync(host);
        await ClaudeDirectTests.AddAccountAsync(host, claude, "guard-acc", "Guard");
        await host.GetJsonAsync($"/api/providers/{claude.Id}/usage");
        await host.SendAsync(HttpMethod.Put, "/api/clients/claude-code/direct", new { mode = "direct", accountId = "guard-acc", telemetryEnabled = true });
        await host.SendAsync(HttpMethod.Post, "/api/clients/claude-code/direct/prepare", new { accountId = "guard-acc" });
        await host.GetJsonAsync("/api/clients/claude-code/direct");
        await host.GetJsonAsync("/api/clients/claude-code/direct/accounts/guard-acc/requests");

        // Traces (.plan/observability.md §4): a seeded call with a span tree and an account attempt, so every payload has data.
        await host.Db.Requests.InsertBatchAsync([new Astra.Core.Requests.RequestRecord
        {
            Id = "guard-trace", StartedAtUtc = DateTimeOffset.UtcNow, ClientKind = "claude-code", ProviderId = claude.Id, ProviderName = claude.Name,
            InboundProtocol = "anthropic", TtfbMs = 300, TraceId = "0af7651916cd43dd8448eb211c80319c", SessionKey = "cc:guard",
            ChainKey = "fp:guard", TurnId = "guard-trace", Step = 1, CallKind = "main", LinkSource = "new_turn",
            Spans =
            [
                new() { SpanId = "a0000000000000a1", Name = "gateway.request", Kind = "server", DurationUs = 1000, AttributesJson = """{"x":1}""" },
                new()
                {
                    SpanId = "a0000000000000a2", ParentSpanId = "a0000000000000a1", Name = "upstream.attempt", Kind = "client", StartUs = 10,
                    DurationUs = 900, AttributesJson = """{"astra.account.id":"guard-acc","astra.ratelimit.x-ratelimit-remaining-requests":"41"}""",
                    EventsJson = """[{"name":"first_token","offset_us":5}]""",
                },
                new() { SpanId = "a0000000000000a3", ParentSpanId = "a0000000000000a1", Name = "gateway.route", DurationUs = 40 },
            ],
        }]);
        await host.GetJsonAsync("/api/traces/requests/guard-trace");
        await host.GetJsonAsync("/api/traces/sessions");
        await host.GetJsonAsync("/api/traces/sessions/cc:guard");
        await host.GetJsonAsync("/api/traces/topology?range=24h");
        await host.SendAsync(HttpMethod.Get, "/api/traces/requests/missing");

        var unregistered = JsonContexts.FallbackTypes;
        Assert.True(unregistered.Count == 0,
            "These types had no generated metadata and fell back to reflection — add them to the owning "
            + "assembly's JsonSerializerContext: " + string.Join(", ", unregistered.Select(t => t.FullName)));
    }

    /// <summary>The provider probe's upstream: one host lists a model, one refuses the key, any other is unreachable.</summary>
    private sealed class GuardUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri?.Host switch
            {
                "models.guard.invalid" => Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"guard-model"}]}""")),
                "denied.guard.invalid" => Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid api key"}}""")),
                "judge.guard.invalid" => Task.FromResult(Json(HttpStatusCode.OK,
                    """{"model":"jev-1.13.0","answers":{"grade":{"type":"choice","choice":"heavy","confidence":0.9,"probabilities":{"light":0.05,"standard":0.05,"heavy":0.9}}},"usage":{"input_tokens":10,"output_tokens":2}}""")),
                _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("unreachable in the guard test")),
            };

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}

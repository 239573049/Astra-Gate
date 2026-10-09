using System.Net;
using Astra.Core;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// Guards the source-generated JSON metadata: every type that crosses a JSON boundary must be
/// registered in an assembly's <c>JsonSerializerContext</c> (see <see cref="JsonContexts"/>).
///
/// An unregistered type still works on the JIT through the registry's reflection fallback — which is
/// exactly why the mistake is easy to make and invisible until a Native AOT build throws at runtime.
/// This test exercises the admin API and then asserts the fallback was never used.
/// </summary>
public sealed class JsonRegistrationTests
{
    [Fact]
    public async Task AdminApi_payloads_are_all_served_from_generated_metadata()
    {
        await using var host = await TestHost.StartAsync();

        // Exercise every response shape we can reach without a provider or an upstream.
        string[] gets =
        [
            "/api/health", "/api/version", "/api/auth/status", "/api/settings", "/api/privacy",
            "/api/privacy/events", "/api/privacy/events/stats", "/api/providers", "/api/clients", "/api/tokens",
            "/api/update/status", "/api/models", "/api/requests", "/api/stats/summary", "/api/stats/timeseries",
            "/api/stats/top-models", "/api/stats/activity-heatmap", "/api/provider-quota/templates",
            "/api/providers/import/sources",
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
        await host.SendAsync(HttpMethod.Post, "/api/tokens", new { name = "Guard" });
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { locale = "en" });
        await host.SendAsync(HttpMethod.Post, "/api/privacy/dry-run", new { text = "hello" });
        await host.SendAsync(HttpMethod.Post, "/api/auth/login", new { password = "wrong" });
        await host.SendAsync(HttpMethod.Delete, "/api/provider-accounts/missing");
        await host.SendAsync(HttpMethod.Get, "/api/models/missing");
        await host.SendAsync(HttpMethod.Get, "/api/nope");

        var unregistered = JsonContexts.FallbackTypes;
        Assert.True(unregistered.Count == 0,
            "These types had no generated metadata and fell back to reflection — add them to the owning "
            + "assembly's JsonSerializerContext: " + string.Join(", ", unregistered.Select(t => t.FullName)));
    }
}

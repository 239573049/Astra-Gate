using Astra.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// GitHub Copilot subscription → api.githubcopilot.com as the Copilot CLI (<c>Copilot-Integration-Id:
/// copilot-developer-cli</c>). The static identity (User-Agent, integration id, API version) is the provider template's
/// <c>defaultHeaders</c>; this adds what the CLI sends per request: a fresh <c>X-Request-Id</c> that also seeds the
/// interaction / agent-task ids, and the conversation intent. A value the client itself sent for one of these headers
/// wins (so a real Copilot CLI pointed at Astra keeps its own ids), then whatever the provider's headers configured,
/// then the default. Credentials are never taken from the client.
/// </summary>
public static class CopilotCliHeaders
{
    /// <summary>Provider family key of the GitHub Copilot subscription template (same rule as <c>SubscriptionSupport</c>).</summary>
    public static bool Applies(Provider provider) => SubscriptionSupport.ProviderKeyOf(provider) == "github-copilot-subscription";

    public static void Apply(HttpRequestMessage request, IHeaderDictionary clientHeaders)
    {
        var requestId = Pick(request, clientHeaders, "X-Request-Id") ?? Guid.NewGuid().ToString();
        Set(request, clientHeaders, "X-Request-Id", requestId);
        Set(request, clientHeaders, "OpenAI-Intent", "conversation-agent");
        Set(request, clientHeaders, "X-Interaction-Id", requestId);
        Set(request, clientHeaders, "X-Interaction-Type", "conversation-agent");
        Set(request, clientHeaders, "X-Agent-Task-Id", requestId);
    }

    private static string? Pick(HttpRequestMessage request, IHeaderDictionary clientHeaders, string name) =>
        clientHeaders[name].ToString() is { Length: > 0 } client ? client
        : request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static void Set(HttpRequestMessage request, IHeaderDictionary clientHeaders, string name, string fallback)
    {
        var value = Pick(request, clientHeaders, name) ?? fallback;
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}

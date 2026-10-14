using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Clients.Adapters;

/// <summary>
/// The pi-ai model vocabulary shared by Pi, omp, MiniMax Code and DeepSeek Harness: the <c>api</c> names of the wire
/// protocols and the gateway base each protocol's SDK expects. The OpenAI SDKs append <c>/chat/completions</c> or
/// <c>/responses</c> to <c>…/v1</c>; pi-ai's Anthropic client appends <c>/v1/messages</c> itself, so it gets the gateway
/// root; its Gemini client posts to <c>{base}/models/…</c> with the version inside the base, so it gets <c>…/v1beta</c>.
/// </summary>
internal static class PiAiModels
{
    public static string ApiOf(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.OpenAIResponses => "openai-responses",
        ApiProtocol.Anthropic => "anthropic-messages",
        ApiProtocol.Gemini => "google-generative-ai",
        _ => "openai-completions",
    };

    /// <summary>The base URL pi-ai's client for <paramref name="protocol"/> needs to reach the gateway.</summary>
    public static string BaseUrlFor(string gatewayBaseUrl, ApiProtocol protocol)
    {
        var root = gatewayBaseUrl.TrimEnd('/');
        return protocol switch
        {
            ApiProtocol.Anthropic => root,
            ApiProtocol.Gemini => root + "/v1beta",
            _ => root + "/v1",
        };
    }

    /// <summary>
    /// The per-model fields Astra knows, under pi-ai's names: <c>reasoning</c> (pi-ai assumes false, which hides every thinking
    /// level), <c>input</c> (text, plus image for vision models), <c>contextWindow</c> and <c>maxTokens</c>; unknown ones are left
    /// to the client's own defaults or catalog.
    /// </summary>
    public static void AddHints(JsonObject model, JsonObject? info)
    {
        if (Config.ClientAdapterBase.BoolHint(info?["reasoning"]) is { } reasoning) model["reasoning"] = reasoning;
        if (Config.ClientAdapterBase.BoolHint(info?["vision"]) is { } vision)
            model["input"] = vision ? new JsonArray("text", "image") : new JsonArray("text");
        if (Config.ClientAdapterBase.PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
        if (Config.ClientAdapterBase.PositiveLong(info?["maxOutputTokens"]) is { } maxTokens) model["maxTokens"] = maxTokens;
    }
}

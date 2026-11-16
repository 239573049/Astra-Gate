using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// What is particular to the two big clouds' model services, kept out of the pipeline's own flow: which wire protocol serves
/// a model on Amazon Bedrock and on Google Vertex AI, where a Vertex request goes (project and location are in the path)
/// and how Anthropic's body is shaped there. Credentials are <see cref="UpstreamAuth"/>'s business (SigV4, a service
/// account's access token). The routing rules follow magpie's presets and <c>vertex.go</c> (MIT).
/// </summary>
public static class CloudProviders
{
    /// <summary>Anthropic's version string a Claude request names in its body on Vertex AI (the header is not used there).</summary>
    public const string VertexAnthropicVersion = "vertex-2023-10-16";

    /// <summary>Whether the provider is Google Vertex AI: its credentials are a Google service account's.</summary>
    public static bool IsVertex(Provider provider) => provider.AuthScheme == AuthSchemes.GcpServiceAccount;

    /// <summary>
    /// Whether the provider is Amazon Bedrock: one of its endpoints is at a Bedrock runtime or Mantle host. Made from a
    /// template or typed in by hand, either way.
    /// </summary>
    public static bool IsBedrock(Provider provider) => provider.Endpoints.Any(e => IsBedrockHost(e.BaseUrl));

    private static bool IsBedrockHost(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.StartsWith("bedrock-runtime.", StringComparison.OrdinalIgnoreCase) && uri.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.StartsWith("bedrock-mantle.", StringComparison.OrdinalIgnoreCase) && uri.Host.EndsWith(".api.aws", StringComparison.OrdinalIgnoreCase));

    private static bool IsMantle(ProviderEndpoint endpoint) =>
        Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) && uri.Host.StartsWith("bedrock-mantle.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The protocols one of these providers serves a model over, or an empty list when the provider is neither (the usual
    /// choice of endpoint then applies). Bedrock: Claude on Anthropic's Messages, OpenAI's closed GPT models on Responses,
    /// everything else on chat completions. Vertex: Claude (<c>claude-…</c>) on Messages, partner and open models
    /// (<c>publisher/model</c>) on chat completions, the rest (Gemini) on generateContent.
    /// </summary>
    public static IReadOnlyList<ApiProtocol> ProtocolsFor(Provider provider, string modelId)
    {
        if (IsVertex(provider))
            return [IsVertexPartner(modelId) ? ApiProtocol.OpenAIChat : IsVertexClaude(modelId) ? ApiProtocol.Anthropic : ApiProtocol.Gemini];
        if (!IsBedrock(provider)) return [];
        if (IsBedrockClaude(modelId)) return [ApiProtocol.Anthropic];
        if (IsBedrockGpt(modelId)) return [ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat];
        return [ApiProtocol.OpenAIChat];
    }

    /// <summary>A Bedrock id of a Claude model: <c>anthropic.claude-…</c>, with or without a <c>global.</c> / <c>us.</c> profile prefix.</summary>
    public static bool IsBedrockClaude(string model) =>
        model.Contains("anthropic.claude", StringComparison.OrdinalIgnoreCase) || model.StartsWith("claude", StringComparison.OrdinalIgnoreCase);

    // OpenAI's closed GPT models (not gpt-oss) answer on Responses on Bedrock (magpie's bedrockGPT).
    private static bool IsBedrockGpt(string model)
    {
        var m = model.ToLowerInvariant();
        var at = m.IndexOf("openai.gpt-", StringComparison.Ordinal);
        return at >= 0 && !m[at..].StartsWith("openai.gpt-oss", StringComparison.Ordinal);
    }

    private static bool IsMantleOpenAIRoute(string model) => IsBedrockGpt(model) || model.StartsWith("xai.", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Vertex AI partner or open model (Model as a Service): its id has a publisher before a slash.</summary>
    public static bool IsVertexPartner(string model) => model.Contains('/', StringComparison.Ordinal);

    /// <summary>A Claude model on Vertex AI (<c>claude-sonnet-5-5</c>, <c>claude-sonnet-4-5@20250929</c>).</summary>
    public static bool IsVertexClaude(string model) =>
        !IsVertexPartner(model) && model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The request address when the provider's cloud decides it, else null. Vertex: <c>https://&lt;host&gt;/v1/projects/&lt;project&gt;/locations/&lt;location&gt;</c>
    /// plus the publisher's path for the model; the endpoint's own address is the root when it is not Google's global host
    /// (a regional or rep host; a test's server). Bedrock Mantle serves OpenAI's closed models and Grok on <c>/openai/v1</c>
    /// instead of <c>/v1</c>.
    /// </summary>
    /// <exception cref="GatewayException">A Vertex provider with no project.</exception>
    public static string? Url(Provider provider, ProviderEndpoint endpoint, string model, bool stream)
    {
        if (endpoint.FullUrl) return null;
        if (IsVertex(provider)) return VertexUrl(provider, endpoint, model, stream);
        if (endpoint.Protocol is ApiProtocol.OpenAIChat or ApiProtocol.OpenAIResponses && IsMantle(endpoint) && IsMantleOpenAIRoute(model))
        {
            var root = endpoint.BaseUrl.TrimEnd('/');
            if (root.EndsWith("/v1", StringComparison.Ordinal) && !root.EndsWith("/openai/v1", StringComparison.Ordinal))
                return UpstreamUrls.For(new ProviderEndpoint { Protocol = endpoint.Protocol, BaseUrl = root[..^3] + "/openai/v1" }, model, stream);
        }
        return null;
    }

    private static string VertexUrl(Provider provider, ProviderEndpoint endpoint, string model, bool stream)
    {
        var (project, location) = VertexTarget(provider);
        if (project.Length == 0)
            throw new GatewayException(400, "invalid_request_error", $"提供商 {provider.Name} 没有设置 Google Cloud 项目（settings.vertex.project）。请在提供商设置里填写项目 ID。");
        var root = VertexRoot(endpoint.BaseUrl, location);
        var @base = $"{root}/v1/projects/{Uri.EscapeDataString(project)}/locations/{Uri.EscapeDataString(location)}";
        var escaped = Uri.EscapeDataString(model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model);
        return endpoint.Protocol switch
        {
            ApiProtocol.Anthropic => $"{@base}/publishers/anthropic/models/{escaped}:{(stream ? "streamRawPredict" : "rawPredict")}",
            ApiProtocol.OpenAIChat or ApiProtocol.OpenAIResponses => $"{@base}/endpoints/openapi/chat/completions",
            _ => $"{@base}/publishers/google/models/{escaped}:{(stream ? "streamGenerateContent?alt=sse" : "generateContent")}",
        };
    }

    /// <summary>The Vertex project and location of a provider: <c>settings.vertex</c>, location <c>global</c> when unset.</summary>
    public static (string Project, string Location) VertexTarget(Provider provider)
    {
        var vertex = provider.Settings["vertex"] as JsonObject;
        string Read(string key) => vertex?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var location = Read("location").ToLowerInvariant();
        return (Read("project"), location.Length == 0 ? "global" : location);
    }

    /// <summary>Vertex's host follows the location: global, the <c>us</c> / <c>eu</c> multi-regions, or a region.</summary>
    public static string VertexHost(string location) => location switch
    {
        "global" => "aiplatform.googleapis.com",
        "us" or "eu" => $"aiplatform.{location}.rep.googleapis.com",
        _ => $"{location}-aiplatform.googleapis.com",
    };

    private static string VertexRoot(string baseUrl, string location)
    {
        var uri = new Uri(baseUrl);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        return uri.Host.Equals("aiplatform.googleapis.com", StringComparison.OrdinalIgnoreCase) ? $"https://{VertexHost(location)}" : origin;
    }

    /// <summary>Whether the upstream body of this protocol is shaped for the provider's cloud (the pipeline then rewrites a copy).</summary>
    public static bool ShapesBody(Provider provider, ApiProtocol protocol) => IsVertex(provider) && protocol == ApiProtocol.Anthropic;

    /// <summary>
    /// Anthropic's Messages body on Vertex AI: the model is in the path, so it leaves the body, and the API version the
    /// header carries elsewhere goes in the body (<see cref="VertexAnthropicVersion"/>) unless the client named one.
    /// Returns whether the tree changed.
    /// </summary>
    public static bool ShapeBody(Provider provider, ApiProtocol protocol, JsonObject tree)
    {
        if (!ShapesBody(provider, protocol)) return false;
        var changed = tree.Remove("model");
        if (!tree.ContainsKey("anthropic_version"))
        {
            tree["anthropic_version"] = VertexAnthropicVersion;
            changed = true;
        }
        return changed;
    }

    /// <summary>A provider's own SigV4 scope (<c>settings.aws.region</c> / <c>service</c>), for an address whose host does not say it (a VPC endpoint).</summary>
    public static (string? Region, string? Service) AwsScopeOverride(Provider provider)
    {
        var aws = provider.Settings["aws"] as JsonObject;
        string? Read(string key) => aws?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : null;
        return (Read("region"), Read("service"));
    }
}

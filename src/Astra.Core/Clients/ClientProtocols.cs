using System.Text.Json.Nodes;

namespace Astra.Core.Clients;

/// <summary>
/// The wire protocols Astra can configure per model for a client, and the user's per-model choices. A model is configured
/// in its native protocol (the "protocols" hint: the protocols the gateway passes through for it without translation)
/// when the client can speak one of them, else in the client's default, which the gateway translates. The user's choices
/// live in <c>clients.extra_json</c> under <see cref="ExtrasKey"/> (<c>{"protocolOverrides":{"gpt-5":"openai-chat"}}</c>),
/// so they need no migration; a choice wins over the native protocol while the client can speak it.
/// </summary>
public static class ClientProtocols
{
    public const string ExtrasKey = "protocolOverrides";

    private static readonly IReadOnlyList<ApiProtocol> OpenAiAndAnthropic =
        [ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, ApiProtocol.Anthropic];

    private static readonly IReadOnlyList<ApiProtocol> AllFour =
        [ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, ApiProtocol.Anthropic, ApiProtocol.Gemini];

    /// <summary>
    /// The protocols Astra can write for one model of this client, the client's default (<see cref="ClientKinds.ProtocolOf"/>)
    /// first. A single entry means every model goes over that protocol.
    /// </summary>
    public static IReadOnlyList<ApiProtocol> For(string kind) => kind switch
    {
        ClientKinds.NextCoWork or ClientKinds.Droid or ClientKinds.KimiCode or ClientKinds.OpenCode or ClientKinds.MiMoCode
            or ClientKinds.CopilotCli or ClientKinds.MiniMaxCode or ClientKinds.DeepSeekHarness
            or ClientKinds.VsCodeCopilot or ClientKinds.VsCodeInsiders or ClientKinds.VsCodium or ClientKinds.ZCode
            or ClientKinds.TKCode => OpenAiAndAnthropic,
        ClientKinds.Pi or ClientKinds.Omp or ClientKinds.QwenCode or ClientKinds.Crush => AllFour,
        ClientKinds.GrokBuild => [ApiProtocol.OpenAIResponses, ApiProtocol.OpenAIChat, ApiProtocol.Anthropic],
        ClientKinds.Zed => [ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses],
        ClientKinds.HermesAgent => [ApiProtocol.OpenAIChat, ApiProtocol.Anthropic],
        ClientKinds.Paseo => [ApiProtocol.Anthropic, ApiProtocol.OpenAIResponses],
        _ => [ClientKinds.ProtocolOf(kind)],
    };

    /// <summary>Whether the client's models can use more than one protocol.</summary>
    public static bool IsPerModel(string kind) => For(kind).Count > 1;

    /// <summary>The user's per-model protocol choices in an extras object; entries that do not parse are skipped.</summary>
    public static IReadOnlyDictionary<string, ApiProtocol> Overrides(JsonObject? extras)
    {
        var result = new Dictionary<string, ApiProtocol>(StringComparer.Ordinal);
        if (extras?[ExtrasKey] is not JsonObject overrides) return result;
        foreach (var (model, value) in overrides)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var id) && ApiProtocols.TryParse(id, out var protocol))
                result[model] = protocol;
        }
        return result;
    }

    /// <summary>Validates a "protocolOverrides" object for a client; returns an error message, or null when valid.</summary>
    public static string? Validate(string kind, JsonObject overrides)
    {
        var supported = For(kind);
        foreach (var (model, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(model)) return "Empty model id in protocolOverrides";
            if (value is null) continue;
            if (value is not JsonValue v || !v.TryGetValue<string>(out var id) || !ApiProtocols.TryParse(id, out var protocol))
                return $"Invalid protocol for model '{model}'";
            if (!supported.Contains(protocol)) return $"{kind} cannot use {protocol.ToId()} for model '{model}'";
        }
        return null;
    }
}

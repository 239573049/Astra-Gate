using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Core;

/// <summary>The four wire protocols Astra speaks, both inbound and upstream.</summary>
[JsonConverter(typeof(ApiProtocolJsonConverter))]
public enum ApiProtocol
{
    OpenAIChat,
    OpenAIResponses,
    Anthropic,
    Gemini,
}

public static class ApiProtocols
{
    public static readonly IReadOnlyList<ApiProtocol> All =
        [ApiProtocol.OpenAIChat, ApiProtocol.OpenAIResponses, ApiProtocol.Anthropic, ApiProtocol.Gemini];

    public static string ToId(this ApiProtocol p) => p switch
    {
        ApiProtocol.OpenAIChat => "openai-chat",
        ApiProtocol.OpenAIResponses => "openai-responses",
        ApiProtocol.Anthropic => "anthropic",
        ApiProtocol.Gemini => "gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static bool TryParse(string? id, out ApiProtocol protocol)
    {
        switch (id?.Trim().ToLowerInvariant())
        {
            case "openai-chat" or "openai" or "chat": protocol = ApiProtocol.OpenAIChat; return true;
            case "openai-responses" or "responses": protocol = ApiProtocol.OpenAIResponses; return true;
            case "anthropic" or "anthropic-messages": protocol = ApiProtocol.Anthropic; return true;
            case "gemini" or "google": protocol = ApiProtocol.Gemini; return true;
            default: protocol = default; return false;
        }
    }

    public static ApiProtocol Parse(string id) =>
        TryParse(id, out var p) ? p : throw new FormatException($"Unknown protocol '{id}'.");
}

public sealed class ApiProtocolJsonConverter : JsonConverter<ApiProtocol>
{
    public override ApiProtocol Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ApiProtocols.Parse(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, ApiProtocol value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToId());
}

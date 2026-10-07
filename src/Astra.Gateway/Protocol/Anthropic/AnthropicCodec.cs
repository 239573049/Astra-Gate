using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Gateway.Protocol.Anthropic;

/// <summary>
/// Anthropic Messages wire protocol (POST /v1/messages). Stateless singleton; the per-response state lives in
/// <see cref="AnthropicResponseDecoder"/> / <see cref="AnthropicResponseEncoder"/>.
/// </summary>
public sealed class AnthropicCodec : IProtocolCodec
{
    public ApiProtocol Protocol => ApiProtocol.Anthropic;

    public UnifiedRequest DecodeRequest(JsonObject body, RequestDecodeContext ctx) => AnthropicRequestCodec.Decode(body);

    public JsonObject EncodeRequest(UnifiedRequest request, RequestEncodeContext ctx) => AnthropicRequestCodec.Encode(request, ctx);

    public IResponseDecoder CreateResponseDecoder(ResponseDecodeContext ctx) => new AnthropicResponseDecoder(ctx);

    public IResponseEncoder CreateResponseEncoder(ResponseEncodeContext ctx) => new AnthropicResponseEncoder(ctx);

    public JsonObject EncodeError(int status, string type, string message) => new()
    {
        ["type"] = "error",
        ["error"] = new JsonObject
        {
            ["type"] = MapErrorType(status, type),
            ["message"] = message,
        },
    };

    public (string Type, string Message) DecodeError(int status, string body) => AnthropicErrors.Decode(status, body);

    /// <summary>
    /// Status → Anthropic error type. A type that is already an Anthropic type (parsed from an upstream body or
    /// raised by the pipeline) is kept as-is.
    /// </summary>
    internal static string MapErrorType(int status, string type)
    {
        if (AnthropicErrors.KnownTypes.Contains(type)) return type;
        return status switch
        {
            400 => "invalid_request_error",
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            413 => "request_too_large",
            429 => "rate_limit_error",
            529 => "overloaded_error",
            >= 500 => "api_error",
            _ => "invalid_request_error",
        };
    }
}

/// <summary>Parses Anthropic upstream error bodies; falls back to the raw body (truncated).</summary>
internal static class AnthropicErrors
{
    public static readonly HashSet<string> KnownTypes =
    [
        "invalid_request_error", "authentication_error", "billing_error", "permission_error", "not_found_error",
        "request_too_large", "rate_limit_error", "api_error", "overloaded_error",
    ];

    public static (string Type, string Message) Decode(int status, string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
            {
                var type = error["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) && t.Length > 0 ? t : null;
                var message = error["message"] is JsonValue mv && mv.TryGetValue<string>(out var m) ? m : null;
                if (type is not null || message is not null)
                    return (type ?? MapFallbackType(status), message ?? body);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: fall through to the raw body.
        }
        return (MapFallbackType(status), Truncate(body, 500));
    }

    private static string MapFallbackType(int status) => status >= 500 ? "api_error" : "invalid_request_error";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

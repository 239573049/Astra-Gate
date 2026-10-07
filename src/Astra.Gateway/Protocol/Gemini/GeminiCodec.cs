using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Gateway.Protocol.Gemini;

/// <summary>
/// Google Gemini codec (plan §6.4): generateContent / streamGenerateContent?alt=sse. The model and the
/// streaming choice travel in the URL path (/v1beta/models/{model}:streamGenerateContent?alt=sse), never
/// in the request body. This class is a stateless shell; the per-response state lives in
/// <see cref="GeminiResponseDecoder"/> / <see cref="GeminiResponseEncoder"/>.
/// </summary>
public sealed class GeminiCodec : IProtocolCodec
{
    public ApiProtocol Protocol => ApiProtocol.Gemini;

    public UnifiedRequest DecodeRequest(JsonObject body, RequestDecodeContext ctx) => GeminiRequests.Decode(body, ctx);

    public JsonObject EncodeRequest(UnifiedRequest request, RequestEncodeContext ctx) => GeminiRequests.Encode(request, ctx);

    public IResponseDecoder CreateResponseDecoder(ResponseDecodeContext ctx) => new GeminiResponseDecoder(ctx);

    public IResponseEncoder CreateResponseEncoder(ResponseEncodeContext ctx) => new GeminiResponseEncoder(ctx);

    /// <summary>Gemini error body: {"error":{"code":400,"message":"…","status":"INVALID_ARGUMENT"}}.</summary>
    public JsonObject EncodeError(int status, string type, string message) => new()
    {
        ["error"] = new JsonObject
        {
            ["code"] = status,
            ["message"] = message,
            ["status"] = StatusName(status),
        },
    };

    public (string Type, string Message) DecodeError(int status, string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
            {
                var message = Fields.Str(error, "message") ?? "Upstream error";
                var code = Fields.Str(error, "status") ?? StatusName(Fields.Int(error, "code") ?? status);
                return (code, message);
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall through to the raw body.
        }
        return ("upstream_error", body.Length <= 500 ? body : body[..500]);
    }

    /// <summary>google.rpc.Code name for an HTTP status, used in the error body's "status" field.</summary>
    internal static string StatusName(int status) => status switch
    {
        400 => "INVALID_ARGUMENT",
        401 => "UNAUTHENTICATED",
        403 => "PERMISSION_DENIED",
        404 => "NOT_FOUND",
        429 => "RESOURCE_EXHAUSTED",
        503 => "UNAVAILABLE",
        504 => "DEADLINE_EXCEEDED",
        >= 500 => "INTERNAL",
        _ => "UNKNOWN",
    };
}

/// <summary>
/// Field access helpers shared by the Gemini codec files. Gemini REST payloads are camelCase; the
/// snake_case variants are accepted as aliases because parts of the ecosystem send them.
/// </summary>
internal static class Fields
{
    /// <summary>First matching property, or null.</summary>
    public static JsonNode? Node(JsonObject? o, params string[] keys)
    {
        if (o is null) return null;
        foreach (var key in keys)
        {
            if (o.TryGetPropertyValue(key, out var node) && node is not null) return node;
        }
        return null;
    }

    public static string? Str(JsonObject? o, params string[] keys) =>
        Node(o, keys) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(JsonObject? o, params string[] keys) => Node(o, keys) switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => bool.TryParse(s, out var b) ? b : null,
        _ => null,
    };

    public static int? Int(JsonObject? o, params string[] keys) => Node(o, keys) switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var i) => i,
        _ => null,
    };

    public static double? Double(JsonObject? o, params string[] keys) => Node(o, keys) switch
    {
        JsonValue v when v.TryGetValue<double>(out var d) => d,
        JsonValue v when v.TryGetValue<string>(out var s) && double.TryParse(s, out var d) => d,
        _ => null,
    };
}

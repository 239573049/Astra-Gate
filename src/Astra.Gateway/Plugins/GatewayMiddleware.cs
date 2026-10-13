using System.Text;
using System.Text.Json;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Astra.Plugins.Middleware;

namespace Astra.Gateway.Plugins;

/// <summary>
/// What the gateway tells magpie-compatible middleware about a request (<see cref="MiddlewareInfo"/>),
/// and how a middleware's ctx.reject reaches the agent: a <see cref="GatewayException"/> the pipeline
/// writes in the agent's own API's error shape.
/// </summary>
public static class GatewayMiddleware
{
    /// <summary>The API names magpie's middleware see in ctx.protocol.</summary>
    public static string ProtocolName(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.OpenAIChat => "chat",
        ApiProtocol.OpenAIResponses => "responses",
        ApiProtocol.Anthropic => "anthropic",
        ApiProtocol.Gemini => "gemini",
        _ => protocol.ToId(),
    };

    /// <summary>The request's info: its model and stream flag read from the body (Gemini's from its URL).</summary>
    public static MiddlewareInfo Info(ApiProtocol protocol, ReadOnlySpan<byte> body, string path, string? agent, string? pathModel, bool? pathStream)
    {
        var (model, stream) = protocol == ApiProtocol.Gemini ? (pathModel, pathStream ?? false) : Peek(body);
        return new MiddlewareInfo(ProtocolName(protocol), model, stream, path, agent);
    }

    /// <summary>A middleware's reject as the error the agent gets.</summary>
    public static GatewayException Rejected(MiddlewareReject reject) =>
        new(reject.Status, reject.Status switch
        {
            401 => "authentication_error",
            403 => "permission_error",
            404 => "not_found_error",
            413 => "request_too_large",
            429 => "rate_limit_error",
            >= 500 => "api_error",
            _ => "invalid_request_error",
        }, reject.Message);

    // The top-level "model" and "stream" of a JSON body, without building a document.
    private static (string? Model, bool Stream) Peek(ReadOnlySpan<byte> body)
    {
        string? model = null;
        var stream = false;
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return (null, false);
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isModel = reader.ValueTextEquals("model"u8);
                var isStream = !isModel && reader.ValueTextEquals("stream"u8);
                reader.Read();
                if (isModel && reader.TokenType == JsonTokenType.String) model = reader.GetString();
                else if (isStream && reader.TokenType is JsonTokenType.True or JsonTokenType.False) stream = reader.GetBoolean();
                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }
        return (model, stream);
    }
}

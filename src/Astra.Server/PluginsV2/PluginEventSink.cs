using System.Text.Json.Nodes;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Astra.Plugins.V2;

namespace Astra.Server.PluginsV2;

/// <summary>
/// The request sink with v2's <c>request.completed</c> event on top (design/plugin-v2 02 §7): each finished request's
/// metadata — never its bodies, client IP or user agent — goes to the plugins subscribed to it (they need the permission
/// data.requests:read). Nothing is built when no plugin listens.
/// </summary>
public sealed class PluginEventSink(IRequestSink inner, PluginV2Host host) : IRequestSink
{
    public ValueTask EnqueueAsync(RequestRecord record)
    {
        if (host.HasSubscribers("request.completed"))
            host.Publish("request.completed", new JsonObject
            {
                ["id"] = record.Id, ["client"] = record.ClientKind, ["token"] = record.TokenName, ["provider"] = record.ProviderName,
                ["protocol"] = record.InboundProtocol, ["model"] = record.RequestedModel, ["status"] = record.Status,
                ["httpStatus"] = record.HttpStatus, ["inputTokens"] = record.TotalInputTokens, ["outputTokens"] = record.TotalOutputTokens,
                ["costNanoUsd"] = record.CostNanoUsd, ["totalMs"] = record.TotalMs,
            });
        return inner.EnqueueAsync(record);
    }
}

using System.Text.Json.Nodes;

namespace Astra.Gateway.Protocol.Responses;

/// <summary>
/// Some clients post a Responses body that carries Chat Completions' spelling of the thinking effort — a top-level
/// <c>reasoning_effort</c> — and strict Responses upstreams (DeepSeek: <c>json: unknown field "reasoning_effort"</c>) refuse
/// the whole request. On the pass-through path the body would otherwise go on as the client wrote it, so the field is
/// moved to its Responses place, <c>reasoning.effort</c>; an effort already written there wins.
/// </summary>
public static class ResponsesChatSpelling
{
    /// <summary>True when the body held a top-level <c>reasoning_effort</c> (it is removed, and moved when it is a non-empty string).</summary>
    public static bool MoveReasoningEffort(JsonObject body)
    {
        if (!body.TryGetPropertyValue("reasoning_effort", out var node))
            return false;
        body.Remove("reasoning_effort");
        if (node is JsonValue v && v.TryGetValue<string>(out var effort) && effort.Length > 0)
        {
            var reasoning = body["reasoning"] as JsonObject ?? new JsonObject();
            if (reasoning["effort"] is null)
                reasoning["effort"] = effort;
            body["reasoning"] = reasoning;
        }
        return true;
    }
}

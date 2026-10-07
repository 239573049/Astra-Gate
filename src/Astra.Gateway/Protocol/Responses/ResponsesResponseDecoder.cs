using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Responses;

/// <summary>
/// Upstream OpenAI Responses output (SSE stream or non-streaming JSON) → IR events.
/// Event dispatch trusts <c>data.type</c> (the SSE <c>event:</c> field is only the fallback).
/// Block indexes (0,1,2 …) are assigned in order of appearance: reasoning / function_call items open a
/// block on <c>response.output_item.added</c>, message text blocks open on <c>response.content_part.added</c>
/// (one per (output_index, content_index)). After <c>response.failed</c> or an <c>error</c> event the
/// stream is terminal — <see cref="Complete"/> emits nothing further.
/// </summary>
public sealed class ResponsesResponseDecoder(ResponseDecodeContext ctx) : IResponseDecoder
{
    private readonly ResponseDecodeContext _ctx = ctx;

    public string? ResponseModel { get; private set; }

    private int _nextBlock;
    private bool _started;
    private bool _stopped;
    private bool _terminal;

    /// <summary>block key → IR block index. Keys: "o{output_index}" for item blocks, "o{out}:c{content}" for text parts.</summary>
    private readonly Dictionary<string, int> _blocks = new();

    /// <summary>output_index → the blocks opened for that item, in order.</summary>
    private readonly Dictionary<int, List<(int Index, BlockKind Kind, string Key)>> _byOutput = new();

    public IEnumerable<UnifiedStreamEvent> DecodeSse(SseEvent sse)
    {
        var events = new List<UnifiedStreamEvent>();
        if (sse.Data is null or "" or "[DONE]")
            return events;
        JsonObject? data;
        try
        {
            data = JsonNode.Parse(sse.Data) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return events;
        }
        if (data is null)
            return events;
        if (ResponsesCodec.Str(data["response"] as JsonObject, "model") is { } model && !string.IsNullOrWhiteSpace(model))
            ResponseModel = model;
        var type = ResponsesCodec.Str(data, "type") ?? sse.Event;
        if (type is not null)
            HandleEvent(type, data, events);
        return events;
    }

    public IEnumerable<UnifiedStreamEvent> DecodeJson(JsonObject body)
    {
        if (ResponsesCodec.Str(body, "model") is { } model && !string.IsNullOrWhiteSpace(model)) ResponseModel = model;
        var events = new List<UnifiedStreamEvent>();
        if (!_started)
        {
            _started = true;
            events.Add(new MessageStartEvent(ResponsesCodec.Str(body, "id"), ResponsesCodec.Str(body, "model") ?? _ctx.Model));
        }
        if (body["output"] is JsonArray output)
            foreach (var item in output)
                DecodeOutputItem(item, events);
        FinishResponse(body, failed: ResponsesCodec.Str(body, "status") == "failed", events);
        return events;
    }

    /// <summary>Non-streaming: walks one entry of <c>response.output</c>, one IR block per content part.</summary>
    private void DecodeOutputItem(JsonNode? node, List<UnifiedStreamEvent> events)
    {
        if (node is not JsonObject item)
            return;
        switch (ResponsesCodec.Str(item, "type"))
        {
            case "message":
            {
                if (item["content"] is not JsonArray content)
                    break;
                foreach (var part in content)
                {
                    if (part is not JsonObject po)
                        continue;
                    var text = ResponsesCodec.Str(po, "type") == "refusal"
                        ? ResponsesCodec.Str(po, "refusal")
                        : ResponsesCodec.Str(po, "text");
                    if (text is null)
                        continue;
                    var index = _nextBlock++;
                    events.Add(new BlockStartEvent(index, BlockKind.Text));
                    events.Add(new TextDeltaEvent(index, text));
                    events.Add(new BlockStopEvent(index));
                }
                break;
            }

            case "reasoning":
            {
                var index = _nextBlock++;
                events.Add(new BlockStartEvent(index, BlockKind.Reasoning));
                var summaries = new List<string>();
                if (item["summary"] is JsonArray summary)
                    foreach (var s in summary)
                        if (s is JsonObject so && ResponsesCodec.Str(so, "text") is { Length: > 0 } st)
                            summaries.Add(st);
                if (summaries.Count > 0)
                    events.Add(new ReasoningDeltaEvent(index, string.Join("\n\n", summaries), Origin: ApiProtocol.OpenAIResponses));
                if (ResponsesCodec.Str(item, "encrypted_content") is { } encrypted)
                    events.Add(new ReasoningDeltaEvent(index, null, EncryptedContent: encrypted, Origin: ApiProtocol.OpenAIResponses));
                events.Add(new BlockStopEvent(index));
                break;
            }

            case "function_call":
            {
                var index = _nextBlock++;
                var callId = ResponsesCodec.Str(item, "call_id") ?? ResponsesCodec.Str(item, "id") ?? "call_" + Ulid.NewUlid();
                events.Add(new BlockStartEvent(index, BlockKind.ToolCall, callId, ResponsesCodec.Str(item, "name") ?? ""));
                events.Add(new ToolArgsDeltaEvent(index, ResponsesCodec.Str(item, "arguments") ?? ""));
                events.Add(new BlockStopEvent(index));
                break;
            }

            // web_search_call, file_search_call, … : server-side items are not representable in the IR.
        }
    }

    public IEnumerable<UnifiedStreamEvent> Complete()
    {
        var events = new List<UnifiedStreamEvent>();
        if (_terminal)
            return events;
        if (!_started)
        {
            // Degenerate upstream stream (nothing usable arrived): clients still need response.created.
            _started = true;
            events.Add(new MessageStartEvent(null, _ctx.Model));
        }
        CloseOpenBlocks(events);
        if (!_stopped)
        {
            _stopped = true;
            events.Add(new MessageStopEvent(FinishReason.Stop));
        }
        return events;
    }

    // ---------------------------------------------------------------- SSE dispatch

    private void HandleEvent(string type, JsonObject data, List<UnifiedStreamEvent> events)
    {
        switch (type)
        {
            case "response.created" or "response.in_progress":
                if (_started)
                    return;
                _started = true;
                var created = data["response"] as JsonObject;
                events.Add(new MessageStartEvent(
                    ResponsesCodec.Str(created, "id"),
                    ResponsesCodec.Str(created, "model") ?? _ctx.Model));
                break;

            case "response.output_item.added":
                OutputItemAdded(data, events);
                break;

            case "response.content_part.added":
                ContentPartAdded(data, events);
                break;

            case "response.output_text.delta" or "response.refusal.delta":
            {
                var index = EnsureTextBlock(data, events);
                events.Add(new TextDeltaEvent(index, ResponsesCodec.Str(data, "delta") ?? ""));
                break;
            }

            case "response.reasoning_summary_text.delta" or "response.reasoning_text.delta":
            {
                var index = EnsureItemBlock(data, BlockKind.Reasoning, events);
                events.Add(new ReasoningDeltaEvent(index, ResponsesCodec.Str(data, "delta"), Origin: ApiProtocol.OpenAIResponses));
                break;
            }

            case "response.function_call_arguments.delta":
            {
                var index = EnsureItemBlock(data, BlockKind.ToolCall, events);
                events.Add(new ToolArgsDeltaEvent(index, ResponsesCodec.Str(data, "delta") ?? ""));
                break;
            }

            case "response.output_text.done" or "response.refusal.done":
                // Closed at content_part.done; nothing to do here.
                break;

            case "response.content_part.done":
                if (_blocks.Remove(BlockKey(data), out var textIndex))
                    events.Add(new BlockStopEvent(textIndex));
                break;

            case "response.reasoning_summary_part.added" or "response.reasoning_summary_part.done"
                or "response.reasoning_summary_text.done" or "response.reasoning_text.done"
                or "response.function_call_arguments.done" or "response.output_text.annotations.added"
                or "response.output_text.annotations.done":
                break;

            case "response.output_item.done":
                OutputItemDone(data, events);
                break;

            case "response.completed" or "response.incomplete" or "response.failed":
                if (data["response"] is JsonObject response)
                    FinishResponse(response, failed: type == "response.failed", events);
                break;

            case "error":
                _terminal = true;
                events.Add(new ErrorEvent(
                    500,
                    ResponsesCodec.Str(data, "code") ?? ResponsesCodec.Str(data, "type") ?? "server_error",
                    ResponsesCodec.Str(data, "message") ?? "upstream returned an error event"));
                break;
        }
    }

    private void OutputItemAdded(JsonObject data, List<UnifiedStreamEvent> events)
    {
        if (data["item"] is not JsonObject item)
            return;
        switch (ResponsesCodec.Str(item, "type"))
        {
            case "message":
                // Text blocks open later, per content part.
                break;
            case "reasoning":
            {
                var index = OpenItemBlock(data, BlockKind.Reasoning);
                events.Add(new BlockStartEvent(index, BlockKind.Reasoning));
                break;
            }
            case "function_call":
            {
                var index = OpenItemBlock(data, BlockKind.ToolCall);
                var callId = ResponsesCodec.Str(item, "call_id") ?? ResponsesCodec.Str(item, "id") ?? "call_" + Ulid.NewUlid();
                events.Add(new BlockStartEvent(index, BlockKind.ToolCall, callId, ResponsesCodec.Str(item, "name") ?? ""));
                break;
            }
            // web_search_call, file_search_call, … : server-side items are not representable in the IR.
        }
    }

    private void ContentPartAdded(JsonObject data, List<UnifiedStreamEvent> events)
    {
        var partType = data["part"] is JsonObject part ? ResponsesCodec.Str(part, "type") : null;
        if (partType is not ("output_text" or "refusal"))
            return;
        var key = BlockKey(data);
        if (_blocks.ContainsKey(key))
            return;
        var index = _nextBlock++;
        _blocks[key] = index;
        Register(data, index, BlockKind.Text, key);
        events.Add(new BlockStartEvent(index, BlockKind.Text));
    }

    private void OutputItemDone(JsonObject data, List<UnifiedStreamEvent> events)
    {
        var outputIndex = ResponsesCodec.Int(data, "output_index") ?? -1;
        var itemKey = $"o{outputIndex}";
        if (data["item"] is JsonObject item
            && ResponsesCodec.Str(item, "type") == "reasoning"
            && ResponsesCodec.Str(item, "encrypted_content") is { } encrypted
            && _blocks.TryGetValue(itemKey, out var reasoningIndex))
        {
            events.Add(new ReasoningDeltaEvent(reasoningIndex, null, EncryptedContent: encrypted, Origin: ApiProtocol.OpenAIResponses));
        }
        if (_byOutput.Remove(outputIndex, out var opened))
            foreach (var (index, _, key) in opened)
                if (_blocks.Remove(key))
                    events.Add(new BlockStopEvent(index));
    }

    private void FinishResponse(JsonObject response, bool failed, List<UnifiedStreamEvent> events)
    {
        if (_terminal)
            return;
        if (!_started)
        {
            _started = true;
            events.Add(new MessageStartEvent(ResponsesCodec.Str(response, "id"), ResponsesCodec.Str(response, "model") ?? _ctx.Model));
        }
        // Defensive: normally every block was closed by the *_done events already.
        CloseOpenBlocks(events);

        if (response["usage"] is JsonObject usage)
        {
            events.Add(new UsageEvent(
                ApiProtocol.OpenAIResponses,
                usage.DeepClone().AsObject(),
                UsageNormalizer.Normalize(ApiProtocol.OpenAIResponses, usage, ResponsesCodec.Str(response, "service_tier")) ?? new NormalizedUsage()));
        }

        if (failed)
        {
            _stopped = true;
            _terminal = true;
            var error = response["error"] as JsonObject;
            events.Add(new ErrorEvent(
                500,
                ResponsesCodec.Str(error, "code") ?? "server_error",
                ResponsesCodec.Str(error, "message") ?? "upstream response failed"));
            return;
        }

        var status = ResponsesCodec.Str(response, "status") ?? "completed";
        if (status == "incomplete")
        {
            var reason = response["incomplete_details"] is JsonObject details ? ResponsesCodec.Str(details, "reason") : null;
            var finish = reason switch
            {
                "max_output_tokens" => FinishReason.Length,
                "content_filter" => FinishReason.ContentFilter,
                _ => FinishReason.Other,
            };
            _stopped = true;
            events.Add(new MessageStopEvent(finish, reason ?? "incomplete"));
            return;
        }

        var hasToolCall = response["output"] is JsonArray output
            && output.OfType<JsonObject>().Any(o => ResponsesCodec.Str(o, "type") == "function_call");
        _stopped = true;
        events.Add(new MessageStopEvent(hasToolCall ? FinishReason.ToolCalls : FinishReason.Stop, "completed"));
    }

    // ---------------------------------------------------------------- block bookkeeping

    private void CloseOpenBlocks(List<UnifiedStreamEvent> events)
    {
        var open = _byOutput.Values.SelectMany(v => v).OrderBy(e => e.Index);
        foreach (var (index, _, key) in open)
            if (_blocks.Remove(key))
                events.Add(new BlockStopEvent(index));
        _byOutput.Clear();
    }

    private string BlockKey(JsonObject data)
    {
        var outputIndex = ResponsesCodec.Int(data, "output_index") ?? -1;
        var contentIndex = ResponsesCodec.Int(data, "content_index") ?? 0;
        return $"o{outputIndex}:c{contentIndex}";
    }

    private void Register(JsonObject data, int index, BlockKind kind, string key)
    {
        var outputIndex = ResponsesCodec.Int(data, "output_index") ?? -1;
        if (!_byOutput.TryGetValue(outputIndex, out var list))
            _byOutput[outputIndex] = list = [];
        list.Add((index, kind, key));
    }

    private int OpenItemBlock(JsonObject data, BlockKind kind)
    {
        var key = $"o{ResponsesCodec.Int(data, "output_index") ?? -1}";
        if (_blocks.TryGetValue(key, out var existing))
            return existing;
        var index = _nextBlock++;
        _blocks[key] = index;
        Register(data, index, kind, key);
        return index;
    }

    private int EnsureItemBlock(JsonObject data, BlockKind kind, List<UnifiedStreamEvent> events)
    {
        var key = $"o{ResponsesCodec.Int(data, "output_index") ?? -1}";
        if (_blocks.TryGetValue(key, out var existing))
            return existing;
        // Defensive: delta arrived without output_item.added.
        var index = _nextBlock++;
        _blocks[key] = index;
        Register(data, index, kind, key);
        events.Add(new BlockStartEvent(index, kind));
        return index;
    }

    private int EnsureTextBlock(JsonObject data, List<UnifiedStreamEvent> events)
    {
        var key = BlockKey(data);
        if (_blocks.TryGetValue(key, out var existing))
            return existing;
        // Defensive: delta arrived without content_part.added.
        var index = _nextBlock++;
        _blocks[key] = index;
        Register(data, index, BlockKind.Text, key);
        events.Add(new BlockStartEvent(index, BlockKind.Text));
        return index;
    }
}

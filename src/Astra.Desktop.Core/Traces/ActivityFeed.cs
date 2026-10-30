using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core.Requests;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// One request of the topology's activity feed (web <c>ActivityItem</c>): from the stored row (<c>GET /api/traces/activity</c>) or, while it
/// runs, the live feed. A live item has its tool names from its phases and how long it thought / wrote; arguments, the reply and sizes
/// arrive with the stored row.
/// </summary>
public sealed record ActivityItem(
    string Id, bool Live, RequestSummaryDto Request, int? Step, RequestCause? Cause, IReadOnlyList<RequestAttempt> Attempts,
    double? ThinkMs, long? ThinkChars, double? TextMs, long? TextChars, string? Reply, IReadOnlyList<TraceToolCallDto> Tools,
    string? FinishReason, string? ErrorMessage);

/// <summary>A tool call's arguments for display (web <c>toolArguments</c>): an object's fields as key / value pairs, else the raw text.</summary>
public sealed record ToolArguments(IReadOnlyList<(string Key, string Value)>? Pairs, string? Raw);

/// <summary>The activity feed beside the topology (port of <c>activityItems</c> / <c>toolArguments</c> / <c>toolHeadline</c> in <c>traces/model.ts</c>).</summary>
public static class ActivityFeed
{
    /// <summary>Requests the feed asks for and shows (web <c>useTraceActivity(30)</c>).</summary>
    public const int Limit = 30;

    /// <summary>
    /// The feed's items, newest first: the stored requests, with the live feed's version of every request still running (or finished and not
    /// stored yet) in their place.
    /// </summary>
    public static List<ActivityItem> Items(IReadOnlyList<TraceActivityDto> stored, IReadOnlyList<RequestSummaryDto> live, double nowMs, int limit = Limit)
    {
        var byId = new Dictionary<string, ActivityItem>(StringComparer.Ordinal);
        foreach (var a in stored)
            byId[a.Request.Id] = new ActivityItem(a.Request.Id, false, a.Request, a.Step, a.Cause, a.Attempts, a.ReasoningMs, a.ReasoningChars,
                null, a.TextChars, a.Reply, a.ToolCalls, a.FinishReason, a.ErrorMessage);
        foreach (var r in live)
        {
            if (r.Status != "pending" && byId.ContainsKey(r.Id)) continue;
            var phases = TraceAnalysis.LivePhases(r, nowMs);
            double Spent(PhaseKind kind) => phases.Where(s => s.Kind == kind).Sum(s => s.To - s.From);
            var think = Spent(PhaseKind.Think);
            var write = Spent(PhaseKind.Write);
            byId[r.Id] = new ActivityItem(r.Id, true, r, null, r.Cause, r.Attempts ?? [], think > 0 ? think : null, null, write > 0 ? write : null,
                null, null, TopologyBuilder.LiveTools(r.Phases).Select(n => new TraceToolCallDto(n, null, null)).ToList(), null, null);
        }
        return byId.Values
            .OrderByDescending(i => i.Request.StartedAtUtc)
            .ThenByDescending(i => i.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>A tool call's arguments: an object's fields (strings as they are, other values as indented JSON), else the raw text; null when none.</summary>
    public static ToolArguments? Arguments(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            if (JsonNode.Parse(text) is JsonObject o)
                return new ToolArguments(o.Select(kv => (kv.Key, kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : Indented(kv.Value))).ToList(), null);
        }
        catch (JsonException)
        {
            // cut at 4 KB, or not JSON: shown as it is
        }
        return new ToolArguments(null, text);
    }

    private static readonly string[] Preferred = ["file_path", "path", "command", "cmd", "pattern", "query", "url", "description"];

    /// <summary>The first argument worth showing next to a tool's name: a path, a command, a pattern… (<c>toolHeadline</c>).</summary>
    public static string? Headline(string? text)
    {
        var args = Arguments(text);
        if (args is null) return null;
        if (args.Raw is { } raw) return raw.Length > 80 ? raw[..80] : raw;
        var pairs = args.Pairs!;
        if (pairs.Count == 0) return null;
        var pair = pairs.FirstOrDefault(p => Preferred.Contains(p.Key));
        if (pair.Key is null) pair = pairs[0];
        var first = pair.Value.Split('\n')[0];
        return first.Length > 80 ? first[..80] : first;
    }

    /// <summary><c>JSON.stringify(value, null, 2)</c>: two-space indent, non-ASCII kept as it is.</summary>
    private static string Indented(JsonNode? node)
    {
        if (node is null) return "null";
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            node.WriteTo(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

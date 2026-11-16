using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>A failure of the bridge, in the words and status an upstream would have given.</summary>
public sealed class ClaudeBridgeException(int status, string type, string message, DateTimeOffset? resetsAt = null) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>Anthropic's error type (<c>rate_limit_error</c>, <c>authentication_error</c>, <c>api_error</c> …).</summary>
    public string ErrorType { get; } = type;

    /// <summary>When the account's limit renews, if <c>claude</c> said.</summary>
    public DateTimeOffset? ResetsAt { get; } = resetsAt;
}

/// <summary>Where a run is in a conversation.</summary>
internal enum ClaudeRunState
{
    /// <summary>Answering a turn.</summary>
    Busy,

    /// <summary>Waiting for its conversation's next turn.</summary>
    Idle,

    /// <summary>Its reply called the caller's tools; waiting for their results.</summary>
    Parked,

    Ended,
}

/// <summary>
/// One <c>claude</c> process (<c>claude -p --input-format stream-json --output-format stream-json</c>) and what the bridge keeps about it: the
/// conversation it has been told (<see cref="Digests"/>), the caller's tool calls it waits on, and the stream of what it prints.
/// </summary>
internal sealed class ClaudeRun
{
    private readonly Channel<JsonObject> _events = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ClaudeToolResult>> _calls = new(StringComparer.Ordinal);
    private readonly StringBuilder _stderr = new();
    private readonly object _gate = new();

    public required string Id { get; init; }
    public required Process Process { get; init; }

    /// <summary>The secret of the run's callback address.</summary>
    public required string Token { get; init; }

    public required string TempDir { get; init; }

    /// <summary>What a turn must match to go on in this run: the account (directory and proxy), the model, the tools and the instructions.</summary>
    public required string Key { get; init; }

    public required string ToolsKey { get; init; }
    public required string Model { get; init; }
    public required IReadOnlyDictionary<string, string> Declared { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public ClaudeRunState State { get; set; } = ClaudeRunState.Busy;
    public DateTimeOffset Since { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The conversation the run has been told and has answered, message by message.</summary>
    public List<string> Digests { get; set; } = [];

    /// <summary>The tool calls of the last reply, in the caller's hands.</summary>
    public List<string> PendingIds { get; set; } = [];

    /// <summary>The latest <c>rate_limit_info</c> the program printed.</summary>
    public JsonObject? LastRateLimit { get; private set; }

    /// <summary>Set when the run's last turn could not be settled (its result never came): it is not given another turn.</summary>
    public bool Suspect { get; set; }

    public int? ExitCode { get; private set; }
    public bool HasExited => ExitCode is not null;
    public string Stderr { get { lock (_gate) return _stderr.ToString(); } }

    public void NoteLimit(JsonObject info) => LastRateLimit = (JsonObject)info.DeepClone();

    public void AppendStderr(string line)
    {
        lock (_gate)
            if (_stderr.Length < 1 << 20) _stderr.AppendLine(line);
    }

    public void Push(JsonObject ev) => _events.Writer.TryWrite(ev);

    /// <summary>Called once the process has ended and its output has been read.</summary>
    public void Finish(int exitCode)
    {
        ExitCode = exitCode;
        _events.Writer.TryWrite(new JsonObject { ["type"] = "exit", ["code"] = exitCode });
        _events.Writer.TryComplete();
        foreach (var c in _calls.Values) c.TrySetCanceled();
    }

    /// <summary>The next thing the program printed; once it has ended, an <c>exit</c> event again and again.</summary>
    public async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_events.Reader.TryRead(out var ev)) return ev;
            if (!await _events.Reader.WaitToReadAsync(ct))
                return new JsonObject { ["type"] = "exit", ["code"] = ExitCode ?? -1 };
        }
    }

    /// <summary>Throws away what the program printed since the last turn ended (a late result or system line), before the next turn is sent.</summary>
    public void Drain()
    {
        while (_events.Reader.TryRead(out var ev))
            if (ev["type"]?.GetValue<string>() == "rate_limit_event" && ev["rate_limit_info"] is JsonObject info) NoteLimit(info);
    }

    /// <summary>Tells the program a user message (<c>--input-format stream-json</c>: one JSON object a line).</summary>
    public async Task SendUserAsync(JsonArray content, CancellationToken ct)
    {
        var line = new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
        }.ToJsonString();
        await Process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
        await Process.StandardInput.FlushAsync(ct);
    }

    // ------------------------------------------------------------------ the caller's tool calls

    private TaskCompletionSource<ClaudeToolResult> CallOf(string id) =>
        _calls.GetOrAdd(id, _ => new TaskCompletionSource<ClaudeToolResult>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>A tool call of the run's reaches the gateway (through the MCP helper): it waits for the caller's result.</summary>
    public Task<ClaudeToolResult> WaitForResultAsync(string id, CancellationToken ct) => CallOf(id).Task.WaitAsync(ct);

    /// <summary>The caller answered a call (before or after the helper asked: either order is a hand-over).</summary>
    public void Deliver(string id, ClaudeToolResult result) => CallOf(id).TrySetResult(result);

    /// <summary>Calls the run made that the caller has not answered, by id.</summary>
    public IReadOnlyList<string> Unanswered() => _calls.Where(c => !c.Value.Task.IsCompleted).Select(c => c.Key).ToList();
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>A test run's state (web <c>RunStatus</c>).</summary>
public enum RunStatus
{
    /// <summary>Waiting for a batch slot.</summary>
    Queued,

    /// <summary>In flight.</summary>
    Running,

    /// <summary>The upstream answered.</summary>
    Success,

    /// <summary>The upstream or the server refused.</summary>
    Failed,

    /// <summary>Stopped by the person (or the dialog closed).</summary>
    Cancelled,
}

/// <summary>One model's test (web <c>Run</c>); immutable, every streamed event makes a copy.</summary>
public sealed record TestRun(
    string ModelId, RunStatus Status, string Text = "", string Reasoning = "", int? HttpStatus = null, double? HttpMs = null, double? TtftMs = null,
    double? TotalMs = null, string? Raw = null, bool RawTruncated = false, string? ContentType = null,
    IReadOnlyList<KeyValuePair<string, string>>? Headers = null, string? Error = null)
{
    /// <summary>Time from the response headers to the first token (web <c>firstWait</c>); null when either is missing.</summary>
    public double? FirstWait => TtftMs is { } t && HttpMs is { } h ? Math.Max(0, t - h) : null;

    /// <summary>The run is finished (anything but queued / running).</summary>
    public bool IsDone => Status is not (RunStatus.Queued or RunStatus.Running);

    /// <summary>
    /// Folds one event of the test stream into the run (web <c>runTest</c>'s switch): <c>headers</c> sets status, timing and headers,
    /// <c>delta</c> appends text or reasoning, <c>done</c> replaces everything with the final result. Other events are ignored.
    /// </summary>
    public TestRun Apply(SseMessage message)
    {
        JsonObject data;
        try
        {
            data = JsonNode.Parse(message.Data) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            data = new JsonObject();
        }
        switch (message.Event)
        {
            case "headers":
                return this with { HttpStatus = Int(data["httpStatus"]), HttpMs = Num(data["httpMs"]), ContentType = Str(data["contentType"]), Headers = HeadersOf(data["headers"]) };
            case "delta":
                var text = Str(data["text"]) ?? "";
                return Str(data["kind"]) == "reasoning" ? this with { Reasoning = Reasoning + text } : this with { Text = Text + text };
            case "done":
                var ok = data["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
                return this with
                {
                    Status = ok ? RunStatus.Success : RunStatus.Failed, Error = Str(data["error"]), HttpStatus = Int(data["httpStatus"]), HttpMs = Num(data["httpMs"]),
                    TtftMs = Num(data["ttftMs"]), TotalMs = Num(data["totalMs"]), Text = Str(data["text"]) ?? "", Reasoning = Str(data["reasoning"]) ?? "",
                    Raw = Str(data["raw"]) ?? "", RawTruncated = data["rawTruncated"] is JsonValue r && r.TryGetValue<bool>(out var rt) && rt,
                    ContentType = Str(data["contentType"]), Headers = HeadersOf(data["headers"]),
                };
            default:
                return this;
        }
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    private static int? Int(JsonNode? n) => Num(n) is { } d ? (int)d : null;

    /// <summary>web <c>headersOf</c>: string values kept, other non-null values as text, anything else an empty list.</summary>
    public static List<KeyValuePair<string, string>> HeadersOf(JsonNode? node)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (node is not JsonObject o) return list;
        foreach (var (k, v) in o)
        {
            if (v is null) continue;
            list.Add(new(k, v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString()));
        }
        return list;
    }
}

/// <summary>
/// The state machine of <c>ProviderTestDialog</c> (web components/ProviderTestDialog.tsx) without the drawing: the configuration (protocol,
/// stream, max tokens, prompt), the single run and the batch (three at a time, "stop the rest" cancels what still waits), the model lists
/// the picker and the batch list offer. Every test is a real request through <c>POST /api/providers/{id}/test</c> (an event stream).
/// <see cref="Changed"/> fires on the caller's thread for every state change; marshal to the UI thread in the view.
/// </summary>
public sealed class ProviderTestSession
{
    /// <summary>Batch tests run this many at a time.</summary>
    public const int BatchConcurrency = 3;

    /// <summary>Max output tokens when the box is empty.</summary>
    public const int DefaultMaxTokens = 256;

    /// <summary>Upper bound of the max-tokens box.</summary>
    public const int MaxTokensLimit = 4096;

    private readonly Func<JsonObject, CancellationToken, IAsyncEnumerable<SseMessage>> _stream;
    private readonly Action _afterRun;
    private readonly object _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _controllers = [];
    private CancellationTokenSource? _single;
    private bool _stopping;
    private bool _running;
    private int _generation;

    /// <summary>A session over <paramref name="stream"/> (the API's test stream); <paramref name="afterRun"/> runs after each finished test (the request log changed).</summary>
    public ProviderTestSession(ProviderDto provider, Func<JsonObject, CancellationToken, IAsyncEnumerable<SseMessage>> stream, Action? afterRun = null)
    {
        Provider = provider;
        _stream = stream;
        _afterRun = afterRun ?? (() => { });
        Protocol = PreferredProtocol(provider);
    }

    /// <summary>The provider under test.</summary>
    public ProviderDto Provider { get; set; }

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    /// <summary>single | batch.</summary>
    public bool BatchMode { get; private set; }

    /// <summary>The protocol to test with (one of the provider's endpoints).</summary>
    public string Protocol { get; set; }

    /// <summary>Stream the answer (default on).</summary>
    public bool Stream { get; set; } = true;

    /// <summary>The prompt ("" = the server's default "Reply with OK.").</summary>
    public string Prompt { get; set; } = "";

    /// <summary>Max output tokens; null = the default.</summary>
    public int? MaxTokens { get; private set; } = DefaultMaxTokens;

    /// <summary>The single mode's model id (free text).</summary>
    public string Model { get; set; } = "";

    /// <summary>The single run.</summary>
    public TestRun? Single { get; private set; }

    /// <summary>The batch's runs by model id.</summary>
    public IReadOnlyDictionary<string, TestRun> Batch => _batch;

    private Dictionary<string, TestRun> _batch = [];

    /// <summary>The batch's model ids in their order.</summary>
    public IReadOnlyList<string> Order { get; private set; } = [];

    /// <summary>The models ticked in the batch list.</summary>
    public HashSet<string> Selected { get; } = [];

    /// <summary>Model ids typed into the batch search and added.</summary>
    public List<string> Typed { get; } = [];

    /// <summary>The batch search text.</summary>
    public string Search { get; set; } = "";

    /// <summary>The model whose result the batch shows full size; null = the table.</summary>
    public string? Detail { get; set; }

    /// <summary>The provider's models as loaded.</summary>
    public IReadOnlyList<ProviderModelDto> Models { get; private set; } = [];

    /// <summary>The protocols the provider has endpoints for.</summary>
    public List<string> Protocols => Provider.Endpoints.Select(e => e.Protocol.ToId()).ToList();

    /// <summary>The protocol a provider prefers, restricted to the endpoints it has (web <c>preferredProtocol</c>).</summary>
    public static string PreferredProtocol(ProviderDto p)
    {
        var configured = p.Endpoints.Select(e => e.Protocol.ToId()).ToList();
        return p.PreferredUpstreamProtocols.Select(x => x.ToId()).FirstOrDefault(configured.Contains) ?? configured.FirstOrDefault() ?? "openai-chat";
    }

    /// <summary>
    /// Sets the provider's models (on open and when they load): the picker list, and the single mode's model when none is chosen yet (the first
    /// enabled one, else the first).
    /// </summary>
    public void SetModels(IReadOnlyList<ProviderModelDto> models)
    {
        Models = models;
        if (Model.Length == 0) Model = models.FirstOrDefault(m => m.Enabled)?.ModelId ?? models.FirstOrDefault()?.ModelId ?? "";
        Raise();
    }

    /// <summary>Model ids the picker offers: enabled first, then by sort order, then the hand-typed ones (web <c>known</c>).</summary>
    public List<string> Known
    {
        get
        {
            var list = Models.Select((m, i) => (m, i)).OrderByDescending(x => x.m.Enabled).ThenBy(x => x.m.SortOrder).ThenBy(x => x.i).Select(x => x.m.ModelId).ToList();
            list.AddRange(Typed.Where(t => !list.Contains(t)));
            return list;
        }
    }

    /// <summary>The batch list rows for the search text (case-insensitive contains).</summary>
    public List<string> Visible
    {
        get
        {
            var needle = Search.Trim().ToLowerInvariant();
            var known = Known;
            return needle.Length == 0 ? known : known.Where(id => id.ToLowerInvariant().Contains(needle, StringComparison.Ordinal)).ToList();
        }
    }

    /// <summary>The search text is a model id not in the list: offer to add it.</summary>
    public bool CanAdd
    {
        get
        {
            var q = Search.Trim();
            return q.Length > 0 && !Known.Any(id => string.Equals(id, q, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The select-all box: true all, false none, null some.</summary>
    public bool? AllSelected
    {
        get
        {
            var known = Known;
            if (Selected.Count == known.Count && known.Count > 0) return true;
            return Selected.Count == 0 ? false : null;
        }
    }

    /// <summary>Sets the max-tokens box (web: clamped to 1..4096, rounded; null stays null).</summary>
    public void SetMaxTokens(decimal? value)
    {
        MaxTokens = value is { } v ? (int)Math.Min(MaxTokensLimit, Math.Max(1, Math.Round(v, MidpointRounding.ToEven))) : null;
        Raise();
    }

    /// <summary>Switches single / batch (the batch detail closes).</summary>
    public void SetMode(bool batch)
    {
        BatchMode = batch;
        Detail = null;
        Raise();
    }

    /// <summary>Ticks or unticks a model.</summary>
    public void Toggle(string id, bool on)
    {
        if (on) Selected.Add(id);
        else Selected.Remove(id);
        Raise();
    }

    /// <summary>Selects every known model, or none.</summary>
    public void SelectAll(bool on)
    {
        Selected.Clear();
        if (on) foreach (var id in Known) Selected.Add(id);
        Raise();
    }

    /// <summary>Adds the search text as a model and ticks it.</summary>
    public void AddTyped(string id)
    {
        if (!Typed.Contains(id)) Typed.Add(id);
        Selected.Add(id);
        Search = "";
        Raise();
    }

    /// <summary>A run is in flight or queued (web <c>busy</c>): Close is disabled then.</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
                return Single?.Status == RunStatus.Running
                       || Order.Any(id => _batch.TryGetValue(id, out var r) && (r.Status == RunStatus.Running || r.Status == RunStatus.Queued && !_stopping));
        }
    }

    /// <summary>Finished batch runs.</summary>
    public int Done { get { lock (_gate) return Order.Count(id => _batch.TryGetValue(id, out var r) && r.IsDone); } }

    /// <summary>Successful batch runs.</summary>
    public int Succeeded { get { lock (_gate) return Order.Count(id => _batch.TryGetValue(id, out var r) && r.Status == RunStatus.Success); } }

    /// <summary>Failed batch runs.</summary>
    public int Failed { get { lock (_gate) return Order.Count(id => _batch.TryGetValue(id, out var r) && r.Status == RunStatus.Failed); } }

    /// <summary>The request body of one test (web <c>request</c>).</summary>
    public JsonObject RequestFor(string modelId) => new()
    {
        ["modelId"] = modelId,
        ["protocol"] = Protocol,
        ["stream"] = Stream,
        ["prompt"] = Prompt.Trim() is { Length: > 0 } p ? p : null,
        ["maxOutputTokens"] = MaxTokens ?? DefaultMaxTokens,
    };

    /// <summary>Starts (or restarts) the single test of <see cref="Model"/>. The returned task ends with the run.</summary>
    public Task StartSingleAsync()
    {
        var modelId = Model.Trim();
        if (modelId.Length == 0) return Task.CompletedTask;
        CancellationTokenSource cts;
        int gen;
        lock (_gate)
        {
            _single?.Cancel();
            cts = _single = new CancellationTokenSource();
            _running = true;
            gen = _generation;
        }
        Detail = null;
        Single = new TestRun(modelId, RunStatus.Running);
        Raise();
        return RunAsync(modelId, cts.Token, run =>
        {
            lock (_gate) if (gen != _generation) return;
            if (run.IsDone) _afterRun();
            Single = run;
            Raise();
        });
    }

    /// <summary>Stops the single run.</summary>
    public void StopSingle()
    {
        lock (_gate) _single?.Cancel();
    }

    /// <summary>Starts a batch over <paramref name="ids"/>, <see cref="BatchConcurrency"/> at a time. The task ends when every worker is done.</summary>
    public Task StartBatchAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return Task.CompletedTask;
        int gen;
        lock (_gate)
        {
            _running = true;
            _stopping = false;
            gen = _generation;
            Order = ids.ToList();
            _batch = ids.ToDictionary(id => id, id => new TestRun(id, RunStatus.Queued));
        }
        Detail = null;
        Raise();
        var next = 0;
        async Task Worker()
        {
            while (true)
            {
                string modelId;
                CancellationTokenSource cts;
                lock (_gate)
                {
                    if (!_running || gen != _generation || next >= ids.Count) return;
                    modelId = ids[next++];
                    if (_stopping)
                    {
                        _batch[modelId] = _batch[modelId] with { Status = RunStatus.Cancelled };
                        cts = null!;
                    }
                    else
                    {
                        cts = new CancellationTokenSource();
                        _controllers[modelId] = cts;
                    }
                }
                if (cts is null)
                {
                    Raise();
                    continue;
                }
                await RunAsync(modelId, cts.Token, run =>
                {
                    lock (_gate)
                    {
                        if (gen != _generation) return;
                        _batch[modelId] = run;
                    }
                    Raise();
                });
                lock (_gate) _controllers.Remove(modelId);
                _afterRun();
            }
        }
        return Task.WhenAll(Enumerable.Range(0, Math.Min(BatchConcurrency, ids.Count)).Select(_ => Task.Run(Worker)));
    }

    /// <summary>"Stop the rest": what still waits is cancelled, the runs in flight finish (web <c>stopBatch</c>).</summary>
    public void StopBatch()
    {
        lock (_gate)
        {
            _stopping = true;
            foreach (var id in _batch.Keys.ToList())
                if (_batch[id].Status == RunStatus.Queued) _batch[id] = _batch[id] with { Status = RunStatus.Cancelled };
        }
        Raise();
    }

    /// <summary>
    /// The dialog closed (web: the <c>open</c> effect's cleanup): everything in flight is cancelled (the server logs those runs as client-cancelled)
    /// and the results, selection, typed models, search and detail are cleared. The configuration stays.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _running = false;
            _single?.Cancel();
            foreach (var c in _controllers.Values) c.Cancel();
            _controllers.Clear();
            Single = null;
            _batch = [];
            Order = [];
        }
        Selected.Clear();
        Typed.Clear();
        Search = "";
        Detail = null;
        Raise();
    }

    /// <summary>Runs one test and reports every step (web <c>runTest</c>).</summary>
    private async Task RunAsync(string modelId, CancellationToken ct, Action<TestRun> report)
    {
        var state = new TestRun(modelId, RunStatus.Running);
        report(state);
        try
        {
            await foreach (var message in _stream(RequestFor(modelId), ct).WithCancellation(ct).ConfigureAwait(false))
            {
                var next = state.Apply(message);
                if (!ReferenceEquals(next, state))
                {
                    state = next;
                    report(state);
                }
            }
            // A stream that ends without "done" (should not happen) still leaves the run finished.
            if (!state.IsDone) report(state = state with { Status = RunStatus.Failed, Error = state.Error ?? "Connection test ended without a result" });
        }
        catch (OperationCanceledException)
        {
            report(state with { Status = RunStatus.Cancelled, Error = null });
        }
        catch (Exception e)
        {
            report(state with { Status = RunStatus.Failed, Error = e.Message });
        }
    }

    private void Raise() => Changed?.Invoke();

    /// <summary>The status line's i18n key (web <c>STATUS_KEY</c>); null run = idle.</summary>
    public static string StatusKey(TestRun? run) => "providers.testDialog.status." + (run?.Status switch
    {
        null => "idle",
        RunStatus.Queued => "queued",
        RunStatus.Running => "running",
        RunStatus.Success => "success",
        RunStatus.Failed => "failed",
        _ => "cancelled",
    });

    /// <summary>web <c>formatMs</c>: "123 ms", "12.3 s" from ten seconds, "—" for none.</summary>
    public static string FormatMs(double? ms)
    {
        if (ms is not { } v) return "—";
        if (v >= 10_000) return (v / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s";
        return Math.Round(v, MidpointRounding.AwayFromZero).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
    }
}

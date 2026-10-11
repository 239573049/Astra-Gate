using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Astra.Plugins.Host;

/// <summary>An error the plugin host (or a plugin) answered, or the host being gone.</summary>
public sealed class PluginHostException(string message) : Exception(message);

/// <summary>
/// A request a provider's plugin makes for the gateway (magpie's plugin.FetchRequest): sent through the
/// plugin's loader fetch, its reply streamed back.
/// </summary>
public sealed record HostFetchRequest(
    string Provider, string? Account, string Model, string? Npm, string Url, string Method,
    Dictionary<string, string> Headers, byte[]? Body, string? Session, string? Proxy);

/// <summary>
/// The connection to one running plugin host (magpie's host.js under Bun): newline-delimited JSON over
/// stdin/stdout, ported from magpie's internal/plugin/host.go. RPC calls are <c>{id, method, params}</c> answered
/// by <c>{id, result}</c> or <c>{id, error}</c>; a fetch streams <c>{id, event:"head"}</c> then <c>{id, event:"chunk",
/// data}</c> frames within the credit window the host gives back as its reader takes bytes, so a slow reader
/// pauses the upstream instead of anything buffering without bound.
/// </summary>
public sealed class PluginHostClient : IAsyncDisposable
{
    // magpie's limits (host.go): a call's credit window, every live window together, a frame's most bytes and
    // what each frame costs besides its bytes, the RPC reserve, the longest line read, the head's envelope.
    internal const int StreamWindow = 512 << 10;
    private const int StreamBudget = 64 << 20;
    internal const int MaxFrame = 64 << 10;
    internal const int FrameOverhead = 2048;
    private const int RpcReserve = 64;
    private const int MaxWireLine = 32 << 20;
    private const int MaxHeadBytes = 64 << 10;
    private const int MaxHeadEntries = 256;
    private const int HeadEntryCost = 4;

    internal const string ErrTooManyCalls = "too many plugin calls are in flight; try again shortly";
    internal const string ErrProtocolOverrun = "the plugin sent more of a reply than the host credited; that request was given up on";
    internal const string ErrHostGone = "the plugin host is gone";

    private readonly Stream _stdin;
    private readonly Process? _process;
    private readonly Action<JsonObject> _onEvent;
    private readonly ConcurrentDictionary<long, HostCall> _calls = new();
    private readonly Channel<string> _out = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _streams = new(StreamBudget / StreamWindow);
    private readonly SemaphoreSlim _slots = new(StreamBudget / StreamWindow + RpcReserve);
    private readonly TaskCompletionSource _dead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextId;
    private string? _deadReason;

    /// <param name="stdin">Where requests are written (the host's stdin).</param>
    /// <param name="stdout">Where the host's lines are read from.</param>
    /// <param name="onEvent">Told every message without an id: log, toast, auth, signIn, renewing, recovered.</param>
    /// <param name="process">The host process, killed on dispose (null for a test's in-memory host).</param>
    internal PluginHostClient(Stream stdin, Stream stdout, Action<JsonObject> onEvent, Process? process = null)
    {
        _stdin = stdin;
        _process = process;
        _onEvent = onEvent;
        _ = Task.Run(() => ReadLoopAsync(stdout));
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>Starts <c>bun run host.js</c> and connects to it.</summary>
    public static PluginHostClient Start(string bun, string hostJs, string workingDirectory,
        IReadOnlyDictionary<string, string> environment, Action<JsonObject> onEvent, Action<string> stderr)
    {
        var info = new ProcessStartInfo(bun)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add(hostJs);
        info.Environment.Clear();
        foreach (var (k, v) in environment) info.Environment[k] = v;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr(e.Data);
        };
        process.Start();
        process.BeginErrorReadLine();
        var client = new PluginHostClient(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, onEvent, process);
        process.Exited += (_, _) => client.Fail($"{ErrHostGone} (it quit with {SafeExitCode(process)})");
        return client;
    }

    /// <summary>Whether the host is still up.</summary>
    public bool Alive => !_dead.Task.IsCompleted;

    /// <summary>Completes when the host is gone.</summary>
    public Task Gone => _dead.Task;

    /// <summary>Why the host is gone; null while it runs.</summary>
    public string? GoneReason => _deadReason;

    /// <summary>The calls (RPCs and fetches) in flight: a retired host is stopped once there are none.</summary>
    public int InFlight => _calls.Count;

    /// <summary>Asks the host <paramref name="method"/> and returns its result (null for none).</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        if (!_slots.Wait(0)) throw new PluginHostException(ErrTooManyCalls);
        var call = Register(stream: false);
        try
        {
            Send(new JsonObject { ["id"] = call.Id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
            using var reg = ct.Register(() => call.Answer.TrySetCanceled(ct));
            var answer = await call.Answer.Task.WaitAsync(ct);
            if (answer["error"] is JsonObject error) throw new PluginHostException(error["message"]?.GetValue<string>() ?? "the plugin host failed");
            return answer["result"]?.DeepClone();
        }
        finally
        {
            _calls.TryRemove(call.Id, out _);
            _slots.Release();
        }
    }

    /// <summary>
    /// Sends a request through the provider's plugin and returns its reply once the head came; the body streams
    /// as it is read. Cancelling, or disposing the body before its end, stops the plugin's fetch.
    /// </summary>
    public async Task<HttpResponseMessage> FetchAsync(HostFetchRequest r, CancellationToken ct = default)
    {
        if (!_streams.Wait(0)) throw new PluginHostException(ErrTooManyCalls);
        if (!_slots.Wait(0))
        {
            _streams.Release();
            throw new PluginHostException(ErrTooManyCalls);
        }
        var call = Register(stream: true);
        var handedOver = false;
        try
        {
            var headers = new JsonObject();
            foreach (var (k, v) in r.Headers) headers[k] = v;
            var p = new JsonObject
            {
                ["provider"] = r.Provider,
                ["model"] = r.Model,
                ["npm"] = r.Npm ?? "",
                ["url"] = r.Url,
                ["method"] = r.Method,
                ["headers"] = headers,
                ["window"] = StreamWindow,
            };
            if (!string.IsNullOrEmpty(r.Account)) p["account"] = r.Account;
            if (r.Body is { Length: > 0 } body) p["body"] = Convert.ToBase64String(body);
            if (!string.IsNullOrEmpty(r.Session)) p["session"] = r.Session;
            if (!string.IsNullOrEmpty(r.Proxy)) p["proxy"] = r.Proxy;
            Send(new JsonObject { ["id"] = call.Id, ["method"] = "fetch", ["params"] = p });

            JsonObject head;
            using (ct.Register(() => call.Fail(new OperationCanceledException(ct))))
                head = await call.Head.Task.WaitAsync(ct);

            var status = head["status"]?.GetValue<int>() ?? 502;
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StreamContent(new HostBodyStream(this, call, ct)),
            };
            if (head["headers"] is JsonObject hs)
            {
                foreach (var (name, value) in hs)
                {
                    // the body is decoded already: fetch took the encoding off
                    if (name.Equals("content-encoding", StringComparison.OrdinalIgnoreCase) || name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var v = value?.GetValue<string>() ?? "";
                    if (!response.Headers.TryAddWithoutValidation(name, v)) response.Content.Headers.TryAddWithoutValidation(name, v);
                }
            }
            handedOver = true;
            return response;
        }
        catch (OperationCanceledException)
        {
            Abort(call);
            throw;
        }
        finally
        {
            if (!handedOver) Release(call);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Fail(ErrHostGone);
        _out.Writer.TryComplete();
        try
        {
            _stdin.Close();
        }
        catch (IOException)
        {
        }
        if (_process is { } p)
        {
            try
            {
                if (!p.HasExited)
                {
                    // the host exits once stdin closes and nothing is pending; give it a moment, then stop it
                    using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try
                    {
                        await p.WaitForExitAsync(wait.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        p.Kill(entireProcessTree: true);
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
            p.Dispose();
        }
    }

    private HostCall Register(bool stream)
    {
        if (!Alive) throw new PluginHostException(_deadReason ?? ErrHostGone);
        var call = new HostCall(Interlocked.Increment(ref _nextId), stream);
        _calls[call.Id] = call;
        if (!Alive)
        {
            call.Fail(new PluginHostException(_deadReason ?? ErrHostGone));
        }
        return call;
    }

    internal void Credit(HostCall call, int n)
    {
        if (n <= 0 || call.Finished) return;
        var owed = Interlocked.Add(ref call.Owed, -n);
        if (owed < 0) Interlocked.Exchange(ref call.Owed, 0);
        Send(new JsonObject { ["method"] = "credit", ["params"] = new JsonObject { ["id"] = call.Id, ["n"] = n } });
    }

    /// <summary>Stops the plugin's fetch, once, unless the host already ended it: nothing more is sent for it.</summary>
    internal void Abort(HostCall call)
    {
        call.Fail(new PluginHostException("the request was given up on"));
        if (Volatile.Read(ref call.HostEnded) == 1 || Interlocked.Exchange(ref call.AbortSent, 1) == 1) return;
        if (Alive) _out.Writer.TryWrite(new JsonObject { ["method"] = "abort", ["params"] = new JsonObject { ["id"] = call.Id } }.ToJsonString());
    }

    /// <summary>Takes the call off the host's books once (its stream slot back too).</summary>
    internal void Release(HostCall call)
    {
        if (Interlocked.Exchange(ref call.Released, 1) == 1) return;
        _calls.TryRemove(call.Id, out _);
        _slots.Release();
        if (call.Stream) _streams.Release();
    }

    private void Send(JsonObject message)
    {
        if (!_out.Writer.TryWrite(message.ToJsonString()))
            throw new PluginHostException(_deadReason ?? ErrHostGone);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var line in _out.Reader.ReadAllAsync())
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _stdin.WriteAsync(bytes);
                await _stdin.FlushAsync();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Fail($"{ErrHostGone}: {e.Message}");
        }
    }

    private async Task ReadLoopAsync(Stream stdout)
    {
        var buffer = new byte[1 << 20];
        var line = new MemoryStream();
        try
        {
            while (true)
            {
                var n = await stdout.ReadAsync(buffer);
                if (n == 0) break;
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    Dispatch(line.GetBuffer().AsSpan(0, (int)line.Length));
                    line.SetLength(0);
                    start = i + 1;
                }
                line.Write(buffer, start, n - start);
                if (line.Length > MaxWireLine)
                {
                    // a child that never ends a line: stop it rather than allocate
                    Fail($"the plugin host sent a line longer than {MaxWireLine} bytes");
                    try
                    {
                        _process?.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        Fail(ErrHostGone + (_process is { HasExited: true } p ? $" (it quit with {SafeExitCode(p)})" : ""));
    }

    private void Dispatch(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty) return;
        JsonObject? m;
        try
        {
            m = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (m is null) return;
        var id = m["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var x) ? x : 0;
        if (id == 0)
        {
            try
            {
                _onEvent(m);
            }
            catch (Exception)
            {
                // an event handler's failure never stops the reader
            }
            return;
        }
        if (!_calls.TryGetValue(id, out var call)) return;
        if (m["event"] is JsonValue ev && ev.TryGetValue<string>(out var name))
        {
            if (!call.Stream) return;
            if (!Push(call, name, m)) Abort(call);
            return;
        }
        call.Answered(m);
    }

    // Queues a frame of a fetch's reply; false when the child broke the protocol (a second head, a frame too
    // large or past the window), which gives the call up.
    private static bool Push(HostCall call, string name, JsonObject m)
    {
        if (call.Finished) return true;
        switch (name)
        {
            case "head":
                if (call.Headed || m["headers"] is JsonObject hs && (hs.Count > MaxHeadEntries || HeadCost(hs) > MaxHeadBytes))
                {
                    call.Fail(new PluginHostException(ErrProtocolOverrun));
                    return false;
                }
                call.Headed = true;
                call.Head.TrySetResult(m);
                return true;
            case "chunk":
                byte[] raw;
                try
                {
                    raw = Convert.FromBase64String(m["data"]?.GetValue<string>() ?? "");
                }
                catch (FormatException)
                {
                    raw = [];
                }
                var cost = raw.Length + FrameOverhead;
                if (raw.Length == 0 || raw.Length > MaxFrame || Interlocked.Add(ref call.Owed, cost) > StreamWindow)
                {
                    call.Fail(new PluginHostException(ErrProtocolOverrun));
                    return false;
                }
                call.Chunks.Writer.TryWrite(raw);
                return true;
            default:
                call.Fail(new PluginHostException(ErrProtocolOverrun));
                return false;
        }
    }

    private static int HeadCost(JsonObject headers)
    {
        var cost = 0;
        foreach (var (k, v) in headers)
            cost += Encoding.UTF8.GetByteCount(k) + Encoding.UTF8.GetByteCount(v?.ToString() ?? "") + HeadEntryCost;
        return cost;
    }

    private void Fail(string reason)
    {
        _deadReason ??= reason;
        _dead.TrySetResult();
        _out.Writer.TryComplete();
        foreach (var call in _calls.Values) call.Fail(new PluginHostException(_deadReason));
    }

    private static string SafeExitCode(Process p)
    {
        try
        {
            return "exit code " + p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return "no exit code";
        }
    }
}

/// <summary>One call to the host: an RPC's answer, or a fetch's head, chunks and end.</summary>
internal sealed class HostCall(long id, bool stream)
{
    public long Id { get; } = id;
    public bool Stream { get; } = stream;
    public TaskCompletionSource<JsonObject> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<JsonObject> Head { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Channel<byte[]> Chunks { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    public bool Headed;
    public long Owed;
    public int Released;
    public int AbortSent;

    /// <summary>The host answered this call (its end or its error): there is nothing left to abort.</summary>
    public int HostEnded;
    private int _finished;

    public bool Finished => Volatile.Read(ref _finished) == 1;

    /// <summary>The host answered: an RPC's result, or the end of a fetch (an error, or its body is complete).</summary>
    public void Answered(JsonObject m)
    {
        Volatile.Write(ref HostEnded, 1);
        if (!Stream)
        {
            Answer.TrySetResult(m);
            return;
        }
        var error = m["error"] is JsonObject e ? new PluginHostException(e["message"]?.GetValue<string>() ?? "the plugin failed") : null;
        if (error is not null)
        {
            Fail(error);
            return;
        }
        Head.TrySetException(new PluginHostException("the plugin answered no response"));
        if (Interlocked.Exchange(ref _finished, 1) == 0) Chunks.Writer.TryComplete();
    }

    public void Fail(Exception error)
    {
        Answer.TrySetException(error);
        Head.TrySetException(error);
        if (Interlocked.Exchange(ref _finished, 1) == 0) Chunks.Writer.TryComplete(error);
    }
}

/// <summary>
/// A fetch's reply body: reads the frames the call queued, in order, giving the host back credit for each
/// byte taken (and a frame's overhead once it is done). Disposing it before the end stops the plugin's fetch.
/// </summary>
internal sealed class HostBodyStream(PluginHostClient host, HostCall call, CancellationToken requestAborted) : Stream
{
    private byte[] _frame = [];
    private int _offset;
    private bool _ended;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0 || _ended) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, requestAborted);
        while (true)
        {
            if (_offset < _frame.Length)
            {
                var n = Math.Min(buffer.Length, _frame.Length - _offset);
                _frame.AsSpan(_offset, n).CopyTo(buffer.Span);
                _offset += n;
                host.Credit(call, n);
                if (_offset == _frame.Length) host.Credit(call, PluginHostClient.FrameOverhead);
                return n;
            }
            try
            {
                if (!await call.Chunks.Reader.WaitToReadAsync(linked.Token))
                {
                    _ended = true;
                    host.Release(call);
                    return 0;
                }
            }
            catch (PluginHostException e)
            {
                _ended = true;
                host.Release(call);
                throw new IOException(e.Message, e);
            }
            catch (OperationCanceledException)
            {
                host.Abort(call);
                host.Release(call);
                _ended = true;
                throw;
            }
            if (call.Chunks.Reader.TryRead(out var frame))
            {
                _frame = frame;
                _offset = 0;
            }
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_ended)
        {
            _ended = true;
            host.Abort(call);
            host.Release(call);
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

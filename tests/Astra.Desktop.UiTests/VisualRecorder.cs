using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Shell;

namespace Astra.Desktop.UiTests;

/// <summary>
/// Records, per scene, every admin-API exchange the scene's <c>ApiClient</c> made, so scripts/visual/capture-web.mjs can replay
/// EXACTLY the same answers to the web UI (plan avalonia-client §23). Off unless <c>ASTRA_VISUAL_RECORD=1</c>; output
/// <c>ASTRA_VISUAL_RECORD_OUT</c>, else <c>&lt;repo&gt;/artifacts/visual/fixtures/&lt;scene&gt;.json</c>. Test-only: the scene
/// factories build their client through <see cref="Scenes.FixtureApi"/>, which wraps the OUTERMOST handler (so a scene's own
/// interceptors, hangs and streams are recorded as the page saw them), and <see cref="FixtureSet.Handler"/> registers its set so
/// the scene's per-path overrides are written too. A request still unanswered when the frame is taken is recorded as a hang, a
/// <c>text/event-stream</c> answer as its frames plus whether it was still open.
/// </summary>
public static class VisualRecorder
{
    /// <summary>True when <c>ASTRA_VISUAL_RECORD=1</c>.</summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable("ASTRA_VISUAL_RECORD") == "1";

    private static Recording? _current;

    /// <summary>Starts recording <paramref name="scene"/> (call before the scene factory builds its client).</summary>
    public static void Begin(string scene)
    {
        _current = Enabled ? new Recording(scene) : null;
    }

    /// <summary>Stops recording (the scene is torn down; what it cancels from here on is not part of what it showed).</summary>
    public static void Stop() => _current = null;

    /// <summary>The recording the next wrapped handler belongs to (null = not recording).</summary>
    internal static Recording? Current => _current;

    /// <summary>Wraps <paramref name="inner"/> so its exchanges are recorded into the current scene (a pass-through when off).</summary>
    public static HttpMessageHandler Wrap(HttpMessageHandler inner)
        => _current is { } rec ? new RecordingHandler(inner, rec) : inner;

    /// <summary>Remembers a fixture set the scene built (its overrides go into the recording).</summary>
    internal static void Track(FixtureSet set)
    {
        if (_current is { } rec) lock (rec.Sets) rec.Sets.Add(set);
    }

    /// <summary>Writes the recording of the current scene; <paramref name="content"/> is the window's content (its shell, if any).</summary>
    public static void End(string scene, object? content, string outRoot)
    {
        var rec = _current;
        _current = null;
        if (rec is null || rec.Scene != scene) return;
        var root = new JsonObject
        {
            ["scene"] = scene,
            ["recordedAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        if (content is Avalonia.StyledElement { DataContext: ShellViewModel shell })
        {
            var page = shell.Navigator.Current;
            root["shell"] = true;
            root["route"] = page.Route.ToString();
            root["param"] = page.Param;
            root["webPath"] = RouteInfo.Of(page.Route).WebPath;
            root["canGoBack"] = shell.CanGoBack;
            root["sidebarCollapsed"] = shell.IsSidebarCollapsed;
            root["title"] = page.Title;
        }
        else
        {
            root["shell"] = false;
            root["view"] = content?.GetType().Name;
        }
        FixtureSet[] sets;
        lock (rec.Sets) sets = [.. rec.Sets];
        if (sets.Length > 0)
        {
            root["now"] = sets[0].Now.ToString("O");
            var overrides = new JsonObject();
            foreach (var set in sets)
                foreach (var (path, (status, body)) in set.Overrides)
                    overrides[path] = new JsonObject { ["status"] = status, ["body"] = body };
            root["overrides"] = overrides;
        }
        var calls = new JsonArray();
        lock (rec.Calls)
            foreach (var c in rec.Calls)
                calls.Add(c.ToJson());
        root["calls"] = calls;
        var dir = Environment.GetEnvironmentVariable("ASTRA_VISUAL_RECORD_OUT") ?? Path.Combine(outRoot, "artifacts", "visual", "fixtures");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, scene + ".json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    internal sealed class Recording(string scene)
    {
        public string Scene { get; } = scene;
        public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();
        public List<Call> Calls { get; } = [];
        public List<FixtureSet> Sets { get; } = [];
    }

    internal sealed class Call
    {
        public required string Method { get; init; }
        public required string Url { get; init; }
        public required long StartMs { get; init; }
        public string? RequestBody { get; set; }
        public int? Status { get; set; }
        public string? ContentType { get; set; }
        public string? Body { get; set; }
        public bool Stream { get; set; }
        public bool Open { get; set; }
        public StringBuilder Frames { get; } = new();
        public long? EndMs { get; set; }
        public string? Error { get; set; }
        public bool Cancelled { get; set; }

        public JsonObject ToJson()
        {
            var o = new JsonObject { ["method"] = Method, ["url"] = Url, ["t"] = StartMs };
            if (RequestBody is not null) o["requestBody"] = RequestBody;
            if (Error is not null) o["error"] = Error;
            if (Cancelled) o["cancelled"] = true;
            if (Status is null && Error is null && !Cancelled)
            {
                o["hang"] = true;
                return o;
            }
            o["status"] = Status;
            if (ContentType is not null) o["contentType"] = ContentType;
            if (Stream)
            {
                o["stream"] = true;
                lock (Frames) o["frames"] = Frames.ToString();
                o["open"] = Open;
            }
            else
            {
                o["body"] = Body;
            }
            if (EndMs is not null) o["ms"] = EndMs - StartMs;
            return o;
        }
    }

    private sealed class RecordingHandler(HttpMessageHandler inner, Recording rec) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var call = new Call { Method = request.Method.Method, Url = request.RequestUri!.PathAndQuery, StartMs = rec.Clock.ElapsedMilliseconds };
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync(cancellationToken);
                call.RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            lock (rec.Calls) rec.Calls.Add(call);
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancelled while the scene ran (a superseded query): not a hang. One the teardown cancels is written as a hang
                // already, because the recording was saved before the teardown.
                call.Cancelled = true;
                throw;
            }
            catch (Exception e)
            {
                call.Error = e.GetType().Name + ": " + e.Message;
                throw;
            }
            call.Status = (int)response.StatusCode;
            var media = response.Content?.Headers.ContentType?.MediaType;
            call.ContentType = media;
            if (response.Content is null) return response;
            if (media == "text/event-stream")
            {
                call.Stream = true;
                call.Open = true;
                var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                var tee = new StreamContent(new TeeStream(source, call));
                foreach (var h in response.Content.Headers) tee.Headers.TryAddWithoutValidation(h.Key, h.Value);
                response.Content = tee;
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                call.Body = body;
                call.EndMs = rec.Clock.ElapsedMilliseconds;
                var replaced = new StringContent(body, Encoding.UTF8);
                replaced.Headers.ContentType = media is null ? null : new MediaTypeHeaderValue(media) { CharSet = "utf-8" };
                response.Content = replaced;
            }
            return response;
        }
    }

    /// <summary>Passes a stream through and keeps a copy of what was read (the SSE frames the page consumed).</summary>
    private sealed class TeeStream(Stream inner, Call call) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Note(buffer.AsSpan(offset, n));
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            Note(buffer.Span[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Note(ReadOnlySpan<byte> bytes)
        {
            lock (call.Frames)
            {
                if (bytes.Length == 0) call.Open = false;
                else call.Frames.Append(Encoding.UTF8.GetString(bytes));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

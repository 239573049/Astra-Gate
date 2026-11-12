using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2.Ui;

/// <summary>
/// The view sessions the shells open (design/plugin-v2 03 §3.2). A session is one open view: the host keeps its state (a
/// JSON snapshot — the engine keeps nothing), its last tree and revision, its data channels and the questions it is
/// waiting on. Events from the shell run the view's actions one at a time per session; every change reaches the shell
/// as a message on the session's stream: <c>patch</c>, <c>reset</c>, <c>effect</c>, <c>ask</c>, <c>data</c>, <c>error</c>.
/// </summary>
public sealed class UiSessions
{
    /// <summary>A session with no stream and no event for this long is closed.</summary>
    public static readonly TimeSpan Abandoned = TimeSpan.FromMinutes(2);

    /// <summary>The most a view's state may serialise to.</summary>
    public const int MaxStateBytes = 256 * 1024;

    /// <summary>The most rows a data channel keeps for a reset.</summary>
    public const int MaxCollectionItems = 10_000;

    /// <summary>The most bytes a file handed to a plugin may have.</summary>
    public const int MaxFileBytes = 25 * 1024 * 1024;

    private readonly PluginV2Host _host;
    private readonly ConcurrentDictionary<string, UiSession> _sessions = new();
    private readonly ConcurrentDictionary<string, UiFile> _files = new();
    private readonly Timer _reaper;

    internal UiSessions(PluginV2Host host)
    {
        _host = host;
        _reaper = new Timer(_ => _ = ReapAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public UiSchema Schema { get; } = UiSchema.Default;

    /// <summary>Opens a view and renders it; the result carries the first tree.</summary>
    public async Task<UiOpenResult> OpenAsync(UiOpenRequest request, CancellationToken ct = default)
    {
        var plugin = await _host.FindAsync(request.PluginId, ct) ?? throw new PluginCallException("not_found", $"no running plugin {request.PluginId}");
        if (!PluginPermissions.Holds(plugin.Granted, PluginPermissions.Ui))
            throw new PluginCallException("permission_denied", $"{plugin.Id} has no ui permission");
        if (request.Slot is { } slot && plugin.Manifest.Slots.All(s => s.Slot != slot || s.View != request.View))
            throw new PluginCallException("not_found", $"{plugin.Id} has no view {request.View} in the {slot} slot");
        if (plugin.Manifest.EnginesUi is { } needs && SemVer.TryParse(request.Client["ui"]?.GetValue<string>() ?? PluginV2Versions.Ui.ToString(), out var shellUi)
            && !SemVer.Satisfies(shellUi, needs))
            throw new PluginCallException("not_supported", $"this view needs UI {needs}; update this Astra app");
        var session = new UiSession(Guid.NewGuid().ToString("n"), plugin.Id, request.View, request.Params ?? [], request.Client,
            request.Query ?? [], request.Slot, request.ParentId);
        _sessions[session.Id] = session;
        try
        {
            plugin.Reason = "view";
            var init = await plugin.Main.RunAsync(Op(session, "view.init"), client: session.Client, ct: ct);
            session.State = init?["state"]?.DeepClone() ?? new JsonObject();
            CheckState(session.State);
            var (tree, actions) = await RenderAsync(plugin, session, session.State, ct);
            lock (session.Sync)
            {
                session.Tree = tree;
                session.Actions = actions;
                session.Rev = 1;
            }
            return new UiOpenResult(session.Id, session.Rev, tree, [.. session.Warnings]);
        }
        catch
        {
            _sessions.TryRemove(session.Id, out _);
            throw;
        }
    }

    /// <summary>
    /// The shell's stream for a session. A shell that reconnects passes the revision it has: when it is behind, the
    /// first message is a reset (the whole tree and the data channels). Questions still open are sent again.
    /// </summary>
    public ChannelReader<JsonObject> Subscribe(string sessionId, long haveRev)
    {
        var s = Get(sessionId);
        var channel = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (s.Sync)
        {
            s.Out?.Writer.TryComplete();
            s.Out = channel;
            s.Touch();
            if (haveRev != s.Rev) channel.Writer.TryWrite(ResetMessage(s));
            foreach (var ask in s.AskMessages.Values) channel.Writer.TryWrite(ask);
        }
        return channel.Reader;
    }

    /// <summary>The shell's stream ended (the page closed or the connection dropped).</summary>
    public void Unsubscribe(string sessionId, ChannelReader<JsonObject> reader)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return;
        lock (s.Sync)
        {
            if (s.Out is not null && ReferenceEquals(s.Out.Reader, reader)) s.Out = null;
            s.Touch();
        }
    }

    /// <summary>
    /// An event from the shell: runs the action on the session's state and pushes the patch (acknowledging
    /// <paramref name="seq"/>) and the action's effects. A failed action leaves the state as it was and shows its error.
    /// </summary>
    public async Task EventAsync(string sessionId, string action, JsonNode? args, JsonObject? local, long seq, CancellationToken ct = default)
    {
        var received = DateTimeOffset.UtcNow;
        var s = Get(sessionId);
        s.Touch();
        var plugin = await _host.FindAsync(s.PluginId, ct) ?? throw new PluginCallException("not_found", "the plugin stopped");
        if (!s.Actions.Contains(action)) throw new PluginCallException("not_found", $"the view has no action \"{action}\"");
        if (args is not null && args.ToJsonString().Length > UiTree.MaxArgsBytes) throw new PluginCallException("invalid_argument", "args too large");
        await s.Lock.WaitAsync(ct);
        try
        {
            if (local is not null) s.Local = (JsonObject)local.DeepClone();
            var before = s.State?.DeepClone();
            var op = Op(s, "view.action");
            op["action"] = action;
            op["args"] = args?.DeepClone();
            op["state"] = before?.DeepClone();
            JsonNode? result;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var queued = (DateTimeOffset.UtcNow - received).TotalMilliseconds;
            try
            {
                result = await plugin.Main.RunAsync(op, client: s.Client, ct: ct);
            }
            catch (PluginCallException e)
            {
                plugin.Fail($"view {s.View} action {action}", e.Message);
                Push(s, new JsonObject { ["type"] = "patch", ["base"] = s.Rev, ["rev"] = s.Rev, ["ack"] = seq, ["ops"] = new JsonArray() });
                Push(s, Effect(new JsonObject { ["type"] = "toast", ["tone"] = "danger", ["message"] = e.Message, ["error"] = true }));
                return;
            }
            var state = result?["state"]?.DeepClone() ?? before;
            CheckState(state);
            s.State = state;
            var handler = clock.Elapsed.TotalMilliseconds;
            var (render, diff, ops) = await CommitAsync(plugin, s, state, seq, ct);
            Record(s, new JsonObject
            {
                ["at"] = DateTimeOffset.UtcNow.ToString("O"), ["action"] = action, ["seq"] = seq, ["queueMs"] = Math.Round(queued, 2),
                ["handlerMs"] = Math.Round(handler, 2), ["renderMs"] = Math.Round(render, 2), ["diffMs"] = Math.Round(diff, 2), ["ops"] = ops,
                ["effects"] = result?["effects"] is JsonArray fx ? fx.Count : 0,
            });
            if (result?["effects"] is JsonArray effects)
                foreach (var e in effects.OfType<JsonObject>())
                    if (CleanEffect(e) is { } clean) Push(s, Effect(clean));
        }
        finally
        {
            s.Lock.Release();
        }
    }

    /// <summary>The shell's answer to a question (confirm, prompt, a sheet's result, a picked file).</summary>
    public bool Answer(string sessionId, string askId, JsonNode? value)
    {
        if (!_sessions.TryGetValue(sessionId, out var s) || !s.Asks.TryRemove(askId, out var tcs)) return false;
        s.AskMessages.TryRemove(askId, out _);
        s.Touch();
        tcs.TrySetResult(value?.DeepClone());
        return true;
    }

    /// <summary>The shell's local state bag (inputs it holds) changed without an event: kept for the next action and refreshes.</summary>
    public void SetLocal(string sessionId, JsonObject local)
    {
        if (_sessions.TryGetValue(sessionId, out var s)) s.Local = (JsonObject)local.DeepClone();
    }

    /// <summary>The shell's context changed (language, theme, width): renders again.</summary>
    public async Task SetClientAsync(string sessionId, JsonObject client, CancellationToken ct = default)
    {
        var s = Get(sessionId);
        var plugin = await _host.FindAsync(s.PluginId, ct) ?? throw new PluginCallException("not_found", "the plugin stopped");
        await s.Lock.WaitAsync(ct);
        try
        {
            s.Client = (JsonObject)client.DeepClone();
            await CommitAsync(plugin, s, s.State, null, ct);
        }
        finally
        {
            s.Lock.Release();
        }
    }

    /// <summary>A file the shell hands over (a picked or dropped file): returns the ref the plugin reads it by.</summary>
    public JsonObject AddFile(string sessionId, string name, string type, byte[] bytes)
    {
        var s = Get(sessionId);
        if (bytes.Length > MaxFileBytes) throw new PluginCallException("quota_exceeded", $"a file may be {MaxFileBytes / 1024 / 1024} MiB at most");
        foreach (var old in _files.Where(f => f.Value.Expires < DateTimeOffset.UtcNow).Select(f => f.Key).ToList()) _files.TryRemove(old, out _);
        var id = Guid.NewGuid().ToString("n");
        var safeName = Path.GetFileName(name);
        _files[id] = new UiFile(s.PluginId, safeName, type, bytes, DateTimeOffset.UtcNow.AddMinutes(10));
        return new JsonObject { ["ref"] = id, ["name"] = safeName, ["size"] = bytes.Length, ["type"] = type };
    }

    /// <summary>Closes a session (the shell closed the view): its dispose runs and its questions are cancelled.</summary>
    public async Task CloseAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var s)) return;
        lock (s.Sync) s.Out?.Writer.TryComplete();
        foreach (var ask in s.Asks.Values) ask.TrySetResult(null);
        foreach (var child in _sessions.Values.Where(c => c.ParentId == sessionId).ToList()) await CloseAsync(child.Id);
        var plugin = await _host.FindAsync(s.PluginId);
        if (plugin is null || !plugin.Running) return;
        try
        {
            await plugin.Main.RunAsync(Op(s, "view.dispose"), TimeSpan.FromSeconds(5));
        }
        catch (PluginCallException)
        {
        }
    }

    public bool HasSessions(string pluginId) => _sessions.Values.Any(s => s.PluginId == pluginId);

    /// <summary>
    /// A dev plugin was reloaded (06 §2): each of its open views renders again on the new code with the state it has, and the
    /// shell gets the difference as an ordinary patch. When the kept state no longer fits the new code (render throws), the
    /// view starts over from init; when that fails too, the shell shows the error.
    /// </summary>
    internal async Task ReloadAsync(string pluginId)
    {
        var plugin = await _host.FindAsync(pluginId);
        if (plugin is null) return;
        foreach (var s in _sessions.Values.Where(s => s.PluginId == pluginId).ToList())
        {
            await s.Lock.WaitAsync();
            try
            {
                try
                {
                    await CommitAsync(plugin, s, s.State, null, CancellationToken.None);
                }
                catch (PluginCallException)
                {
                    var init = await plugin.Main.RunAsync(Op(s, "view.init"), client: s.Client);
                    s.State = init?["state"]?.DeepClone() ?? new JsonObject();
                    CheckState(s.State);
                    await CommitAsync(plugin, s, s.State, null, CancellationToken.None);
                }
            }
            catch (PluginCallException e)
            {
                plugin.Fail($"view {s.View} reload", e.Message);
                Push(s, Effect(new JsonObject { ["type"] = "toast", ["tone"] = "danger", ["message"] = e.Message, ["error"] = true }));
            }
            finally
            {
                s.Lock.Release();
            }
        }
    }

    internal async Task CloseAllAsync(string pluginId)
    {
        foreach (var s in _sessions.Values.Where(s => s.PluginId == pluginId).ToList())
        {
            lock (s.Sync)
            {
                s.Out?.Writer.TryWrite(new JsonObject { ["type"] = "closed", ["reason"] = "the plugin was stopped or updated" });
                s.Out?.Writer.TryComplete();
            }
            _sessions.TryRemove(s.Id, out _);
            foreach (var ask in s.Asks.Values) ask.TrySetResult(null);
        }
        await Task.CompletedTask;
    }

    /// <summary>The session's current tree and revision (what a reset would send); tests and the dev inspector read it.</summary>
    public (JsonObject Tree, long Rev, JsonNode? State) Snapshot(string sessionId)
    {
        var s = Get(sessionId);
        lock (s.Sync) return ((JsonObject)s.Tree.DeepClone(), s.Rev, s.State?.DeepClone());
    }

    // ---- host calls from the plugin

    internal async Task<JsonNode?> AskAsync(PluginV2Instance p, JsonObject? args, CancellationToken ct)
    {
        var sessionId = args?["session"]?.GetValue<string>();
        if (sessionId is null || !_sessions.TryGetValue(sessionId, out var s) || s.PluginId != p.Id)
            throw new PluginCallException("invalid_argument", "this works in a view's action only (no open session)");
        if (args?["effect"] is not JsonObject effect || CleanEffect(effect) is not { } clean)
            throw new PluginCallException("invalid_argument", "not a question the shell can ask");
        var id = Guid.NewGuid().ToString("n");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Asks[id] = tcs;
        var message = new JsonObject { ["type"] = "ask", ["id"] = id, ["effect"] = clean };
        s.AskMessages[id] = message;
        Push(s, (JsonObject)message.DeepClone());
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            s.Asks.TryRemove(id, out _);
            s.AskMessages.TryRemove(id, out _);
        }
    }

    internal async Task FlushAsync(PluginV2Instance p, JsonObject? args, CancellationToken ct)
    {
        var sessionId = args?["session"]?.GetValue<string>();
        if (sessionId is null || !_sessions.TryGetValue(sessionId, out var s) || s.PluginId != p.Id) return;
        var state = args?["state"]?.DeepClone();
        var (tree, actions) = await RenderAsync(p, s, state, ct);
        Swap(s, tree, actions, null);
    }

    internal void Refresh(PluginV2Instance p, string? sessionId)
    {
        foreach (var s in _sessions.Values.Where(s => s.PluginId == p.Id && (sessionId is null || s.Id == sessionId)).ToList())
            _ = RefreshAsync(p, s);
    }

    private async Task RefreshAsync(PluginV2Instance p, UiSession s)
    {
        await s.Lock.WaitAsync();
        try
        {
            var op = Op(s, "view.refresh");
            op["state"] = s.State?.DeepClone();
            var result = await p.Main.RunAsync(op, client: s.Client);
            var state = result?["state"]?.DeepClone() ?? s.State;
            CheckState(state);
            s.State = state;
            await CommitAsync(p, s, state, null, CancellationToken.None);
            if (result?["effects"] is JsonArray effects)
                foreach (var e in effects.OfType<JsonObject>())
                    if (CleanEffect(e) is { } clean) Push(s, Effect(clean));
        }
        catch (PluginCallException e)
        {
            p.Fail($"view {s.View} refresh", e.Message);
        }
        finally
        {
            s.Lock.Release();
        }
    }

    /// <summary>A data channel operation (ui.collection): kept for resets and pushed to the shell. Null when it is fine.</summary>
    internal string? Data(PluginV2Instance p, JsonObject? args)
    {
        var sessionId = args?["session"]?.GetValue<string>();
        if (sessionId is null || !_sessions.TryGetValue(sessionId, out var s) || s.PluginId != p.Id) return "no open session";
        var name = args?["collection"]?.GetValue<string>();
        var op = args?["op"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name) || op is null) return "collection and op are required";
        var message = new JsonObject { ["type"] = "data", ["collection"] = name, ["op"] = op };
        lock (s.Sync)
        {
            var c = s.Collections.TryGetValue(name, out var existing) ? existing : s.Collections[name] = new UiCollection();
            var items = args?["items"] as JsonArray;
            switch (op)
            {
                case "replace":
                    c.Items.Clear();
                    if (items is not null) c.Items.AddRange(items.Select(i => i?.DeepClone()).OfType<JsonNode>());
                    c.Meta = args?["meta"]?.DeepClone() as JsonObject ?? [];
                    message["meta"] = c.Meta.DeepClone();
                    break;
                case "append":
                    if (items is not null) c.Items.AddRange(items.Select(i => i?.DeepClone()).OfType<JsonNode>());
                    break;
                case "prepend":
                    if (items is not null) c.Items.InsertRange(0, items.Select(i => i?.DeepClone()).OfType<JsonNode>());
                    break;
                case "update":
                case "remove":
                {
                    var key = args?["key"]?.ToString();
                    var keyField = c.Meta["key"]?.GetValue<string>() ?? "id";
                    var at = c.Items.FindIndex(i => i[keyField]?.ToString() == key);
                    if (at >= 0)
                    {
                        if (op == "remove") c.Items.RemoveAt(at);
                        else if (args?["patch"] is JsonObject patch && c.Items[at] is JsonObject row)
                            foreach (var (k, v) in patch) row[k] = v?.DeepClone();
                    }
                    message["key"] = key;
                    if (args?["patch"] is { } pt) message["patch"] = pt.DeepClone();
                    break;
                }
                case "clear":
                    c.Items.Clear();
                    break;
                case "children":
                {
                    var key = args?["key"]?.ToString() ?? "";
                    c.Children[key] = new JsonArray([.. (items ?? []).Select(i => i?.DeepClone())]);
                    message["key"] = key;
                    break;
                }
                default:
                    return $"unknown data op \"{op}\"";
            }
            if (c.Items.Count > MaxCollectionItems) c.Items.RemoveRange(0, c.Items.Count - MaxCollectionItems);
            if (items is not null && op != "children") message["items"] = items.DeepClone();
            if (op == "children") message["items"] = c.Children[message["key"]!.GetValue<string>()].DeepClone();
        }
        Push(s, message);
        return null;
    }

    internal JsonObject? ReadFile(PluginV2Instance p, string fileRef)
    {
        if (!_files.TryGetValue(fileRef, out var f) || f.PluginId != p.Id || f.Expires < DateTimeOffset.UtcNow) return null;
        return new JsonObject { ["name"] = f.Name, ["type"] = f.Type, ["size"] = f.Bytes.Length, ["base64"] = Convert.ToBase64String(f.Bytes) };
    }

    // ---- internals

    private UiSession Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : throw new PluginCallException("not_found", "no such view session (it was closed)");

    private static JsonObject Op(UiSession s, string op) => new()
    {
        ["op"] = op,
        ["view"] = s.View,
        ["session"] = s.Id,
        ["params"] = s.Params.DeepClone(),
        ["query"] = s.Query.DeepClone(),
        ["client"] = s.Client.DeepClone(),
        ["local"] = s.Local.DeepClone(),
    };

    private async Task<(JsonObject Tree, HashSet<string> Actions)> RenderAsync(PluginV2Instance plugin, UiSession s, JsonNode? state, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var op = Op(s, "view.render");
        op["state"] = state?.DeepClone();
        JsonNode? result;
        try
        {
            result = await plugin.Main.RunAsync(op, TimeSpan.FromSeconds(10), PluginMain.RenderLimit, s.Client, ct);
        }
        catch (PluginCallException e)
        {
            plugin.Fail($"view {s.View} render", e.Message);
            return (ErrorTree(plugin, e.Message), []);
        }
        var actions = result?["actions"] is JsonArray a ? a.Select(x => x?.GetValue<string>()).OfType<string>().ToHashSet() : [];
        var warnings = new List<string>();
        JsonObject tree;
        try
        {
            tree = UiTree.Normalize(result?["tree"], actions, Schema, warnings, s.Slot);
        }
        catch (UiTreeException e)
        {
            plugin.Fail($"view {s.View} render", e.Message);
            return (ErrorTree(plugin, e.Message), actions);
        }
        if (warnings.Count > 0)
        {
            s.Warnings = warnings;
            foreach (var w in warnings.Take(5)) plugin.Log("warn", $"view {s.View}: {w}");
        }
        s.RenderMicros = (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        return (tree, actions);
    }

    private static JsonObject ErrorTree(PluginV2Instance plugin, string message) => new()
    {
        ["type"] = "Alert",
        ["props"] = new JsonObject
        {
            ["tone"] = "danger",
            ["title"] = plugin.Manifest.DisplayName("en"),
            ["message"] = message,
        },
    };

    // renders and pushes the patch; how long the render and the diff took, and how many ops it had (the inspector's timeline)
    private async Task<(double RenderMs, double DiffMs, int Ops)> CommitAsync(PluginV2Instance plugin, UiSession s, JsonNode? state, long? ack, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (tree, actions) = await RenderAsync(plugin, s, state, ct);
        var render = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        var ops = Swap(s, tree, actions, ack);
        return (render, clock.Elapsed.TotalMilliseconds, ops);
    }

    private static void Record(UiSession s, JsonObject entry)
    {
        lock (s.Sync)
        {
            s.Timeline.Enqueue(entry);
            while (s.Timeline.Count > 100) s.Timeline.Dequeue();
        }
    }

    /// <summary>
    /// What the dev inspector shows for a session (06 §2): the tree, revision, state, actions, warnings and the timeline of
    /// round trips. Only for a dev plugin (one loaded from a folder); null otherwise or when there is no such session.
    /// </summary>
    public async Task<JsonObject?> InspectAsync(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) return null;
        if (await _host.FindAsync(s.PluginId) is not { Dev: true }) return null;
        lock (s.Sync)
            return new JsonObject
            {
                ["plugin"] = s.PluginId, ["view"] = s.View, ["rev"] = s.Rev, ["tree"] = s.Tree.DeepClone(), ["state"] = s.State?.DeepClone(),
                ["local"] = s.Local.DeepClone(), ["client"] = s.Client.DeepClone(),
                ["actions"] = new JsonArray([.. s.Actions.Order(StringComparer.Ordinal).Select(a => (JsonNode?)JsonValue.Create(a))]),
                ["warnings"] = new JsonArray([.. s.Warnings.Select(w => (JsonNode?)JsonValue.Create(w))]),
                ["renderMicros"] = s.RenderMicros,
                ["timeline"] = new JsonArray([.. s.Timeline.Select(e => (JsonNode?)e.DeepClone())]),
            };
    }

    private static int Swap(UiSession s, JsonObject tree, HashSet<string> actions, long? ack)
    {
        lock (s.Sync)
        {
            var ops = UiDiff.Diff(s.Tree, tree);
            var message = new JsonObject { ["type"] = "patch", ["base"] = s.Rev };
            s.Tree = tree;
            if (actions.Count > 0 || tree["type"]?.GetValue<string>() != "Alert") s.Actions = actions;
            s.Rev++;
            message["rev"] = s.Rev;
            if (ack is { } a) message["ack"] = a;
            message["ops"] = ops;
            s.Out?.Writer.TryWrite(message);
            return ops.Count;
        }
    }

    private static void Push(UiSession s, JsonObject message)
    {
        lock (s.Sync) s.Out?.Writer.TryWrite(message);
    }

    private static JsonObject ResetMessage(UiSession s)
    {
        var collections = new JsonObject();
        foreach (var (name, c) in s.Collections)
        {
            var children = new JsonObject();
            foreach (var (k, v) in c.Children) children[k] = v.DeepClone();
            collections[name] = new JsonObject
            {
                ["items"] = new JsonArray([.. c.Items.Select(i => i.DeepClone())]),
                ["meta"] = c.Meta.DeepClone(),
                ["children"] = children,
            };
        }
        return new JsonObject { ["type"] = "reset", ["rev"] = s.Rev, ["tree"] = s.Tree.DeepClone(), ["collections"] = collections };
    }

    private static JsonObject Effect(JsonObject effect) => new() { ["type"] = "effect", ["effect"] = effect };

    private static readonly HashSet<string> EffectTypes =
    [
        "toast", "navigate", "back", "openExternal", "copy", "download", "focus", "scrollTo", "close", "fail", "setQuery",
        "confirm", "prompt", "pickFile", "openSheet", "openDialog",
    ];

    // Only what a shell knows how to do, and links it may open: https for openExternal.
    private static JsonObject? CleanEffect(JsonObject e)
    {
        var type = e["type"]?.GetValue<string>();
        if (type is null || !EffectTypes.Contains(type)) return null;
        if (type == "openExternal")
        {
            var url = e["url"]?.GetValue<string>() ?? "";
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return null;
        }
        if (type == "download" && (e["base64"]?.GetValue<string>()?.Length ?? e["text"]?.GetValue<string>()?.Length ?? 0) > 20 * 1024 * 1024) return null;
        var clean = (JsonObject)e.DeepClone();
        foreach (var (k, v) in e)
            if (v is JsonValue sv && sv.TryGetValue<string>(out var str) && str.Length > 4096 && k is not ("text" or "base64"))
                clean[k] = str[..4096];
        return clean;
    }

    private static void CheckState(JsonNode? state)
    {
        var size = state?.ToJsonString().Length ?? 0;
        if (size > MaxStateBytes)
            throw new PluginCallException("quota_exceeded", $"the view's state is {size / 1024} KiB; the most is {MaxStateBytes / 1024} KiB — keep large data in a data channel");
    }

    private async Task ReapAsync()
    {
        foreach (var s in _sessions.Values.ToList())
        {
            bool idle;
            lock (s.Sync) idle = s.Out is null && s.Asks.IsEmpty && DateTimeOffset.UtcNow - s.LastSeen > Abandoned;
            if (idle) await CloseAsync(s.Id);
        }
        foreach (var old in _files.Where(f => f.Value.Expires < DateTimeOffset.UtcNow).Select(f => f.Key).ToList()) _files.TryRemove(old, out _);
    }
}

/// <summary>What a shell asks to open.</summary>
/// <param name="Client">The shell's context: shell, ui, components, locale, theme, width, surface, platform, timeZone, utcOffset.</param>
public sealed record UiOpenRequest(string PluginId, string View, JsonObject? Params, JsonObject Client, JsonObject? Query = null,
    string? Slot = null, string? ParentId = null);

/// <summary>A session opened: its id, revision and first tree, and what the host dropped from it.</summary>
public sealed record UiOpenResult(string SessionId, long Rev, JsonObject Tree, List<string> Warnings);

internal sealed class UiSession(string id, string pluginId, string view, JsonObject parameters, JsonObject client, JsonObject query, string? slot, string? parentId)
{
    public string Id { get; } = id;
    public string PluginId { get; } = pluginId;
    public string View { get; } = view;
    public JsonObject Params { get; } = parameters;
    public JsonObject Client { get; set; } = client;
    public JsonObject Query { get; } = query;
    public string? Slot { get; } = slot;
    public string? ParentId { get; } = parentId;
    public JsonObject Local { get; set; } = [];
    public JsonNode? State { get; set; }
    public JsonObject Tree { get; set; } = new() { ["type"] = "Stack", ["props"] = new JsonObject() };
    public HashSet<string> Actions { get; set; } = [];
    public long Rev { get; set; }
    public List<string> Warnings { get; set; } = [];
    public long RenderMicros { get; set; }
    /// <summary>The last round trips (dev inspector, 06 §2): newest last, at most 100.</summary>
    public Queue<JsonObject> Timeline { get; } = new();
    public Lock Sync { get; } = new();
    public SemaphoreSlim Lock { get; } = new(1, 1);
    public Channel<JsonObject>? Out { get; set; }
    public Dictionary<string, UiCollection> Collections { get; } = [];
    public ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> Asks { get; } = new();
    public ConcurrentDictionary<string, JsonObject> AskMessages { get; } = new();
    public DateTimeOffset LastSeen { get; private set; } = DateTimeOffset.UtcNow;

    public void Touch() => LastSeen = DateTimeOffset.UtcNow;
}

internal sealed class UiCollection
{
    public List<JsonNode> Items { get; } = [];
    public JsonObject Meta { get; set; } = [];
    public Dictionary<string, JsonArray> Children { get; } = [];
}

internal sealed record UiFile(string PluginId, string Name, string Type, byte[] Bytes, DateTimeOffset Expires);

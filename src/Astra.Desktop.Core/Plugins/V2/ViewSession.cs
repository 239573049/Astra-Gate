using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>Which view to open: a plugin's view, with its params, query, slot and the session that opened it.</summary>
public sealed record ViewTarget(string Plugin, string View, JsonObject? Params = null, JsonObject? Query = null, string? Slot = null, string? Parent = null);

/// <summary>One data channel (03 §6 / 05 §5): its rows, its meta, and a tree's lazily loaded children.</summary>
public sealed class UiCollection
{
    public List<JsonObject> Items { get; } = [];
    public JsonObject Meta { get; set; } = [];
    public Dictionary<string, JsonArray> Children { get; } = [];
}

/// <summary>
/// One open plugin view on the desktop (design/plugin-v2 03 §3.2), the web's <c>useViewSession</c>: opens the session,
/// follows its event stream (reconnecting with the revision it has), applies patches, keeps the local state bag and the
/// per-input edit sequence, and sends events. Everything it raises is posted through <c>post</c> (the UI thread).
/// </summary>
public sealed class ViewSession : IAsyncDisposable
{
    private readonly ApiClient _api;
    private readonly ViewTarget _target;
    private readonly Func<JsonObject> _client;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _stop = new();
    private Task? _stream;
    private long _seq;

    public ViewSession(ApiClient api, ViewTarget target, Func<JsonObject> client, Action<Action> post)
    {
        _api = api;
        _target = target;
        _client = client;
        _post = post;
    }

    public ViewTarget Target => _target;
    public string? Id { get; private set; }
    public JsonObject? Tree { get; private set; }
    public long Rev { get; private set; }
    public string? Error { get; private set; }
    public Dictionary<string, UiCollection> Collections { get; } = [];

    /// <summary>The local state bag: what inputs hold, sent with every event (03 §6).</summary>
    public Dictionary<string, JsonNode?> Local { get; } = [];

    /// <summary>Per input name, the sequence of its last local edit; a patch acknowledging less doesn't overwrite it (03 §6.1).</summary>
    public Dictionary<string, long> EditedAt { get; } = [];

    /// <summary>The newest event sequence the host has acknowledged.</summary>
    public long Ack { get; private set; }

    /// <summary>Field errors from ctx.fail, by input name.</summary>
    public Dictionary<string, string> FieldErrors { get; } = [];

    /// <summary>The key of the control whose action has been in flight for 150 ms (automatic busy state).</summary>
    public string? BusyKey { get; private set; }

    /// <summary>The tree changed: <c>ops</c> applied (null = a whole new tree).</summary>
    public event Action<JsonArray?>? TreeChanged;

    /// <summary>An effect to run (toast, navigate, download, …).</summary>
    public event Action<JsonObject>? Effect;

    /// <summary>A question to answer (confirm, prompt, pickFile, openSheet, openDialog): its id and effect.</summary>
    public event Action<string, JsonObject>? Ask;

    /// <summary>A data channel changed.</summary>
    public event Action<string>? DataChanged;

    /// <summary>Busy state or field errors changed.</summary>
    public event Action? StateChanged;

    /// <summary>The session failed or was closed by the host (the plugin stopped).</summary>
    public event Action<string>? Failed;

    /// <summary>Opens the session and starts following its stream.</summary>
    public async Task OpenAsync(CancellationToken ct = default)
    {
        try
        {
            var body = new JsonObject
            {
                ["plugin"] = _target.Plugin,
                ["view"] = _target.View,
                ["params"] = _target.Params?.DeepClone() ?? new JsonObject(),
                ["query"] = _target.Query?.DeepClone() ?? new JsonObject(),
                ["slot"] = _target.Slot,
                ["parent"] = _target.Parent,
                ["client"] = _client(),
            };
            var open = await _api.PostAsync<JsonObject, UiOpenDto>("/api/plugins/v2/ui/sessions", body, ct) ?? throw new ApiException(0, "empty response");
            Id = open.Session;
            Rev = open.Rev;
            Tree = open.Tree;
            _post(() => TreeChanged?.Invoke(null));
            _stream = Task.Run(() => FollowAsync(_stop.Token));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            Error = e.Message;
            _post(() => Failed?.Invoke(e.Message));
        }
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Id is { } id)
        {
            try
            {
                await foreach (var frame in _api.StreamAsync($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(id)}/stream?rev={Rev}", ct))
                {
                    JsonObject? message;
                    try
                    {
                        message = JsonNode.Parse(frame.Data) as JsonObject;
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (message is null) continue;
                    var reconnect = false;
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _post(() =>
                    {
                        try
                        {
                            reconnect = Handle(message);
                        }
                        finally
                        {
                            done.TrySetResult();
                        }
                    });
                    await done.Task;
                    if (reconnect) break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is ApiException { Status: 404 })
            {
                _post(() => Failed?.Invoke(e.Message));
                return;
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or IOException)
            {
                // the server restarted or the connection dropped: reconnect with what we have
            }
            try
            {
                await Task.Delay(1500, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // On the UI thread: one stream message. True when the stream should reconnect (a patch was missed).
    private bool Handle(JsonObject m)
    {
        switch (m["type"]?.GetValue<string>())
        {
            case "patch":
            {
                var ops = m["ops"] as JsonArray ?? [];
                if (m["base"]?.GetValue<long>() != Rev && ops.Count > 0) return true;
                Rev = m["rev"]?.GetValue<long>() ?? Rev;
                if (m["ack"] is JsonValue a)
                {
                    Ack = Math.Max(Ack, a.GetValue<long>());
                    BusyKey = null;
                    StateChanged?.Invoke();
                }
                if (ops.Count > 0 && Tree is not null)
                {
                    Tree = PluginUiProtocol.ApplyPatch(Tree, ops);
                    TreeChanged?.Invoke(ops);
                }
                return false;
            }
            case "reset":
                Rev = m["rev"]?.GetValue<long>() ?? Rev;
                Tree = m["tree"] as JsonObject ?? Tree;
                Collections.Clear();
                if (m["collections"] is JsonObject cols)
                    foreach (var (name, c) in cols)
                    {
                        var col = new UiCollection { Meta = c?["meta"] as JsonObject ?? [] };
                        foreach (var item in c?["items"] as JsonArray ?? []) if (item is JsonObject o) col.Items.Add((JsonObject)o.DeepClone());
                        if (c?["children"] is JsonObject kids) foreach (var (k, v) in kids) col.Children[k] = (JsonArray)v!.DeepClone();
                        Collections[name] = col;
                    }
                TreeChanged?.Invoke(null);
                return false;
            case "effect":
                if (m["effect"] is JsonObject e)
                {
                    if (e["type"]?.GetValue<string>() == "fail")
                    {
                        FieldErrors.Clear();
                        if (e["errors"] is JsonObject errors) foreach (var (k, v) in errors) FieldErrors[k] = v?.ToString() ?? "";
                        StateChanged?.Invoke();
                    }
                    else Effect?.Invoke(e);
                }
                return false;
            case "ask":
                if (m["id"]?.GetValue<string>() is { } askId && m["effect"] is JsonObject ef) Ask?.Invoke(askId, ef);
                return false;
            case "data":
                ApplyData(m);
                return false;
            case "error":
                Effect?.Invoke(new JsonObject { ["type"] = "toast", ["tone"] = "danger", ["message"] = m["message"]?.DeepClone(), ["error"] = true });
                return false;
            case "closed":
                Failed?.Invoke(m["reason"]?.GetValue<string>() ?? "closed");
                _stop.Cancel();
                return false;
        }
        return false;
    }

    private void ApplyData(JsonObject m)
    {
        var name = m["collection"]?.GetValue<string>();
        if (name is null) return;
        if (!Collections.TryGetValue(name, out var c)) Collections[name] = c = new UiCollection();
        var keyField = c.Meta["key"]?.GetValue<string>() ?? "id";
        var items = (m["items"] as JsonArray ?? []).OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()).ToList();
        switch (m["op"]?.GetValue<string>())
        {
            case "replace":
                c.Items.Clear();
                c.Items.AddRange(items);
                c.Meta = m["meta"]?.DeepClone() as JsonObject ?? [];
                break;
            case "append":
                c.Items.AddRange(items);
                break;
            case "prepend":
                c.Items.InsertRange(0, items);
                break;
            case "update":
                foreach (var row in c.Items.Where(i => i[keyField]?.ToString() == m["key"]?.ToString()))
                    if (m["patch"] is JsonObject patch) foreach (var (k, v) in patch) row[k] = v?.DeepClone();
                break;
            case "remove":
                c.Items.RemoveAll(i => i[keyField]?.ToString() == m["key"]?.ToString());
                break;
            case "clear":
                c.Items.Clear();
                break;
            case "children":
                c.Children[m["key"]?.ToString() ?? ""] = new JsonArray([.. items.Select(i => (JsonNode?)i)]);
                break;
        }
        if (c.Items.Count > 10_000) c.Items.RemoveRange(0, c.Items.Count - 10_000);
        DataChanged?.Invoke(name);
    }

    /// <summary>Sends an event: an action with its args, the local bag and the next sequence number.</summary>
    /// <summary>
    /// Shows <paramref name="tree"/> with no session (the component gallery, 06 §2): it is never sent anywhere, and an action
    /// a control fires is only reported through <see cref="Sent"/>.
    /// </summary>
    public void ShowStatic(JsonObject tree)
    {
        _static = true;
        Tree = tree;
        Rev = 1;
        _post(() => TreeChanged?.Invoke(null));
    }

    /// <summary>An action fired in a static view (<see cref="ShowStatic"/>): name and args.</summary>
    public event Action<string, JsonNode?>? Sent;

    private bool _static;

    public void Send(string action, JsonNode? args, string? sourceKey = null)
    {
        if (_static)
        {
            Sent?.Invoke(action, args?.DeepClone());
            return;
        }
        if (Id is not { } id) return;
        var seq = Interlocked.Increment(ref _seq);
        FieldErrors.Clear();
        var key = sourceKey ?? action;
        var timer = new Timer(_ => _post(() =>
        {
            BusyKey = key;
            StateChanged?.Invoke();
        }), null, 150, Timeout.Infinite);
        var local = new JsonObject();
        foreach (var (k, v) in Local) local[k] = v?.DeepClone();
        var body = new JsonObject { ["action"] = action, ["args"] = args?.DeepClone(), ["local"] = local, ["seq"] = seq };
        _ = Task.Run(async () =>
        {
            try
            {
                await _api.PostAsync($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(id)}/events", body);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
                _post(() => Effect?.Invoke(new JsonObject { ["type"] = "toast", ["tone"] = "danger", ["message"] = e.Message, ["error"] = true }));
            }
            finally
            {
                await timer.DisposeAsync();
                await Task.Delay(3000);
                _post(() =>
                {
                    if (BusyKey != key) return;
                    BusyKey = null;
                    StateChanged?.Invoke();
                });
            }
        });
    }

    /// <summary>An input's local value changed (the user typed, picked, toggled).</summary>
    public void SetLocal(string name, JsonNode? value)
    {
        Local[name] = value?.DeepClone();
        EditedAt[name] = Interlocked.Read(ref _seq) + 1;
    }

    /// <summary>Whether the host's <paramref name="name"/> value may replace the local one (no newer local edit).</summary>
    public bool ServerWins(string name) => !EditedAt.TryGetValue(name, out var at) || at <= Ack;

    /// <summary>Answers a question.</summary>
    public void Answer(string askId, JsonNode? value)
    {
        if (Id is not { } id) return;
        var body = new JsonObject { ["id"] = askId, ["value"] = value?.DeepClone() };
        _ = Task.Run(async () =>
        {
            try
            {
                await _api.PostAsync($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(id)}/answers", body);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
            }
        });
    }

    /// <summary>The shell's context changed (language, theme, width): the view renders again.</summary>
    public void ClientChanged()
    {
        if (Id is not { } id) return;
        var body = new JsonObject { ["client"] = _client() };
        _ = Task.Run(async () =>
        {
            try
            {
                await _api.PostAsync($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(id)}/client", body);
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
            }
        });
    }

    /// <summary>Hands a file to the plugin (pickFile / FileDrop): its ref object, or null when it failed.</summary>
    public async Task<JsonNode?> UploadAsync(string name, string type, byte[] bytes, CancellationToken ct = default)
    {
        if (Id is not { } id) return null;
        var body = new JsonObject { ["name"] = name, ["type"] = type, ["base64"] = Convert.ToBase64String(bytes) };
        try
        {
            return await _api.PostAsync<JsonObject, JsonObject>($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(id)}/files", body, ct);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _post(() => Effect?.Invoke(new JsonObject { ["type"] = "toast", ["tone"] = "danger", ["message"] = e.Message, ["error"] = true }));
            return null;
        }
    }

    /// <summary>Closes the session (the view went away).</summary>
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (Id is { } id)
        {
            try
            {
                await _api.DeleteAsync($"/api/plugins/v2/ui/sessions/{ApiClient.Enc(id)}");
            }
            catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
            {
            }
        }
        if (_stream is not null)
        {
            try
            {
                await _stream;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}

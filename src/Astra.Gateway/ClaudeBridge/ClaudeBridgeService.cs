using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>Which account a bridged request is made for: its <c>claude</c> sign-in's config directory (empty = the default) and its proxy.</summary>
public sealed record ClaudeTarget(string ConfigDir, string? Proxy, string? AccountId)
{
    public string Key => ConfigDir + "\0" + Proxy;
}

/// <summary>One run, as the admin status lists it.</summary>
public sealed record ClaudeRunInfo(string Id, int? Pid, string Model, string State, DateTimeOffset Since, string Account);

/// <summary>
/// The Claude subscription bridge (magpie's <c>serveClaudeSubscription</c>, ported from its documentation and sources, MIT): a request the
/// gateway has turned into a Messages request is answered by the real <c>claude</c> program instead of by a call to Anthropic's API with
/// an OAuth token. The caller's tools are offered to the run as an MCP server (<see cref="ClaudeMcpHelper"/>, this program's own hidden
/// <c>claude-mcp-helper</c> subcommand); a call of one ends the reply with <c>stop_reason: tool_use</c> and the run is parked, its MCP call
/// held, until the caller's next request carries the result. A conversation's next turn goes on in the run that answered it when that run is
/// still there (told only the new messages, so Anthropic reads the rest from its cache), else a new run is told the whole conversation.
/// Runs are <c>claude -p</c> in <c>stream-json</c>; magpie's default interactive (pty) mode and the Bun tap it uses are not ported.
/// </summary>
public sealed class ClaudeBridgeService : IAsyncDisposable
{
    /// <summary>How long a run waits for the caller's tool results before it is ended.</summary>
    internal static TimeSpan ParkLimit { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long a turn waits for a place among the processes before it is refused.</summary>
    internal static TimeSpan RoomWait { get; set; } = TimeSpan.FromMinutes(2);

    private const string RunSettings = """{"disableDeepLinkRegistration":"disable","showThinkingSummaries":true}""";
    private static readonly TimeSpan SettleWait = TimeSpan.FromSeconds(5);

    private readonly IGatewaySettings _settings;
    private readonly ClaudeCodeLocator _locator;
    private readonly Func<(string Command, IReadOnlyList<string> Args)> _helper;
    private readonly ILogger<ClaudeBridgeService> _logger;
    private readonly ClaudeCallbackServer _callbacks = new();
    private readonly object _gate = new();
    private readonly List<ClaudeRun> _runs = [];
    private readonly ConcurrentDictionary<string, ClaudeRun> _byToken = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-claude-bridge");
    private readonly ConcurrentDictionary<string, JsonObject> _limits = new(StringComparer.Ordinal);
    private Task? _maintenance;
    private int _next;
    private int _disposed;

    /// <param name="helper">The command that runs <c>claude-mcp-helper</c> (this program), and the arguments before the helper's own.</param>
    public ClaudeBridgeService(IGatewaySettings settings, ClaudeCodeLocator locator, Func<(string Command, IReadOnlyList<string> Args)> helper,
        ILogger<ClaudeBridgeService> logger)
    {
        _settings = settings;
        _locator = locator;
        _helper = helper;
        _logger = logger;
        _callbacks.Handler = OnToolCallAsync;
    }

    public ClaudeCodeLocator Locator => _locator;

    /// <summary>The live runs: their process, model, state and since when.</summary>
    public IReadOnlyList<ClaudeRunInfo> Runs()
    {
        lock (_gate)
            return _runs.Select(r => new ClaudeRunInfo(r.Id, r.Process.HasExited ? null : r.Process.Id, r.Model, r.State.ToString().ToLowerInvariant(), r.Since, r.Key.Split('\0')[0])).ToList();
    }

    /// <summary>The latest allowance the program said for an account's config directory (its <c>rate_limit_info</c>), if any.</summary>
    public JsonObject? LimitsOf(string configDir) => _limits.TryGetValue(configDir, out var l) ? (JsonObject)l.DeepClone() : null;

    // ------------------------------------------------------------------ a turn

    /// <summary>
    /// The events of the reply (Anthropic stream events, <c>message_start</c> … <c>message_stop</c>), with the caller's tool names as the caller
    /// declared them. A failure before the first event is a <see cref="ClaudeBridgeException"/> on the first read; the run is ended unless the
    /// reply was read to its end.
    /// </summary>
    public async IAsyncEnumerable<JsonObject> AnswerAsync(ClaudeRequest request, ClaudeTarget target, [EnumeratorCancellation] CancellationToken ct)
    {
        var run = await AcquireAsync(request, target, ct);
        var completed = false;
        try
        {
            await foreach (var ev in ReadReplyAsync(run, ct)) yield return ev;
            completed = true;
        }
        finally
        {
            if (!completed) await EndAsync(run);
        }
    }

    private async Task<ClaudeRun> AcquireAsync(ClaudeRequest request, ClaudeTarget target, CancellationToken ct)
    {
        var settings = _settings.Current.ClaudeBridge;
        var binary = _locator.Find() ?? throw new ClaudeBridgeException(503, "api_error",
            "找不到 claude 程序（Claude Code）。请先安装并登录它，或在设置里指定它的路径。");
        if (ClaudeProcess.ProxyProblem(target.Proxy) is { } proxyProblem) throw new ClaudeBridgeException(400, "invalid_request_error", proxyProblem);
        EnsureMaintenance();

        var key = target.Key;
        var model = request.Model;
        var matched = TryMatch(request, key, model);
        if (matched is not null)
        {
            var (run, parked) = matched.Value;
            try
            {
                if (parked)
                {
                    var results = request.ToolResults(run.Digests.Count);
                    foreach (var id in run.PendingIds)
                        run.Deliver(id, results.TryGetValue(id, out var r) ? r
                            : new ClaudeToolResult([(JsonNode)new JsonObject { ["type"] = "text", ["text"] = "The caller gave no result for this call." }], true));
                    run.Digests = [.. request.Digests];
                    run.PendingIds = [];
                    Mark(run, ClaudeRunState.Busy);
                    _logger.LogInformation("Claude bridge: run {Run} goes on with {Count} tool result(s)", run.Id, results.Count);
                }
                else
                {
                    run.Drain();
                    var since = run.Digests.Count;
                    run.Digests = [.. request.Digests];
                    Mark(run, ClaudeRunState.Busy);
                    await run.SendUserAsync(request.RenderSince(since), ct);
                    _logger.LogInformation("Claude bridge: run {Run} is told {Count} new message(s)", run.Id, request.Digests.Count - since);
                }
                return run;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException)
            {
                await EndAsync(run);
                // the process was gone after all: a new one is told the whole conversation
            }
        }

        var fresh = await StartAsync(request, target, binary.Path, settings, ct);
        fresh.Digests = [.. request.Digests];
        try
        {
            await fresh.SendUserAsync(request.RenderWhole(), ct);
        }
        catch
        {
            await EndAsync(fresh);
            throw;
        }
        _logger.LogInformation("Claude bridge: a new claude run {Run} ({Model}) is told the whole conversation: {Count} messages", fresh.Id, model, request.Digests.Count);
        return fresh;
    }

    /// <summary>The run that answered this conversation so far: idle for a next turn, or parked on the tool results this request carries.</summary>
    private (ClaudeRun Run, bool Parked)? TryMatch(ClaudeRequest request, string key, string model)
    {
        lock (_gate)
        {
            foreach (var run in _runs)
            {
                if (run.Key != key || run.Model != model || run.ToolsKey != request.ToolsKey || run.HasExited || run.Suspect) continue;
                if (run.State is not (ClaudeRunState.Idle or ClaudeRunState.Parked)) continue;
                if (request.Digests.Count <= run.Digests.Count || !request.Digests.Take(run.Digests.Count).SequenceEqual(run.Digests)) continue;
                if (run.State == ClaudeRunState.Parked)
                {
                    var results = request.ToolResults(run.Digests.Count);
                    if (!run.PendingIds.Any(results.ContainsKey))
                    {
                        // the caller went another way (a user's interruption): the run waits for results nobody will give
                        _ = EndAsync(run);
                        continue;
                    }
                    return (run, true);
                }
                return (run, false);
            }
        }
        return null;
    }

    private void Mark(ClaudeRun run, ClaudeRunState state)
    {
        run.State = state;
        run.Since = DateTimeOffset.UtcNow;
    }

    // ------------------------------------------------------------------ reading a reply

    private async IAsyncEnumerable<JsonObject> ReadReplyAsync(ClaudeRun run, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = false;
        var finished = false;
        var text = new StringBuilder();
        var toolIds = new List<string>();
        string? stop = null;
        string? synthetic = null;
        while (!finished)
        {
            var ev = await run.ReadAsync(ct);
            switch (ev["type"]?.GetValue<string>())
            {
                case "rate_limit_event":
                    if (ev["rate_limit_info"] is JsonObject info) NoteLimit(run, info);
                    break;
                case "assistant":
                    // a reply the program gave itself (a limit, a refusal) comes as an assistant message of the synthetic model and a failed result
                    if (!started && ev["message"] is JsonObject m && m["model"]?.GetValue<string>() == "<synthetic>")
                        synthetic = string.Concat((m["content"] as JsonArray ?? []).OfType<JsonObject>().Select(b => b["text"]?.GetValue<string>() ?? ""));
                    break;
                case "stream_event":
                    if (ev["event"] is not JsonObject inner) break;
                    var type = inner["type"]?.GetValue<string>();
                    if (type == "message_start")
                    {
                        // a second start in one reply is the program asking again after a cut stream: what the caller was told cannot be taken back
                        if (started) throw new ClaudeBridgeException(502, "api_error", "claude 的回复流中断后重新开始了，无法接续。");
                        started = true;
                        yield return inner;
                        break;
                    }
                    if (!started) break;
                    if (type == "content_block_start" && inner["content_block"] is JsonObject block && block["type"]?.GetValue<string>() == "tool_use")
                    {
                        if (block["name"]?.GetValue<string>() is { } name) block["name"] = DeclaredName(run, name);
                        if (block["id"]?.GetValue<string>() is { } id) toolIds.Add(id);
                    }
                    else if (type == "content_block_delta" && inner["delta"] is JsonObject delta && delta["type"]?.GetValue<string>() == "text_delta")
                        text.Append(delta["text"]?.GetValue<string>());
                    else if (type == "message_delta" && inner["delta"]?["stop_reason"]?.GetValue<string>() is { } reason)
                        stop = reason;
                    yield return inner;
                    if (type == "message_stop") finished = true;
                    break;
                case "result":
                    if (ev["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var isError) && isError) throw Classify(run, ev, synthetic);
                    if (!started)
                    {
                        // a program that streams nothing (no --include-partial-messages): the whole reply as one text
                        var whole = ev["result"]?.GetValue<string>() ?? "";
                        foreach (var synth in Synthesize(run.Model, whole, ev["usage"] as JsonObject)) yield return synth;
                        text.Append(whole);
                        stop = "end_turn";
                        started = true;
                    }
                    finished = true;
                    break;
                case "exit":
                    throw ExitError(run);
            }
        }

        var reply = ClaudeRequest.DigestOfReply(text.ToString(), toolIds);
        run.Digests = [.. run.Digests, reply];
        if (stop == "tool_use" && toolIds.Count > 0)
        {
            run.PendingIds = toolIds;
            Mark(run, ClaudeRunState.Parked);
            yield break;
        }
        // the result of the turn comes after its last message: wait for it, so the next turn does not read it as its own
        if (!await SettleAsync(run, ct)) run.Suspect = true;
        Mark(run, ClaudeRunState.Idle);
    }

    private static async Task<bool> SettleAsync(ClaudeRun run, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SettleWait);
        try
        {
            while (true)
            {
                var ev = await run.ReadAsync(cts.Token);
                switch (ev["type"]?.GetValue<string>())
                {
                    case "rate_limit_event": if (ev["rate_limit_info"] is JsonObject info) run.NoteLimit(info); break;
                    case "result": return true;
                    case "exit": return false;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void NoteLimit(ClaudeRun run, JsonObject info)
    {
        run.NoteLimit(info);
        _limits[run.Key.Split('\0')[0]] = (JsonObject)info.DeepClone();
    }

    /// <summary>The caller's name of a tool the run called (<c>mcp__astra__&lt;bridge name&gt;</c>).</summary>
    private static string DeclaredName(ClaudeRun run, string name)
    {
        if (!name.StartsWith(ClaudeNames.Prefix, StringComparison.Ordinal)) return name;
        var bridge = name[ClaudeNames.Prefix.Length..];
        return run.Declared.TryGetValue(bridge, out var declared) ? declared : bridge;
    }

    private static IEnumerable<JsonObject> Synthesize(string model, string text, JsonObject? usage)
    {
        yield return new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = "msg_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), ["type"] = "message", ["role"] = "assistant",
                ["model"] = model, ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = usage?["input_tokens"]?.DeepClone() ?? 0, ["output_tokens"] = 0 },
            },
        };
        yield return new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } };
        yield return new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = text } };
        yield return new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 };
        yield return new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = "end_turn", ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = usage?["output_tokens"]?.DeepClone() ?? 0 },
        };
        yield return new JsonObject { ["type"] = "message_stop" };
    }

    // ------------------------------------------------------------------ failures

    /// <summary>A failed result as the upstream's own error: a limit is 429 (with its reset), a sign-in problem 401, the rest what the program said.</summary>
    private ClaudeBridgeException Classify(ClaudeRun run, JsonObject result, string? synthetic)
    {
        var text = result["result"]?.GetValue<string>() is { Length: > 0 } r ? r : synthetic ?? "claude 返回了错误。";
        var status = result["api_error_status"] is JsonValue v && v.TryGetValue<int>(out var s) ? s : 0;
        return ClassifyText(text, status, ResetOf(run));
    }

    internal static ClaudeBridgeException ClassifyText(string text, int status, DateTimeOffset? resetsAt)
    {
        var lower = text.ToLowerInvariant();
        if (status == 429 || lower.Contains("hit your limit") || lower.Contains("usage limit") || lower.Contains("limit reached") || lower.Contains("rate limit"))
            return new ClaudeBridgeException(429, "rate_limit_error", text, resetsAt);
        if (status == 401 || lower.Contains("not logged in") || lower.Contains("/login") || lower.Contains("invalid api key")
            || lower.Contains("oauth token has expired") || lower.Contains("authentication_error"))
            return new ClaudeBridgeException(401, "authentication_error", "claude 未登录或登录已失效：" + text);
        if (status is 529 or 503 or 500 or 502 or 504) return new ClaudeBridgeException(status, "api_error", text);
        if (status is >= 400 and < 500) return new ClaudeBridgeException(status, "invalid_request_error", text);
        return new ClaudeBridgeException(502, "api_error", text);
    }

    private static DateTimeOffset? ResetOf(ClaudeRun run) =>
        run.LastRateLimit?["resetsAt"] is JsonValue v && v.TryGetValue<long>(out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;

    private static ClaudeBridgeException ExitError(ClaudeRun run)
    {
        var err = run.Stderr.Trim();
        var tail = err.Length > 600 ? err[^600..] : err;
        if (tail.Length == 0) return new ClaudeBridgeException(502, "api_error", $"claude 在回复前退出了（退出码 {run.ExitCode}）。");
        var classified = ClassifyText(tail, 0, ResetOf(run));
        return classified.Status == 502 ? new ClaudeBridgeException(502, "api_error", $"claude 在回复前退出了（退出码 {run.ExitCode}）：{tail}") : classified;
    }

    // ------------------------------------------------------------------ starting a run

    private async Task<ClaudeRun> StartAsync(ClaudeRequest request, ClaudeTarget target, string binary, ClaudeBridgeSettings settings, CancellationToken ct)
    {
        await TakeRoomAsync(Math.Clamp(settings.MaxRuns, 1, ClaudeBridgeSettings.MaxRunsLimit), ct);
        var id = "run" + Interlocked.Increment(ref _next);
        var tmp = Path.Combine(_root, id + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant());
        Directory.CreateDirectory(tmp);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        var psi = new ProcessStartInfo(binary)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = WorkDir(),
        };
        string mcpConfig;
        if (request.Tools.Count > 0)
        {
            var toolsPath = Path.Combine(tmp, "tools.json");
            var tools = new JsonArray();
            foreach (var t in request.Tools)
            {
                var def = new JsonObject { ["name"] = t.BridgeName, ["inputSchema"] = t.Schema.DeepClone() };
                if (t.Description is not null) def["description"] = t.Description;
                tools.Push(def);
            }
            await WriteSecretAsync(toolsPath, tools.ToJsonString(), ct);
            // the token goes in a file of the run's own, which only this user reads, never on claude's command line
            var callbackPath = Path.Combine(tmp, "callback");
            await WriteSecretAsync(callbackPath, _callbacks.BaseUrl + token, ct);
            var (command, args) = _helper();
            var helperArgs = new JsonArray();
            foreach (var a in args) helperArgs.Push(a);
            helperArgs.Push(ClaudeMcpHelper.Command);
            helperArgs.Push(callbackPath);
            helperArgs.Push(toolsPath);
            mcpConfig = new JsonObject { ["mcpServers"] = new JsonObject { [ClaudeNames.ServerName] = new JsonObject { ["command"] = command, ["args"] = helperArgs } } }.ToJsonString();
        }
        else
        {
            mcpConfig = """{"mcpServers":{}}""";
        }
        foreach (var a in LaunchArguments(request, mcpConfig)) psi.ArgumentList.Add(a);
        ClaudeProcess.ApplyEnvironment(psi, target.ConfigDir, target.Proxy, run: true);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("claude did not start");
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            TryDelete(tmp);
            throw new ClaudeBridgeException(503, "api_error", "无法启动 claude：" + e.Message);
        }
        var run = new ClaudeRun
        {
            Id = id, Process = process, Token = token, TempDir = tmp, Key = target.Key, ToolsKey = request.ToolsKey, Model = request.Model,
            Declared = request.DeclaredByBridgeName,
        };
        lock (_gate) _runs.Add(run);
        _byToken[token] = run;
        _ = Task.Run(() => PumpAsync(run));
        _ = Task.Run(() => PumpErrorsAsync(run));
        _logger.LogInformation("Claude bridge: started claude pid {Pid} for {Model}", process.Id, request.Model);
        return run;
    }

    internal static IEnumerable<string> LaunchArguments(ClaudeRequest request, string mcpConfig)
    {
        yield return "-p";
        yield return "--output-format"; yield return "stream-json";
        yield return "--input-format"; yield return "stream-json";
        yield return "--include-partial-messages";
        yield return "--verbose";
        yield return "--model"; yield return ClaudeRequest.RunName(request.Model);
        // none of claude's own tools: the only ones the run has are the caller's, through MCP
        yield return "--tools"; yield return "";
        yield return "--strict-mcp-config";
        yield return "--mcp-config"; yield return mcpConfig;
        // no settings of the user's or the project's (hooks, plugins, memory): the run is the caller's conversation alone
        yield return "--setting-sources"; yield return "";
        yield return "--settings"; yield return RunSettings;
        yield return "--dangerously-skip-permissions";
        // the run's history lives in the process
        yield return "--no-session-persistence";
        if (request.Effort is { } effort)
        {
            yield return "--effort"; yield return effort;
            yield return "--thinking-display"; yield return "summarized";
        }
        if (request.ThinkingOff)
        {
            yield return "--thinking"; yield return "disabled";
        }
    }

    private static async Task WriteSecretAsync(string path, string content, CancellationToken ct)
    {
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The folder every run works in: Claude Code tells its model the folder it runs in, and one shared folder keeps every conversation's prompt the same.</summary>
    private string WorkDir()
    {
        var dir = Path.Combine(_root, "work");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private async Task PumpAsync(ClaudeRun run)
    {
        try
        {
            string? line;
            while ((line = await run.Process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (line.Length == 0) continue;
                try
                {
                    if (JsonNode.Parse(line) is JsonObject ev) run.Push(ev);
                }
                catch (System.Text.Json.JsonException)
                {
                    run.AppendStderr(line);
                }
            }
            await run.Process.WaitForExitAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        var code = run.Process.HasExited ? run.Process.ExitCode : -1;
        run.State = ClaudeRunState.Ended;
        run.Finish(code);
        lock (_gate) _runs.Remove(run);
        _byToken.TryRemove(run.Token, out _);
        TryDelete(run.TempDir);
        _logger.LogInformation("Claude bridge: run {Run} ended (exit {Code})", run.Id, code);
    }

    private static async Task PumpErrorsAsync(ClaudeRun run)
    {
        try
        {
            string? line;
            while ((line = await run.Process.StandardError.ReadLineAsync()) is not null) run.AppendStderr(line);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ------------------------------------------------------------------ places

    /// <summary>A place among the processes (magpie's <c>claudeRoom</c>): the run idle longest is let go for it; else wait for one to end.</summary>
    private async Task TakeRoomAsync(int most, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + RoomWait;
        while (true)
        {
            ClaudeRun? victim = null;
            lock (_gate)
            {
                if (_runs.Count < most) return;
                victim = _runs.Where(r => r.State == ClaudeRunState.Idle).OrderBy(r => r.Since).FirstOrDefault();
                if (victim is not null) victim.State = ClaudeRunState.Ended;
            }
            if (victim is not null)
            {
                await EndAsync(victim);
                continue;
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new ClaudeBridgeException(503, "api_error", $"已有 {most} 个 claude 进程在运行，等待 {RoomWait.TotalSeconds:0} 秒仍没有空位。可以在设置里调大 claude 进程数。");
            await Task.Delay(250, ct);
        }
    }

    private async Task EndAsync(ClaudeRun run)
    {
        run.State = ClaudeRunState.Ended;
        try
        {
            run.Process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        await ClaudeProcess.StopAsync(run.Process, TimeSpan.FromSeconds(5));
    }

    private void EnsureMaintenance()
    {
        if (_maintenance is not null) return;
        lock (_gate) _maintenance ??= Task.Run(MaintainAsync);
    }

    /// <summary>Ends runs that waited too long: idle past the setting, parked past <see cref="ParkLimit"/>.</summary>
    private async Task MaintainAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var idle = TimeSpan.FromMinutes(Math.Clamp(_settings.Current.ClaudeBridge.IdleMinutes, 1, 120));
                List<ClaudeRun> expired;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    expired = _runs.Where(r => r.State == ClaudeRunState.Idle && now - r.Since > idle
                                               || r.State == ClaudeRunState.Parked && now - r.Since > ParkLimit).ToList();
                }
                foreach (var run in expired) await EndAsync(run);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ------------------------------------------------------------------ the caller's tool calls

    private async Task<(JsonArray Content, bool IsError)?> OnToolCallAsync(string token, string id, string name, JsonNode? arguments, CancellationToken ct)
    {
        if (!_byToken.TryGetValue(token, out var run)) return null;
        try
        {
            var result = await run.WaitForResultAsync(id, ct);
            return (result.Content, result.IsError);
        }
        catch (OperationCanceledException)
        {
            // the run ended, or claude gave the call up
            return ([(JsonNode)new JsonObject { ["type"] = "text", ["text"] = "The run ended before the caller gave a result." }], true);
        }
    }

    // ------------------------------------------------------------------ one-off asks

    /// <summary>
    /// Has <c>claude</c> answer one short message with none of its tools and nothing kept (magpie's <c>askClaude</c>): the warm-up's request, and the way to test
    /// that an account answers. Throws <see cref="ClaudeBridgeException"/> with what the program said when it fails.
    /// </summary>
    public async Task AskAsync(ClaudeTarget target, string model, string prompt, CancellationToken ct)
    {
        var binary = _locator.Find() ?? throw new ClaudeBridgeException(503, "api_error", "找不到 claude 程序（Claude Code）。");
        if (ClaudeProcess.ProxyProblem(target.Proxy) is { } problem) throw new ClaudeBridgeException(400, "invalid_request_error", problem);
        var psi = new ProcessStartInfo(binary.Path)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = WorkDir(),
        };
        foreach (var a in new[] { "-p", "--output-format", "json", "--model", model, "--tools", "", "--strict-mcp-config", "--setting-sources", "", "--settings", RunSettings, "--no-session-persistence" })
            psi.ArgumentList.Add(a);
        ClaudeProcess.ApplyEnvironment(psi, target.ConfigDir, target.Proxy, run: false);
        using var process = Process.Start(psi) ?? throw new ClaudeBridgeException(503, "api_error", "claude 没有启动。");
        await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        string output;
        try
        {
            output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ClaudeProcess.StopAsync(process, TimeSpan.FromSeconds(3));
            throw new ClaudeBridgeException(504, "api_error", "claude 两分钟内没有回答。");
        }
        // --output-format json prints one result object (an array of messages with --verbose)
        JsonObject? result = null;
        try
        {
            var parsed = JsonNode.Parse(output);
            result = parsed as JsonObject ?? (parsed as JsonArray)?.OfType<JsonObject>().LastOrDefault(o => o["type"]?.GetValue<string>() == "result");
        }
        catch (System.Text.Json.JsonException)
        {
        }
        if (result is not null && result["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var isError) && isError)
            throw ClassifyText(result["result"]?.GetValue<string>() ?? "claude 返回了错误。", result["api_error_status"] is JsonValue sv && sv.TryGetValue<int>(out var st) ? st : 0, null);
        if (process.ExitCode != 0)
        {
            var message = (await error).Trim();
            if (message.Length == 0) message = output.Trim();
            throw ClassifyText(message.Length == 0 ? $"claude 退出码 {process.ExitCode}" : message, 0, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        List<ClaudeRun> all;
        lock (_gate) all = [.. _runs];
        await Task.WhenAll(all.Select(EndAsync));
        _callbacks.Dispose();
        try
        {
            if (_maintenance is not null) await _maintenance;
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }
}

using System.Diagnostics;
using Astra.Core.Requests;

namespace Astra.Gateway.Tracing;

/// <summary>Where a request sits in its agent's loop, as <see cref="TraceLinker"/> decided.</summary>
public sealed record TraceLink(
    string TraceId, string TurnId, string? ParentRequestId, int Step, string CallKind, string LinkSource,
    string RequestId, string? SessionKey, string? ChainKey);

/// <summary>
/// Links consecutive calls of an agent into turns without the agent's help (.plan/observability.md §3.2): a request
/// answering tool calls continues the request that made them; a user message starts a new turn on the main chain, or —
/// on another chain of the same session — a sub-agent (while the main chain waits on tool calls) or a side call.
/// In-memory and bounded: what a restart forgets is only the exact links across it.
/// </summary>
public sealed class TraceLinker(TimeProvider? clock = null)
{
    /// <summary>Tool call ids remembered (oldest dropped first).</summary>
    public const int MaxToolCalls = 4096;

    /// <summary>Sessions remembered (least recently active dropped first).</summary>
    public const int MaxSessions = 1024;

    /// <summary>A session quiet for longer than this is forgotten.</summary>
    public static readonly TimeSpan SessionIdle = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TraceLink> _calls = new(StringComparer.Ordinal);
    private readonly Queue<string> _callOrder = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private int _sinceSweep;

    private sealed class Session
    {
        public string? MainChain;
        public TraceLink? LastMain;

        /// <summary>The main call whose tool calls are not all answered yet, and those ids.</summary>
        public TraceLink? Waiting;
        public HashSet<string> Pending { get; } = new(StringComparer.Ordinal);

        /// <summary>The latest call of every chain seen in the session.</summary>
        public Dictionary<string, TraceLink> Chains { get; } = new(StringComparer.Ordinal);
        public DateTimeOffset LastSeen;
    }

    /// <summary>
    /// Places a request that just arrived. <paramref name="inboundTraceId"/> is the agent's own trace id (from its
    /// traceparent): it names the trace of a turn the request opens.
    /// </summary>
    public TraceLink Link(string requestId, string? sessionKey, string? chainKey, LastMessage last, string? inboundTraceId)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (++_sinceSweep >= 256) Sweep(now);
            Session? session = null;
            if (sessionKey is not null && !_sessions.TryGetValue(sessionKey, out session))
            {
                if (_sessions.Count >= MaxSessions) DropOldestSession();
                _sessions[sessionKey] = session = new Session();
            }
            if (session is not null) session.LastSeen = now;

            var link = Decide(requestId, sessionKey, chainKey, last, inboundTraceId, session);
            if (session is not null)
            {
                foreach (var id in last.ToolResultIds) session.Pending.Remove(id);
                if (session.Pending.Count == 0) session.Waiting = null;
                if (chainKey is not null) session.Chains[chainKey] = link;
                if (link.CallKind == TraceCallKinds.Main)
                {
                    session.LastMain = link;
                    session.MainChain = chainKey ?? session.MainChain;
                }
            }
            return link;
        }
    }

    /// <summary>The response of <paramref name="link"/> is over: remember the tool calls it asked for.</summary>
    public void Complete(TraceLink link, IReadOnlyList<string> toolCallIds)
    {
        if (toolCallIds.Count == 0) return;
        lock (_lock)
        {
            foreach (var id in toolCallIds)
            {
                if (id.Length == 0) continue;
                if (_calls.TryAdd(id, link)) _callOrder.Enqueue(id);
                else _calls[id] = link;
            }
            while (_calls.Count > MaxToolCalls && _callOrder.TryDequeue(out var oldest)) _calls.Remove(oldest);
            if (link.CallKind == TraceCallKinds.Main && link.SessionKey is { } key && _sessions.TryGetValue(key, out var session))
            {
                session.Waiting = link;
                session.Pending.Clear();
                foreach (var id in toolCallIds) if (id.Length > 0) session.Pending.Add(id);
            }
        }
    }

    private TraceLink Decide(string requestId, string? sessionKey, string? chainKey, LastMessage last, string? inboundTraceId, Session? session)
    {
        // Exact: the request answers tool calls we saw being made.
        foreach (var id in last.ToolResultIds)
        {
            if (!_calls.TryGetValue(id, out var parent)) continue;
            return Continue(parent, TraceLinkSources.ToolCall);
        }

        if (!last.UserTurn)
        {
            // Tool results of calls we never saw (a restart, an evicted id) or an assistant prefill: the chain's latest call.
            if (session is not null && chainKey is not null && session.Chains.TryGetValue(chainKey, out var latest))
                return Continue(latest, TraceLinkSources.Session);
            return NewTurn();
        }

        if (session is null || session.LastMain is null || chainKey is null || session.MainChain is null || chainKey == session.MainChain)
            return NewTurn();

        // Another chain of the session starts: a sub-agent while the main agent waits on its tool calls, else a side call.
        if (session.Waiting is { } waiting && session.Pending.Count > 0)
            return new TraceLink(waiting.TraceId, waiting.TurnId, waiting.RequestId, 1, TraceCallKinds.Sidechain, TraceLinkSources.Inferred,
                requestId, sessionKey, chainKey);
        var main = session.LastMain;
        return new TraceLink(main.TraceId, main.TurnId, main.RequestId, 1, TraceCallKinds.Aux, TraceLinkSources.Inferred,
            requestId, sessionKey, chainKey);

        TraceLink Continue(TraceLink parent, string source) =>
            new(parent.TraceId, parent.TurnId, parent.RequestId, parent.Step + 1, parent.CallKind, source, requestId, sessionKey, chainKey ?? parent.ChainKey);

        TraceLink NewTurn() =>
            new(inboundTraceId ?? ActivityTraceId.CreateRandom().ToHexString(), requestId, null, 1, TraceCallKinds.Main, TraceLinkSources.NewTurn,
                requestId, sessionKey, chainKey);
    }

    private void Sweep(DateTimeOffset now)
    {
        _sinceSweep = 0;
        List<string>? idle = null;
        foreach (var (key, session) in _sessions)
            if (now - session.LastSeen > SessionIdle) (idle ??= []).Add(key);
        if (idle is not null) foreach (var key in idle) _sessions.Remove(key);
    }

    private void DropOldestSession()
    {
        string? oldest = null;
        var at = DateTimeOffset.MaxValue;
        foreach (var (key, session) in _sessions)
        {
            if (session.LastSeen >= at) continue;
            at = session.LastSeen;
            oldest = key;
        }
        if (oldest is not null) _sessions.Remove(oldest);
    }
}

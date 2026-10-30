using Astra.Api.Contracts;
using Astra.Core.Requests;

namespace Astra.Desktop.Core.Traces;

/// <summary>The path one request took, for highlighting it on the canvas: its client and every account row an attempt went to (<c>HighlightPath</c>).</summary>
public sealed record HighlightPath(string? ClientKind, IReadOnlyList<RequestAttempt> Attempts, string? ProviderId = null, string? AccountId = null);

/// <summary>What the topology needs beyond the aggregates and the live feed (<c>TopologyOptions</c>).</summary>
/// <param name="NowMs">The time now (Unix ms): how long ago an attempt failed, how long a switch note stays.</param>
/// <param name="Highlight">Dim everything this request did not touch.</param>
/// <param name="Activity">The latest stored requests (the activity feed): what a client's agent does between calls.</param>
public sealed record TopologyOptions(double NowMs, HighlightPath? Highlight = null, IReadOnlyList<TraceActivityDto>? Activity = null);

/// <summary>
/// Topology canvas (port of <c>buildTopology</c> / <c>withLive</c> / <c>hubNote</c> / <c>clientStatus</c> in <c>traces/model.ts</c>): client
/// and provider columns around the Astra hub. A provider's card lists its accounts (or its API key) as rows, and each row has its own line from
/// the hub, so a request is drawn on the account that serves it: going out while it is sent, coming back coloured by what the model produces,
/// bouncing back red when the upstream refused it and leaving for the next row (or provider) on an orange line when the gateway retries. Line
/// width follows volume; orange marks rate limits and failovers.
/// </summary>
public static class TopologyBuilder
{
    /// <summary>Id of the hub node (<c>HUB_ID</c>).</summary>
    public const string HubId = "astra";
    private const double Row = 100;

    /// <summary>Provider card: header height (<c>PROVIDER_HEAD</c>), then one row per account (<c>ACCOUNT_ROW</c>).</summary>
    public const double ProviderHead = 50;

    /// <summary>One account row's pitch on a provider card (<c>ACCOUNT_ROW</c>).</summary>
    public const double AccountRowPitch = 34;
    private const double ProviderGap = 22;

    /// <summary>How long a failed attempt keeps bouncing on its row (<c>FAIL_SHOW_MS</c>).</summary>
    public const double FailShowMs = 1800;

    /// <summary>How long a switch stays written above the hub (<c>NOTE_SHOW_MS</c>; a plain route note stays half as long).</summary>
    public const double NoteShowMs = 2600;

    /// <summary>Node id of a client's card (<c>clientNodeId</c>); a null kind is "direct".</summary>
    public static string ClientNodeId(string? kind) => $"client:{kind ?? "direct"}";

    /// <summary>Node id of a provider's card (<c>providerNodeId</c>).</summary>
    public static string ProviderNodeId(string id) => $"provider:{id}";

    /// <summary>The handle (and edge suffix) of one account row; <c>a:key</c> for a provider called with its API key (<c>accountRowId</c>).</summary>
    public static string AccountRowId(string? accountId) => string.IsNullOrEmpty(accountId) ? "a:key" : $"a:{accountId}";

    /// <summary>The attempt a live request is on now (its last one); null for a feed without attempts (<c>currentAttempt</c>).</summary>
    public static RequestAttempt? CurrentAttempt(RequestSummaryDto r) => r.Attempts is { Count: > 0 } a ? a[^1] : null;

    private static readonly HashSet<string> SwitchReasons = ["rate_limited_failover", "auth_failover", "auth_refresh", "provider_failover", "compression_rejected"];

    /// <summary>The short <c>traces.switch.*</c> label of a retry reason (<c>switchReason</c>); null for none or one without a label.</summary>
    public static string? SwitchReason(string? retry, TraceText t) => retry is not null && SwitchReasons.Contains(retry) ? t("traces.switch." + retry) : null;

    /// <summary>An attempt that did not answer: refused by the upstream, or failed before it could (<c>attemptFailed</c>).</summary>
    public static bool AttemptFailed(RequestAttempt a) =>
        a.EndMs is not null && ((a.Status ?? 0) >= 400 || ((a.Status ?? 0) == 0 && !string.IsNullOrEmpty(a.Error)) || !string.IsNullOrEmpty(a.Retry));

    private static double StartOf(RequestSummaryDto r) => r.StartedAtUtc.ToUnixTimeMilliseconds();

    /// <summary>
    /// Builds the canvas. <paramref name="live"/> (the live request feed) puts every request in flight on its lines and adds the client or
    /// provider it uses when the window has no traffic of theirs yet.
    /// </summary>
    public static TraceGraph Build(TraceTopologyDto source, TraceText t, Func<string?, string> clientLabel,
        IReadOnlyDictionary<string, ProviderBrand> brands, IReadOnlyList<RequestSummaryDto>? live, TopologyOptions options)
    {
        live ??= [];
        var data = WithLive(source, live, brands);
        var now = options.NowMs;
        var pending = live.Where(r => r.Status == "pending").ToList();
        var pendingIds = pending.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        double max = 1;
        foreach (var c in data.Clients) max = Math.Max(max, c.Requests);
        foreach (var p in data.Providers) max = Math.Max(max, p.Requests);
        double Width(double requests) => 1.5 + 3.5 * Math.Sqrt(Math.Min(1, requests / max));
        var liveRange = data.Range == "live";
        string Label(double perMinute, long requests) =>
            liveRange
                ? t("traces.topology.perMinute", ("count", perMinute < 10 ? perMinute.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : TraceFormat.Num(TraceFormat.JsRound(perMinute))))
                : t("traces.topology.count", ("count", requests));

        // Which row each live request is on, and the rows where an attempt of it just failed.
        List<string> RowsOf(TraceProviderNodeDto p) => p.Accounts.Count > 0 ? p.Accounts.Select(a => AccountRowId(a.Id)).ToList() : [AccountRowId(null)];
        var providerById = new Dictionary<string, TraceProviderNodeDto>(StringComparer.Ordinal);
        foreach (var p in data.Providers) providerById.TryAdd(p.ProviderId, p);
        string? RowFor(string? providerId, string? accountId)
        {
            if (providerId is null || !providerById.TryGetValue(providerId, out var p)) return null;
            var rows = RowsOf(p);
            var wanted = AccountRowId(accountId);
            return rows.Contains(wanted) ? wanted : rows[0];
        }
        var on = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var failed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var retrying = new HashSet<string>(StringComparer.Ordinal);
        static void Push(Dictionary<string, List<string>> map, string key, string id)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(id);
        }
        foreach (var r in live)
        {
            var attempts = r.Attempts ?? [];
            var current = attempts.Count > 0 ? attempts[^1] : null;
            var providerId = current is not null ? current.ProviderId ?? r.ProviderId : r.ProviderId;
            var row = RowFor(providerId, current is not null ? current.AccountId : r.AccountId);
            // A finished request still rides its line (its burst); a pending one only once it has a provider.
            if (row is not null && !string.IsNullOrEmpty(providerId) && (r.Status != "pending" || current is null || !AttemptFailed(current)))
                Push(on, $"{providerId}|{row}", r.Id);
            for (var i = 0; i < attempts.Count; i++)
            {
                var a = attempts[i];
                var at = a.ProviderId ?? r.ProviderId;
                var failedRow = RowFor(at, a.AccountId);
                if (string.IsNullOrEmpty(at) || failedRow is null) continue;
                if (AttemptFailed(a) && now - (StartOf(r) + (a.EndMs ?? 0)) < FailShowMs) Push(failed, $"{at}|{failedRow}", r.Id);
                if (i > 0 && r.Status == "pending" && a.EndMs is null && (a.Status ?? 0) == 0) retrying.Add($"{at}|{failedRow}");
            }
        }

        // Dimming: a highlighted request lights its client and the rows its attempts went to.
        var hl = options.Highlight;
        HashSet<string>? hlRows = null;
        if (hl is not null)
        {
            var path = hl.Attempts.Count > 0 ? hl.Attempts.Select(a => (a.ProviderId, a.AccountId)).ToList() : [(hl.ProviderId, hl.AccountId)];
            hlRows = path.Where(a => !string.IsNullOrEmpty(a.ProviderId)).Select(a => $"{a.ProviderId}|{RowFor(a.ProviderId, a.AccountId) ?? ""}")
                .ToHashSet(StringComparer.Ordinal);
        }
        var hlClient = hl is not null ? ClientNodeId(hl.ClientKind) : null;

        double ProviderHeight(TraceProviderNodeDto p) => ProviderHead + RowsOf(p).Count * AccountRowPitch + 10;
        var clientsHeight = data.Clients.Count * Row - (Row - 60);
        var providersHeight = data.Providers.Sum(ProviderHeight) + Math.Max(0, data.Providers.Count - 1) * ProviderGap;
        var height = Math.Max(Math.Max(clientsHeight, providersHeight), 60);

        var nodes = new List<TraceNode> { new(HubId, NodeKind.Hub, 404, height / 2 - 48, Busy: pending.Count > 0) { Note = HubNoteOf(live, now, data, t) } };
        var edges = new List<TraceEdge>();
        var clientTop = (height - clientsHeight) / 2;
        for (var i = 0; i < data.Clients.Count; i++)
        {
            var c = data.Clients[i];
            var id = ClientNodeId(c.ClientKind);
            var mine = pending.Count(r => r.ClientKind == c.ClientKind);
            var dim = hlClient is not null && hlClient != id;
            nodes.Add(new TraceNode(id, NodeKind.Card, 0, clientTop + i * Row, 184, Card: new CardData
            {
                Title = c.ClientKind is not null ? clientLabel(c.ClientKind) : t("traces.topology.direct"),
                Lines = [c.ActiveSessions > 0 ? t("traces.topology.activeSessions", ("count", c.ActiveSessions)) : t("traces.topology.idle")],
                Tone = c.ActiveSessions > 0 || mine > 0 ? Traces.Tone.Ok : Traces.Tone.Idle,
                Client = c.ClientKind,
                Live = mine,
                Status = ClientStatus(c.ClientKind, live, options.Activity ?? [], now, t),
                Dim = dim,
            }));
            edges.Add(new TraceEdge
            {
                Id = $"e:{id}", Source = id, Target = HubId, Type = EdgeType.Flow, Tone = EdgeTone.Flow, Arrow = false,
                Label = Label(c.PerMinute, c.Requests), StrokeWidth = Width(c.Requests) + (hlClient == id ? 1.5 : 0), Opacity = dim ? 0.25 : null,
                Flow = new FlowData("client", live.Where(r => r.ClientKind == c.ClientKind).Select(r => r.Id).ToList(), false),
            });
        }

        var y = (height - providersHeight) / 2;
        foreach (var p in data.Providers)
        {
            var id = ProviderNodeId(p.ProviderId);
            var cooling = p.Accounts.Count(a => a.Status == "cooldown");
            var warn = p.RateLimited > 0 || p.Failovers > 0 || cooling > 0;
            var failing = p.Requests > 0 && (double)p.Errors / p.Requests > 0.2;
            var brand = brands.TryGetValue(p.ProviderId, out var b) ? b : new ProviderBrand(p.Name, p.Icon, p.TemplateId);
            var rowIds = RowsOf(p);
            var multi = p.Accounts.Count > 1;
            var rows = rowIds.Select((rowId, k) =>
            {
                var account = k < p.Accounts.Count ? p.Accounts[k] : null;
                var key = $"{p.ProviderId}|{rowId}";
                var riding = on.TryGetValue(key, out var list) ? list : [];
                var hit = failed.TryGetValue(key, out var bad) && bad.Count > 0 ? "fail" : riding.Any(pendingIds.Contains) ? "on" : null;
                return new AccountRow(rowId, account?.Id, account is not null ? account.Name : t("traces.topology.apiKey"), account?.Status ?? "ok",
                    account is not null ? AccountDetail(account, now, t) : p.Attempts > 0 ? t("traces.account.attempts", ("count", p.Attempts)) : t("traces.account.ready"),
                    account is not null ? account.Attempts : p.Attempts, account is not null ? account.Errors : p.Errors,
                    riding.Count(pendingIds.Contains), hit, hlRows is not null && !hlRows.Contains(key));
            }).ToList();
            var line = string.Join(" · ", new[]
            {
                p.Accounts.Count > 0 ? t("traces.topology.accounts", ("count", p.Accounts.Count)) : t("traces.topology.apiKey"),
                cooling > 0 ? t("traces.topology.cooling", ("count", cooling)) : null,
                multi ? Label(p.PerMinute, p.Requests) : null,
            }.Where(x => !string.IsNullOrEmpty(x)));
            nodes.Add(new TraceNode(id, NodeKind.Provider, 760, y, 248, Card: new CardData
            {
                Title = p.Name,
                Lines = [line],
                Tone = failing ? Traces.Tone.Error : warn ? Traces.Tone.Warn : Traces.Tone.Ok,
                Provider = brand,
                Live = pending.Count(r => (CurrentAttempt(r)?.ProviderId ?? r.ProviderId) == p.ProviderId),
                Dim = hlRows is not null && !rows.Any(r => !r.Dim),
            }) { Rows = rows });
            y += ProviderHeight(p) + ProviderGap;
            foreach (var row in rows)
            {
                var key = $"{p.ProviderId}|{row.Id}";
                var volume = p.Accounts.Count > 0 ? row.Attempts : p.Requests;
                var rowWarn = row.Status == "cooldown" || row.Hit == "fail";
                var tone = failing ? EdgeTone.Error : warn && (rowWarn || !multi) ? EdgeTone.Warn : EdgeTone.Flow;
                var isHl = hlRows?.Contains(key) ?? false;
                edges.Add(new TraceEdge
                {
                    Id = $"e:{id}:{row.Id}", Source = HubId, Target = id, TargetRow = row.Id, Type = EdgeType.Flow, Arrow = false, Tone = tone,
                    Label = multi ? null : Label(p.PerMinute, p.Requests), StrokeWidth = Width(volume) + (isHl ? 1.5 : 0),
                    Opacity = hlRows is not null && !isHl ? 0.25 : null,
                    Flow = new FlowData("account", on.TryGetValue(key, out var riding) ? riding : [], warn)
                    {
                        Failed = failed.TryGetValue(key, out var bad) ? bad : [], Retry = retrying.Contains(key),
                    },
                });
            }
        }
        return new TraceGraph(nodes, edges);
    }

    /// <summary>What an account row says on its right: why it is out, or what the upstream last reported of its quota (<c>accountDetail</c>).</summary>
    private static string AccountDetail(TraceAccountDto a, double now, TraceText t)
    {
        if (a.Status == "cooldown" && a.CooldownUntilUtc is { } until)
        {
            var left = until.ToUnixTimeMilliseconds() - now;
            return left is > 0 and < 3_600_000
                ? t("traces.account.cooldownIn", ("time", TraceFormat.Ms(Math.Ceiling(left / 1000) * 1000)))
                : t("traces.account.cooldown", ("time", until.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (a.Status != "ok") return t("traces.account." + a.Status);
        if (!string.IsNullOrEmpty(a.RemainingRequests)) return t("traces.account.remaining", ("count", a.RemainingRequests));
        return a.Attempts > 0 ? t("traces.account.attempts", ("count", a.Attempts)) : t("traces.account.ready");
    }

    /// <summary>The words above the hub: the switch a request just made (why, and where to), else where a request was just sent (<c>hubNote</c>).</summary>
    public static HubNote? HubNoteOf(IReadOnlyList<RequestSummaryDto> live, double now, TraceTopologyDto data, TraceText t)
    {
        (string Provider, string? Account) NameOf(string? providerId, string? accountId)
        {
            var p = data.Providers.FirstOrDefault(x => x.ProviderId == providerId);
            var a = !string.IsNullOrEmpty(accountId) ? p?.Accounts.FirstOrDefault(x => x.Id == accountId) : null;
            return (p?.Name ?? providerId ?? "", a?.Name);
        }
        double? bestAt = null;
        HubNote? best = null;
        foreach (var r in live)
        {
            var start = StartOf(r);
            var attempts = r.Attempts ?? [];
            for (var i = 0; i < attempts.Count; i++)
            {
                var a = attempts[i];
                var next = i + 1 < attempts.Count ? attempts[i + 1] : null;
                if (next is not null && AttemptFailed(a))
                {
                    var at = start + (a.EndMs ?? next.StartMs);
                    if (now - at > NoteShowMs || (bestAt is { } b && b > at)) continue;
                    var to = NameOf(next.ProviderId, next.AccountId);
                    var same = (next.ProviderId ?? r.ProviderId) == (a.ProviderId ?? r.ProviderId);
                    var reason = SwitchReason(a.Retry, t)
                        ?? (a.Status is { } s and not 0 ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : t("traces.live.failed"));
                    bestAt = at;
                    best = new HubNote(t("traces.hub.switch", ("reason", reason), ("target", same && to.Account is not null ? to.Account : to.Provider)), "warn");
                }
                else if (i == 0)
                {
                    var at = start + a.StartMs;
                    if (now - at > NoteShowMs / 2 || (bestAt is { } b && b >= at)) continue;
                    var to = NameOf(a.ProviderId ?? r.ProviderId, a.AccountId);
                    bestAt = at;
                    best = new HubNote(t("traces.hub.route", ("target", to.Account is not null ? $"{to.Provider} · {to.Account}" : to.Provider)), "info");
                }
            }
        }
        return best;
    }

    /// <summary>
    /// What a client's agent is doing, on its card: the call in flight (what the model produces), else — for a moment after a call that asked
    /// for tools — running those tools (<c>clientStatus</c>).
    /// </summary>
    private static CardStatus? ClientStatus(string? kind, IReadOnlyList<RequestSummaryDto> live, IReadOnlyList<TraceActivityDto> activity, double now, TraceText t)
    {
        var mine = live.Where(r => r.ClientKind == kind).ToList();
        var running = mine.Where(r => r.Status == "pending").OrderByDescending(r => r.StartedAtUtc).FirstOrDefault();
        if (running is not null)
        {
            var stage = running.Stage ?? "";
            if (stage.StartsWith("tool:", StringComparison.Ordinal)) return new CardStatus(t("traces.agent.tool", ("name", stage["tool:".Length..])), "tool");
            if (stage == "thinking") return new CardStatus(t("traces.agent.thinking"), "think");
            if (stage == "writing") return new CardStatus(t("traces.agent.writing"), "write");
            if (CurrentAttempt(running) is { } current && AttemptFailed(current)) return new CardStatus(t("traces.agent.retrying"), "warn");
            return new CardStatus(running.HttpStatus is { } h and not 0 ? t("traces.agent.waiting") : t("traces.agent.upstream"), "wait");
        }
        // The latest finished call of this client, from the live feed (not yet stored) or the activity feed.
        var finished = mine.Where(r => r.Status != "pending")
            .Select(r => (End: StartOf(r) + (r.TotalMs ?? 0), Ok: r.Status == "success", Tools: LiveTools(r.Phases)))
            .Concat(activity.Where(a => a.Request.ClientKind == kind && a.Request.CallKind != "aux").Select(a =>
                (End: StartOf(a.Request) + (a.Request.TotalMs ?? 0), Ok: a.Request.Status == "success",
                    Tools: a.FinishReason == "tool_calls" || a.ToolCallCount > 0 ? a.ToolCalls.Select(c => c.Name).ToList() : [])))
            .OrderByDescending(x => x.End)
            .FirstOrDefault();
        if (finished.Tools is null || now - finished.End > 120_000 || !finished.Ok || finished.Tools.Count == 0) return null;
        return new CardStatus(t("traces.agent.tools", ("names", TraceFormat.ToolSummary(finished.Tools))), "run");
    }

    /// <summary>Tool names a live call produced (its <c>tool:&lt;name&gt;</c> phases; <c>liveTools</c>).</summary>
    public static List<string> LiveTools(string? phases) =>
        (phases ?? "").Split(',')
            .Select(p => p.LastIndexOf('@') is var at and >= 0 ? p[..at] : p[..Math.Max(0, p.Length - 1)])
            .Where(s => s.StartsWith("tool:", StringComparison.Ordinal))
            .Select(s => s["tool:".Length..])
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>The aggregates plus a zero row for every client / provider an in-flight request uses that the window has not seen (<c>withLive</c>).</summary>
    public static TraceTopologyDto WithLive(TraceTopologyDto data, IReadOnlyList<RequestSummaryDto> live, IReadOnlyDictionary<string, ProviderBrand> brands)
    {
        var clients = new List<TraceClientNodeDto>(data.Clients);
        var providers = new List<TraceProviderNodeDto>(data.Providers);
        foreach (var r in live)
        {
            if (!clients.Any(c => c.ClientKind == r.ClientKind))
                clients.Add(new TraceClientNodeDto(r.ClientKind, 0, 0, 0, 0));
            var used = new List<string>();
            foreach (var id in new[] { r.ProviderId }.Concat((r.Attempts ?? []).Select(a => a.ProviderId)))
                if (!string.IsNullOrEmpty(id) && !used.Contains(id)) used.Add(id);
            foreach (var providerId in used)
            {
                if (providers.Any(p => p.ProviderId == providerId)) continue;
                brands.TryGetValue(providerId, out var brand);
                var named = (r.Attempts ?? []).FirstOrDefault(a => a.ProviderId == providerId)?.ProviderName;
                providers.Add(new TraceProviderNodeDto(providerId, brand?.Name ?? named ?? (providerId == r.ProviderId ? r.ProviderName : null) ?? providerId,
                    brand?.Icon, brand?.TemplateId, 0, 0, 0, 0, 0, 0, null, []));
            }
        }
        return clients.Count == data.Clients.Count && providers.Count == data.Providers.Count
            ? data
            : data with { Clients = clients, Providers = providers };
    }
}

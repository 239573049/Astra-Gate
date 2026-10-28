using Astra.Api.Contracts;
using Astra.Core.Requests;
using static Astra.Desktop.Core.Traces.SpanAttrs;
using static Astra.Desktop.Core.Traces.TraceAnalysis;

namespace Astra.Desktop.Core.Traces;

/// <summary>Where an inspector link goes; the page turns it into navigation (<see cref="TraceRoutes"/> holds the web paths).</summary>
public sealed record TraceRoute(string Path);

/// <summary>A link under an inspector.</summary>
public sealed record InspectorAction(string Label, TraceRoute Route);

/// <summary>A monospace block (headers, rewrites, warnings) with a title.</summary>
public sealed record InspectorBlock(string Title, IReadOnlyList<string> Lines);

/// <summary>A provider account line of the topology inspector.</summary>
public sealed record AccountLine(string Name, string Text, bool Muted);

/// <summary>The details of one trace node (<c>InspectorProps</c>): a kind caption, an icon hint, title, subtitle, key/value rows and blocks.</summary>
public sealed record InspectorModel
{
    /// <summary>Caption above the title.</summary>
    public string? Kind { get; init; }
    /// <summary>Client kind whose icon to show.</summary>
    public string? ClientIcon { get; init; }
    /// <summary>Provider whose icon to show.</summary>
    public ProviderBrand? ProviderIcon { get; init; }
    /// <summary>Title.</summary>
    public required string Title { get; init; }
    /// <summary>Subtitle.</summary>
    public string? Sub { get; init; }
    /// <summary>Label / value rows; values are already formatted text.</summary>
    public IReadOnlyList<(string Label, string Value)> Rows { get; init; } = [];
    /// <summary>A code block (headers, warnings).</summary>
    public InspectorBlock? Code { get; init; }
    /// <summary>Everything the span recorded, folded away under the summary.</summary>
    public InspectorBlock? Raw { get; init; }
    /// <summary>Links.</summary>
    public IReadOnlyList<InspectorAction> Actions { get; init; } = [];
    /// <summary>Provider account list (topology provider inspector).</summary>
    public IReadOnlyList<AccountLine> Accounts { get; init; } = [];
}

/// <summary>The web routes of the traces views (<c>/traces</c>, <c>/traces/sessions/:key</c>, <c>/traces/requests/:id</c>) and the tab they imply.</summary>
public static class TraceRoutes
{
    // JS encodeURIComponent leaves !'()* alone; Uri.EscapeDataString does not.
    private static string Enc(string s) => Uri.EscapeDataString(s).Replace("%21", "!").Replace("%27", "'").Replace("%28", "(").Replace("%29", ")").Replace("%2A", "*");

    /// <summary>Percent-encodes one path segment or query value the way JS <c>encodeURIComponent</c> does.</summary>
    public static string EncodeSegment(string s) => Enc(s);

    /// <summary>The three tabs of the Traces page.</summary>
    public enum Tab { Topology, Sessions, Requests }

    /// <summary>A request's trace.</summary>
    public static TraceRoute Request(string id) => new($"/traces/requests/{Enc(id)}");

    /// <summary>A session, optionally at one turn.</summary>
    public static TraceRoute Session(string key, string? turnId = null) =>
        new($"/traces/sessions/{Enc(key)}{(string.IsNullOrEmpty(turnId) ? "" : $"?turn={Enc(turnId)}")}");

    /// <summary>The sessions list, optionally filtered to a client.</summary>
    public static TraceRoute Sessions(string? client = null) =>
        new($"/traces/sessions{(string.IsNullOrEmpty(client) ? "" : $"?client={Enc(client)}")}");

    /// <summary>The tab of a path (<c>TracesPage</c>): sessions, requests, else topology.</summary>
    public static Tab TabOf(string pathname) =>
        pathname.StartsWith("/traces/sessions", StringComparison.Ordinal) ? Tab.Sessions
        : pathname.StartsWith("/traces/requests", StringComparison.Ordinal) ? Tab.Requests
        : Tab.Topology;

    /// <summary>The path a tab opens (<c>navigate(...)</c> of the page's segmented control).</summary>
    public static string PathOf(Tab tab) => tab == Tab.Topology ? "/traces" : $"/traces/{tab.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Inspector content of the three traces views. Ports of <c>NodeInspector</c> (request.tsx), <c>TopologyInspector</c>
/// (topology.tsx) and <c>CallInspector</c> (session.tsx). Dates go through <paramref name="formatDateTime"/> (the web's
/// locale-aware <c>formatDateTime</c>), which stays a UI concern.
/// </summary>
public static class Inspectors
{
    private static string Opt(string? s) => s ?? TraceFormat.Dash;
    private static string N(double? v) => v is { } x ? TraceFormat.Num(x) : TraceFormat.Dash;

    /// <summary>Everything a span recorded (attributes, then events at their offsets), for the folded block (<c>rawOf</c>).</summary>
    public static InspectorBlock? RawOf(IReadOnlyList<SpanDto?> spans, TraceText t)
    {
        var lines = new List<string>();
        foreach (var s in spans)
        {
            if (s is null) continue;
            if (spans.Count > 1) lines.Add($"# {s.Name} · +{TraceFormat.Us(s.StartUs)} · {TraceFormat.Us(s.DurationUs)}");
            lines.AddRange(Entries(s).OrderBy(kv => kv.Key, StringComparer.InvariantCulture).Select(kv => $"{kv.Key} = {kv.Value}"));
            foreach (var e in Events(s))
                lines.Add($"@ +{TraceFormat.Us(e.OffsetUs)} {e.Name}" + (e.Attributes is { } a
                    ? " " + string.Join(" ", a.Select(kv => $"{kv.Key}={JsString(kv.Value)}")) : ""));
            if (s.Status == TraceSpanStatus.Error && !string.IsNullOrEmpty(s.StatusMessage)) lines.Add($"! {s.StatusMessage}");
        }
        return lines.Count > 0 ? new InspectorBlock(t("traces.kv.raw"), lines) : null;
    }

    private static InspectorBlock? RawOf(TraceText t, params SpanDto?[] spans) => RawOf(spans, t);

    /// <summary>The inspector of a node of a request canvas, or null for an unknown id.</summary>
    public static InspectorModel? ForRequestNode(string id, TraceRequestDto data, TraceText t, Func<DateTimeOffset, string> formatDateTime,
        Func<string?, string> clientLabel, ProviderBrand? brand)
    {
        var r = data.Request;
        // Resolve / encode repeat per member a provider pool tries: show the last one (what answered).
        SpanDto? Span(string name) => LastByName(data.Spans, name);
        string Stage(string name) => t("traces.stage." + name);
        string Dur(SpanDto? s) => TraceFormat.Us(s?.DurationUs);
        string Yes(bool? v) => v == true ? t("traces.yes") : v == false ? t("traces.no") : TraceFormat.Dash;
        // when the step began, from the moment the request arrived
        (string, string) At(SpanDto? s) => (t("traces.kv.startAt"), s is not null ? $"+{TraceFormat.Us(s.StartUs)}" : TraceFormat.Dash);
        var root = Span(TraceSpanNames.Request);
        string Count(double? n) => n is null ? TraceFormat.Dash : TraceFormat.Num(n.Value);
        // JS `attr(...)` printed by yes(): true / false pass through, everything else is "—".
        bool? Flag(SpanDto? s, string key)
        {
            var kind = Attr(s, key) is System.Text.Json.Nodes.JsonValue v ? v.GetValueKind() : System.Text.Json.JsonValueKind.Undefined;
            return kind == System.Text.Json.JsonValueKind.True ? true : kind == System.Text.Json.JsonValueKind.False ? false : null;
        }
        var decision = RouteDecisionOf(data.Spans);

        switch (id)
        {
            case "agent":
            {
                var sys = Num(root, "astra.request.system_chars");
                var max = Num(root, "gen_ai.request.max_tokens");
                var actions = new List<InspectorAction>();
                if (data.Parent is not null) actions.Add(new InspectorAction($"← {t("traces.request.parent")}", TraceRoutes.Request(data.Parent.Id)));
                foreach (var c in data.Children) actions.Add(new InspectorAction($"{t("traces.request.child")} →", TraceRoutes.Request(c.Id)));
                return new InspectorModel
                {
                    Kind = t("traces.kind.agent"),
                    ClientIcon = r.ClientKind,
                    Title = !string.IsNullOrEmpty(r.ClientKind) ? clientLabel(r.ClientKind) : t("traces.topology.direct"),
                    Sub = formatDateTime(r.StartedAtUtc),
                    Rows =
                    [
                        (t("traces.kv.session"), r.SessionKey ?? TraceFormat.Dash),
                        (t("traces.kv.step"), r.Step is { } st and not 0 ? $"{t("traces.kind." + (r.CallKind ?? "main"))} · #{st}" : TraceFormat.Dash),
                        (t("traces.kv.link"), t("traces.link." + (r.LinkSource ?? "new_turn"))),
                        (t("traces.kv.protocols"), $"{r.InboundProtocol}{(r.Stream ? " · stream" : "")}"),
                        (t("traces.kv.model"), Opt(r.RequestedModel)),
                        (t("traces.kv.messages"), Count(Num(root, "astra.request.messages"))),
                        (t("traces.kv.toolDefs"), Count(Num(root, "astra.request.tools"))),
                        (t("traces.kv.systemChars"), sys is not null ? t("traces.unit.chars", ("count", TraceFormat.Tokens(sys))) : TraceFormat.Dash),
                        (t("traces.kv.maxTokens"), max is not null ? TraceFormat.Tokens(max) : TraceFormat.Dash),
                        (t("traces.kv.reasoning"), JoinNonEmpty(" · ", r.ReasoningEffort, r.ReasoningMode) is { Length: > 0 } rs ? rs : TraceFormat.Dash),
                        (t("traces.kv.traceparent"), !string.IsNullOrEmpty(r.ParentSpanId) ? $"{Head(r.TraceId, 8)}… / {r.ParentSpanId}" : TraceFormat.Dash),
                        (t("traces.kv.requestId"), r.Id),
                    ],
                    Raw = RawOf(t, root),
                    Actions = actions,
                };
            }
            case "route":
            {
                var route = Span(TraceSpanNames.Route);
                var body = Span(TraceSpanNames.Body);
                return new InspectorModel
                {
                    Kind = Stage(TraceSpanNames.Route),
                    Title = t("traces.node.entry"),
                    Sub = Dur(route),
                    Rows =
                    [
                        At(route),
                        (t("traces.kv.token"), Opt(Str(route, "astra.token.id"))),
                        (t("traces.kv.client"), Str(route, "astra.client.kind") ?? t("traces.topology.direct")),
                        (t("traces.kv.bytes"), Bytes(Num(body, "astra.body.bytes"))),
                        (t("traces.kv.bodyTime"), Dur(body)),
                    ],
                    Raw = RawOf(t, route, body),
                    Code = route?.Status == TraceSpanStatus.Error
                        ? new InspectorBlock(t("traces.kv.status"), [r.ErrorMessage ?? route.StatusMessage ?? ""]) : null,
                };
            }
            case "middleware":
            {
                var m = Span(TraceSpanNames.Middleware);
                return new InspectorModel
                {
                    Kind = Stage(TraceSpanNames.Middleware), Title = t("traces.node.middleware"), Sub = Dur(m),
                    Rows =
                    [
                        At(m),
                        (t("traces.kv.rewritten"), Yes(Flag(m, "astra.middleware.rewritten"))),
                        (t("traces.kv.bytes"), $"{Bytes(Num(m, "astra.body.bytes_before"))} → {Bytes(Num(m, "astra.body.bytes"))}"),
                    ],
                    Raw = RawOf(t, m),
                };
            }
            case "privacy":
            {
                var p = Span(TraceSpanNames.Privacy);
                return new InspectorModel
                {
                    Kind = Stage(TraceSpanNames.Privacy), Title = t("traces.node.privacy"), Sub = Dur(p),
                    Rows =
                    [
                        At(p),
                        (t("traces.kv.hits"), N(Num(p, "astra.privacy.hits") ?? 0)),
                        (t("traces.kv.categories"), Opt(Str(p, "astra.privacy.categories"))),
                        (t("traces.kv.rewritten"), Yes(Flag(p, "astra.privacy.rewritten"))),
                        (t("traces.kv.restored"), N(Num(p, "astra.privacy.restorable") ?? 0)),
                        (t("traces.kv.dryRun"), Yes(Flag(p, "astra.privacy.dry_run"))),
                    ],
                    Raw = RawOf(t, p),
                };
            }
            case "routing":
            {
                if (decision is null) return null;
                var reason = RouteReasonLabel(decision.FallbackReason, t);
                var judge = JoinNonEmpty(" · ", decision.JudgeProtocol, decision.JudgeModel);
                var routeLines = Entries(decision.Span).Where(kv => kv.Key.StartsWith("astra.route.", StringComparison.Ordinal))
                    .OrderBy(kv => kv.Key, StringComparer.InvariantCulture).Select(kv => $"{kv.Key} = {kv.Value}").ToList();
                var rows = new List<(string, string)>
                {
                    (t("traces.kv.startAt"), $"+{TraceFormat.Us(decision.Span.StartUs)}"),
                    (t("traces.kv.routeSource"), RouteSourceLabel(decision.Source, t)),
                    (t("traces.kv.routeMode"), RouteModeLabel(decision.Mode, t)),
                    (t("traces.kv.model"), Opt(decision.Model)),
                };
                if (decision.Confidence is { } conf) rows.Add((t("traces.kv.routeConfidence"), $"{TraceFormat.Num(TraceFormat.JsRound(conf * 100))}%"));
                if (!string.IsNullOrEmpty(reason)) rows.Add((t("traces.kv.routeFallback"), reason));
                if (!string.IsNullOrEmpty(decision.AdjustedFrom)) rows.Add((t("traces.kv.routeAdjusted"), decision.AdjustedFrom));
                if (judge.Length > 0) rows.Add((t("traces.kv.routeJudge"), judge));
                if (decision.JudgeInput is not null || decision.JudgeOutput is not null)
                    rows.Add((t("traces.kv.routeJudgeTokens"), $"{N(decision.JudgeInput ?? 0)} / {N(decision.JudgeOutput ?? 0)}"));
                if (decision.NewConnection) rows.Add((t("traces.kv.routeNewConnection"), t("traces.yes")));
                return new InspectorModel
                {
                    Kind = t("traces.node.routing"),
                    Title = !string.IsNullOrEmpty(decision.Grade) ? decision.Grade : t("traces.node.routing"),
                    Sub = decision.JudgeMs > 0 ? TraceFormat.Us(TraceFormat.JsRound(decision.JudgeMs * 1000)) : RouteSourceLabel(decision.Source, t),
                    Rows = rows,
                    Raw = routeLines.Count > 0 ? new InspectorBlock(t("traces.kv.raw"), routeLines) : null,
                };
            }
            case "resolve":
            {
                var s = Span(TraceSpanNames.Resolve);
                // The judge runs inside this span; the routing node owns that time, so here it starts after the judge.
                var judged = JudgeUsOn(s, decision);
                var rows = new List<(string, string)> { (t("traces.kv.startAt"), s is not null ? $"+{TraceFormat.Us(s.StartUs + judged)}" : TraceFormat.Dash) };
                if (decision is not null) rows.Add((t("traces.node.routing"), RouteSummary(decision, t)));
                rows.Add((t("traces.kv.provider"), Str(s, "astra.provider.name") ?? r.ProviderName ?? TraceFormat.Dash));
                rows.Add((t("traces.kv.model"), Str(s, "gen_ai.request.model") ?? r.RequestedModel ?? TraceFormat.Dash));
                rows.Add((t("traces.kv.protocols"), Opt(Str(s, "astra.protocol.upstream"))));
                rows.Add((t("traces.kv.capabilities"), TraceFormat.Us(Num(s, "astra.resolve.capabilities_us"))));
                return new InspectorModel
                {
                    Kind = Stage(TraceSpanNames.Resolve), Title = t("traces.node.resolve"),
                    Sub = s is not null ? TraceFormat.Us(Math.Max(0, s.DurationUs - judged)) : Dur(s),
                    Rows = rows, Raw = RawOf(t, s),
                };
            }
            case "encode":
            {
                var e = Span(TraceSpanNames.Encode);
                var warnings = Str(e, "astra.encode.warnings");
                var rows = new List<(string, string)>
                {
                    At(e),
                    (t("traces.kv.protocols"), $"{Str(e, "astra.protocol.inbound") ?? r.InboundProtocol} → {Str(e, "astra.protocol.upstream") ?? r.UpstreamProtocol ?? TraceFormat.Dash}"),
                    (t("traces.kv.model"), $"{Opt(Str(e, "gen_ai.request.model"))} → {Opt(Str(e, "astra.upstream.model"))}"),
                    (t("traces.kv.rewritten"), Yes(Flag(e, "astra.encode.rewritten"))),
                    (t("traces.kv.lossy"), N(Num(e, "astra.encode.lossy") ?? 0)),
                    (t("traces.kv.bytes"), Bytes(Num(e, "astra.body.bytes"))),
                };
                if (!string.IsNullOrEmpty(Str(e, "astra.claude.mode"))) rows.Add((t("traces.kv.claudeMode"), Str(e, "astra.claude.mode")!));
                return new InspectorModel
                {
                    Kind = Stage(TraceSpanNames.Encode),
                    Title = IsTrue(e, "astra.passthrough") ? t("traces.node.passthrough") : t("traces.node.encode"),
                    Sub = Dur(e), Rows = rows,
                    Code = !string.IsNullOrEmpty(warnings) ? new InspectorBlock(t("traces.kv.lossy"), warnings.Split("; ")) : null,
                    Raw = RawOf(t, e),
                };
            }
            case "provider":
                return new InspectorModel
                {
                    Kind = t("traces.kind.provider"),
                    ProviderIcon = brand,
                    Title = brand?.Name ?? r.ProviderName ?? TraceFormat.Dash,
                    Sub = r.UpstreamProtocol,
                    Rows =
                    [
                        (t("traces.kv.model"), !string.IsNullOrEmpty(r.ResponseModel) && r.ResponseModel != r.UpstreamModel
                            ? $"{Opt(r.UpstreamModel)} → {r.ResponseModel}" : r.UpstreamModel ?? r.ResponseModel ?? TraceFormat.Dash),
                        (t("traces.kv.finish"), Opt(r.FinishReason)),
                        (t("traces.kv.tokens"), $"{TraceFormat.Tokens(r.TotalInputTokens, true)} / {TraceFormat.Tokens(r.TotalOutputTokens, true)}"),
                        (t("traces.kv.cacheRead"), TraceFormat.Tokens(r.CacheReadTokens, true)),
                        (t("traces.kv.reasoningTokens"), TraceFormat.Tokens(r.ReasoningTokens, true)),
                        (t("traces.kv.ttfb"), r.TtfbMs is { } and not 0 ? TraceFormat.Ms(r.TtfbMs) : TraceFormat.Dash),
                        (t("traces.kv.generation"), r.GenerationMs is { } and not 0 ? TraceFormat.Ms(r.GenerationMs) : TraceFormat.Dash),
                        (t("traces.kv.speed"), r.OutputTps is { } tps and not 0 ? $"{tps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} tok/s" : TraceFormat.Dash),
                        (t("traces.kv.upstreamId"), Opt(r.UpstreamRequestId)),
                        (t("traces.kv.cost"), TraceFormat.Nanos(r.CostNanoUsd)),
                    ],
                };
            case "decode" or "reencode" or "restore" or "middlewareResponse":
            {
                var s = Span(TraceSpanNames.Response);
                var phases = CallPhasesOf(data.Spans);
                var reasoningChars = Num(s, "astra.response.reasoning_chars");
                var textChars = Num(s, "astra.response.text_chars");
                return new InspectorModel
                {
                    Kind = Stage(TraceSpanNames.Response),
                    Title = t("traces.node." + id),
                    Sub = Dur(s),
                    Rows =
                    [
                        At(s),
                        (t("traces.kv.firstToken"), r.TtftMs is { } and not 0 ? TraceFormat.Ms(r.TtftMs) : TraceFormat.Dash),
                        (t("traces.kv.phases"), phases is not null ? PhaseDetail(phases, t) : TraceFormat.Dash),
                        (t("traces.kv.chars"), reasoningChars is not null || textChars is not null
                            ? $"{TraceFormat.Tokens(reasoningChars ?? 0)} / {TraceFormat.Tokens(textChars ?? 0)}" : TraceFormat.Dash),
                        (t("traces.kv.toolCalls"), phases is { Tools.Count: > 0 } ? TraceFormat.ToolSummary(phases.Tools) : N(Num(s, "astra.response.tool_calls") ?? 0)),
                        (t("traces.kv.finish"), Str(s, "gen_ai.response.finish_reasons") ?? r.FinishReason ?? TraceFormat.Dash),
                        (t("traces.kv.reads"), N(Num(s, "astra.response.reads") ?? 0)),
                        (t("traces.kv.events"), N(Num(s, "astra.response.events") ?? 0)),
                        (t("traces.kv.maxGap"), TraceFormat.Ms(Num(s, "astra.response.max_gap_ms") ?? 0)),
                        (t("traces.kv.restored"), N(Num(s, "astra.restore.placeholders") ?? 0)),
                        (t("traces.kv.protocols"), $"{r.UpstreamProtocol ?? TraceFormat.Dash} → {Str(s, "astra.protocol.client") ?? r.InboundProtocol}"),
                    ],
                    Code = s?.Status == TraceSpanStatus.Error && !string.IsNullOrEmpty(s.StatusMessage)
                        ? new InspectorBlock(t("traces.kv.status"), [s.StatusMessage]) : null,
                    Raw = RawOf(t, s),
                };
            }
        }
        if (!id.StartsWith("attempt:", StringComparison.Ordinal)) return null;
        // Number('x') is NaN in JS and indexes nothing.
        if (!int.TryParse(id["attempt:".Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || index < 0) return null;
        var attempts = Attempts(data.Spans);
        if (index >= attempts.Count) return null;
        var a = attempts[index];
        var headers = Entries(a).Where(kv => kv.Key.StartsWith("astra.ratelimit.", StringComparison.Ordinal))
            .Select(kv => $"{kv.Key["astra.ratelimit.".Length..]}: {kv.Value}").ToList();
        var retry = Str(a, "astra.attempt.retry");
        var arows = new List<(string, string)>
        {
            At(a),
            (t("traces.kv.http"), N(Num(a, "http.response.status_code"))),
            (t("traces.kv.ttfb"), TraceFormat.Us(a.DurationUs)),
            (t("traces.kv.credentials"), $"{TraceFormat.Us(Num(a, "astra.credentials_us"))}{(IsTrue(a, "astra.credentials.refreshed") ? $" · {t("traces.kv.refreshed")}" : "")}"),
            (t("traces.kv.retry"), !string.IsNullOrEmpty(retry) ? t("traces.retry." + retry) : TraceFormat.Dash),
            (t("traces.kv.upstreamId"), Opt(Str(a, "astra.upstream.request_id"))),
        };
        if (Num(a, "astra.upstream.processing_ms") is { } pm) arows.Add((t("traces.kv.processing"), TraceFormat.Ms(pm)));
        return new InspectorModel
        {
            Kind = Stage(TraceSpanNames.Attempt),
            Title = $"{t("traces.node.attempt", ("n", index + 1))} · {Str(a, "astra.provider.name") ?? Str(a, "astra.account.id") ?? TraceFormat.Dash}",
            Sub = $"{N(Num(a, "http.response.status_code"))} · {TraceFormat.Us(a.DurationUs)}",
            Rows = arows,
            Code = headers.Count > 0 ? new InspectorBlock(t("traces.kv.rateLimitHeaders"), headers)
                : !string.IsNullOrEmpty(a.StatusMessage) && a.Status == TraceSpanStatus.Error ? new InspectorBlock(t("traces.kv.status"), [a.StatusMessage]) : null,
            Raw = RawOf(t, a),
        };
    }

    /// <summary>The inspector of a topology node (hub, <c>client:KIND</c>, <c>provider:ID</c>), or null.</summary>
    public static InspectorModel? ForTopologyNode(string id, TraceTopologyDto data, TraceText t, Func<DateTimeOffset, string> formatDateTime,
        Func<string?, string> clientLabel, IReadOnlyDictionary<string, ProviderBrand> brands)
    {
        if (id == TopologyBuilder.HubId)
        {
            var g = data.Gateway;
            var rows = new List<(string, string)>
            {
                (t("traces.kv.requests"), N(g.Requests)), (t("traces.kv.errors"), N(g.Errors)),
                (t("traces.kv.overheadP50"), TraceFormat.Us(g.OverheadP50Us)), (t("traces.kv.overheadP95"), TraceFormat.Us(g.OverheadP95Us)),
                (t("traces.kv.failovers"), N(g.Failovers)),
            };
            rows.AddRange(g.Stages.Where(s => s.Samples > 0)
                .Select(s => (t("traces.stage." + s.Name), $"{TraceFormat.Us(s.P50Us)} / {TraceFormat.Us(s.P95Us)}")));
            return new InspectorModel
            {
                Kind = t("traces.kind.gateway"), Title = t("traces.topology.gateway"),
                Sub = t("traces.topology.inFlightCount", ("count", g.InFlight)), Rows = rows,
                Code = new InspectorBlock(t("traces.topology.overheadNoteTitle"), [t("traces.topology.overheadNote")]),
            };
        }
        if (id.StartsWith("client:", StringComparison.Ordinal))
        {
            var kind = id["client:".Length..];
            var c = data.Clients.FirstOrDefault(x => (x.ClientKind ?? "direct") == kind);
            if (c is null) return null;
            return new InspectorModel
            {
                Kind = t("traces.kind.agent"), ClientIcon = c.ClientKind,
                Title = !string.IsNullOrEmpty(c.ClientKind) ? clientLabel(c.ClientKind) : t("traces.topology.direct"),
                Rows =
                [
                    (t("traces.kv.requests"), N(c.Requests)), (t("traces.kv.errors"), N(c.Errors)),
                    (t("traces.kv.rate"), c.PerMinute.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)),
                    (t("traces.kv.activeSessions"), N(c.ActiveSessions)),
                ],
                Actions = [new InspectorAction($"{t("traces.viewSessions")} →", TraceRoutes.Sessions(c.ClientKind))],
            };
        }
        var p = data.Providers.FirstOrDefault(x => $"provider:{x.ProviderId}" == id);
        if (p is null) return null;
        brands.TryGetValue(p.ProviderId, out var brand);
        var accounts = p.Accounts.Select(a =>
        {
            var text = a.Status == "cooldown" && a.CooldownUntilUtc is { } until
                ? t("traces.account.cooldown", ("time", formatDateTime(until)))
                : t("traces.account." + a.Status);
            if (!string.IsNullOrEmpty(a.RemainingRequests)) text += $" · {t("traces.account.remaining", ("count", a.RemainingRequests))}";
            return new AccountLine(a.Name, text, a.Status == "ok");
        }).ToList();
        return new InspectorModel
        {
            Kind = t("traces.kind.provider"),
            ProviderIcon = new ProviderBrand(p.Name, brand?.Icon ?? p.Icon, brand?.TemplateId ?? p.TemplateId),
            Title = p.Name,
            Rows =
            [
                (t("traces.kv.requests"), N(p.Requests)), (t("traces.kv.errors"), N(p.Errors)), (t("traces.kv.attempts"), N(p.Attempts)),
                (t("traces.kv.rateLimited"), N(p.RateLimited)), (t("traces.kv.failovers"), N(p.Failovers)),
                (t("traces.kv.ttfbP95"), TraceFormat.Ms(p.TtfbP95Ms)),
            ],
            Accounts = accounts,
        };
    }

    /// <summary>
    /// The inspector of a call (or the tool gap before it, <c>tool:ID</c>, or the whole turn, <c>turn</c>) in a session; null when
    /// the id matches nothing.
    /// </summary>
    public static InspectorModel? ForSessionNode(string id, IReadOnlyList<TraceCallDto> calls, IReadOnlyList<SpanDto> spans, TraceText t,
        IReadOnlyDictionary<string, ProviderBrand> brands)
    {
        var by = SpansByRequest(spans);
        if (id == "turn")
        {
            var time = SplitTurnTime(calls, spans);
            return new InspectorModel
            {
                Title = t("traces.session.turnRow"),
                Rows =
                [
                    (t("traces.kv.calls"), N(calls.Count)),
                    (t("traces.session.time.model"), TraceFormat.Ms(time.ModelMs)),
                    (t("traces.session.time.tools"), TraceFormat.Ms(time.ToolMs)),
                    (t("traces.session.time.retries"), TraceFormat.Ms(time.RetryMs)),
                    (t("traces.kv.cost"), TraceFormat.Nanos(calls.Sum(c => (double)c.CostNanoUsd))),
                ],
            };
        }
        if (id.StartsWith("tool:", StringComparison.Ordinal))
        {
            var childId = id["tool:".Length..];
            var child = calls.FirstOrDefault(c => c.Id == childId);
            // the call that asked for the tools: the linked parent, else the main call just before
            TraceCallDto? parent = null;
            if (child is not null)
            {
                if (!string.IsNullOrEmpty(child.ParentRequestId)) parent = calls.FirstOrDefault(c => c.Id == child.ParentRequestId);
                parent ??= calls.Where(c => c.CallKind is not (TraceCallKinds.Sidechain or TraceCallKinds.Aux) && StartMs(c) < StartMs(child))
                    .OrderByDescending(StartMs).FirstOrDefault();
            }
            if (child is null || parent is null) return null;
            var names = CallPhasesOf(by.GetValueOrDefault(parent.Id) ?? [])?.Tools ?? [];
            var gap = StartMs(child) - (StartMs(parent) + (parent.TotalMs ?? 0));
            return new InspectorModel
            {
                Kind = t("traces.kind.tool"),
                Title = names.Count > 0 ? TraceFormat.ToolSummary(names) : t("traces.session.toolCount", ("count", parent.ToolCalls)),
                Rows =
                [
                    (t("traces.kv.toolCalls"), N(parent.ToolCalls)),
                    (t("traces.kv.gap"), TraceFormat.Ms(Math.Max(0, gap))),
                    (t("traces.kv.link"), t("traces.link." + (child.LinkSource ?? "new_turn"))),
                ],
                Code = new InspectorBlock(t("traces.session.toolNoteTitle"), [t("traces.session.toolNote")]),
            };
        }
        var call = calls.FirstOrDefault(c => c.Id == id);
        if (call is null) return null;
        var own = by.GetValueOrDefault(call.Id) ?? [];
        var attempts = own.Where(s => s.Name == TraceSpanNames.Attempt).ToList();
        var routed = RouteDecisionOf(own);
        brands.TryGetValue(call.ProviderId ?? "", out var brand);
        var phases = CallPhasesOf(own);
        var response = own.FirstOrDefault(s => s.Name == TraceSpanNames.Response);
        var reasoningChars = Num(response, "astra.response.reasoning_chars");
        var textChars = Num(response, "astra.response.text_chars");
        var rows = new List<(string, string)>
        {
            (t("traces.kv.status"), call.HttpStatus is { } hs and not 0 ? $"{call.Status} · {hs}" : call.Status),
        };
        if (routed is not null) rows.Add((t("traces.node.routing"), RouteSummary(routed, t)));
        rows.Add((t("traces.kv.duration"), $"{TraceFormat.Ms(call.TotalMs)}{(call.TtftMs is { } ttft and not 0 ? $" · {t("traces.kv.firstToken")} {TraceFormat.Ms(ttft)}" : "")}"));
        rows.Add((t("traces.kv.tokens"), $"{TraceFormat.Tokens(call.TotalInputTokens, true)} / {TraceFormat.Tokens(call.TotalOutputTokens, true)}"));
        if (phases is not null) rows.Add((t("traces.kv.phases"), PhaseDetail(phases, t)));
        if (!string.IsNullOrEmpty(call.ReasoningEffort) || !string.IsNullOrEmpty(call.ReasoningMode))
            rows.Add((t("traces.kv.reasoning"), JoinNonEmpty(" · ", call.ReasoningEffort, call.ReasoningMode)));
        if (reasoningChars is not null || textChars is not null) rows.Add((t("traces.kv.chars"), $"{N(reasoningChars ?? 0)} / {N(textChars ?? 0)}"));
        if (phases is { Tools.Count: > 0 }) rows.Add((t("traces.kv.tools"), TraceFormat.ToolSummary(phases.Tools)));
        rows.Add((t("traces.kv.finish"), Opt(call.FinishReason)));
        var attemptText = string.Join(" → ", attempts.Select(a => $"{Str(a, "astra.account.id") ?? TraceFormat.Dash} {(Attr(a, "http.response.status_code") is { } v ? JsString(v) : "")}"));
        rows.Add((t("traces.kv.attempts"), attemptText.Length > 0 ? attemptText : TraceFormat.Dash));
        rows.Add((t("traces.kv.link"), t("traces.link." + (call.LinkSource ?? "new_turn"))));
        rows.Add((t("traces.kv.cost"), TraceFormat.Nanos(call.CostNanoUsd)));
        return new InspectorModel
        {
            Kind = t("traces.kind." + (call.CallKind ?? "main")),
            ProviderIcon = brand,
            Title = $"LLM #{call.Step ?? 1} · {TraceFormat.ShortModel(call.UpstreamModel ?? call.RequestedModel)}",
            Sub = JoinNonEmpty(" · ", call.ProviderName, call.AccountName ?? call.AccountId),
            Rows = rows,
            Actions = [new InspectorAction($"{t("traces.openRequest")} →", TraceRoutes.Request(call.Id))],
        };
    }

    private static string Bytes(double? n) => n is null ? TraceFormat.Dash : $"{TraceFormat.Tokens(n)} B";

    private static string Head(string? s, int n) => s is null ? "undefined" : s.Length <= n ? s : s[..n];

    private static string JoinNonEmpty(string sep, params string?[] parts) => string.Join(sep, parts.Where(p => !string.IsNullOrEmpty(p)));
}

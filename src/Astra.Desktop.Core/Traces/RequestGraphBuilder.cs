using Astra.Api.Contracts;
using Astra.Core.Requests;
using static Astra.Desktop.Core.Traces.SpanAttrs;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// One request as a canvas: the request leg left to right (entry ... attempts, provider) and the response leg back underneath.
/// Port of <c>buildRequest</c> in <c>traces/model.ts</c>. Node ids are the ones the inspector switches on
/// (<c>agent route middleware privacy routing resolve encode attempt:N provider provider:ID decode reencode restore middlewareResponse</c>).
/// </summary>
public static class RequestGraphBuilder
{
    private const double Pitch = 170;

    /// <summary>Builds the canvas for a trace request.</summary>
    public static TraceGraph Build(TraceRequestDto trace, TraceText t, Func<string?, string> clientLabel, ProviderBrand? brand)
    {
        var r = trace.Request;
        var spans = trace.Spans;
        SpanDto? ByName(string name) => spans.FirstOrDefault(s => s.Name == name);
        // A provider-pool failover resolves and encodes once per member tried: the last ones describe what answered.
        SpanDto? LastByName(string name) => TraceAnalysis.LastByName(spans, name);
        var route = ByName(TraceSpanNames.Route);
        var body = ByName(TraceSpanNames.Body);
        var middleware = ByName(TraceSpanNames.Middleware);
        var privacy = ByName(TraceSpanNames.Privacy);
        var resolve = LastByName(TraceSpanNames.Resolve);
        var encode = LastByName(TraceSpanNames.Encode);
        var response = ByName(TraceSpanNames.Response);
        var attempts = TraceAnalysis.Attempts(spans);
        static Tone SpanTone(SpanDto? s) => s?.Status == TraceSpanStatus.Error ? Tone.Error : Tone.Neutral;
        var nodes = new List<TraceNode>();
        var edges = new List<TraceEdge>();
        const double rowY = 100;

        nodes.Add(new TraceNode("agent", NodeKind.Card, -56, rowY, 168, Card: new CardData
        {
            Title = !string.IsNullOrEmpty(r.ClientKind) ? clientLabel(r.ClientKind) : t("traces.topology.direct"),
            Lines = [$"{r.InboundProtocol}{(r.Stream ? " · stream" : "")}"],
            Client = r.ClientKind,
        }));
        var chain = new List<(string Id, double X)>();
        double x = 170;
        void Stage(string id, SpanDto? span, string title, string[] lines, Tone? toneOverride = null)
        {
            if (span is null) return;
            nodes.Add(new TraceNode(id, NodeKind.Card, x, rowY, 140, Card: new CardData { Title = title, Lines = lines, Tone = toneOverride ?? SpanTone(span) }));
            chain.Add((id, x));
            x += Pitch;
        }
        if (route is not null)
        {
            var entry = route with { DurationUs = route.DurationUs + (body?.DurationUs ?? 0) };
            Stage("route", entry, t("traces.node.entry"), [t("traces.node.entryDetail"), TraceFormat.Us(entry.DurationUs)]);
        }
        Stage("middleware", middleware, t("traces.node.middleware"),
            [Truthy(middleware, "astra.middleware.rewritten") ? t("traces.node.rewritten") : t("traces.node.unchanged"), TraceFormat.Us(middleware?.DurationUs)]);
        Stage("privacy", privacy, t("traces.node.privacy"),
            [t("traces.node.hits", ("count", Num(privacy, "astra.privacy.hits") ?? 0)), TraceFormat.Us(privacy?.DurationUs)]);
        // Smart routing is attributes on the resolve span, not a span of its own (plan 5.9). Drawn between the guard and
        // the resolve, and its judge time is taken back out of the resolve node so the two don't both claim it.
        var decision = TraceAnalysis.RouteDecisionOf(spans);
        if (decision is not null)
        {
            var sourceLabel = TraceAnalysis.RouteSourceLabel(decision.Source, t);
            var warn = decision.Source is "fallback" or "breaker" || !string.IsNullOrEmpty(decision.AdjustedFrom);
            Stage("routing", decision.Span, t("traces.node.routing"),
                [decision.Grade ?? TraceFormat.Dash, decision.JudgeMs > 0 ? $"{sourceLabel} · {TraceFormat.Us(TraceFormat.JsRound(decision.JudgeMs * 1000))}" : sourceLabel],
                warn ? Tone.Warn : Tone.Ok);
        }
        var judged = TraceAnalysis.JudgeUsOn(resolve, decision);
        Stage("resolve", resolve, t("traces.node.resolve"),
            [Str(resolve, "astra.provider.name") ?? r.ProviderName ?? TraceFormat.Dash,
             TraceFormat.Us(resolve is null ? null : Math.Max(0, resolve.DurationUs - judged))]);
        var passthrough = IsTrue(encode, "astra.passthrough") || r.Passthrough;
        Stage("encode", encode, passthrough ? t("traces.node.passthrough") : t("traces.node.encode"),
            [passthrough ? (r.UpstreamProtocol ?? r.InboundProtocol) : $"→ {r.UpstreamProtocol ?? TraceFormat.Dash}", TraceFormat.Us(encode?.DurationUs)]);

        var center = rowY + 32;
        var attemptX = x + 20;
        var providerX = attemptX + 200;
        var providerHeight = Math.Max(150, attempts.Count * 80);
        // Attempts of a provider pool can go to several providers (issue #2): the one that answered is the main node, the
        // others failed over and sit above it.
        string? AttemptProvider(SpanDto a) => Str(a, "astra.provider.id") ?? r.ProviderId;
        var others = attempts.Select(AttemptProvider).Where(p => !string.IsNullOrEmpty(p) && p != r.ProviderId).Select(p => p!).Distinct().ToList();
        string ProviderNode(SpanDto a)
        {
            var p = AttemptProvider(a);
            return !string.IsNullOrEmpty(p) && p != r.ProviderId ? $"provider:{p}" : "provider";
        }
        for (var k = 0; k < others.Count; k++)
        {
            var p = others[k];
            var named = attempts.FirstOrDefault(a => AttemptProvider(a) == p);
            nodes.Add(new TraceNode($"provider:{p}", NodeKind.Card, providerX, center - providerHeight / 2 - 96 * (k + 1), 168, Card: new CardData
            {
                Title = Str(named, "astra.provider.name") ?? p,
                Lines = [t("traces.node.failedOver")],
                Tone = Tone.Error,
            }));
        }
        for (var i = 0; i < attempts.Count; i++)
        {
            var a = attempts[i];
            var y = center - 30 + (i - (attempts.Count - 1) / 2.0) * 80;
            var status = Num(a, "http.response.status_code");
            var failed = a.Status == TraceSpanStatus.Error;
            nodes.Add(new TraceNode($"attempt:{i}", NodeKind.Card, attemptX, y, 136, Card: new CardData
            {
                Title = t("traces.node.attempt", ("n", i + 1)),
                Lines =
                [
                    (others.Count > 0 ? Str(a, "astra.provider.name") : null) ?? Str(a, "astra.account.id") ?? TraceFormat.Dash,
                    $"{(status is null ? TraceFormat.Dash : TraceFormat.Num(status.Value))} · {TraceFormat.Us(a.DurationUs)}",
                ],
                Tone = failed ? Tone.Error : Tone.Ok,
            }));
            var lastStage = chain.Count > 0 ? chain[^1].Id : "agent";
            edges.Add(new TraceEdge { Id = $"e:fork:{i}", Source = lastStage, Target = $"attempt:{i}", Tone = EdgeTone.Request });
            edges.Add(new TraceEdge
            {
                Id = $"e:attempt:{i}", Source = $"attempt:{i}", Target = ProviderNode(a), Tone = failed ? EdgeTone.Error : EdgeTone.Ok,
                Label = status is { } s && s != 0 ? TraceFormat.Num(s) : null, Dashed = failed,
            });
        }
        nodes.Add(new TraceNode("provider", NodeKind.Card, providerX, center - providerHeight / 2, 168, providerHeight, new CardData
        {
            Title = brand?.Name ?? r.ProviderName ?? TraceFormat.Dash,
            Lines = new[]
            {
                r.UpstreamModel ?? r.RequestedModel ?? TraceFormat.Dash,
                !string.IsNullOrEmpty(r.FinishReason) ? $"finish: {r.FinishReason}" : "",
                !string.IsNullOrEmpty(r.UpstreamRequestId) ? $"req-id …{Last(r.UpstreamRequestId, 6)}" : "",
            }.Where(l => l.Length > 0).ToList(),
            Provider = brand,
            Stacked = true,
            Tone = r.Status == "success" ? Tone.Neutral : Tone.Error,
        }));
        // request leg: agent -> stages
        var legs = new List<string> { "agent" };
        legs.AddRange(chain.Select(c => c.Id));
        for (var i = 0; i + 1 < legs.Count; i++)
            edges.Add(new TraceEdge { Id = $"e:req:{legs[i]}", Source = legs[i], Target = legs[i + 1], Tone = EdgeTone.Request });
        if (attempts.Count == 0 && chain.Count > 0)
            edges.Add(new TraceEdge { Id = "e:req:provider", Source = chain[^1].Id, Target = "provider", Tone = EdgeTone.Request });

        // response leg, right to left under the stages it mirrors
        const double backY = 340;
        var back = new List<string>();
        void ResponseNode(string id, double atX, string title, string[] lines)
        {
            nodes.Add(new TraceNode(id, NodeKind.Card, atX, backY, 136, Card: new CardData { Title = title, Lines = lines, Tone = Tone.Neutral }));
            back.Add(id);
        }
        if (response is not null)
        {
            ResponseNode("decode", attemptX, t("traces.node.decode"),
                [t("traces.node.maxGap", ("ms", TraceFormat.Ms(Num(response, "astra.response.max_gap_ms") ?? 0))), TraceFormat.Us(response.DurationUs)]);
            double? At(string id) { foreach (var c in chain) if (c.Id == id) return c.X; return null; }
            if (!passthrough) ResponseNode("reencode", At("encode") ?? attemptX - Pitch, t("traces.node.reencode"), [$"→ {r.InboundProtocol}"]);
            var restored = Num(response, "astra.restore.placeholders") ?? 0;
            if (restored > 0) ResponseNode("restore", At("privacy") ?? attemptX - 2 * Pitch, t("traces.node.restore"), [t("traces.node.restored", ("count", restored))]);
            if (IsTrue(response, "astra.middleware.response"))
                ResponseNode("middlewareResponse", At("middleware") ?? 170, t("traces.node.middlewareResponse"), [t("traces.node.unchanged")]);
            var events = Num(response, "astra.response.events");
            edges.Add(new TraceEdge
            {
                Id = "e:res:provider", Source = "provider", Target = "decode", Tone = EdgeTone.Response,
                SourceHandle = Handle.Bottom, TargetHandle = Handle.Top,
                Label = events is { } ev && ev != 0 ? t("traces.node.events", ("count", ev)) : null,
            });
            for (var i = 0; i < back.Count; i++)
            {
                var hasNext = i + 1 < back.Count;
                edges.Add(new TraceEdge
                {
                    Id = $"e:res:{back[i]}", Source = back[i], Target = hasNext ? back[i + 1] : "agent", Tone = EdgeTone.Response,
                    SourceHandle = Handle.Left, TargetHandle = hasNext ? Handle.Right : Handle.Bottom,
                });
            }
        }
        return new TraceGraph(nodes, edges);
    }

    private static string Last(string s, int n) => s.Length <= n ? s : s[^n..];
}

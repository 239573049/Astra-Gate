using Astra.Api.Contracts;
using Astra.Core.Requests;
using static Astra.Desktop.Core.Traces.TraceAnalysis;

namespace Astra.Desktop.Core.Traces;

/// <summary>Layout knobs of <see cref="SessionBuilder.Build"/> (<c>SessionOptions</c>).</summary>
/// <param name="Columns">Steps per row before the main chain wraps.</param>
/// <param name="Collapse">Fold runs of unremarkable steps into groups once the turn is long.</param>
/// <param name="Expanded">Group ids the user opened.</param>
/// <param name="Pending">Calls of this session from the live feed; those not among the calls yet are drawn after the last step.</param>
public sealed record SessionOptions(int Columns, bool Collapse, IReadOnlySet<string> Expanded, IReadOnlyList<RequestSummaryDto>? Pending = null)
{
    /// <summary>4 columns, folding on, nothing opened.</summary>
    public static SessionOptions Default { get; } = new(4, true, new HashSet<string>());
}

/// <summary>
/// One turn of a session as a canvas: the main agent wraps into rows like text; each sub-agent / side-call chain gets a lane
/// under the row of the step that started it. Tools run between two calls are written on the line joining them; long
/// unremarkable runs fold into groups. Port of <c>buildSession</c> in <c>traces/model.ts</c>.
/// </summary>
public static class SessionBuilder
{
    private const double StepW = 210;
    /// <summary>Horizontal distance between steps (<c>STEP_PITCH</c>); the page derives its column count from it.</summary>
    public const double StepPitch = 340;
    private const double LaneH = 118;
    private const double SidePitch = StepPitch;
    /// <summary>Calls in flight at once (parallel sub-agents) stack this far apart.</summary>
    private const double PendingPitch = 84;

    private sealed record Item(string Id, List<TraceCallDto> Calls, bool Group);

    private static bool IsSide(TraceCallDto c) => c.CallKind is TraceCallKinds.Sidechain or TraceCallKinds.Aux;

    /// <summary>Builds the canvas of one turn.</summary>
    public static SessionGraph Build(IReadOnlyList<TraceCallDto> calls, IReadOnlyList<SpanDto> spans, TraceText t,
        IReadOnlyDictionary<string, ProviderBrand> brands, SessionOptions? options = null)
    {
        options ??= SessionOptions.Default;
        var by = SpansByRequest(spans);
        var ordered = calls.OrderBy(StartMs).ToList();
        var byId = new Dictionary<string, TraceCallDto>(StringComparer.Ordinal);
        foreach (var c in ordered) byId[c.Id] = c; // a later duplicate wins, as in `new Map(...)`
        var main = ordered.Where(c => !IsSide(c)).ToList();
        var side = ordered.Where(IsSide).ToList();
        var anomalies = FindAnomalies(ordered, spans);
        int Tries(TraceCallDto c) => (by.GetValueOrDefault(c.Id) ?? []).Count(s => s.Name == TraceSpanNames.Attempt);
        var columns = Math.Max(1, options.Columns);

        // the main call a side chain hangs off (sub-agents link through one another)
        string? AnchorOf(TraceCallDto c)
        {
            TraceCallDto? cur = c;
            for (var i = 0; cur is not null && i < 64; i++)
            {
                TraceCallDto? parent = null;
                if (!string.IsNullOrEmpty(cur.ParentRequestId)) byId.TryGetValue(cur.ParentRequestId, out parent);
                if (parent is null) return null;
                if (!IsSide(parent)) return parent.Id;
                cur = parent;
            }
            return null;
        }
        var anchors = new HashSet<string>(side.Select(AnchorOf).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!), StringComparer.Ordinal);

        // items: single steps, or folded runs of unremarkable ones
        var items = new List<Item>();
        bool Keep(TraceCallDto c, int i) =>
            !options.Collapse || main.Count <= CollapseAfter || i == 0 || i >= main.Count - 2 || anomalies.ContainsKey(c.Id) || anchors.Contains(c.Id);
        var run = new List<TraceCallDto>();
        void Flush()
        {
            var id = run.Count > 0 ? $"group:{run[0].Id}" : "";
            if (run.Count >= 3 && !options.Expanded.Contains(id)) items.Add(new Item(id, run, true));
            else foreach (var c in run) items.Add(new Item(c.Id, [c], false));
            run = [];
        }
        for (var i = 0; i < main.Count; i++)
        {
            if (Keep(main[i], i))
            {
                Flush();
                items.Add(new Item(main[i].Id, [main[i]], false));
            }
            else run.Add(main[i]);
        }
        Flush();
        var itemOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++) foreach (var c in items[i].Calls) itemOf[c.Id] = i;
        // in flight (or finished but not stored yet): stacked in the slot after the last step
        var pending = (options.Pending ?? []).Where(r => !byId.ContainsKey(r.Id)).ToList();
        var slots = items.Count + (pending.Count > 0 ? 1 : 0);
        var pendingRow = items.Count / columns;

        // side chains: one lane each, under the row of their anchor
        var chainKeys = new List<string>();
        var chains = new Dictionary<string, List<TraceCallDto>>(StringComparer.Ordinal);
        foreach (var c in side)
        {
            var key = $"{c.CallKind}:{c.ChainKey ?? c.Id}:{AnchorOf(c) ?? ""}";
            if (chains.TryGetValue(key, out var list)) list.Add(c);
            else { chains[key] = [c]; chainKeys.Add(key); }
        }
        var rows = Math.Max(1, (int)Math.Ceiling(slots / (double)columns));
        var lanesOfRow = Enumerable.Range(0, rows).Select(_ => new List<(string Key, int AnchorItem)>()).ToArray();
        foreach (var key in chainKeys)
        {
            var anchor = AnchorOf(chains[key][0]);
            var index = anchor is not null && itemOf.TryGetValue(anchor, out var at) ? at : 0;
            lanesOfRow[index / columns].Add((key, index));
        }
        var rowTop = new List<double>();
        double y = 0;
        double StackExtra(int r) => pending.Count > 1 && r == pendingRow ? (pending.Count - 1) * PendingPitch : 0;
        for (var r = 0; r < rows; r++)
        {
            rowTop.Add(y);
            y += 96 + StackExtra(r) + lanesOfRow[r].Count * LaneH + 70;
        }
        double RowRight(int r) => (Math.Min(columns, items.Count - r * columns) - 1) * StepPitch + StepW;

        var nodes = new List<TraceNode>();
        var edges = new List<TraceEdge>();
        CardFlag? Flag(string id) => anomalies.TryGetValue(id, out var a)
            ? new CardFlag(t("traces.flag." + a.ToString().ToLowerInvariant()), a == Anomaly.Error ? Tone.Error : Tone.Warn)
            : null;
        ProviderBrand? BrandOf(string? providerId, string? providerName) =>
            string.IsNullOrEmpty(providerId) ? null : brands.TryGetValue(providerId, out var b) ? b : new ProviderBrand(providerName ?? "");
        void Card(TraceCallDto c, double x, double cy, bool sub)
        {
            var n = Tries(c);
            var own = by.GetValueOrDefault(c.Id) ?? [];
            var phases = PhaseLine(CallPhasesOf(own), t);
            var routed = RouteDecisionOf(own);
            var lines = new List<string>
            {
                $"{TraceFormat.Ms(c.TotalMs)} · {TraceFormat.Tokens(c.TotalInputTokens, true)}→{TraceFormat.Tokens(c.TotalOutputTokens, true)}{(n > 1 ? $" · {t("traces.session.tries", ("count", n))}" : "")}",
            };
            if (routed is not null) lines.Add(t("traces.session.routing", ("grade", routed.Grade ?? TraceFormat.Dash)));
            if (phases is not null) lines.Add(phases);
            nodes.Add(new TraceNode(c.Id, NodeKind.Card, x, cy, StepW, Card: new CardData
            {
                Title = $"{KindLabel(c, sub, t)} #{c.Step ?? 1} · {TraceFormat.ShortModel(c.UpstreamModel ?? c.RequestedModel)}",
                Lines = lines,
                Tone = c.Status != "success" ? Tone.Error : n > 1 ? Tone.Warn : sub ? Tone.Sub : Tone.Neutral,
                Provider = BrandOf(c.ProviderId, c.ProviderName),
                Flag = Flag(c.Id),
            }));
        }
        StepData ToolLabel(TraceCallDto from, TraceCallDto to, StepWrap? wrap)
        {
            var ms = Math.Max(0, StartMs(to) - EndMs(from));
            var gap = ms > 0 ? TraceFormat.Ms(ms) : null;
            string? label, tools;
            if (from.ToolCalls == 0) { label = gap; tools = null; }
            else
            {
                var names = CallPhasesOf(by.GetValueOrDefault(from.Id) ?? [])?.Tools ?? [];
                tools = names.Count > 0 ? TraceFormat.ToolSummary(names) : t("traces.session.toolCount", ("count", from.ToolCalls));
                label = gap is not null ? $"{tools} · {gap}" : tools;
            }
            return new StepData(label, tools, gap, from.ToolCalls > 0 ? $"tool:{to.Id}" : null, wrap);
        }
        TraceEdge StepEdge(string id, string source, string target, TraceCallDto from, TraceCallDto to, StepWrap? wrap = null) =>
            new()
            {
                Id = id, Source = source, Target = target, Type = EdgeType.Step, Tone = EdgeTone.Request,
                Dashed = to.LinkSource == TraceLinkSources.Session, Step = ToolLabel(from, to, wrap),
            };

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = i / columns;
            double x = (i % columns) * StepPitch;
            var top = rowTop[row];
            if (item.Group)
            {
                var first = item.Calls[0];
                var last = item.Calls[^1];
                var total = item.Calls.Sum(c => (double)(c.TotalMs ?? 0));
                var toolCount = item.Calls.Sum(c => c.ToolCalls);
                nodes.Add(new TraceNode(item.Id, NodeKind.Card, x, top, StepW, Card: new CardData
                {
                    Title = $"LLM #{first.Step ?? 1}–#{last.Step ?? 1}",
                    Lines = [t("traces.session.groupCalls", ("count", item.Calls.Count), ("ms", TraceFormat.Ms(total))), t("traces.session.groupTools", ("count", toolCount))],
                    Group = true,
                    Provider = BrandOf(first.ProviderId, first.ProviderName),
                }));
            }
            else Card(item.Calls[0], x, top, false);
            if (i + 1 >= items.Count) continue;
            var next = items[i + 1];
            var nextRow = (i + 1) / columns;
            edges.Add(StepEdge($"e:{item.Id}->{next.Id}", item.Id, next.Id, item.Calls[^1], next.Calls[0], null) with
            {
                Step = ToolLabel(item.Calls[^1], next.Calls[0],
                    nextRow != row ? new StepWrap(RowRight(row) + 44, rowTop[nextRow] - 36, -44) : null),
            });
        }

        // side lanes
        for (var row = 0; row < lanesOfRow.Length; row++)
        {
            var lanes = lanesOfRow[row];
            for (var lane = 0; lane < lanes.Count; lane++)
            {
                var (key, anchorItem) = lanes[lane];
                var list = chains[key];
                var laneY = rowTop[row] + 96 + StackExtra(row) + 22 + lane * LaneH;
                double x = (anchorItem % columns) * StepPitch;
                for (var k = 0; k < list.Count; k++)
                {
                    var c = list[k];
                    Card(c, x, laneY, true);
                    if (k == 0)
                        edges.Add(new TraceEdge
                        {
                            Id = $"e:side:{c.Id}", Source = items[anchorItem].Id, Target = c.Id, Tone = EdgeTone.Inferred, Dashed = true,
                            SourceHandle = Handle.Bottom, TargetHandle = Handle.Top,
                            Label = t(c.CallKind == TraceCallKinds.Sidechain ? "traces.session.inferredSub" : "traces.session.inferredAux"),
                        });
                    else edges.Add(StepEdge($"e:{list[k - 1].Id}->{c.Id}", list[k - 1].Id, c.Id, list[k - 1], c));
                    x += SidePitch;
                }
            }
        }

        // the calls in flight, joined by a moving line to the call before them (else the last step)
        var lastItem = items.Count > 0 ? items[^1] : null;
        var lastRow = (items.Count - 1) / columns; // -1 / n is 0 in C#, like Math.floor(-1/n) never being used when items is empty
        (string Id, TraceCallDto Call, bool Wraps)? SourceOf(RequestSummaryDto r)
        {
            TraceCallDto? parent = null;
            if (!string.IsNullOrEmpty(r.ParentRequestId)) byId.TryGetValue(r.ParentRequestId, out parent);
            if (parent is not null)
            {
                var has = itemOf.TryGetValue(parent.Id, out var at);
                return (has ? items[at].Id : parent.Id, parent, has && at == items.Count - 1);
            }
            return lastItem is not null ? (lastItem.Id, lastItem.Calls[^1], true) : null;
        }
        for (var k = 0; k < pending.Count; k++)
        {
            var r = pending[k];
            nodes.Add(new TraceNode(r.Id, NodeKind.Card, (items.Count % columns) * StepPitch, rowTop[pendingRow] + k * PendingPitch, StepW, Card: new CardData
            {
                Title = $"LLM · {TraceFormat.ShortModel(r.UpstreamModel ?? r.RequestedModel)}",
                Provider = BrandOf(r.ProviderId, r.ProviderName),
                Pending = r.Id,
            }));
            var source = SourceOf(r);
            if (source is null) continue;
            var from = source.Value.Call;
            var tools = from.ToolCalls > 0 ? (CallPhasesOf(by.GetValueOrDefault(from.Id) ?? [])?.Tools ?? []) : [];
            var label = tools.Count > 0 ? TraceFormat.ToolSummary(tools) : from.ToolCalls > 0 ? t("traces.session.toolCount", ("count", from.ToolCalls)) : null;
            edges.Add(new TraceEdge
            {
                Id = $"e:{source.Value.Id}->{r.Id}", Source = source.Value.Id, Target = r.Id, Type = EdgeType.Step, Tone = EdgeTone.Response, Animated = true,
                Step = new StepData(label, label, null, null,
                    source.Value.Wraps && pendingRow != lastRow ? new StepWrap(RowRight(lastRow) + 44, rowTop[pendingRow] - 36, -44) : null),
            });
        }

        var visible = new HashSet<string>(nodes.Select(n => n.Id), StringComparer.Ordinal);
        return new SessionGraph(nodes, edges, ordered.Where(c => anomalies.ContainsKey(c.Id) && visible.Contains(c.Id)).Select(c => c.Id).ToList());
    }

    /// <summary>"LLM", or the sub-agent / side-call label (<c>traces.kind.sidechain</c> / <c>traces.kind.aux</c>).</summary>
    public static string KindLabel(TraceCallDto c, bool sub, TraceText t) =>
        sub ? (c.CallKind == TraceCallKinds.Sidechain ? t("traces.kind.sidechain") : t("traces.kind.aux")) : "LLM";
}

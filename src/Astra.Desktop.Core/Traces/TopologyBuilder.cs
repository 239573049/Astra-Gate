using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// Topology canvas: client and provider columns around the Astra hub; line width follows volume, orange marks rate limits and
/// failovers. Port of <c>buildTopology</c> / <c>withLive</c> in <c>traces/model.ts</c>.
/// </summary>
public static class TopologyBuilder
{
    /// <summary>Id of the hub node (<c>HUB_ID</c>).</summary>
    public const string HubId = "astra";
    private const double Row = 100;

    /// <summary>Node id of a client's card (<c>clientNodeId</c>); a null kind is "direct".</summary>
    public static string ClientNodeId(string? kind) => $"client:{kind ?? "direct"}";

    /// <summary>Node id of a provider's card (<c>providerNodeId</c>).</summary>
    public static string ProviderNodeId(string id) => $"provider:{id}";

    /// <summary>
    /// Builds the canvas. <paramref name="live"/> (the live request feed) puts every request in flight on its lines and adds the
    /// client or provider it uses when the window has no traffic of theirs yet.
    /// </summary>
    public static TraceGraph Build(TraceTopologyDto source, TraceText t, Func<string?, string> clientLabel,
        IReadOnlyDictionary<string, ProviderBrand> brands, IReadOnlyList<RequestSummaryDto>? live = null)
    {
        live ??= [];
        var data = WithLive(source, live, brands);
        var pending = live.Where(r => r.Status == "pending").ToList();
        var rows = Math.Max(1, Math.Max(data.Clients.Count, data.Providers.Count));
        var hubY = (rows - 1) * Row / 2 + 30 - 48;
        double max = 1;
        foreach (var c in data.Clients) max = Math.Max(max, c.Requests);
        foreach (var p in data.Providers) max = Math.Max(max, p.Requests);
        double Width(double requests) => 1.5 + 3.5 * Math.Sqrt(requests / max);
        var liveRange = data.Range == "live";
        string Label(double perMinute, long requests) =>
            liveRange
                ? t("traces.topology.perMinute", ("count", perMinute < 10 ? perMinute.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) : TraceFormat.Num(TraceFormat.JsRound(perMinute))))
                : t("traces.topology.count", ("count", requests));
        double Offset(int count) => (rows - count) * Row / 2;

        var nodes = new List<TraceNode> { new(HubId, NodeKind.Hub, 404, hubY, Busy: pending.Count > 0) };
        var edges = new List<TraceEdge>();
        for (var i = 0; i < data.Clients.Count; i++)
        {
            var c = data.Clients[i];
            var id = ClientNodeId(c.ClientKind);
            var mine = pending.Count(r => r.ClientKind == c.ClientKind);
            nodes.Add(new TraceNode(id, NodeKind.Card, 0, Offset(data.Clients.Count) + i * Row, 184, Card: new CardData
            {
                Title = c.ClientKind is not null ? clientLabel(c.ClientKind) : t("traces.topology.direct"),
                Lines = [c.ActiveSessions > 0 ? t("traces.topology.activeSessions", ("count", c.ActiveSessions)) : t("traces.topology.idle")],
                Tone = c.ActiveSessions > 0 || mine > 0 ? Traces.Tone.Ok : Traces.Tone.Idle,
                Client = c.ClientKind,
                Live = mine,
            }));
            edges.Add(new TraceEdge
            {
                Id = $"e:{id}", Source = id, Target = HubId, Type = EdgeType.Flow, Tone = EdgeTone.Flow, Arrow = false,
                Label = Label(c.PerMinute, c.Requests), StrokeWidth = Width(c.Requests),
                Flow = new FlowData("client", live.Where(r => r.ClientKind == c.ClientKind).Select(r => r.Id).ToList(), false),
            });
        }
        for (var i = 0; i < data.Providers.Count; i++)
        {
            var p = data.Providers[i];
            var id = ProviderNodeId(p.ProviderId);
            var cooling = p.Accounts.Count(a => a.Status == "cooldown");
            var warn = p.RateLimited > 0 || p.Failovers > 0 || cooling > 0;
            var failing = p.Requests > 0 && (double)p.Errors / p.Requests > 0.2;
            var brand = brands.TryGetValue(p.ProviderId, out var b) ? b : new ProviderBrand(p.Name, p.Icon, p.TemplateId);
            var line = p.Accounts.Count > 0
                ? string.Join(" · ", new[] { t("traces.topology.accounts", ("count", p.Accounts.Count)), cooling > 0 ? t("traces.topology.cooling", ("count", cooling)) : null }
                    .Where(x => !string.IsNullOrEmpty(x)))
                : t("traces.topology.apiKey");
            nodes.Add(new TraceNode(id, NodeKind.Card, 760, Offset(data.Providers.Count) + i * Row, 224, Card: new CardData
            {
                Title = p.Name,
                Lines = [line],
                Tone = failing ? Traces.Tone.Error : warn ? Traces.Tone.Warn : Traces.Tone.Ok,
                Provider = brand,
                Live = pending.Count(r => r.ProviderId == p.ProviderId),
            }));
            edges.Add(new TraceEdge
            {
                Id = $"e:{id}", Source = HubId, Target = id, Type = EdgeType.Flow, Arrow = false,
                Tone = failing ? EdgeTone.Error : warn ? EdgeTone.Warn : EdgeTone.Flow,
                Label = Label(p.PerMinute, p.Requests), StrokeWidth = Width(p.Requests),
                Flow = new FlowData("provider", live.Where(r => r.ProviderId == p.ProviderId).Select(r => r.Id).ToList(), warn),
            });
        }
        return new TraceGraph(nodes, edges);
    }

    /// <summary>The aggregates plus a zero row for every client / provider an in-flight request uses that the window has not seen (<c>withLive</c>).</summary>
    public static TraceTopologyDto WithLive(TraceTopologyDto data, IReadOnlyList<RequestSummaryDto> live, IReadOnlyDictionary<string, ProviderBrand> brands)
    {
        var clients = new List<TraceClientNodeDto>(data.Clients);
        var providers = new List<TraceProviderNodeDto>(data.Providers);
        foreach (var r in live)
        {
            if (!clients.Any(c => c.ClientKind == r.ClientKind))
                clients.Add(new TraceClientNodeDto(r.ClientKind, 0, 0, 0, 0));
            if (!string.IsNullOrEmpty(r.ProviderId) && !providers.Any(p => p.ProviderId == r.ProviderId))
            {
                brands.TryGetValue(r.ProviderId, out var brand);
                providers.Add(new TraceProviderNodeDto(r.ProviderId, brand?.Name ?? r.ProviderName ?? r.ProviderId, brand?.Icon,
                    brand?.TemplateId, 0, 0, 0, 0, 0, 0, null, []));
            }
        }
        return clients.Count == data.Clients.Count && providers.Count == data.Providers.Count
            ? data
            : data with { Clients = clients, Providers = providers };
    }
}

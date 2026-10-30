using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Traces;

/// <summary>Semantic tone of a card or a status; the UI maps it to a colour (web <c>Tone</c>).</summary>
public enum Tone { Neutral, Ok, Warn, Error, Idle, Sub, Task }

/// <summary>Semantic colour of an edge (web <c>COLORS</c> in model.ts): flow = accent 55 %, request = label-3, response = accent 70 %,
/// warn = orange, error = red, ok = green, inferred = accent-2.</summary>
public enum EdgeTone { Flow, Request, Response, Warn, Error, Ok, Inferred }

/// <summary>Where an edge leaves or enters a node.</summary>
public enum Handle { Left, Right, Top, Bottom }

/// <summary>How an edge is drawn: a plain curve, a topology line with live dots, or a session step with a label.</summary>
public enum EdgeType { Default, Flow, Step }

/// <summary>Node shape: a card, the Astra hub of the topology, or a provider card with one row per account (topology).</summary>
public enum NodeKind { Card, Hub, Provider }

/// <summary>Anomaly kinds of a turn's call (web <c>Anomaly</c>); the flag text key is <c>traces.flag.{error|retry|slow}</c>.</summary>
public enum Anomaly { Error, Retry, Slow }

/// <summary>The model's phases inside a call; the UI maps them to the phase swatches (wait = label-3 70 %, think = accent-2, write = accent, tool = green).</summary>
public enum PhaseKind { Wait, Think, Write, Tool }

/// <summary>Where an in-flight request is (<c>LivePhase</c>).</summary>
public enum LivePhase { Arriving, Upstream, Streaming, Done, Failed }

/// <summary>Brand data of a provider (icon, template) for a card.</summary>
public sealed record ProviderBrand(string Name, string? Icon = null, string? TemplateId = null);

/// <summary>A badge on a card for a failed / retried / slow call.</summary>
public sealed record CardFlag(string Text, Tone Tone);

/// <summary>A card on a canvas (web <c>CardData</c>).</summary>
public sealed record CardData
{
    /// <summary>Card title.</summary>
    public required string Title { get; init; }
    /// <summary>Secondary lines.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];
    /// <summary>Tone; null draws the card plain.</summary>
    public Tone? Tone { get; init; }
    /// <summary>Client kind whose icon the card shows.</summary>
    public string? Client { get; init; }
    /// <summary>Provider brand whose icon the card shows.</summary>
    public ProviderBrand? Provider { get; init; }
    /// <summary>Icon above the text instead of beside it (tall nodes).</summary>
    public bool Stacked { get; init; }
    /// <summary>A tool run by the agent: a dashed pill.</summary>
    public bool Pill { get; init; }
    /// <summary>Requests in flight right now (topology badge).</summary>
    public int? Live { get; init; }
    /// <summary>Failed / retried / slow badge (session).</summary>
    public CardFlag? Flag { get; init; }
    /// <summary>A folded run of steps: click to expand.</summary>
    public bool Group { get; init; }
    /// <summary>Id of the in-flight request this card shows; the card reads what the model is doing right now.</summary>
    public string? Pending { get; init; }
    /// <summary>What the client's agent is doing right now (topology): a coloured line under the title.</summary>
    public CardStatus? Status { get; init; }
    /// <summary>Another request's path is highlighted: this card fades.</summary>
    public bool Dim { get; init; }
}

/// <summary>
/// A client card's live line (web <c>CardData.status</c>); <paramref name="Tone"/> is think | write | tool | wait | run | warn and picks
/// the colour (accent-2, accent, green, secondary, green, orange).
/// </summary>
public sealed record CardStatus(string Text, string Tone);

/// <summary>The words above the hub (web <c>HubData.note</c>): a switch a request just made, or where one was just sent. Tone: info | warn.</summary>
public sealed record HubNote(string Text, string Tone);

/// <summary>
/// One row of a provider's card on the topology (web <c>AccountRow</c>): an account, or the API key, its requests go through. <see cref="Id"/> is
/// the row's handle (<see cref="TopologyBuilder.AccountRowId"/>); <see cref="Hit"/> is "on" while a request is served here, "fail" right
/// after one was refused here.
/// </summary>
public sealed record AccountRow(string Id, string? AccountId, string Name, string Status, string Detail, long Attempts, long Errors, int Live,
    string? Hit, bool Dim);

/// <summary>
/// A positioned node. Coordinates are the web's React Flow pixels (the UI may scale them). <see cref="Height"/> is null for
/// content-sized cards.
/// </summary>
public sealed record TraceNode(string Id, NodeKind Kind, double X, double Y, double? Width = null, double? Height = null,
    CardData? Card = null, bool Busy = false)
{
    /// <summary>A provider node's rows (one per account, or the API key).</summary>
    public IReadOnlyList<AccountRow>? Rows { get; init; }
    /// <summary>The hub's note.</summary>
    public HubNote? Note { get; init; }
}

/// <summary>
/// A topology line and the requests riding it (<c>FlowData</c>). Role: client (agent → Astra), provider, or account (Astra → one account row
/// of a provider). <see cref="Failed"/> holds requests whose attempt on this line was just refused (a red capsule bounces back to the hub);
/// <see cref="Retry"/> marks a line a retry is heading down (orange, dashed, moving).
/// </summary>
public sealed record FlowData(string Role, IReadOnlyList<string> Requests, bool Warn)
{
    /// <summary>Requests whose attempt on this line was just refused.</summary>
    public IReadOnlyList<string> Failed { get; init; } = [];
    /// <summary>A retry is heading down this line.</summary>
    public bool Retry { get; init; }
}

/// <summary>The line from the end of a row back to the start of the next one (<c>StepData.wrap</c>).</summary>
public sealed record StepWrap(double RightX, double GapY, double LeftX);

/// <summary>Label and selection of a session step edge (<c>StepData</c>).</summary>
public sealed record StepData(string? Label, string? Tools, string? Gap, string? SelectId, StepWrap? Wrap);

/// <summary>An edge between two nodes (React Flow's <c>Edge</c> reduced to what the UI needs).</summary>
public sealed record TraceEdge
{
    /// <summary>Edge id.</summary>
    public required string Id { get; init; }
    /// <summary>Source node id.</summary>
    public required string Source { get; init; }
    /// <summary>Target node id.</summary>
    public required string Target { get; init; }
    /// <summary>Handle on the source.</summary>
    public Handle SourceHandle { get; init; } = Handle.Right;
    /// <summary>Handle on the target.</summary>
    public Handle TargetHandle { get; init; } = Handle.Left;
    /// <summary>A topology line into a provider card ends on this row's handle (<see cref="AccountRow.Id"/>) instead of <see cref="TargetHandle"/>.</summary>
    public string? TargetRow { get; init; }
    /// <summary>Stroke opacity (a line off the highlighted path fades to 0.25); null = opaque.</summary>
    public double? Opacity { get; init; }
    /// <summary>Drawing style.</summary>
    public EdgeType Type { get; init; } = EdgeType.Default;
    /// <summary>Semantic colour.</summary>
    public EdgeTone Tone { get; init; }
    /// <summary>Stroke width in px.</summary>
    public double StrokeWidth { get; init; } = 1.75;
    /// <summary>Dashed stroke ("5 4").</summary>
    public bool Dashed { get; init; }
    /// <summary>Arrow head at the target (topology lines have none).</summary>
    public bool Arrow { get; init; } = true;
    /// <summary>Label on the line.</summary>
    public string? Label { get; init; }
    /// <summary>Moving line (a call in flight).</summary>
    public bool Animated { get; init; }
    /// <summary>Topology flow data (type Flow).</summary>
    public FlowData? Flow { get; init; }
    /// <summary>Session step data (type Step).</summary>
    public StepData? Step { get; init; }
}

/// <summary>Nodes and edges of one canvas.</summary>
public record TraceGraph(IReadOnlyList<TraceNode> Nodes, IReadOnlyList<TraceEdge> Edges)
{
    /// <summary>An empty canvas.</summary>
    public static readonly TraceGraph Empty = new([], []);
}

/// <summary>A session turn's canvas plus the ids of its anomalies, in order.</summary>
public sealed record SessionGraph(IReadOnlyList<TraceNode> Nodes, IReadOnlyList<TraceEdge> Edges, IReadOnlyList<string> Anomalies)
    : TraceGraph(Nodes, Edges);

/// <summary>A smart-routing decision recorded on a resolve span (<c>RouteDecision</c>).</summary>
public sealed record RouteDecision(SpanDto Span, string? Mode, string? Source, string? Grade, string? Model, double? Confidence,
    string? FallbackReason, string? AdjustedFrom, string? JudgeProtocol, string? JudgeModel, double JudgeMs, double? JudgeInput,
    double? JudgeOutput, bool NewConnection);

/// <summary>Where one request's time went (<c>TimeSplit</c>), in microseconds.</summary>
public sealed record TimeSplit(double AstraUs, double RoutingUs, double RetryUs, double TtfbUs, double FirstTokenUs,
    double GenerationUs, double TailUs, double TotalUs);

/// <summary>Where a turn's time went (<c>TurnTime</c>), in milliseconds.</summary>
public sealed record TurnTime(double ModelMs, double ToolMs, double RetryMs, double OverheadMs, double TotalMs);

/// <summary>One bar of a call's own time axis (ms from the call's start, or from the turn's start in a timeline).</summary>
public sealed record PhaseSegment(PhaseKind Kind, double From, double To);

/// <summary>When the model waited, thought, wrote and called tools in one call (<c>CallPhases</c>).</summary>
public sealed record CallPhases(double WaitMs, double ThinkMs, double WriteMs, double ToolMs, IReadOnlyList<PhaseSegment> Segments,
    IReadOnlyList<string> Tools);

/// <summary>The model's phases summed over a turn, in ms.</summary>
public sealed record PhaseSums(double Wait, double Think, double Write, double Tool)
{
    /// <summary>Sum of the four phases.</summary>
    public double Total => Wait + Think + Write + Tool;
    /// <summary>One phase's value.</summary>
    public double Of(PhaseKind k) => k switch { PhaseKind.Wait => Wait, PhaseKind.Think => Think, PhaseKind.Write => Write, _ => Tool };
}

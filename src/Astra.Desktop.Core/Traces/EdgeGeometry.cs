namespace Astra.Desktop.Core.Traces;

/// <summary>A point in canvas (world) pixels.</summary>
public readonly record struct Pt(double X, double Y);

/// <summary>A rectangle in canvas pixels.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    /// <summary>Right edge.</summary>
    public double Right => X + Width;
    /// <summary>Bottom edge.</summary>
    public double Bottom => Y + Height;
    /// <summary>The smallest box holding both.</summary>
    public Box Union(Box o)
    {
        var x = Math.Min(X, o.X);
        var y = Math.Min(Y, o.Y);
        return new Box(x, y, Math.Max(Right, o.Right) - x, Math.Max(Bottom, o.Bottom) - y);
    }
}

/// <summary>The kind of one piece of an edge path.</summary>
public enum SegmentKind { Line, Quad, Cubic }

/// <summary>
/// One piece of a path from the previous end point: a line to <see cref="P3"/>, a quadratic curve through control <see cref="P1"/> to
/// <see cref="P3"/>, or a cubic through <see cref="P1"/> and <see cref="P2"/> to <see cref="P3"/>.
/// </summary>
public readonly record struct PathSegment(SegmentKind Kind, Pt P1, Pt P2, Pt P3);

/// <summary>An edge's path plus where its label sits (the SVG <c>d</c> and the label position React Flow computes).</summary>
public sealed record EdgeShape(Pt Start, IReadOnlyList<PathSegment> Segments, Pt Label)
{
    /// <summary>The path's end point.</summary>
    public Pt End => Segments.Count == 0 ? Start : Segments[^1].P3;

    /// <summary>The path as a polyline of <paramref name="perSegment"/> points per curved piece (lines stay two points).</summary>
    public IReadOnlyList<Pt> Flatten(int perSegment = 24)
    {
        var points = new List<Pt> { Start };
        var from = Start;
        foreach (var s in Segments)
        {
            if (s.Kind == SegmentKind.Line) points.Add(s.P3);
            else
                for (var i = 1; i <= perSegment; i++)
                {
                    var t = (double)i / perSegment;
                    points.Add(s.Kind == SegmentKind.Quad ? EdgeGeometry.Quad(from, s.P1, s.P3, t) : EdgeGeometry.Cubic(from, s.P1, s.P2, s.P3, t));
                }
            from = s.P3;
        }
        return points;
    }

    /// <summary>
    /// The point <paramref name="fraction"/> (0..1) of the way along the path by length: what SVG <c>animateMotion</c> with
    /// <c>calcMode="linear"</c> and <c>keyPoints="0;1"</c> follows, so a live dot moves at an even speed.
    /// </summary>
    public Pt PointAt(double fraction)
    {
        var pts = Flatten();
        if (pts.Count == 1) return pts[0];
        var lengths = new double[pts.Count];
        for (var i = 1; i < pts.Count; i++) lengths[i] = lengths[i - 1] + Dist(pts[i - 1], pts[i]);
        var total = lengths[^1];
        if (total <= 0) return pts[0];
        var want = Math.Clamp(fraction, 0, 1) * total;
        for (var i = 1; i < pts.Count; i++)
        {
            if (lengths[i] < want) continue;
            var span = lengths[i] - lengths[i - 1];
            var k = span <= 0 ? 0 : (want - lengths[i - 1]) / span;
            return new Pt(pts[i - 1].X + (pts[i].X - pts[i - 1].X) * k, pts[i - 1].Y + (pts[i].Y - pts[i - 1].Y) * k);
        }
        return pts[^1];
    }

    /// <summary>The unit direction the path arrives in at its end (an arrow head points along it).</summary>
    public Pt EndDirection()
    {
        var pts = Flatten();
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            var d = Dist(pts[i], pts[^1]);
            if (d > 1e-6) return new Pt((pts[^1].X - pts[i].X) / d, (pts[^1].Y - pts[i].Y) / d);
        }
        return new Pt(1, 0);
    }

    private static double Dist(Pt a, Pt b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}

/// <summary>
/// Edge geometry of the trace canvases, ported from what React Flow draws for the web (<c>@xyflow/system</c>'s <c>getBezierPath</c> and
/// handle positions) and from <c>StepEdgeView</c> / <c>rounded</c> in traces/nodes.tsx, so edges leave and enter the cards at the same
/// points and bend the same way.
/// </summary>
public static class EdgeGeometry
{
    /// <summary>Handles are 6 × 6 px boxes centred on the card's side (flow.css <c>.react-flow__handle</c>).</summary>
    public const double HandleSize = 6;

    /// <summary>React Flow's default bezier curvature.</summary>
    public const double Curvature = 0.25;

    /// <summary>
    /// Where an edge attaches to a node of box <paramref name="node"/> on <paramref name="side"/>: React Flow's <c>getHandlePosition</c>
    /// returns the outer edge of the 6 px handle box (so 3 px outside the card) at the side's middle.
    /// </summary>
    public static Pt HandlePoint(Box node, Handle side)
    {
        const double h = HandleSize / 2;
        return side switch
        {
            Handle.Left => new Pt(node.X - h, node.Y + node.Height / 2),
            Handle.Right => new Pt(node.Right + h, node.Y + node.Height / 2),
            Handle.Top => new Pt(node.X + node.Width / 2, node.Y - h),
            _ => new Pt(node.X + node.Width / 2, node.Bottom + h),
        };
    }

    private static double ControlOffset(double distance, double curvature) =>
        distance >= 0 ? 0.5 * distance : curvature * 25 * Math.Sqrt(-distance);

    private static Pt Control(Handle side, Pt from, Pt to, double c) => side switch
    {
        Handle.Left => new Pt(from.X - ControlOffset(from.X - to.X, c), from.Y),
        Handle.Right => new Pt(from.X + ControlOffset(to.X - from.X, c), from.Y),
        Handle.Top => new Pt(from.X, from.Y - ControlOffset(from.Y - to.Y, c)),
        _ => new Pt(from.X, from.Y + ControlOffset(to.Y - from.Y, c)),
    };

    /// <summary>React Flow's <c>getBezierPath</c>: one cubic, its label at the curve's middle.</summary>
    public static EdgeShape Bezier(Pt source, Handle sourceSide, Pt target, Handle targetSide, double curvature = Curvature)
    {
        var c1 = Control(sourceSide, source, target, curvature);
        var c2 = Control(targetSide, target, source, curvature);
        var label = new Pt(
            source.X * 0.125 + c1.X * 0.375 + c2.X * 0.375 + target.X * 0.125,
            source.Y * 0.125 + c1.Y * 0.375 + c2.Y * 0.375 + target.Y * 0.125);
        return new EdgeShape(source, [new PathSegment(SegmentKind.Cubic, c1, c2, target)], label);
    }

    /// <summary>A rounded orthogonal path through <paramref name="points"/> (nodes.tsx <c>rounded</c>, corner radius 12).</summary>
    public static IReadOnlyList<PathSegment> Rounded(IReadOnlyList<Pt> points, double r = 12)
    {
        var segs = new List<PathSegment>();
        for (var i = 1; i < points.Count - 1; i++)
        {
            var (p, x, n) = (points[i - 1], points[i], points[i + 1]);
            var inLen = Math.Sqrt((x.X - p.X) * (x.X - p.X) + (x.Y - p.Y) * (x.Y - p.Y));
            var outLen = Math.Sqrt((n.X - x.X) * (n.X - x.X) + (n.Y - x.Y) * (n.Y - x.Y));
            var k = Math.Min(r, Math.Min(inLen / 2, outLen / 2));
            var a = new Pt(x.X - (x.X - p.X) / (inLen == 0 ? 1 : inLen) * k, x.Y - (x.Y - p.Y) / (inLen == 0 ? 1 : inLen) * k);
            var b = new Pt(x.X + (n.X - x.X) / (outLen == 0 ? 1 : outLen) * k, x.Y + (n.Y - x.Y) / (outLen == 0 ? 1 : outLen) * k);
            segs.Add(new PathSegment(SegmentKind.Line, default, default, a));
            segs.Add(new PathSegment(SegmentKind.Quad, x, default, b));
        }
        segs.Add(new PathSegment(SegmentKind.Line, default, default, points[^1]));
        return segs;
    }

    /// <summary>
    /// A session step (<c>StepEdgeView</c>): a step that wraps to the next row runs round the right edge, back along the gap above that
    /// row, into its first step; any other step is a bezier.
    /// </summary>
    public static EdgeShape Step(Pt source, Handle sourceSide, Pt target, Handle targetSide, StepWrap? wrap)
    {
        if (wrap is not null && target.Y > source.Y + 60)
        {
            var rx = Math.Max(wrap.RightX, source.X + 24);
            var lx = Math.Min(wrap.LeftX, target.X - 24);
            var gy = Math.Min(Math.Max(wrap.GapY, source.Y + 30), target.Y - 30);
            Pt[] pts = [source, new(rx, source.Y), new(rx, gy), new(lx, gy), new(lx, target.Y), target];
            return new EdgeShape(source, Rounded(pts), new Pt((rx + lx) / 2, gy));
        }
        return Bezier(source, sourceSide, target, targetSide);
    }

    /// <summary>A point of a cubic bezier.</summary>
    public static Pt Cubic(Pt p0, Pt p1, Pt p2, Pt p3, double t)
    {
        var u = 1 - t;
        double B(double a, double b, double c, double d) => u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
        return new Pt(B(p0.X, p1.X, p2.X, p3.X), B(p0.Y, p1.Y, p2.Y, p3.Y));
    }

    /// <summary>A point of a quadratic bezier.</summary>
    public static Pt Quad(Pt p0, Pt p1, Pt p2, double t)
    {
        var u = 1 - t;
        return new Pt(u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X, u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y);
    }
}

/// <summary>
/// The pan / zoom of a trace canvas: screen = world × <see cref="Zoom"/> + (<see cref="X"/>, <see cref="Y"/>). The operations are React
/// Flow's (<c>fitView</c>, <c>setCenter</c>, <c>zoomIn</c> / <c>zoomOut</c>, wheel zoom around the pointer) with the limits the web
/// canvas passes (min 0.25, max 2.5).
/// </summary>
public readonly record struct Viewport(double X, double Y, double Zoom)
{
    /// <summary>The web canvas's <c>minZoom</c>.</summary>
    public const double MinZoom = 0.25;

    /// <summary>The web canvas's <c>maxZoom</c>.</summary>
    public const double MaxZoom = 2.5;

    /// <summary>The fit padding every <c>fitView</c> call of canvas.tsx passes.</summary>
    public const double FitPadding = 0.18;

    /// <summary>React Flow's zoom step for the + / − buttons.</summary>
    public const double ZoomStep = 1.2;

    /// <summary>The identity view.</summary>
    public static Viewport Identity { get; } = new(0, 0, 1);

    /// <summary>A world point on screen.</summary>
    public Pt ToScreen(Pt world) => new(world.X * Zoom + X, world.Y * Zoom + Y);

    /// <summary>A screen point in the world.</summary>
    public Pt ToWorld(Pt screen) => new((screen.X - X) / Zoom, (screen.Y - Y) / Zoom);

    /// <summary>
    /// <c>fitView({ padding: 0.18, maxZoom: 1 })</c>: the zoom that fits <paramref name="bounds"/> with the padding (clamped), centred
    /// in a <paramref name="width"/> × <paramref name="height"/> view.
    /// </summary>
    public static Viewport Fit(Box bounds, double width, double height, double padding = FitPadding, double maxZoom = 1, double minZoom = MinZoom)
    {
        if (width <= 0 || height <= 0 || bounds.Width <= 0 && bounds.Height <= 0) return Identity;
        var zx = bounds.Width > 0 ? width / (bounds.Width * (1 + padding)) : double.PositiveInfinity;
        var zy = bounds.Height > 0 ? height / (bounds.Height * (1 + padding)) : double.PositiveInfinity;
        var zoom = Math.Clamp(Math.Min(zx, zy), minZoom, maxZoom);
        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        return new Viewport(width / 2 - cx * zoom, height / 2 - cy * zoom, zoom);
    }

    /// <summary><c>setCenter(x, y, { zoom })</c>: <paramref name="world"/> in the middle of the view at <paramref name="zoom"/>.</summary>
    public static Viewport Center(Pt world, double zoom, double width, double height) =>
        new(width / 2 - world.X * zoom, height / 2 - world.Y * zoom, zoom);

    /// <summary>The view zoomed to <paramref name="zoom"/> (clamped) keeping the screen point <paramref name="anchor"/> still.</summary>
    public Viewport ZoomAround(Pt anchor, double zoom)
    {
        var z = Math.Clamp(zoom, MinZoom, MaxZoom);
        var w = ToWorld(anchor);
        return new Viewport(anchor.X - w.X * z, anchor.Y - w.Y * z, z);
    }

    /// <summary>The view moved by a screen delta.</summary>
    public Viewport Pan(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>The part of the world the view shows.</summary>
    public Box Visible(double width, double height)
    {
        var tl = ToWorld(new Pt(0, 0));
        return new Box(tl.X, tl.Y, width / Zoom, height / Zoom);
    }
}

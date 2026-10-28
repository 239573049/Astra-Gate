using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Tests.Traces;

/// <summary>
/// The page-side logic WP7 added to Desktop.Core/Traces: the route parameter (<see cref="TracesLocation"/>), the traces queries
/// (<see cref="TraceQueries"/>: paths, keys, intervals, unsupported / not-found errors), the canvas geometry (React Flow's bezier and handle
/// positions, the session step that wraps a row, path sampling) and the viewport maths (fit, centre, zoom around a point), plus the two
/// session-wide canvas settings.
/// </summary>
public sealed class TraceCanvasLogicTests
{
    // ------------------------------------------------------------------ route parameter

    [Theory]
    [InlineData(null, TraceRoutes.Tab.Topology, null, null)]
    [InlineData("", TraceRoutes.Tab.Topology, null, null)]
    [InlineData("/traces", TraceRoutes.Tab.Topology, null, null)]
    [InlineData("/traces/sessions", TraceRoutes.Tab.Sessions, null, null)]
    [InlineData("sessions", TraceRoutes.Tab.Sessions, null, null)]
    [InlineData("/traces/sessions/cc%3Aabc", TraceRoutes.Tab.Sessions, "cc:abc", null)]
    [InlineData("/traces/requests/", TraceRoutes.Tab.Requests, null, null)]
    [InlineData("/traces/requests/req_1", TraceRoutes.Tab.Requests, null, "req_1")]
    [InlineData("requests/a%2Fb", TraceRoutes.Tab.Requests, null, "a/b")]
    [InlineData("/traces/unknown", TraceRoutes.Tab.Topology, null, null)]
    public void Parses_the_web_path(string? param, TraceRoutes.Tab tab, string? session, string? request)
    {
        var at = TracesLocation.Parse(param);
        Assert.Equal(tab, at.Tab);
        Assert.Equal(session, at.SessionKey);
        Assert.Equal(request, at.RequestId);
    }

    [Fact]
    public void Reads_and_sets_search_params()
    {
        var at = TracesLocation.Parse("/traces/sessions/cc%3Aabc?turn=t%203&view=timeline&empty=");
        Assert.Equal("t 3", at.Get("turn"));
        Assert.Equal("timeline", at.Get("view"));
        Assert.Null(at.Get("empty"));
        Assert.Null(at.Get("missing"));
        var next = at.With("view", null).With("turn", "t4");
        Assert.Null(next.Get("view"));
        Assert.Equal("t4", next.Get("turn"));
        Assert.Equal("timeline", at.Get("view")); // the original is unchanged
    }

    [Fact]
    public void Url_search_params_decode_plus_as_space()
    {
        Assert.Equal("a b", TracesLocation.Parse("/traces/sessions?client=a+b").Get("client"));
    }

    [Theory]
    [InlineData("/traces")]
    [InlineData("/traces/sessions")]
    [InlineData("/traces/requests")]
    [InlineData("/traces/requests/req_1")]
    [InlineData("/traces/sessions/cc%3Aabc?turn=t3")]
    public void Round_trips_to_the_same_path(string path)
    {
        Assert.Equal(path, TracesLocation.Parse(path).ToParam());
    }

    [Fact]
    public void The_inspector_routes_parse_back()
    {
        var session = TracesLocation.Parse(TraceRoutes.Session("cc:a b", "t(1)").Path);
        Assert.Equal("cc:a b", session.SessionKey);
        Assert.Equal("t(1)", session.Get("turn"));
        var sessions = TracesLocation.Parse(TraceRoutes.Sessions("claude-code").Path);
        Assert.Equal(TraceRoutes.Tab.Sessions, sessions.Tab);
        Assert.Null(sessions.SessionKey);
        Assert.Equal("claude-code", sessions.Get("client"));
        Assert.Equal("x!y", TracesLocation.Parse(TraceRoutes.Request("x!y").Path).RequestId);
    }

    // ------------------------------------------------------------------ queries

    [Fact]
    public void Query_paths_and_keys_match_the_web_hooks()
    {
        Assert.Equal("/api/traces/topology?range=live", TraceQueries.TopologyPath("live"));
        Assert.Equal(QueryKey.Of("traces", "topology", "1h"), TraceQueries.TopologyKey("1h"));
        Assert.Equal("/api/traces/sessions?range=24h", TraceQueries.SessionsPath("24h", null));
        Assert.Equal("/api/traces/sessions?range=7d&client=codex", TraceQueries.SessionsPath("7d", "codex"));
        Assert.Equal(QueryKey.Of("traces", "sessions", "24h", ""), TraceQueries.SessionsKey("24h", null));
        Assert.Equal("/api/traces/sessions/cc%3Aabc", TraceQueries.SessionPath("cc:abc", null));
        Assert.Equal("/api/traces/sessions/cc%3Aabc?turn=t2", TraceQueries.SessionPath("cc:abc", "t2"));
        Assert.Equal(QueryKey.Of("traces", "session", "cc:abc", ""), TraceQueries.SessionKey("cc:abc", null));
        Assert.Equal("/api/traces/requests/r%2F1", TraceQueries.RequestPath("r/1"));
        Assert.Equal(QueryKey.Of("traces", "request", "r1"), TraceQueries.RequestKey("r1"));
        // the live feed's persisted refetch invalidates these prefixes
        Assert.True(TraceQueries.SessionKey("k", "t").StartsWith(QueryKey.Of("traces", "session")));
        Assert.True(TraceQueries.RequestKey("r1").StartsWith(QueryKey.Of("traces", "request", "r1")));
    }

    [Fact]
    public void Poll_intervals_follow_the_web()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), TraceQueries.TopologyInterval("live"));
        Assert.Equal(TimeSpan.FromSeconds(30), TraceQueries.TopologyInterval("24h"));
        Assert.Equal(TimeSpan.FromSeconds(10), TraceQueries.SessionsInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), TraceQueries.SessionInterval(true));
        Assert.Null(TraceQueries.SessionInterval(false));
        Assert.Equal(TimeSpan.FromSeconds(1), TraceQueries.RequestInterval(true));
        Assert.Null(TraceQueries.RequestInterval(false));
    }

    [Fact]
    public void A_404_on_the_traces_api_means_an_older_server()
    {
        var unsupported = new ApiException(404, "no route: GET /api/traces/topology");
        var gone = new ApiException(404, "session not found");
        Assert.True(TraceQueries.IsUnsupported(unsupported));
        Assert.False(TraceQueries.IsNotFound(unsupported));
        Assert.False(TraceQueries.IsUnsupported(gone));
        Assert.True(TraceQueries.IsNotFound(gone));
        Assert.False(TraceQueries.IsUnsupported(new ApiException(500, "/api/traces broke")));
        Assert.False(TraceQueries.IsNotFound(new InvalidOperationException()));
        Assert.False(TraceQueries.IsUnsupported(null));
    }

    // ------------------------------------------------------------------ edge geometry

    [Fact]
    public void Handles_sit_on_the_outer_edge_of_the_six_pixel_box()
    {
        var box = new Box(100, 50, 200, 60);
        Assert.Equal(new Pt(97, 80), EdgeGeometry.HandlePoint(box, Handle.Left));
        Assert.Equal(new Pt(303, 80), EdgeGeometry.HandlePoint(box, Handle.Right));
        Assert.Equal(new Pt(200, 47), EdgeGeometry.HandlePoint(box, Handle.Top));
        Assert.Equal(new Pt(200, 113), EdgeGeometry.HandlePoint(box, Handle.Bottom));
    }

    [Fact]
    public void Bezier_matches_react_flow()
    {
        // getBezierPath({ sourceX: 0, sourceY: 0, sourcePosition: Right, targetX: 100, targetY: 40, targetPosition: Left }):
        // "M0,0 C50,0 50,40 100,40", label at (50, 20)
        var e = EdgeGeometry.Bezier(new Pt(0, 0), Handle.Right, new Pt(100, 40), Handle.Left);
        var c = Assert.Single(e.Segments);
        Assert.Equal(SegmentKind.Cubic, c.Kind);
        Assert.Equal(new Pt(50, 0), c.P1);
        Assert.Equal(new Pt(50, 40), c.P2);
        Assert.Equal(new Pt(100, 40), c.P3);
        Assert.Equal(new Pt(50, 20), e.Label);
    }

    [Fact]
    public void A_backwards_bezier_bows_out_by_the_curvature()
    {
        // target left of the source on a right→left edge: offset 0.25 * 25 * sqrt(distance)
        var e = EdgeGeometry.Bezier(new Pt(100, 0), Handle.Right, new Pt(0, 0), Handle.Left);
        var c = e.Segments[0];
        Assert.Equal(100 + 0.25 * 25 * 10, c.P1.X, 6);
        Assert.Equal(0 - 0.25 * 25 * 10, c.P2.X, 6);
    }

    [Fact]
    public void A_wrapping_step_runs_round_the_right_edge_and_back_along_the_gap()
    {
        var wrap = new StepWrap(RightX: 900, GapY: 140, LeftX: -44);
        var e = EdgeGeometry.Step(new Pt(800, 30), Handle.Right, new Pt(-3, 220), Handle.Left, wrap);
        Assert.Equal(new Pt((900 + -44) / 2.0, 140), e.Label);
        Assert.Equal(new Pt(-3, 220), e.End);
        var pts = e.Flatten();
        Assert.Contains(pts, p => Math.Abs(p.X - 900) < 1e-9 && p.Y > 30 && p.Y < 140);
        Assert.All(pts, p => Assert.InRange(p.X, -44 - 1e-9, 900 + 1e-9));
        // a step to a card on the same row is a plain bezier
        var flat = EdgeGeometry.Step(new Pt(0, 30), Handle.Right, new Pt(100, 30), Handle.Left, wrap);
        Assert.Equal(SegmentKind.Cubic, Assert.Single(flat.Segments).Kind);
    }

    [Fact]
    public void Rounded_corners_never_exceed_half_a_leg()
    {
        var segs = EdgeGeometry.Rounded([new Pt(0, 0), new Pt(10, 0), new Pt(10, 100)]);
        // line to (5,0), quad through (10,0) to (10,5), line to the end: the 12 px radius is cut to half the 10 px leg
        Assert.Equal(new Pt(5, 0), segs[0].P3);
        Assert.Equal(new Pt(10, 0), segs[1].P1);
        Assert.Equal(new Pt(10, 5), segs[1].P3);
        Assert.Equal(new Pt(10, 100), segs[^1].P3);
    }

    [Fact]
    public void Points_along_a_path_move_by_length()
    {
        var e = new EdgeShape(new Pt(0, 0), [new PathSegment(SegmentKind.Line, default, default, new Pt(100, 0)), new PathSegment(SegmentKind.Line, default, default, new Pt(100, 100))], default);
        Assert.Equal(new Pt(0, 0), e.PointAt(0));
        Assert.Equal(new Pt(50, 0), e.PointAt(0.25));
        Assert.Equal(new Pt(100, 0), e.PointAt(0.5));
        Assert.Equal(new Pt(100, 50), e.PointAt(0.75));
        Assert.Equal(new Pt(100, 100), e.PointAt(1));
        Assert.Equal(new Pt(100, 100), e.PointAt(2)); // clamped
        Assert.Equal(new Pt(0, 1), e.EndDirection());
    }

    // ------------------------------------------------------------------ viewport

    [Fact]
    public void Fit_view_uses_the_padding_and_never_zooms_past_one()
    {
        // small graph in a big view: zoom 1, centred
        var v = Viewport.Fit(new Box(0, 0, 200, 100), 1000, 600);
        Assert.Equal(1, v.Zoom);
        Assert.Equal(new Pt(500, 300), v.ToScreen(new Pt(100, 50)));
        // a wide graph: width / (bounds * 1.18)
        var wide = Viewport.Fit(new Box(0, 0, 2000, 100), 1000, 600);
        Assert.Equal(1000 / (2000 * 1.18), wide.Zoom, 9);
        // never below the minimum zoom
        Assert.Equal(Viewport.MinZoom, Viewport.Fit(new Box(0, 0, 100_000, 100), 1000, 600).Zoom);
        Assert.Equal(Viewport.Identity, Viewport.Fit(new Box(0, 0, 0, 0), 1000, 600));
    }

    [Fact]
    public void Zoom_keeps_the_anchor_still_and_clamps()
    {
        var v = new Viewport(10, 20, 1);
        var anchor = new Pt(300, 200);
        var world = v.ToWorld(anchor);
        var z = v.ZoomAround(anchor, 2);
        Assert.Equal(2, z.Zoom);
        Assert.Equal(anchor.X, z.ToScreen(world).X, 9);
        Assert.Equal(anchor.Y, z.ToScreen(world).Y, 9);
        Assert.Equal(Viewport.MaxZoom, v.ZoomAround(anchor, 99).Zoom);
        Assert.Equal(Viewport.MinZoom, v.ZoomAround(anchor, 0.01).Zoom);
    }

    [Fact]
    public void Centre_pan_and_visible_area()
    {
        var c = Viewport.Center(new Pt(100, 100), 0.9, 800, 400);
        Assert.Equal(new Pt(400, 200), c.ToScreen(new Pt(100, 100)));
        var p = c.Pan(10, -5);
        Assert.Equal(new Pt(410, 195), p.ToScreen(new Pt(100, 100)));
        var vis = new Viewport(-100, -50, 2).Visible(400, 200);
        Assert.Equal(new Box(50, 25, 200, 100), vis);
        Assert.Equal(new Box(0, 0, 30, 40), new Box(0, 0, 10, 10).Union(new Box(20, 30, 10, 10)));
    }

    // ------------------------------------------------------------------ canvas settings

    [Fact]
    public void Dragged_positions_are_kept_under_their_key()
    {
        var key = "test-" + Guid.NewGuid();
        Assert.Empty(TraceLayouts.Read(key));
        TraceLayouts.Write(key, new Dictionary<string, Pt> { ["a"] = new(1, 2) });
        var read = TraceLayouts.Read(key);
        Assert.Equal(new Pt(1, 2), read["a"]);
        read["b"] = new Pt(3, 4); // a copy: the store does not change
        Assert.Single(TraceLayouts.Read(key));
        TraceLayouts.Write(key, new Dictionary<string, Pt>());
        Assert.Empty(TraceLayouts.Read(key));
        // no key: nothing is remembered
        TraceLayouts.Write(null, new Dictionary<string, Pt> { ["a"] = new(1, 2) });
        Assert.Empty(TraceLayouts.Read(null));
    }

    [Fact]
    public void The_details_switch_tells_every_canvas()
    {
        var was = TraceInspectorSwitch.On;
        var calls = 0;
        void Count() => calls++;
        TraceInspectorSwitch.Changed += Count;
        try
        {
            TraceInspectorSwitch.Set(!was);
            TraceInspectorSwitch.Set(!was); // same value: no event
            Assert.Equal(!was, TraceInspectorSwitch.On);
            Assert.Equal(1, calls);
        }
        finally
        {
            TraceInspectorSwitch.Changed -= Count;
            TraceInspectorSwitch.Set(was);
        }
    }

    // ------------------------------------------------------------------ persisted canvas settings (canvas.tsx localStorage)

    private sealed class FakeStore : ITracePrefsStore
    {
        public Dictionary<string, System.Text.Json.Nodes.JsonNode?> Values { get; } = new(StringComparer.Ordinal);
        public int Writes { get; private set; }

        public System.Text.Json.Nodes.JsonNode? Get(string key) => Values.TryGetValue(key, out var v) ? v?.DeepClone() : null;

        public void Set(string key, System.Text.Json.Nodes.JsonNode? value)
        {
            Writes++;
            if (value is null) Values.Remove(key);
            else Values[key] = value.DeepClone();
        }
    }

    [Fact]
    public void Positions_and_the_details_switch_survive_a_restart()
    {
        var was = TraceInspectorSwitch.On;
        try
        {
            TraceInspectorSwitch.Set(false);
            var first = new FakeStore();
            TracePrefs.Use(first);
            TraceLayouts.Write("topology", new Dictionary<string, Pt> { ["hub"] = new(10.5, -4), ["p:a"] = new(300, 20) });
            TraceInspectorSwitch.Set(true);
            Assert.Equal("""{"layouts":{"topology":{"hub":{"x":10.5,"y":-4},"p:a":{"x":300,"y":20}}},"inspector":true}""",
                first.Values[TracePrefs.Key]!.ToJsonString());
            TracePrefs.Use(first); // the same store again: nothing re-read or written
            Assert.Equal(2, first.Writes);

            // "restart": the switch is off in memory, a new store holds what the first one saved
            TracePrefs.Use(null);
            TraceInspectorSwitch.Set(false);
            Assert.Empty(TraceLayouts.Read("topology"));
            var second = new FakeStore();
            second.Values[TracePrefs.Key] = first.Values[TracePrefs.Key]!.DeepClone();
            TracePrefs.Use(second);
            Assert.True(TraceInspectorSwitch.On);
            Assert.Equal(new Pt(10.5, -4), TraceLayouts.Read("topology")["hub"]);
            Assert.Equal(new Pt(300, 20), TraceLayouts.Read("topology")["p:a"]);

            // reset layout removes the key; the switch stays
            TraceLayouts.Write("topology", new Dictionary<string, Pt>());
            Assert.Equal("""{"inspector":true}""", second.Values[TracePrefs.Key]!.ToJsonString());
        }
        finally
        {
            TracePrefs.Use(null);
            TraceInspectorSwitch.Set(was);
        }
    }

    [Fact]
    public void Stored_layouts_drop_entries_that_are_not_points()
    {
        var map = System.Text.Json.Nodes.JsonNode.Parse("""{"a":{"x":1,"y":2},"b":{"x":"1","y":2},"c":{"x":1},"d":3}""")!.AsObject();
        var read = TracePrefs.ParseLayout(map);
        Assert.Equal([("a", new Pt(1, 2))], read.Select(kv => (kv.Key, kv.Value)));
    }

    // ------------------------------------------------------------------ transitions (React Flow duration: d3-zoom smooth zoom)

    [Fact]
    public void A_transition_starts_and_ends_exactly_on_its_views()
    {
        var from = new Viewport(10, 20, 1);
        var to = new Viewport(-200, 50, 0.6);
        var tween = new ViewportTween(from, to, 800, 600);
        Assert.Equal(from, tween.At(0));
        Assert.Equal(to, tween.At(1));
        Assert.Equal(to, tween.At(1.4));
        var mid = tween.At(0.5);
        Assert.InRange(mid.Zoom, Viewport.MinZoom, 1.0);
    }

    [Fact]
    public void A_pure_zoom_keeps_the_centre_and_moves_monotonically()
    {
        // zoomIn around the centre: the world point under the centre never moves, the zoom only grows
        var from = new Viewport(100, 50, 1);
        var to = from.ZoomAround(new Pt(400, 300), 1.2);
        var tween = new ViewportTween(from, to, 800, 600);
        var centre = from.ToWorld(new Pt(400, 300));
        var last = from.Zoom;
        for (var i = 1; i <= 10; i++)
        {
            var v = tween.At(i / 10.0);
            Assert.True(v.Zoom >= last - 1e-12);
            last = v.Zoom;
            var c = v.ToWorld(new Pt(400, 300));
            Assert.Equal(centre.X, c.X, 6);
            Assert.Equal(centre.Y, c.Y, 6);
        }
    }

    [Fact]
    public void A_long_pan_zooms_out_on_the_way()
    {
        // van Wijk & Nuij: travelling far at one zoom pulls back mid-way, then returns
        var from = new Viewport(0, 0, 1);
        var to = new Viewport(-4000, 0, 1);
        var mid = new ViewportTween(from, to, 800, 600).At(0.5);
        Assert.True(mid.Zoom < 1);
    }

    [Fact]
    public void Easing_is_d3_cubic_in_out()
    {
        Assert.Equal(0, ViewportTween.EaseCubicInOut(0), 12);
        Assert.Equal(0.5, ViewportTween.EaseCubicInOut(0.5), 12);
        Assert.Equal(1, ViewportTween.EaseCubicInOut(1), 12);
        Assert.Equal(0.032, ViewportTween.EaseCubicInOut(0.2), 12);
    }

    [Theory]
    [InlineData(960, false)]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(1440, true)]
    public void The_live_legend_needs_a_large_window(double width, bool shows) => Assert.Equal(shows, TopologyPageLogic.ShowsLegend(width));

    [Theory]
    [InlineData(0, 400, 100, 40, 2000, 0)]      // in view: stays
    [InlineData(0, 400, 500, 40, 2000, 140)]    // below: bottom edge
    [InlineData(600, 400, 500, 40, 2000, 500)]  // above: top edge
    [InlineData(0, 400, 500, 600, 2000, 500)]   // taller than the view: top
    [InlineData(0, 400, 1990, 40, 2000, 1600)]  // never past the end
    public void The_timeline_scrolls_only_as_far_as_needed(double offset, double viewport, double top, double height, double extent, double expected)
        => Assert.Equal(expected, TimelineScroll.Nearest(offset, viewport, top, height, extent));
}

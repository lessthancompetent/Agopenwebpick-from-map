// AgOpenWeb — headless route-planning API (Layer 2).
//
// Ported from the AgValoniaGPS-RoutePlanner fork's PlanRouteJob, trimmed to a
// web-first headless surface: the route options the fork read from DisplayConfig
// (pattern / headland passes / skip / block / angle) come in as explicit args
// from the web command; everything else (tool width, turn radius, clearance,
// boundary, vehicle pose) is read from AgOpenWeb's existing config + state.
//
// Web → PlanRoute(...) builds a RoutePlan and stashes it; the web then GETs
// /api/routeplan (GetRoutePlanJson) and draws the polylines client-side, the
// same lightweight pattern used for pick-from-map. No binary scene-protocol change.
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.RoutePlanning;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.RoutePlanning;
using AgOpenWeb.Services.Track;

namespace AgOpenWeb.ViewModels;

public partial class MainViewModel
{
    private IRoutePlanningService? _routePlannerBacking;
    private IRoutePlanningService RoutePlanner =>
        _routePlannerBacking ??= new RoutePlanningService(new PolygonOffsetService());

    /// <summary>The most recently planned coverage route, for the web preview.
    /// Always the composite of <see cref="_routeLayers"/> when layers exist.</summary>
    private RoutePlan? _currentRoutePlan;

    /// <summary>
    /// The plan as named layers — "Headland" (whole-boundary laps), one per split
    /// block ("A", "B", …), or "Main" (unsplit interior). Each layer replans
    /// independently (edit block B after block A is driven) and the web client
    /// toggles their visibility so the operator sees only the path being driven.
    /// </summary>
    private readonly List<(string Name, RoutePlan Plan)> _routeLayers = new();

    /// <summary>Per-block manual pass angles (label → degrees), persisted with the
    /// split lines. Absent = auto heading for that block.</summary>
    private readonly Dictionary<string, double> _routeBlockAngles = new();

    /// <summary>Block label for a stable split-region index: A, B, … Z, then numbers.</summary>
    private static string BlockLabel(int i) => i < 26 ? ((char)('A' + i)).ToString() : (i + 1).ToString();

    /// <summary>When driving a planned route, halt the sim at the path end (the
    /// record/playback feature leaves the user in control of speed, so this is gated).</summary>
    private bool _haltSimAtRouteEnd;

    /// <summary>Crossing-family heading (radians) of the active cross-drill plan —
    /// the live coverage-channel resolver marks/checks channel 1 while the tool
    /// runs along this family. Null = not a cross-drill job.</summary>
    private double? _crossFamilyBHeadingRad;

    /// <summary>Coverage channel per recorded point of the active route drive
    /// (parallel to State.RecordedPath.RecordedPoints) — carries the plan's
    /// segment channels through the sim replay, where heading can't tell the
    /// offset second headland set from the first.</summary>
    private int[]? _routeDriveChannels;

    // ---- Plan persistence (pause / resume) -----------------------------------
    // The planned route saves with the FIELD and reloads on open, so a job
    // interrupted by rain / refill / breakdown resumes on the SAME paths:
    // replanning from the shed would rebuild from a different start position
    // (different approach, ordering, weave rotation) and no longer line up
    // with the half-driven coverage.
    private const string RoutePlanFileName = "routeplan.json";
    private string? _routePlanLoadedFieldDir;

    private void SaveRoutePlanToField()
    {
        string? dir = ActiveField?.DirectoryPath;
        if (string.IsNullOrWhiteSpace(dir)) return;
        _routePlanLoadedFieldDir = dir;
        try
        {
            string path = System.IO.Path.Combine(dir, RoutePlanFileName);
            if (_routeLayers.Count == 0)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            var layersOut = new List<object>(_routeLayers.Count);
            foreach (var (name, p) in _routeLayers)
            {
                var segsOut = new List<object>(p.Segments.Count);
                foreach (var s in p.Segments)
                {
                    var pts = new List<double>(s.Points.Count * 3);
                    foreach (var pt in s.Points)
                    {
                        pts.Add(Math.Round(pt.Easting, 2));
                        pts.Add(Math.Round(pt.Northing, 2));
                        pts.Add(Math.Round(pt.Heading, 3));
                    }
                    segsOut.Add(new Dictionary<string, object>
                    { ["t"] = (int)s.Type, ["ch"] = s.Channel, ["pts"] = pts });
                }
                layersOut.Add(new Dictionary<string, object> { ["name"] = name, ["segs"] = segsOut });
            }
            var root = new Dictionary<string, object?>
            {
                ["w"] = _currentRoutePlan?.Metadata.ToolWidthMeters ?? 0,
                // The plan's drive geometry embeds this lateral tool offset —
                // resuming with a different implement config mis-drives by the
                // difference, so the loader warns on mismatch.
                ["toolOffset"] = _configStore.Tool.Offset,
                ["famB"] = _crossFamilyBHeadingRad,
                ["layers"] = layersOut,
            };
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(root));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save route plan: {ex.Message}";
        }
    }

    /// <summary>Rehydrate the saved plan when the active field changes (lazy —
    /// every plan entry point calls this first, like the split lines).</summary>
    private void EnsureRoutePlanLoaded()
    {
        string? dir = ActiveField?.DirectoryPath;
        if (dir == _routePlanLoadedFieldDir) return;
        _routePlanLoadedFieldDir = dir;
        _routeLayers.Clear();
        _currentRoutePlan = null;
        _crossFamilyBHeadingRad = null;
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            string path = System.IO.Path.Combine(dir, RoutePlanFileName);
            if (!System.IO.File.Exists(path)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
            var root = doc.RootElement;
            double toolW = root.TryGetProperty("w", out var w) ? w.GetDouble() : 0;
            if (root.TryGetProperty("famB", out var fb)
                && fb.ValueKind == System.Text.Json.JsonValueKind.Number)
                _crossFamilyBHeadingRad = fb.GetDouble();
            if (!root.TryGetProperty("layers", out var layers)
                || layers.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            foreach (var layer in layers.EnumerateArray())
            {
                string name = layer.GetProperty("name").GetString() ?? "Main";
                var segs = new List<RouteSegment>();
                foreach (var seg in layer.GetProperty("segs").EnumerateArray())
                {
                    var pts = seg.GetProperty("pts");
                    int count = pts.GetArrayLength() / 3;
                    var list = new List<Vec3>(count);
                    for (int i = 0; i < count; i++)
                        list.Add(new Vec3(
                            pts[i * 3].GetDouble(), pts[i * 3 + 1].GetDouble(), pts[i * 3 + 2].GetDouble()));
                    if (list.Count < 2) continue;
                    segs.Add(new RouteSegment(
                        (RouteSegmentType)seg.GetProperty("t").GetInt32(), list,
                        seg.TryGetProperty("ch", out var ch) ? ch.GetInt32() : 0));
                }
                if (segs.Count == 0) continue;
                var raw = new RoutePlan(segs, new RoutePlanMetadata(0, 0, 0, 0, 0, 0, toolW));
                _routeLayers.Add((name, Subplan(raw, 0, segs.Count)));
            }
            if (_routeLayers.Count > 0)
            {
                ComposeRouteLayers();
                RegisterRouteSteerTracks();
                // The saved drive geometry embeds the lateral tool offset it was
                // planned with — a different current offset mis-places every band
                // by the difference. Warn loudly; the operator replans or refits.
                double planOffset = root.TryGetProperty("toolOffset", out var po)
                    && po.ValueKind == System.Text.Json.JsonValueKind.Number ? po.GetDouble() : 0;
                if (Math.Abs(planOffset - _configStore.Tool.Offset) > 0.05)
                    StatusMessage = $"Route plan was made for tool offset {planOffset:0.0#} m "
                        + $"but the current tool is {_configStore.Tool.Offset:0.0#} m — REPLAN before drilling";
                else
                    StatusMessage = "Saved route plan restored — steer a Route track or Drive to continue";
            }
        }
        catch
        {
            _routeLayers.Clear();
            _currentRoutePlan = null;
        }
    }

    // ---- Field splitting -----------------------------------------------------
    // Operator-drawn split lines (two map taps each) carve the field into simpler
    // regions the way it would be worked by hand — e.g. an L into a tall and a wide
    // rectangle, each with straight passes in its own best direction, instead of
    // one compromise heading across the whole shape. Persisted per field.
    private readonly List<(Vec2 A, Vec2 B)> _routeSplitLines = new();
    private string? _routeSplitsFieldDir;
    private const string RouteSplitsFileName = "RouteSplits.json";

    /// <summary>Reload the split lines when the active field changes (lazy — no
    /// field-open hook needed; every split entry point calls this first).</summary>
    private void EnsureRouteSplitsLoaded()
    {
        string? dir = ActiveField?.DirectoryPath;
        if (dir == _routeSplitsFieldDir) return;
        _routeSplitsFieldDir = dir;
        _routeSplitLines.Clear();
        _routeBlockAngles.Clear();
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            string path = System.IO.Path.Combine(dir, RouteSplitsFileName);
            if (!System.IO.File.Exists(path)) return;
            string json = System.IO.File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            // v2 object {lines, angles}; v1 was a bare array of [e1,n1,e2,n2] rows.
            var lines = root.ValueKind == System.Text.Json.JsonValueKind.Object
                ? (root.TryGetProperty("lines", out var l) ? l : default)
                : root;
            if (lines.ValueKind == System.Text.Json.JsonValueKind.Array)
                foreach (var row in lines.EnumerateArray())
                    if (row.ValueKind == System.Text.Json.JsonValueKind.Array && row.GetArrayLength() >= 4)
                        _routeSplitLines.Add((
                            new Vec2(row[0].GetDouble(), row[1].GetDouble()),
                            new Vec2(row[2].GetDouble(), row[3].GetDouble())));
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("angles", out var ang)
                && ang.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var p in ang.EnumerateObject())
                    _routeBlockAngles[p.Name] = p.Value.GetDouble();
        }
        catch { /* unreadable splits file — start empty */ }
    }

    private void SaveRouteSplits()
    {
        string? dir = ActiveField?.DirectoryPath;
        if (string.IsNullOrWhiteSpace(dir)) return;
        try
        {
            string path = System.IO.Path.Combine(dir, RouteSplitsFileName);
            if (_routeSplitLines.Count == 0)
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                return;
            }
            var rows = new List<double[]>(_routeSplitLines.Count);
            foreach (var (a, b) in _routeSplitLines)
                rows.Add(new[] { a.Easting, a.Northing, b.Easting, b.Northing });
            System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(
                new { lines = rows, angles = _routeBlockAngles }));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't save split lines: {ex.Message}";
        }
    }

    /// <summary>Add a split line from two map taps (field-local metres). The line is
    /// extended to infinity when planning, so a short stroke across the waist works.</summary>
    public void AddRouteSplitLine(double e1, double n1, double e2, double n2)
    {
        if (ActiveField == null) { StatusMessage = "Open a field first to add a split"; return; }
        double dE = e2 - e1, dN = n2 - n1;
        if (Math.Sqrt(dE * dE + dN * dN) < 1.0)
        {
            StatusMessage = "Split taps too close together — tap two points across the field";
            return;
        }
        EnsureRouteSplitsLoaded();
        _routeSplitLines.Add((new Vec2(e1, n1), new Vec2(e2, n2)));
        SaveRouteSplits();
        StatusMessage = $"Split line {_routeSplitLines.Count} added — Plan Route to use it";
    }

    /// <summary>Remove all split lines for the open field.</summary>
    public void ClearRouteSplitLines()
    {
        EnsureRouteSplitsLoaded();
        if (_routeSplitLines.Count == 0) { StatusMessage = "No split lines to clear"; return; }
        _routeSplitLines.Clear();
        _routeBlockAngles.Clear();
        SaveRouteSplits();
        StatusMessage = "Split lines cleared";
    }

    /// <summary>Everything PlanRoute derives from config + field state before the
    /// pattern-specific work — shared with per-block planning so both plan with
    /// identical geometry (same edge offset, margins, radii, trailing extension).</summary>
    private sealed class RouteCtx
    {
        public List<Vec2> Pts = new();
        public List<IReadOnlyList<Vec2>>? Inners;
        public double EdgeOff, Width, PhysWidth, TurnRadius, MinTurn, HeadlandMargin,
            Clearance, CornerRadius, TrailExt;
        public int Passes;
        public Vec3? StartPos;
        /// <summary>Hand-built Field Builder headland interior (Line/Curve segments
        /// present): passes fill THIS polygon and the planner must not clobber the
        /// segments. Null when the headland is absent or planner-made (uniform).</summary>
        public List<Vec2>? CustomHeadland;
    }

    private RouteCtx? TryBuildRouteContext(int headlandPasses)
    {
        if (State.Field.ActiveField?.Boundary?.OuterBoundary is not { IsValid: true } outer)
            return null;

        // Trailed implements jackknife in reverse — the tool setting decides
        // whether any planner connector may fall back to a 3-point K-turn.
        RoutePlanner.AllowReverseTurns = _configStore.Tool.IsKTurnAllowed;
        // Lateral tool offset: combs stay tool centerlines; the assembled DRIVE
        // geometry shifts left-of-travel so the band lands on the comb.
        RoutePlanner.ToolOffset = _configStore.Tool.Offset;
        // Swept-path clearance for planned connectors: the implement BODY —
        // physical frame width (a spreader throws 15 m but is ~2.8 m of steel),
        // length behind its attachment, mount type — must clear a HARD fence and
        // every hard hole, the same way the live U-turn checks it.
        {
            var t = _configStore.Tool;
            var mount = t.IsToolFrontFixed ? AgOpenWeb.Models.Tool.ToolMount.FrontFixed
                      : t.IsToolTBT ? AgOpenWeb.Models.Tool.ToolMount.TBT
                      : t.IsToolTrailing ? AgOpenWeb.Models.Tool.ToolMount.Trailing
                      : AgOpenWeb.Models.Tool.ToolMount.RearFixed;
            double bodyWidth = t.PhysicalWidth > 0.1 ? t.PhysicalWidth : _configStore.ActualToolWidth;
            RoutePlanner.SweptToolGeometry = new AgOpenWeb.Models.Tool.ToolGeometry(
                Mount: mount,
                Width: bodyWidth,
                Offset: t.Offset,
                VehicleHitchLength: _configStore.Vehicle.HitchLength,
                ToolHitchLength: t.HitchLength,
                TrailingHitchLength: t.TrailingHitchLength,
                TrailingToolToPivotLength: t.TrailingToolToPivotLength,
                TankTrailingHitchLength: t.TankTrailingHitchLength,
                Length: t.Length);
            RoutePlanner.OuterBoundaryIsHard = outer.IsHard;
        }

        var ctx = new RouteCtx();
        var pts = new List<Vec2>(outer.Points.Count);
        foreach (var p in outer.Points) pts.Add(new Vec2(p.Easting, p.Northing));

        // Inner obstacles the route must avoid: only HARD inner boundaries (a
        // drive-through / soft exclusion is driven over — sections just switch off —
        // so it isn't routed around). Mirrors Boundary.IsPointInside, which ignores
        // drive-through inners.
        List<IReadOnlyList<Vec2>>? inners = null;
        var innerList = State.Field.ActiveField?.Boundary?.InnerBoundaries;
        if (innerList is { Count: > 0 })
        {
            inners = new List<IReadOnlyList<Vec2>>();
            foreach (var ib in innerList)
            {
                if (ib is not { IsValid: true } || ib.IsDriveThrough) continue;
                var ring = new List<Vec2>(ib.Points.Count);
                foreach (var p in ib.Points) ring.Add(new Vec2(p.Easting, p.Northing));
                if (ring.Count >= 3) inners.Add(ring);
            }
            if (inners.Count == 0) inners = null;
        }
        ctx.Inners = inners;

        // First-pass edge offset: pull the plan boundary in so the outer lap's tool
        // edge stays clear of a fence that sits exactly ON the mapped line.
        ctx.EdgeOff = Math.Max(0, _configStore.Guidance.RouteFirstPassOffsetM);
        if (ctx.EdgeOff > 0.01)
        {
            var edgeInset = new PolygonOffsetService().CreateInwardOffset(pts, ctx.EdgeOff);
            if (edgeInset is { Count: >= 3 }) pts = edgeInset;
        }
        ctx.Pts = pts;

        double width = _configStore.ActualToolWidth;
        if (width <= 0.1) width = 6.0;
        ctx.Width = width;
        // Physical frame width for obstacle clearance (small obstacles get swerved by the
        // frame, not the spread). 0 = fall back to working width (no swerve distinction).
        ctx.PhysWidth = _configStore.Tool.PhysicalWidth;
        double turnRadius = _configStore.Guidance.UTurnRadius;
        if (turnRadius <= 0.1) turnRadius = width / 2.0;

        double minTurn = _configStore.Vehicle.MinTurningRadius;
        if (double.IsNaN(minTurn) || double.IsInfinity(minTurn) || minTurn <= 0.1)
            minTurn = turnRadius;
        ctx.MinTurn = minTurn;
        ctx.TurnRadius = Math.Max(turnRadius, minTurn);
        ctx.CornerRadius = minTurn;

        // Headland width + lap count. Auto (0) uses the route-planner rule
        // max(3·minTurn, width); a manual pass count fixes it to that many widths.
        if (headlandPasses > 0)
        {
            ctx.Passes = headlandPasses;
            ctx.HeadlandMargin = ctx.Passes * width;
        }
        else
        {
            ctx.HeadlandMargin = RoutePlanningService.RecommendHeadlandWidth(width, minTurn);
            ctx.Passes = Math.Max(1, (int)Math.Round(ctx.HeadlandMargin / Math.Max(width, 0.1)));
        }

        // Drive-to-start: current machine pose (skip if no fix).
        double vE = State.Vehicle.Easting, vN = State.Vehicle.Northing;
        ctx.StartPos = (vE == 0 && vN == 0)
            ? (Vec3?)null
            : new Vec3(vE, vN, State.Vehicle.Heading * Math.PI / 180.0);

        ctx.Clearance = Math.Max(0, _configStore.Guidance.UTurnDistanceFromBoundary);

        // The tool trails behind the tractor (hitch + trailing drawbar); pass ends
        // are extended by this so the TOOL reaches the headland line before the
        // turn — otherwise every pass's coverage stops short of the headland.
        ctx.TrailExt = Math.Abs(ConfigStore.Tool.HitchLength)
            + (ConfigStore.Tool.IsToolTrailing ? Math.Abs(ConfigStore.Tool.TrailingHitchLength) : 0);

        // A hand-built headland (any Line/Curve segment drawn in the Field Builder)
        // is the operator's own demarcation — adopt it: passes fill its interior,
        // laps trace its shape, and the planner must NOT replace the segments.
        // Planner-made headlands are whole-Boundary segments and keep the uniform
        // margin flow above.
        bool handBuilt = false;
        foreach (var seg in HeadlandSegments)
            if (seg.Type != Models.Headland.HeadlandSegmentType.Boundary) { handBuilt = true; break; }
        if (handBuilt && State.Field.HeadlandLine is { Count: >= 4 } hl)
        {
            var ring = new List<Vec2>(hl.Count);
            int last = hl.Count;
            // drop the closing duplicate point if present
            if (Math.Abs(hl[0].Easting - hl[^1].Easting) < 0.01 && Math.Abs(hl[0].Northing - hl[^1].Northing) < 0.01)
                last--;
            for (int i = 0; i < last; i++) ring.Add(new Vec2(hl[i].Easting, hl[i].Northing));
            if (ring.Count >= 3) ctx.CustomHeadland = ring;
        }
        return ctx;
    }

    /// <summary>
    /// Headland lap paths for a hand-built (variable-width) headland: outward
    /// offsets of the built headland line toward the fence at half-width spacing,
    /// outermost first (drive outside-in, same as planned laps), joined with
    /// Approach connectors. The innermost lap's tool edge lands ON the built line.
    /// </summary>
    private RoutePlan? BuildCustomHeadlandLaps(RouteCtx ctx, int laps)
    {
        if (ctx.CustomHeadland == null || laps <= 0) return null;
        var offset = new PolygonOffsetService();
        var segs = new List<RouteSegment>();
        Vec3? prevEnd = null;
        for (int k = laps - 1; k >= 0; k--)
        {
            var ring2 = offset.CreateOutwardOffset(ctx.CustomHeadland, (k + 0.5) * ctx.Width);
            if (ring2 is not { Count: >= 3 }) continue;
            // start the ring at the vertex nearest the previous lap's end
            int start = 0;
            if (prevEnd is { } pe)
            {
                double best = double.MaxValue;
                for (int i = 0; i < ring2.Count; i++)
                {
                    double dE = ring2[i].Easting - pe.Easting, dN = ring2[i].Northing - pe.Northing;
                    double d = dE * dE + dN * dN;
                    if (d < best) { best = d; start = i; }
                }
            }
            var pts = new List<Vec3>(ring2.Count + 1);
            for (int i = 0; i <= ring2.Count; i++)
            {
                var p = ring2[(start + i) % ring2.Count];
                pts.Add(new Vec3(p.Easting, p.Northing, 0));
            }
            if (prevEnd is { } pv)
                segs.Add(new RouteSegment(RouteSegmentType.Approach, new List<Vec3> { pv, pts[0] }));
            segs.Add(new RouteSegment(RouteSegmentType.Headland, pts));
            prevEnd = pts[^1];
        }
        if (segs.Count == 0) return null;
        // metadata via the shared per-segment recompute
        var plan = new RoutePlan(segs, new RoutePlanMetadata(0, 0, 0, 0, 0, 0, ctx.Width));
        return Subplan(plan, 0, segs.Count);
    }

    /// <summary>Lap count for a hand-built headland when the panel says auto (0):
    /// average fence→headland gap (area difference over perimeter) in tool widths.</summary>
    private static int EstimateCustomLaps(IReadOnlyList<Vec2> fence, IReadOnlyList<Vec2> headland, double width)
    {
        double AreaOf(IReadOnlyList<Vec2> poly)
        {
            double a = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                a += p.Easting * q.Northing - q.Easting * p.Northing;
            }
            return Math.Abs(a / 2.0);
        }
        double per = 0;
        for (int i = 0; i < headland.Count; i++)
        {
            var p = headland[i]; var q = headland[(i + 1) % headland.Count];
            per += Math.Sqrt((q.Easting - p.Easting) * (q.Easting - p.Easting) + (q.Northing - p.Northing) * (q.Northing - p.Northing));
        }
        if (per < 1) return 1;
        double avgGap = (AreaOf(fence) - AreaOf(headland)) / per;
        return Math.Clamp((int)Math.Round(avgGap / Math.Max(width, 0.1)), 1, 6);
    }

    /// <summary>
    /// Plan a coverage route for the open field and stash it for the web preview.
    /// Pattern: 0 auto, 1 skip, 2 cross-drill, 3 spiral, 4 block.
    /// headlandPasses 0 = auto (route-planner rule); skipCount/blockSkip used by
    /// the skip/cross/block patterns; angleDeg rotates the field-aligned passes.
    /// </summary>
    public void PlanRoute(int pattern, int headlandPasses, int skipCount, int blockSkip, double angleDeg, bool cornerFill = false,
        double? headingOverrideRad = null, IReadOnlyList<Vec3>? refCurve = null,
        int headlandStyle = 0, bool headlandFirst = true, bool headlandBackCut = false,
        double rowSpacingM = 0, bool crossWeave = false, bool slopeCorrected = true)
    {
        // Clear any prior plan up front so the web client, which polls
        // /api/routeplan for the result, can't pick up a stale plan while this
        // (potentially slow) obstacle-aware planning runs.
        _currentRoutePlan = null;
        _routeLayers.Clear();

        var ctx = TryBuildRouteContext(headlandPasses);
        if (ctx == null)
        {
            StatusMessage = "Open a field with a boundary to plan a route";
            return;
        }

        // Operator's headland style — picked up by every plan assembly below
        // (including the auto-orientation trials and split-field blocks).
        RoutePlanner.HeadlandStyle =
            (Models.RoutePlanning.RouteHeadlandStyle)Math.Clamp(headlandStyle, 0, 3);
        RoutePlanner.HeadlandFirstPhase = headlandFirst;
        RoutePlanner.HeadlandBackCut = headlandBackCut;

        // Already-worked outer laps drop out of the plan: the boundary-recording
        // lap driven with the tool on (or any lap finished before a replan) shows
        // as coverage along the lap line, so the route doesn't ask for it again.
        // Band geometry is untouched — only the drive paths are skipped.
        RoutePlanner.HeadlandSkipOuterLaps = CountWorkedOuterLaps(ctx);

        // Slope-corrected pass spacing from the recorded Terrain map: on a
        // side slope, map-spaced passes sit width/cos(slope) apart on the
        // GROUND — a sliver of miss per pass that accumulates across a face.
        // No terrain data (or the toggle off) → uniform spacing as before.
        RoutePlanner.ElevationSampler = slopeCorrected && _elevationMapService.CellCount > 0
            ? _elevationMapService.GetAltitude
            : null;
        var pts = ctx.Pts;
        var inners = ctx.Inners;
        double edgeOff = ctx.EdgeOff, width = ctx.Width, physWidth = ctx.PhysWidth,
            turnRadius = ctx.TurnRadius, headlandMargin = ctx.HeadlandMargin,
            clearance = ctx.Clearance, cornerRadius = ctx.CornerRadius, trailExt = ctx.TrailExt;
        int passes = ctx.Passes;
        Vec3? startPos = ctx.StartPos;

        // Turn ordering. Mirror the fork's per-pattern skip/block derivation, but
        // keyed off the explicit args rather than DisplayConfig.
        bool spiral = pattern == 3;
        bool cross = pattern == 2;
        // Auto + narrow tool (2R > W): use the BLOCK plotter, not the lane/skip comb.
        // The comb's serpentine lane transitions land on an ADJACENT pass (a 1-width
        // side-step) — exactly the loop-turn case skipping exists to avoid; the block
        // sequence keeps EVERY consecutive pair >= skip rows apart, so with
        // skip = ceil(2R/W) every turn is a plain wide U-turn (no loops/shunts).
        bool narrowAuto = pattern == 0 && 2.0 * turnRadius > width;
        int skipPasses = pattern switch
        {
            1 => Math.Max(0, skipCount),
            0 => 0,   // narrowAuto routes through blkSkip below; wide tools serpentine plainly
            2 => Math.Max(0, skipCount),
            _ => 0,
        };
        int blkSkip =
            narrowAuto ? (int)Math.Ceiling(2.0 * turnRadius / Math.Max(width, 0.1))
            : pattern == 4 ? Math.Max(1, blockSkip)
            : (pattern == 2 && skipPasses == 0) ? Math.Max(1, blockSkip)
            : 0;
        double crossAngleRad = 90.0 * Math.PI / 180.0;
        double angleRad = angleDeg * Math.PI / 180.0;
        double heading = headingOverrideRad ?? (LongestEdgeHeading(pts) + angleRad);

        // Auto-orientation: unless the user has dialled in a manual angle, quickly try
        // several candidate pass headings and keep the one with the lowest estimated
        // drive time (work + turns + a per-turn overhead). Orientation is the biggest
        // efficiency lever — the wrong one can nearly double the turn count. The trial
        // plans skip obstacle handling for speed (it barely changes the ranking); the
        // winning heading is then planned in full below.
        EnsureRouteSplitsLoaded();
        bool useSplits = !spiral && !cross && refCurve == null && _routeSplitLines.Count > 0;

        if (!spiral && !cross && !useSplits && refCurve == null && !headingOverrideRad.HasValue
            && Math.Abs(angleDeg) < 0.01)
        {
            double bestSecs = double.MaxValue;
            foreach (double h in CandidateHeadings(pts))
            {
                // Obstacle-aware but fast: passes are split around obstacles (so the
                // turn count is honest) while the pretty reroute/smoothing is skipped.
                var trial = RoutePlanner.GenerateBoustrophedon(pts, width, turnRadius, headlandMargin, h,
                    SwathPattern.Boustrophedon, passes, startPos, 0, false, false, clearance,
                    skipPasses, blkSkip, cornerRadius, inners, false, true, physWidth, trailExt);
                if (trial == null) continue;
                double secs = RoutePlanningService.EstimateWorkSeconds(
                    trial.Metadata, RouteWorkSpeedMps, RouteTurnSpeedMps, RouteTurnOverheadSec);
                if (secs < bestSecs) { bestSecs = secs; heading = h; }
            }
        }

        // Split lines beat pattern selection: each block is planned as its own
        // LAYER ("A", "B", …) with its stored per-block angle (or auto heading),
        // the whole-boundary headland laps ride the first block's plan and are
        // sliced into their own "Headland" layer. A manual panel angle overrides
        // every block for this plan. Blocks chain nearest label order (A, B, …),
        // approaches connecting them.
        bool manualAngle = Math.Abs(angleDeg) >= 0.01 || headingOverrideRad.HasValue;
        // Hand-built Field Builder headland: passes fill ITS interior instead of a
        // uniform inset, and the laps trace its (possibly variable-width) shape.
        var customHl = ctx.CustomHeadland;
        int customLaps = customHl == null ? 0
            : headlandPasses > 0 ? headlandPasses
            : EstimateCustomLaps(pts, customHl, width);
        if (useSplits)
        {
            var regions = RoutePlanner.ComputeSplitRegions(pts, _routeSplitLines);
            if (regions.Count > 1)
            {
                if (customHl != null)
                {
                    var laps = BuildCustomHeadlandLaps(ctx, customLaps);
                    if (laps != null) _routeLayers.Add(("Headland", laps));
                }
                Vec3? cursor = startPos;
                for (int i = 0; i < regions.Count; i++)
                {
                    string label = BlockLabel(i);
                    double? h = manualAngle ? heading
                        : _routeBlockAngles.TryGetValue(label, out var ba)
                            ? LongestEdgeHeading(pts) + ba * Math.PI / 180.0
                            : (double?)null;
                    // With a custom headland the laps are prepended above, so no
                    // block carries them (headlandPasses 0) and the shared inset is
                    // the built headland polygon.
                    var bp = RoutePlanner.GenerateSplitField(pts, _routeSplitLines, width, turnRadius,
                        headlandMargin, SwathPattern.Boustrophedon,
                        customHl == null && i == 0 ? passes : 0, cursor,
                        clearance, skipPasses, blkSkip, cornerRadius, inners, physWidth, trailExt,
                        h, null, i, customHl);
                    if (bp == null || bp.Segments.Count == 0) continue;
                    if (customHl == null && i == 0 && passes > 0)
                    {
                        var (laps, rest, lapsFirst) = SplitLapsAffix(bp);
                        if (laps != null && lapsFirst) _routeLayers.Add(("Headland", laps));
                        if (rest.Segments.Count > 0) _routeLayers.Add((label, rest));
                        if (laps != null && !lapsFirst) _routeLayers.Add(("Headland", laps));
                    }
                    else
                    {
                        _routeLayers.Add((label, bp));
                    }
                    var lastSeg = bp.Segments[^1];
                    if (lastSeg.Points.Count > 0) cursor = lastSeg.Points[^1];
                }
            }
        }
        bool splitApplied = _routeLayers.Count > 0;
        if (useSplits && !splitApplied)
            StatusMessage = "Split lines don't divide this field — planned as one piece";

        if (!splitApplied)
        {
            RoutePlan? plan = refCurve != null
                ? RoutePlanner.GenerateAlongCurve(pts, refCurve, width, turnRadius, headlandMargin,
                    SwathPattern.Boustrophedon, passes, startPos, false, false, clearance, skipPasses, blkSkip, cornerRadius)
                : spiral
                    ? RoutePlanner.GenerateSpiral(pts, width, startPos, clearance, cornerRadius, cornerFill, inners)
                    : cross
                        ? (crossWeave
                            // Woven cross-drill. Straightening: the leg extension puts
                            // ~2× the trailing length of straight tractor travel around
                            // the working line (ext before it + trailExt until the tool
                            // arrives) — the same settle mechanism as every pattern, so
                            // no extra run-in is added (a pulled-back Dubins target
                            // forces loop turns on tight Vs). Rotation is trialled
                            // inside unless the operator set an angle.
                            ? RoutePlanner.GenerateCrossDrillWoven(pts, width, turnRadius, headlandMargin,
                                heading, crossAngleRad, passes, startPos, clearance, cornerRadius, inners,
                                trailExt, rowSpacingM,
                                trialHeadings: !headingOverrideRad.HasValue && Math.Abs(angleDeg) < 0.01)
                            : RoutePlanner.GenerateCrossDrill(pts, width, turnRadius, headlandMargin, heading, crossAngleRad,
                                SwathPattern.Boustrophedon, passes, startPos, 0, false, false, clearance, skipPasses, blkSkip,
                                cornerRadius, inners, rowSpacingM))
                        : RoutePlanner.GenerateBoustrophedon(pts, width, turnRadius, headlandMargin, heading,
                            SwathPattern.Boustrophedon, customHl == null ? passes : 0, startPos, 0, false, false,
                            clearance, skipPasses, blkSkip, cornerRadius, inners,
                            physicalToolWidth: physWidth, passEndExtension: trailExt,
                            cultivatedOverride: customHl);
            if (plan != null && plan.Segments.Count > 0)
            {
                if (customHl != null && refCurve == null && !spiral)
                {
                    var laps = BuildCustomHeadlandLaps(ctx, customLaps);
                    if (laps != null) _routeLayers.Add(("Headland", laps));
                    _routeLayers.Add(("Main", plan));
                }
                else
                {
                    var (laps, rest, lapsFirst) = SplitLapsAffix(plan);
                    RoutePlan body = rest;
                    RoutePlan? tail = null;
                    if (laps != null && lapsFirst)
                    {
                        // A TRAILING lap set can also exist — the cross-drill's
                        // offset second headland set, or the back-cut. Its own
                        // layer ("Headland 2") keeps it separately steerable.
                        var (laps2, rest2, lapsFirst2) = SplitLapsAffix(rest);
                        if (laps2 != null && !lapsFirst2) { tail = laps2; body = rest2; }
                    }
                    if (laps != null && lapsFirst) _routeLayers.Add(("Headland", laps));
                    if (body.Segments.Count > 0) _routeLayers.Add(("Main", body));
                    if (laps != null && !lapsFirst) _routeLayers.Add(("Headland", laps));
                    if (tail != null) _routeLayers.Add(("Headland 2", tail));
                }
            }
        }

        ComposeRouteLayers();
        if (_currentRoutePlan == null)
        {
            StatusMessage = "Route planning produced no plan for this field";
            return;
        }

        // Cross-drill: remember the crossing family's heading so the live
        // coverage-channel resolver can tell the two families apart while
        // driving (channel 1 = aligned with family B).
        _crossFamilyBHeadingRad = cross ? heading + crossAngleRad : (double?)null;

        // Auto-headland: demarcate the planned headland band as the native headland
        // line so the native headland toggle / section-in-headland control just works.
        // Replace, don't stack: drop prior whole-boundary segments first (each replan
        // would otherwise add another "Boundary N" row to the Field Builder list).
        // A hand-built headland (Line/Curve segments) IS the demarcation — leave it
        // completely alone.
        if (customHl == null)
        {
            try
            {
                for (int i = HeadlandSegments.Count - 1; i >= 0; i--)
                    if (HeadlandSegments[i].Type == Models.Headland.HeadlandSegmentType.Boundary)
                        RemoteDeleteHeadlandAt(i);
                RemoteCreateHeadlandWholeBoundary(edgeOff + headlandMargin);
            }
            catch { /* headland is a convenience here — never fail the plan on it */ }
        }

        // Make the plan's paths available as native guidance lines immediately,
        // and persist the plan with the field so an interrupted job resumes on
        // the SAME paths after a restart.
        RegisterRouteSteerTracks();
        SaveRoutePlanToField();

        var m = _currentRoutePlan.Metadata;
        double areaHa = m.WorkDistanceMeters * m.ToolWidthMeters / 10000.0;
        double estMin = RoutePlanningService.EstimateWorkSeconds(
            m, RouteWorkSpeedMps, RouteTurnSpeedMps, RouteTurnOverheadSec) / 60.0;
        double hdgDeg = ((heading * 180.0 / Math.PI) % 180.0 + 180.0) % 180.0;
        int blockCount = 0;
        foreach (var (name, _) in _routeLayers) if (name != "Headland" && name != "Main") blockCount++;
        string hdgTxt = splitApplied
            ? (manualAngle ? $"{blockCount} blocks @ {hdgDeg:F0}°" : $"{blockCount} blocks, per-block headings")
            : $"@ {hdgDeg:F0}°";
        int skippedLaps = RoutePlanner.HeadlandSkipOuterLaps;
        string hlTxt = customHl != null
            ? $"(hand-built headland, {customLaps} laps)"
            : $"(headland {headlandMargin:F1} m, {passes} laps"
              + (skippedLaps > 0 ? $", {skippedLaps} already worked — skipped)" : ")");
        StatusMessage = $"Route: {m.SwathCount} passes, {m.TurnCount} turns, " +
            $"{m.TotalDistanceMeters / 1000.0:F2} km, {areaHa:F1} ha, ~{estMin:F0} min {hdgTxt} {hlTxt}";
    }

    /// <summary>
    /// Plan the route referenced to the SELECTED saved track (Tracks manager /
    /// Field Builder): an AB line sets the pass direction exactly; a curve makes
    /// the passes follow its shape (offset copies clipped to the field). Splits
    /// are bypassed — the track IS the reference.
    /// </summary>
    public void PlanRouteAlongSelectedTrack(int headlandPasses, int skipCount, int blockSkip,
        int headlandStyle = 0, bool headlandFirst = true, bool headlandBackCut = false)
    {
        var track = SelectedTrack;
        if (track?.Points is not { Count: >= 2 } tp)
        {
            StatusMessage = "Select a track first (Tracks manager) — AB line or curve";
            return;
        }
        if (track.Name.StartsWith("Route ", StringComparison.Ordinal))
        {
            StatusMessage = "That's a planned route path — pick a hand-made AB line or curve";
            return;
        }
        if (tp.Count == 2)
        {
            double hdg = Math.Atan2(tp[1].Easting - tp[0].Easting, tp[1].Northing - tp[0].Northing);
            PlanRoute(0, headlandPasses, skipCount, blockSkip, 0, false, headingOverrideRad: hdg,
                headlandStyle: headlandStyle, headlandFirst: headlandFirst, headlandBackCut: headlandBackCut);
            if (_currentRoutePlan != null)
                StatusMessage = $"Route aligned to '{track.Name}' — " + StatusMessage;
        }
        else
        {
            PlanRoute(0, headlandPasses, skipCount, blockSkip, 0, false, refCurve: tp,
                headlandStyle: headlandStyle, headlandFirst: headlandFirst, headlandBackCut: headlandBackCut);
            if (_currentRoutePlan != null)
                StatusMessage = $"Route follows curve '{track.Name}' — " + StatusMessage;
        }
    }

    /// <summary>
    /// Two map taps → a real AB line track, added to the field's tracks and
    /// selected — the Route Planner's "plot the reference right here" shortcut
    /// (the client follows up with route.planTrack). Reusable afterwards like
    /// any hand-built AB line.
    /// </summary>
    public void CreateAbTrackFromPoints(double aE, double aN, double bE, double bN)
    {
        double dE = bE - aE, dN = bN - aN;
        if (Math.Sqrt(dE * dE + dN * dN) < 2.0)
        {
            StatusMessage = "Points too close together — tap two points a few metres apart";
            return;
        }
        double h = Math.Atan2(dE, dN);
        int n = 1;
        foreach (var t in SavedTracks)
            if (t.Name.StartsWith("AB ", StringComparison.Ordinal)) n++;
        var track = new Models.Track.Track
        {
            Name = $"AB {n}",
            Points = new List<Vec3> { new(aE, aN, h), new(bE, bN, h) },
            Type = Models.Track.TrackType.ABLine,
            IsVisible = true,
        };
        SavedTracks.Add(track);
        SelectedTrack = track;
        SaveTracksToFile();   // a plotted AB is a real field track — survives reopen
        StatusMessage = $"'{track.Name}' plotted and selected";
    }

    /// <summary>Discard the current route preview.</summary>
    public void ClearRoutePlan()
    {
        if (State.RecordedPath.IsDrivingRecordedPath) StopRouteDrive();
        _currentRoutePlan = null;
        _routeLayers.Clear();
        _crossFamilyBHeadingRad = null;
        // Drop the plan's registered "Route *" steer tracks. RegisterRouteSteerTracks
        // only prunes them on the NEXT plan, so without this a clear leaves "Route
        // Headland"/"Route Main"/… in the Tracks list — still visible and selectable —
        // and the route looks like it never cleared. Deactivate first if one of them is
        // the active guidance line (SelectedTrack = null clears the map track + guidance).
        if (SelectedTrack != null && SelectedTrack.Name.StartsWith("Route ", StringComparison.Ordinal))
            SelectedTrack = null;
        bool removedTrack = false;
        for (int i = SavedTracks.Count - 1; i >= 0; i--)
            if (SavedTracks[i].Name.StartsWith("Route ", StringComparison.Ordinal))
            {
                SavedTracks.RemoveAt(i);
                removedTrack = true;
            }
        if (removedTrack) SaveTracksToFile();
        SaveRoutePlanToField();   // empty layers → deletes the saved plan
        StatusMessage = "Route cleared";
    }

    // ---- Route layers --------------------------------------------------------

    private static double PathLen(IReadOnlyList<Vec3> pts)
    {
        double d = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double dE = pts[i].Easting - pts[i - 1].Easting, dN = pts[i].Northing - pts[i - 1].Northing;
            d += Math.Sqrt(dE * dE + dN * dN);
        }
        return d;
    }

    /// <summary>A plan from a segment range of <paramref name="src"/>, metadata
    /// recomputed from those segments so layer sums match the whole.</summary>
    private static RoutePlan Subplan(RoutePlan src, int from, int to)
    {
        var segs = new List<RouteSegment>(to - from);
        int swaths = 0, turns = 0; double tot = 0, work = 0, turnM = 0;
        for (int i = from; i < to; i++)
        {
            var s = src.Segments[i];
            segs.Add(s);
            double d = PathLen(s.Points);
            tot += d;
            if (s.Type == RouteSegmentType.Swath) { swaths++; work += d; }
            else if (s.Type == RouteSegmentType.Headland) work += d;
            else { turnM += d; if (s.Type == RouteSegmentType.Turn) turns++; }
        }
        return new RoutePlan(segs, new RoutePlanMetadata(swaths, tot, 0, work, turnM, turns, src.Metadata.ToolWidthMeters));
    }

    /// <summary>Slice a plan at the interior fill: the headland laps (plus their
    /// drive-to-start / connector) versus the interior body. Headland-first
    /// plans carry the laps as a PREFIX before the first pass; headland-last
    /// (mow-style) plans carry them as a SUFFIX after the last pass —
    /// <c>LapsFirst</c> says which, so callers keep the drive order when they
    /// register the layers. Laps side is null when the plan has no laps.</summary>
    private static (RoutePlan? Laps, RoutePlan Body, bool LapsFirst) SplitLapsAffix(RoutePlan plan)
    {
        int firstSwath = -1, lastSwath = -1;
        bool lapsBefore = false, lapsAfter = false;
        for (int i = 0; i < plan.Segments.Count; i++)
        {
            var t = plan.Segments[i].Type;
            if (t == RouteSegmentType.Swath)
            {
                if (firstSwath < 0) firstSwath = i;
                lastSwath = i;
            }
            else if (t == RouteSegmentType.Headland)
            {
                if (firstSwath < 0) lapsBefore = true;
                else lapsAfter = true;
            }
        }
        if (firstSwath > 0 && lapsBefore)
            return (Subplan(plan, 0, firstSwath), Subplan(plan, firstSwath, plan.Segments.Count), true);
        if (lastSwath >= 0 && lapsAfter)
            return (Subplan(plan, lastSwath + 1, plan.Segments.Count), Subplan(plan, 0, lastSwath + 1), false);
        return (null, plan, true);
    }

    /// <summary>Rebuild the composite plan (what Drive and the stats read) from the
    /// named layers, metadata summed across them.</summary>
    private void ComposeRouteLayers()
    {
        if (_routeLayers.Count == 0) { _currentRoutePlan = null; return; }
        var segs = new List<RouteSegment>();
        int swaths = 0, turns = 0; double tot = 0, work = 0, turnM = 0, est = 0, toolW = 0;
        foreach (var (_, p) in _routeLayers)
        {
            segs.AddRange(p.Segments);
            var m = p.Metadata;
            swaths += m.SwathCount; turns += m.TurnCount; tot += m.TotalDistanceMeters;
            work += m.WorkDistanceMeters; turnM += m.TurnDistanceMeters; est += m.EstimatedSeconds;
            toolW = Math.Max(toolW, m.ToolWidthMeters);
        }
        _currentRoutePlan = new RoutePlan(segs, new RoutePlanMetadata(swaths, tot, est, work, turnM, turns, toolW));
    }

    /// <summary>
    /// Plan (or re-plan) ONE split block as its own layer, leaving every other
    /// layer untouched — so block B's path can change after block A is planned or
    /// even driven. Stores the block's angle (0 = auto) with the splits, so a full
    /// re-plan keeps each block's chosen direction. No headland laps (those come
    /// from Plan Route / the Headland layer).
    /// </summary>
    public void PlanRouteBlock(string label, int headlandPasses, double angleDeg)
    {
        EnsureRouteSplitsLoaded();
        if (_routeSplitLines.Count == 0) { StatusMessage = "Draw split lines first"; return; }
        var ctx = TryBuildRouteContext(headlandPasses);
        if (ctx == null) { StatusMessage = "Open a field with a boundary to plan a route"; return; }

        var regions = RoutePlanner.ComputeSplitRegions(ctx.Pts, _routeSplitLines);
        int idx = -1;
        for (int i = 0; i < regions.Count; i++)
            if (BlockLabel(i) == label) { idx = i; break; }
        if (idx < 0) { StatusMessage = $"No block {label} in this field"; return; }

        bool manual = Math.Abs(angleDeg) >= 0.01;
        if (manual) _routeBlockAngles[label] = angleDeg; else _routeBlockAngles.Remove(label);
        SaveRouteSplits();

        // Same narrow-tool turn ordering rule as pattern 0 in PlanRoute.
        int blk = 2.0 * ctx.TurnRadius > ctx.Width
            ? (int)Math.Ceiling(2.0 * ctx.TurnRadius / Math.Max(ctx.Width, 0.1)) : 0;
        double? h = manual ? LongestEdgeHeading(ctx.Pts) + angleDeg * Math.PI / 180.0 : (double?)null;
        var bp = RoutePlanner.GenerateSplitField(ctx.Pts, _routeSplitLines, ctx.Width, ctx.TurnRadius,
            ctx.HeadlandMargin, SwathPattern.Boustrophedon, 0, ctx.StartPos, ctx.Clearance, 0, blk,
            ctx.CornerRadius, ctx.Inners, ctx.PhysWidth, ctx.TrailExt, h, null, idx, ctx.CustomHeadland);
        if (bp == null || bp.Segments.Count == 0)
        {
            StatusMessage = $"Couldn't plan block {label}";
            return;
        }

        // Replace just this block's layer, keeping order: Headland, A, B, …, Main.
        for (int i = _routeLayers.Count - 1; i >= 0; i--)
            if (_routeLayers[i].Name == label) _routeLayers.RemoveAt(i);
        int ins = _routeLayers.Count;
        for (int i = 0; i < _routeLayers.Count; i++)
        {
            string n = _routeLayers[i].Name;
            // A leading Headland layer stays first; a trailing one (headland-last
            // style) stays last — blocks slot in between, in label order.
            if (n == "Headland") { if (i == 0) continue; ins = i; break; }
            if (n == "Main" || string.CompareOrdinal(n, label) > 0) { ins = i; break; }
        }
        _routeLayers.Insert(ins, (label, bp));
        ComposeRouteLayers();
        RegisterRouteSteerTracks();
        SaveRoutePlanToField();

        var m = bp.Metadata;
        double estMin = RoutePlanningService.EstimateWorkSeconds(
            m, RouteWorkSpeedMps, RouteTurnSpeedMps, RouteTurnOverheadSec) / 60.0;
        StatusMessage = $"Block {label}: {m.SwathCount} passes, {m.TurnCount} turns, ~{estMin:F0} min "
            + (manual ? $"@ {angleDeg:F0}° from field default" : "(auto heading)")
            + " — steer via track 'Route " + label + "'";
    }

    /// <summary>
    /// How many OUTERMOST headland laps are already worked, sampled from the
    /// coverage map along each lap's centre line (channel 0, ≥80% covered,
    /// consecutive from the fence inward). This is what lets "drive the first
    /// lap while recording the boundary" just work: that lap's paint is on the
    /// map, so the next plan leaves the lap out.
    /// </summary>
    private int CountWorkedOuterLaps(RouteCtx ctx)
    {
        if (!_coverageMapService.IsFieldBoundsSet || ctx.Passes <= 0) return 0;
        var offset = new PolygonOffsetService();
        int worked = 0;
        for (int k = 0; k < ctx.Passes; k++)
        {
            var ring = offset.CreateInwardOffset(ctx.Pts, (k + 0.5) * ctx.Width);
            if (ring is not { Count: >= 3 }) break;
            int total = 0, covered = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                double len = Math.Sqrt(
                    (b.Easting - a.Easting) * (b.Easting - a.Easting)
                    + (b.Northing - a.Northing) * (b.Northing - a.Northing));
                int steps = Math.Max(1, (int)(len / 3.0));
                for (int s = 0; s < steps; s++)
                {
                    double t = (s + 0.5) / steps;
                    total++;
                    if (_coverageMapService.IsPointCoveredInChannel(
                        a.Easting + (b.Easting - a.Easting) * t,
                        a.Northing + (b.Northing - a.Northing) * t, 0))
                        covered++;
                }
            }
            if (total == 0 || covered < total * 0.8) break;
            worked++;
        }
        return worked;
    }

    /// <summary>
    /// The coverage channel the tool is working right now (cross-drill jobs).
    /// Resolution order: (1) an active route drive uses the plan's own segment
    /// channels (the only way to tell the offset second headland set from the
    /// first); (2) steering the "Route Headland 2" track forces channel 1;
    /// (3) otherwise heading classification — within 45° (mod 180°) of the
    /// crossing family's heading = channel 1. Not a cross-drill job = 0.
    /// </summary>
    private int ResolveCoverageChannel(double toolHeadingRad)
    {
        if (State.RecordedPath.IsDrivingRecordedPath && _routeDriveChannels is { Length: > 0 } chans)
        {
            int idx = Math.Clamp(State.RecordedPath.CurrentPositionIndex, 0, chans.Length - 1);
            return chans[idx];
        }
        if (_crossFamilyBHeadingRad is not { } famB) return 0;
        if (SelectedTrack?.Name == "Route Headland 2") return 1;

        double h = ((toolHeadingRad % Math.PI) + Math.PI) % Math.PI;
        double b = ((famB % Math.PI) + Math.PI) % Math.PI;
        double d = Math.Abs(h - b);
        if (d > Math.PI / 2) d = Math.PI - d;
        return d < Math.PI / 4 ? 1 : 0;
    }

    /// <summary>Area centroid of a polygon (falls back to vertex average when degenerate).</summary>
    private static Vec2 PolyCentroid(IReadOnlyList<Vec2> poly)
    {
        double a2 = 0, cE = 0, cN = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i]; var q = poly[(i + 1) % poly.Count];
            double cr = p.Easting * q.Northing - q.Easting * p.Northing;
            a2 += cr; cE += (p.Easting + q.Easting) * cr; cN += (p.Northing + q.Northing) * cr;
        }
        if (Math.Abs(a2) < 1e-6)
        {
            double sE = 0, sN = 0;
            foreach (var p in poly) { sE += p.Easting; sN += p.Northing; }
            return new Vec2(sE / Math.Max(1, poly.Count), sN / Math.Max(1, poly.Count));
        }
        return new Vec2(cE / (3.0 * a2), cN / (3.0 * a2));
    }

    /// <summary>
    /// The split blocks for the web overlay: label + centroid (label anchor) +
    /// the stored manual angle (absent = auto). <c>{"blocks":[{"label":"A","e":…,
    /// "n":…,"angle":…}]}</c>; empty list when the field has no splits.
    /// </summary>
    public string GetRouteBlocksJson()
    {
        EnsureRouteSplitsLoaded();
        if (_routeSplitLines.Count == 0
            || State.Field.ActiveField?.Boundary?.OuterBoundary is not { IsValid: true } outer)
            return "{\"blocks\":[]}";
        var pts = new List<Vec2>(outer.Points.Count);
        foreach (var p in outer.Points) pts.Add(new Vec2(p.Easting, p.Northing));
        var regions = RoutePlanner.ComputeSplitRegions(pts, _routeSplitLines);
        if (regions.Count <= 1) return "{\"blocks\":[]}";

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(512);
        sb.Append("{\"blocks\":[");
        for (int i = 0; i < regions.Count; i++)
        {
            var c = PolyCentroid(regions[i]);
            if (i > 0) sb.Append(',');
            sb.Append("{\"label\":\"").Append(BlockLabel(i)).Append("\",\"e\":")
              .Append(c.Easting.ToString("0.0", inv)).Append(",\"n\":")
              .Append(c.Northing.ToString("0.0", inv));
            if (_routeBlockAngles.TryGetValue(BlockLabel(i), out var a))
                sb.Append(",\"angle\":").Append(a.ToString("0.#", inv));
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>
    /// Drive the planned route in the simulator. Flatten the plan into recorded-path
    /// points and hand them to AgOpenWeb's recorded-path playback engine (Dubins
    /// approach to the start, then Pure-Pursuit along the path); then set a forward sim
    /// speed so it moves. Sections replay ON over worked passes (Swath/Headland) and OFF
    /// over turns/transit. The sim speed slider still adjusts pace; StopRouteDrive ends it.
    /// </summary>
    /// <summary>
    /// Turn the planned route into a normal guidance track (a curve) and select it, so the
    /// operator drives it with ordinary autosteer. The plan is split at the first interior
    /// pass: <paramref name="headland"/> = the headland laps (+ drive-to-start) as one path,
    /// else the main-paddock fill (passes/turns/loops). Each is a separate SavedTrack, so one
    /// can be part-driven, the other started, and switched back to — coverage tracks progress.
    /// </summary>
    /// <summary>
    /// True if a segment's points reverse direction mid-path (a Reeds-Shepp 3-point
    /// shunt: consecutive travel directions flip ~180°). The sim/steer followers are
    /// forward-only — feeding them a reversing leg makes the lookahead land behind the
    /// vehicle and commands hard-lock steering (spin-outs) — so those legs are spliced
    /// out of follower paths and replaced by a straight join. The rendered plan keeps
    /// them: on a real machine the operator (or a future reverse-capable follower)
    /// performs the shunt.
    /// </summary>
    private static bool ContainsReversal(IReadOnlyList<Vec3> pts)
    {
        for (int i = 2; i < pts.Count; i++)
        {
            double d1e = pts[i - 1].Easting - pts[i - 2].Easting, d1n = pts[i - 1].Northing - pts[i - 2].Northing;
            double d2e = pts[i].Easting - pts[i - 1].Easting, d2n = pts[i].Northing - pts[i - 1].Northing;
            double l1 = Math.Sqrt(d1e * d1e + d1n * d1n), l2 = Math.Sqrt(d2e * d2e + d2n * d2n);
            if (l1 < 1e-6 || l2 < 1e-6) continue;
            if ((d1e * d2e + d1n * d2n) / (l1 * l2) < -0.5) return true;
        }
        return false;
    }

    /// <summary>
    /// Build a steer path from route segments as a curve Track, or null when too
    /// short. Reverse shunts are spliced to a straight join (forward-only followers).
    /// </summary>
    private Models.Track.Track? BuildSegmentsTrack(string name, IEnumerable<RouteSegment> source)
    {
        var pts = new List<Vec3>();
        foreach (var seg in source)
        {
            if (seg.Points.Count >= 2 && ContainsReversal(seg.Points))
            {   // forward-only follower: replace the shunt with a straight join
                pts.Add(seg.Points[0]);
                pts.Add(seg.Points[^1]);
                continue;
            }
            foreach (var p in seg.Points) pts.Add(p);
        }
        if (pts.Count < 2) return null;

        return new Models.Track.Track
        {
            Name = name,
            Points = Models.Guidance.CurveProcessing.CalculateHeadings(pts),
            Type = Models.Track.TrackType.Curve,
            IsVisible = true,
            IsClosed = false,
        };
    }

    /// <summary>Install a route track into SavedTracks, replacing any prior copy;
    /// keeps the selection pointing at the fresh instance when it was selected.</summary>
    private Models.Track.Track InstallRouteTrack(Models.Track.Track track)
    {
        bool wasSelected = SelectedTrack != null && SelectedTrack.Name == track.Name;
        for (int i = SavedTracks.Count - 1; i >= 0; i--)
            if (SavedTracks[i].Name == track.Name) SavedTracks.RemoveAt(i);
        SavedTracks.Add(track);
        if (wasSelected) SelectedTrack = track;
        return track;
    }

    /// <summary>
    /// Register EVERY route layer as an ordinary saved track ("Route Headland",
    /// "Route A", "Route B", …, plus "Route Main" — the whole non-headland body)
    /// right after planning, so they appear in the native Tracks manager alongside
    /// AB lines — separately selectable there and engageable with the normal
    /// autosteer button or an external engage switch. Stale route tracks from a
    /// prior plan (removed blocks) are dropped. Runs on every successful plan.
    /// </summary>
    private void RegisterRouteSteerTracks()
    {
        var current = new List<string>();
        foreach (var (name, p) in _routeLayers)
        {
            var t = BuildSegmentsTrack("Route " + name, p.Segments);
            if (t != null) { InstallRouteTrack(t); current.Add(t.Name); }
        }
        if (!current.Contains("Route Main"))
        {
            var mainSegs = new List<RouteSegment>();
            foreach (var (name, p) in _routeLayers)
                if (name != "Headland") mainSegs.AddRange(p.Segments);
            var mt = BuildSegmentsTrack("Route Main", mainSegs);
            if (mt != null) { InstallRouteTrack(mt); current.Add(mt.Name); }
        }
        for (int i = SavedTracks.Count - 1; i >= 0; i--)
            if (SavedTracks[i].Name.StartsWith("Route ", StringComparison.Ordinal)
                && !current.Contains(SavedTracks[i].Name))
                SavedTracks.RemoveAt(i);
    }

    public void ActivateRouteSteerPath(bool headland)
    {
        EnsureRoutePlanLoaded();
        if (_routeLayers.Count == 0) { StatusMessage = "Plan a route first"; return; }
        string name = headland ? "Route Headland" : "Route Main";
        Models.Track.Track? track = null;
        foreach (var t in SavedTracks) if (t.Name == name) { track = t; break; }
        if (track == null)
        {
            StatusMessage = headland ? "This plan has no headland laps" : "Route path too short to steer";
            return;
        }
        SelectedTrack = track;
        StatusMessage = $"{name} active — engage autosteer to follow it (switch anytime)";
    }

    public void DriveRoute()
    {
        EnsureRoutePlanLoaded();
        var plan = _currentRoutePlan;
        if (plan == null) { StatusMessage = "Plan a route first"; return; }

        const double driveSpeedKph = 8.0;

        // Flatten the plan to (E, N, working, channel) — sections ON over worked
        // passes; the channel rides along so the replay can drive the coverage
        // channel (cross-drill family B / offset second headland = 1).
        var raw = new List<(double e, double n, bool work, int ch)>();
        foreach (var seg in plan.Segments)
        {
            bool working = seg.Type == RouteSegmentType.Swath || seg.Type == RouteSegmentType.Headland;
            if (seg.Points.Count >= 2 && ContainsReversal(seg.Points))
            {   // forward-only follower: straight join instead of the reverse shunt
                raw.Add((seg.Points[0].Easting, seg.Points[0].Northing, working, seg.Channel));
                raw.Add((seg.Points[^1].Easting, seg.Points[^1].Northing, working, seg.Channel));
                continue;
            }
            foreach (var p in seg.Points) raw.Add((p.Easting, p.Northing, working, seg.Channel));
        }
        if (raw.Count < 2) { StatusMessage = "Route too short to drive"; return; }

        // Resample to a dense, uniform ~1 m spacing. AgOpenWeb's recorded-path follower
        // looks a fixed few POINTS ahead and snaps to the globally-nearest forward point,
        // so coarse route vertices (a straight swath is just 2 points tens of metres apart)
        // make the look-ahead overshoot and can let the vehicle jump onto an adjacent lap.
        // Dense points keep the nearest point sequential and the look-ahead ~2-3 m.
        const double step = 1.0;
        var pts = new List<RecPathPoint>(raw.Count * 8);
        var chans = new List<int>(raw.Count * 8);
        pts.Add(new RecPathPoint(raw[0].e, raw[0].n, 0, driveSpeedKph, raw[0].work));
        chans.Add(raw[0].ch);
        double carry = 0; // distance already travelled past the last emitted point
        for (int i = 1; i < raw.Count; i++)
        {
            var a = raw[i - 1]; var b = raw[i];
            double dE = b.e - a.e, dN = b.n - a.n;
            double len = Math.Sqrt(dE * dE + dN * dN);
            if (len < 1e-9) continue;
            double d = step - carry;                       // first new point on this segment
            for (; d <= len; d += step)
            {
                double t = d / len;
                pts.Add(new RecPathPoint(a.e + dE * t, a.n + dN * t, 0, driveSpeedKph, b.work));
                chans.Add(b.ch);
            }
            carry = len - (d - step);                      // leftover into the next segment
        }
        // Point headings: face the next point (the last keeps the previous heading).
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var p = pts[i]; var q = pts[i + 1];
            p.Heading = Math.Atan2(q.Easting - p.Easting, q.Northing - p.Northing);
            pts[i] = p;
        }
        if (pts.Count < 5) { StatusMessage = "Route too short to drive"; return; }

        State.RecordedPath.RecordedPoints = pts;
        State.RecordedPath.CurrentPositionIndex = 0;
        _routeDriveChannels = chans.ToArray();
        if (!StartDrivingRecordedPath())
        {
            _routeDriveChannels = null;
            StatusMessage = "Couldn't start driving the route";
            return;
        }
        _haltSimAtRouteEnd = true;
        if (IsSimulatorEnabled) SimulatorSpeedKph = driveSpeedKph; // get moving; slider still adjusts
        StatusMessage = "Driving route…";
    }

    /// <summary>Stop driving the route and halt the simulator.</summary>
    public void StopRouteDrive()
    {
        _haltSimAtRouteEnd = false;
        _routeDriveChannels = null;
        StopDrivingRecordedPath();
        if (IsSimulatorEnabled) SimulatorSpeedKph = 0;
        StatusMessage = "Route drive stopped";
    }

    /// <summary>
    /// The current plan as JSON for the web preview, in the active map plane (metres):
    /// <c>{"segments":[{"type":"Swath","pts":[[e,n],…]},…],"meta":{…}}</c>.
    /// Coordinates are emitted at 0.1 m precision — plenty for a visual preview.
    /// Empty object when there's no plan.
    /// </summary>
    public string GetRoutePlanJson()
    {
        var inv = CultureInfo.InvariantCulture;
        EnsureRouteSplitsLoaded();
        EnsureRoutePlanLoaded();
        var splits = new StringBuilder("[");
        for (int i = 0; i < _routeSplitLines.Count; i++)
        {
            var (a, b) = _routeSplitLines[i];
            if (i > 0) splits.Append(',');
            splits.Append('[')
                .Append(a.Easting.ToString("0.0", inv)).Append(',')
                .Append(a.Northing.ToString("0.0", inv)).Append(',')
                .Append(b.Easting.ToString("0.0", inv)).Append(',')
                .Append(b.Northing.ToString("0.0", inv)).Append(']');
        }
        splits.Append(']');

        var plan = _currentRoutePlan;
        if (plan == null || _routeLayers.Count == 0) return $"{{\"splits\":{splits}}}";
        var sb = new StringBuilder(64 * 1024);
        sb.Append("{\"layers\":[");
        bool firstLayer = true;
        foreach (var (name, lp) in _routeLayers)
        {
            if (!firstLayer) sb.Append(',');
            firstLayer = false;
            sb.Append("{\"name\":\"").Append(name).Append("\",\"segments\":[");
            bool firstSeg = true;
            foreach (var seg in lp.Segments)
            {
                if (seg.Points == null || seg.Points.Count == 0) continue;
                if (!firstSeg) sb.Append(',');
                firstSeg = false;
                sb.Append("{\"type\":\"").Append(seg.Type).Append('"');
                if (seg.Channel != 0) sb.Append(",\"ch\":").Append(seg.Channel.ToString(inv));
                sb.Append(",\"pts\":[");
                bool firstPt = true;
                foreach (var p in seg.Points)
                {
                    if (!firstPt) sb.Append(',');
                    firstPt = false;
                    sb.Append('[')
                      .Append(p.Easting.ToString("0.0", inv)).Append(',')
                      .Append(p.Northing.ToString("0.0", inv)).Append(']');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
        }
        sb.Append("],\"meta\":{");
        var m = plan.Metadata;
        sb.Append("\"swaths\":").Append(m.SwathCount.ToString(inv))
          .Append(",\"turns\":").Append(m.TurnCount.ToString(inv))
          .Append(",\"distanceM\":").Append(m.TotalDistanceMeters.ToString("0.0", inv))
          .Append(",\"workM\":").Append(m.WorkDistanceMeters.ToString("0.0", inv))
          .Append(",\"turnM\":").Append(m.TurnDistanceMeters.ToString("0.0", inv))
          .Append(",\"toolWidthM\":").Append(m.ToolWidthMeters.ToString("0.00", inv))
          .Append("},\"splits\":").Append(splits).Append('}');
        return sb.ToString();
    }

    /// <summary>Heading (north-referenced) of the longest edge of a polygon.</summary>
    private static double LongestEdgeHeading(IReadOnlyList<Vec2> poly)
    {
        double bestLenSq = -1, bestHeading = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % n];
            double dE = b.Easting - a.Easting, dN = b.Northing - a.Northing;
            double lenSq = dE * dE + dN * dN;
            if (lenSq > bestLenSq)
            {
                bestLenSq = lenSq;
                bestHeading = Math.Atan2(dE, dN);
            }
        }
        return bestHeading;
    }

    // Machine speed model for route scoring/ETA — per-profile settings, editable in the
    // Route Planner panel (defaults 8 / 6 km/h, 4 s per turn).
    private double RouteWorkSpeedMps => Math.Max(0.5, _configStore.Guidance.RouteWorkSpeedKmh) / 3.6;
    private double RouteTurnSpeedMps => Math.Max(0.5, _configStore.Guidance.RouteTurnSpeedKmh) / 3.6;
    private double RouteTurnOverheadSec => Math.Max(0, _configStore.Guidance.RouteTurnOverheadSec);

    /// <summary>
    /// Candidate pass headings to trial for auto-orientation: the field's longest edge
    /// and its perpendicular (the natural alignments), plus a coarse 15° sweep so a
    /// diagonal optimum isn't missed. Folded to [0, π) and de-duplicated to ~3°.
    /// </summary>
    private static List<double> CandidateHeadings(IReadOnlyList<Vec2> poly)
    {
        var cand = new List<double>();
        void Add(double h)
        {
            h = ((h % Math.PI) + Math.PI) % Math.PI;
            foreach (var e in cand)
            {
                double d = Math.Abs(h - e);
                if (d < 0.06 || Math.Abs(d - Math.PI) < 0.06) return;
            }
            cand.Add(h);
        }
        double le = LongestEdgeHeading(poly);
        Add(le);
        Add(le + Math.PI / 2.0);
        for (int deg = 0; deg < 180; deg += 15) Add(deg * Math.PI / 180.0);
        return cand;
    }
}

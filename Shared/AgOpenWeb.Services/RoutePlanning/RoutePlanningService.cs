// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.RoutePlanning;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Models.YouTurn;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.Track;
using Clipper2Lib;

namespace AgOpenWeb.Services.RoutePlanning;

/// <inheritdoc />
public sealed class RoutePlanningService : IRoutePlanningService
{
    // Default forward speed used only for the time estimate (m/s ≈ 9 km/h).
    private const double EstimateSpeedMps = 2.5;

    private readonly IPolygonOffsetService _offset;

    /// <inheritdoc />
    public RouteHeadlandStyle HeadlandStyle { get; set; } = RouteHeadlandStyle.Laps;

    /// <inheritdoc />
    public bool HeadlandFirstPhase { get; set; } = true;

    /// <inheritdoc />
    public bool HeadlandBackCut { get; set; }

    /// <inheritdoc />
    public int HeadlandSkipOuterLaps { get; set; }

    /// <inheritdoc />
    public bool AllowReverseTurns { get; set; } = true;

    /// <summary>Lateral tool offset (metres, positive = tool RIGHT of travel —
    /// ToolConfig.Offset). Generators plan TOOL-centerline combs unchanged; at
    /// assembly, once each leg's drive direction is final, the DRIVE geometry
    /// is shifted left-of-travel by this amount so the tool band lands back on
    /// the comb (same model as GuidanceGeometry.VehicleDistAway). Feasibility
    /// checks use the TIGHT vehicle spacing (w − 2·|offset|).</summary>
    public double ToolOffset { get; set; }

    /// <summary>Implement body geometry for the swept-path clearance check on
    /// planned connectors (null = tractor-path checks only, the old behaviour).
    /// Width here is the PHYSICAL frame width (a spreader throwing 15 m is
    /// ~2.8 m of steel), length is the body behind its attachment — a mounted
    /// drill 3 m behind the axle swings well outside the tractor's own arc.
    /// Same model the live U-turn uses (ImplementSweptPath + TurnClearance).</summary>
    public ToolGeometry? SweptToolGeometry { get; set; }

    /// <summary>The outer boundary is a hard fence: the swept implement must stay
    /// inside it (with the clearance margin). Soft/drive-through outers keep the
    /// tractor-path-only rule, matching the live turn's hardOuter gate. Hard
    /// inner holes are always enforced against the swept body.</summary>
    public bool OuterBoundaryIsHard { get; set; }

    /// <summary>Everything a connector candidate must clear with its swept body.</summary>
    private sealed record SweptConstraint(
        ToolGeometry Geom,
        IReadOnlyList<Vec2>? HardOuter,
        IReadOnlyList<IReadOnlyList<Vec2>>? Holes,
        double Margin);

    /// <summary>True when the implement's four body-corner rails along
    /// <paramref name="path"/> stay inside the hard outer (if any) and outside
    /// every hard hole, each by the margin.</summary>
    private static bool SweptClear(IReadOnlyList<Vec3> path, SweptConstraint? c)
        => SweptIntrusion(path, c) <= 0;

    /// <summary>Worst implement-body intrusion (metres past the margin) of
    /// <paramref name="path"/> against the hard outer and every hard hole; ≤ 0
    /// = clear. Lets the turn builder pick the LEAST-intruding candidate when
    /// nothing clears outright, instead of the shortest clipping one.</summary>
    private static double SweptIntrusion(IReadOnlyList<Vec3> path, SweptConstraint? c)
    {
        if (c == null || path.Count < 2) return double.NegativeInfinity;
        var swept = ImplementSweptPath.Compute(path, c.Geom);
        double worst = double.NegativeInfinity;
        if (c.HardOuter is { Count: >= 3 })
            worst = Math.Max(worst, TurnClearance.Evaluate(swept, c.HardOuter,
                TurnClearance.KeepSide.Inside, c.Margin).MaxIntrusion);
        if (c.Holes != null)
            foreach (var h in c.Holes)
                if (h is { Count: >= 3 })
                    worst = Math.Max(worst, TurnClearance.Evaluate(swept, h,
                        TurnClearance.KeepSide.Outside, c.Margin).MaxIntrusion);
        return worst;
    }

    /// <inheritdoc />
    public Func<double, double, double?>? ElevationSampler { get; set; }

    public RoutePlanningService(IPolygonOffsetService offset)
    {
        _offset = offset;
    }

    /// <summary>
    /// Recommended number of headland passes for a tool that turns with the
    /// given radius: enough perimeter laps to give room for the U-turn
    /// (≈ one turning diameter), clamped to a sane 1–3. The headland inset is
    /// then <c>passes × toolWidth</c>.
    /// </summary>
    public static int RecommendHeadlandPasses(double toolWidth, double turnRadius, double toolOffset = 0)
    {
        if (toolWidth <= 0) return 1;
        // Offset tool: the binding constraint is the TIGHT vehicle-line spacing
        // (w − 2·|o|) — the hardest turn the headland must accommodate.
        double tight = Math.Max(0.5, toolWidth - 2.0 * Math.Abs(toolOffset));
        int passes = (int)Math.Ceiling((2.0 * turnRadius) / tight);
        return Math.Clamp(passes, 1, 3);
    }

    /// <summary>
    /// Recommended headland WIDTH (metres): at least three minimum turning radii
    /// — room to complete the U-turn — but never less than one implement width.
    /// Matches the common route-planner default; recompute when the implement
    /// (or vehicle geometry) changes.
    /// </summary>
    public static double RecommendHeadlandWidth(double toolWidth, double minTurnRadius, double toolOffset = 0)
    {
        double r = minTurnRadius > 0 && !double.IsInfinity(minTurnRadius) ? minTurnRadius : 0;
        // An offset tool's vehicle line strays |o| further toward the fence on
        // one direction of travel — reserve that extra room.
        return Math.Max(3.0 * r + Math.Abs(toolOffset), Math.Max(toolWidth, 0));
    }

    /// <summary>
    /// Recommended swath ordering: when the turning diameter exceeds one tool
    /// width the machine can't flip 180° into the adjacent swath without a
    /// tight omega turn, so leap-frog (skip) ordering is more efficient.
    /// Otherwise plain back-and-forth.
    /// </summary>
    public static SwathPattern RecommendPattern(double toolWidth, double turnRadius, double toolOffset = 0)
        => (2.0 * turnRadius > Math.Max(0.5, toolWidth - 2.0 * Math.Abs(toolOffset)))
            ? SwathPattern.Snake : SwathPattern.Boustrophedon;

    public RoutePlan? GenerateBoustrophedon(
        IReadOnlyList<Vec2> outerBoundary,
        double swathWidth,
        double turnRadius,
        double headlandMargin,
        double? headingRad = null,
        SwathPattern pattern = SwathPattern.Boustrophedon,
        int headlandPasses = 0,
        Vec3? startPos = null,
        double swathOffset = 0,
        bool swapEnds = false,
        bool startOppositeSide = false,
        double boundaryClearance = 0,
        int skipPasses = 0,
        int blockSkip = 0,
        double cornerRadius = 0,
        IReadOnlyList<IReadOnlyList<Vec2>>? innerBoundaries = null,
        bool addPondLoops = true,
        bool fastScore = false,
        double physicalToolWidth = 0,
        double passEndExtension = 0,
        IReadOnlyList<Vec2>? cultivatedOverride = null)
    {
        if (outerBoundary == null || outerBoundary.Count < 3 || swathWidth <= 0)
            return null;

        var boundary = new List<Vec2>(outerBoundary);

        // 1. Cultivated polygon = boundary inset by the headland margin. A split-field
        // region call overrides this with (region ∩ full-field inset): the interior
        // passes then fill only the region, while `boundary` stays the TRUE fence so
        // headland laps and turn validation see the whole field.
        var cultivated = boundary;
        if (cultivatedOverride is { Count: >= 3 })
        {
            cultivated = new List<Vec2>(cultivatedOverride);
        }
        else if (headlandMargin > 0)
        {
            var inset = _offset.CreateInwardOffset(boundary, headlandMargin);
            if (inset is { Count: >= 3 }) cultivated = inset;
        }

        // 2. Heading: caller-supplied (e.g. an AB line), else the longest edge —
        // of the WORKED area, so a split region orients to its own shape.
        double theta = headingRad ?? LongestEdgeHeading(cultivatedOverride is { Count: >= 3 } ? cultivated : boundary);
        double dE = Math.Sin(theta), dN = Math.Cos(theta);    // travel direction
        double pE = Math.Cos(theta), pN = -Math.Sin(theta);   // perpendicular (spacing axis)

        // 1b. Inner obstacles (ponds). Two inflations:
        //  - holes: uniform (tool half-width + clearance) — used for the turn/connector
        //    reroute, which needs all-round clearance.
        //  - clipHoles: anisotropic — inflated by the tool half-width only PERPENDICULAR to
        //    the passes (the one direction the swath band can overlap the pond), so passes
        //    reach right up to the faces they hit head-on. A uniform inflation over-clips
        //    those faces and its rounded corners leave triangular wedge gaps at the corners.
        // Small obstacles (footprint <= a working width) whose PHYSICAL tool width is
        // narrower than its spread get swerved around, not treated as a full field hole:
        // the passes stay continuous, deviating only enough for the physical frame to
        // clear (section control shuts the spread off over the footprint). Larger
        // obstacles keep the split-and-loop treatment.
        double physWidth = physicalToolWidth > 0.1 ? physicalToolWidth : swathWidth;
        bool haveSwerve = physWidth < swathWidth - 0.1;

        List<List<Vec2>>? holes = null, clipHoles = null, swerveHoles = null;
        if (innerBoundaries is { Count: > 0 })
        {
            holes = new List<List<Vec2>>();
            clipHoles = new List<List<Vec2>>();
            swerveHoles = new List<List<Vec2>>();
            double clr = Math.Max(0, boundaryClearance);
            double inflate = swathWidth / 2.0 + clr;
            foreach (var h in innerBoundaries)
            {
                if (h is not { Count: >= 3 }) continue;
                if (haveSwerve && ObstacleMaxExtent(h) <= swathWidth)
                {
                    // Swerve: inflate only by the physical half-width + clearance.
                    var s = _offset.CreateOutwardOffset(new List<Vec2>(h), physWidth / 2.0 + clr);
                    swerveHoles.Add(s is { Count: >= 3 } ? s : new List<Vec2>(h));
                    continue;
                }
                var infl = _offset.CreateOutwardOffset(new List<Vec2>(h), inflate);
                holes.Add(infl is { Count: >= 3 } ? infl : new List<Vec2>(h));
                clipHoles.Add(InflatePerp(h, pE, pN, swathWidth / 2.0, clr + swathWidth * 0.05));
            }
            if (holes.Count == 0) holes = null;
            if (clipHoles.Count == 0) clipHoles = null;
            if (swerveHoles.Count == 0) swerveHoles = null;
        }

        var o = Centroid(cultivated);

        // Range of the cultivated polygon along the perpendicular axis.
        double pmin = double.MaxValue, pmax = double.MinValue;
        foreach (var v in cultivated)
        {
            double proj = (v.Easting - o.Easting) * pE + (v.Northing - o.Northing) * pN;
            if (proj < pmin) pmin = proj;
            if (proj > pmax) pmax = proj;
        }
        if (pmax - pmin <= 0) return null;

        // 3. Parallel swath lines clipped to the cultivated polygon — kept in
        // spatial order across the field (each oriented entry -> exit). The
        // offset shifts the whole comb sideways within one swath spacing; start
        // a step early / end a step late so nothing is dropped at the edges.
        double off = ((swathOffset % swathWidth) + swathWidth) % swathWidth;
        // Each parallel line -> its passes (left-to-right along the travel axis). A line
        // crossing a pond yields >1 pass; those get clustered into separate blocks below.
        var lines = new List<List<List<Vec3>>>();
        double sStep = swathWidth;
        for (double s = pmin + swathWidth / 2.0 + off - swathWidth; s <= pmax + swathWidth; s += sStep)
        {
            sStep = swathWidth;   // default; slope sampling below may shrink it
            var lp = new Vec2(o.Easting + s * pE, o.Northing + s * pN);
            var segs = ClipSegments(lp, dE, dN, cultivated, clipHoles);
            if (segs.Count == 0) continue;
            // Slope-corrected comb: the NEXT pass steps by width·cos(cross-slope)
            // so on-ground spacing stays a full tool width on side slopes.
            sStep = swathWidth * MeanCrossSlopeCos(pE, pN, segs);
            var passes = new List<List<Vec3>>(segs.Count);
            // Trailing-tool overshoot: passes are TRACTOR paths, but coverage comes
            // from the tool trailing behind. Without extending the working ends, the
            // tractor turns away at the headland line and the tool (metres behind)
            // never reaches it — every pass's paint stops short of the headland by
            // the trailing offset. Extend both ends into the band so the TOOL
            // reaches the line before the turn starts; clamped to the band depth.
            double ext = Math.Max(0, passEndExtension);
            if (headlandMargin > 0) ext = Math.Min(ext, headlandMargin);
            foreach (var seg in segs)
            {
                double h = Math.Atan2(seg.Exit.Easting - seg.Entry.Easting,
                                       seg.Exit.Northing - seg.Entry.Northing);
                passes.Add(new List<Vec3>
                {
                    new Vec3(seg.Entry.Easting - dE * ext, seg.Entry.Northing - dN * ext, h),
                    new Vec3(seg.Exit.Easting + dE * ext, seg.Exit.Northing + dN * ext, h),
                });
            }
            lines.Add(passes);
        }
        if (lines.Count == 0) return null;

        // 4. Cluster passes into blocks (per Hameed/Höffmann): while the number of passes
        //    per line is stable, each column feeds one block; when it changes (entering or
        //    leaving a pond) close the current blocks and open a fresh set. Each block is a
        //    contiguous obstacle-free strip covered by a simple back-and-forth — so the
        //    field left/right of a pond becomes two blocks driven one after the other,
        //    instead of turning across the pond on every row.
        var blocks = new List<List<List<Vec3>>>();
        List<List<List<Vec3>>>? current = null;
        int prevCount = -1;
        foreach (var passes in lines)
        {
            int m = passes.Count;
            if (m != prevCount)
            {
                current = new List<List<List<Vec3>>>(m);
                for (int i = 0; i < m; i++) { var b = new List<List<Vec3>>(); current.Add(b); blocks.Add(b); }
                prevCount = m;
            }
            for (int i = 0; i < m; i++) current![i].Add(passes[i]);
        }

        // 5. Order each block (sequential / leap-frog / skip) and concatenate; assemble
        //    turns + headland + approach across the whole sequence.
        var ordered = new List<List<Vec3>>();
        foreach (var block in blocks)
        {
            if (block.Count == 0) continue;
            var order =
                blockSkip > 0 ? SwathOrderingService.GenerateBlockSequence(block.Count, blockSkip)
                : skipPasses > 0 ? SwathOrderingService.GenerateSkipSequence(block.Count, skipPasses)
                : SwathOrderingService.GenerateSequence(block.Count, pattern);
            if (swapEnds) order.Reverse();
            foreach (int idx in order) ordered.Add(block[idx]);
        }
        if (ordered.Count == 0) return null;

        // Turn lead-in/out: pull each inter-pass U-turn OFF the application edge, deeper
        // into the reserved headland, so the tractor+trailed tool are settled straight on
        // the line before the worked pass resumes AND drive straight out past the pass end
        // before the turn curves away (both ends — symmetric). Tool-up connector runs, no
        // coverage. Budget = the headland depth left over after the trailing-tool extension
        // (ext), the fence clearance, and the room the U-turn bulb itself needs (~radius),
        // capped at one turn radius and floored at 0 so a tight headland simply keeps
        // today's edge-to-edge turn. Computed from GenerateBoustrophedon's own params so
        // the auto-orientation trial and the built plan derive the identical value.
        double turnLeadIn = turnRadius > 0.01
            ? Math.Max(0.0, Math.Min(turnRadius,
                headlandMargin - Math.Max(0, passEndExtension) - Math.Max(0, boundaryClearance) - turnRadius))
            : 0.0;

        // fastScore: the passes are still split around obstacles (accurate pass/turn
        // counts for comparing candidate orientations), but the expensive obstacle
        // reroute/smoothing, pond loops and transit-gap fixing are skipped.
        var plan = Assemble(ordered, boundary, swathWidth, headlandPasses, startPos, startOppositeSide, turnRadius, boundaryClearance, cornerRadius, fastScore ? null : holes, entryRunIn: turnLeadIn, rawHoles: innerBoundaries);
        if (fastScore || plan == null) return plan;

        // Small obstacles: swerve every leg (swaths included) around the physical-width
        // ring so the passes deviate just enough for the frame to clear. Continuous
        // passes, no split, no loop.
        if (swerveHoles is { Count: > 0 })
            plan = SwervePlan(plan, swerveHoles, turnRadius);

        if (holes is { Count: > 0 } && addPondLoops)
            plan = AddPondLoops(plan, holes, turnRadius, swathWidth);

        // Junction smoothing runs on EVERY plan (kinked joins exist even obstacle-free —
        // e.g. into/out of the perimeter loop, drive-to-start). Links are validated against
        // the PHYSICAL keep-out around each large obstacle (frame half-width + clearance —
        // the tool is lifted in a transit, so the coverage rings would be needlessly wide
        // and block perfectly good links) plus the small-obstacle swerve rings, and kept
        // inside the boundary-clearance limit.
        double tr = turnRadius > 0.1 ? turnRadius : swathWidth * 0.5;
        double hardMargin = physWidth / 2.0 + Math.Max(1.0, boundaryClearance);
        var linkHoles = new List<List<Vec2>>();
        if (holes is { Count: > 0 })
            foreach (var h in innerBoundaries!)
            {
                if (h is not { Count: >= 3 }) continue;
                if (haveSwerve && ObstacleMaxExtent(h) <= swathWidth) continue;   // small = swerved
                var infl = _offset.CreateOutwardOffset(new List<Vec2>(h), hardMargin);
                linkHoles.Add(infl is { Count: >= 3 } ? infl : new List<Vec2>(h));
            }
        if (swerveHoles is { Count: > 0 }) linkHoles.AddRange(swerveHoles);

        List<Vec2>? linkLimit = boundaryClearance > 0.01
            ? _offset.CreateInwardOffset(boundary, boundaryClearance)
            : boundary;
        if (linkLimit is not { Count: >= 3 }) linkLimit = boundary;
        return CloseTransitGaps(plan, linkHoles, tr, swathWidth, linkLimit);
    }

    /// <summary>
    /// Junction smoothing. Walks consecutive legs and replaces any join the tractor can't
    /// physically drive — a heading kink sharper than ~30°, a positional gap left by earlier
    /// surgery, or a straight gap that cuts across an obstacle ring — with a tangent Dubins
    /// link from the exit POSE to the entry POSE (leave along the current heading, arrive
    /// lined up with the next leg), validated against <paramref name="holes"/> and
    /// <paramref name="limit"/>. Falls back to the taut ring-hugging reroute only for an
    /// obstacle-crossing gap with no clear tangent link. This is what turns "arrive at the
    /// next pass from wherever" into "arrive lined up with the pass".
    /// </summary>
    private RoutePlan CloseTransitGaps(RoutePlan plan, List<List<Vec2>> holes, double radius,
        double swathWidth, IReadOnlyList<Vec2>? limit = null)
    {
        const double kinkLimit = 30.0 * Math.PI / 180.0;
        var segs = new List<RouteSegment>(plan.Segments.Count);
        for (int i = 0; i < plan.Segments.Count; i++)
        {
            segs.Add(plan.Segments[i]);
            if (i + 1 >= plan.Segments.Count) continue;
            var cur = plan.Segments[i]; var nxt = plan.Segments[i + 1];
            if (cur.Points.Count < 2 || nxt.Points.Count < 2) continue;
            var pa = cur.Points[^1]; var pb = nxt.Points[0];
            var A = new Vec2(pa.Easting, pa.Northing);
            var B = new Vec2(pb.Easting, pb.Northing);
            double gap = Distance(A, B);

            double ha = EndHeading(cur.Points);
            double hb = StartHeading(nxt.Points);

            // Sharpness of the join: with a gap both the pivot ONTO the chord and OFF it
            // matter; with no gap it's the direct heading step at the shared point.
            double kink;
            if (gap > 0.5)
            {
                double hAB = Math.Atan2(B.Easting - A.Easting, B.Northing - A.Northing);
                kink = Math.Max(Math.Abs(WrapAngle(hAB - ha)), Math.Abs(WrapAngle(hb - hAB)));
            }
            else kink = Math.Abs(WrapAngle(hb - ha));

            // Endpoints that already sit inside a ring (pass ends are deliberately close to
            // the obstacle face) would invalidate every candidate link. Swap any ring that
            // contains an endpoint for a core shrunk just past the deeper endpoint — the
            // link is then only required to keep its MIDDLE off the obstacle itself.
            var holesForLink = holes;
            if (holes.Count > 0)
            {
                List<List<Vec2>>? swapped = null;
                foreach (var hole in holes)
                {
                    if (hole.Count < 3) continue;
                    bool cA = GeometryMath.IsPointInPolygon(hole, A);
                    bool cB = GeometryMath.IsPointInPolygon(hole, B);
                    if (!cA && !cB) { swapped?.Add(hole); continue; }
                    if (swapped == null)
                    {
                        swapped = new List<List<Vec2>>(holes.Count);
                        foreach (var h0 in holes) { if (ReferenceEquals(h0, hole)) break; swapped.Add(h0); }
                    }
                    double depth = 0;
                    if (cA) depth = Math.Max(depth, DepthInside(hole, A));
                    if (cB) depth = Math.Max(depth, DepthInside(hole, B));
                    var core = _offset.CreateInwardOffset(new List<Vec2>(hole), depth + 0.6);
                    if (core is { Count: >= 3 }) swapped.Add(core);
                    // collapsed core: obstacle smaller than the shrink — drop it for this link
                }
                if (swapped != null) holesForLink = swapped;
            }

            bool crosses = gap > 0.5 && holesForLink.Count > 0 && FirstHoleCrossed(A, B, holesForLink) != null;
            if (!crosses && kink < kinkLimit) continue;   // drivable as-is

            // Tangent link between the two poses at the machine's radius.
            var link = BuildTurn(new Vec3(A.Easting, A.Northing, ha),
                                 new Vec3(B.Easting, B.Northing, hb), radius, limit, holesForLink,
                                 allowReverse: AllowReverseTurns);

            bool clear = link.Count >= 2;
            if (clear && holesForLink.Count > 0)
                foreach (var p in link)
                {
                    foreach (var hole in holesForLink)
                        if (hole.Count >= 3 && GeometryMath.IsPointInPolygon(hole, new Vec2(p.Easting, p.Northing)))
                        { clear = false; break; }
                    if (!clear) break;
                }

            // The link must also stay inside the boundary — BuildTurn's fallback ignores
            // the limit. Exempt junctions whose own endpoints sit outside it (the gate
            // case: drive-to-start from a machine parked outside the field).
            if (clear && limit is { Count: >= 3 }
                && GeometryMath.IsPointInPolygon(limit, A) && GeometryMath.IsPointInPolygon(limit, B))
                foreach (var p in link)
                    if (!GeometryMath.IsPointInPolygon(limit, new Vec2(p.Easting, p.Northing)))
                    { clear = false; break; }

            if (!clear)
            {
                // A crossing link is worse than a kink — only fall back to the taut
                // ring-hugging reroute when the ORIGINAL straight gap crossed too.
                if (!crosses) continue;
                var hole2 = FirstHoleCrossed(A, B, holesForLink)!;
                var a2 = NudgeOutside(A, hole2);
                var b2 = NudgeOutside(B, hole2);
                var mid = RouteAroundHoles(
                    new List<Vec3> { new Vec3(a2.Easting, a2.Northing, 0), new Vec3(b2.Easting, b2.Northing, 0) },
                    holesForLink, radius);
                var conn = new List<Vec3>(mid.Count + 2) { pa };
                conn.AddRange(mid);
                conn.Add(pb);
                segs.Add(new RouteSegment(RouteSegmentType.Approach,
                    limit is { Count: >= 3 } ? ClampInside(conn, limit) : conn));
                continue;
            }

            // Clamp gate-exempt links (an endpoint outside the field skips the limit
            // validation above) so they hug the fence instead of sweeping outside it.
            segs.Add(new RouteSegment(RouteSegmentType.Turn,
                limit is { Count: >= 3 } ? ClampInside(link, limit) : link));
        }
        return new RoutePlan(segs, BuildMeta(segs, swathWidth));
    }

    /// <summary>How far inside a ring a point sits: min distance to the ring's edges.</summary>
    private static double DepthInside(IReadOnlyList<Vec2> ring, Vec2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            double d = GeometryMath.PointToSegmentDistance(p, ring[i], ring[(i + 1) % ring.Count]);
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Travel direction over the last ~1.5 m of a polyline.</summary>
    private static double EndHeading(IReadOnlyList<Vec3> pts)
    {
        int j = pts.Count - 1, i = j;
        double acc = 0;
        while (i > 0 && acc < 1.5)
        {
            i--;
            acc += Distance(new Vec2(pts[i].Easting, pts[i].Northing), new Vec2(pts[i + 1].Easting, pts[i + 1].Northing));
        }
        return Math.Atan2(pts[j].Easting - pts[i].Easting, pts[j].Northing - pts[i].Northing);
    }

    /// <summary>Travel direction over the first ~1.5 m of a polyline.</summary>
    private static double StartHeading(IReadOnlyList<Vec3> pts)
    {
        int j = 0;
        double acc = 0;
        while (j < pts.Count - 1 && acc < 1.5)
        {
            j++;
            acc += Distance(new Vec2(pts[j - 1].Easting, pts[j - 1].Northing), new Vec2(pts[j].Easting, pts[j].Northing));
        }
        return Math.Atan2(pts[j].Easting - pts[0].Easting, pts[j].Northing - pts[0].Northing);
    }

    private static double WrapAngle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }

    /// <summary>First hole whose interior the straight A→B gap enters (endpoints skipped).</summary>
    private static List<Vec2>? FirstHoleCrossed(Vec2 a, Vec2 b, List<List<Vec2>> holes)
    {
        foreach (var hole in holes)
        {
            if (hole.Count < 3) continue;
            for (int k = 2; k <= 8; k++)
            {
                double t = k / 10.0;
                var q = new Vec2(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t);
                if (GeometryMath.IsPointInPolygon(hole, q)) return hole;
            }
        }
        return null;
    }

    /// <summary>Push a point radially out of <paramref name="hole"/> (away from its centre)
    /// until just outside; points already outside are returned unchanged.</summary>
    private static Vec2 NudgeOutside(Vec2 p, List<Vec2> hole)
    {
        if (!GeometryMath.IsPointInPolygon(hole, p)) return p;
        var c = Centroid(hole);
        double dx = p.Easting - c.Easting, dy = p.Northing - c.Northing;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
        dx /= len; dy /= len;
        for (double step = 0.5; step < 400; step += 0.5)
        {
            var q = new Vec2(p.Easting + dx * step, p.Northing + dy * step);
            if (!GeometryMath.IsPointInPolygon(hole, q))
                return new Vec2(p.Easting + dx * (step + 0.5), p.Northing + dy * (step + 0.5));
        }
        return p;
    }

    /// <summary>
    /// Add one worked perimeter loop around each obstacle, on the inflated (tool-half-width)
    /// boundary so its band just reaches the obstacle face. This covers the strip immediately
    /// beside faces the passes run PARALLEL to — which passes can't reach without their band
    /// dipping into the obstacle. Each loop is INSERTED right after the segment whose end is
    /// nearest the obstacle, entering at its nearest point, so the drive slips into the loop
    /// and back out without a long transit or a jump across the field.
    /// </summary>
    private RoutePlan AddPondLoops(RoutePlan plan, List<List<Vec2>> holes, double turnRadius, double swathWidth)
    {
        var segs = new List<RouteSegment>(plan.Segments);
        foreach (var hole in holes)
        {
            var ring = turnRadius > 0.1 ? RoundCorners(hole, turnRadius) : new List<Vec2>(hole);
            if (ring.Count < 3) continue;

            // Insert point: the segment whose LAST point is nearest the obstacle centre.
            var c = Centroid(ring);
            int best = -1; double bestD = double.MaxValue;
            for (int s = 0; s < segs.Count; s++)
            {
                if (segs[s].Points.Count == 0) continue;
                var e = segs[s].Points[^1];
                double d = Distance(new Vec2(e.Easting, e.Northing), c);
                if (d < bestD) { bestD = d; best = s; }
            }

            // Enter the loop at its point nearest that segment's end (short slip-in).
            if (best >= 0)
            {
                var e = segs[best].Points[^1];
                RotateToNearest(ring, new Vec2(e.Easting, e.Northing));
            }

            var loop = new List<Vec3>(ring.Count + 1);
            for (int j = 0; j < ring.Count; j++)
            {
                var a = ring[j]; var b = ring[(j + 1) % ring.Count];
                loop.Add(new Vec3(a.Easting, a.Northing, Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
            }
            loop.Add(loop[0]);   // closed: exits where it entered, back beside the pass
            var loopSeg = new RouteSegment(RouteSegmentType.Swath, loop);
            if (best >= 0) segs.Insert(best + 1, loopSeg); else segs.Add(loopSeg);
        }
        return new RoutePlan(segs, BuildMeta(segs, swathWidth));
    }

    /// <summary>
    /// Cross-drill: two complete coverages of the field, the second rotated by
    /// <paramref name="crossAngleRad"/> from the first (45–90°). Used when double
    /// ground coverage is wanted (e.g. drilling seed). The first set drives the
    /// headland laps + interior at <paramref name="headingRad"/>; the second set
    /// re-covers the interior at the crossing angle and is joined onto the end of
    /// the first (its drive-to-start approach bridges the two).
    /// </summary>
    public RoutePlan? GenerateCrossDrill(
        IReadOnlyList<Vec2> outerBoundary,
        double swathWidth,
        double turnRadius,
        double headlandMargin,
        double headingRad,
        double crossAngleRad,
        SwathPattern pattern = SwathPattern.Boustrophedon,
        int headlandPasses = 0,
        Vec3? startPos = null,
        double swathOffset = 0,
        bool swapEnds = false,
        bool startOppositeSide = false,
        double boundaryClearance = 0,
        int skipPasses = 0,
        int blockSkip = 0,
        double cornerRadius = 0,
        IReadOnlyList<IReadOnlyList<Vec2>>? innerBoundaries = null,
        double rowSpacing = 0)
    {
        var first = GenerateBoustrophedon(outerBoundary, swathWidth, turnRadius, headlandMargin,
            headingRad, pattern, headlandPasses, startPos, swathOffset, swapEnds, startOppositeSide,
            boundaryClearance, skipPasses, blockSkip, cornerRadius, innerBoundaries);
        if (first == null) return null;

        // Second set starts where the first finished; it re-covers the interior at
        // the crossing angle and is tagged coverage channel 1, so its sections
        // neither read nor trip the first family's paint.
        Vec3? secondStart = first.Segments.Count > 0 && first.Segments[^1].Points.Count > 0
            ? first.Segments[^1].Points[^1]
            : startPos;
        var second = GenerateBoustrophedon(outerBoundary, swathWidth, turnRadius, headlandMargin,
            headingRad + crossAngleRad, pattern, 0, secondStart, swathOffset, swapEnds, startOppositeSide,
            boundaryClearance, skipPasses, blockSkip, cornerRadius, innerBoundaries, addPondLoops: false);
        if (second == null) return first;

        var segs = new List<RouteSegment>(first.Segments.Count + second.Segments.Count);
        segs.AddRange(first.Segments);
        foreach (var s in second.Segments)
            segs.Add(new RouteSegment(s.Type, s.Points, 1));

        var plan = new RoutePlan(segs, BuildMeta(segs, swathWidth));

        // The perimeter can't be re-covered at a crossing angle, so its double
        // coverage comes from a SECOND set of headland laps offset by half a
        // row-unit spacing — the second set's seed rows fall midway between the
        // first set's. Driven last, tagged channel 1.
        if (rowSpacing > 0.001 && headlandPasses > 0)
            plan = AppendSecondHeadland(plan, outerBoundary, swathWidth, headlandPasses,
                rowSpacing / 2.0, turnRadius, cornerRadius);
        return plan;
    }

    /// <summary>
    /// Cross-drill driven as the operator's WOVEN pattern: family A and family B
    /// legs alternate — work one A leg, V-turn in the side headland onto a B leg,
    /// back across, V onto the next A leg — a W marching down the paddock, then a
    /// parallel return W, until both families are complete. Every family switch is
    /// a gentle turn of the crossing angle (~90°) instead of a 180° keyhole, and
    /// the V has no adjacent-pass 2r≤w constraint at all. Legs pair a couple of
    /// grid slots ahead (the turn needs 2r·sin(X/2) of along-fence advance) —
    /// later Ws collect the skipped rows. Obstacle fields fall back to the
    /// sequential cross-drill (the weave's dual-family reroute isn't built).
    /// </summary>
    public RoutePlan? GenerateCrossDrillWoven(
        IReadOnlyList<Vec2> outerBoundary,
        double swathWidth,
        double turnRadius,
        double headlandMargin,
        double headingRad,
        double crossAngleRad,
        int headlandPasses = 0,
        Vec3? startPos = null,
        double boundaryClearance = 0,
        double cornerRadius = 0,
        IReadOnlyList<IReadOnlyList<Vec2>>? innerBoundaries = null,
        double passEndExtension = 0,
        double rowSpacing = 0,
        double entryRunIn = 0,
        bool trialHeadings = true)
    {
        if (outerBoundary == null || outerBoundary.Count < 3 || swathWidth <= 0)
            return null;

        // Obstacles interrupt both families at once and couple their reroutes —
        // out of the weave's scope; the sequential planner handles them per family.
        if (innerBoundaries is { Count: > 0 })
            return GenerateCrossDrill(outerBoundary, swathWidth, turnRadius, headlandMargin,
                headingRad, crossAngleRad, SwathPattern.Boustrophedon, headlandPasses, startPos,
                0, false, false, boundaryClearance, 0, 0, cornerRadius, innerBoundaries, rowSpacing);

        var boundary = new List<Vec2>(outerBoundary);
        var cultivated = boundary;
        if (headlandMargin > 0)
        {
            var inset = _offset.CreateInwardOffset(boundary, headlandMargin);
            if (inset is { Count: >= 3 }) cultivated = inset;
        }

        // Legs carry the SAME trailing-tool extension as any other pattern; the
        // straighten-up run rides in the connectors (entryRunIn), not the worked
        // leg — so the drill settles with the tool up and nothing double-paints.
        double ext = Math.Max(0, passEndExtension);
        if (headlandMargin > 0) ext = Math.Min(ext, headlandMargin);
        double runIn = Math.Max(0, entryRunIn);
        if (headlandMargin > 0) runIn = Math.Min(runIn, Math.Max(0, headlandMargin - ext));

        // Orientation is decided by MEASURED tour cost, not by formula: the family
        // pair is trialled at several rotations and the cheapest sequenced tour
        // wins. At wall-aligned rotation the family-blind tour degenerates to a
        // sequential-like order (all A, then all B), so the weave can never lose
        // to it by more than search noise; oblique rotations put both families'
        // ends on shared walls where cheap V-turns exist — the optimiser takes
        // whichever the paddock's shape actually rewards. A manual panel angle
        // pins the rotation (trialHeadings = false).
        double[] rotations = trialHeadings
            ? new[] { 0.0, Math.PI / 12, Math.PI / 6, Math.PI / 4 }
            : new[] { 0.0 };

        var candidates = new List<(double Cost, List<List<Vec3>> Tour, double ThetaB)>();
        foreach (double rot in rotations)
        {
            var fa = BuildFamilyPasses(cultivated, headingRad + rot, swathWidth, ext);
            var fb = BuildFamilyPasses(cultivated, headingRad + rot + crossAngleRad, swathWidth, ext);
            if (fa.Count == 0 || fb.Count == 0) continue;
            var tour = WeaveSequence(fa, fb, startPos, turnRadius, swathWidth, ToolOffset);
            if (tour.Count == 0) continue;

            // Model cost of this candidate: worked legs + Dubins connectors + the
            // per-junction straighten-up run (junction COUNT varies with rotation).
            double cost = runIn * Math.Max(0, tour.Count - 1);
            for (int i = 0; i < tour.Count; i++)
            {
                cost += Distance(
                    new Vec2(tour[i][0].Easting, tour[i][0].Northing),
                    new Vec2(tour[i][^1].Easting, tour[i][^1].Northing));
                if (i > 0)
                {
                    var a = tour[i - 1]; var b = tour[i];
                    double hOut = Math.Atan2(a[^1].Easting - a[0].Easting, a[^1].Northing - a[0].Northing);
                    double hIn = Math.Atan2(b[^1].Easting - b[0].Easting, b[^1].Northing - b[0].Northing);
                    cost += DubinsTurn.ShortestLength(
                        new Vec3(a[^1].Easting, a[^1].Northing, hOut),
                        new Vec3(b[0].Easting, b[0].Northing, hIn),
                        Math.Max(0.5, turnRadius));
                }
            }
            candidates.Add((cost, tour, headingRad + rot + crossAngleRad));
        }
        if (candidates.Count == 0) return null;

        // The model under-prices what BuildTurn actually drives (validation,
        // CC blending, boundary clamping), and the bias varies with rotation —
        // so ASSEMBLE the top two model-ranked candidates and keep the one
        // whose BUILT plan is actually shorter.
        candidates.Sort((x, y) => x.Cost.CompareTo(y.Cost));
        RoutePlan? plan = null;
        double thetaB = candidates[0].ThetaB;
        for (int c = 0; c < Math.Min(2, candidates.Count); c++)
        {
            var built = Assemble(candidates[c].Tour, boundary, swathWidth, headlandPasses, startPos,
                false, turnRadius, boundaryClearance, cornerRadius, null, preOriented: true,
                entryRunIn: runIn, rawHoles: innerBoundaries);
            if (built == null) continue;
            if (plan == null || built.Metadata.TotalDistanceMeters < plan.Metadata.TotalDistanceMeters)
            {
                plan = built;
                thetaB = candidates[c].ThetaB;
            }
        }
        if (plan == null) return null;

        plan = TagCrossChannels(plan, thetaB);
        if (rowSpacing > 0.001 && headlandPasses > 0)
            plan = AppendSecondHeadland(plan, boundary, swathWidth, headlandPasses,
                rowSpacing / 2.0, turnRadius, cornerRadius);
        return plan;
    }

    /// <summary>
    /// Mean cos of the CROSS-slope along a pass line — the map-vs-ground
    /// spacing ratio. Sampled from the Terrain grid one cell each side,
    /// perpendicular to the pass, every ~15 m; cells without data contribute
    /// nothing; no data at all = 1 (uniform spacing). Clamped to ≥0.85 (~30°)
    /// so noisy altitude can't over-shrink the comb.
    /// </summary>
    private double MeanCrossSlopeCos(
        double pE, double pN, IReadOnlyList<(Vec2 Entry, Vec2 Exit)> segs)
    {
        var sampler = ElevationSampler;
        if (sampler == null) return 1.0;
        const double BASE = 5.0;   // gradient baseline each side (one terrain cell)
        double sum = 0; int n = 0;
        foreach (var (entry, exit) in segs)
        {
            double len = Distance(entry, exit);
            int steps = Math.Max(1, (int)(len / 15.0));
            for (int i = 0; i < steps; i++)
            {
                double t = (i + 0.5) / steps;
                double e = entry.Easting + (exit.Easting - entry.Easting) * t;
                double nn = entry.Northing + (exit.Northing - entry.Northing) * t;
                if (sampler(e + pE * BASE, nn + pN * BASE) is not { } h1) continue;
                if (sampler(e - pE * BASE, nn - pN * BASE) is not { } h2) continue;
                double m = (h1 - h2) / (2 * BASE);
                sum += 1.0 / Math.Sqrt(1.0 + m * m);
                n++;
            }
        }
        return n == 0 ? 1.0 : Math.Max(0.85, sum / n);
    }

    /// <summary>One family's parallel passes clipped to the cultivated polygon,
    /// spatial order, both ends extended by <paramref name="ext"/> into the band.</summary>
    private List<(Vec3 A, Vec3 B)> BuildFamilyPasses(
        IReadOnlyList<Vec2> cultivated, double theta, double swathWidth, double ext)
    {
        double dE = Math.Sin(theta), dN = Math.Cos(theta);
        double pE = Math.Cos(theta), pN = -Math.Sin(theta);
        var o = Centroid(cultivated);
        double pmin = double.MaxValue, pmax = double.MinValue;
        foreach (var v in cultivated)
        {
            double proj = (v.Easting - o.Easting) * pE + (v.Northing - o.Northing) * pN;
            if (proj < pmin) pmin = proj;
            if (proj > pmax) pmax = proj;
        }
        var result = new List<(Vec3, Vec3)>();
        if (pmax - pmin <= 0) return result;
        double sStep = swathWidth;
        for (double s = pmin + swathWidth / 2.0; s <= pmax; s += sStep)
        {
            sStep = swathWidth;
            var lp = new Vec2(o.Easting + s * pE, o.Northing + s * pN);
            var clipped = ClipSegments(lp, dE, dN, cultivated, null);
            if (clipped.Count > 0) sStep = swathWidth * MeanCrossSlopeCos(pE, pN, clipped);
            foreach (var seg in clipped)
            {
                double h = Math.Atan2(seg.Exit.Easting - seg.Entry.Easting,
                                       seg.Exit.Northing - seg.Entry.Northing);
                result.Add((
                    new Vec3(seg.Entry.Easting - dE * ext, seg.Entry.Northing - dN * ext, h),
                    new Vec3(seg.Exit.Easting + dE * ext, seg.Exit.Northing + dN * ext, h)));
            }
        }
        return result;
    }

    /// <summary>
    /// The weave drive order as a cost-driven open tour over BOTH families' legs:
    /// every leg is a node drivable in either direction, every connector is priced
    /// at its exact Dubins length (the same length BuildTurn drives later), and
    /// the order is greedy-constructed then improved with 2-opt + relocation
    /// local search. The V-weave emerges because an A→B turn at the same wall is
    /// the cheapest connector; where the paddock's shape makes a same-family
    /// skip-U cheaper (corner stubs, angled edges), the optimiser simply takes
    /// it — no straggler fallbacks, no hand-tuned pairing rules. 2-opt segment
    /// reversal is O(1) per move because Dubins length is time-reversal
    /// symmetric (internal connector costs are unchanged by reversal).
    /// </summary>
    private static List<List<Vec3>> WeaveSequence(
        List<(Vec3 A, Vec3 B)> famA, List<(Vec3 A, Vec3 B)> famB,
        Vec3? startPos, double turnRadius, double legSpacing, double toolOffset = 0)
    {
        int nA = famA.Count, n = nA + famB.Count;
        if (n == 0) return new List<List<Vec3>>();
        double r = Math.Max(0.5, turnRadius);

        (Vec3 A, Vec3 B) Pass(int i) => i < nA ? famA[i] : famB[i - nA];

        // Pose helpers. flip=false drives A→B (build heading); flip=true B→A.
        Vec3 Entry(int leg, bool flip)
        {
            var p = Pass(leg);
            return flip
                ? new Vec3(p.B.Easting, p.B.Northing, p.A.Heading + Math.PI)
                : p.A;
        }
        Vec3 Exit(int leg, bool flip)
        {
            var p = Pass(leg);
            return flip
                ? new Vec3(p.A.Easting, p.A.Northing, p.A.Heading + Math.PI)
                : new Vec3(p.B.Easting, p.B.Northing, p.A.Heading);
        }

        var order = new int[n];
        var flip = new bool[n];

        // ---- construction seeds ----
        // Greedy: cheapest-connector-next from the start pose. Finds the weave
        // where V pairings exist, but can tangle on shapes where they don't.
        void BuildGreedy()
        {
            var used = new bool[n];
            Vec3? cursor = startPos;
            for (int step = 0; step < n; step++)
            {
                int best = -1; bool bestFlip = false;
                double bestC = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    for (int f = 0; f < 2; f++)
                    {
                        var e = Entry(i, f == 1);
                        double c = cursor.HasValue
                            ? DubinsTurn.ShortestLength(cursor.Value, e, r)
                            : 0;
                        if (c < bestC) { bestC = c; best = i; bestFlip = f == 1; }
                    }
                }
                used[best] = true;
                order[step] = best;
                flip[step] = bestFlip;
                cursor = Exit(best, bestFlip);
            }
        }

        // Block-skip seed: each family in the app's narrow-tool block order (the
        // sequential cross-drill's own sequencing), A then B. Guarantees the tour
        // search STARTS at sequential quality — local search can only improve it,
        // so the weave never loses to the sequential baseline by more than noise.
        void BuildBlockSeed()
        {
            // Offset tool: block-skip drivability is bound by the TIGHT
            // vehicle-line spacing (legSpacing − 2·|offset| on one pairing).
            int skip = Math.Max(1, (int)Math.Ceiling(
                2.0 * r / Math.Max(0.5, legSpacing - 2.0 * Math.Abs(toolOffset))));
            int k = 0;
            Vec3? cur = startPos;
            foreach (var (offset, count) in new[] { (0, nA), (nA, n - nA) })
            {
                foreach (int idx in SwathOrderingService.GenerateBlockSequence(count, skip))
                {
                    int leg = offset + idx;
                    bool f = false;
                    if (cur.HasValue)
                    {
                        var a = Entry(leg, false);
                        var b = Entry(leg, true);
                        double da = Distance(new Vec2(cur.Value.Easting, cur.Value.Northing), new Vec2(a.Easting, a.Northing));
                        double db = Distance(new Vec2(cur.Value.Easting, cur.Value.Northing), new Vec2(b.Easting, b.Northing));
                        f = db < da;
                    }
                    order[k] = leg; flip[k] = f; k++;
                    cur = Exit(leg, f);
                }
            }
        }

        // ---- local search: 2-opt (reverse a span, flipping each leg) + single-leg
        // relocation, first-improvement sweeps until a full quiet pass ----
        double Conn(int p, int q)   // connector cost between tour positions (p = -1 → start pose)
        {
            var e = Entry(order[q], flip[q]);
            if (p < 0)
                return startPos.HasValue ? DubinsTurn.ShortestLength(startPos.Value, e, r) : 0;
            return DubinsTurn.ShortestLength(Exit(order[p], flip[p]), e, r);
        }
        double ConnRevIn(int p, int j)   // pos p's exit → position-j leg driven REVERSED
        {
            var e = Entry(order[j], !flip[j]);
            if (p < 0)
                return startPos.HasValue ? DubinsTurn.ShortestLength(startPos.Value, e, r) : 0;
            return DubinsTurn.ShortestLength(Exit(order[p], flip[p]), e, r);
        }
        double ConnRevOut(int i, int q) =>   // position-i leg driven REVERSED → pos q's entry
            DubinsTurn.ShortestLength(Exit(order[i], !flip[i]), Entry(order[q], flip[q]), r);

        const double EPS = 0.05;
        void Improve()
        {
        for (int sweep = 0; sweep < 15; sweep++)
        {
            bool improved = false;

            // 2-opt: reverse span [i..j]. Internal connectors keep their cost
            // (time-reversal symmetry); only the two boundary connectors change.
            // Windowed on big fields — long-range fixes are relocation's job.
            for (int i = 0; i < n - 1; i++)
            {
                int jMax = Math.Min(n - 1, i + 80);
                for (int j = i + 1; j <= jMax; j++)
                {
                    double before = Conn(i - 1, i) + (j + 1 < n ? Conn(j, j + 1) : 0);
                    double after = ConnRevIn(i - 1, j) + (j + 1 < n ? ConnRevOut(i, j + 1) : 0);
                    if (after < before - EPS)
                    {
                        Array.Reverse(order, i, j - i + 1);
                        Array.Reverse(flip, i, j - i + 1);
                        for (int k = i; k <= j; k++) flip[k] = !flip[k];
                        improved = true;
                    }
                }
            }

            // Relocation: move one leg (either orientation) to a better slot —
            // the straggler killer the span reversal can't express.
            for (int p = 0; p < n; p++)
            {
                double removeGain = Conn(p - 1, p) + (p + 1 < n ? Conn(p, p + 1) : 0)
                    - (p + 1 < n ? Conn(p - 1, p + 1) : 0);
                if (removeGain < EPS) continue;

                int leg = order[p];
                int bestQ = int.MinValue; bool bestF = flip[p]; double bestDelta = EPS;
                for (int q = -1; q < n; q++)
                {
                    if (q == p || q == p - 1) continue;   // no-op slots
                    int after = q + 1;                     // neither q nor after is p here
                    for (int f = 0; f < 2; f++)
                    {
                        bool nf = f == 1;
                        var entry = Entry(leg, nf);
                        var exit = Exit(leg, nf);
                        double insBefore = q < 0
                            ? (startPos.HasValue ? DubinsTurn.ShortestLength(startPos.Value, entry, r) : 0)
                            : DubinsTurn.ShortestLength(Exit(order[q], flip[q]), entry, r);
                        double insAfter = after < n
                            ? DubinsTurn.ShortestLength(exit, Entry(order[after], flip[after]), r)
                            : 0;
                        double oldGap = after < n ? Conn(q, after) : 0;
                        double delta = removeGain - (insBefore + insAfter - oldGap);
                        if (delta > bestDelta) { bestDelta = delta; bestQ = q; bestF = nf; }
                    }
                }
                if (bestQ != int.MinValue)
                {
                    // Rebuild the tour with the leg re-inserted after original
                    // position bestQ (-1 = front).
                    var no = new List<int>(n); var nfl = new List<bool>(n);
                    if (bestQ == -1) { no.Add(leg); nfl.Add(bestF); }
                    for (int k = 0; k < n; k++)
                    {
                        if (k == p) continue;
                        no.Add(order[k]); nfl.Add(flip[k]);
                        if (k == bestQ) { no.Add(leg); nfl.Add(bestF); }
                    }
                    if (no.Count == n)
                    {
                        no.CopyTo(order); nfl.CopyTo(flip);
                        improved = true;
                    }
                }
            }

            if (!improved) break;
        }
        }

        double TotalConn()
        {
            double s = 0;
            for (int p = 0; p < n; p++) s += Conn(p - 1, p);
            return s;
        }

        // Two starts, best polished tour wins: the greedy weave and the
        // sequential-equivalent block order. Whichever the paddock's shape
        // rewards survives.
        BuildGreedy();
        Improve();
        double greedyCost = TotalConn();
        var greedyOrder = (int[])order.Clone();
        var greedyFlip = (bool[])flip.Clone();

        BuildBlockSeed();
        Improve();
        if (greedyCost < TotalConn())
        {
            greedyOrder.CopyTo(order, 0);
            greedyFlip.CopyTo(flip, 0);
        }

        var result = new List<List<Vec3>>(n);
        for (int s = 0; s < n; s++)
        {
            var p = Pass(order[s]);
            result.Add(flip[s]
                ? new List<Vec3> { p.B, p.A }
                : new List<Vec3> { p.A, p.B });
        }
        return result;
    }

    /// <summary>Tag the woven plan's working legs with their coverage channel by
    /// heading: legs aligned (mod 180°) with the crossing family are channel 1.</summary>
    private static RoutePlan TagCrossChannels(RoutePlan plan, double familyBHeading)
    {
        double hb = Fold180(familyBHeading);
        var segs = new List<RouteSegment>(plan.Segments.Count);
        foreach (var s in plan.Segments)
        {
            if (s.Type == RouteSegmentType.Swath && s.Points.Count >= 2)
            {
                double h = Fold180(Math.Atan2(
                    s.Points[1].Easting - s.Points[0].Easting,
                    s.Points[1].Northing - s.Points[0].Northing));
                double d = Math.Abs(h - hb);
                if (d > Math.PI / 2) d = Math.PI - d;
                segs.Add(d < Math.PI / 4 ? new RouteSegment(s.Type, s.Points, 1) : s);
            }
            else segs.Add(s);
        }
        return new RoutePlan(segs, plan.Metadata) { MissedRegions = plan.MissedRegions };
    }

    private static double Fold180(double h)
    {
        h %= Math.PI;
        return h < 0 ? h + Math.PI : h;
    }

    /// <summary>
    /// The cross-drill second headland set: the same laps offset inward by
    /// <paramref name="bias"/> (half a row-unit spacing, so the second set's seed
    /// rows fall midway between the first set's), appended at the route's end,
    /// tagged coverage channel 1, linked with a Dubins connector.
    /// </summary>
    private RoutePlan AppendSecondHeadland(
        RoutePlan plan, IReadOnlyList<Vec2> boundary, double width, int passes,
        double bias, double turnRadius, double cornerRadius)
    {
        double cr = cornerRadius > 0.01 ? cornerRadius : turnRadius;
        Vec3? from = null;
        for (int i = plan.Segments.Count - 1; i >= 0 && from == null; i--)
            if (plan.Segments[i].Points.Count > 0) from = plan.Segments[i].Points[^1];

        var rings = BuildHeadlandRings(boundary, width, passes, from, cr, insetBias: bias);
        if (rings.Count == 0) return plan;

        var boundaryList = boundary as List<Vec2> ?? new List<Vec2>(boundary);
        var segs = new List<RouteSegment>(plan.Segments);
        foreach (var ring in rings)
        {
            if (from.HasValue)
            {
                var link = BuildTurn(from.Value, ring[0], turnRadius, boundaryList, null);
                if (link.Count < 2) link = new List<Vec3> { from.Value, ring[0] };
                segs.Add(new RouteSegment(RouteSegmentType.Turn, link, 1));
            }
            segs.Add(new RouteSegment(RouteSegmentType.Headland, ring, 1));
            from = ring[^1];
        }
        return new RoutePlan(segs, BuildMeta(segs, width)) { MissedRegions = plan.MissedRegions };
    }

    /// <summary>
    /// Plan a field the way an operator would work it by hand: split lines carve
    /// the boundary into simpler regions (a triangle and a square instead of one
    /// awkward L), and each region is worked with its own pass direction (auto:
    /// the region's longest edge). The headland laps still trace the WHOLE true
    /// boundary once — split lines are working aids, not fences — and each
    /// region's interior is clipped to (region ∩ full-field headland inset) so
    /// nothing double-plants along the split and nothing leaks into the band.
    /// Regions chain nearest-first from the start position via the normal
    /// approach machinery. Returns null when the splits don't actually divide
    /// the field (caller falls back to plain planning).
    /// </summary>
    public RoutePlan? GenerateSplitField(
        IReadOnlyList<Vec2> outerBoundary,
        IReadOnlyList<(Vec2 A, Vec2 B)> splitLines,
        double swathWidth,
        double turnRadius,
        double headlandMargin,
        SwathPattern pattern = SwathPattern.Boustrophedon,
        int headlandPasses = 0,
        Vec3? startPos = null,
        double boundaryClearance = 0,
        int skipPasses = 0,
        int blockSkip = 0,
        double cornerRadius = 0,
        IReadOnlyList<IReadOnlyList<Vec2>>? innerBoundaries = null,
        double physicalToolWidth = 0,
        double passEndExtension = 0,
        double? headingRad = null,
        Vec2? onlyRegionAt = null,
        int onlyRegionIndex = -1,
        IReadOnlyList<Vec2>? insetOverride = null)
    {
        if (outerBoundary == null || outerBoundary.Count < 3 || swathWidth <= 0) return null;
        if (splitLines == null || splitLines.Count == 0) return null;

        var regions = ComputeSplitRegions(outerBoundary, splitLines);
        if (regions.Count <= 1) return null;

        // Single-block mode: keep only the region at the label index (stable
        // ComputeSplitRegions order) or containing the pick point, so the operator
        // can plan (and angle) each block independently. Null when the pick misses
        // every region — the caller decides the fallback.
        if (onlyRegionIndex >= 0)
        {
            if (onlyRegionIndex >= regions.Count) return null;
            regions = new List<List<Vec2>> { regions[onlyRegionIndex] };
        }
        else if (onlyRegionAt is { } pick)
        {
            List<Vec2>? sel = null;
            foreach (var r in regions)
                if (GeometryMath.IsPointInPolygon(r, pick)) { sel = r; break; }
            if (sel == null) return null;
            regions = new List<List<Vec2>> { sel };
        }

        // The shared working area: the true boundary inset by the headland depth
        // (or a caller-supplied polygon — e.g. a hand-built Field Builder headland
        // whose width varies by side). Regions are intersected with THIS, never
        // inset themselves — insetting a region would pull passes back from the
        // split line and leave a gap strip.
        IReadOnlyList<Vec2> inset = outerBoundary;
        if (insetOverride is { Count: >= 3 })
        {
            inset = insetOverride;
        }
        else if (headlandMargin > 0)
        {
            var i = _offset.CreateInwardOffset(new List<Vec2>(outerBoundary), headlandMargin);
            if (i is { Count: >= 3 }) inset = i;
        }

        // Work regions nearest-first from the start position so the machine flows
        // across the field instead of criss-crossing between distant regions.
        var ordered = new List<List<Vec2>>(regions);
        var cursorPt = startPos != null
            ? new Vec2(startPos.Value.Easting, startPos.Value.Northing)
            : Centroid(outerBoundary);
        for (int i = 0; i < ordered.Count - 1; i++)
        {
            int best = i; double bestD = double.MaxValue;
            for (int j = i; j < ordered.Count; j++)
            {
                double d = (Centroid(ordered[j]) - cursorPt).GetLengthSquared();
                if (d < bestD) { bestD = d; best = j; }
            }
            (ordered[i], ordered[best]) = (ordered[best], ordered[i]);
            cursorPt = Centroid(ordered[i]);
        }

        var segs = new List<RouteSegment>();
        Vec3? cursor = startPos;
        bool first = true;
        foreach (var region in ordered)
        {
            var cult = IntersectPolygons(region, inset);
            foreach (var piece in cult)
            {
                if (Math.Abs(SignedArea(piece)) < swathWidth * swathWidth) continue; // sliver

                // First call carries the headland laps for the WHOLE field (its
                // boundary argument is the true fence); later calls are interior-only.
                var plan = GenerateBoustrophedon(outerBoundary, swathWidth, turnRadius,
                    headlandMargin, headingRad, pattern, first ? headlandPasses : 0, cursor,
                    0, false, false, boundaryClearance, skipPasses, blockSkip, cornerRadius,
                    innerBoundaries, addPondLoops: first, fastScore: false,
                    physicalToolWidth, passEndExtension, cultivatedOverride: piece);
                if (plan == null || plan.Segments.Count == 0) continue;

                segs.AddRange(plan.Segments);
                var lastSeg = plan.Segments[^1];
                if (lastSeg.Points.Count > 0) cursor = lastSeg.Points[^1];
                first = false;
            }
        }

        return segs.Count > 0 ? new RoutePlan(segs, BuildMeta(segs, swathWidth)) : null;
    }

    /// <summary>
    /// The split regions in stable "block label" order (north-most centroid
    /// first, then west-most): region 0 is block A, 1 is B, and so on. This
    /// order is what onlyRegionIndex on GenerateSplitField addresses, and it
    /// only changes when the split lines themselves change.
    /// </summary>
    public List<List<Vec2>> ComputeSplitRegions(
        IReadOnlyList<Vec2> outerBoundary, IReadOnlyList<(Vec2 A, Vec2 B)> splitLines)
    {
        var regions = SplitPolygon(outerBoundary, splitLines);
        regions.Sort((a, b) =>
        {
            var ca = Centroid(a); var cb = Centroid(b);
            int byN = cb.Northing.CompareTo(ca.Northing);       // north-most first
            return byN != 0 ? byN : ca.Easting.CompareTo(cb.Easting); // then west-most
        });
        return regions;
    }

    /// <summary>
    /// Cut a polygon by each split line in turn (the line is extended to
    /// infinity, so a partial stroke across the map still cuts cleanly).
    /// Implemented as Clipper intersections with a huge half-plane quad on each
    /// side of the line; a concave boundary can yield several pieces per side
    /// and every piece becomes its own region. Slivers below ~25 m² are dropped.
    /// </summary>
    internal static List<List<Vec2>> SplitPolygon(
        IReadOnlyList<Vec2> polygon, IReadOnlyList<(Vec2 A, Vec2 B)> splitLines)
    {
        const double S = 100.0;             // 1 cm integer grid
        const double MinAreaM2 = 25.0;

        // Extent that safely exceeds the polygon from any line position.
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var p in polygon)
        {
            minE = Math.Min(minE, p.Easting); maxE = Math.Max(maxE, p.Easting);
            minN = Math.Min(minN, p.Northing); maxN = Math.Max(maxN, p.Northing);
        }
        double ext = 2.0 * Math.Max(maxE - minE, maxN - minN) + 100.0;

        var regions = new List<List<Vec2>> { new(polygon) };
        foreach (var (a, b) in splitLines)
        {
            var dir = b - a;
            double len = dir.GetLength();
            if (len < 0.5) continue;                       // degenerate stroke
            dir = new Vec2(dir.Easting / len, dir.Northing / len);
            var nrm = new Vec2(dir.Northing, -dir.Easting); // perpendicular

            var p0 = new Vec2(a.Easting - dir.Easting * ext, a.Northing - dir.Northing * ext);
            var p1 = new Vec2(b.Easting + dir.Easting * ext, b.Northing + dir.Northing * ext);
            Path64 HalfPlane(double side) => new(new[]
            {
                new Point64((long)(p0.Easting * S), (long)(p0.Northing * S)),
                new Point64((long)(p1.Easting * S), (long)(p1.Northing * S)),
                new Point64((long)((p1.Easting + side * nrm.Easting * ext) * S), (long)((p1.Northing + side * nrm.Northing * ext) * S)),
                new Point64((long)((p0.Easting + side * nrm.Easting * ext) * S), (long)((p0.Northing + side * nrm.Northing * ext) * S)),
            });

            var next = new List<List<Vec2>>();
            foreach (var region in regions)
            {
                var subject = new Path64(region.Count);
                foreach (var p in region)
                    subject.Add(new Point64((long)(p.Easting * S), (long)(p.Northing * S)));

                foreach (double side in new[] { 1.0, -1.0 })
                {
                    var clipped = Clipper.Intersect(
                        new Paths64 { subject }, new Paths64 { HalfPlane(side) }, FillRule.NonZero);
                    foreach (var path in clipped)
                    {
                        if (path.Count < 3) continue;
                        if (Math.Abs(Clipper.Area(path)) / (S * S) < MinAreaM2) continue;
                        var poly = new List<Vec2>(path.Count);
                        foreach (var pt in path) poly.Add(new Vec2(pt.X / S, pt.Y / S));
                        next.Add(poly);
                    }
                }
            }
            if (next.Count > 0) regions = next;
        }
        return regions;
    }

    private static List<List<Vec2>> IntersectPolygons(IReadOnlyList<Vec2> a, IReadOnlyList<Vec2> b)
    {
        const double S = 100.0;
        Path64 ToPath(IReadOnlyList<Vec2> poly)
        {
            var p = new Path64(poly.Count);
            foreach (var v in poly) p.Add(new Point64((long)(v.Easting * S), (long)(v.Northing * S)));
            return p;
        }
        var outp = Clipper.Intersect(new Paths64 { ToPath(a) }, new Paths64 { ToPath(b) }, FillRule.NonZero);
        var result = new List<List<Vec2>>(outp.Count);
        foreach (var path in outp)
        {
            if (path.Count < 3) continue;
            var poly = new List<Vec2>(path.Count);
            foreach (var pt in path) poly.Add(new Vec2(pt.X / S, pt.Y / S));
            result.Add(poly);
        }
        return result;
    }

    private static double SignedArea(IReadOnlyList<Vec2> poly)
    {
        double a = 0;
        for (int i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            a += p.Easting * q.Northing - q.Easting * p.Northing;
        }
        return a / 2.0;
    }

    public RoutePlan? GenerateAlongCurve(
        IReadOnlyList<Vec2> outerBoundary,
        IReadOnlyList<Vec3> baseCurve,
        double swathWidth,
        double turnRadius,
        double headlandMargin,
        SwathPattern pattern = SwathPattern.Boustrophedon,
        int headlandPasses = 0,
        Vec3? startPos = null,
        bool swapEnds = false,
        bool startOppositeSide = false,
        double boundaryClearance = 0,
        int skipPasses = 0,
        int blockSkip = 0,
        double cornerRadius = 0)
    {
        if (outerBoundary == null || outerBoundary.Count < 3 || swathWidth <= 0)
            return null;
        if (baseCurve == null || baseCurve.Count < 2)
            return null;

        var boundary = new List<Vec2>(outerBoundary);

        var cultivated = boundary;
        if (headlandMargin > 0)
        {
            var inset = _offset.CreateInwardOffset(boundary, headlandMargin);
            if (inset is { Count: >= 3 }) cultivated = inset;
        }

        // Resample the guide curve to a fine, even spacing first: offsetting and
        // polygon-clipping a sparse curve yields blocky passes and coarse end
        // points. Dense input -> smooth curved swaths and accurate clipping.
        double sampleStep = Math.Max(0.5, swathWidth / 8.0);
        var guide = ResampleCurve(baseCurve, sampleStep);
        double minRunLen = swathWidth;   // drop slivers shorter than one pass width

        // Offset the guide curve to both sides by whole swath widths, clip each
        // to the cultivated polygon (keeping every inside run, so concave fields
        // are filled), and keep them in spatial (k ascending) order. Cap by the
        // polygon's diagonal so we always terminate.
        double diag = PolygonDiagonal(cultivated);
        int maxEach = (int)(diag / swathWidth) + 2;

        var byKey = new SortedDictionary<int, List<List<Vec3>>>();
        for (int dir = -1; dir <= 1; dir += 2)
        {
            int misses = 0;
            for (int k = (dir < 0 ? -1 : 0); dir < 0 ? k >= -maxEach : k <= maxEach; k += dir)
            {
                var off = k == 0
                    ? guide
                    : CurveProcessing.CreateOffsetCurve(guide, k * swathWidth);
                var runs = ClipCurveRuns(off, cultivated, minRunLen);
                if (runs.Count == 0)
                {
                    if (++misses >= 2 && k != 0) break;   // walked off the field
                    continue;
                }
                misses = 0;
                byKey[k] = runs;
            }
        }
        if (byKey.Count == 0) return null;

        var spatial = new List<List<Vec3>>();
        foreach (var runs in byKey.Values) spatial.AddRange(runs);
        var order =
            blockSkip > 0 ? SwathOrderingService.GenerateBlockSequence(spatial.Count, blockSkip)
            : skipPasses > 0 ? SwathOrderingService.GenerateSkipSequence(spatial.Count, skipPasses)
            : SwathOrderingService.GenerateSequence(spatial.Count, pattern);
        if (swapEnds) order.Reverse();
        var ordered = new List<List<Vec3>>(order.Count);
        foreach (int idx in order) ordered.Add(spatial[idx]);

        return Assemble(ordered, boundary, swathWidth, headlandPasses, startPos, startOppositeSide, turnRadius, boundaryClearance, cornerRadius);
    }

    public RoutePlan? GenerateSpiral(
        IReadOnlyList<Vec2> outerBoundary,
        double swathWidth,
        Vec3? startPos = null,
        double boundaryClearance = 0,
        double cornerRadius = 0,
        bool cornerLoops = false,
        IReadOnlyList<IReadOnlyList<Vec2>>? innerBoundaries = null)
    {
        if (outerBoundary == null || outerBoundary.Count < 3 || swathWidth <= 0)
            return null;

        var poly = new List<Vec2>(outerBoundary);

        // Inner obstacles (ponds) inflated by the tool half-width (+clearance): each
        // spiral ring is clipped to the arc OUTSIDE these, so the winding avoids the pond.
        List<List<Vec2>>? holes = null;
        if (innerBoundaries is { Count: > 0 })
        {
            holes = new List<List<Vec2>>();
            double inflate = swathWidth / 2.0 + Math.Max(0, boundaryClearance);
            foreach (var h in innerBoundaries)
            {
                if (h is not { Count: >= 3 }) continue;
                var infl = _offset.CreateOutwardOffset(new List<Vec2>(h), inflate);
                holes.Add(infl is { Count: >= 3 } ? infl : new List<Vec2>(h));
            }
        }

        // Seam pinned to a real field CORNER — the boundary vertex nearest the entry
        // point — so every lap's inward turn-in lands at the same corner (not mid-edge),
        // matching how an operator spirals in from a corner.
        var entry = startPos.HasValue
            ? new Vec2(startPos.Value.Easting, startPos.Value.Northing)
            : new Vec2(poly[0].Easting, poly[0].Northing);
        var seed = poly[0]; double seedD = double.MaxValue;
        foreach (var v in poly) { double d = Distance(v, entry); if (d < seedD) { seedD = d; seed = v; } }

        // Build ONE continuous inward spiral as an open polyline: each lap (offset in by
        // a further swath width, rotated to start at the seam) winds around and flows
        // straight into the next inset lap. No closed loops → no radial seam "spoke".
        var path = new List<Vec2>();
        var seamIdx = new HashSet<int>();   // path indices at lap-to-lap seam junctions
        Vec2 center = seed; int laps = 0;
        for (int i = 0; i < 1000; i++)
        {
            var ring = _offset.CreateInwardOffset(poly, (i + 0.5) * swathWidth);
            if (ring is not { Count: >= 3 }) break;
            var rp = new List<Vec2>(ring);
            RotateToNearest(rp, seed);
            if (holes != null)
            {
                // Keep only the arc of this ring outside the pond(s); skip a ring the
                // pond fully shadows. The winding then flows around the obstacle.
                var clipped = ClipRingAgainstHoles(rp, holes);
                if (clipped.Count < 2) continue;
                rp = clipped;
            }
            if (path.Count > 0) { seamIdx.Add(path.Count - 1); seamIdx.Add(path.Count); }
            path.AddRange(rp);        // open lap; the join to the next lap is the step-in
            center = Centroid(rp);
            laps++;
        }
        if (laps == 0) return null;

        // Centre finish: run into the field centre so the spiral doesn't leave a middle
        // pocket — but not if the centre sits inside a pond.
        bool centerInHole = false;
        if (holes != null)
            foreach (var h in holes)
                if (GeometryMath.IsPointInPolygon(h, center)) { centerInHole = true; break; }
        if (!centerInHole) path.Add(center);

        // Corner-fill option: at each ~90° lap corner, replace the tight rounded corner
        // with a forward 270° loop that swings out into the corner apex, covering the
        // wedge a wide tool would otherwise miss (operator's "drive out + 270° loop").
        double loopR = cornerRadius > 0.01 ? cornerRadius : swathWidth * 0.5;
        if (cornerLoops) path = InsertCornerLoops(path, loopR, swathWidth * 2.0, seamIdx);

        // Smooth every corner to the turning circle in one open-polyline pass — the lap
        // corners AND the inward step-ins — so the turn-ins are rounded, not sharp.
        var smooth = RoundCorners(path, cornerRadius, closed: false);

        var spiral = new List<Vec3>(smooth.Count);
        for (int k = 0; k < smooth.Count; k++)
        {
            var a = smooth[k];
            var b = smooth[Math.Min(k + 1, smooth.Count - 1)];
            spiral.Add(new Vec3(a.Easting, a.Northing,
                Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
        }

        // Keep the whole spiral the configured distance inside the fence.
        if (boundaryClearance > 0.01)
        {
            var limit = _offset.CreateInwardOffset(poly, boundaryClearance);
            if (limit is { Count: >= 3 }) spiral = ClampInside(spiral, limit);
        }

        // Route the winding + approach around the ponds too (the per-ring clip keeps the
        // laps out, but the arc-to-arc joins and the drive-to-start can still cross one).
        if (holes is { Count: > 0 }) spiral = RouteAroundHoles(spiral, holes, cornerRadius > 0.1 ? cornerRadius : swathWidth * 0.5);

        var segments = new List<RouteSegment>();
        double approachLen = 0;
        if (startPos.HasValue && spiral.Count > 0)
        {
            var approach = new List<Vec3> { startPos.Value, spiral[0] };
            if (holes is { Count: > 0 }) approach = RouteAroundHoles(approach, holes, cornerRadius > 0.1 ? cornerRadius : swathWidth * 0.5);
            approachLen = PolylineLength(approach);
            segments.Add(new RouteSegment(RouteSegmentType.Approach, approach));
        }
        segments.Add(new RouteSegment(RouteSegmentType.Swath, spiral));

        double workLen = PolylineLength(spiral);
        double total = workLen + approachLen;
        double est = EstimateSpeedMps > 0 ? total / EstimateSpeedMps : 0;
        // One continuous Swath segment, but report the real lap count as the pass count.
        var meta = new RoutePlanMetadata(laps, total, est, workLen, approachLen, 0, swathWidth);
        var plan = new RoutePlan(segments, meta);

        // Close the clip-seam channel: the per-ring clip keeps the laps out of the pond but
        // leaves an uncovered strip where every arc opens. A perimeter loop covers it (same
        // as boustrophedon); then close any transit that would jump across the obstacle.
        if (holes is { Count: > 0 })
        {
            double tr = cornerRadius > 0.1 ? cornerRadius : swathWidth * 0.5;
            plan = AddPondLoops(plan, holes, tr, swathWidth);
            var transitHoles = new List<List<Vec2>>();
            foreach (var h in innerBoundaries!)
            {
                if (h is not { Count: >= 3 }) continue;
                var infl = _offset.CreateOutwardOffset(new List<Vec2>(h), tr * 1.3 + Math.Max(0, boundaryClearance));
                transitHoles.Add(infl is { Count: >= 3 } ? infl : new List<Vec2>(h));
            }
            if (transitHoles.Count > 0) plan = CloseTransitGaps(plan, transitHoles, tr, swathWidth);
        }
        return plan;
    }

    // ---- assembly ----

    /// <summary>
    /// Build the final route from working passes already in driving order:
    /// optional drive-to-start approach, outer-to-inner headland laps, then the
    /// interior passes alternated and linked with semicircle U-turns.
    /// </summary>
    /// <summary>Shift a drive polyline perpendicular to its own travel
    /// direction so the laterally offset tool lands on the planned comb: the
    /// vehicle drives left-of-travel by ToolOffset (positive offset = tool
    /// right of the tractor). Directions come from the point sequence (the
    /// FINAL drive order — call only after reversals are done), not stored
    /// headings, so it works for combs, laps and spirals alike. No-op at
    /// zero offset.</summary>
    private List<Vec3> ShiftLeftOfTravel(List<Vec3> poly)
    {
        double o = ToolOffset;
        if (Math.Abs(o) < 0.001 || poly.Count < 2) return poly;
        var shifted = new List<Vec3>(poly.Count);
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[Math.Max(0, i - 1)];
            var b = poly[Math.Min(poly.Count - 1, i + 1)];
            double dE = b.Easting - a.Easting, dN = b.Northing - a.Northing;
            double len = Math.Sqrt(dE * dE + dN * dN);
            if (len < 1e-9) { shifted.Add(poly[i]); continue; }
            // right of travel = (dN, −dE)/len; vehicle goes LEFT by o.
            shifted.Add(new Vec3(
                poly[i].Easting - (dN / len) * o,
                poly[i].Northing + (dE / len) * o,
                poly[i].Heading));
        }
        return shifted;
    }

    private RoutePlan? Assemble(
        List<List<Vec3>> ordered,
        IReadOnlyList<Vec2> boundary,
        double swathWidth,
        int headlandPasses,
        Vec3? startPos,
        bool flipStartSide = false,
        double turnRadius = 0,
        double boundaryClearance = 0,
        double cornerRadius = 0,
        List<List<Vec2>>? holes = null,
        bool preOriented = false,
        double entryRunIn = 0,
        IReadOnlyList<IReadOnlyList<Vec2>>? rawHoles = null)
    {
        if (ordered.Count == 0) return null;

        // Swept-body constraint for every connector built below: the raw fence
        // (only if hard) and the raw hard holes — the inflated `holes` are the
        // tractor-path proxy, the swept check wants the real polygons.
        SweptConstraint? swept = SweptToolGeometry is { } sg
            ? new SweptConstraint(sg, OuterBoundaryIsHard ? boundary : null, rawHoles, Math.Max(0, boundaryClearance))
            : null;

        // Headland-lap corners are filleted to the tractor's real turning circle
        // (cornerRadius, from the vehicle setup); fall back to the U-turn radius.
        double cr = cornerRadius > 0.01 ? cornerRadius : turnRadius;

        // Headland laps in drive order, shaped by the operator's style choice:
        // classic separate laps, one continuous spiral (in or out), or none at
        // all — the margin is still reserved, the laps are just worked separately.
        var lapPaths = new List<List<Vec3>>();
        if (headlandPasses > 0 && HeadlandStyle != RouteHeadlandStyle.None)
        {
            int skipOuter = Math.Clamp(HeadlandSkipOuterLaps, 0, headlandPasses);
            if (HeadlandStyle == RouteHeadlandStyle.Laps)
                lapPaths = BuildHeadlandRings(boundary, swathWidth, headlandPasses, startPos, cr,
                    skipOuter: skipOuter);
            else
            {
                var spiralHl = BuildHeadlandSpiral(boundary, swathWidth, headlandPasses, startPos, cr,
                    outward: HeadlandStyle == RouteHeadlandStyle.SpiralOut, skipOuter: skipOuter);
                if (spiralHl != null) lapPaths.Add(spiralHl);
                else lapPaths = BuildHeadlandRings(boundary, swathWidth, headlandPasses, startPos, cr,
                    skipOuter: skipOuter);
            }
        }
        var headland = new List<RouteSegment>();
        foreach (var lap in lapPaths)
            // Lap traversal direction is final (RotateToNearest / back-cut
            // reversal already applied) — shift the DRIVE line so the offset
            // tool's band stays on the planned lap inset.
            headland.Add(new RouteSegment(RouteSegmentType.Headland, ShiftLeftOfTravel(lap)));

        var interior = new List<RouteSegment>();
        double totalDist = 0;
        Vec3? prevExit = null;
        int swathCount = 0;

        foreach (var lap in lapPaths) totalDist += PolylineLength(lap);

        // Containment ring for turn validation: the outer boundary, pulled in by
        // the clearance if one is configured. Turns must stay inside this so a
        // Dubins turn that fits is preferred over one that pokes past the fence.
        var boundaryList = boundary as List<Vec2> ?? new List<Vec2>(boundary);
        List<Vec2>? turnLimit = boundaryClearance > 0.01
            ? _offset.CreateInwardOffset(boundaryList, boundaryClearance)
            : boundaryList;
        if (turnLimit is not { Count: >= 3 }) turnLimit = boundaryList;

        for (int k = 0; k < ordered.Count; k++)
        {
            var poly = new List<Vec3>(ordered[k]);
            // Serpentine drive direction; flipStartSide begins the first pass at
            // the opposite end (route start on the other side). A pre-oriented
            // sequence (the cross-drill weave) already encodes drive direction
            // in the point order, so no alternation is applied.
            if (!preOriented && ((k + (flipStartSide ? 1 : 0)) % 2) == 1) poly.Reverse();
            if (poly.Count < 2) continue;

            // Reversing a pass leaves each point's stored heading pointing the
            // OLD way, so the exit heading would face back up the pass and the
            // U-turn would bulge the wrong side. Recompute headings along the
            // actual driven direction so the turn always exits outward.
            for (int i = 0; i < poly.Count - 1; i++)
            {
                var a = poly[i];
                var b = poly[i + 1];
                poly[i] = new Vec3(a.Easting, a.Northing,
                    Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing));
            }
            poly[^1] = new Vec3(poly[^1].Easting, poly[^1].Northing, poly[^2].Heading);

            // Drive direction is now FINAL — shift the vehicle line off the
            // tool comb for a laterally offset implement. Connectors below are
            // built from the SHIFTED poses, so the alternating w∓2·offset turn
            // throws fall out with no turn-side special casing.
            poly = ShiftLeftOfTravel(poly);

            if (prevExit.HasValue)
            {
                // Symmetric straighten-up runs so the U-turn sits DEEPER in the reserved
                // headland (off the application edge) and the tractor+trailed tool are
                // settled straight both LEAVING and ENTERING the worked leg — tool up, so
                // no coverage (Turn segments are excluded from the worked/coverage model):
                //  • run-OUT: continue straight along the EXIT heading past the pass end,
                //    pushing the turn's START into the headland;
                //  • run-IN:  arrive straight along the next pass's ENTRY heading, pushing
                //    the turn's END into the headland (the original pulled-target trick).
                var origSrc = prevExit.Value;   // worked-pass exit pose (heading = drive-out)
                var origTgt = poly[0];          // next worked-pass entry pose
                var src = origSrc;
                var target = origTgt;
                List<Vec3>? runOut = null, runIn = null;
                if (entryRunIn > 0.01)
                {
                    var pushed = new Vec3(
                        origSrc.Easting + Math.Sin(origSrc.Heading) * entryRunIn,
                        origSrc.Northing + Math.Cos(origSrc.Heading) * entryRunIn,
                        origSrc.Heading);
                    runOut = new List<Vec3> { origSrc, pushed };
                    src = pushed;

                    var pulled = new Vec3(
                        origTgt.Easting - Math.Sin(origTgt.Heading) * entryRunIn,
                        origTgt.Northing - Math.Cos(origTgt.Heading) * entryRunIn,
                        origTgt.Heading);
                    runIn = new List<Vec3> { pulled, origTgt };
                    target = pulled;
                }
                var turn = BuildTurn(src, target, turnRadius, turnLimit, holes, swept: swept);
                if (turn.Count < 2)
                {
                    // No valid arc reaches the pushed/pulled poses → fall back to a plain
                    // connector between the ORIGINAL pass endpoints (today's behaviour);
                    // never emit a bad path just to keep the lead-in.
                    turn = (runOut != null || runIn != null)
                        ? new List<Vec3> { origSrc, origTgt }
                        : turn;
                }
                else
                {
                    if (runOut != null) turn.Insert(0, origSrc);   // prevExit → pushed (straight)
                    if (runIn != null) turn.Add(origTgt);          // pulled → entry pose (straight)
                }
                if (turn.Count >= 2)
                {
                    // Hard-fence swept guard: the fence-ward lead-in/out shortens the
                    // standoff, so a long implement BODY can swing past a HARD fence even
                    // though BuildTurn cleared the bare arc (and the appended straight runs
                    // are not themselves swept-checked). If the full connector's swept body
                    // intrudes, drop the lead-in and fall back to the plain swept-validated
                    // turn between the true pass endpoints (BuildTurn picks the
                    // least-intruding arc). Soft fences don't enforce the body, so the
                    // lead-in stands there.
                    if ((runOut != null || runIn != null) && swept != null && !SweptClear(turn, swept))
                    {
                        var plain = BuildTurn(origSrc, origTgt, turnRadius, turnLimit, holes, swept: swept);
                        if (plain.Count >= 2) turn = plain;
                    }
                    interior.Add(new RouteSegment(RouteSegmentType.Turn, turn));
                    totalDist += PolylineLength(turn);
                }
            }

            interior.Add(new RouteSegment(RouteSegmentType.Swath, poly));
            totalDist += PolylineLength(poly);
            prevExit = poly[^1];
            swathCount++;
        }

        var segments = new List<RouteSegment>();
        bool headlandFirst = HeadlandFirstPhase || interior.Count == 0;
        Vec3? firstInterior = interior.Count > 0 ? FirstSwathPoint(interior) : null;

        // Entry/exit poses of the headland block, for the connectors in and out.
        Vec3? hlStart = lapPaths.Count > 0 ? lapPaths[0][0] : (Vec3?)null;
        Vec3? hlExit = null;
        if (lapPaths.Count > 0)
        {
            var lastLap = lapPaths[^1];
            var e = lastLap[^1];
            double hOut = lastLap.Count >= 2
                ? Math.Atan2(e.Easting - lastLap[^2].Easting, e.Northing - lastLap[^2].Northing)
                : e.Heading;
            hlExit = new Vec3(e.Easting, e.Northing, hOut);
        }

        // Route start = first thing actually driven; the approach connects the
        // machine to it. Headland-last (mow-style) starts on the interior fill.
        Vec3? routeStart = headlandFirst ? (hlStart ?? firstInterior) : (firstInterior ?? hlStart);
        if (startPos.HasValue && routeStart.HasValue)
            segments.Add(new RouteSegment(RouteSegmentType.Approach,
                new List<Vec3> { startPos.Value, routeStart.Value }));

        // Block-to-block connector: a tangent Dubins link between the two poses
        // (leave along the exit heading, arrive lined up with the target), not a
        // straight jump the tractor can't pivot onto.
        void Link(Vec3 from, Vec3 to)
        {
            var connector = BuildTurn(from, to, turnRadius, turnLimit, holes, swept: swept);
            if (connector.Count < 2) connector = new List<Vec3> { from, to };
            segments.Add(new RouteSegment(RouteSegmentType.Turn, connector));
        }

        Vec3? routeEnd;
        if (headlandFirst)
        {
            segments.AddRange(headland);
            if (hlExit.HasValue && firstInterior.HasValue) Link(hlExit.Value, firstInterior.Value);
            segments.AddRange(interior);
            routeEnd = prevExit ?? hlExit;
        }
        else
        {
            segments.AddRange(interior);
            if (prevExit.HasValue && hlStart.HasValue) Link(prevExit.Value, hlStart.Value);
            segments.AddRange(headland);
            routeEnd = hlExit ?? prevExit;
        }

        // Mower back-cut: one final fence-tight lap driven the opposite way
        // round, so the tool's other side dresses the fence line. Always the
        // very last thing driven.
        if (HeadlandBackCut && headlandPasses > 0)
        {
            var back = BuildBackCutLap(boundary, swathWidth, routeEnd ?? startPos, cr);
            if (back != null)
            {
                if (routeEnd.HasValue) Link(routeEnd.Value, back[0]);
                segments.Add(new RouteSegment(RouteSegmentType.Headland, back));
            }
        }


        // Route the non-working pieces (turns, connectors, approach, headland laps) around
        // the obstacles too — the passes are already clipped, but these can cut across a pond.
        double rerouteRadius = turnRadius > 0.1 ? turnRadius : swathWidth * 0.5;
        if (holes is { Count: > 0 })
        {
            // Detour rings = obstacles inflated a further turn-radius (round joins), so any
            // arc that hugs one is never tighter than the turn radius. The connector is then
            // pushed out to THESE rings and smoothed, which keeps it drivable and clear.
            var detourRings = new List<List<Vec2>>(holes.Count);
            foreach (var h in holes)
            {
                var infl = _offset.CreateOutwardOffset(new List<Vec2>(h), rerouteRadius);
                detourRings.Add(infl is { Count: >= 3 } ? infl : h);
            }
            // Turns that BuildTurn validated are already hole-aware and tangent at both
            // ends — rerouting them would shift their endpoints off the pass ends and
            // destroy the tangency (the "sharp angle straight out of the pass" artifact).
            // Only turns that actually ENTER a hole (BuildTurn's last-resort fallback when
            // no clear path exists, e.g. a pass ending right at the pond face) still get
            // pushed around the rings.
            bool EntersHole(IReadOnlyList<Vec3> pts)
            {
                foreach (var p in pts)
                    foreach (var hole in holes)
                        if (hole.Count >= 3 && GeometryMath.IsPointInPolygon(hole, new Vec2(p.Easting, p.Northing)))
                            return true;
                return false;
            }
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Points.Count < 2) continue;
                var st = segments[i].Type;
                if (st == RouteSegmentType.Swath) continue;
                if (st == RouteSegmentType.Turn && !EntersHole(segments[i].Points)) continue;
                // localOnly: deform only around the crossing — the global taut-string
                // smoothing would CONTRACT a whole headland lap / long connector into
                // chords that can cut across boundary notches (outside the field).
                segments[i] = new RouteSegment(st, RouteAroundHoles(segments[i].Points, detourRings, rerouteRadius, localOnly: true));
            }
        }

        // Boundary containment LAST: keep every point inside the field (inset by the
        // clearance when configured — turnLimit above). BuildTurn's fallback ignores the
        // limit and the obstacle reroute can nudge legs, so a leg could otherwise cross
        // the fence; clamping pulls those points onto the line instead. The drive-to-
        // start approach is exempt — the machine may legitimately start outside.
        for (int i = 0; i < segments.Count; i++)
        {
            if (segments[i].Type == RouteSegmentType.Swath) continue;
            if (i == 0 && segments[i].Type == RouteSegmentType.Approach) continue;
            segments[i] = new RouteSegment(segments[i].Type, ClampInside(segments[i].Points, turnLimit));
        }

        var meta = BuildMeta(segments, swathWidth);
        return new RoutePlan(segments, meta);
    }

    /// <summary>
    /// Pull any point that lies outside <paramref name="limit"/> onto the nearest
    /// point of the limit polygon; points inside are returned unchanged. Keeps
    /// turns from crossing the boundary-clearance line.
    /// </summary>
    private static List<Vec3> ClampInside(IReadOnlyList<Vec3> pts, IReadOnlyList<Vec2> limit)
    {
        var outp = new List<Vec3>(pts.Count);
        foreach (var p in pts)
        {
            if (PointInPolygon(new Vec2(p.Easting, p.Northing), limit))
                outp.Add(p);
            else
            {
                var c = NearestOnPolygon(new Vec2(p.Easting, p.Northing), limit);
                outp.Add(new Vec3(c.Easting, c.Northing, p.Heading));
            }
        }
        return outp;
    }

    /// <summary>Nearest point on the edges of <paramref name="poly"/> to <paramref name="q"/>.</summary>
    private static Vec2 NearestOnPolygon(Vec2 q, IReadOnlyList<Vec2> poly)
    {
        Vec2 best = poly[0];
        double bestD = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double ex = b.Easting - a.Easting, ey = b.Northing - a.Northing;
            double len2 = ex * ex + ey * ey;
            double t = len2 > 1e-9 ? ((q.Easting - a.Easting) * ex + (q.Northing - a.Northing) * ey) / len2 : 0;
            t = Math.Clamp(t, 0, 1);
            var c = new Vec2(a.Easting + t * ex, a.Northing + t * ey);
            double d = Distance(c, q);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private static Vec3? FirstSwathPoint(List<RouteSegment> segs)
    {
        foreach (var s in segs)
            if (s.Type == RouteSegmentType.Swath && s.Points.Count > 0)
                return s.Points[0];
        return null;
    }

    /// <summary>
    /// Concentric headland laps: <paramref name="passes"/> rings inset by
    /// (i + 0.5) × width from the boundary, outermost first. The outer ring is
    /// rotated to begin at the vertex nearest <paramref name="startPos"/> so the
    /// drive-to-start lands at a natural entry point.
    /// </summary>
    /// <inheritdoc />
    public List<Vec3>? BuildLapRing(IReadOnlyList<Vec2> boundary, double insetMeters, double cornerRadius, Vec3? startPos = null)
    {
        // Same pipeline as a headland lap (BuildHeadlandRings, i = 0) with an explicit
        // inset instead of (i + 0.5)·width: inset → anchor seam at the machine → round
        // corners to the turn radius → close → headings.
        var poly = boundary as List<Vec2> ?? new List<Vec2>(boundary);
        if (poly.Count < 3) return null;
        // Positive inset = inward (the outer fence); negative = OUTWARD (an obstacle ring
        // offset away from its hole). |inset| < 5 cm uses the ring as-is.
        var ring = insetMeters > 0.05 ? _offset.CreateInwardOffset(poly, insetMeters)
                 : insetMeters < -0.05 ? _offset.CreateOutwardOffset(poly, -insetMeters)
                 : new List<Vec2>(poly);
        if (ring is not { Count: >= 3 }) return null;

        var pts = new List<Vec2>(ring);
        if (startPos.HasValue)
            RotateToNearest(pts, new Vec2(startPos.Value.Easting, startPos.Value.Northing));
        pts = RoundCorners(pts, cornerRadius);
        if (pts.Count < 3) return null;

        var loop = new List<Vec3>(pts.Count + 1);
        for (int j = 0; j < pts.Count; j++)
        {
            var a = pts[j];
            var b = pts[(j + 1) % pts.Count];
            loop.Add(new Vec3(a.Easting, a.Northing, Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
        }
        loop.Add(loop[0]);   // close the lap
        return loop;
    }

    private List<List<Vec3>> BuildHeadlandRings(
        IReadOnlyList<Vec2> boundary, double width, int passes, Vec3? startPos, double turnRadius = 0,
        double insetBias = 0, int skipOuter = 0)
    {
        var rings = new List<List<Vec3>>();
        var poly = boundary as List<Vec2> ?? new List<Vec2>(boundary);
        for (int i = Math.Clamp(skipOuter, 0, passes); i < passes; i++)
        {
            var ring = _offset.CreateInwardOffset(poly, (i + 0.5) * width + insetBias);
            if (ring is not { Count: >= 3 }) continue;

            var pts = new List<Vec2>(ring);
            if (i == 0 && startPos.HasValue)
                RotateToNearest(pts, new Vec2(startPos.Value.Easting, startPos.Value.Northing));

            // Round the lap corners to the turn radius so the tractor can actually
            // drive them (a sharp offset corner isn't drivable).
            pts = RoundCorners(pts, turnRadius);

            var loop = new List<Vec3>(pts.Count + 1);
            for (int j = 0; j < pts.Count; j++)
            {
                var a = pts[j];
                var b = pts[(j + 1) % pts.Count];
                double h = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
                loop.Add(new Vec3(a.Easting, a.Northing, h));
            }
            loop.Add(loop[0]);   // close the lap
            rings.Add(loop);
        }
        return rings;
    }

    /// <summary>
    /// The headland driven as ONE continuous spiral instead of separate closed
    /// laps: each lap winds the full ring back to its own start (so its band is
    /// completely covered), then lane-changes one working width inward over a
    /// few turn radii onto the next lap — that transition driven over the
    /// already-worked start of the lap it just closed, so nothing is missed.
    /// Anchored at the boundary vertex nearest the machine (the field entry),
    /// the way an operator spirals in from the gate. <paramref name="outward"/>
    /// reverses the traversal (innermost lap first, finishing on the fence lap
    /// at the entry) for headland-last routes. Null when no ring fits.
    /// </summary>
    private List<Vec3>? BuildHeadlandSpiral(
        IReadOnlyList<Vec2> boundary, double width, int passes, Vec3? startPos,
        double turnRadius, bool outward = false, int skipOuter = 0)
    {
        if (passes <= 0 || width <= 0.01) return null;
        var poly = boundary as List<Vec2> ?? new List<Vec2>(boundary);
        if (poly.Count < 3) return null;

        // Seam pinned to the boundary vertex nearest the entry point (same rule
        // as the whole-field spiral) so the lane changes cluster at the gate.
        var entry = startPos.HasValue
            ? new Vec2(startPos.Value.Easting, startPos.Value.Northing)
            : poly[0];
        var seam = poly[0];
        double seamD = double.MaxValue;
        foreach (var v in poly)
        {
            double d = Distance(v, entry);
            if (d < seamD) { seamD = d; seam = v; }
        }

        // Lane change: one width sideways over a few turn radii forward. Each
        // lap starts where the previous transition lands, so the seam walks
        // forward along the boundary lap by lap.
        double lane = Math.Max(2.5 * Math.Max(turnRadius, 0.5), width);

        var path = new List<Vec2>();
        double advance = 0;
        int laps = 0;
        for (int k = Math.Clamp(skipOuter, 0, passes); k < passes; k++)
        {
            var ring = _offset.CreateInwardOffset(poly, (k + 0.5) * width);
            if (ring is not { Count: >= 3 }) break;
            var pts = new List<Vec2>(ring);
            RotateToNearest(pts, seam);
            path.AddRange(WalkRingFrom(pts, advance));
            advance += lane;
            laps++;
        }
        if (laps == 0) return null;

        // One open-polyline smoothing pass rounds the lap corners AND the
        // lane-change kinks to the turning circle.
        var smooth = RoundCorners(path, turnRadius, closed: false);
        if (outward) smooth.Reverse();

        var spiral = new List<Vec3>(smooth.Count);
        for (int i = 0; i < smooth.Count; i++)
        {
            var a = smooth[i];
            var b = smooth[Math.Min(i + 1, smooth.Count - 1)];
            spiral.Add(new Vec3(a.Easting, a.Northing,
                Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
        }
        if (spiral.Count >= 2)
            spiral[^1] = new Vec3(spiral[^1].Easting, spiral[^1].Northing, spiral[^2].Heading);
        return spiral;
    }

    /// <summary>One full circuit of a closed ring, re-based to start
    /// <paramref name="startDist"/> metres along it (interpolated on the edge)
    /// and ending back at that same point.</summary>
    private static List<Vec2> WalkRingFrom(IReadOnlyList<Vec2> ring, double startDist)
    {
        int n = ring.Count;
        double per = 0;
        for (int i = 0; i < n; i++) per += Distance(ring[i], ring[(i + 1) % n]);
        if (per < 1e-6) return new List<Vec2>(ring);
        startDist %= per;

        double acc = 0; int i0 = 0; double t0 = 0;
        for (int i = 0; i < n; i++)
        {
            double len = Distance(ring[i], ring[(i + 1) % n]);
            if (acc + len >= startDist) { i0 = i; t0 = len > 1e-9 ? (startDist - acc) / len : 0; break; }
            acc += len;
        }
        t0 = Math.Clamp(t0, 0, 1);
        var a0 = ring[i0];
        var b0 = ring[(i0 + 1) % n];
        var start = new Vec2(a0.Easting + (b0.Easting - a0.Easting) * t0,
                             a0.Northing + (b0.Northing - a0.Northing) * t0);
        var walk = new List<Vec2>(n + 2) { start };
        for (int i = 1; i <= n; i++) walk.Add(ring[(i0 + i) % n]);
        walk.Add(start);
        for (int i = walk.Count - 1; i > 0; i--)
            if (Distance(walk[i], walk[i - 1]) < 1e-6) walk.RemoveAt(i);
        return walk;
    }

    /// <summary>
    /// The mower back-cut: one last fence-tight lap (the outer ring's geometry)
    /// driven the OPPOSITE way round, so the tool's other side dresses the
    /// fence line. Started at the vertex nearest <paramref name="from"/>.
    /// </summary>
    private List<Vec3>? BuildBackCutLap(
        IReadOnlyList<Vec2> boundary, double width, Vec3? from, double turnRadius)
    {
        var poly = boundary as List<Vec2> ?? new List<Vec2>(boundary);
        var ring = _offset.CreateInwardOffset(poly, 0.5 * width);
        if (ring is not { Count: >= 3 }) return null;
        var pts = new List<Vec2>(ring);
        pts.Reverse();   // opposite-hand traversal
        if (from.HasValue) RotateToNearest(pts, new Vec2(from.Value.Easting, from.Value.Northing));
        pts = RoundCorners(pts, turnRadius);
        var loop = new List<Vec3>(pts.Count + 1);
        for (int j = 0; j < pts.Count; j++)
        {
            var a = pts[j];
            var b = pts[(j + 1) % pts.Count];
            loop.Add(new Vec3(a.Easting, a.Northing,
                Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
        }
        loop.Add(loop[0]);
        return loop;
    }

    /// <summary>
    /// Fillet each corner of a closed polygon with an arc of <paramref name="radius"/>
    /// (the turn radius), so a vehicle can actually drive the path instead of a
    /// sharp offset corner. The tangent set-back is clamped to the shorter adjacent
    /// edge; near-straight corners are left untouched. Returns an OPEN ring (caller
    /// re-closes). Input may be open or closed (a duplicate closing point is dropped).
    /// </summary>
    private static List<Vec2> RoundCorners(IReadOnlyList<Vec2> poly, double radius, double angleStepDeg = 12.0, bool closed = true)
    {
        int n = poly.Count;
        if (n < 3 || radius <= 0.01) return new List<Vec2>(poly);

        int count = (Distance(poly[0], poly[n - 1]) < 1e-6) ? n - 1 : n;   // drop closing dup
        if (count < 3) return new List<Vec2>(poly);

        double step = angleStepDeg * Math.PI / 180.0;
        var outp = new List<Vec2>(count * 3);
        for (int i = 0; i < count; i++)
        {
            var V = poly[i];
            // Open polylines (a continuous spiral) keep their endpoints; only interior
            // vertices are filleted, and neighbours are not wrapped around the ends.
            if (!closed && (i == 0 || i == count - 1)) { outp.Add(V); continue; }
            var P = poly[(i - 1 + count) % count];
            var N = poly[(i + 1) % count];

            double e1x = P.Easting - V.Easting, e1y = P.Northing - V.Northing;
            double e2x = N.Easting - V.Easting, e2y = N.Northing - V.Northing;
            double len1 = Math.Sqrt(e1x * e1x + e1y * e1y);
            double len2 = Math.Sqrt(e2x * e2x + e2y * e2y);
            if (len1 < 1e-6 || len2 < 1e-6) { outp.Add(V); continue; }
            double u1x = e1x / len1, u1y = e1y / len1;
            double u2x = e2x / len2, u2y = e2y / len2;

            double dot = Math.Clamp(u1x * u2x + u1y * u2y, -1.0, 1.0);
            double gamma = Math.Acos(dot);                 // interior angle at V
            if (gamma > Math.PI - 0.05 || gamma < 0.05) { outp.Add(V); continue; }

            double half = gamma / 2.0;
            double t = radius / Math.Tan(half);
            double maxT = 0.45 * Math.Min(len1, len2);
            if (t > maxT) t = maxT;
            double rEff = t * Math.Tan(half);
            if (rEff < 0.05) { outp.Add(V); continue; }

            double ax = V.Easting + u1x * t, ay = V.Northing + u1y * t;   // tangent on incoming edge
            double bx = V.Easting + u2x * t, by = V.Northing + u2y * t;   // tangent on outgoing edge

            double bisx = u1x + u2x, bisy = u1y + u2y;
            double bl = Math.Sqrt(bisx * bisx + bisy * bisy);
            if (bl < 1e-6) { outp.Add(V); continue; }
            bisx /= bl; bisy /= bl;
            double cDist = rEff / Math.Sin(half);
            double cx = V.Easting + bisx * cDist, cy = V.Northing + bisy * cDist;

            double a0 = Math.Atan2(ay - cy, ax - cx);
            double a1 = Math.Atan2(by - cy, bx - cx);
            double da = a1 - a0;
            while (da > Math.PI) da -= 2 * Math.PI;
            while (da < -Math.PI) da += 2 * Math.PI;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(da) / step));

            outp.Add(new Vec2(ax, ay));
            for (int s = 1; s < steps; s++)
            {
                double ang = a0 + da * s / steps;
                outp.Add(new Vec2(cx + rEff * Math.Cos(ang), cy + rEff * Math.Sin(ang)));
            }
            outp.Add(new Vec2(bx, by));
        }
        return outp;
    }

    /// <summary>
    /// Replace each ~90° corner of an OPEN polyline with a forward 270° "loop" turn that
    /// swings out into the corner apex (covering the wedge a rounded corner would miss),
    /// then continues on the outgoing edge. Non-corner vertices pass through unchanged.
    /// The loop is tangent to both edges and turns the long way, so it never reverses.
    /// </summary>
    private static List<Vec2> InsertCornerLoops(IReadOnlyList<Vec2> path, double radius, double minEdge, HashSet<int>? skip = null)
    {
        int n = path.Count;
        if (n < 3 || radius < 0.5) return new List<Vec2>(path);
        var outp = new List<Vec2> { path[0] };
        for (int i = 1; i < n - 1; i++)
        {
            var P = path[i - 1]; var V = path[i]; var N = path[i + 1];
            double ax = V.Easting - P.Easting, ay = V.Northing - P.Northing;
            double bx = N.Easting - V.Easting, by = N.Northing - V.Northing;
            double la = Math.Sqrt(ax * ax + ay * ay), lb = Math.Sqrt(bx * bx + by * by);
            if (la < 1e-6 || lb < 1e-6) { outp.Add(V); continue; }
            ax /= la; ay /= la; bx /= lb; by /= lb;
            double turn = Math.Atan2(ax * by - ay * bx, ax * bx + ay * by); // signed a->b
            // Fill a genuine ~90° LAP corner only. Skip the seam junctions between laps
            // (their connector is a long diagonal whose ends look like 90° corners), and
            // require both edges long enough for the loop to have room.
            if ((skip != null && skip.Contains(i))
                || Math.Abs(Math.Abs(turn) - Math.PI / 2) > 0.6 || Math.Min(la, lb) < minEdge)
            {
                outp.Add(V);
                continue;
            }
            outp.AddRange(CornerLoop(V, ax, ay, bx, by, radius));
        }
        outp.Add(path[n - 1]);
        return outp;
    }

    /// <summary>
    /// Forward 270° loop connecting incoming dir (ax,ay) to outgoing dir (bx,by) at corner
    /// V, tangent to both edges and bulging toward the corner apex. Entry → arc → exit.
    /// </summary>
    private static List<Vec2> CornerLoop(Vec2 V, double ax, double ay, double bx, double by, double R)
    {
        // Exterior-corner centre (distance R from both edges, apex side): V + R·(a - b).
        // Entry tangent = V + R·a; exit tangent = V - R·b.
        double cx = V.Easting + R * (ax - bx), cy = V.Northing + R * (ay - by);
        double p1x = V.Easting + R * ax, p1y = V.Northing + R * ay;
        double p2x = V.Easting - R * bx, p2y = V.Northing - R * by;

        double a1 = Math.Atan2(p1y - cy, p1x - cx);
        double a2 = Math.Atan2(p2y - cy, p2x - cx);
        double d = a2 - a1;
        while (d <= -Math.PI) d += 2 * Math.PI;
        while (d > Math.PI) d -= 2 * Math.PI;                       // short delta (~±90°)
        double longD = d > 0 ? d - 2 * Math.PI : d + 2 * Math.PI;   // go the long way (~270°)

        int steps = Math.Max(10, (int)Math.Ceiling(Math.Abs(longD) * R / 0.5));
        var outp = new List<Vec2>(steps + 2) { new Vec2(p1x, p1y) };
        for (int s = 1; s < steps; s++)
        {
            double ang = a1 + longD * s / steps;
            outp.Add(new Vec2(cx + R * Math.Cos(ang), cy + R * Math.Sin(ang)));
        }
        outp.Add(new Vec2(p2x, p2y));
        return outp;
    }

    private static void RotateToNearest(List<Vec2> poly, Vec2 target)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            double d = Distance(poly[i], target);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best == 0) return;
        var rotated = new List<Vec2>(poly.Count);
        for (int i = 0; i < poly.Count; i++) rotated.Add(poly[(best + i) % poly.Count]);
        poly.Clear();
        poly.AddRange(rotated);
    }

    /// <summary>
    /// Every contiguous run of <paramref name="curve"/> points inside
    /// <paramref name="poly"/> that is at least <paramref name="minLen"/> long.
    /// Keeping all runs (not just the longest) fills concave fields where one
    /// offset curve enters, exits, and re-enters the cultivated area.
    /// </summary>
    private static List<List<Vec3>> ClipCurveRuns(
        IReadOnlyList<Vec3> curve, IReadOnlyList<Vec2> poly, double minLen)
    {
        var runs = new List<List<Vec3>>();
        List<Vec3>? run = null;

        void Flush()
        {
            if (run is { Count: >= 2 } && PolylineLength(run) >= minLen) runs.Add(run);
            run = null;
        }

        foreach (var p in curve)
        {
            if (PointInPolygon(new Vec2(p.Easting, p.Northing), poly))
                (run ??= new List<Vec3>()).Add(p);
            else
                Flush();
        }
        Flush();
        return runs;
    }

    /// <summary>
    /// Resample a polyline to roughly even <paramref name="spacing"/> by
    /// arc-length, recomputing per-point heading. Keeps the first and last
    /// points so the curve's extent is preserved.
    /// </summary>
    private static List<Vec3> ResampleCurve(IReadOnlyList<Vec3> pts, double spacing)
    {
        if (pts.Count < 2 || spacing <= 0) return new List<Vec3>(pts);

        var outp = new List<Vec3> { pts[0] };
        double residual = 0;   // distance already consumed past the last sample

        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            double segLen = Distance(new Vec2(a.Easting, a.Northing), new Vec2(b.Easting, b.Northing));
            if (segLen < 1e-9) continue;

            double dirE = (b.Easting - a.Easting) / segLen;
            double dirN = (b.Northing - a.Northing) / segLen;
            double h = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);

            double pos = spacing - residual;     // first sample offset into this segment
            while (pos <= segLen)
            {
                outp.Add(new Vec3(a.Easting + dirE * pos, a.Northing + dirN * pos, h));
                pos += spacing;
            }
            residual = segLen - (pos - spacing);
        }

        outp.Add(pts[^1]);
        return outp;
    }

    private static bool PointInPolygon(Vec2 p, IReadOnlyList<Vec2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if (((a.Northing > p.Northing) != (b.Northing > p.Northing)) &&
                (p.Easting < (b.Easting - a.Easting) * (p.Northing - a.Northing) /
                    (b.Northing - a.Northing) + a.Easting))
                inside = !inside;
        }
        return inside;
    }

    private static double PolygonDiagonal(IReadOnlyList<Vec2> poly)
    {
        double minE = double.MaxValue, minN = double.MaxValue, maxE = double.MinValue, maxN = double.MinValue;
        foreach (var v in poly)
        {
            if (v.Easting < minE) minE = v.Easting;
            if (v.Easting > maxE) maxE = v.Easting;
            if (v.Northing < minN) minN = v.Northing;
            if (v.Northing > maxN) maxN = v.Northing;
        }
        double de = maxE - minE, dn = maxN - minN;
        return Math.Sqrt(de * de + dn * dn);
    }

    // ---- geometry helpers ----

    private static double LongestEdgeHeading(IReadOnlyList<Vec2> poly)
    {
        double best = 0, bestLen = -1;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double len = Distance(a, b);
            if (len > bestLen)
            {
                bestLen = len;
                best = Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing);
            }
        }
        return best;
    }

    private static Vec2 Centroid(IReadOnlyList<Vec2> poly)
    {
        double e = 0, n = 0;
        foreach (var v in poly) { e += v.Easting; n += v.Northing; }
        return new Vec2(e / poly.Count, n / poly.Count);
    }

    /// <summary>
    /// Clip the infinite line through <paramref name="lp"/> with direction
    /// (dE,dN) against the polygon and return the longest interior segment as
    /// (entry, exit), or null if the line misses the polygon.
    /// </summary>
    /// <summary>
    /// All interior intervals (parameter t along the line lp + t·d) where the line is
    /// inside <paramref name="poly"/>, as (lo,hi) pairs of consecutive crossings.
    /// </summary>
    private static List<(double lo, double hi)> LineInsideIntervals(
        Vec2 lp, double dE, double dN, IReadOnlyList<Vec2> poly)
    {
        var ts = new List<double>();
        for (int i = 0; i < poly.Count; i++)
        {
            var v0 = poly[i]; var v1 = poly[(i + 1) % poly.Count];
            double eE = v1.Easting - v0.Easting, eN = v1.Northing - v0.Northing;
            double det = eE * dN - eN * dE;
            if (Math.Abs(det) < 1e-9) continue;
            double rhsE = v0.Easting - lp.Easting, rhsN = v0.Northing - lp.Northing;
            double u = (dE * rhsN - dN * rhsE) / det;
            if (u < -1e-9 || u > 1 + 1e-9) continue;
            ts.Add((eE * rhsN - eN * rhsE) / det);
        }
        var res = new List<(double, double)>();
        if (ts.Count < 2) return res;
        ts.Sort();
        var uniq = new List<double> { ts[0] };
        for (int i = 1; i < ts.Count; i++) if (ts[i] - uniq[^1] > 1e-6) uniq.Add(ts[i]);
        for (int i = 0; i + 1 < uniq.Count; i += 2) res.Add((uniq[i], uniq[i + 1]));
        return res;
    }

    /// <summary>
    /// Clip a swath line to the parts inside <paramref name="outer"/> but OUTSIDE every
    /// hole in <paramref name="holes"/> (inner boundaries / ponds, already inflated by the
    /// tool half-width). A line crossing a hole splits into multiple segments that avoid it.
    /// </summary>
    private static List<(Vec2 Entry, Vec2 Exit)> ClipSegments(
        Vec2 lp, double dE, double dN, IReadOnlyList<Vec2> outer,
        IReadOnlyList<IReadOnlyList<Vec2>>? holes)
    {
        var result = new List<(Vec2, Vec2)>();
        var inside = LineInsideIntervals(lp, dE, dN, outer);
        if (inside.Count == 0) return result;

        var holeIv = new List<(double lo, double hi)>();
        if (holes != null)
            foreach (var h in holes)
                if (h.Count >= 3) holeIv.AddRange(LineInsideIntervals(lp, dE, dN, h));

        foreach (var (lo, hi) in inside)
        {
            var pieces = new List<(double lo, double hi)> { (lo, hi) };
            foreach (var (hlo, hhi) in holeIv)
            {
                var next = new List<(double lo, double hi)>();
                foreach (var (plo, phi) in pieces)
                {
                    if (hhi <= plo || hlo >= phi) { next.Add((plo, phi)); continue; } // no overlap
                    if (hlo > plo) next.Add((plo, hlo));   // piece before the hole
                    if (hhi < phi) next.Add((hhi, phi));   // piece after the hole
                }
                pieces = next;
            }
            foreach (var (plo, phi) in pieces)
                if (phi - plo > 1.0) // drop slivers < 1 m
                    result.Add((new Vec2(lp.Easting + plo * dE, lp.Northing + plo * dN),
                                new Vec2(lp.Easting + phi * dE, lp.Northing + phi * dN)));
        }
        return result;
    }

    /// <summary>
    /// Keep only the longest contiguous arc of <paramref name="ring"/> that lies OUTSIDE
    /// every hole (inflated pond) — the arc that winds around the obstacle. Returns the
    /// ring unchanged if it touches no hole, or empty if a hole fully shadows it.
    /// </summary>
    private static List<Vec2> ClipRingAgainstHoles(List<Vec2> ring, List<List<Vec2>> holes)
    {
        int n = ring.Count;
        if (n < 3) return ring;
        var inside = new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            bool ins = false;
            foreach (var h in holes)
                if (h.Count >= 3 && GeometryMath.IsPointInPolygon(h, ring[i])) { ins = true; break; }
            inside[i] = ins; any |= ins;
        }
        if (!any) return ring;

        // Longest circular run of outside points (scan the doubled index to allow wrap).
        int bestStart = 0, bestLen = 0, curStart = 0, curLen = 0;
        for (int k = 0; k < 2 * n; k++)
        {
            int idx = k % n;
            if (!inside[idx])
            {
                if (curLen == 0) curStart = idx;
                curLen++;
                if (curLen > bestLen && curLen <= n) { bestLen = curLen; bestStart = curStart; }
            }
            else curLen = 0;
        }
        if (bestLen < 2) return new List<Vec2>();
        var arc = new List<Vec2>(bestLen);
        for (int k = 0; k < bestLen; k++) arc.Add(ring[(bestStart + k) % n]);
        return arc;
    }

    /// <summary>
    /// Reroute a polyline (a turn / connector / approach / headland lap) so it goes AROUND
    /// the holes instead of cutting into them. Densify to ~2 m, then replace every run of
    /// points that falls inside a hole with an arc along that hole's boundary (shorter way).
    /// Handles both a straight edge crossing a hole and a turn that dips inside it.
    /// </summary>
    private static List<Vec3> RouteAroundHoles(IReadOnlyList<Vec3> line, List<List<Vec2>> holes,
        double smoothRadius = 0, bool localOnly = false)
    {
        if (line.Count < 2 || holes.Count == 0) return new List<Vec3>(line);

        // Densify so a crossing registers as a run of interior points. The parallel
        // 'moveable' mask marks detour points (and later their neighbourhood) — in
        // localOnly mode ONLY those may be smoothed, so the rest of the leg keeps its
        // exact shape (a headland lap or a long turn must not be globally relaxed:
        // Laplacian smoothing is curve-shortening and would contract it into a blob).
        var pts = new List<Vec2>();
        for (int i = 0; i < line.Count; i++)
        {
            var a = new Vec2(line[i].Easting, line[i].Northing);
            pts.Add(a);
            if (i + 1 >= line.Count) continue;
            var b = new Vec2(line[i + 1].Easting, line[i + 1].Northing);
            int steps = (int)(Distance(a, b) / 2.0);
            for (int s = 1; s < steps; s++)
            {
                double t = (double)s / steps;
                pts.Add(new Vec2(a.Easting + (b.Easting - a.Easting) * t, a.Northing + (b.Northing - a.Northing) * t));
            }
        }
        var moveable = new List<bool>(new bool[pts.Count]);

        bool detoured = false;
        foreach (var hole in holes)
        {
            if (hole.Count < 3) continue;
            var np = new List<Vec2>();
            var nm = new List<bool>();
            int i = 0;
            while (i < pts.Count)
            {
                if (!GeometryMath.IsPointInPolygon(hole, pts[i])) { np.Add(pts[i]); nm.Add(moveable[i]); i++; continue; }
                int j = i;
                while (j < pts.Count && GeometryMath.IsPointInPolygon(hole, pts[j])) j++;
                var before = np.Count > 0 ? np[^1] : pts[i];
                var after = j < pts.Count ? pts[j] : before;
                foreach (var v in HoleBoundaryDetour(hole, before, after)) { np.Add(v); nm.Add(true); }
                detoured = true;
                i = j;
            }
            pts = np;
            moveable = nm;
        }

        if (!detoured) return new List<Vec3>(line);   // no crossing — leave the line as-is

        // Ease the detour: hugging the obstacle-boundary vertices makes the turn snap around
        // sharp corners. Constrained smoothing (Laplacian relaxation that pushes any point
        // that drifts inside a ring back onto its boundary) converges to the taut-string path
        // — tangent straights + ring-hugging arcs. localOnly restricts the relaxation to a
        // window around each detour so the leg only deviates near the obstacle.
        if (detoured && smoothRadius > 0.1 && pts.Count >= 3)
        {
            if (localOnly)
            {
                ExpandMask(pts, moveable, Math.Max(10.0, smoothRadius * 2.5));
                pts = ConstrainedSmoothMasked(pts, holes, moveable, 150);
            }
            else
                pts = ConstrainedSmooth(pts, holes, smoothRadius, 150);
        }

        var outp = new List<Vec3>(pts.Count);
        for (int k = 0; k < pts.Count; k++)
        {
            var a = pts[k]; var b = pts[Math.Min(k + 1, pts.Count - 1)];
            outp.Add(new Vec3(a.Easting, a.Northing, Math.Atan2(b.Easting - a.Easting, b.Northing - a.Northing)));
        }
        return outp;
    }

    /// <summary>Largest bounding-box side of an obstacle polygon (its rough footprint size).</summary>
    private static double ObstacleMaxExtent(IReadOnlyList<Vec2> poly)
    {
        double minE = double.MaxValue, maxE = double.MinValue, minN = double.MaxValue, maxN = double.MinValue;
        foreach (var v in poly)
        {
            if (v.Easting < minE) minE = v.Easting;
            if (v.Easting > maxE) maxE = v.Easting;
            if (v.Northing < minN) minN = v.Northing;
            if (v.Northing > maxN) maxN = v.Northing;
        }
        return Math.Max(maxE - minE, maxN - minN);
    }

    /// <summary>
    /// Swerve every leg of a plan (swaths included) around small obstacles. Each swerve
    /// ring is inflated a further turn radius (round joins → drivable arcs), then every
    /// segment is rerouted around them with the constrained smoother. Legs that don't come
    /// near a ring are returned untouched, so the passes stay dead straight except for a
    /// local deviation where the physical frame would otherwise clip the obstacle.
    /// </summary>
    private RoutePlan SwervePlan(RoutePlan plan, List<List<Vec2>> swerveHoles, double turnRadius)
    {
        // Push out only to the physical-clearance ring itself (NOT inflated by the turn
        // radius, unlike the connector reroute) so the deviation is minimal — just enough
        // for the frame to clear. The constrained smoother then spreads that small lateral
        // offset longitudinally into a gentle, drivable bump rather than a tight bulge.
        double r = turnRadius > 0.1 ? turnRadius : 3.0;
        var segs = new List<RouteSegment>(plan.Segments.Count);
        foreach (var seg in plan.Segments)
            segs.Add(seg.Points.Count >= 2
                ? new RouteSegment(seg.Type, RouteAroundHoles(seg.Points, swerveHoles, r, localOnly: true))
                : seg);
        return new RoutePlan(segs, BuildMeta(segs, plan.Metadata.ToolWidthMeters));
    }

    /// <summary>
    /// Smooth a detour polyline toward the shortest drivable path that stays outside the
    /// <paramref name="rings"/> (obstacles inflated by the turn radius). Endpoints are pinned;
    /// each interior point is relaxed toward the midpoint of its neighbours, then any point
    /// that lands inside a ring is projected back onto that ring's boundary. Repeating this
    /// pulls the path taut (straight where it can be, hugging a ring where it must) while
    /// never letting curvature exceed the ring's — i.e. never tighter than the turn radius.
    /// </summary>
    private static List<Vec2> ConstrainedSmooth(List<Vec2> pts, List<List<Vec2>> rings, double smoothRadius, int iterations)
    {
        // Coarser spacing converges to a given radius far faster (iterations needed
        // scale with (R/ds)^2) and keeps the point count sane.
        double ds = Math.Max(0.5, smoothRadius * 0.2);
        var work = ResampleUniform(pts, ds);
        int n = work.Count;
        if (n < 3) return work;

        for (int it = 0; it < iterations; it++)
        {
            var np = new List<Vec2>(work);
            for (int i = 1; i < n - 1; i++)
            {
                double mx = 0.5 * (work[i - 1].Easting + work[i + 1].Easting);
                double my = 0.5 * (work[i - 1].Northing + work[i + 1].Northing);
                np[i] = new Vec2(work[i].Easting + 0.5 * (mx - work[i].Easting),
                                 work[i].Northing + 0.5 * (my - work[i].Northing));
            }
            for (int i = 1; i < n - 1; i++)
                foreach (var ring in rings)
                    if (ring.Count >= 3 && GeometryMath.IsPointInPolygon(ring, np[i]))
                        np[i] = NearestOnPolygon(ring, np[i]);
            work = np;
        }
        return work;
    }

    /// <summary>Widen the moveable mask by <paramref name="window"/> meters of polyline on
    /// each side of every detour point, so the smoother can blend the deviation into the
    /// surrounding leg. Endpoints stay pinned.</summary>
    private static void ExpandMask(List<Vec2> pts, List<bool> mask, double window)
    {
        int n = pts.Count;
        var orig = new bool[n];
        for (int i = 0; i < n; i++) orig[i] = mask[i];
        for (int i = 0; i < n; i++)
        {
            if (!orig[i]) continue;
            double acc = 0;
            for (int k = i - 1; k >= 0 && acc < window; k--) { acc += Distance(pts[k], pts[k + 1]); mask[k] = true; }
            acc = 0;
            for (int k = i + 1; k < n && acc < window; k++) { acc += Distance(pts[k], pts[k - 1]); mask[k] = true; }
        }
        mask[0] = false;
        mask[n - 1] = false;
    }

    /// <summary>
    /// Like <see cref="ConstrainedSmooth"/> but only relaxes points flagged moveable —
    /// used by the small-obstacle swerve so a pass bends smoothly around the obstacle
    /// while the rest of the leg (which may be a whole headland lap) keeps its shape.
    /// No resampling, so the mask stays aligned with the points.
    /// </summary>
    private static List<Vec2> ConstrainedSmoothMasked(List<Vec2> pts, List<List<Vec2>> rings,
        List<bool> moveable, int iterations)
    {
        int n = pts.Count;
        if (n < 3) return pts;
        var work = new List<Vec2>(pts);
        for (int it = 0; it < iterations; it++)
        {
            var np = new List<Vec2>(work);
            for (int i = 1; i < n - 1; i++)
            {
                if (!moveable[i]) continue;
                double mx = 0.5 * (work[i - 1].Easting + work[i + 1].Easting);
                double my = 0.5 * (work[i - 1].Northing + work[i + 1].Northing);
                np[i] = new Vec2(work[i].Easting + 0.5 * (mx - work[i].Easting),
                                 work[i].Northing + 0.5 * (my - work[i].Northing));
            }
            for (int i = 1; i < n - 1; i++)
            {
                if (!moveable[i]) continue;
                foreach (var ring in rings)
                    if (ring.Count >= 3 && GeometryMath.IsPointInPolygon(ring, np[i]))
                        np[i] = NearestOnPolygon(ring, np[i]);
            }
            work = np;
        }
        return work;
    }

    /// <summary>Resample a polyline to roughly uniform <paramref name="ds"/>-meter spacing,
    /// keeping the exact endpoints.</summary>
    private static List<Vec2> ResampleUniform(List<Vec2> pts, double ds)
    {
        if (pts.Count < 2 || ds <= 0.01) return new List<Vec2>(pts);
        var outp = new List<Vec2> { pts[0] };
        double carry = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1]; var b = pts[i];
            double seg = Distance(a, b);
            if (seg < 1e-6) continue;
            double t = ds - carry;
            while (t <= seg)
            {
                double f = t / seg;
                outp.Add(new Vec2(a.Easting + (b.Easting - a.Easting) * f,
                                  a.Northing + (b.Northing - a.Northing) * f));
                t += ds;
            }
            carry = seg - (t - ds);
        }
        if (Distance(outp[^1], pts[^1]) > 1e-6) outp.Add(pts[^1]);
        return outp;
    }

    /// <summary>Nearest point to <paramref name="p"/> on the boundary (edges) of a polygon.</summary>
    private static Vec2 NearestOnPolygon(IReadOnlyList<Vec2> poly, Vec2 p)
    {
        Vec2 best = poly[0]; double bestD = double.MaxValue;
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[(i + 1) % poly.Count];
            var q = Vec2.ProjectOnSegment(p, a, b);
            double d = Distance(p, q);
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    /// <summary>
    /// Inflate a hole ANISOTROPICALLY — by <paramref name="perpAmt"/> only along the pass-
    /// perpendicular axis (pE,pN), plus a small uniform <paramref name="clearance"/>. This is
    /// the Minkowski sum of the hole with a cross-pass segment (approximated by the convex
    /// hull of the hole translated ±perp, then offset). Used to clip swaths so they reach the
    /// faces they hit head-on without the wedge gaps a uniform disc inflation leaves.
    /// </summary>
    private List<Vec2> InflatePerp(IReadOnlyList<Vec2> poly, double pE, double pN, double perpAmt, double clearance)
    {
        var pts = new List<Vec2>(poly.Count * 2);
        foreach (var v in poly)
        {
            pts.Add(new Vec2(v.Easting + pE * perpAmt, v.Northing + pN * perpAmt));
            pts.Add(new Vec2(v.Easting - pE * perpAmt, v.Northing - pN * perpAmt));
        }
        var hull = ConvexHull(pts);
        if (hull.Count < 3) return new List<Vec2>(poly);
        if (clearance > 0.01)
        {
            var infl = _offset.CreateOutwardOffset(hull, clearance);
            if (infl is { Count: >= 3 }) return infl;
        }
        return hull;
    }

    /// <summary>Andrew's monotone-chain convex hull (CCW).</summary>
    private static List<Vec2> ConvexHull(List<Vec2> pts)
    {
        if (pts.Count < 3) return new List<Vec2>(pts);
        var p = new List<Vec2>(pts);
        p.Sort((a, b) => a.Easting != b.Easting ? a.Easting.CompareTo(b.Easting) : a.Northing.CompareTo(b.Northing));
        double Cross(Vec2 o, Vec2 a, Vec2 b) =>
            (a.Easting - o.Easting) * (b.Northing - o.Northing) - (a.Northing - o.Northing) * (b.Easting - o.Easting);
        var h = new List<Vec2>();
        for (int i = 0; i < p.Count; i++)   // lower
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p[i]);
        }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--)   // upper
        {
            while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1);
            h.Add(p[i]);
        }
        h.RemoveAt(h.Count - 1);
        return h;
    }

    /// <summary>Douglas-Peucker: drop points within <paramref name="tol"/> of the line
    /// through their neighbours, keeping only the corners that define the shape.</summary>
    private static List<Vec2> Simplify(List<Vec2> pts, double tol)
    {
        int n = pts.Count;
        if (n < 3) return pts;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int a, int b)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            double maxD = 0; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                double d = PerpDistance(pts[i], pts[a], pts[b]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (maxD > tol && idx > 0)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }
        var r = new List<Vec2>();
        for (int i = 0; i < n; i++) if (keep[i]) r.Add(pts[i]);
        return r;
    }

    private static double PerpDistance(Vec2 p, Vec2 a, Vec2 b)
    {
        double ex = b.Easting - a.Easting, ey = b.Northing - a.Northing;
        double len2 = ex * ex + ey * ey;
        if (len2 < 1e-9) return Distance(p, a);
        double t = Math.Clamp(((p.Easting - a.Easting) * ex + (p.Northing - a.Northing) * ey) / len2, 0, 1);
        return Distance(new Vec2(a.Easting + t * ex, a.Northing + t * ey), p);
    }

    /// <summary>Vertices of <paramref name="hole"/> from nearest-to-E to nearest-to-X, the
    /// shorter way around — the arc the detour follows along the (inflated) obstacle.</summary>
    private static List<Vec2> HoleBoundaryDetour(List<Vec2> hole, Vec2 E, Vec2 X)
    {
        int n = hole.Count;
        int iE = 0, iX = 0; double dE0 = double.MaxValue, dX0 = double.MaxValue;
        for (int k = 0; k < n; k++)
        {
            double de = Distance(hole[k], E); if (de < dE0) { dE0 = de; iE = k; }
            double dx = Distance(hole[k], X); if (dx < dX0) { dX0 = dx; iX = k; }
        }
        List<Vec2> Walk(int step)
        {
            var r = new List<Vec2>();
            for (int k = iE, guard = 0; guard <= n; k = (k + step + n) % n, guard++)
            {
                r.Add(hole[k]);
                if (k == iX) break;
            }
            return r;
        }
        var fwd = Walk(1); var bwd = Walk(-1);
        return PolylineLength2(fwd) <= PolylineLength2(bwd) ? fwd : bwd;
    }

    private static double PolylineLength2(List<Vec2> pts)
    {
        double L = 0;
        for (int i = 1; i < pts.Count; i++) L += Distance(pts[i - 1], pts[i]);
        return L;
    }

    private static (Vec2 Entry, Vec2 Exit)? ClipLongestSegment(
        Vec2 lp, double dE, double dN, IReadOnlyList<Vec2> poly)
    {
        var ts = new List<double>();
        for (int i = 0; i < poly.Count; i++)
        {
            var v0 = poly[i];
            var v1 = poly[(i + 1) % poly.Count];
            double eE = v1.Easting - v0.Easting;
            double eN = v1.Northing - v0.Northing;

            double det = eE * dN - eN * dE;
            if (Math.Abs(det) < 1e-9) continue;     // parallel

            double rhsE = v0.Easting - lp.Easting;
            double rhsN = v0.Northing - lp.Northing;
            double u = (dE * rhsN - dN * rhsE) / det;     // along the edge
            if (u < -1e-9 || u > 1 + 1e-9) continue;

            double t = (eE * rhsN - eN * rhsE) / det;     // along the line
            ts.Add(t);
        }

        if (ts.Count < 2) return null;
        ts.Sort();

        // Dedup near-equal crossings (a line through a vertex hits two edges).
        var uniq = new List<double> { ts[0] };
        for (int i = 1; i < ts.Count; i++)
            if (ts[i] - uniq[^1] > 1e-6) uniq.Add(ts[i]);
        if (uniq.Count < 2) return null;

        // Longest interior interval (pairs of crossings).
        double bestLo = 0, bestHi = 0, bestLen = -1;
        for (int i = 0; i + 1 < uniq.Count; i += 2)
        {
            double lo = uniq[i], hi = uniq[i + 1];
            if (hi - lo > bestLen) { bestLen = hi - lo; bestLo = lo; bestHi = hi; }
        }
        if (bestLen <= 0) return null;

        var entry = new Vec2(lp.Easting + bestLo * dE, lp.Northing + bestLo * dN);
        var exit = new Vec2(lp.Easting + bestHi * dE, lp.Northing + bestHi * dN);
        return (entry, exit);
    }

    /// <summary>
    /// A headland turn from <paramref name="from"/> (exit pose) to
    /// <paramref name="to"/> (next pass entry) that never turns tighter than the
    /// tractor's <paramref name="turnRadius"/>. When the passes are at least one
    /// turning diameter apart, a plain semicircle (radius = half the gap, which is
    /// ≥ the turn radius) suffices. When they're closer than 2R, an omega/sagitta
    /// turn at radius R is used instead — so the path stays physically drivable.
    /// </summary>
    private static List<Vec3> BuildTurn(Vec3 from, Vec3 to, double turnRadius,
        IReadOnlyList<Vec2>? limit, List<List<Vec2>>? holes, bool allowReverse = false,
        SweptConstraint? swept = null)
    {
        var toPt = new Vec2(to.Easting, to.Northing);

        // No radius limit configured — keep the simple, exact semicircle.
        if (turnRadius <= 0.1)
            return BuildSemicircleTurn(from, toPt);

        // 1. Direct Dubins turn at the min radius. Shortest-valid picks the tidy
        //    headland omega when it fits, or a longer loop into open space when
        //    the omega would poke past the fence / into an obstacle.
        var best = swept != null ? new BestEffort() : null;
        var direct = ValidatedDubinsTurn(from, 0, to, turnRadius, limit, holes, swept, best);
        if (direct != null) return direct;

        // 2. Nothing fit at the pass end. Drive further forward first (into the
        //    open headland / paddock) so the loop has room, growing the run until
        //    a valid turn appears — "drive on, turn where there's space, come back".
        for (double d = turnRadius; d <= turnRadius * 6.0 + 1e-6; d += turnRadius)
        {
            var extended = ValidatedDubinsTurn(from, d, to, turnRadius, limit, holes, swept, best);
            if (extended != null) return extended;
        }

        // 3. Reverse K-turn (Reeds-Shepp): where forward-only motion physically can't
        //    work — e.g. a pass ending at an obstacle face pointing straight at it —
        //    a 3-point turn can. Validated like the Dubins candidates; waypoint
        //    headings are vehicle-facing so reverse legs render/steer correctly.
        if (allowReverse)
            try
            {
                var rs = new ReedsSheppPathService(turnRadius).GetShortestPath(from, to, 0.2);
                if (rs.Waypoints.Count >= 2)
                {
                    var rsPts = new List<Vec2>(rs.Waypoints.Count);
                    foreach (var w in rs.Waypoints) rsPts.Add(new Vec2(w.Easting, w.Northing));
                    if (PathInside(rsPts, limit) && PathClearsHoles(rsPts, holes)
                        && SweptClear(rs.Waypoints, swept))
                        return new List<Vec3>(rs.Waypoints);
                }
            }
            catch { /* degenerate poses — fall through to the clamped fallback */ }

        // 4. No fully-in-bounds turn found. With a swept-body constraint, prefer
        //    the candidate whose implement clipped the LEAST (tractor path was
        //    fine) — that's the safest drivable option near a hard fence.
        if (best?.Path != null) return best.Path;
        //    Otherwise the shortest *min-radius* Dubins path even though it clips
        //    the boundary/an obstacle: it's smooth and drivable, and the later
        //    RouteAroundHoles / CloseTransitGaps stages detour it around obstacles.
        //    This is the common case for long block-transition connectors around
        //    a pond. Only if Dubins yields nothing at all do we drop to the legacy omega.
        var fallback = DubinsTurn.AllPaths(from, to, turnRadius, CcBlendFor(turnRadius));
        if (fallback.Count > 0)
            return DensifyToVec3(fallback[0].Coords, from.Heading);
        return LegacyOmega(from, toPt, turnRadius);
    }

    /// <summary>
    /// Build a turn that first drives straight <paramref name="forward"/> meters
    /// from <paramref name="from"/> along its heading, then follows the shortest
    /// Dubins arc (at <paramref name="turnRadius"/>) to <paramref name="to"/>.
    /// Returns the path (sub-sampled Vec3 with headings) only if every point stays
    /// inside <paramref name="limit"/> and outside <paramref name="holes"/>; else null.
    /// </summary>
    /// <summary>Continuous-curvature blend distance for a given turn radius: a
    /// quarter of the radius, capped at 1.5 m. Big enough that the steering
    /// sweeps rather than steps at arc junctions, small enough that the path
    /// barely deviates from the analytic Dubins (which the boundary checks then
    /// validate as-driven anyway).</summary>
    private static double CcBlendFor(double turnRadius) => Math.Min(1.5, turnRadius * 0.25);

    private static List<Vec3>? ValidatedDubinsTurn(Vec3 from, double forward, Vec3 to,
        double turnRadius, IReadOnlyList<Vec2>? limit, List<List<Vec2>>? holes,
        SweptConstraint? swept = null, BestEffort? best = null)
    {
        double dE = Math.Sin(from.Heading), dN = Math.Cos(from.Heading);
        var arcStart = new Vec3(from.Easting + forward * dE, from.Northing + forward * dN, from.Heading);

        foreach (var (coords, _) in DubinsTurn.AllPaths(arcStart, to, turnRadius, CcBlendFor(turnRadius)))
        {
            // Prepend the straight forward leg (sampled) so it's validated too.
            var dense = new List<Vec2>();
            if (forward > 0.01)
            {
                int n = Math.Max(1, (int)Math.Ceiling(forward / 0.25));
                for (int i = 0; i < n; i++)
                {
                    double t = forward * i / n;
                    dense.Add(new Vec2(from.Easting + t * dE, from.Northing + t * dN));
                }
            }
            dense.AddRange(coords);

            if (!PathInside(dense, limit)) continue;
            if (!PathClearsHoles(dense, holes)) continue;
            // Tractor path fits — now the implement body: rear corners of a long
            // mounted tool swing wide of the pivot's arc, a trailed one off-tracks.
            var vec3 = DensifyToVec3(dense, from.Heading);
            double intrusion = SweptIntrusion(vec3, swept);
            if (intrusion > 0)
            {
                // Tractor fits but the implement clips: keep as best-effort fallback.
                if (best != null && intrusion < best.Intrusion) { best.Intrusion = intrusion; best.Path = vec3; }
                continue;
            }
            return vec3;
        }
        return null;
    }

    /// <summary>Least-intruding candidate seen while searching for a fully clear
    /// one — used as the fallback when none clears.</summary>
    private sealed class BestEffort
    {
        public double Intrusion = double.PositiveInfinity;
        public List<Vec3>? Path;
    }

    private static bool PathInside(IReadOnlyList<Vec2> pts, IReadOnlyList<Vec2>? limit)
    {
        if (limit is not { Count: >= 3 }) return true;
        foreach (var p in pts)
            if (!GeometryMath.IsPointInPolygon(limit, p)) return false;
        return true;
    }

    private static bool PathClearsHoles(IReadOnlyList<Vec2> pts, List<List<Vec2>>? holes)
    {
        if (holes is not { Count: > 0 }) return true;
        foreach (var hole in holes)
        {
            if (hole.Count < 3) continue;
            foreach (var p in pts)
                if (GeometryMath.IsPointInPolygon(hole, p)) return false;
        }
        return true;
    }

    /// <summary>
    /// Sub-sample dense (0.05 m) Dubins coordinates to ~0.4 m spacing and attach
    /// travel headings (dE = sin, dN = cos).
    /// </summary>
    private static List<Vec3> DensifyToVec3(IReadOnlyList<Vec2> coords, double startHeading)
    {
        if (coords.Count == 0) return new List<Vec3>();

        var pts = new List<Vec2> { coords[0] };
        double acc = 0; Vec2 last = coords[0];
        for (int i = 1; i < coords.Count; i++)
        {
            acc += Distance(last, coords[i]); last = coords[i];
            if (acc >= 0.4) { pts.Add(coords[i]); acc = 0; }
        }
        if (pts.Count < 2 || pts[^1] != coords[^1]) pts.Add(coords[^1]);

        var outp = new List<Vec3>(pts.Count);
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double hE = pts[i + 1].Easting - pts[i].Easting;
            double hN = pts[i + 1].Northing - pts[i].Northing;
            double hdg = (Math.Abs(hE) < 1e-9 && Math.Abs(hN) < 1e-9)
                ? (i > 0 ? outp[i - 1].Heading : startHeading)
                : Math.Atan2(hE, hN);
            outp.Add(new Vec3(pts[i].Easting, pts[i].Northing, hdg));
        }
        outp.Add(new Vec3(pts[^1].Easting, pts[^1].Northing, outp.Count > 0 ? outp[^1].Heading : startHeading));
        return outp;
    }

    /// <summary>
    /// Legacy omega/semicircle turn (no boundary awareness) used as a last-resort
    /// fallback when no Dubins turn fits even with a forward run.
    /// </summary>
    private static List<Vec3> LegacyOmega(Vec3 from, Vec2 to, double turnRadius)
    {
        var p0 = new Vec2(from.Easting, from.Northing);
        double gap = Distance(p0, to);

        if (gap >= 2.0 * turnRadius - 0.05)
            return BuildSemicircleTurn(from, to);

        double fE = Math.Sin(from.Heading), fN = Math.Cos(from.Heading);   // exit travel
        double rightE = fN, rightN = -fE;                                  // right normal
        double latE = to.Easting - p0.Easting, latN = to.Northing - p0.Northing;
        bool turnRight = (latE * rightE + latN * rightN) > 0;

        double offset = Math.Clamp(2.0 * turnRadius - gap, 0, 2.0 * turnRadius);
        var arc = SagittaTurnGeometry.BuildOffsetArc(
            new Vec3(from.Easting, from.Northing, from.Heading),
            from.Heading, turnRight, turnRadius, offset, Math.PI);

        if (arc.Count >= 2)
        {
            var end = arc[^1];
            if (Distance(new Vec2(end.Easting, end.Northing), to) > 0.05)
                arc.Add(new Vec3(to.Easting, to.Northing, end.Heading));
        }
        return arc.Count >= 2 ? arc : BuildSemicircleTurn(from, to);
    }

    /// <summary>
    /// A semicircular U-turn from <paramref name="from"/> to <paramref name="to"/>,
    /// bulging in the exit travel direction (into the headland). Radius is half the
    /// gap between the two swath ends.
    /// </summary>
    private static List<Vec3> BuildSemicircleTurn(Vec3 from, Vec2 to)
    {
        var p0 = new Vec2(from.Easting, from.Northing);
        double mE = (p0.Easting + to.Easting) / 2.0;
        double mN = (p0.Northing + to.Northing) / 2.0;
        double r = Distance(p0, to) / 2.0;

        if (r < 1e-3)
            return new List<Vec3> { from, new Vec3(to.Easting, to.Northing, from.Heading) };

        double u0E = (p0.Easting - mE) / r, u0N = (p0.Northing - mN) / r;     // center -> p0 (lateral)
        double dE = Math.Sin(from.Heading), dN = Math.Cos(from.Heading);      // exit travel dir (bulge side)

        // Sample finely enough that the chord stays ~0.5 m even on wide turns —
        // a fixed step count makes big semicircles look faceted/angular.
        int steps = Math.Clamp((int)Math.Ceiling(Math.PI * r / 0.5), 18, 240);
        var pts = new List<Vec3>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double ang = Math.PI * i / steps;
            double c = Math.Cos(ang), s = Math.Sin(ang);
            double e = mE + r * c * u0E + r * s * dE;
            double n = mN + r * c * u0N + r * s * dN;
            pts.Add(new Vec3(e, n, 0));
        }
        return pts;
    }

    private static double Distance(Vec2 a, Vec2 b)
    {
        double de = a.Easting - b.Easting, dn = a.Northing - b.Northing;
        return Math.Sqrt(de * de + dn * dn);
    }

    private static double PolylineLength(IReadOnlyList<Vec3> pts)
    {
        double total = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            double de = pts[i].Easting - pts[i - 1].Easting;
            double dn = pts[i].Northing - pts[i - 1].Northing;
            total += Math.Sqrt(de * de + dn * dn);
        }
        return total;
    }

    /// <summary>
    /// Find the uncovered regions of <paramref name="boundary"/> for an assembled
    /// route: inflate every worked pass (swaths + headland laps) to a band one
    /// <paramref name="toolWidth"/> wide, union them into the swept area, and
    /// subtract that from the field. Regions smaller than <paramref name="minAreaM2"/>
    /// are dropped as slivers. Returns the missed-spot polygons in local meters.
    /// </summary>
    public List<List<Vec2>> FindCoverageGaps(
        IReadOnlyList<Vec2> boundary, RoutePlan plan, double toolWidth, double minAreaM2)
    {
        var gaps = new List<List<Vec2>>();
        if (boundary == null || boundary.Count < 3 || plan == null || toolWidth <= 0.1)
            return gaps;

        const double S = 1000.0;   // Clipper integer scale (mm precision)

        // Worked passes -> open line paths.
        var lines = new Paths64();
        foreach (var seg in plan.Segments)
        {
            if (seg.Type is not (RouteSegmentType.Swath or RouteSegmentType.Headland)) continue;
            if (seg.Points.Count < 2) continue;
            var p = new Path64(seg.Points.Count);
            foreach (var q in seg.Points)
                p.Add(new Point64((long)(q.Easting * S), (long)(q.Northing * S)));
            lines.Add(p);
        }
        if (lines.Count == 0) return gaps;

        // Inflate each pass to a tool-width band; union -> swept area.
        var bands = Clipper.InflatePaths(lines, toolWidth * 0.5 * S, JoinType.Round, EndType.Round);
        var coverage = Clipper.Union(bands, FillRule.NonZero);

        // Field minus coverage = the missed regions.
        var field = new Path64(boundary.Count);
        foreach (var b in boundary)
            field.Add(new Point64((long)(b.Easting * S), (long)(b.Northing * S)));
        var diff = Clipper.Difference(new Paths64 { field }, coverage, FillRule.NonZero);

        double minArea = minAreaM2 * S * S;
        foreach (var path in diff)
        {
            if (path.Count < 3 || Math.Abs(Clipper.Area(path)) < minArea) continue;
            var poly = new List<Vec2>(path.Count);
            foreach (var pt in path) poly.Add(new Vec2(pt.X / S, pt.Y / S));
            gaps.Add(poly);
        }
        return gaps;
    }

    /// <summary>
    /// Summarise an assembled route: split distance into working (swaths +
    /// headland laps) vs non-working (turns + transport), count the U-turns, and
    /// carry the tool width for area/coverage. Speeds are applied later (VM).
    /// </summary>
    private static RoutePlanMetadata BuildMeta(IReadOnlyList<RouteSegment> segments, double toolWidth)
    {
        double total = 0, work = 0, turn = 0;
        int swaths = 0, turns = 0;
        foreach (var seg in segments)
        {
            double len = PolylineLength(seg.Points);
            total += len;
            switch (seg.Type)
            {
                case RouteSegmentType.Swath: work += len; swaths++; break;
                case RouteSegmentType.Headland: work += len; break;
                case RouteSegmentType.Turn: turn += len; turns++; break;
                default: turn += len; break;   // Approach / transport
            }
        }
        double est = EstimateSpeedMps > 0 ? total / EstimateSpeedMps : 0;
        return new RoutePlanMetadata(swaths, total, est, work, turn, turns, toolWidth);
    }

    /// <summary>
    /// Estimated time to drive a route, in seconds: worked distance at the working
    /// speed, non-working (turn + transport) distance at the turn speed, plus a fixed
    /// per-turn overhead for the decelerate / manoeuvre / accelerate each U-turn costs
    /// beyond its arc length. Used to compare candidate plans (pattern / angle / skip)
    /// so the planner can pick the genuinely fastest one, not just the shortest.
    /// </summary>
    public static double EstimateWorkSeconds(
        RoutePlanMetadata meta, double workSpeedMps, double turnSpeedMps, double turnOverheadSec)
    {
        if (meta == null) return double.MaxValue;
        double w = meta.WorkDistanceMeters / Math.Max(0.1, workSpeedMps);
        double t = meta.TurnDistanceMeters / Math.Max(0.1, turnSpeedMps);
        return w + t + meta.TurnCount * Math.Max(0, turnOverheadSec);
    }
}

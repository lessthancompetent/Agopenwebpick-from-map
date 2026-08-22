// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.RoutePlanning;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.RoutePlanning;

namespace AgOpenWeb.Services.Tests.RoutePlanning;

/// <summary>
/// Pins the symmetric turn lead-in/out: each inter-pass U-turn must be pushed OFF the
/// application edge into the reserved headland, with a straight run aligned to the pass
/// on BOTH ends — a run-OUT continuing the exit heading past the pass end, and a run-IN
/// arriving straight along the next pass heading. Tool-up connector runs (RouteSegmentType
/// .Turn), so no coverage. The depth is derived inside GenerateBoustrophedon:
///   L = max(0, min(turnRadius, headlandMargin − ext − clearance − turnRadius)).
/// </summary>
[TestFixture]
public class RouteTurnLeadInTests
{
    // 200 × 200 m square; passes run east-west (heading 90°) so they turn at the
    // E/W ends inside a deep headland band.
    private static readonly List<Vec2> Field = new()
    {
        new Vec2(0, 0), new Vec2(200, 0), new Vec2(200, 200), new Vec2(0, 200),
    };

    private const double Width = 6.0;
    private const double TurnR = 4.0;
    private const double HeadlandMargin = 30.0;   // deep: 5 × width
    // ext = 0 (no trailing tool), clearance = 0 → L = min(4, 30-0-0-4) = 4 m.
    private const double ExpectedLead = 4.0;

    private static RoutePlanningService NewPlanner() => new(new PolygonOffsetService());

    private static (double dirE, double dirN) Unit(Vec3 from, Vec3 to)
    {
        double e = to.Easting - from.Easting, n = to.Northing - from.Northing;
        double len = Math.Sqrt(e * e + n * n);
        return len < 1e-9 ? (0, 0) : (e / len, n / len);
    }

    // Component of (a→b) along the unit direction u, and the perpendicular deviation.
    private static (double along, double perp) Project(Vec3 a, Vec3 b, (double e, double n) u)
    {
        double e = b.Easting - a.Easting, n = b.Northing - a.Northing;
        double along = e * u.e + n * u.n;
        double perp = Math.Abs(e * (-u.n) + n * u.e);
        return (along, perp);
    }

    private static List<(RouteSegment prev, RouteSegment turn, RouteSegment next)> InteriorTurns(RoutePlan plan)
    {
        var segs = plan.Segments;
        var result = new List<(RouteSegment, RouteSegment, RouteSegment)>();
        for (int i = 1; i < segs.Count - 1; i++)
            if (segs[i].Type == RouteSegmentType.Turn
                && segs[i - 1].Type == RouteSegmentType.Swath
                && segs[i + 1].Type == RouteSegmentType.Swath)
                result.Add((segs[i - 1], segs[i], segs[i + 1]));
        return result;
    }

    [Test]
    public void DeepHeadland_EachTurn_HasSymmetricAlignedLeadInAndOut()
    {
        var plan = NewPlanner().GenerateBoustrophedon(Field, Width, TurnR, HeadlandMargin,
            headingRad: Math.PI / 2, headlandPasses: 0, startPos: null);
        Assert.That(plan, Is.Not.Null);

        var turns = InteriorTurns(plan!);
        Assert.That(turns.Count, Is.GreaterThan(3), "several inter-pass turns expected");

        foreach (var (prev, turn, next) in turns)
        {
            var pts = turn.Points;
            Assert.That(pts.Count, Is.GreaterThan(3), "a lead-in turn has run-out + arc + run-in");

            // Connector is continuous with the passes it joins.
            Assert.That(Distance(pts[0], prev.Points[^1]), Is.LessThan(0.1),
                "turn starts at the previous pass exit");
            Assert.That(Distance(pts[^1], next.Points[0]), Is.LessThan(0.1),
                "turn ends at the next pass entry");

            // Run-OUT: first turn step continues the previous pass's EXIT heading,
            // straight, by ~L, going DEEPER into the headland (positive along-component).
            var exitDir = Unit(prev.Points[^2], prev.Points[^1]);
            var (outAlong, outPerp) = Project(pts[0], pts[1], exitDir);
            Assert.That(outAlong, Is.GreaterThanOrEqualTo(ExpectedLead - 0.5),
                "run-out drives straight past the pass end by ~L");
            Assert.That(outPerp, Is.LessThan(0.3), "run-out is aligned with the pass (no sideways drift)");

            // Run-IN: last turn step arrives along the next pass's ENTRY heading,
            // straight, by ~L, from DEEPER in the headland.
            var entryDir = Unit(next.Points[0], next.Points[1]);
            var (inAlong, inPerp) = Project(pts[^2], pts[^1], entryDir);
            Assert.That(inAlong, Is.GreaterThanOrEqualTo(ExpectedLead - 0.5),
                "run-in drives straight into the pass start by ~L");
            Assert.That(inPerp, Is.LessThan(0.3), "run-in is aligned with the pass (no sideways drift)");
        }
    }

    [Test]
    public void DeepHeadland_LeadInTurns_StayInsideOuterBoundary()
    {
        var plan = NewPlanner().GenerateBoustrophedon(Field, Width, TurnR, HeadlandMargin,
            headingRad: Math.PI / 2, headlandPasses: 0, startPos: null);
        Assert.That(plan, Is.Not.Null);

        foreach (var seg in plan!.Segments)
            foreach (var p in seg.Points)
            {
                Assert.That(p.Easting, Is.GreaterThanOrEqualTo(-0.05).And.LessThanOrEqualTo(200.05),
                    "pushed-out turns must stay inside the outer fence (E)");
                Assert.That(p.Northing, Is.GreaterThanOrEqualTo(-0.05).And.LessThanOrEqualTo(200.05),
                    "pushed-out turns must stay inside the outer fence (N)");
            }
    }

    [Test]
    public void ShallowHeadland_NoRoomForLeadIn_DegradesToPlainTurn()
    {
        // headlandMargin == turnRadius → L = max(0, min(4, 4-0-0-4)) = 0: no lead-in,
        // exactly today's behaviour. The interior turn is then just the arc; its first
        // step is NOT a straight run of length ~L aligned with the pass.
        var plan = NewPlanner().GenerateBoustrophedon(Field, Width, TurnR, TurnR,
            headingRad: Math.PI / 2, headlandPasses: 0, startPos: null);
        Assert.That(plan, Is.Not.Null);

        var turns = InteriorTurns(plan!);
        Assert.That(turns.Count, Is.GreaterThan(3));

        // At least one interior turn's opening step is shorter than the deep-headland
        // lead (i.e. no full straight run-out was inserted).
        bool anyWithoutLead = turns.Any(t =>
        {
            var exitDir = Unit(t.prev.Points[^2], t.prev.Points[^1]);
            var (along, _) = Project(t.turn.Points[0], t.turn.Points[1], exitDir);
            return along < ExpectedLead - 0.5;
        });
        Assert.That(anyWithoutLead, Is.True,
            "with no headland room the turn keeps its plain arc — no straight lead-in run");
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        double e = a.Easting - b.Easting, n = a.Northing - b.Northing;
        return Math.Sqrt(e * e + n * n);
    }
}

// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
//
// Licensed under GNU GPL v3. See LICENSE.md.

using System;
using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Guidance;
using AgOpenWeb.Models.RoutePlanning;
using AgOpenWeb.Models.Tool;
using AgOpenWeb.Services.Geometry;
using AgOpenWeb.Services.RoutePlanning;

namespace AgOpenWeb.Services.Tests.RoutePlanning;

/// <summary>
/// Planned connectors must keep the IMPLEMENT BODY — not just the tractor's
/// pivot path — clear of a hard fence. A mounted implement with its rear
/// corners metres behind the axle swings well outside the tractor's own arc;
/// the planner now runs the same ImplementSweptPath + TurnClearance check the
/// live U-turn uses, with the PHYSICAL frame width (not the spread).
/// </summary>
[TestFixture]
public class SweptPathClearanceTests
{
    // 200 × 100 m rectangle, tight 2-pass headland, passes east-west.
    private static readonly List<Vec2> Field = new()
    {
        new Vec2(0, 0), new Vec2(200, 0), new Vec2(200, 100), new Vec2(0, 100),
    };
    private static readonly Vec3 Start = new(-5, -5, 0);
    private const double Width = 6.0;
    private const double TurnR = 4.5;
    private const double Clearance = 0.5;

    // A long rear-mounted implement: 4.5 m of body behind a 1.8 m hitch.
    private static ToolGeometry LongMounted(double bodyWidth) => new(
        Mount: ToolMount.RearFixed, Width: bodyWidth, Offset: 0,
        VehicleHitchLength: 0, ToolHitchLength: 1.8, TrailingHitchLength: 0,
        TrailingToolToPivotLength: 0, TankTrailingHitchLength: 0, Length: 4.5);

    private static RoutePlan Plan(RoutePlanningService svc) =>
        svc.GenerateBoustrophedon(Field, Width, TurnR, 2 * Width,
            headingRad: Math.PI / 2, headlandPasses: 0, startPos: Start,
            boundaryClearance: Clearance)!;

    private static double WorstIntrusion(RoutePlan plan, ToolGeometry geom)
    {
        double worst = double.NegativeInfinity;
        int idx = 0;
        foreach (var seg in plan.Segments)
        {
            idx++;
            if (seg.Type != RouteSegmentType.Turn) continue;
            // Skip the degenerate approach→first-pass heading-alignment loop
            // (start == end): it is the route-start artifact, not a headland
            // turn, and the operator usually drives to the start by hand.
            if (GeometryMath.Distance(seg.Points[0], seg.Points[^1]) < 0.5) continue;
            var swept = ImplementSweptPath.Compute(seg.Points, geom);
            var r = TurnClearance.Evaluate(swept, Field, TurnClearance.KeepSide.Inside, Clearance);
            if (r.MaxIntrusion > 0)
                TestContext.Out.WriteLine($"  seg {idx} ({seg.Points.Count} pts) from ({seg.Points[0].Easting:F1},{seg.Points[0].Northing:F1}) to ({seg.Points[^1].Easting:F1},{seg.Points[^1].Northing:F1}): intrusion {r.MaxIntrusion:F2}");
            worst = Math.Max(worst, r.MaxIntrusion);
        }
        return worst;
    }

    [Test]
    public void HardFence_LongMountedImplement_TurnsKeepBodyInside()
    {
        var geom = LongMounted(2.8);

        // Baseline (old behaviour): tractor-path-only validation lets the
        // implement's rear corners swing past the fence margin somewhere.
        var blind = new RoutePlanningService(new PolygonOffsetService());
        double blindWorst = WorstIntrusion(Plan(blind), geom);

        // Swept-aware planner: every turn's body footprint holds the margin.
        var swept = new RoutePlanningService(new PolygonOffsetService())
        { SweptToolGeometry = geom, OuterBoundaryIsHard = true };
        var plan = Plan(swept);
        Assert.That(plan, Is.Not.Null);
        double sweptWorst = WorstIntrusion(plan, geom);

        TestContext.Out.WriteLine($"worst intrusion: blind={blindWorst:F2} m, swept-aware={sweptWorst:F2} m");
        Assert.That(sweptWorst, Is.LessThanOrEqualTo(0.05),
            "swept-aware turns must keep the implement body inside the hard fence");
        // The check must actually bite. Without swept awareness the long body swings well
        // past the fence margin; the swept-aware planner pulls it far inside. (The turn
        // lead-in feature reshapes the blind arc, so assert the IMPROVEMENT the swept check
        // delivers — robust to the exact blind geometry — plus that blind clearly intrudes.)
        Assert.That(blindWorst, Is.GreaterThan(1.5),
            "the blind planner swings the body well past the margin");
        Assert.That(blindWorst - sweptWorst, Is.GreaterThan(2.0),
            "the swept-aware planner keeps the body far clearer than the blind one");
    }

    [Test]
    public void SoftFence_SweptBodyNotEnforced()
    {
        // Same geometry, boundary NOT hard: tractor-path rule only (matches the
        // live turn's hardOuter gate), so the plan is the old one.
        var geom = LongMounted(2.8);
        var soft = new RoutePlanningService(new PolygonOffsetService())
        { SweptToolGeometry = geom, OuterBoundaryIsHard = false };
        var blind = new RoutePlanningService(new PolygonOffsetService());
        var a = Plan(soft); var b = Plan(blind);
        double la = a.Segments.Sum(s => PathLen(s.Points)), lb = b.Segments.Sum(s => PathLen(s.Points));
        Assert.That(la, Is.EqualTo(lb).Within(1e-6), "soft fence: swept check must not change the plan");
    }

    [Test]
    public void PhysicalWidth_NotSpreadWidth_DrivesTheCheck()
    {
        // A spreader: 15 m spread, 2.8 m of steel. The swept body uses the
        // physical width the VM supplies — a 15 m-wide body would be far more
        // constrained (or unplannable) than the real frame.
        var narrow = new RoutePlanningService(new PolygonOffsetService())
        { SweptToolGeometry = LongMounted(2.8), OuterBoundaryIsHard = true };
        var wide = new RoutePlanningService(new PolygonOffsetService())
        { SweptToolGeometry = LongMounted(15.0), OuterBoundaryIsHard = true };
        double ln = Plan(narrow).Segments.Sum(s => PathLen(s.Points));
        double lw = Plan(wide).Segments.Sum(s => PathLen(s.Points));
        Assert.That(lw, Is.GreaterThanOrEqualTo(ln),
            "a wider body can only force longer (or equal) connectors, never shorter");
    }

    private static double PathLen(IReadOnlyList<Vec3> pts)
    {
        double d = 0;
        for (int i = 1; i < pts.Count; i++) d += GeometryMath.Distance(pts[i - 1], pts[i]);
        return d;
    }
}

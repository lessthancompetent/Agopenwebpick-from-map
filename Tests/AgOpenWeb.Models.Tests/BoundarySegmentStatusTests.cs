using System;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Tests;

// Regression for the section-control boundary gate failing OPEN on non-axis-aligned
// boundaries: GetSegmentBoundaryStatus had a fast path keyed on distance from the
// axis-aligned BOUNDING BOX, so a section deep inside the bbox but sitting on a
// DIAGONAL / concave fence edge was reported "fully inside" and the sprayer painted
// straight past the fence.
[TestFixture]
public class BoundarySegmentStatusTests
{
    // 45°-rotated square: vertices (±150,0),(0,±150). Every edge is a diagonal,
    // and the axis-aligned bbox is E[-150,150] N[-150,150].
    private static BoundaryPolygon Diamond()
    {
        var poly = new BoundaryPolygon();
        poly.Points.Add(new BoundaryPoint(150, 0, 0));
        poly.Points.Add(new BoundaryPoint(0, 150, 0));
        poly.Points.Add(new BoundaryPoint(-150, 0, 0));
        poly.Points.Add(new BoundaryPoint(0, -150, 0));
        poly.UpdateBounds();
        return poly;
    }

    [Test]
    public void SwathStraddlingDiagonalEdge_ReadsPartlyOutside_NotFailOpen()
    {
        var poly = Diamond();
        Assert.That(poly.IsValid, Is.True,
            "4-point diamond is a valid polygon — this is NOT the invalid-boundary path");

        // Centre just inside the top-right edge (E+N=148 vs the fence at 150), 74 m
        // from every bbox edge — well past the 50 m deep-inside margin, so the old
        // bbox fast path fired here. Swath runs along the outward normal (1,1), so it
        // straddles the fence: the inner ~2/3 is inside, the outer part is outside.
        var center = new Vec2(74, 74);
        double heading = -Math.PI / 4;   // swath perpendicular = the (1,1) normal
        double halfWidth = 4.0;

        var res = poly.GetSegmentBoundaryStatus(center, heading, halfWidth);

        Assert.That(res.InsidePercent, Is.LessThan(0.95),
            $"a swath straddling the diagonal fence must read partly-outside (was fail-open at 1.0), got {res.InsidePercent:F3}");
        Assert.That(res.InsidePercent, Is.GreaterThan(0.30),
            $"the crossing test must actually run (a genuine partial reading, not degenerate), got {res.InsidePercent:F3}");
    }

    [Test]
    public void DeepInterior_StillFullyInside()
    {
        // The corrected fast path must not over-correct: a point genuinely far from
        // every edge (origin is ~106 m from the nearest diagonal) stays fully inside.
        var poly = Diamond();
        var res = poly.GetSegmentBoundaryStatus(new Vec2(0, 0), 0.0, 4.0);
        Assert.That(res.InsidePercent, Is.EqualTo(1.0).Within(1e-6));
    }

    [Test]
    public void FarOutside_FullyOutside()
    {
        var poly = Diamond();
        // (200,200) is outside the diamond and far from every edge.
        var res = poly.GetSegmentBoundaryStatus(new Vec2(200, 200), 0.0, 4.0);
        Assert.That(res.InsidePercent, Is.LessThan(0.05));
    }
}

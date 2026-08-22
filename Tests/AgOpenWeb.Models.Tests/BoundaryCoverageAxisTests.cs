using System.Collections.Generic;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Tests;

// Two-axis boundary model: StopCoverageAtEdge (coverage axis) is independent of IsHard
// (physical axis), and section-level coverage gating honours it per boundary.
[TestFixture]
public class BoundaryCoverageAxisTests
{
    private static BoundaryPolygon RectPoly(double e0, double e1, double n0, double n1)
    {
        var p = new BoundaryPolygon
        {
            Points = new List<BoundaryPoint>
            {
                new(e0, n0, 0), new(e1, n0, 0), new(e1, n1, 0), new(e0, n1, 0)
            }
        };
        p.UpdateBounds();
        return p;
    }

    private static Boundary Field() => new() { OuterBoundary = RectPoly(0, 200, 0, 100) };

    [Test]
    public void StopCoverageAtEdge_DefaultsTrue_IndependentOfIsHard()
    {
        var p = new BoundaryPolygon();
        Assert.That(p.StopCoverageAtEdge, Is.True);
        Assert.That(p.IsHard, Is.False);
        p.IsHard = true; // flip the physical axis
        Assert.That(p.StopCoverageAtEdge, Is.True, "physical axis must not touch the coverage axis");
    }

    [Test]
    public void OuterEdge_StopCoverageOn_GatesCoverage_Off_LetsSpreadBeyond()
    {
        var b = Field();
        var center = new Vec2(200, 50);   // on the right fence; swath E[195,205] straddles it
        const double heading = 0.0, halfWidth = 5.0;

        b.OuterBoundary!.StopCoverageAtEdge = true;
        Assert.That(b.GetSegmentBoundaryStatus(center, heading, halfWidth).InsidePercent,
            Is.LessThan(0.95), "coverage stops at the edge when StopCoverageAtEdge is on");

        b.OuterBoundary!.StopCoverageAtEdge = false; // broadcast beyond the line
        Assert.That(b.GetSegmentBoundaryStatus(center, heading, halfWidth).InsidePercent,
            Is.EqualTo(1.0).Within(1e-6), "coverage extends beyond when StopCoverageAtEdge is off");
    }

    // Four-corner matrix {StopCoverageAtEdge, IsHard} on the outer edge: the COVERAGE
    // result depends ONLY on the coverage axis. IsHard is the physical axis (live-turn
    // enforcement is bug E) and must not change what section-control coverage sees here.
    [TestCase(true, true)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(false, false)]
    public void OuterEdge_FourCorners_CoverageDependsOnlyOnStopCoverageAxis(bool stopCoverage, bool isHard)
    {
        var b = Field();
        b.OuterBoundary!.StopCoverageAtEdge = stopCoverage;
        b.OuterBoundary!.IsHard = isHard;
        var center = new Vec2(200, 50);   // straddling the right edge
        var inside = b.GetSegmentBoundaryStatus(center, 0.0, 5.0).InsidePercent;

        if (stopCoverage)
            Assert.That(inside, Is.LessThan(0.95),
                "coverage stops at the edge whenever StopCoverageAtEdge is on — IsHard is irrelevant to coverage");
        else
            Assert.That(inside, Is.EqualTo(1.0).Within(1e-6),
                "coverage paints beyond whenever StopCoverageAtEdge is off — IsHard is irrelevant to coverage");
    }

    [Test]
    public void InnerHole_StopCoverageOn_ExcludesCoverage_Off_SpraysThrough()
    {
        var b = Field();
        var hole = RectPoly(90, 110, 40, 60);   // exclusion around (100,50)
        b.InnerBoundaries.Add(hole);
        var center = new Vec2(100, 50);          // swath E[97,103] entirely inside the hole
        const double heading = 0.0, halfWidth = 3.0;

        hole.StopCoverageAtEdge = true;          // default: covered ground excluded over the hole
        Assert.That(b.GetSegmentBoundaryStatus(center, heading, halfWidth).InsidePercent,
            Is.LessThan(0.5), "coverage excluded over a coverage-stopping hole");

        hole.StopCoverageAtEdge = false;         // spray through this hole
        Assert.That(b.GetSegmentBoundaryStatus(center, heading, halfWidth).InsidePercent,
            Is.GreaterThan(0.9), "coverage sprayed through a coverage-off hole");
    }
}

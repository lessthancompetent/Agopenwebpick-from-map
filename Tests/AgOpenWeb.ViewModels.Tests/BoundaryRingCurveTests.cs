using System;
using System.Linq;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The Field Builder's whole-ring "Boundary Curve" (remap P1.4) is the route planner's
/// headland lap brought across (RoutePlanner.BuildLapRing): half-tool inset, corners
/// rounded to the tractor's turning radius, closed, one track per ring (outer + each
/// obstacle), AOG mode 32 (BoundaryCurve), refused while one exists, saved at once.
/// The old command insetted the raw ring with SHARP corners (not drivable) on the outer
/// ring only and only persisted on field close.
/// </summary>
[TestFixture]
public class BoundaryRingCurveTests
{
    private static readonly (double e, double n)[] OuterSquare = { (0, 0), (0, 100), (100, 100), (100, 0) };
    private static readonly (double e, double n)[] InnerSquare = { (40, 40), (40, 60), (60, 60), (60, 40) };

    private static BoundaryPolygon Ring((double e, double n)[] verts)
    {
        var poly = new BoundaryPolygon();
        foreach (var (e, n) in verts) poly.Points.Add(new BoundaryPoint(e, n, 0));
        poly.UpdateBounds();
        return poly;
    }

    private static MainViewModel BuildVm(bool withHole = false)
    {
        var vm = new MainViewModelBuilder().Build();
        var bnd = new Boundary { OuterBoundary = Ring(OuterSquare) };
        if (withHole) bnd.InnerBoundaries.Add(Ring(InnerSquare));
        vm.State.Field.CurrentBoundary = bnd;
        return vm;
    }

    private static double HalfTool() => ConfigurationStore.Instance.ActualToolWidth / 2.0;

    [Test]
    public void OuterRing_IsClosed_Inset_AndHasRoundedCorners()
    {
        var vm = BuildVm();
        vm.CreateCurveFromBoundaryCommand!.Execute(null);

        var t = vm.SavedTracks.Single(x => x.Type == TrackType.BoundaryCurve);
        Assert.That(t.Name, Is.EqualTo("Boundary Curve"));
        Assert.That(t.IsClosed, Is.True);
        var p = t.Points;
        Assert.That(p.Count, Is.GreaterThan(5), "a rounded ring has more than the 4+1 raw vertices");
        Assert.That(p[0].Easting, Is.EqualTo(p[^1].Easting).Within(1e-6), "closed: last point == first");
        Assert.That(p[0].Northing, Is.EqualTo(p[^1].Northing).Within(1e-6));

        // Inset: every point sits at least halfTool inside the 100×100 fence (corner
        // rounding only pulls points further in, never out).
        double h = HalfTool();
        foreach (var q in p)
        {
            Assert.That(q.Easting, Is.GreaterThanOrEqualTo(h - 0.05).And.LessThanOrEqualTo(100 - h + 0.05));
            Assert.That(q.Northing, Is.GreaterThanOrEqualTo(h - 0.05).And.LessThanOrEqualTo(100 - h + 0.05));
        }
        // Rounded corners: no point sits ON the sharp inset corner (h, h) — the planner's
        // RoundCorners replaces it with an arc.
        bool sharpCorner = p.Any(q => Math.Abs(q.Easting - h) < 0.05 && Math.Abs(q.Northing - h) < 0.05);
        Assert.That(sharpCorner, Is.False, "corners are rounded to the turning radius, not sharp");
        Assert.That(vm.SelectedTrack, Is.EqualTo(t), "the new ring is selected");
    }

    [Test]
    public void WithAHole_MakesOneRingPerBoundary_InnerOffsetAwayFromTheHole()
    {
        var vm = BuildVm(withHole: true);
        vm.CreateCurveFromBoundaryCommand!.Execute(null);

        var rings = vm.SavedTracks.Where(x => x.Type == TrackType.BoundaryCurve).ToList();
        Assert.That(rings.Count, Is.EqualTo(2), "outer + one inner");
        var inner = rings.Single(r => r.Name == "Inner Boundary Curve 1");
        double h = HalfTool();
        // The hole is 40..60; its ring must sit OUTSIDE the hole by ~halfTool (away from it).
        foreach (var q in inner.Points)
        {
            bool insideHole = q.Easting > 40 + 0.05 && q.Easting < 60 - 0.05 && q.Northing > 40 + 0.05 && q.Northing < 60 - 0.05;
            Assert.That(insideHole, Is.False, "inner ring is offset AWAY from the hole, never into it");
        }
        Assert.That(inner.Points.Any(q => q.Easting < 40 - h + 0.5 || q.Easting > 60 + h - 0.5), Is.True,
            "inner ring reaches ~halfTool outside the hole");
    }

    [Test]
    public void SecondBuild_IsRefusedWhileRingsExist()
    {
        var vm = BuildVm();
        vm.CreateCurveFromBoundaryCommand!.Execute(null);
        int before = vm.SavedTracks.Count;

        vm.CreateCurveFromBoundaryCommand!.Execute(null);

        Assert.That(vm.SavedTracks.Count, Is.EqualTo(before), "AOG greys the button while a boundary curve exists");
        Assert.That(vm.StatusMessage, Does.Contain("already exist"));
    }

    [Test]
    public void NoBoundary_IsRefused()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.CreateCurveFromBoundaryCommand!.Execute(null);
        Assert.That(vm.SavedTracks.Any(x => x.Type == TrackType.BoundaryCurve), Is.False);
    }
}

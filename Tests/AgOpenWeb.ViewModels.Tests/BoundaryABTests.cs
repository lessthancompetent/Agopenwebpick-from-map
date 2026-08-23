using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// "Bnd. AB" two-tap straight line: RemoteCreateBoundaryAB. Mirrors AgOpenGPS 6.8.6
/// FormABDraw.BtnMakeABLine_Click — tap A snaps to the closest point on the closest EDGE of ANY
/// ring, tap B to the closest edge point of that same ring, the index-ordering rule decides
/// which point is A, heading = atan2(B − A). Placement: the line is shifted (w−o)/2 toward the
/// field interior so pass 0 puts the tool EDGE on the fence (AOG's swath-edge convention,
/// CABLine.cs:118/140-142); the shifted points are the FIXED anchors, and the persisted 2-point
/// line is the anchors pushed out by the tails (fence crossing + 20 m, capped 99 m).
/// The regression taps sit just OUTSIDE the corners (on the diagonal), where the edge projection
/// clamps to the corner itself, so the results match the vertex-snapped version. Mid-side taps
/// (the edge-snap case proper) are covered in BoundaryTailTests.
/// </summary>
[TestFixture]
public class BoundaryABTests
{
    // Outer square, vertices walked clockwise (E,N): P0 (0,0) → P1 (0,100) → P2 (100,100) → P3 (100,0).
    private static readonly (double e, double n)[] OuterSquare = { (0, 0), (0, 100), (100, 100), (100, 0) };
    // Inner square hole: Q0 (40,40) → Q1 (40,60) → Q2 (60,60) → Q3 (60,40).
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

    /// <summary>(w − o)/2 from the live (singleton) config, read — not set — so the tests don't
    /// leak tool settings into other fixtures.</summary>
    private static double HalfSwath()
    {
        var cfg = ConfigurationStore.Instance;
        return Math.Max(0, (cfg.ActualToolWidth - cfg.Tool.Overlap) * 0.5);
    }

    private static double Wrap2Pi(double rad) => (rad % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);

    // Taps near P1 (0,100) then P2 (100,100): on the dense ring (64 vertices per side) P1 is
    // index 64 and P2 index 128, |64−128| ≤ n/2 → swap so start > end (start=P2, end=P1).
    // A = P2, B = P1, heading = atan2(0−100, 100−100) = −π/2 → 270° (west).
    [Test]
    public void TwoTaps_SnapToFenceVertices_AndBuildABWithAogOrderingAndHeading()
    {
        var vm = BuildVm();

        vm.RemoteCreateBoundaryAB(-2, 102, 102, 102);

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        var track = vm.SavedTracks[0];
        Assert.That(track.Type, Is.EqualTo(TrackType.ABLine));
        Assert.That(track.Points, Has.Count.EqualTo(2));
        Assert.That(track.Name, Is.EqualTo("AB 270°"));
        Assert.That(track.NoPassOffset, Is.False, "normal parallel passes, unlike the boundary curve");
        Assert.That(vm.SelectedTrack, Is.SameAs(track));
        Assert.That(vm.StatusMessage, Is.EqualTo("Created boundary AB 270° (outer ring)"));

        double expected = Wrap2Pi(Math.Atan2(0 - 100, 100 - 100)); // fence[end] − fence[start] after the rule
        Assert.That(Wrap2Pi(track.Heading), Is.EqualTo(expected).Within(1e-9));

        // Placement: the top edge is N = 100; the interior (centroid 50,50) is south of it, so the
        // line sits (w−o)/2 south of the fence — tool edge on the fence under centre guidance.
        double lineN = 100 - HalfSwath();
        Assert.That(track.Points[0].Northing, Is.EqualTo(lineN).Within(1e-6));
        Assert.That(track.Points[1].Northing, Is.EqualTo(lineN).Within(1e-6));
        // Extended past the fence: A (east end, from P2) beyond E=100+20, B (west end) before E=0−20.
        Assert.That(track.Points[0].Easting, Is.GreaterThanOrEqualTo(100 + 20 - 1e-6));
        Assert.That(track.Points[1].Easting, Is.LessThanOrEqualTo(0 - 20 + 1e-6));
        // Anchors = the shifted fence points themselves; the tips are the anchors ± the tails.
        Assert.That(track.HasAnchors, Is.True);
        Assert.That(track.AnchorA!.Value.Easting, Is.EqualTo(100).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(track.AnchorA!.Value.Northing, Is.EqualTo(lineN).Within(1e-6));
        Assert.That(track.Points[0].Easting, Is.EqualTo(100 + track.TailA).Within(1e-9));
        Assert.That(track.Points[1].Easting, Is.EqualTo(0 - track.TailB).Within(1e-9));
    }

    // Same two vertices tapped in the opposite order give the SAME line: the index-ordering
    // rule (not tap order) decides which vertex is A.
    [Test]
    public void OppositeTapOrder_GivesTheSameLine()
    {
        var vm1 = BuildVm();
        vm1.RemoteCreateBoundaryAB(-2, 102, 102, 102);  // P1 then P2
        var vm2 = BuildVm();
        vm2.RemoteCreateBoundaryAB(102, 102, -2, 102);  // P2 then P1

        var t1 = vm1.SavedTracks[0];
        var t2 = vm2.SavedTracks[0];
        Assert.That(t2.Name, Is.EqualTo(t1.Name));
        Assert.That(Wrap2Pi(t2.Heading), Is.EqualTo(Wrap2Pi(t1.Heading)).Within(1e-9));
        for (int i = 0; i < 2; i++)
        {
            Assert.That(t2.Points[i].Easting, Is.EqualTo(t1.Points[i].Easting).Within(1e-6));
            Assert.That(t2.Points[i].Northing, Is.EqualTo(t1.Points[i].Northing).Within(1e-6));
        }
    }

    // A tap nearest an INNER ring picks that ring; B then snaps to the same ring. Along the top
    // of the hole (N = 60) the line shifts AWAY from the hole's centroid, i.e. north.
    [Test]
    public void TapNearestInnerRing_SnapsToThatRing_AndShiftsAwayFromTheHole()
    {
        var vm = BuildVm(withHole: true);

        vm.RemoteCreateBoundaryAB(39.8, 60.2, 60.2, 60.2); // Q1 (40,60) then Q2 (60,60), just outside the hole

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        var track = vm.SavedTracks[0];
        // Inner ring (400 m² → 0.55 m spacing, 20 m sides halve to 0.625 m = 32 vertices per
        // side): Q1 = index 32, Q2 = 64, |32−64| ≤ n/2 → swap → A = Q2 (60,60), B = Q1 (40,60):
        // heading west = 270°.
        Assert.That(track.Name, Is.EqualTo("AB 270°"));
        Assert.That(vm.StatusMessage, Is.EqualTo("Created boundary AB 270° (inner 1 ring)"));
        Assert.That(Wrap2Pi(track.Heading), Is.EqualTo(1.5 * Math.PI).Within(1e-9));

        double lineN = 60 + HalfSwath();
        Assert.That(track.Points[0].Northing, Is.EqualTo(lineN).Within(1e-6));
        Assert.That(track.Points[1].Northing, Is.EqualTo(lineN).Within(1e-6));
        // Still extended out past the OUTER fence on both ends.
        Assert.That(track.Points[0].Easting, Is.GreaterThanOrEqualTo(100 + 20 - 1e-6));
        Assert.That(track.Points[1].Easting, Is.LessThanOrEqualTo(0 - 20 + 1e-6));
    }

    // Two taps that snap to the same vertex are refused (AOG would build a degenerate north
    // line through one vertex; we tell the operator instead).
    [Test]
    public void SameVertexTwice_IsRefusedWithStatus()
    {
        var vm = BuildVm();

        vm.RemoteCreateBoundaryAB(-1, -1, -2, -1);     // both outside the corner, nearest P0 (0,0)

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.SelectedTrack, Is.Null);
        Assert.That(vm.StatusMessage, Is.EqualTo("Pick two different points on the boundary"));
    }

    [Test]
    public void NoBoundary_IsRefused()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.State.Field.CurrentBoundary = null;

        vm.RemoteCreateBoundaryAB(0, 0, 10, 10);

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.StatusMessage, Is.EqualTo("Load a field with a boundary first"));
    }
}

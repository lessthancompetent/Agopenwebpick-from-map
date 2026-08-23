using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Boundary-segment curve ("Bnd. Curve" two-tap) creation + the A++/A−−/B++/B−− tail buttons:
/// RemoteCreateBoundaryCurveSegment / RemoteAdjustTail. Uses a circular boundary so the
/// on-boundary arc portion of the track (the fixed BODY between the anchors) is identifiable
/// by radius — the straight tangent tails leave the circle — which makes "the body never
/// changes, only the tails do" directly measurable.
/// P1.1/P1.3: the ring is first normalised by FixSpacing (1.1 m rule → the 0.87 m circle is
/// thinned to 1.745 m), the walk is fence-winding from anchor A to anchor B, the body is
/// midpoint-densified to ≤ 1.6 m, and the track is named "Cu {deg}°" (AOG FormABDraw.cs:505)
/// instead of the old fixed "Boundary Curve" — hence the name assertions below check the AOG
/// pattern.
/// </summary>
[TestFixture]
public class BoundaryCurveSegmentTests
{
    private const double Radius = 50.0;
    private const int RingPoints = 360; // vertex spacing ≈ 0.87 m

    private static MainViewModel BuildVmWithCircleBoundary()
    {
        // The builder's mocked IPolygonOffsetService returns null from CreateInwardOffset,
        // so the VM falls back to the raw boundary ring — the curve sits ON the circle.
        var vm = new MainViewModelBuilder().Build();
        var pts = new List<BoundaryPoint>(RingPoints);
        for (int i = 0; i < RingPoints; i++)
        {
            double t = i * 2.0 * Math.PI / RingPoints;
            pts.Add(new BoundaryPoint(Radius * Math.Sin(t), Radius * Math.Cos(t), 0));
        }
        var bnd = new Boundary { OuterBoundary = new BoundaryPolygon { Points = pts } };
        bnd.OuterBoundary.UpdateBounds();
        vm.State.Field.CurrentBoundary = bnd;
        return vm;
    }

    /// <summary>Polyline length of the track portion lying on the boundary circle
    /// (the tangent extensions past the fence fall outside the radius band).</summary>
    private static double ArcBandLength(IEnumerable<Vec3> points)
    {
        double len = 0;
        Vec3? prev = null;
        foreach (var p in points)
        {
            double r = Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing);
            bool inBand = Math.Abs(r - Radius) < 0.25;
            if (inBand && prev is { } q)
            {
                double dx = p.Easting - q.Easting, dy = p.Northing - q.Northing;
                len += Math.Sqrt(dx * dx + dy * dy);
            }
            prev = inBand ? p : null;
        }
        return len;
    }

    [Test]
    public void Create_WalksShorterArcBetweenTaps()
    {
        var vm = BuildVmWithCircleBoundary();

        // Top of the circle → east: quarter arc ≈ 78.5 m (shorter than the 3/4 way round).
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        var track = vm.SavedTracks[0];
        Assert.That(track.Name, Does.StartWith("Cu ").And.EndWith("°"));
        // Top → east: headings sweep 90° → 180°, circular mean ≈ 135°.
        Assert.That(double.Parse(track.Name[3..^1], System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(135).Within(2));
        Assert.That(vm.SelectedTrack, Is.SameAs(track));
        // Quarter arc, not the 3/4 complement (the near-tip extension points hug the
        // circle, so the band reads a few metres long — hence the loose tolerance).
        Assert.That(ArcBandLength(track.Points), Is.EqualTo(Math.PI * Radius / 2).Within(12.0));
    }

    // A++ / B++ grow ONLY the straight tail past the anchor: the on-circle body length is
    // unchanged, the point count grows by exactly 10 (1 m tail points), the anchors stay put.
    [Test]
    public void TailPlus_GrowsOnlyTheTail_BodyUnchanged()
    {
        var vm = BuildVmWithCircleBoundary();
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        var track = vm.SavedTracks[0];
        string name = track.Name;
        double bodyBefore = ArcBandLength(track.Body!);
        int countBefore = track.Points.Count;
        var anchorA = track.AnchorA!.Value;
        var anchorB = track.AnchorB!.Value;

        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(vm.SavedTracks[0], Is.SameAs(track));
        Assert.That(track.Name, Is.EqualTo(name), "name is not recomputed on a tail change (AOG's A++/B++ don't either)");
        Assert.That(track.Points, Has.Count.EqualTo(countBefore + 10));
        Assert.That(ArcBandLength(track.Body!), Is.EqualTo(bodyBefore).Within(1e-9), "the body is untouched");
        Assert.That(track.AnchorA!.Value, Is.EqualTo(anchorA));
        Assert.That(track.AnchorB!.Value, Is.EqualTo(anchorB));
        // The anchors are still track points, and the tail tip is tailA metres straight behind A.
        Assert.That(track.Points.Any(p => Math.Abs(p.Easting - anchorA.Easting) < 1e-9 && Math.Abs(p.Northing - anchorA.Northing) < 1e-9), Is.True);
        double tipDist = Math.Sqrt(Math.Pow(track.Points[0].Easting - anchorA.Easting, 2) + Math.Pow(track.Points[0].Northing - anchorA.Northing, 2));
        Assert.That(tipDist, Is.EqualTo(track.TailA).Within(1e-9));

        vm.RemoteAdjustTail(isA: false, deltaMeters: 10);
        Assert.That(track.Points, Has.Count.EqualTo(countBefore + 20));
        Assert.That(ArcBandLength(track.Body!), Is.EqualTo(bodyBefore).Within(1e-9));
    }

    // A−− / B−− shrink the tail, clamp at 0 (the line then ends exactly at the anchor) and are a
    // quiet no-op once there: no rebuild, just a status.
    [Test]
    public void TailMinus_ClampsAtTheAnchor_ThenIsANoOp()
    {
        var vm = BuildVmWithCircleBoundary();
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        var track = vm.SavedTracks[0];
        double bodyBefore = ArcBandLength(track.Body!);
        var anchorB = track.AnchorB!.Value;

        for (int i = 0; i < 20 && track.TailB > 0; i++) vm.RemoteAdjustTail(isA: false, deltaMeters: -10);

        Assert.That(track.TailB, Is.EqualTo(0));
        Assert.That(vm.StatusMessage, Is.EqualTo("B end now at point B"));
        Assert.That(track.Points[^1].Easting, Is.EqualTo(anchorB.Easting).Within(1e-9));
        Assert.That(track.Points[^1].Northing, Is.EqualTo(anchorB.Northing).Within(1e-9));
        Assert.That(ArcBandLength(track.Body!), Is.EqualTo(bodyBefore).Within(1e-9), "shortening never eats the body");
        var pointsAtZero = track.Points;

        vm.RemoteAdjustTail(isA: false, deltaMeters: -10);

        Assert.That(track.Points, Is.SameAs(pointsAtZero), "no rebuild at zero tail");
        Assert.That(vm.StatusMessage, Is.EqualTo("B end already at point B"));
        Assert.That(track.TailB, Is.EqualTo(0));
    }

    [Test]
    public void Tail_IsRefusedWhenTheTrackWasDeleted()
    {
        var vm = BuildVmWithCircleBoundary();
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        vm.SavedTracks.Clear(); // track deleted (or another field opened — same reload path)
        vm.SelectedTrack = null;

        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.StatusMessage, Is.EqualTo("Select a boundary AB or curve"));
    }

    [Test]
    public void Tail_IsRefusedBeforeAnyBoundaryLineExists()
    {
        var vm = BuildVmWithCircleBoundary();

        vm.RemoteAdjustTail(isA: false, deltaMeters: 10);

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.StatusMessage, Is.EqualTo("Select a boundary AB or curve"));
    }
}

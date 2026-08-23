using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// Boundary-segment curve ("Bnd. Curve" two-tap) creation + the A++/A−−/B++/B−− extend
/// buttons: RemoteCreateBoundaryCurveSegment / RemoteBoundarySegExtend. Uses a circular
/// boundary so the on-boundary arc portion of the track is identifiable by radius (the
/// past-fence tangent extensions leave the circle), making the ±5 m growth measurable.
/// P1.1/P1.3: the ring is first normalised by FixSpacing (1.1 m rule → the 0.87 m circle is
/// thinned to 1.745 m), the walk is fence-winding and half-open, the body is midpoint-densified
/// to ≤ 1.6 m, and the track is named "Cu {deg}°" (AOG FormABDraw.cs:505) instead of the old
/// fixed "Boundary Curve" — hence the name assertions below check the AOG pattern.
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

    [Test]
    public void Extend_GrowsSameTrackByAboutFiveMeters()
    {
        var vm = BuildVmWithCircleBoundary();
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        var track = vm.SavedTracks[0];
        string name = track.Name;
        double before = ArcBandLength(track.Points);

        vm.RemoteBoundarySegExtend("A", 1);

        // Same track, same name — rebuilt in place, ~5 m longer along the boundary
        // (walks whole ring vertices, so a step lands just under the 5 m budget).
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(vm.SavedTracks[0], Is.SameAs(track));
        Assert.That(track.Name, Is.EqualTo(name));
        Assert.That(ArcBandLength(track.Points) - before, Is.EqualTo(5.0).Within(2.0));

        double afterA = ArcBandLength(track.Points);
        vm.RemoteBoundarySegExtend("B", 1);
        Assert.That(ArcBandLength(track.Points) - afterA, Is.EqualTo(5.0).Within(2.0));
    }

    [Test]
    public void Shorten_ClampsAtMinimumCurveLength()
    {
        var vm = BuildVmWithCircleBoundary();
        // ~9.6 m arc from the top of the circle.
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius * Math.Sin(0.2), Radius * Math.Cos(0.2));
        var track = vm.SavedTracks[0];

        vm.RemoteBoundarySegExtend("B", -1); // ~9.6 → ~5.2 m
        vm.RemoteBoundarySegExtend("B", -1); // ~5.2 → ~2.6 m (budget-clamped above 2 m)
        var pointsAtMin = track.Points;

        vm.RemoteBoundarySegExtend("B", -1); // would drop below ~2 m → refused

        Assert.That(track.Points, Is.SameAs(pointsAtMin), "clamped step must not rebuild the track");
        Assert.That(vm.StatusMessage, Is.EqualTo("Boundary curve at minimum length"));
        Assert.That(ArcBandLength(track.Points), Is.GreaterThan(1.5));
    }

    [Test]
    public void Extend_IsNoOpWhenTrackWasDeleted()
    {
        var vm = BuildVmWithCircleBoundary();
        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        vm.SavedTracks.Clear(); // track deleted (or another field opened — same reload path)

        vm.RemoteBoundarySegExtend("A", 1);

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.StatusMessage, Does.StartWith("Create a boundary curve first"));
    }

    [Test]
    public void Extend_IsNoOpBeforeAnyCurveExists()
    {
        var vm = BuildVmWithCircleBoundary();

        vm.RemoteBoundarySegExtend("B", 1);

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.StatusMessage, Does.StartWith("Create a boundary curve first"));
    }
}

using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// AgOpenGPS "A++" / "B++" (6.8.6 FormABDraw.cs btnALength_Click :845-860 / btnBLength_Click
/// :862-876) on the SELECTED track: RemoteExtendTrackEnd. A straight run-out of 49 points at
/// 1 m spacing along the end point's stored heading — A end inserted BEFORE Points[0] (ordered
/// 49 m … 1 m behind), B end appended AFTER the original last point. AB lines (2 points) are
/// refused, as AOG greys the buttons unless mode == Curve (:833-842). Also covers Cancel in the
/// Bnd. Curve trim phase (RemoteBoundarySegCancel, P2.4).
/// </summary>
[TestFixture]
public class TrackExtendEndTests
{
    private const double HeadingA = Math.PI / 2;   // P0 points east
    private const double HeadingB = Math.PI / 4;   // P2 points north-east

    /// <summary>3-point curve P0 (0,0) → P1 (10,0) → P2 (20,5) with explicit per-point headings.</summary>
    private static Track ThreePointCurve() => new()
    {
        Name = "Cu test",
        Type = TrackType.Curve,
        IsVisible = true,
        IsClosed = false,
        Points = new List<Vec3>
        {
            new(0, 0, HeadingA),
            new(10, 0, Math.PI / 2),
            new(20, 5, HeadingB),
        }
    };

    private static MainViewModel BuildVmWith(Track track)
    {
        var vm = new MainViewModelBuilder().Build();
        vm.SavedTracks.Add(track);
        vm.SelectedTrack = track;
        return vm;
    }

    private static void AssertSamePoint(Vec3 actual, Vec3 expected)
    {
        Assert.That(actual.Easting, Is.EqualTo(expected.Easting).Within(1e-9));
        Assert.That(actual.Northing, Is.EqualTo(expected.Northing).Within(1e-9));
        Assert.That(actual.Heading, Is.EqualTo(expected.Heading).Within(1e-12));
    }

    [Test]
    public void ExtendA_Inserts49PointsBeforeP0_AlongP0Heading_OriginalsUntouched()
    {
        var track = ThreePointCurve();
        var original = track.Points.ToList();
        var vm = BuildVmWith(track);

        vm.RemoteExtendTrackEnd(isA: true, metres: 49);

        Assert.That(track.Points, Has.Count.EqualTo(original.Count + 49));
        // Ordered 49 m … 1 m BEHIND P0 along its heading (east) — i.e. easting −49 … −1 — so the
        // list still runs A → B. Each inserted point inherits P0's heading.
        for (int k = 0; k < 49; k++)
        {
            int metresBehind = 49 - k;
            AssertSamePoint(track.Points[k],
                new Vec3(-Math.Sin(HeadingA) * metresBehind, -Math.Cos(HeadingA) * metresBehind, HeadingA));
        }
        // Consecutive inserted points are 1 m apart.
        for (int k = 1; k < 49; k++)
        {
            double d = Math.Sqrt(
                Math.Pow(track.Points[k].Easting - track.Points[k - 1].Easting, 2)
                + Math.Pow(track.Points[k].Northing - track.Points[k - 1].Northing, 2));
            Assert.That(d, Is.EqualTo(1.0).Within(1e-9));
        }
        // Original points follow, unchanged, at +49.
        for (int i = 0; i < original.Count; i++) AssertSamePoint(track.Points[49 + i], original[i]);
        Assert.That(vm.SelectedTrack, Is.SameAs(track));
        Assert.That(track.Name, Is.EqualTo("Cu test"), "AOG doesn't rename on extend");
        Assert.That(vm.StatusMessage, Is.EqualTo("A end extended 49 m"));
    }

    [Test]
    public void ExtendB_Appends49PointsAfterOriginalLast_AlongItsHeading()
    {
        var track = ThreePointCurve();
        var original = track.Points.ToList();
        var last = original[^1];
        var vm = BuildVmWith(track);

        vm.RemoteExtendTrackEnd(isA: false, metres: 49);

        Assert.That(track.Points, Has.Count.EqualTo(original.Count + 49));
        for (int i = 0; i < original.Count; i++) AssertSamePoint(track.Points[i], original[i]);
        // 1 m … 49 m AHEAD of the ORIGINAL last point along its heading (NE).
        for (int i = 1; i <= 49; i++)
        {
            AssertSamePoint(track.Points[original.Count + i - 1],
                new Vec3(last.Easting + Math.Sin(HeadingB) * i, last.Northing + Math.Cos(HeadingB) * i, HeadingB));
        }
        Assert.That(vm.StatusMessage, Is.EqualTo("B end extended 49 m"));
    }

    [Test]
    public void ExtendIsRepeatable_SecondPressAddsAnother49()
    {
        var track = ThreePointCurve();
        var vm = BuildVmWith(track);

        vm.RemoteExtendTrackEnd(isA: false);
        vm.RemoteExtendTrackEnd(isA: false);

        Assert.That(track.Points, Has.Count.EqualTo(3 + 98));
        // Second press starts from the end the first press created: 20 + sin45·98 along NE.
        var tail = track.Points[^1];
        Assert.That(tail.Easting, Is.EqualTo(20 + Math.Sin(HeadingB) * 98).Within(1e-9));
        Assert.That(tail.Northing, Is.EqualTo(5 + Math.Cos(HeadingB) * 98).Within(1e-9));
    }

    [Test]
    public void ABLine_IsRefusedWithStatus_NoChange()
    {
        var ab = new Track
        {
            Name = "AB 90°",
            Type = TrackType.ABLine,
            IsVisible = true,
            Points = new List<Vec3> { new(0, 0, Math.PI / 2), new(100, 0, Math.PI / 2) }
        };
        var before = ab.Points;
        var vm = BuildVmWith(ab);

        vm.RemoteExtendTrackEnd(isA: true);

        Assert.That(ab.Points, Is.SameAs(before));
        Assert.That(ab.Points, Has.Count.EqualTo(2));
        Assert.That(vm.StatusMessage, Does.Contain("AB line"));
    }

    [Test]
    public void NoSelection_IsRefusedWithStatus()
    {
        var vm = new MainViewModelBuilder().Build();
        var track = ThreePointCurve();
        vm.SavedTracks.Add(track); // present but NOT selected

        vm.RemoteExtendTrackEnd(isA: true);

        Assert.That(track.Points, Has.Count.EqualTo(3));
        Assert.That(vm.StatusMessage, Is.EqualTo("No track selected"));
    }

    [Test]
    public void ClosedTrack_IsRefusedWithStatus()
    {
        var ring = ThreePointCurve();
        ring.IsClosed = true;
        var vm = BuildVmWith(ring);

        vm.RemoteExtendTrackEnd(isA: false);

        Assert.That(ring.Points, Has.Count.EqualTo(3));
        Assert.That(vm.StatusMessage, Is.EqualTo("Can't extend a closed track"));
    }

    // A curve whose points carry no headings at all (every Heading == 0) would extend due north
    // whatever its shape under AOG's literal rule; we derive the end heading from the end segment.
    [Test]
    public void AllZeroHeadings_DeriveEndHeadingFromEndSegment()
    {
        var track = new Track
        {
            Name = "imported",
            Type = TrackType.Curve,
            IsVisible = true,
            Points = new List<Vec3> { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0) } // runs due east
        };
        var vm = BuildVmWith(track);

        vm.RemoteExtendTrackEnd(isA: false, metres: 10);

        Assert.That(track.Points, Has.Count.EqualTo(13));
        var tail = track.Points[^1];
        Assert.That(tail.Easting, Is.EqualTo(30).Within(1e-9), "extended east, not north");
        Assert.That(tail.Northing, Is.EqualTo(0).Within(1e-9));
        Assert.That(tail.Heading, Is.EqualTo(Math.PI / 2).Within(1e-9));
    }

    // ---- P2.4: Cancel in the Bnd. Curve trim phase ----

    private const double Radius = 50.0;

    private static MainViewModel BuildVmWithCircleBoundary()
    {
        var vm = new MainViewModelBuilder().Build();
        var pts = new List<BoundaryPoint>(360);
        for (int i = 0; i < 360; i++)
        {
            double t = i * 2.0 * Math.PI / 360;
            pts.Add(new BoundaryPoint(Radius * Math.Sin(t), Radius * Math.Cos(t), 0));
        }
        var bnd = new Boundary { OuterBoundary = new BoundaryPolygon { Points = pts } };
        bnd.OuterBoundary.UpdateBounds();
        vm.State.Field.CurrentBoundary = bnd;
        return vm;
    }

    [Test]
    public void BoundarySegCancel_RemovesTheNewCurve_AndRestoresThePreviousSelection()
    {
        var vm = BuildVmWithCircleBoundary();
        var previous = ThreePointCurve();
        vm.SavedTracks.Add(previous);
        vm.SelectedTrack = previous;

        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(2));
        var created = vm.SavedTracks[1];
        Assert.That(vm.SelectedTrack, Is.SameAs(created));

        vm.RemoteBoundarySegCancel();

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(vm.SavedTracks[0], Is.SameAs(previous));
        Assert.That(vm.SelectedTrack, Is.SameAs(previous));
        Assert.That(previous.IsActive, Is.True);
        Assert.That(created.IsActive, Is.False);
        Assert.That(vm.StatusMessage, Does.StartWith("Boundary curve discarded"));

        // A second Cancel has nothing to act on.
        vm.RemoteBoundarySegCancel();
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(vm.SelectedTrack, Is.SameAs(previous));
        Assert.That(vm.StatusMessage, Is.EqualTo("No boundary curve to cancel"));
    }

    [Test]
    public void BoundarySegCancel_WithNoPreviousSelection_LeavesNothingSelected()
    {
        var vm = BuildVmWithCircleBoundary();

        vm.RemoteCreateBoundaryCurveSegment(0, Radius, Radius, 0);
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));

        vm.RemoteBoundarySegCancel();

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.SelectedTrack, Is.Null);
    }
}

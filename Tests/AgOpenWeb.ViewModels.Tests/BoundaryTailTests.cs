using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Track;
using NSubstitute;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// The operator's "body + tails" model for the two-tap boundary tools (Bnd. AB / Bnd. Curve):
///  - A and B are FIXED anchors — once placed they never move, and the body between them is
///    fixed and not editable;
///  - A++ / A−− / B++ / B−− (RemoteAdjustTail, track.tail) change ONLY the length of the
///    straight line protruding past the anchor; a tail already at 0 makes "−−" a quiet no-op;
///  - taps snap to the closest point on the closest boundary EDGE, not to a vertex;
///  - anchors + tails persist through the Tracks.meta.json sidecar next to TrackLines.txt.
/// The builder's mocked IPolygonOffsetService returns null from CreateInwardOffset, so the
/// curve sits ON the fence, which makes the geometry directly checkable.
/// </summary>
[TestFixture]
public class BoundaryTailTests
{
    // Outer square, vertices walked clockwise (E,N): P0 (0,0) → P1 (0,100) → P2 (100,100) → P3 (100,0).
    // Sparse: each 100 m side is ONE edge — the edge snap must land taps on the side itself.
    private static readonly (double e, double n)[] Square = { (0, 0), (0, 100), (100, 100), (100, 0) };

    private static BoundaryPolygon Ring((double e, double n)[] verts)
    {
        var poly = new BoundaryPolygon();
        foreach (var (e, n) in verts) poly.Points.Add(new BoundaryPoint(e, n, 0));
        poly.UpdateBounds();
        return poly;
    }

    private static MainViewModel BuildSquareVm(MainViewModelBuilder? builder = null)
    {
        var vm = (builder ?? new MainViewModelBuilder()).Build();
        vm.State.Field.CurrentBoundary = new Boundary { OuterBoundary = Ring(Square) };
        return vm;
    }

    private static double HalfSwath()
    {
        var cfg = ConfigurationStore.Instance;
        return Math.Max(0, (cfg.ActualToolWidth - cfg.Tool.Overlap) * 0.5);
    }

    private static void AssertSamePoint(Vec3 actual, Vec3 expected, string? why = null)
    {
        Assert.That(actual.Easting, Is.EqualTo(expected.Easting).Within(1e-9), why);
        Assert.That(actual.Northing, Is.EqualTo(expected.Northing).Within(1e-9), why);
        Assert.That(actual.Heading, Is.EqualTo(expected.Heading).Within(1e-12), why);
    }

    private static int IndexOfPoint(List<Vec3> pts, Vec3 p)
        => pts.FindIndex(q => Math.Abs(q.Easting - p.Easting) < 1e-9 && Math.Abs(q.Northing - p.Northing) < 1e-9);

    private static double Dist(Vec3 a, Vec3 b)
        => Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing));

    // ---- creation: edge snap + anchors + initial tails ----

    // (5) A tap 3 m off the MIDDLE of a 100 m side snaps onto that side — never to a corner —
    // for the CURVE. (3, 50.5) → (0, 50.5) and (3, 20.5) → (0, 20.5) on the left side E = 0,
    // neither of which is a dense-ring vertex (N = k × 1.5625), so the anchors are spliced in
    // exactly. Fence winding P0 → P1 runs north, so A = (0, 20.5), B = (0, 50.5).
    [Test]
    public void Curve_TapsOffTheMiddleOfASide_SnapOntoTheEdge_NotACorner()
    {
        var vm = BuildSquareVm();

        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1), vm.StatusMessage);
        var track = vm.SavedTracks[0];
        Assert.That(track.HasAnchors, Is.True);
        Assert.That(track.AnchorA!.Value.Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(track.AnchorA!.Value.Northing, Is.EqualTo(20.5).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Northing, Is.EqualTo(50.5).Within(1e-9));
        // Body = A … B exactly, 30 m of the side: (0,20.5), dense vertices 21.875 … 50, (0,50.5).
        Assert.That(track.Body![0].Northing, Is.EqualTo(20.5).Within(1e-9));
        Assert.That(track.Body![^1].Northing, Is.EqualTo(50.5).Within(1e-9));
        Assert.That(track.Body!.All(p => Math.Abs(p.Easting) < 1e-9), Is.True, "the body lies on the side");
        Assert.That(track.Points.Any(p => Math.Abs(p.Easting) < 1e-9 && Math.Abs(p.Northing) < 1e-9), Is.False, "no corner in the line");
        Assert.That(track.Points.Any(p => Math.Abs(p.Easting) < 1e-9 && Math.Abs(p.Northing - 100) < 1e-9), Is.False, "no corner in the line");
        // Initial tails = fence crossing + 20 m: A south to N = 0 (20.5 + 20 → 41 m, whole metres),
        // B north to N = 100 (49.5 + 20 → 70 m).
        Assert.That(track.TailA, Is.EqualTo(41));
        Assert.That(track.TailB, Is.EqualTo(70));
        Assert.That(track.Points[0].Northing, Is.EqualTo(20.5 - 41).Within(1e-9));
        Assert.That(track.Points[^1].Northing, Is.EqualTo(50.5 + 70).Within(1e-9));
        Assert.That(track.Points, Has.Count.EqualTo(41 + track.Body!.Count + 70));
        Assert.That(track.Name, Is.EqualTo("Cu 0°"));
    }

    // (5) … and for the AB tool: same two taps, the line's anchors sit at N = 50.5 / 20.5, shifted
    // (w−o)/2 into the field (east of the left side), never at a corner.
    [Test]
    public void AB_TapsOffTheMiddleOfASide_SnapOntoTheEdge_NotACorner()
    {
        var vm = BuildSquareVm();

        vm.RemoteCreateBoundaryAB(3, 50.5, 3, 20.5);

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1), vm.StatusMessage);
        var track = vm.SavedTracks[0];
        Assert.That(track.Type, Is.EqualTo(TrackType.ABLine));
        Assert.That(track.HasAnchors, Is.True);
        Assert.That(track.Points, Has.Count.EqualTo(2), "stays a 2-point (infinite) AB line");
        // Index rule: the short way doesn't cross the seam → A is the higher index = (0,50.5);
        // heading south (180°), interior (centroid 50,50) to the left → shifted east by (w−o)/2.
        double hs = HalfSwath();
        Assert.That(track.Name, Is.EqualTo("AB 180°"));
        Assert.That(track.AnchorA!.Value.Easting, Is.EqualTo(hs).Within(1e-9));
        Assert.That(track.AnchorA!.Value.Northing, Is.EqualTo(50.5).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Easting, Is.EqualTo(hs).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Northing, Is.EqualTo(20.5).Within(1e-9));
        // Tails: A backwards = north to N = 100 (49.5 + 20 → 70), B forwards = south to N = 0 (41).
        Assert.That(track.TailA, Is.EqualTo(70));
        Assert.That(track.TailB, Is.EqualTo(41));
        Assert.That(track.Points[0].Northing, Is.EqualTo(50.5 + 70).Within(1e-9));
        Assert.That(track.Points[1].Northing, Is.EqualTo(20.5 - 41).Within(1e-9));
        Assert.That(track.Points[0].Easting, Is.EqualTo(hs).Within(1e-9));
    }

    // ---- the four buttons ----

    // (1) Anchors never move: after any sequence of ++/−− presses the anchors are still in the
    // points at the same coordinates and the body between them is identical, point for point.
    [Test]
    public void Curve_AnchorsAndBody_NeverMove_UnderAnySequenceOfPresses()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
        var track = vm.SavedTracks[0];
        var anchorA = track.AnchorA!.Value;
        var anchorB = track.AnchorB!.Value;
        var body = track.Body!.ToList();

        var presses = new (bool isA, double d)[]
        {
            (true, 10), (true, 10), (false, -10), (true, -10), (false, 10), (true, -10), (true, -10), (true, -10), (true, -10), (false, -10), (true, 10),
        };
        foreach (var (isA, d) in presses)
        {
            vm.RemoteAdjustTail(isA, d);

            Assert.That(track.AnchorA!.Value, Is.EqualTo(anchorA));
            Assert.That(track.AnchorB!.Value, Is.EqualTo(anchorB));
            Assert.That(track.Body, Has.Count.EqualTo(body.Count));
            int ia = IndexOfPoint(track.Points, anchorA);
            int ib = IndexOfPoint(track.Points, anchorB);
            Assert.That(ia, Is.GreaterThanOrEqualTo(0), "anchor A is still a track point");
            Assert.That(ib, Is.EqualTo(ia + body.Count - 1), "anchor B is still a track point, body.Count−1 after A");
            for (int i = 0; i < body.Count; i++)
                AssertSamePoint(track.Points[ia + i], body[i], $"body point {i} after press {isA}/{d}");
            Assert.That(ia, Is.EqualTo(TrackTails.TailPointCount(track.TailA)));
            Assert.That(track.Points.Count - 1 - ib, Is.EqualTo(TrackTails.TailPointCount(track.TailB)));
        }
    }

    // (2) A−− at tail 0 is a no-op: same Points reference, tail stays 0, status says so.
    [Test]
    public void AMinus_AtZeroTail_IsANoOp_WithStatus()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
        var track = vm.SavedTracks[0];
        Assert.That(track.TailA, Is.EqualTo(41));
        for (int i = 0; i < 4; i++) vm.RemoteAdjustTail(isA: true, deltaMeters: -10); // 41 → 1
        Assert.That(track.TailA, Is.EqualTo(1));
        vm.RemoteAdjustTail(isA: true, deltaMeters: -10); // clamps at 0
        Assert.That(track.TailA, Is.EqualTo(0));
        Assert.That(vm.StatusMessage, Is.EqualTo("A end now at point A"));
        AssertSamePoint(track.Points[0], track.AnchorA!.Value, "the line now starts exactly at A");
        var pointsAtZero = track.Points;

        vm.RemoteAdjustTail(isA: true, deltaMeters: -10);

        Assert.That(track.Points, Is.SameAs(pointsAtZero), "no rebuild");
        Assert.That(track.TailA, Is.EqualTo(0));
        Assert.That(vm.StatusMessage, Is.EqualTo("A end already at point A"));

        // Same at the B end.
        for (int i = 0; i < 7; i++) vm.RemoteAdjustTail(isA: false, deltaMeters: -10); // 70 → 0
        Assert.That(track.TailB, Is.EqualTo(0));
        var pts2 = track.Points;
        vm.RemoteAdjustTail(isA: false, deltaMeters: -10);
        Assert.That(track.Points, Is.SameAs(pts2));
        Assert.That(vm.StatusMessage, Is.EqualTo("B end already at point B"));
        AssertSamePoint(track.Points[^1], track.AnchorB!.Value, "the line now ends exactly at B");
    }

    // (3) A++ then A−− returns to the previous tail and point count (and identical points).
    [Test]
    public void APlus_ThenAMinus_ReturnsToThePreviousTailAndPointCount()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
        var track = vm.SavedTracks[0];
        var before = track.Points.ToList();
        double tailBefore = track.TailA;

        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);
        Assert.That(track.TailA, Is.EqualTo(tailBefore + 10));
        Assert.That(track.Points, Has.Count.EqualTo(before.Count + 10));
        Assert.That(vm.StatusMessage, Is.EqualTo($"A end lengthened to {tailBefore + 10:F0} m past point A"));

        vm.RemoteAdjustTail(isA: true, deltaMeters: -10);
        Assert.That(track.TailA, Is.EqualTo(tailBefore));
        Assert.That(track.Points, Has.Count.EqualTo(before.Count));
        for (int i = 0; i < before.Count; i++) AssertSamePoint(track.Points[i], before[i], $"point {i}");

        // B end likewise.
        double tailBBefore = track.TailB;
        vm.RemoteAdjustTail(isA: false, deltaMeters: 10);
        Assert.That(track.Points, Has.Count.EqualTo(before.Count + 10));
        vm.RemoteAdjustTail(isA: false, deltaMeters: -10);
        Assert.That(track.TailB, Is.EqualTo(tailBBefore));
        Assert.That(track.Points, Has.Count.EqualTo(before.Count));
    }

    // (4) Tail points are 1 m apart and collinear with the body's end heading (the tail runs
    // straight along the end segment's bearing, each point carrying that heading).
    [Test]
    public void TailPoints_Are1mSpaced_AndCollinearWithTheEndHeading()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
        var track = vm.SavedTracks[0];
        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);
        vm.RemoteAdjustTail(isA: false, deltaMeters: 10);
        var pts = track.Points;
        int countA = TrackTails.TailPointCount(track.TailA);
        int countB = TrackTails.TailPointCount(track.TailB);
        var a = track.AnchorA!.Value;
        var b = track.AnchorB!.Value;
        double hA = TrackTails.EndHeading(track.Body!, true);   // north
        double hB = TrackTails.EndHeading(track.Body!, false);  // north
        Assert.That(hA, Is.EqualTo(0).Within(1e-9));
        Assert.That(hB, Is.EqualTo(0).Within(1e-9));

        // A tail: ordered tip → anchor, k m behind A along −(sin h, cos h), 1 m steps.
        for (int i = 0; i < countA; i++)
        {
            int metresBehind = countA - i;
            AssertSamePoint(pts[i], new Vec3(a.Easting - Math.Sin(hA) * metresBehind, a.Northing - Math.Cos(hA) * metresBehind, hA), $"A tail {i}");
            if (i > 0) Assert.That(Dist(pts[i], pts[i - 1]), Is.EqualTo(1.0).Within(1e-9));
        }
        Assert.That(Dist(pts[countA - 1], a), Is.EqualTo(1.0).Within(1e-9), "last A-tail point is 1 m from the anchor");
        // B tail: 1 m, 2 m … ahead of B.
        int ib = countA + track.Body!.Count - 1;
        for (int i = 1; i <= countB; i++)
        {
            AssertSamePoint(pts[ib + i], new Vec3(b.Easting + Math.Sin(hB) * i, b.Northing + Math.Cos(hB) * i, hB), $"B tail {i}");
            Assert.That(Dist(pts[ib + i], pts[ib + i - 1]), Is.EqualTo(1.0).Within(1e-9));
        }
        Assert.That(pts, Has.Count.EqualTo(countA + track.Body!.Count + countB));
    }

    // The AB line's tails move only the two tips; the line stays 2 points and the anchors fixed.
    [Test]
    public void AB_Tails_MoveOnlyTheTips_AnchorsFixed()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryAB(3, 50.5, 3, 20.5);
        var track = vm.SavedTracks[0];
        var a = track.AnchorA!.Value; var b = track.AnchorB!.Value;
        double tailA = track.TailA, tailB = track.TailB;

        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);
        vm.RemoteAdjustTail(isA: false, deltaMeters: -10);

        Assert.That(track.Points, Has.Count.EqualTo(2));
        Assert.That(track.TailA, Is.EqualTo(tailA + 10));
        Assert.That(track.TailB, Is.EqualTo(tailB - 10));
        Assert.That(track.AnchorA!.Value, Is.EqualTo(a));
        Assert.That(track.AnchorB!.Value, Is.EqualTo(b));
        // Heading south: A tip is tailA+10 north of A, B tip tailB−10 south of B.
        Assert.That(track.Points[0].Northing, Is.EqualTo(a.Northing + tailA + 10).Within(1e-9));
        Assert.That(track.Points[1].Northing, Is.EqualTo(b.Northing - (tailB - 10)).Within(1e-9));
        Assert.That(track.Points[0].Easting, Is.EqualTo(a.Easting).Within(1e-9));
        Assert.That(track.IsABLine, Is.True);

        // Shorten B all the way to the anchor: the line then ends exactly at B.
        for (int i = 0; i < 10; i++) vm.RemoteAdjustTail(isA: false, deltaMeters: -10);
        Assert.That(track.TailB, Is.EqualTo(0));
        Assert.That(track.Points[1].Northing, Is.EqualTo(b.Northing).Within(1e-9));
        Assert.That(track.Points[1].Easting, Is.EqualTo(b.Easting).Within(1e-9));
    }

    [Test]
    public void AdjustTail_RefusesATrackWithoutAnchors_AndNoSelection()
    {
        var vm = BuildSquareVm();
        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);
        Assert.That(vm.StatusMessage, Is.EqualTo("Select a boundary AB or curve"));

        var drawn = new Track
        {
            Name = "Cu hand",
            Type = TrackType.Curve,
            Points = new List<Vec3> { new(0, 0, 0), new(0, 10, 0), new(0, 20, 0) },
        };
        vm.SavedTracks.Add(drawn);
        vm.SelectedTrack = drawn;
        var before = drawn.Points;

        vm.RemoteAdjustTail(isA: false, deltaMeters: 10);

        Assert.That(drawn.Points, Is.SameAs(before));
        Assert.That(drawn.HasAnchors, Is.False);
        Assert.That(vm.StatusMessage, Is.EqualTo("Select a boundary AB or curve"));
    }

    // ---- (6) persistence: anchors + tails survive SaveTracksToFile → reload via the sidecar ----

    [Test]
    public void AnchorsAndTails_SurviveSaveAndReload_ThroughTheSidecar()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AgOpenWeb_BoundaryTailTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var builder = new MainViewModelBuilder();
            var vm = BuildSquareVm(builder);
            builder.FieldService.ActiveField.Returns(new Field { Name = "Tails", DirectoryPath = dir });

            vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
            vm.RemoteAdjustTail(isA: true, deltaMeters: -10);       // 41 → 31
            vm.RemoteCreateBoundaryAB(-2, 102, 102, 102);            // second track, an AB with anchors
            var curve = vm.SavedTracks[0];
            var ab = vm.SavedTracks[1];
            Assert.That(File.Exists(Path.Combine(dir, "TrackLines.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(dir, Services.TrackFilesService.MetaFileName)), Is.True, "sidecar written");

            var loaded = Services.TrackFilesService.Load(dir);

            Assert.That(loaded, Has.Count.EqualTo(2));
            foreach (var (orig, back) in new[] { (curve, loaded[0]), (ab, loaded[1]) })
            {
                Assert.That(back.Name, Is.EqualTo(orig.Name));
                Assert.That(back.HasAnchors, Is.True, $"'{orig.Name}' keeps its anchors");
                Assert.That(back.AnchorA!.Value.Easting, Is.EqualTo(orig.AnchorA!.Value.Easting).Within(1e-9));
                Assert.That(back.AnchorA!.Value.Northing, Is.EqualTo(orig.AnchorA!.Value.Northing).Within(1e-9));
                Assert.That(back.AnchorB!.Value.Easting, Is.EqualTo(orig.AnchorB!.Value.Easting).Within(1e-9));
                Assert.That(back.AnchorB!.Value.Northing, Is.EqualTo(orig.AnchorB!.Value.Northing).Within(1e-9));
                Assert.That(back.TailA, Is.EqualTo(orig.TailA));
                Assert.That(back.TailB, Is.EqualTo(orig.TailB));
                Assert.That(back.Body, Has.Count.EqualTo(orig.Body!.Count));
                Assert.That(back.Points, Has.Count.EqualTo(orig.Points.Count));
                for (int i = 0; i < orig.Body!.Count; i++)
                {
                    Assert.That(back.Body![i].Easting, Is.EqualTo(orig.Body[i].Easting).Within(0.0006), $"body E {i}"); // file = 3 decimals
                    Assert.That(back.Body![i].Northing, Is.EqualTo(orig.Body[i].Northing).Within(0.0006), $"body N {i}");
                }
            }
            Assert.That(loaded[0].TailA, Is.EqualTo(31));

            // The reloaded track is still adjustable: A−− rebuilds from the recovered body and the
            // anchor stays put.
            vm.SavedTracks.Clear();
            foreach (var t in loaded) vm.SavedTracks.Add(t);
            vm.SelectedTrack = loaded[0];
            vm.RemoteAdjustTail(isA: true, deltaMeters: -10);
            Assert.That(loaded[0].TailA, Is.EqualTo(21));
            Assert.That(loaded[0].Points, Has.Count.EqualTo(21 + loaded[0].Body!.Count + TrackTails.TailPointCount(loaded[0].TailB)));
            Assert.That(loaded[0].Points[21].Northing, Is.EqualTo(20.5).Within(0.0006), "anchor A still at N = 20.5");
            Assert.That(loaded[0].Points[0].Northing, Is.EqualTo(20.5 - 21).Within(0.0006));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // A track without anchors writes no sidecar entry; a field with none has no sidecar at all.
    [Test]
    public void NoAnchoredTracks_NoSidecar()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AgOpenWeb_BoundaryTailTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, Services.TrackFilesService.MetaFileName), "{}"); // stale leftover
            var t = new Track { Name = "AB 90°", Type = TrackType.ABLine, Points = new List<Vec3> { new(0, 0, Math.PI / 2), new(100, 0, Math.PI / 2) } };
            Services.TrackFilesService.Save(dir, new[] { t });
            Assert.That(File.Exists(Path.Combine(dir, Services.TrackFilesService.MetaFileName)), Is.False, "stale sidecar removed");
            var loaded = Services.TrackFilesService.Load(dir);
            Assert.That(loaded[0].HasAnchors, Is.False);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- Cancel in the tails phase (P2.4) — both products ----

    private static Track ThreePointCurve() => new()
    {
        Name = "Cu test",
        Type = TrackType.Curve,
        IsVisible = true,
        Points = new List<Vec3> { new(0, 0, Math.PI / 2), new(10, 0, Math.PI / 2), new(20, 5, Math.PI / 4) },
    };

    [Test]
    public void BoundarySegCancel_RemovesTheNewCurve_AndRestoresThePreviousSelection()
    {
        var vm = BuildSquareVm();
        var previous = ThreePointCurve();
        vm.SavedTracks.Add(previous);
        vm.SelectedTrack = previous;

        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(2));
        var created = vm.SavedTracks[1];
        Assert.That(vm.SelectedTrack, Is.SameAs(created));

        vm.RemoteBoundarySegCancel();

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(vm.SavedTracks[0], Is.SameAs(previous));
        Assert.That(vm.SelectedTrack, Is.SameAs(previous));
        Assert.That(previous.IsActive, Is.True);
        Assert.That(created.IsActive, Is.False);
        Assert.That(vm.StatusMessage, Does.StartWith("Boundary line discarded"));

        // A second Cancel has nothing to act on.
        vm.RemoteBoundarySegCancel();
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));
        Assert.That(vm.SelectedTrack, Is.SameAs(previous));
        Assert.That(vm.StatusMessage, Is.EqualTo("No boundary line to cancel"));
    }

    [Test]
    public void BoundarySegCancel_AlsoDiscardsAJustBuiltAB_WithNoPreviousSelection()
    {
        var vm = BuildSquareVm();

        vm.RemoteCreateBoundaryAB(3, 50.5, 3, 20.5);
        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1));

        vm.RemoteBoundarySegCancel();

        Assert.That(vm.SavedTracks, Is.Empty);
        Assert.That(vm.SelectedTrack, Is.Null);
        Assert.That(vm.StatusMessage, Is.EqualTo("Boundary line discarded"));
    }

    // ---- TrackTails builder itself ----

    [Test]
    public void BuildWithTails_ZeroTails_ReturnsTheBodyVerbatim_AndFractionalTailsGetAnExactTip()
    {
        var body = new List<Vec3> { new(0, 0, Math.PI / 2), new(10, 0, Math.PI / 2), new(20, 0, Math.PI / 2) }; // east

        var none = TrackTails.BuildWithTails(body, 0, 0);
        Assert.That(none, Has.Count.EqualTo(3));
        for (int i = 0; i < 3; i++) AssertSamePoint(none[i], body[i]);

        var frac = TrackTails.BuildWithTails(body, 2.5, 0);
        Assert.That(TrackTails.TailPointCount(2.5), Is.EqualTo(3));
        Assert.That(frac, Has.Count.EqualTo(6));
        Assert.That(frac[0].Easting, Is.EqualTo(-2.5).Within(1e-9), "exact tip");
        Assert.That(frac[1].Easting, Is.EqualTo(-2).Within(1e-9));
        Assert.That(frac[2].Easting, Is.EqualTo(-1).Within(1e-9));
        Assert.That(frac[3].Easting, Is.EqualTo(0).Within(1e-9));

        // Recovery is the exact inverse.
        var back = TrackTails.RecoverBody(frac, body[0], body[^1], 2.5, 0, endpointsOnly: false);
        Assert.That(back, Is.Not.Null);
        Assert.That(back, Has.Count.EqualTo(3));
        for (int i = 0; i < 3; i++) AssertSamePoint(back![i], body[i]);
        // A mismatched tail length (points don't land on the anchors) is rejected, not trusted.
        Assert.That(TrackTails.RecoverBody(frac, body[0], body[^1], 1, 0, endpointsOnly: false), Is.Null);
    }

    // Verifier finding: Swap A/B, Smooth and the Field Builder drag-edit rewrote Points
    // wholesale and would have corrupted an anchored line (anchors stale, body detached).
    // They must refuse on an anchored line — A and B are fixed by definition.
    [Test]
    public void SwapSmoothAndDragEdit_RefuseOnAnAnchoredLine_AnchorsUntouched()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(3, 50.5, 3, 20.5);
        var t = vm.SelectedTrack!;
        Assert.That(t.HasAnchors, Is.True);
        var a = t.AnchorA!.Value; var b = t.AnchorB!.Value;
        var pts = t.Points.Select(p => (p.Easting, p.Northing)).ToList();

        vm.SwapABPointsCommand!.Execute(null);
        Assert.That(vm.StatusMessage, Does.Contain("fixed"), "swap refused");
        vm.SmoothABLineCommand!.Execute(null);
        Assert.That(vm.StatusMessage, Does.Contain("fixed"), "smooth refused");
        vm.RemoteSaveTrackEdit(vm.SavedTracks.IndexOf(t), new List<(double e, double n)> { (1, 1), (2, 2), (3, 3) });
        Assert.That(vm.StatusMessage, Does.Contain("fixed"), "drag-edit refused");

        Assert.That(t.AnchorA!.Value.Easting, Is.EqualTo(a.Easting).Within(1e-9)); Assert.That(t.AnchorA!.Value.Northing, Is.EqualTo(a.Northing).Within(1e-9));
        Assert.That(t.AnchorB!.Value.Easting, Is.EqualTo(b.Easting).Within(1e-9)); Assert.That(t.AnchorB!.Value.Northing, Is.EqualTo(b.Northing).Within(1e-9));
        Assert.That(t.Points.Select(p => (p.Easting, p.Northing)).ToList(), Is.EqualTo(pts), "points byte-identical — nothing rewrote them");
    }
}

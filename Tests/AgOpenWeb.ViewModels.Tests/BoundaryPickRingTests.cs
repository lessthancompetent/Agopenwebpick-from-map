using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Geometry;

namespace AgOpenWeb.ViewModels.Tests;

/// <summary>
/// P1.1 dense pick ring + P1.3 segment-curve geometry of the two-tap "Bnd. Curve"
/// (RemoteCreateBoundaryCurveSegment), mirroring AgOpenGPS 6.8.6 FormABDraw.BtnMakeCurve_Click
/// (FormABDraw.cs:424-529) on top of a CFenceLine.FixFenceLine-dense fence (CFenceLine.cs:48-114):
///  - taps snap to the closest point on the closest EDGE and are spliced into the DENSE ring
///    (1.1 m under 20 ha, halved for inner rings) as exact vertices, so two taps on one straight
///    side of a 4-vertex square become two exact anchors and the dense side vertices between
///    them make the curve body;
///  - the body follows the shorter arc by vertex count, walked in FENCE WINDING regardless of
///    tap order, from anchor A to anchor B inclusive, wrapping through index 0 when the taps
///    straddle the seam;
///  - midpoint densification to ≤ 1.6 m with NO smoothing (corners stay exactly where they are);
///  - central-difference headings, "Cu {deg}°" name from the circular-mean heading.
/// The builder's mocked IPolygonOffsetService returns null from CreateInwardOffset, so the VM
/// falls back to the raw boundary ring — the curve sits ON the fence, which makes the geometry
/// directly checkable against the fence coordinates.
/// </summary>
[TestFixture]
public class BoundaryPickRingTests
{
    // Outer square, vertices walked clockwise (E,N): P0 (0,0) → P1 (0,100) → P2 (100,100) → P3 (100,0).
    // 1 ha → AOG spacing 1.1 m; FixSpacing halves each 100 m side until ≤ 1.65 m → 64 segments
    // of 1.5625 m per side, 256 dense vertices, P0 at index 0.
    private static readonly (double e, double n)[] Square = { (0, 0), (0, 100), (100, 100), (100, 0) };
    private const double DenseStep = 100.0 / 64; // 1.5625 m

    private static BoundaryPolygon Ring((double e, double n)[] verts)
    {
        var poly = new BoundaryPolygon();
        foreach (var (e, n) in verts) poly.Points.Add(new BoundaryPoint(e, n, 0));
        poly.UpdateBounds();
        return poly;
    }

    private static MainViewModel BuildSquareVm()
    {
        var vm = new MainViewModelBuilder().Build();
        vm.State.Field.CurrentBoundary = new Boundary { OuterBoundary = Ring(Square) };
        return vm;
    }

    private static MainViewModel BuildCircleVm(double radius, int ringPoints)
    {
        var vm = new MainViewModelBuilder().Build();
        var pts = new List<BoundaryPoint>(ringPoints);
        for (int i = 0; i < ringPoints; i++)
        {
            double t = i * 2.0 * Math.PI / ringPoints;
            pts.Add(new BoundaryPoint(radius * Math.Sin(t), radius * Math.Cos(t), 0));
        }
        var bnd = new Boundary { OuterBoundary = new BoundaryPolygon { Points = pts } };
        bnd.OuterBoundary.UpdateBounds();
        vm.State.Field.CurrentBoundary = bnd;
        return vm;
    }

    private static bool OnSquareLeftSide(Vec3 p) => Math.Abs(p.Easting) < 1e-9 && p.Northing >= -1e-9 && p.Northing <= 100 + 1e-9;

    // (a) Two taps 30 m apart on the SAME straight side of the sparse square. With vertex snapping
    // on the raw 4-vertex ring both would snap to P0 or P1 ("Pick two different points"); the edge
    // snap lands them ON the side at exactly N = 30 and N = 60 (neither is a dense vertex), and
    // the dense side vertices between them make the body — lying exactly on the side (no smoothing).
    [Test]
    public void SameStraightSide_TwoTaps_SnapOntoTheEdge_AndMakeACurve()
    {
        var vm = BuildSquareVm();

        vm.RemoteCreateBoundaryCurveSegment(1, 30, 1, 60); // both 1 m inside the left side E = 0

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1), vm.StatusMessage);
        var track = vm.SavedTracks[0];
        Assert.That(track.Type, Is.EqualTo(TrackType.Curve));
        Assert.That(track.NoPassOffset, Is.True);
        Assert.That(track.IsClosed, Is.False);
        Assert.That(vm.SelectedTrack, Is.SameAs(track));
        Assert.That(vm.StatusMessage, Does.StartWith("Created Cu "));

        // Anchors: (0, 30) and (0, 60) exactly; body = A, dense vertices 20..38 (31.25 … 59.375 m),
        // B → 21 points, every one exactly on E = 0.
        Assert.That(track.HasAnchors, Is.True);
        Assert.That(track.AnchorA!.Value.Northing, Is.EqualTo(30).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Northing, Is.EqualTo(60).Within(1e-9));
        var body = track.Body!;
        Assert.That(body, Has.Count.EqualTo(21));
        Assert.That(body[0].Northing, Is.EqualTo(30).Within(1e-9));
        Assert.That(body[1].Northing, Is.EqualTo(20 * DenseStep).Within(1e-9));
        Assert.That(body[^2].Northing, Is.EqualTo(38 * DenseStep).Within(1e-9));
        Assert.That(body[^1].Northing, Is.EqualTo(60).Within(1e-9));
        Assert.That(body.All(OnSquareLeftSide), Is.True);
        // Tails: A south to the bottom side (30 + 20 → 50 m), B north to the top (40 + 20 → 60 m).
        Assert.That(track.TailA, Is.EqualTo(50));
        Assert.That(track.TailB, Is.EqualTo(60));
        Assert.That(track.Points, Has.Count.EqualTo(50 + 21 + 60));
        Assert.That(track.Points[0].Northing, Is.EqualTo(30 - 50).Within(1e-9));
        Assert.That(track.Points[^1].Northing, Is.EqualTo(60 + 60).Within(1e-9));
        // Every track point lies on the side's line E = 0 (tails included — they run along it).
        Assert.That(track.Points.All(p => Math.Abs(p.Easting) < 1e-9), Is.True, "no smoothing / no lateral shift");
        // (e) name: "Cu " + circular-mean heading, general number format + degree sign. Walking
        // north along E = 0 every heading is 0 → "Cu 0°".
        Assert.That(track.Name, Does.StartWith("Cu "));
        Assert.That(track.Name, Does.EndWith("°"));
        Assert.That(track.Name, Is.EqualTo("Cu 0°"));
    }

    // (b) Tap order does NOT change the travel direction: the curve is walked in fence winding
    // (increasing ring index = P0 → P1 = north along E = 0) whichever point was tapped first.
    [Test]
    public void TapOrder_DoesNotChangeDirection_FenceWindingWins()
    {
        var vm1 = BuildSquareVm();
        vm1.RemoteCreateBoundaryCurveSegment(1, 30, 1, 60); // A south, B north
        var vm2 = BuildSquareVm();
        vm2.RemoteCreateBoundaryCurveSegment(1, 60, 1, 30); // A north, B south

        var p1 = vm1.SavedTracks[0].Points;
        var p2 = vm2.SavedTracks[0].Points;
        Assert.That(p2, Has.Count.EqualTo(p1.Count));
        for (int i = 0; i < p1.Count; i++)
        {
            Assert.That(p2[i].Easting, Is.EqualTo(p1[i].Easting).Within(1e-9), $"E at {i}");
            Assert.That(p2[i].Northing, Is.EqualTo(p1[i].Northing).Within(1e-9), $"N at {i}");
            Assert.That(p2[i].Heading, Is.EqualTo(p1[i].Heading).Within(1e-9), $"heading at {i}");
        }
        // Both run NORTH (fence winding P0 → P1), first to last.
        Assert.That(p1[^1].Northing, Is.GreaterThan(p1[0].Northing));
        Assert.That(p2[^1].Northing, Is.GreaterThan(p2[0].Northing));
        Assert.That(vm2.SavedTracks[0].Name, Is.EqualTo(vm1.SavedTracks[0].Name));
    }

    // (c) Taps straddling index 0 (P0 at the seam): A on the left side 10 m up, B on the bottom
    // side 10 m along. The snaps (0,10) and (10,0) are spliced in at indices 7 and 251 of the
    // 258-vertex ring; |7 − 251| > n/2 → the short arc crosses the seam and is walked 251..257,
    // 0..7 (bottom side → corner P0 → left side): 15 vertices, not the ~245-vertex complement.
    // The corner (0,0) is in the body exactly (no corner cutting); the anchors are the body's ends.
    [Test]
    public void TapsStraddlingTheSeam_WalkTheShortArcThroughIndexZero()
    {
        var vm = BuildSquareVm();

        vm.RemoteCreateBoundaryCurveSegment(1, 10, 10, 1);

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1), vm.StatusMessage);
        var track = vm.SavedTracks[0];
        var pts = track.Points;
        int corner = pts.FindIndex(p => Math.Abs(p.Easting) < 1e-9 && Math.Abs(p.Northing) < 1e-9);
        Assert.That(corner, Is.GreaterThan(0), "corner P0 (0,0) must be a curve point");
        // Before the corner: on the bottom side (N = 0, E > 0) moving toward P0; after: up the left side.
        Assert.That(pts[corner - 1].Northing, Is.EqualTo(0).Within(1e-9));
        Assert.That(pts[corner - 1].Easting, Is.EqualTo(DenseStep).Within(1e-9));
        Assert.That(pts[corner + 1].Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(pts[corner + 1].Northing, Is.EqualTo(DenseStep).Within(1e-9));
        // Body = A (10,0) + 6 bottom-side vertices (E = 9.375 … 1.5625) + P0 + 6 left-side
        // (N = 1.5625 … 9.375) + B (0,10) = 15 points, 20 m.
        var body = track.Body!;
        Assert.That(body, Has.Count.EqualTo(15));
        Assert.That(body[0].Easting, Is.EqualTo(10).Within(1e-9), "walk starts at anchor A (10,0)");
        Assert.That(body[0].Northing, Is.EqualTo(0).Within(1e-9));
        Assert.That(body[1].Easting, Is.EqualTo(6 * DenseStep).Within(1e-9));
        Assert.That(body[^2].Northing, Is.EqualTo(6 * DenseStep).Within(1e-9));
        Assert.That(body[^1].Easting, Is.EqualTo(0).Within(1e-9), "…and ends at anchor B (0,10)");
        Assert.That(body[^1].Northing, Is.EqualTo(10).Within(1e-9));
        Assert.That(track.AnchorA!.Value.Easting, Is.EqualTo(10).Within(1e-9));
        Assert.That(track.AnchorB!.Value.Northing, Is.EqualTo(10).Within(1e-9));
        // Far smaller than the complement (~245 body vertices + tails).
        Assert.That(pts, Has.Count.LessThan(250));
    }

    // Seam symmetry: tapping the same two points in the other order gives the identical curve.
    [Test]
    public void TapsStraddlingTheSeam_OppositeOrder_GivesTheSameCurve()
    {
        var vm1 = BuildSquareVm();
        vm1.RemoteCreateBoundaryCurveSegment(1, 10, 10, 1);
        var vm2 = BuildSquareVm();
        vm2.RemoteCreateBoundaryCurveSegment(10, 1, 1, 10);

        var p1 = vm1.SavedTracks[0].Points;
        var p2 = vm2.SavedTracks[0].Points;
        Assert.That(p2, Has.Count.EqualTo(p1.Count));
        for (int i = 0; i < p1.Count; i++)
        {
            Assert.That(p2[i].Easting, Is.EqualTo(p1[i].Easting).Within(1e-9));
            Assert.That(p2[i].Northing, Is.EqualTo(p1[i].Northing).Within(1e-9));
        }
    }

    // (d) Densified spacing ≤ 1.6 m between consecutive curve points, tails excluded. A 360-point
    // circle (0.87 m) is thinned by FixSpacing's < 0.9 × 1.1 m pass to 1.745 m, which is above
    // AOG's 1.6 m curve spacing, so MakePointMinimumSpacing must halve it. Body points (including
    // the inserted chord midpoints, sagitta 0.008 m) stay within 0.02 m of the circle; the
    // straight 1 m tangent tails leave that band by their second step (0.04 m out).
    [Test]
    public void CurveBody_IsDensifiedToAtMost1_6m()
    {
        const double radius = 50.0;
        var vm = BuildCircleVm(radius, 360);

        vm.RemoteCreateBoundaryCurveSegment(0, radius, radius, 0); // top → east, quarter arc

        Assert.That(vm.SavedTracks, Has.Count.EqualTo(1), vm.StatusMessage);
        var pts = vm.SavedTracks[0].Points;
        bool InBand(Vec3 p) => Math.Abs(Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing) - radius) < 0.02;
        int pairs = 0; double maxStep = 0, sumStep = 0;
        for (int i = 1; i < pts.Count; i++)
        {
            if (!InBand(pts[i - 1]) || !InBand(pts[i])) continue;
            double dx = pts[i].Easting - pts[i - 1].Easting, dy = pts[i].Northing - pts[i - 1].Northing;
            double d = Math.Sqrt(dx * dx + dy * dy);
            pairs++; sumStep += d; if (d > maxStep) maxStep = d;
        }
        Assert.That(pairs, Is.GreaterThan(80), "quarter arc ≈ 78.5 m at ≤ 1.6 m");
        Assert.That(maxStep, Is.LessThanOrEqualTo(1.6 + 1e-9));
        Assert.That(sumStep / pairs, Is.EqualTo(1.745 / 2).Within(0.05), "midpoints of the 1.745 m dense ring");
        Assert.That(vm.SavedTracks[0].Name, Does.StartWith("Cu ").And.EndWith("°"));
    }

    // Headings are central differences (AOG CABCurve.CalculateHeadings): on the quarter arc the
    // heading rotates smoothly from ≈ 90° (east, at the top) to ≈ 180° (south, at the east point),
    // and a body point's heading matches the direction from its previous to its next point.
    [Test]
    public void CurveBody_HasCentralDifferenceHeadings()
    {
        const double radius = 50.0;
        var vm = BuildCircleVm(radius, 360);
        vm.RemoteCreateBoundaryCurveSegment(0, radius, radius, 0);
        var pts = vm.SavedTracks[0].Points;
        bool InBand(Vec3 p) => Math.Abs(Math.Sqrt(p.Easting * p.Easting + p.Northing * p.Northing) - radius) < 0.02;

        int checkedPts = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            if (!InBand(pts[i - 1]) || !InBand(pts[i]) || !InBand(pts[i + 1])) continue;
            double expected = Math.Atan2(pts[i + 1].Easting - pts[i - 1].Easting, pts[i + 1].Northing - pts[i - 1].Northing);
            if (expected < 0) expected += 2 * Math.PI;
            Assert.That(pts[i].Heading, Is.EqualTo(expected).Within(1e-9), $"heading at {i}");
            checkedPts++;
        }
        Assert.That(checkedPts, Is.GreaterThan(80));
    }

    // The initial straight tails are capped at AOG's 99 m (AddFirstLastPoints adds 1..99 m). On
    // the square the body's end tangent runs ALONG the bottom side, so the fence-crossing raycast
    // lands on the far corner 90 m away (+20 m margin = 110 m) — the cap holds it to 99 m.
    [Test]
    public void Tails_AreCappedAt99m()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(1, 10, 10, 1); // anchors: A (10, 0) … B (0, 10)
        var track = vm.SavedTracks[0];
        var pts = track.Points;

        Assert.That(track.TailA, Is.EqualTo(99));
        Assert.That(track.TailB, Is.EqualTo(99));
        // A tail: backwards from (10, 0) = east along N = 0 → tip at E = 10 + 99, 99 points at 1 m.
        Assert.That(pts[0].Northing, Is.EqualTo(0).Within(1e-9));
        Assert.That(pts[0].Easting, Is.EqualTo(10 + 99).Within(1e-6));
        Assert.That(pts[98].Easting, Is.EqualTo(11).Within(1e-6));
        // B tail: forwards from (0, 10) = north along E = 0 → tip at N = 10 + 99.
        Assert.That(pts[^1].Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(pts[^1].Northing, Is.EqualTo(10 + 99).Within(1e-6));
        Assert.That(pts, Has.Count.EqualTo(99 + 15 + 99));
    }

    // (g) FixSpacing (the FixFenceLine port BuildPickRing runs): outer ring of a 1 ha square gets
    // AOG's 1.1 m rule — every gap within (0.9, 1.5) × 1.1 — and an inner ring half of that.
    // (Midpoint halving of a 100 m side bottoms out at 1.5625 m for the outer ring and 0.78 m
    // for the inner, i.e. "≈ 1.1" / "≈ 0.55" within the rule's own tolerance band.)
    [Test]
    public void FixSpacing_UsesAogAreaRule_OuterAbout1_1m_InnerAbout0_55m()
    {
        var svc = new FenceLineService();
        var raw = Square.Select(v => new Vec3(v.e, v.n, 0)).ToList();

        var outer = svc.FixSpacing(raw, 100 * 100, 0, out _);
        var inner = svc.FixSpacing(raw, 100 * 100, 1, out _);

        static (double min, double max, double mean) Gaps(List<Vec3> ring)
        {
            double min = double.MaxValue, max = 0, sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                double d = Math.Sqrt((a.Easting - b.Easting) * (a.Easting - b.Easting) + (a.Northing - b.Northing) * (a.Northing - b.Northing));
                min = Math.Min(min, d); max = Math.Max(max, d); sum += d;
            }
            return (min, max, sum / ring.Count);
        }

        var o = Gaps(outer);
        Assert.That(outer, Has.Count.EqualTo(256));
        Assert.That(o.min, Is.GreaterThanOrEqualTo(1.1 * 0.9 - 1e-9));
        Assert.That(o.max, Is.LessThanOrEqualTo(1.1 * 1.5 + 1e-9));
        Assert.That(o.mean, Is.EqualTo(1.1).Within(0.5));

        var n = Gaps(inner);
        Assert.That(inner, Has.Count.EqualTo(512));
        Assert.That(n.min, Is.GreaterThanOrEqualTo(0.55 * 0.9 - 1e-9));
        Assert.That(n.max, Is.LessThanOrEqualTo(0.55 * 1.5 + 1e-9));
        Assert.That(n.mean, Is.EqualTo(0.55).Within(0.25));
        Assert.That(n.mean, Is.EqualTo(o.mean / 2).Within(1e-9), "inner ring = half the outer spacing");

        // Densifying only ADDS points: every original corner is still present at its position.
        foreach (var (e, nn) in Square)
        {
            Assert.That(outer.Any(p => Math.Abs(p.Easting - e) < 1e-9 && Math.Abs(p.Northing - nn) < 1e-9), Is.True, $"outer keeps ({e},{nn})");
            Assert.That(inner.Any(p => Math.Abs(p.Easting - e) < 1e-9 && Math.Abs(p.Northing - nn) < 1e-9), Is.True, $"inner keeps ({e},{nn})");
        }
    }

    // The tail buttons never touch the ring or the body: A++ on the sparse square adds 10 straight
    // 1 m points south of anchor A (0, 30) along the side's line; the body's dense vertices are
    // exactly the ones there before, and B's side is untouched.
    [Test]
    public void TailButtons_NeverMoveTheAnchorsOrBody()
    {
        var vm = BuildSquareVm();
        vm.RemoteCreateBoundaryCurveSegment(1, 30, 1, 60); // anchors (0,30) … (0,60)
        var track = vm.SavedTracks[0];
        string name = track.Name;
        var bodyBefore = track.Body!.ToList();
        bool HasVertex(int i) => track.Points.Any(p => Math.Abs(p.Easting) < 1e-9 && Math.Abs(p.Northing - i * DenseStep) < 1e-9);
        Assert.That(HasVertex(20), Is.True);
        Assert.That(HasVertex(38), Is.True);
        Assert.That(HasVertex(19), Is.False, "19 × 1.5625 = 29.69 is below anchor A — not a body vertex, and the 1 m tail never lands on it");

        vm.RemoteAdjustTail(isA: true, deltaMeters: 10);

        Assert.That(vm.SavedTracks[0], Is.SameAs(track));
        Assert.That(vm.StatusMessage, Is.EqualTo("A end lengthened to 60 m past point A"));
        Assert.That(track.Body, Has.Count.EqualTo(bodyBefore.Count));
        for (int i = 0; i < bodyBefore.Count; i++)
        {
            Assert.That(track.Body![i].Easting, Is.EqualTo(bodyBefore[i].Easting).Within(1e-12));
            Assert.That(track.Body![i].Northing, Is.EqualTo(bodyBefore[i].Northing).Within(1e-12));
        }
        Assert.That(track.AnchorA!.Value.Northing, Is.EqualTo(30).Within(1e-12));
        Assert.That(track.Points[0].Northing, Is.EqualTo(30 - 60).Within(1e-9), "tip 60 m south of A");
        Assert.That(track.Points[60].Northing, Is.EqualTo(30).Within(1e-9), "anchor A after 60 tail points");
        Assert.That(track.Points.All(p => Math.Abs(p.Easting) < 1e-9), Is.True, "everything still on the side's line");
        Assert.That(track.Name, Is.EqualTo(name), "name is not recomputed on a tail change (AOG's A++/B++ don't either)");
    }
}

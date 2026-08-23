using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;

// Alias to avoid conflict with AgOpenWeb.Services.Track namespace
using MTrack = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services.Tests;

/// <summary>
/// Tracks.meta.json — the sidecar TrackFilesService writes next to TrackLines.txt for
/// boundary-derived tracks (fixed anchors A/B + tail lengths; Track.AnchorA/B, TailA/B, Body).
/// TrackLines.txt itself stays AgOpenGPS-compatible (points only); the sidecar is keyed by
/// index AND name, and an entry whose recorded tails don't reproduce the file's points is
/// dropped rather than trusted.
/// </summary>
[TestFixture]
public class TrackMetaSidecarTests
{
    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"agvalonia_meta_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private static MTrack AnchoredCurve(string name = "Cu 90°")
    {
        // Body: 5 points east along N = 0 from (0,0) to (4,0); tails 3 m (A) / 2 m (B).
        var body = new List<Vec3>();
        for (int i = 0; i <= 4; i++) body.Add(new Vec3(i, 0, Math.PI / 2));
        return new MTrack
        {
            Name = name,
            Type = TrackType.Curve,
            Body = body,
            AnchorA = body[0],
            AnchorB = body[^1],
            TailA = 3,
            TailB = 2,
            Points = TrackTails.BuildWithTails(body, 3, 2),
        };
    }

    private static MTrack AnchoredAB()
    {
        var body = new List<Vec3> { new(10, 10, 0), new(10, 50, 0) }; // north
        return new MTrack
        {
            Name = "AB 0°",
            Type = TrackType.ABLine,
            Body = body,
            AnchorA = body[0],
            AnchorB = body[1],
            TailA = 25,
            TailB = 40,
            Points = TrackTails.BuildWithTails(body, 25, 40, endpointsOnly: true),
        };
    }

    [Test]
    public void AnchorsAndTails_RoundTrip_ThroughTheSidecar()
    {
        var plain = new MTrack { Name = "hand AB", Type = TrackType.ABLine, Points = new List<Vec3> { new(0, 0, 0), new(0, 100, 0) } };
        var curve = AnchoredCurve();
        var ab = AnchoredAB();

        TrackFilesService.Save(_tempDir, new[] { plain, curve, ab });

        Assert.That(File.Exists(Path.Combine(_tempDir, TrackFilesService.MetaFileName)), Is.True);
        var loaded = TrackFilesService.Load(_tempDir);
        Assert.That(loaded, Has.Count.EqualTo(3));
        Assert.That(loaded[0].HasAnchors, Is.False, "a plain track gets no anchors");

        var c = loaded[1];
        Assert.That(c.HasAnchors, Is.True);
        Assert.That(c.TailA, Is.EqualTo(3));
        Assert.That(c.TailB, Is.EqualTo(2));
        Assert.That(c.AnchorA!.Value.Easting, Is.EqualTo(0).Within(1e-9));
        Assert.That(c.AnchorB!.Value.Easting, Is.EqualTo(4).Within(1e-9));
        Assert.That(c.Body, Has.Count.EqualTo(5));
        Assert.That(c.Points, Has.Count.EqualTo(3 + 5 + 2));
        Assert.That(c.Points[0].Easting, Is.EqualTo(-3).Within(1e-3));
        Assert.That(c.Points[^1].Easting, Is.EqualTo(6).Within(1e-3));
        // Rebuilding from the recovered body reproduces the file's points (within its 3 decimals).
        var rebuilt = TrackTails.BuildWithTails(c.Body!, c.TailA, c.TailB);
        for (int i = 0; i < rebuilt.Count; i++)
        {
            Assert.That(rebuilt[i].Easting, Is.EqualTo(c.Points[i].Easting).Within(1e-3));
            Assert.That(rebuilt[i].Northing, Is.EqualTo(c.Points[i].Northing).Within(1e-3));
        }

        var a = loaded[2];
        Assert.That(a.HasAnchors, Is.True);
        Assert.That(a.Points, Has.Count.EqualTo(2), "still a 2-point AB line");
        Assert.That(a.TailA, Is.EqualTo(25));
        Assert.That(a.TailB, Is.EqualTo(40));
        Assert.That(a.AnchorA!.Value.Northing, Is.EqualTo(10).Within(1e-9));
        Assert.That(a.AnchorB!.Value.Northing, Is.EqualTo(50).Within(1e-9));
        Assert.That(a.Body![0].Northing, Is.EqualTo(10).Within(1e-9));
        Assert.That(a.Points[0].Northing, Is.EqualTo(10 - 25).Within(1e-3));
        Assert.That(a.Points[1].Northing, Is.EqualTo(50 + 40).Within(1e-3));
    }

    [Test]
    public void TrackLinesTxt_StaysAogCompatible_NoExtraLines()
    {
        TrackFilesService.Save(_tempDir, new[] { AnchoredCurve() });
        var lines = File.ReadAllLines(Path.Combine(_tempDir, "TrackLines.txt"));
        // $TrackLines, name, heading, A, B, nudge, mode, visible, count, then exactly `count` points.
        Assert.That(lines[0], Is.EqualTo("$TrackLines"));
        int count = int.Parse(lines[8], System.Globalization.CultureInfo.InvariantCulture);
        Assert.That(count, Is.EqualTo(10));
        Assert.That(lines, Has.Length.EqualTo(9 + count));
    }

    [Test]
    public void SidecarEntry_WithMismatchedName_IsIgnored()
    {
        TrackFilesService.Save(_tempDir, new[] { AnchoredCurve("Cu 90°") });
        // Another program renamed the track in TrackLines.txt: the name no longer matches.
        var path = Path.Combine(_tempDir, "TrackLines.txt");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Cu 90°", "Renamed"));

        var loaded = TrackFilesService.Load(_tempDir);

        Assert.That(loaded[0].Name, Is.EqualTo("Renamed"));
        Assert.That(loaded[0].HasAnchors, Is.False, "no anchors attached to a track that no longer matches");
        Assert.That(loaded[0].Points, Has.Count.EqualTo(10), "points untouched");
    }

    [Test]
    public void SidecarEntry_WhoseTailsDontReproduceThePoints_IsIgnored()
    {
        TrackFilesService.Save(_tempDir, new[] { AnchoredCurve() });
        var metaPath = Path.Combine(_tempDir, TrackFilesService.MetaFileName);
        File.WriteAllText(metaPath, File.ReadAllText(metaPath).Replace("\"tailA\": 3", "\"tailA\": 5"));

        var loaded = TrackFilesService.Load(_tempDir);

        Assert.That(loaded[0].HasAnchors, Is.False);
    }

    [Test]
    public void CorruptSidecar_IsIgnored_AndRemovedWhenNoTrackHasAnchors()
    {
        TrackFilesService.Save(_tempDir, new[] { AnchoredCurve() });
        File.WriteAllText(Path.Combine(_tempDir, TrackFilesService.MetaFileName), "{ not json");
        Assert.DoesNotThrow(() => TrackFilesService.Load(_tempDir));
        Assert.That(TrackFilesService.Load(_tempDir)[0].HasAnchors, Is.False);

        // Saving a field with no anchored track removes the stale sidecar.
        TrackFilesService.Save(_tempDir, new[] { new MTrack { Name = "x", Type = TrackType.ABLine, Points = new List<Vec3> { new(0, 0, 0), new(1, 1, 0) } } });
        Assert.That(File.Exists(Path.Combine(_tempDir, TrackFilesService.MetaFileName)), Is.False);
    }

    // Verifier finding: the tail heading was re-derived from the recovered body's END
    // SEGMENT after load; with a sub-metre end segment (3-decimal file rounding) that
    // bearing is garbage, so a reload could swing the tail. The sidecar's saved anchor
    // headings are now stamped back onto the body ends and preferred on short segments.
    [Test]
    public void TailHeading_SurvivesReload_WhenEndSegmentIsTiny()
    {
        // Body heading east (π/2) but the LAST segment is only 1 mm long — a bearing
        // derived from it would be noise. The stored end heading must win.
        var body = new List<Vec3> { new(0, 0, Math.PI / 2), new(4, 0, Math.PI / 2), new(4.001, 0.0004, Math.PI / 2) };
        var t = new MTrack
        {
            Name = "Cu 90°", Type = TrackType.Curve, Body = body, AnchorA = body[0], AnchorB = body[^1],
            TailA = 0, TailB = 10, Points = TrackTails.BuildWithTails(body, 0, 10),
        };
        TrackFilesService.Save(_tempDir, new[] { t });
        var back = TrackFilesService.Load(_tempDir).Single();

        Assert.That(back.HasAnchors, Is.True);
        // Re-extend from the reloaded body: the B tail must still head EAST (π/2), not
        // along the 1 mm noise segment.
        double h = TrackTails.EndHeading(back.Body!, isA: false);
        Assert.That(h, Is.EqualTo(Math.PI / 2).Within(0.02), "tail heading after reload must be the saved east heading");
        var tip = back.Points[^1];
        Assert.That(tip.Easting, Is.EqualTo(back.AnchorB!.Value.Easting + 10).Within(0.05), "10 m tail points east of B");
        Assert.That(tip.Northing, Is.EqualTo(back.AnchorB!.Value.Northing).Within(0.05));
    }
}

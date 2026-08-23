// AgOpenWeb
// Copyright (C) 2024-2025 AgOpenWeb Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Linq;

using AgOpenWeb.Models;
using AgOpenWeb.Models.State;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using AgOpenWeb.Services.Interfaces;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.Input;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AgOpenWeb.ViewModels;

/// <summary>
/// Track management commands - AB lines, curves, guidance control, flags.
/// </summary>
public partial class MainViewModel
{
    /// <summary>
    /// Clear all applied-area coverage. Deletes the painted coverage + persisted
    /// Sections.txt and refreshes the worked-area stats — and ONLY that. Guidance,
    /// nudge/pathsAway and any active U-turn are deliberately left untouched: coverage
    /// is just painted area and is independent of the guidance line, so clearing it must
    /// not snap the magenta line back to the reference pass or orphan an in-progress turn.
    /// (The earlier version reset _trackGuidanceState + zeroed pathsAway/NudgeDistance,
    /// which forced exactly that — disable/re-enable-autosteer recovery dance.)
    /// </summary>
    public void DeleteAppliedAreaConfirmed()
    {
        _coverageMapService.ClearAll();

        if (State.Field.ActiveField != null)
        {
            var sectionsFile = System.IO.Path.Combine(State.Field.ActiveField.DirectoryPath, "Sections.txt");
            if (System.IO.File.Exists(sectionsFile))
            {
                try
                {
                    System.IO.File.Delete(sectionsFile);
                    _logger.LogDebug($"[Coverage] Deleted {sectionsFile}");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"[Coverage] Error deleting Sections.txt: {ex.Message}");
                }
            }
        }

        RefreshCoverageStatistics();
        StatusMessage = "Applied area deleted";
    }

    // The boundary line (Bnd. AB or Bnd. Curve) the two-tap tool just built — a reference INTO
    // SavedTracks — so Cancel in the tails phase can discard it (RemoteBoundarySegCancel).
    // Invalidated by SavedTracks membership (track deleted, or another field opened →
    // SavedTracks reloads with fresh Track objects).
    private Models.Track.Track? _bndSegTrack;
    // The track that was selected BEFORE the boundary line was created, so Cancel can put the
    // operator back where they were (P2.4).
    private Models.Track.Track? _bndSegPrevSelected;

    /// <summary>
    /// P1.1 dense, normalised pick ring. AgOpenGPS's two-tap boundary tools (FormABDraw) search
    /// <c>fenceLine</c>, which CFenceLine.FixFenceLine (CFenceLine.cs:48-114) has already
    /// densified to 1.1 / 2.2 / 3.3 m (by fence area; ×0.5 for inner rings) and given headings.
    /// Our rings are sparse after BoundaryResolution.Normalize + Clipper (a straight side can be
    /// a single 100–400 m segment). The taps themselves snap to the nearest point on the nearest
    /// EDGE (<see cref="NearestPointOnRing"/>) and are inserted into this ring as exact vertices
    /// (<see cref="InsertRingPoints"/>); the dense ring is what the curve walk copies between
    /// them, so the body has AOG's vertex density. Densification only ADDS midpoints on the
    /// segments (and thins runs closer than 0.9 × spacing, as AOG does), so every surviving
    /// sparse vertex is still at the same position. <paramref name="areaSqM"/> is the ring's OWN
    /// area (AOG uses each CBoundaryList's area, so a hole gets 1.1 × 0.5 regardless of the
    /// field size).
    /// </summary>
    private System.Collections.Generic.List<Models.Base.Vec3> BuildPickRing(
        System.Collections.Generic.IReadOnlyList<Models.Base.Vec3> ring, int ringIndex, double areaSqM)
    {
        var raw = new System.Collections.Generic.List<Models.Base.Vec3>(ring);
        if (raw.Count < 3) return raw;
        var dense = _fenceLineService.FixSpacing(raw, areaSqM, ringIndex, out _);
        return dense ?? raw;
    }

    /// <summary>Dense pick ring of a boundary polygon (raw fence), area = the polygon's own area.</summary>
    private System.Collections.Generic.List<Models.Base.Vec3> BuildPickRing(BoundaryPolygon ring, int ringIndex)
    {
        var raw = new System.Collections.Generic.List<Models.Base.Vec3>(ring.Points.Count);
        foreach (var p in ring.Points) raw.Add(new Models.Base.Vec3(p.Easting, p.Northing, 0));
        return BuildPickRing(raw, ringIndex, ring.AreaSquareMeters);
    }

    /// <summary>A tap snapped onto a ring: the closest point on the closest edge, the edge it
    /// lies on (vertex <c>Edge</c> → <c>Edge+1</c>, wrapping) and its parameter along it.</summary>
    private readonly record struct RingSnap(double E, double N, int Edge, double T, double DistSq);

    /// <summary>
    /// Edge snap (operator spec #3): the closest point ON the closest boundary EDGE to (e, n) —
    /// every edge (p, q) of the ring, the tap projected onto it with t clamped to [0, 1], the
    /// nearest projection wins (first wins on ties). Unlike nearest-VERTEX snapping this lands a
    /// tap 3 m off the middle of a 100 m side ON that side, never on a corner.
    /// </summary>
    private static RingSnap NearestPointOnRing(System.Collections.Generic.IReadOnlyList<Models.Base.Vec3> ring, double e, double n)
    {
        int cnt = ring.Count;
        var best = new RingSnap(e, n, -1, 0, double.MaxValue);
        if (cnt == 0) return best;
        if (cnt == 1) return new RingSnap(ring[0].Easting, ring[0].Northing, 0, 0, Sq(ring[0].Easting - e) + Sq(ring[0].Northing - n));
        for (int i = 0; i < cnt; i++)
        {
            var p = ring[i];
            var q = ring[(i + 1) % cnt];
            double ex = q.Easting - p.Easting, ey = q.Northing - p.Northing;
            double len2 = ex * ex + ey * ey;
            double t = len2 < 1e-12 ? 0 : ((e - p.Easting) * ex + (n - p.Northing) * ey) / len2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double pe = p.Easting + ex * t, pn = p.Northing + ey * t;
            double d = Sq(pe - e) + Sq(pn - n);
            if (d < best.DistSq) best = new RingSnap(pe, pn, i, t, d);
        }
        return best;
    }

    private static double Sq(double v) => v * v;

    /// <summary>
    /// Make the two snapped points exact ring vertices so the walk can start/end ON them: a snap
    /// that coincides with an existing vertex (within 1 mm) keeps that vertex; any other is
    /// spliced into its edge in t order (two snaps on one edge stay ordered). Returns the
    /// augmented ring and the final indices of A and B on it.
    /// </summary>
    private static System.Collections.Generic.List<Models.Base.Vec3> InsertRingPoints(
        System.Collections.Generic.IReadOnlyList<Models.Base.Vec3> ring, RingSnap a, RingSnap b, out int ia, out int ib)
    {
        int cnt = ring.Count;
        const double vertexTol = 1e-3; // m
        // (edge, t, point) of the snaps that need a NEW vertex; -1 index = existing vertex.
        var inserts = new System.Collections.Generic.List<(int edge, double t, double e, double n, int which)>();
        int[] vertexOf = { -1, -1 };
        var snaps = new[] { a, b };
        for (int k = 0; k < 2; k++)
        {
            var s = snaps[k];
            int i0 = s.Edge, i1 = (s.Edge + 1) % cnt;
            if (Sq(ring[i0].Easting - s.E) + Sq(ring[i0].Northing - s.N) <= vertexTol * vertexTol) vertexOf[k] = i0;
            else if (Sq(ring[i1].Easting - s.E) + Sq(ring[i1].Northing - s.N) <= vertexTol * vertexTol) vertexOf[k] = i1;
            else inserts.Add((s.Edge, s.T, s.E, s.N, k));
        }
        inserts.Sort((x, y) => x.edge != y.edge ? x.edge.CompareTo(y.edge) : x.t.CompareTo(y.t));

        var outRing = new System.Collections.Generic.List<Models.Base.Vec3>(cnt + inserts.Count);
        int[] finalIndex = { -1, -1 };
        int next = 0;
        for (int i = 0; i < cnt; i++)
        {
            for (int k = 0; k < 2; k++) if (vertexOf[k] == i) finalIndex[k] = outRing.Count;
            outRing.Add(ring[i]);
            while (next < inserts.Count && inserts[next].edge == i)
            {
                var ins = inserts[next++];
                var q = ring[(i + 1) % cnt];
                double h = Math.Atan2(q.Easting - ring[i].Easting, q.Northing - ring[i].Northing);
                if (h < 0) h += 2.0 * Math.PI;
                finalIndex[ins.which] = outRing.Count;
                outRing.Add(new Models.Base.Vec3(ins.e, ins.n, h));
            }
        }
        ia = finalIndex[0];
        ib = finalIndex[1];
        return outRing;
    }

    /// <summary>Two snaps closer than this are "the same point" and refused.</summary>
    private const double MinAnchorSeparationMeters = 0.01;

    /// <summary>
    /// Boundary curve from two tapped points (remote/web "Bnd. Curve"), after AgOpenGPS 6.8.6
    /// FormABDraw.BtnMakeCurve_Click (FormABDraw.cs:424-529) with the operator's body + tails
    /// model on top:
    ///  - A and B snap to the closest point on the closest EDGE of the pick ring — here the
    ///    outer boundary inset by half the tool width plus half the U-turn clearance (kept from
    ///    before: the pass sits inside the fence and the turn zone) — and become the track's
    ///    FIXED anchors. They never move afterwards.
    ///  - Direction rule (:426-446): the body follows the SHORTER arc by vertex count and is
    ///    ALWAYS walked in increasing ring index, i.e. the fence's winding direction. If
    ///    |start−end| > n/2 the short arc crosses the index seam: start is made the larger index
    ///    and the walk wraps; else start is the smaller. Tap order never changes travel direction.
    ///  - The body runs from anchor A to anchor B INCLUSIVE (the anchors are exact ring vertices,
    ///    so AOG's half-open "exclude the higher touched vertex" quirk no longer applies),
    ///    MakePointMinimumSpacing 1.6 m (no smoothing), central-difference headings, circular-mean
    ///    heading for the "Cu {deg}°" name (:484-515).
    ///  - Tails: the initial straight run-out past each anchor is what the fence raycast used to
    ///    give (fence crossing + 20 m, capped at AOG's 99 m, whole metres) and is stored as
    ///    TailA/TailB; A++/A−−/B++/B−− (RemoteAdjustTail) change only those lengths.
    /// </summary>
    public void RemoteCreateBoundaryCurveSegment(double aE, double aN, double bE, double bN)
    {
        // A new pick always supersedes the last one. Clear the cancel state BEFORE any early
        // return, so a failed creation can never leave a previous line silently cancellable
        // (track.boundarySegCancel would delete the wrong track).
        _bndSegTrack = null;
        _bndSegPrevSelected = null;

        var boundary = State.Field.CurrentBoundary?.OuterBoundary;
        if (boundary?.Points == null || boundary.Points.Count < 3)
        {
            StatusMessage = "Load a field with a boundary first";
            return;
        }
        // Offset the boundary inward by half the tool width PLUS half the U-turn clearance so the
        // curve sits a half-implement inside the fence AND clears the turn line (which is
        // UTurnDistanceFromBoundary inside) with margin — following it keeps the whole implement in
        // the field (#422) while the pass stays inside the cultivated/turn zone. Fall back to the
        // raw boundary if the offset fails.
        double insetDistance = ConfigStore.ActualToolWidth / 2.0
            + ConfigStore.Guidance.UTurnDistanceFromBoundary / 2.0;
        var rawVec2 = new System.Collections.Generic.List<Models.Base.Vec2>(boundary.Points.Count);
        foreach (var p in boundary.Points) rawVec2.Add(new Models.Base.Vec2(p.Easting, p.Northing));
        var offset = insetDistance > 0.05 ? _polygonOffsetService.CreateInwardOffset(rawVec2, insetDistance) : null;
        var source = (offset != null && offset.Count >= 3) ? offset : rawVec2;
        var sourceVec3 = new System.Collections.Generic.List<Models.Base.Vec3>(source.Count);
        foreach (var p in source) sourceVec3.Add(new Models.Base.Vec3(p.Easting, p.Northing, 0));
        // Densify THE RING THE CURVE LIVES ON (the inset one). Spacing keys off the outer
        // boundary's area, which is what AOG's fence ring would use (the inset ring's own area
        // differs from it only by a strip, never enough to cross a 20/40 ha band in practice).
        var ring = BuildPickRing(sourceVec3, 0, boundary.AreaSquareMeters);

        var snapA = NearestPointOnRing(ring, aE, aN);
        var snapB = NearestPointOnRing(ring, bE, bN);
        if (Sq(snapA.E - snapB.E) + Sq(snapA.N - snapB.N) < Sq(MinAnchorSeparationMeters))
        {
            StatusMessage = "Pick two different points on the boundary";
            return;
        }
        var walk = InsertRingPoints(ring, snapA, snapB, out int start, out int end);
        int n = walk.Count;

        // FormABDraw.cs:426-446 — shorter arc by vertex count, walked in fence winding.
        if (Math.Abs(start - end) > n * 0.5)
        {
            if (start < end) (end, start) = (start, end); // wraps through the seam
        }
        else
        {
            if (start > end) (end, start) = (start, end);
        }

        var body = BuildBoundarySegmentBody(walk, start, end, out double meanHeading);
        if (body == null) { StatusMessage = "Segment too short for a curve"; return; }
        var (tailA, tailB) = FenceTailLengths(body, BoundaryTailMaxMeters);
        var track = new Models.Track.Track
        {
            Name = CurveNameFromHeading(meanHeading),
            Type = Models.Track.TrackType.Curve,
            IsVisible = true,
            IsClosed = false,
            // Drive the boundary itself: this curve isn't worked in parallel passes, so the
            // guidance follows it directly (pass 0) instead of free-drive snapping to an inner pass.
            NoPassOffset = true,
            Body = body,
            AnchorA = body[0],
            AnchorB = body[^1],
            TailA = tailA,
            TailB = tailB,
            Points = TrackTails.BuildWithTails(body, tailA, tailB),
        };
        _bndSegPrevSelected = SelectedTrack; // remembered for RemoteBoundarySegCancel
        SavedTracks.Add(track);
        SelectedTrack = track;
        SaveTracksToFile();
        _bndSegTrack = track;
        StatusMessage = $"Created {track.Name} ({track.Points.Count} points, {insetDistance:F1} m inside fence)";
        _logger.LogDebug($"[BoundaryCurve] ring n={n} start={start} end={end} body={body.Count} tails A={tailA:F0} B={tailB:F0} points={track.Points.Count}");
    }

    /// <summary>AOG FormABDraw.cs:505-507: "Cu " + Math.Round(degrees, 1) in general (invariant)
    /// format + "°" — "Cu 270°", "Cu 45.5°", never a forced ".0" (same as the AB naming).</summary>
    private static string CurveNameFromHeading(double headingRad)
    {
        double deg = headingRad * 180.0 / Math.PI;
        return "Cu " + Math.Round(deg, 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "°";
    }

    /// <summary>
    /// Straight AB line from two tapped boundary points (remote/web "Bnd. AB"). Mirrors
    /// AgOpenGPS 6.8.6 FormABDraw.BtnMakeABLine_Click (FormABDraw.cs:531-580) plus its two-tap
    /// search (:625-661), with edge snapping and the body + tails model:
    ///  - A snaps to the closest point on the closest EDGE of ANY ring (outer + every inner),
    ///    recording the ring (:625-646); B snaps to the closest edge point of THAT SAME ring
    ///    (:647-661). Both are spliced into the ring's dense pick ring as exact vertices.
    ///  - AOG's index-ordering rule (:534-547) decides which point is A: when the short way
    ///    round does not cross the index seam (|start−end| ≤ n/2) start > end, else start < end.
    ///    So the SAME two taps give the SAME line whichever order they were tapped in.
    ///  - heading = atan2(fence[end] − fence[start]) wrapped to [0, 2π) (:550-553); name
    ///    "AB {deg:F1}°" (:570-571).
    /// Placement: AOG stores the line ON the fence, but its guidance references a swath EDGE
    /// (CABLine.cs:118 `distanceFromRefLine −= 0.5·(w−o)`, :140-142 `distAway += 0.5·(w−o)`)
    /// so pass 0 runs half a swath inside with the tool edge on the fence. Our guidance is
    /// centre-convention, so to get the same net result the line is shifted (w−o)/2 toward the
    /// field interior (outer ring: the side the ring's centroid lies on; inner ring: the side
    /// AWAY from the hole's centroid). The shifted points are the track's FIXED anchors (its
    /// 2-point body); the tails past them start at the fence-crossing length (+ 20 m, capped
    /// 99 m) and are adjusted by A++/A−−/B++/B−−. The persisted line is the two tail tips, so
    /// it stays a 2-point (infinite) AB line for guidance.
    /// </summary>
    public void RemoteCreateBoundaryAB(double aE, double aN, double bE, double bN)
    {
        _bndSegTrack = null;
        _bndSegPrevSelected = null;

        var bnd = State.Field.CurrentBoundary;
        var outer = bnd?.OuterBoundary;
        if (bnd == null || outer?.Points == null || outer.Points.Count < 3)
        {
            StatusMessage = "Load a field with a boundary first";
            return;
        }

        // bndList order: [0] = outer, then every inner ring (FormABDraw.cs:629 loops them all).
        var rings = new System.Collections.Generic.List<BoundaryPolygon> { outer };
        foreach (var inner in bnd.InnerBoundaries)
            if (inner?.Points != null && inner.Points.Count >= 3) rings.Add(inner);

        // P1.1: the dense pick ring (AOG's fenceLine density) is what the anchors are spliced
        // into; the edge projection itself gives the same point on the sparse or dense ring.
        var dense = new System.Collections.Generic.List<System.Collections.Generic.List<Models.Base.Vec3>>(rings.Count);
        for (int j = 0; j < rings.Count; j++) dense.Add(BuildPickRing(rings[j], j));

        // Tap A: closest edge point over every ring (:625-646).
        int ringIdx = 0;
        RingSnap snapA = default;
        double best = double.MaxValue;
        for (int j = 0; j < dense.Count; j++)
        {
            var s = NearestPointOnRing(dense[j], aE, aN);
            if (s.DistSq < best) { best = s.DistSq; ringIdx = j; snapA = s; }
        }

        // Tap B: only the ring A landed on (:647-661).
        var fence = dense[ringIdx];
        var snapB = NearestPointOnRing(fence, bE, bN);
        if (Sq(snapA.E - snapB.E) + Sq(snapA.N - snapB.N) < Sq(MinAnchorSeparationMeters))
        {
            StatusMessage = "Pick two different points on the boundary";
            return;
        }
        var walk = InsertRingPoints(fence, snapA, snapB, out int start, out int end);
        int n = walk.Count;

        // Ordering rule (:534-547): index order, not tap order, decides which point is A.
        if (Math.Abs(start - end) <= n * 0.5)
        {
            if (start < end) (end, start) = (start, end);
        }
        else
        {
            if (start > end) (end, start) = (start, end);
        }

        var fa = walk[start];
        var fb = walk[end];
        double heading = Math.Atan2(fb.Easting - fa.Easting, fb.Northing - fa.Northing);
        if (heading < 0) heading += 2.0 * Math.PI;
        double sinH = Math.Sin(heading), cosH = Math.Cos(heading);

        // Interior side: sign of cross(dir, centroid − A). Positive = centroid left of travel.
        // (Centroid of the source polygon — densifying only adds points on its edges.)
        var (cE, cN) = RingCentroid(rings[ringIdx].Points);
        double cross = sinH * (cN - fa.Northing) - cosH * (cE - fa.Easting);
        bool shiftLeft = ringIdx == 0 ? cross >= 0 : cross < 0; // inner ring: away from the hole
        // Left-perpendicular of (sinH, cosH) is (−cosH, sinH); right is (cosH, −sinH).
        double halfSwath = (ConfigStore.ActualToolWidth - ConfigStore.Tool.Overlap) * 0.5;
        if (halfSwath < 0) halfSwath = 0;
        double offE = (shiftLeft ? -cosH : cosH) * halfSwath;
        double offN = (shiftLeft ? sinH : -sinH) * halfSwath;

        var body = new List<Vec3>
        {
            new(fa.Easting + offE, fa.Northing + offN, heading),
            new(fb.Easting + offE, fb.Northing + offN, heading),
        };
        var (tailA, tailB) = FenceTailLengths(body, BoundaryTailMaxMeters);

        double deg = heading * 180.0 / Math.PI;
        // AOG FormABDraw.cs:570-571 uses plain ToString (general format): "AB 270°", "AB 45.5°" — never a forced ".0".
        string degText = Math.Round(deg, 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var track = new Models.Track.Track
        {
            Name = "AB " + degText + "°",
            Type = Models.Track.TrackType.ABLine,
            IsVisible = true,
            IsClosed = false,
            NoPassOffset = false, // normal parallel passes, unlike the driven-as-is boundary curve
            Body = body,
            AnchorA = body[0],
            AnchorB = body[1],
            TailA = tailA,
            TailB = tailB,
            Points = TrackTails.BuildWithTails(body, tailA, tailB, endpointsOnly: true),
        };
        _bndSegPrevSelected = SelectedTrack; // remembered for RemoteBoundarySegCancel
        SavedTracks.Add(track);
        SelectedTrack = track; // disengages autosteer if engaged — intended for a new reference line
        SaveTracksToFile();
        _bndSegTrack = track;
        string ringName = ringIdx == 0 ? "outer" : $"inner {ringIdx}";
        StatusMessage = $"Created boundary AB {degText}° ({ringName} ring)";
        _logger.LogDebug($"[BoundaryAB] ring={ringIdx} start={start} end={end} heading={deg:F1}° shift={halfSwath:F2}m {(shiftLeft ? "left" : "right")} tails A={tailA:F0} B={tailB:F0}");
    }

    /// <summary>Area-weighted (shoelace) centroid of a closed ring; falls back to the vertex
    /// mean for a degenerate (zero-area) ring.</summary>
    private static (double e, double n) RingCentroid(System.Collections.Generic.List<BoundaryPoint> pts)
    {
        double area2 = 0, cE = 0, cN = 0;
        int n = pts.Count;
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            var q = pts[(i + 1) % n];
            double w = p.Easting * q.Northing - q.Easting * p.Northing;
            area2 += w;
            cE += (p.Easting + q.Easting) * w;
            cN += (p.Northing + q.Northing) * w;
        }
        if (Math.Abs(area2) < 1e-9)
        {
            double mE = 0, mN = 0;
            foreach (var p in pts) { mE += p.Easting; mN += p.Northing; }
            return (mE / n, mN / n);
        }
        return (cE / (3.0 * area2), cN / (3.0 * area2));
    }

    /// <summary>
    /// A++ / A−− / B++ / B−− (the operator's names) on the SELECTED boundary-derived track:
    /// lengthen or shorten ONLY the straight tail protruding past anchor A or B by
    /// <paramref name="deltaMeters"/> (the web client sends ±<see cref="TrackTails.StepMeters"/>).
    /// The anchors and the body between them never move. The tail clamps at 0 — the line then
    /// ends exactly at the anchor — and shortening a zero tail is a quiet no-op (no rebuild, no
    /// save, just a status). Points are rebuilt from the fixed body with
    /// <see cref="TrackTails.BuildWithTails"/>, saved (sidecar carries the anchors/tails) and the
    /// guidance pipeline re-pushed so it re-searches the changed line. Tier-2 (mutates the
    /// active guidance line).
    /// </summary>
    public void RemoteAdjustTail(bool isA, double deltaMeters)
    {
        var track = SelectedTrack;
        if (track == null || !track.HasAnchors)
        {
            StatusMessage = "Select a boundary AB or curve";
            return;
        }
        if (double.IsNaN(deltaMeters) || double.IsInfinity(deltaMeters) || deltaMeters == 0) return;
        string endName = isA ? "A" : "B";
        double tail = isA ? track.TailA : track.TailB;
        if (deltaMeters < 0 && tail <= 1e-9)
        {
            StatusMessage = $"{endName} end already at point {endName}";
            return;
        }
        double newTail = Math.Max(0, tail + deltaMeters);
        if (newTail > BoundaryTailMaxAdjustMeters) newTail = BoundaryTailMaxAdjustMeters;
        if (Math.Abs(newTail - tail) < 1e-9)
        {
            StatusMessage = $"{endName} end at maximum length";
            return;
        }
        if (isA) track.TailA = newTail; else track.TailB = newTail;

        // Build a NEW list and swap it in (the pipeline + scene projector read track.Points on
        // their own threads; mutating the live list in place would race them).
        track.Points = TrackTails.BuildWithTails(track.Body!, track.TailA, track.TailB,
            endpointsOnly: track.Type == Models.Track.TrackType.ABLine);
        SaveTracksToFile();
        // SelectedTrack is unchanged (same reference), so the setter's sync doesn't fire —
        // push the pipeline explicitly so guidance re-searches the changed line.
        SyncGuidanceStateToPipeline();
        StatusMessage = newTail <= 1e-9
            ? $"{endName} end now at point {endName}"
            : $"{endName} end {(deltaMeters > 0 ? "lengthened" : "shortened")} to {newTail:F0} m past point {endName}";
        _logger.LogDebug($"[Tail] '{track.Name}' {endName} tail {tail:F0} → {newTail:F0} m → {track.Points.Count} points");
    }

    /// <summary>
    /// Cancel in the Bnd. AB/Curve tails phase (P2.4): discard the line the two-tap tool just
    /// made and put the selection back on whatever was selected before it (if that track still
    /// exists), so a mis-tapped line never lingers as a saved track. "Done" is client-only —
    /// the line was committed at creation, so Done has nothing to send.
    /// </summary>
    public void RemoteBoundarySegCancel()
    {
        var track = _bndSegTrack;
        if (track == null || !SavedTracks.Contains(track))
        {
            _bndSegTrack = null;
            _bndSegPrevSelected = null;
            StatusMessage = "No boundary line to cancel";
            return;
        }
        var prev = _bndSegPrevSelected;
        var restore = prev != null && SavedTracks.Contains(prev) ? prev : null;
        // Only move the selection when it still sits on the discarded line (or nothing); if
        // the operator picked some other track meanwhile, leave that choice alone.
        if (SelectedTrack == null || ReferenceEquals(SelectedTrack, track))
            SelectedTrack = restore;
        SavedTracks.Remove(track);
        SaveTracksToFile();
        _bndSegTrack = null;
        _bndSegPrevSelected = null;
        StatusMessage = restore != null
            ? $"Boundary line discarded — back on '{restore.Name}'"
            : "Boundary line discarded";
    }

    /// <summary>
    /// Ring range [start, end] walked in increasing index (wrapping through 0 when start > end)
    /// → the fixed BODY of a boundary curve, per AgOpenGPS FormABDraw.BtnMakeCurve_Click
    /// (FormABDraw.cs:448-499) — P1.3, minus the tails:
    ///  1. copy the ring vertices start..end INCLUSIVE (both are the exact snapped anchors);
    ///  2. at least two vertices and ~2 m of length required, else null (caller reports it);
    ///  3. CABCurve.MakePointMinimumSpacing 1.6 m — midpoint densification, NO smoothing (the
    ///     old Chaikin pass cut ≈9 m off a 90° corner on 50 m legs);
    ///  4. CABCurve.CalculateHeadings — central difference (ends = the end segments' bearings,
    ///     which is what the tails run along);
    ///  5. <paramref name="meanHeading"/> = circular mean of the point headings (:484-494),
    ///     taken BEFORE the tails like AOG, for the "Cu {deg}°" name.
    /// </summary>
    private static List<Vec3>? BuildBoundarySegmentBody(
        System.Collections.Generic.List<Models.Base.Vec3> ring, int start, int end, out double meanHeading)
    {
        meanHeading = 0;
        int n = ring.Count;
        if (n < 2 || start == end) return null;
        var seg = new List<Vec3>();
        for (int i = start; ; i = (i + 1) % n)
        {
            var p = ring[i];
            seg.Add(new Vec3(p.Easting, p.Northing, p.Heading));
            if (i == end || seg.Count > n) break;
        }
        if (seg.Count < 2) return null;
        double len = 0;
        for (int i = 1; i < seg.Count; i++)
            len += Math.Sqrt(Sq(seg[i].Easting - seg[i - 1].Easting) + Sq(seg[i].Northing - seg[i - 1].Northing));
        if (len < BoundaryCurveMinBodyMeters) return null;
        var dense = Models.Guidance.CurveProcessing.MakePointMinimumSpacing(seg, BoundaryCurveMaxSpacing);
        Models.Guidance.CurveProcessing.CalculateCentralHeadings(dense);
        meanHeading = Models.Guidance.CurveProcessing.ComputeAverageHeading(dense);
        return dense;
    }

    /// <summary>
    /// Initial tail lengths for a boundary-derived body: what ExtendCurvePastBoundary used to
    /// produce — a raycast from each end along the end heading (A backwards, B forwards) to the
    /// outer fence, plus <see cref="BoundaryTailMarginMeters"/>, at least the margin when nothing
    /// is hit, capped at <paramref name="maxTail"/> (AOG's 99 m, which also stops the
    /// run-along-the-fence case — tangent parallel to the fence → crossing hundreds of metres
    /// away — producing a giant tail). Rounded UP to whole metres so the tail points are 1 m
    /// apart end to end.
    /// </summary>
    private (double tailA, double tailB) FenceTailLengths(IReadOnlyList<Vec3> body, double maxTail)
    {
        double hA = TrackTails.EndHeading(body, true);
        double hB = TrackTails.EndHeading(body, false);
        var a = body[0];
        var b = body[body.Count - 1];
        double tailA = BoundaryTailMarginMeters, tailB = BoundaryTailMarginMeters;
        double hitA = RaycastToOuterFence(a.Easting, a.Northing, -Math.Sin(hA), -Math.Cos(hA));
        double hitB = RaycastToOuterFence(b.Easting, b.Northing, Math.Sin(hB), Math.Cos(hB));
        if (hitA > 0) tailA = Math.Max(tailA, hitA + BoundaryTailMarginMeters);
        if (hitB > 0) tailB = Math.Max(tailB, hitB + BoundaryTailMarginMeters);
        tailA = Math.Ceiling(Math.Min(tailA, maxTail) - 1e-9);
        tailB = Math.Ceiling(Math.Min(tailB, maxTail) - 1e-9);
        return (tailA, tailB);
    }

    /// <summary>
    /// Distance along the ray (origin, unit dir) to the FARTHEST crossing of the outer boundary,
    /// or −1 when the ray hits nothing / there is no valid boundary. Parametric segment
    /// intersection over every fence edge (the raycast every "extend past the boundary" creator
    /// shares): the farthest hit is kept so a line that starts inside and crosses the fence
    /// more than once still ends outside the field.
    /// </summary>
    private double RaycastToOuterFence(double oE, double oN, double dx, double dy)
    {
        var outer = State.Field.CurrentBoundary?.OuterBoundary;
        if (outer == null || !outer.IsValid) return -1;
        var boundaryPts = outer.Points;
        int count = boundaryPts.Count;
        double best = -1;
        for (int i = 0; i < count; i++)
        {
            var p1 = boundaryPts[i];
            var p2 = boundaryPts[(i + 1) % count];
            double ex = p2.Easting - p1.Easting;
            double ey = p2.Northing - p1.Northing;
            double denom = dx * ey - dy * ex;
            if (Math.Abs(denom) < 0.0001) continue;
            double t = ((p1.Easting - oE) * ey - (p1.Northing - oN) * ex) / denom;
            double u = ((p1.Easting - oE) * dy - (p1.Northing - oN) * dx) / denom;
            if (t > 0 && u >= 0 && u <= 1 && t > best) best = t;
        }
        return best;
    }

    /// <summary>AOG CABCurve.MakePointMinimumSpacing argument in BtnMakeCurve_Click (FormABDraw.cs:480).</summary>
    private const double BoundaryCurveMaxSpacing = 1.6;
    /// <summary>Shortest body the two-tap curve accepts (two taps closer than this make no curve).</summary>
    private const double BoundaryCurveMinBodyMeters = 2.0;
    /// <summary>Margin past the fence crossing for an initial tail (the "extend past the boundary" default).</summary>
    private const double BoundaryTailMarginMeters = 20.0;
    /// <summary>AOG CABCurve.AddFirstLastPoints tail length with a boundary (CABCurve.cs:1610-1626: 1..99 m):
    /// cap on an INITIAL tail.</summary>
    private const double BoundaryTailMaxMeters = 99.0;
    /// <summary>Cap on a tail the operator lengthens by hand (sanity clamp on repeated A++/B++).</summary>
    private const double BoundaryTailMaxAdjustMeters = 999.0;

    private void InitializeTrackCommands()
    {
        // AB Line Guidance Commands - Bottom Bar
        SnapLeftCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null)
            {
                StatusMessage = "No track selected";
                return;
            }
            _intents.RequestGuidanceSnap(left: true);
            StatusMessage = "Snapped left";
        });

        SnapRightCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null)
            {
                StatusMessage = "No track selected";
                return;
            }
            _intents.RequestGuidanceSnap(left: false);
            StatusMessage = "Snapped right";
        });

        StopGuidanceCommand = new RelayCommand(() =>
        {
            StatusMessage = "Guidance Stopped";
        });

        UTurnCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null)
            {
                StatusMessage = "No track selected for U-turn";
                return;
            }

            if (!IsAutoSteerEngaged)
            {
                StatusMessage = "Enable autosteer before triggering U-turn";
                return;
            }

            if (!HasBoundary && !HasHeadland)
            {
                _logger.LogDebug("[UTurn] No boundary/headland, triggering manual U-turn left");
            }

            TriggerManualYouTurnLeft();
        });

        // AB Line Guidance Commands - Flyout Menu
        ShowTracksDialogCommand = new RelayCommand(() =>
        {
            State.UI.ShowDialog(DialogType.Tracks);
        });

        CloseTracksDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        // Track management commands
        DeleteSelectedTrackCommand = new RelayCommand(() =>
        {
            if (SelectedTrack != null)
            {
                SavedTracks.Remove(SelectedTrack);
                SelectedTrack = null;
                SaveTracksToFile();
                StatusMessage = "Track deleted";
            }
        });

        DeleteAllTracksCommand = new RelayCommand(() =>
        {
            if (SavedTracks.Count == 0)
            {
                StatusMessage = "No tracks to delete";
                return;
            }
            ShowConfirmationDialog(
                "Delete All Tracks",
                $"Delete all {SavedTracks.Count} tracks? This cannot be undone.",
                DeleteAllTracksRemote);
        });

        SwapABPointsCommand = new RelayCommand(() =>
        {
            if (SelectedTrack is { HasAnchors: true })
            {
                // A boundary line's A and B are fixed anchors; reversing Points would leave
                // the anchors/body/tails inconsistent. The line's direction is the fence's.
                StatusMessage = "Boundary line: A and B are fixed — use A++/A−−/B++/B−− for the ends";
                return;
            }
            if (SelectedTrack != null && SelectedTrack.Points.Count >= 2)
            {
                SelectedTrack.Points.Reverse();
                StatusMessage = $"Swapped A/B points for {SelectedTrack.Name}";
            }
        });

        SelectTrackAsActiveCommand = new RelayCommand(() =>
        {
            if (SelectedTrack != null)
            {
                if (SelectedTrack.IsActive)
                {
                    SelectedTrack = null;
                    StatusMessage = "Track deactivated";
                }
                else
                {
                    StatusMessage = $"Activated track: {SelectedTrack.Name}";
                }
                State.UI.CloseDialog();
            }
        });

        // Quick AB Selector
        ShowQuickABSelectorCommand = new RelayCommand(() =>
        {
            State.UI.ShowDialog(DialogType.QuickABSelector);
        });

        CloseQuickABSelectorCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        ShowDrawABDialogCommand = new RelayCommand(() =>
        {
            State.UI.ShowDialog(DialogType.DrawAB);
        });

        CloseDrawABDialogCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        StartNewABLineCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            CurrentABCreationMode = ABCreationMode.DriveAB;
            CurrentABPointStep = ABPointStep.SettingPointA;
            PendingPointA = null;
            StatusMessage = "Drive-in AB Line: tap to set Point A at current position";
        });

        StartNewABCurveCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            CurrentABCreationMode = ABCreationMode.Curve;
            _recordedCurvePoints.Clear();
            _lastCurvePoint = null;

            if (Easting != 0 || Northing != 0)
            {
                var headingRadians = Heading * Math.PI / 180.0;
                var firstPoint = new Vec3(Easting, Northing, headingRadians);
                _recordedCurvePoints.Add(firstPoint);
                _lastCurvePoint = firstPoint;

                var displayPoints = _recordedCurvePoints.Select(p => (p.Easting, p.Northing)).ToList();
                _mapService.SetRecordingPoints(displayPoints);
            }

            StatusMessage = $"Curve recording started ({_recordedCurvePoints.Count} pts) - drive along path, tap when done";
            OnPropertyChanged(nameof(IsRecordingCurve));
            OnPropertyChanged(nameof(RecordedCurvePointCount));
            OnPropertyChanged(nameof(ABCreationInstructions));
        });

        StartAPlusLineCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();

            if (Easting == 0 && Northing == 0)
            {
                StatusMessage = "No GPS position - cannot create A+ line";
                return;
            }

            double headingRad = Heading * Math.PI / 180.0;
            var pointA = new Vec3(Easting, Northing, headingRad);
            // Project Point B 100m ahead along current heading
            var pointB = new Vec3(
                Easting + Math.Sin(headingRad) * 100.0,
                Northing + Math.Cos(headingRad) * 100.0,
                headingRad);

            var track = Track.FromABLine($"A+ {DateTime.Now:HH:mm}", pointA, pointB);
            SavedTracks.Add(track);
            SelectedTrack = track;
            _mapService.SetActiveTrack(track);

            CurrentABCreationMode = ABCreationMode.None;
            StatusMessage = $"A+ line '{track.Name}' created at heading {Heading:F1}";
        });

        StartDriveABCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            CurrentABCreationMode = ABCreationMode.DriveAB;
            CurrentABPointStep = ABPointStep.SettingPointA;
            PendingPointA = null;
            StatusMessage = ABCreationInstructions;
        });

        StartCurveRecordingCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            CurrentABCreationMode = ABCreationMode.Curve;
            _recordedCurvePoints.Clear();
            _lastCurvePoint = null;

            // Capture first point immediately at current position
            if (Easting != 0 || Northing != 0)
            {
                var headingRadians = Heading * Math.PI / 180.0;
                var firstPoint = new Vec3(Easting, Northing, headingRadians);
                _recordedCurvePoints.Add(firstPoint);
                _lastCurvePoint = firstPoint;

                // Show first point on map
                var displayPoints = _recordedCurvePoints.Select(p => (p.Easting, p.Northing)).ToList();
                _mapService.SetRecordingPoints(displayPoints);
            }

            StatusMessage = $"Curve recording started ({_recordedCurvePoints.Count} pts) - drive along path, tap when done";
            OnPropertyChanged(nameof(IsRecordingCurve));
            OnPropertyChanged(nameof(RecordedCurvePointCount));
            OnPropertyChanged(nameof(ABCreationInstructions));
        });

        FinishCurveRecordingCommand = new RelayCommand(() =>
        {
            if (CurrentABCreationMode != ABCreationMode.Curve)
            {
                return;
            }

            // Need at least 3 points for a valid curve
            if (_recordedCurvePoints.Count < 3)
            {
                StatusMessage = $"Need at least 3 points for a curve (have {_recordedCurvePoints.Count})";
                return;
            }

            // Deactivate all existing tracks before adding the new one
            foreach (var existingTrack in SavedTracks)
            {
                existingTrack.IsActive = false;
            }

            // Extend curve ends past boundary for U-turn detection
            var extendedPoints = ExtendCurvePastBoundary(_recordedCurvePoints);

            // Create the curve track
            var newTrack = Track.FromCurve(
                $"Curve {DateTime.Now:HH:mm:ss}",
                extendedPoints,
                isClosed: false);

            // Add track and select it as active (SelectedTrack setter handles IsActive and map update)
            SavedTracks.Add(newTrack);
            SelectedTrack = newTrack;
            SaveTracksToFile();

            StatusMessage = $"Created curve with {_recordedCurvePoints.Count} points: {newTrack.Name}";
            _logger.LogDebug($"[Curve] Created curve track: {newTrack.Name} with {_recordedCurvePoints.Count} points");

            // Clear recording display from map
            _mapService.ClearRecordingPoints();

            // Reset state
            CurrentABCreationMode = ABCreationMode.None;
            _recordedCurvePoints.Clear();
            _lastCurvePoint = null;
            OnPropertyChanged(nameof(IsRecordingCurve));
            OnPropertyChanged(nameof(RecordedCurvePointCount));
        });

        StartDrawABModeCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            CurrentABCreationMode = ABCreationMode.DrawAB;
            CurrentABPointStep = ABPointStep.SettingPointA;
            PendingPointA = null;
            StatusMessage = ABCreationInstructions;
        });

        StartDrawCurveModeCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
            CurrentABCreationMode = ABCreationMode.DrawCurve;
            _drawnCurvePoints.Clear();
            StatusMessage = ABCreationInstructions;
            OnPropertyChanged(nameof(IsDrawingCurve));
            OnPropertyChanged(nameof(DrawnCurvePointCount));
            OnPropertyChanged(nameof(ABCreationInstructions));
        });

        FinishDrawCurveCommand = new RelayCommand(() =>
        {
            if (CurrentABCreationMode != ABCreationMode.DrawCurve)
            {
                return;
            }

            // Need at least 2 points for a valid track
            if (_drawnCurvePoints.Count < 2)
            {
                StatusMessage = $"Need at least 2 points (have {_drawnCurvePoints.Count})";
                return;
            }

            // Clear drawing display from map
            _mapService.ClearRecordingPoints();

            // Deactivate all existing tracks before adding the new one
            foreach (var existingTrack in SavedTracks)
            {
                existingTrack.IsActive = false;
            }

            Track newTrack;

            // If only 2 points, create a straight AB line
            if (_drawnCurvePoints.Count == 2)
            {
                var (extendedA, extendedB) = ExtendABLinePastBoundary(_drawnCurvePoints[0], _drawnCurvePoints[1]);
                newTrack = Track.FromABLine(
                    $"AB_{extendedA.Heading * 180.0 / Math.PI:F1} {DateTime.Now:HH:mm:ss}",
                    extendedA,
                    extendedB);
                StatusMessage = $"Created AB line: {newTrack.Name}";
                _logger.LogDebug($"[DrawCurve] Created AB line from 2 points: {newTrack.Name}");
            }
            else
            {
                // 3+ points - smooth the curve using Catmull-Rom spline, then extend past boundary
                var smoothedPoints = Models.Guidance.CurveProcessing.SmoothWithCatmullRom(_drawnCurvePoints, pointsPerSegment: 10);
                smoothedPoints = Models.Guidance.CurveProcessing.CalculateHeadings(smoothedPoints);
                var extendedPoints = ExtendCurvePastBoundary(smoothedPoints);
                newTrack = Track.FromCurve(
                    $"DrawnCurve {DateTime.Now:HH:mm:ss}",
                    extendedPoints,
                    isClosed: false);
                StatusMessage = $"Created smooth curve from {_drawnCurvePoints.Count} control points: {newTrack.Name}";
                _logger.LogDebug($"[DrawCurve] Created smooth curve track: {newTrack.Name} from {_drawnCurvePoints.Count} control points → {extendedPoints.Count} smoothed points");
            }

            // Add track and select it as active (SelectedTrack setter handles IsActive and map update)
            SavedTracks.Add(newTrack);
            SelectedTrack = newTrack;
            SaveTracksToFile();

            // Reset state
            CurrentABCreationMode = ABCreationMode.None;
            _drawnCurvePoints.Clear();
            OnPropertyChanged(nameof(IsDrawingCurve));
            OnPropertyChanged(nameof(DrawnCurvePointCount));
        });

        UndoLastDrawnPointCommand = new RelayCommand(() =>
        {
            if (CurrentABCreationMode != ABCreationMode.DrawCurve || _drawnCurvePoints.Count == 0)
            {
                return;
            }

            _drawnCurvePoints.RemoveAt(_drawnCurvePoints.Count - 1);

            // Update map display
            if (_drawnCurvePoints.Count > 0)
            {
                var displayPoints = _drawnCurvePoints.Select(p => (p.Easting, p.Northing)).ToList();
                _mapService.SetRecordingPoints(displayPoints);
            }
            else
            {
                _mapService.ClearRecordingPoints();
            }

            OnPropertyChanged(nameof(DrawnCurvePointCount));
            OnPropertyChanged(nameof(ABCreationInstructions));
            StatusMessage = $"Removed last point ({_drawnCurvePoints.Count} points remaining)";
        });

        SetABPointCommand = new RelayCommand<object?>(param =>
        {
            _logger.LogDebug($"[SetABPointCommand] Called with param={param?.GetType().Name ?? "null"}, Mode={CurrentABCreationMode}, Step={CurrentABPointStep}");

            if (CurrentABCreationMode == ABCreationMode.None)
            {
                _logger.LogDebug("[SetABPointCommand] Mode is None, returning");
                return;
            }

            // Handle curve mode - tap to finish recording
            if (CurrentABCreationMode == ABCreationMode.Curve)
            {
                _logger.LogDebug($"[SetABPointCommand] Curve mode - finishing with {_recordedCurvePoints.Count} points");
                FinishCurveRecordingCommand?.Execute(null);
                return;
            }

            // Handle draw curve mode - tap to add points
            if (CurrentABCreationMode == ABCreationMode.DrawCurve && param is Position curveMapPos)
            {
                // Calculate heading from previous point (or use 0 for first point)
                double heading = 0;
                if (_drawnCurvePoints.Count > 0)
                {
                    var lastPt = _drawnCurvePoints[^1];
                    heading = Math.Atan2(curveMapPos.Easting - lastPt.Easting, curveMapPos.Northing - lastPt.Northing);
                }

                var point = new Vec3(curveMapPos.Easting, curveMapPos.Northing, heading);
                _drawnCurvePoints.Add(point);

                // Update map display
                var displayPoints = _drawnCurvePoints.Select(p => (p.Easting, p.Northing)).ToList();
                _mapService.SetRecordingPoints(displayPoints);

                OnPropertyChanged(nameof(DrawnCurvePointCount));
                OnPropertyChanged(nameof(ABCreationInstructions));
                StatusMessage = $"Added point {_drawnCurvePoints.Count} - tap more points or Finish";
                _logger.LogDebug($"[SetABPointCommand] DrawCurve - Added point {_drawnCurvePoints.Count}: E={curveMapPos.Easting:F2}, N={curveMapPos.Northing:F2}");
                return;
            }

            Position pointToSet;

            if (CurrentABCreationMode == ABCreationMode.DriveAB)
            {
                pointToSet = new Position
                {
                    Latitude = Latitude,
                    Longitude = Longitude,
                    Easting = Easting,
                    Northing = Northing,
                    Heading = Heading
                };
                _logger.LogDebug($"[SetABPointCommand] DriveAB - GPS position: E={Easting:F2}, N={Northing:F2}");
            }
            else if (CurrentABCreationMode == ABCreationMode.DrawAB && param is Position mapPos)
            {
                pointToSet = mapPos;
                _logger.LogDebug($"[SetABPointCommand] DrawAB - Map position: E={mapPos.Easting:F2}, N={mapPos.Northing:F2}");
            }
            else
            {
                _logger.LogDebug($"[SetABPointCommand] Invalid state - returning");
                return;
            }

            if (CurrentABPointStep == ABPointStep.SettingPointA)
            {
                PendingPointA = pointToSet;
                CurrentABPointStep = ABPointStep.SettingPointB;
                StatusMessage = ABCreationInstructions;
                _logger.LogDebug($"[SetABPointCommand] Set Point A: E={pointToSet.Easting:F2}, N={pointToSet.Northing:F2}");
            }
            else if (CurrentABPointStep == ABPointStep.SettingPointB)
            {
                if (PendingPointA != null)
                {
                    // Deactivate all existing tracks before adding the new one
                    foreach (var existingTrack in SavedTracks)
                    {
                        existingTrack.IsActive = false;
                    }

                    var heading = CalculateHeading(PendingPointA, pointToSet);
                    var headingRadians = heading * Math.PI / 180.0;

                    // Extend AB Line points past boundary for proper U-turn detection
                    var (extendedA, extendedB) = ExtendABLinePastBoundary(
                        new Vec3(PendingPointA.Easting, PendingPointA.Northing, headingRadians),
                        new Vec3(pointToSet.Easting, pointToSet.Northing, headingRadians));

                    var newTrack = Track.FromABLine(
                        $"AB_{heading:F1} {DateTime.Now:HH:mm:ss}",
                        extendedA,
                        extendedB);

                    // Add track and select it as active (SelectedTrack setter handles IsActive and map update)
                    SavedTracks.Add(newTrack);
                    SelectedTrack = newTrack;
                    SaveTracksToFile();
                    StatusMessage = $"Created AB line: {newTrack.Name} ({heading:F1})";
                    _logger.LogDebug($"[SetABPointCommand] Created AB Line: {newTrack.Name}");

                    CurrentABCreationMode = ABCreationMode.None;
                    CurrentABPointStep = ABPointStep.None;
                    PendingPointA = null;
                }
            }
        });

        CancelABCreationCommand = new RelayCommand(() =>
        {
            // Clean up curve recording state if active
            if (CurrentABCreationMode == ABCreationMode.Curve)
            {
                _mapService.ClearRecordingPoints(); // Clear recording display from map
                _recordedCurvePoints.Clear();
                _lastCurvePoint = null;
                OnPropertyChanged(nameof(IsRecordingCurve));
                OnPropertyChanged(nameof(RecordedCurvePointCount));
            }

            // Clean up draw curve state if active
            if (CurrentABCreationMode == ABCreationMode.DrawCurve)
            {
                _mapService.ClearRecordingPoints(); // Clear drawing display from map
                _drawnCurvePoints.Clear();
                OnPropertyChanged(nameof(IsDrawingCurve));
                OnPropertyChanged(nameof(DrawnCurvePointCount));
            }

            CurrentABCreationMode = ABCreationMode.None;
            CurrentABPointStep = ABPointStep.None;
            PendingPointA = null;
            StatusMessage = "AB line/curve creation cancelled";
        });

        CycleABLinesCommand = new RelayCommand(() =>
        {
            if (SavedTracks.Count == 0)
            {
                StatusMessage = "No tracks to cycle";
                return;
            }

            int currentIndex = SelectedTrack != null ? SavedTracks.IndexOf(SelectedTrack) : -1;
            int nextIndex = (currentIndex + 1) % SavedTracks.Count;
            SelectedTrack = SavedTracks[nextIndex];
            StatusMessage = $"Active track: {SelectedTrack.Name}";
        });

        SmoothABLineCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null)
            {
                StatusMessage = "No track selected";
                return;
            }
            if (SelectedTrack.HasAnchors)
            {
                // The body between the fixed anchors IS the fence segment; smoothing would
                // rewrite it and detach it from the anchors/tails model.
                StatusMessage = "Boundary line: the path between A and B is fixed and can't be smoothed";
                return;
            }
            if (SelectedTrack.IsABLine)
            {
                StatusMessage = "Cannot smooth AB lines (only 2 points)";
                return;
            }
            if (SelectedTrack.Points.Count < 5)
            {
                StatusMessage = "Too few points to smooth (need at least 5)";
                return;
            }

            int beforeCount = SelectedTrack.Points.Count;
            var smoothed = Models.Guidance.CurveProcessing.SmoothWithCatmullRom(SelectedTrack.Points, 4);
            smoothed = Models.Guidance.CurveProcessing.CalculateHeadings(smoothed);
            SelectedTrack.Points = smoothed;

            // Invalidate guidance state so it recalculates from the new curve
            _trackGuidanceState = null;
            _mapService.SetActiveTrack(SelectedTrack);
            SaveTracksToFile();

            StatusMessage = $"Smoothed '{SelectedTrack.Name}': {beforeCount} -> {smoothed.Count} points";
        });

        // Nudge commands
        NudgeLeftCommand = new RelayCommand(() =>
        {
            NudgeTrack(-ConfigStore.AutoSteer.NudgeDistance * 0.01); // cm to m, negative = left
        });

        NudgeRightCommand = new RelayCommand(() =>
        {
            NudgeTrack(ConfigStore.AutoSteer.NudgeDistance * 0.01); // cm to m, positive = right
        });

        FineNudgeLeftCommand = new RelayCommand(() =>
        {
            NudgeTrack(-ConfigStore.AutoSteer.NudgeDistance * 0.0025); // 1/4 of standard nudge, left
        });

        FineNudgeRightCommand = new RelayCommand(() =>
        {
            NudgeTrack(ConfigStore.AutoSteer.NudgeDistance * 0.0025); // 1/4 of standard nudge, right
        });

        // Half-tool-width nudge (legacy FormNudge half-tool buttons)
        HalfToolNudgeLeftCommand = new RelayCommand(() =>
        {
            double halfWidth = (ConfigStore.ActualToolWidth - ConfigStore.Tool.Overlap) * 0.5;
            NudgeTrack(-halfWidth);
        });

        HalfToolNudgeRightCommand = new RelayCommand(() =>
        {
            double halfWidth = (ConfigStore.ActualToolWidth - ConfigStore.Tool.Overlap) * 0.5;
            NudgeTrack(halfWidth);
        });

        // Reset nudge to zero (legacy FormNudge zero button)
        ResetNudgeCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null) return;
            SelectedTrack.NudgeDistance = 0;
            _intents.RequestGuidanceResetNudge();
            StatusMessage = "Nudge reset to zero";
        });

        // Bottom Strip Commands - cycle through preset coverage colors
        ChangeMappingColorCommand = new RelayCommand(() =>
        {
            uint[] presets = new uint[]
            {
                0x98FB98, // Pale green (default)
                0x00CED1, // Dark turquoise
                0xFFD700, // Gold
                0xFF8C00, // Dark orange
                0xFF69B4, // Hot pink
                0x87CEEB, // Sky blue
                0xDDA0DD, // Plum
                0xF0E68C, // Khaki
            };

            var tool = ConfigStore.Tool;
            uint current = tool.SingleCoverageColor;

            // Find current index and cycle to next
            int idx = Array.IndexOf(presets, current);
            int next = (idx + 1) % presets.Length;
            tool.SingleCoverageColor = presets[next];

            // Extract RGB for status message
            byte r = (byte)((presets[next] >> 16) & 0xFF);
            byte g = (byte)((presets[next] >> 8) & 0xFF);
            byte b = (byte)(presets[next] & 0xFF);
            string[] names = { "Green", "Turquoise", "Gold", "Orange", "Pink", "Blue", "Plum", "Khaki" };
            StatusMessage = $"Coverage color: {names[next]}";
        });

        SnapToPivotCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null)
            {
                StatusMessage = "No track selected";
                return;
            }
            // Snap by nudging the track by the current cross-track error (XTE)
            // This aligns the guidance line to the vehicle's current position
            double xte = State.Guidance.CrossTrackError;
            if (Math.Abs(xte) < 0.001)
            {
                StatusMessage = "Already on track";
                return;
            }
            NudgeTrack(xte);
        });

        ToggleYouSkipCommand = new RelayCommand(() =>
        {
            IsSkipWorkedMode = !IsSkipWorkedMode;
            StatusMessage = IsSkipWorkedMode
                ? "Skip worked tracks: ON — will skip already-worked rows"
                : "Skip worked tracks: OFF — fixed skip pattern";
        });

        ToggleUTurnSkipRowsCommand = new RelayCommand(() =>
        {
            IsUTurnSkipRowsEnabled = !IsUTurnSkipRowsEnabled;
            IsSkipWorkedMode = IsUTurnSkipRowsEnabled;
            // Reset snake sequence so it rebuilds on next turn
            State.YouTurn.SnakeSequence = null;
            State.YouTurn.SnakeIndex = -1;
            StatusMessage = IsUTurnSkipRowsEnabled
                ? $"U-Turn skip rows: ON ({UTurnSkipRows} rows, snake pattern)"
                : "U-Turn skip rows: OFF";
        });

        CycleUTurnSkipRowsCommand = new RelayCommand(() =>
        {
            UTurnSkipRows = (UTurnSkipRows + 1) % 10;
            StatusMessage = $"Skip rows: {UTurnSkipRows}";
        });

        // Flags Commands
        PlaceRedFlagCommand = new RelayCommand(() => PlaceFlag(FlagColor.Red));
        PlaceGreenFlagCommand = new RelayCommand(() => PlaceFlag(FlagColor.Green));
        PlaceYellowFlagCommand = new RelayCommand(() => PlaceFlag(FlagColor.Yellow));

        PlaceFlagHereCommand = new RelayCommand(() => PlaceFlag(NextAutoColor()));

        DeleteAllFlagsCommand = new RelayCommand(() =>
        {
            if (Flags.Count == 0)
            {
                StatusMessage = "No flags to delete";
                return;
            }
            ShowConfirmationDialog(
                "Delete All Flags",
                $"Delete all {Flags.Count} flags? This cannot be undone.",
                () =>
                {
                    int count = Flags.Count;
                    Flags.Clear();
                    _nextFlagId = 1;
                    UpdateFlagsOnMap();
                    StatusMessage = $"Deleted {count} flags";
                });
        });

        DeleteFlagCommand = new RelayCommand<object>(param =>
        {
            if (param is Flag flag)
            {
                Flags.Remove(flag);
                UpdateFlagsOnMap();
                StatusMessage = $"Deleted flag '{flag.Name}'";
            }
        });

        ShowFlagListCommand = new RelayCommand(() =>
        {
            State.UI.ShowDialog(Models.State.DialogType.FlagList);
        });

        CloseFlagListCommand = new RelayCommand(() =>
        {
            State.UI.CloseDialog();
        });

        PlaceFlagOnClickCommand = new RelayCommand(() =>
        {
            IsPlaceFlagOnClickMode = !IsPlaceFlagOnClickMode;
            StatusMessage = IsPlaceFlagOnClickMode
                ? "Tap on map to place a flag (tap again to cancel)"
                : "Flag placement cancelled";
            // Close any open dialog so the map is visible for tapping
            if (IsPlaceFlagOnClickMode)
                State.UI.CloseDialog();
        });

        // Section control commands
        ToggleManualModeCommand = new RelayCommand(() =>
        {
            IsManualSectionMode = !IsManualSectionMode;
            if (IsManualSectionMode)
                IsSectionMasterOn = false;

            var newState = IsManualSectionMode ? SectionButtonState.On : SectionButtonState.Off;
            _sectionControlService.SetAllSections(newState);
            // Sound on the user button press only — NOT in OnSectionStateChanged, which also
            // fires every auto coverage cycle and would spam it (issue #48).
            _audioService.Play(IsManualSectionMode
                ? Services.Interfaces.SoundEffect.SectionOn
                : Services.Interfaces.SoundEffect.SectionOff);

            StatusMessage = IsManualSectionMode ? "All sections ON" : "All sections OFF";
        });

        ToggleSectionMasterCommand = new RelayCommand(() =>
        {
            IsSectionMasterOn = !IsSectionMasterOn;
            if (IsSectionMasterOn)
                IsManualSectionMode = false;

            var newState = IsSectionMasterOn ? SectionButtonState.Auto : SectionButtonState.Off;
            _sectionControlService.SetAllSections(newState);
            _audioService.Play(IsSectionMasterOn
                ? Services.Interfaces.SoundEffect.SectionOn
                : Services.Interfaces.SoundEffect.SectionOff);

            StatusMessage = IsSectionMasterOn ? "All sections AUTO" : "All sections OFF";
        });

        ToggleSectionCommand = new RelayCommand<object>(param =>
        {
            if (param == null) return;

            int sectionIndex;
            if (param is int intVal)
                sectionIndex = intVal;
            else if (param is string strVal && int.TryParse(strVal, out var parsed))
                sectionIndex = parsed;
            else
                return;

            if (sectionIndex < 0 || sectionIndex >= _sectionControlService.NumSections)
                return;

            var currentState = _sectionControlService.SectionStates[sectionIndex].ButtonState;
            var newState = currentState switch
            {
                SectionButtonState.Off => SectionButtonState.Auto,
                SectionButtonState.Auto => SectionButtonState.On,
                SectionButtonState.On => SectionButtonState.Off,
                _ => SectionButtonState.Off
            };

            _sectionControlService.SetSectionState(sectionIndex, newState);
            _audioService.Play(newState == SectionButtonState.Off
                ? Services.Interfaces.SoundEffect.SectionOff
                : Services.Interfaces.SoundEffect.SectionOn);
            StatusMessage = $"Section {sectionIndex + 1}: {newState}";
        });

        ToggleYouTurnCommand = new RelayCommand(() =>
        {
            // No U-turns on a closed/polygon track — there's no field end to turn at (#421).
            if (IsActiveTrackClosed)
            {
                IsYouTurnEnabled = false;
                StatusMessage = "U-turns aren't available on a closed (polygon) track";
                return;
            }

            IsYouTurnEnabled = !IsYouTurnEnabled;
            SyncGuidanceStateToPipeline();
            StatusMessage = IsYouTurnEnabled ? "YouTurn enabled" : "YouTurn disabled";
        });

        ManualYouTurnLeftCommand = new RelayCommand(TriggerManualYouTurnLeft);
        ManualYouTurnRightCommand = new RelayCommand(TriggerManualYouTurnRight);
        ToggleUTurnDirectionCommand = new RelayCommand(ToggleUTurnDirection);

        ToggleAutoSteerCommand = new RelayCommand(() =>
        {
            // Disengage is always allowed — the user must be able to stop the
            // tractor even after the track/field has been cleared. Engagement
            // is the only path with preconditions.
            if (!IsAutoSteerEngaged && !IsAutoSteerAvailable)
            {
                StatusMessage = "AutoSteer not available - no active track";
                return;
            }

            // Engagement has no boundary/headland preconditions.
            //  - No boundary: AB-lines-only workflow with manual sections.
            //  - Boundary but no headland: auto-uturn still works against a
            //    synthetic headland line inset from the outer boundary by
            //    (UTurnRadius + UTurnDistanceFromBoundary). See
            //    GpsPipelineService.GetOrComputeSyntheticHeadland.

            IsAutoSteerEngaged = !IsAutoSteerEngaged;
            _audioService.Play(IsAutoSteerEngaged
                ? Services.Interfaces.SoundEffect.AutoSteerOn
                : Services.Interfaces.SoundEffect.AutoSteerOff);
            if (IsAutoSteerEngaged)
            {
                _autoSteerService.Engage();
                double widthMinusOverlap = ConfigStore.ActualToolWidth - Tool.Overlap;
                _logger.LogDebug($"[NUDGE] AutoSteer ENGAGED: State.Guidance.HowManyPathsAway={State.Guidance.HowManyPathsAway}, offset={State.Guidance.HowManyPathsAway * widthMinusOverlap:F2}m");
            }
            else
            {
                _autoSteerService.Disengage();
            }
            SyncGuidanceStateToPipeline();
            StatusMessage = IsAutoSteerEngaged ? "AutoSteer ENGAGED" : "AutoSteer disengaged";
        });

        // Contour commands
        ToggleContourModeCommand = new RelayCommand(() =>
        {
            IsContourModeOn = !IsContourModeOn;
            StatusMessage = IsContourModeOn ? "Contour mode ON" : "Contour mode OFF";
        });

        DeleteContoursCommand = new RelayCommand(() =>
        {
            _coverageMapService.ClearAll();
            // Reset track guidance state to force global search for nearest segment
            _trackGuidanceState = null;
            // Phase D D6: seed pending zeros and sync — the cycle becomes the
            // writer of HowManyPathsAway / NudgeOffset (via SetActiveTrack in
            // SyncGuidanceStateToPipeline). State.Guidance gets zeroed on the
            // next snapshot mirror.
            _pendingInitialPathsAway = 0;
            _pendingInitialNudgeOffset = 0;
            SyncGuidanceStateToPipeline();
            foreach (var track in SavedTracks)
            {
                track.NudgeDistance = 0;
                track.ClearWorkedPaths();
            }
            SaveTracksToFile();
            StatusMessage = "Coverage/contours cleared";
        });

        DeleteAppliedAreaCommand = new RelayCommand(() =>
        {
            ShowConfirmationDialog(
                "Delete Applied Area",
                "Are you sure you want to delete all applied area coverage? This cannot be undone.",
                DeleteAppliedAreaConfirmed);
        }, () => IsFieldOpen);

        // Tram line commands
        ToggleTramDisplayCommand = new RelayCommand(() =>
        {
            var tram = ConfigStore.Tram;

            // Cycle through modes like legacy: if only parallel lines, toggle on/off
            // Otherwise cycle Off -> All -> Lines -> Outer -> Off
            if (_tramLineService.ParallelTramLines.Count > 0 &&
                _tramLineService.OuterBoundaryTrack.Count == 0)
            {
                tram.DisplayMode = tram.DisplayMode != Models.Configuration.TramDisplayMode.Off
                    ? Models.Configuration.TramDisplayMode.Off
                    : Models.Configuration.TramDisplayMode.LinesOnly;
            }
            else
            {
                tram.DisplayMode = tram.DisplayMode switch
                {
                    Models.Configuration.TramDisplayMode.Off => Models.Configuration.TramDisplayMode.All,
                    Models.Configuration.TramDisplayMode.All => Models.Configuration.TramDisplayMode.LinesOnly,
                    Models.Configuration.TramDisplayMode.LinesOnly => Models.Configuration.TramDisplayMode.OuterOnly,
                    _ => Models.Configuration.TramDisplayMode.Off,
                };
            }

            ConfigStore.Guidance.TramDisplay = tram.DisplayMode != Models.Configuration.TramDisplayMode.Off;
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramDisplayLabel));
            StatusMessage = tram.DisplayMode switch
            {
                Models.Configuration.TramDisplayMode.Off => "Tram lines OFF",
                Models.Configuration.TramDisplayMode.All => "Tram lines: All",
                Models.Configuration.TramDisplayMode.LinesOnly => "Tram lines: Lines only",
                Models.Configuration.TramDisplayMode.OuterOnly => "Tram lines: Outer only",
                _ => "Tram lines"
            };
        });

        BuildTramLinesCommand = new RelayCommand(() =>
        {
            // Systems resolve their own references. Without systems we build
            // controlled-traffic lanes parallel to the field boundary, so a boundary
            // is required (no guidance track needed).
            if (ConfigStore.Tram.Systems.Count == 0 && !HasBoundary)
            {
                ShowErrorDialog("No Boundary",
                    "Create a field boundary before building tram lines.");
                return;
            }

            ConfigStore.Tram.DisplayMode = Models.Configuration.TramDisplayMode.All;
            ConfigStore.Guidance.TramDisplay = true;
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramDisplayLabel));
            StatusMessage = ConfigStore.Tram.Systems.Count > 0
                ? $"Tram lines built from {ConfigStore.Tram.Systems.Count} system(s)"
                : SelectedTrack != null
                    ? $"Tram lines built from '{SelectedTrack.Name}'"
                    : "Tram lines built from boundary";
        });

        ShowTramSettingsCommand = new RelayCommand(() =>
        {
            if (SelectedTrack == null)
            {
                ShowErrorDialog("No Track Selected", "Select an AB line or curve track first.");
                return;
            }
            State.UI.ShowDialog(Models.State.DialogType.TramSettings);
        });

        CloseTramSettingsCommand = new RelayCommand(() => State.UI.CloseDialog());

        IncreaseTramPassesCommand = new RelayCommand(() =>
        {
            ConfigStore.Tram.Passes = Math.Min(20, ConfigStore.Tram.Passes + 1);
            ConfigStore.Guidance.TramPasses = ConfigStore.Tram.Passes;
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramPasses));
            OnPropertyChanged(nameof(TramWidthDisplay));
            OnPropertyChanged(nameof(TramLineCountDisplay));
        });

        DecreaseTramPassesCommand = new RelayCommand(() =>
        {
            ConfigStore.Tram.Passes = Math.Max(1, ConfigStore.Tram.Passes - 1);
            ConfigStore.Guidance.TramPasses = ConfigStore.Tram.Passes;
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramPasses));
            OnPropertyChanged(nameof(TramWidthDisplay));
            OnPropertyChanged(nameof(TramLineCountDisplay));
        });

        void SetTramMode(Models.Configuration.TramDisplayMode mode)
        {
            ConfigStore.Tram.DisplayMode = mode;
            ConfigStore.Guidance.TramDisplay = mode != Models.Configuration.TramDisplayMode.Off;
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramDisplayLabel));
        }

        SetTramModeOffCommand = new RelayCommand(() => SetTramMode(Models.Configuration.TramDisplayMode.Off));
        SetTramModeAllCommand = new RelayCommand(() => SetTramMode(Models.Configuration.TramDisplayMode.All));
        SetTramModeLinesCommand = new RelayCommand(() => SetTramMode(Models.Configuration.TramDisplayMode.LinesOnly));
        SetTramModeOuterCommand = new RelayCommand(() => SetTramMode(Models.Configuration.TramDisplayMode.OuterOnly));

        IncreaseTramStartPassCommand = new RelayCommand(() =>
        {
            ConfigStore.Tram.StartPass++;
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramStartPass));
            OnPropertyChanged(nameof(TramLineCountDisplay));
        });

        DecreaseTramStartPassCommand = new RelayCommand(() =>
        {
            ConfigStore.Tram.StartPass = Math.Max(0, ConfigStore.Tram.StartPass - 1);
            UpdateTramLines(SelectedTrack);
            OnPropertyChanged(nameof(TramStartPass));
            OnPropertyChanged(nameof(TramLineCountDisplay));
        });

        SwapTramSideCommand = new RelayCommand(() =>
        {
            ConfigStore.Tram.IsOuterInverted = !ConfigStore.Tram.IsOuterInverted;
            UpdateTramLines(SelectedTrack);
            StatusMessage = $"Tram side: {(ConfigStore.Tram.IsOuterInverted ? "Inverted" : "Normal")}";
        });

        ClearTramLinesCommand = new RelayCommand(() =>
        {
            ShowConfirmationDialog("Clear Tram Lines",
                "Delete all tram lines? This cannot be undone.",
                () =>
                {
                    _tramLineService.Clear();
                    ConfigStore.Tram.DisplayMode = Models.Configuration.TramDisplayMode.Off;
                    _mapService.SetTramLines(
                        _tramLineService.OuterBoundaryTrack,
                        _tramLineService.InnerBoundaryTrack,
                        _tramLineService.ParallelTramLines);
                    OnPropertyChanged(nameof(TramLineCountDisplay));
                    StatusMessage = "Tram lines cleared";
                });
        });

        IncreaseTramLineCommand = new RelayCommand(() =>
        {
            ConfigStore.Guidance.TramLine++;
            OnPropertyChanged(nameof(TramLineNumber));
        });

        DecreaseTramLineCommand = new RelayCommand(() =>
        {
            ConfigStore.Guidance.TramLine = Math.Max(1, ConfigStore.Guidance.TramLine - 1);
            OnPropertyChanged(nameof(TramLineNumber));
        });

        ToggleTramLeftManualCommand = new RelayCommand(() =>
        {
            _tramLineService.IsLeftManualOn = !_tramLineService.IsLeftManualOn;
            OnPropertyChanged(nameof(TramLeftManualOn));
        });

        ToggleTramRightManualCommand = new RelayCommand(() =>
        {
            _tramLineService.IsRightManualOn = !_tramLineService.IsRightManualOn;
            OnPropertyChanged(nameof(TramRightManualOn));
        });

        CreateTrackFromBoundaryCommand = new RelayCommand(() =>
        {
            var boundary = State.Field.CurrentBoundary?.OuterBoundary;
            if (boundary?.Points == null || boundary.Points.Count < 3)
            {
                ShowErrorDialog("No Boundary", "Load a field with a boundary first.");
                return;
            }

            // Find the longest edge of the boundary polygon
            var pts = boundary.Points;
            double maxDist = 0;
            int bestIdx = 0;

            for (int i = 0; i < pts.Count; i++)
            {
                int next = (i + 1) % pts.Count;
                double dx = pts[next].Easting - pts[i].Easting;
                double dy = pts[next].Northing - pts[i].Northing;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    bestIdx = i;
                }
            }

            var p1 = pts[bestIdx];
            var p2 = pts[(bestIdx + 1) % pts.Count];
            double heading = Math.Atan2(p2.Easting - p1.Easting, p2.Northing - p1.Northing);

            // Extend 50m past both ends for full field coverage
            var a = new Models.Base.Vec3(
                p1.Easting - Math.Sin(heading) * 50,
                p1.Northing - Math.Cos(heading) * 50,
                heading);
            var b = new Models.Base.Vec3(
                p2.Easting + Math.Sin(heading) * 50,
                p2.Northing + Math.Cos(heading) * 50,
                heading);

            var track = new Models.Track.Track
            {
                Name = $"Boundary Edge {bestIdx + 1}",
                Points = new System.Collections.Generic.List<Models.Base.Vec3> { a, b },
                Type = Models.Track.TrackType.ABLine,
                IsVisible = true
            };

            SavedTracks.Add(track);
            SelectedTrack = track;
            StatusMessage = $"Created AB line from longest boundary edge ({maxDist:F0}m)";
        });

        // A Line: create AB line from current position + heading
        CreateALineFromPositionCommand = new RelayCommand(() =>
        {
            double heading = State.Vehicle.Heading * Math.PI / 180.0;
            double e = Easting;
            double n = Northing;

            // Extend 200m in both directions from current position
            var a = new Models.Base.Vec3(
                e - Math.Sin(heading) * 200,
                n - Math.Cos(heading) * 200,
                heading);
            var b = new Models.Base.Vec3(
                e + Math.Sin(heading) * 200,
                n + Math.Cos(heading) * 200,
                heading);

            var track = new Models.Track.Track
            {
                Name = $"A+ {Math.Round(State.Vehicle.Heading, 1)}\u00B0",
                Points = new System.Collections.Generic.List<Models.Base.Vec3> { a, b },
                Type = Models.Track.TrackType.ABLine,
                IsVisible = true
            };

            SavedTracks.Add(track);
            SelectedTrack = track;
            StatusMessage = $"Created A+ line at {State.Vehicle.Heading:F0}\u00B0";
        });

        // Field Builder dialog
        ShowFieldBuilderCommand = new RelayCommand(() =>
            OpenChainDialog(Models.State.DialogType.FieldBuilder));

        CloseFieldBuilderCommand = new RelayCommand(() =>
            State.UI.CloseDialog());

        IncreaseHeadlandDistanceCommand = new RelayCommand(() =>
        {
            HeadlandDistance = Math.Min(100, HeadlandDistance + 1.0);
            OnPropertyChanged(nameof(HeadlandDistance));
        });

        DecreaseHeadlandDistanceCommand = new RelayCommand(() =>
        {
            HeadlandDistance = Math.Max(1, HeadlandDistance - 1.0);
            OnPropertyChanged(nameof(HeadlandDistance));
        });

        CreateCurveFromBoundaryCommand = new RelayCommand(() =>
        {
            var bnd = State.Field.CurrentBoundary;
            var outer = bnd?.OuterBoundary;
            if (outer?.Points == null || outer.Points.Count < 3)
            {
                ShowErrorDialog("No Boundary", "Load a field with a boundary first.");
                return;
            }

            // AOG's btnMakeBoundaryCurve is greyed while a boundary-curve track exists
            // (FormABDraw.cs:823-831): one set per field. Refuse the same way.
            if (SavedTracks.Any(t => t.Type == Models.Track.TrackType.BoundaryCurve))
            {
                StatusMessage = "Boundary ring curves already exist — delete them to rebuild";
                return;
            }

            // The whole-ring curve IS the route planner's headland lap, brought across:
            // boundary inset by half the tool width (tool edge on the fence, #422), seam
            // anchored at the machine, corners rounded to the tractor's minimum turning
            // radius (a sharp offset corner isn't drivable), closed, with travel headings.
            // RoutePlanner.BuildLapRing is the same code the planned laps drive, so the
            // curve track and the planner's lap are the same line.
            double halfTool = ConfigStore.ActualToolWidth / 2.0;
            double minTurn = _configStore.Vehicle.MinTurningRadius;
            if (double.IsNaN(minTurn) || double.IsInfinity(minTurn) || minTurn <= 0.1)
                minTurn = Math.Max(0.1, _configStore.Guidance.UTurnRadius);
            double vE = State.Vehicle.Easting, vN = State.Vehicle.Northing;
            Models.Base.Vec3? startPos = (vE == 0 && vN == 0) ? null
                : new Models.Base.Vec3(vE, vN, State.Vehicle.Heading * Math.PI / 180.0);

            int made = 0;
            Models.Track.Track? first = null;
            // One track per ring (AOG: one per bndList entry): outer, then each inner hole.
            // An inner ring is offset AWAY from the hole: a negative inset = outward.
            var rings = new System.Collections.Generic.List<(Models.BoundaryPolygon poly, string name, double inset)>
                { (outer, "Boundary Curve", halfTool) };
            if (bnd != null)
                for (int i = 0; i < bnd.InnerBoundaries.Count; i++)
                    if (bnd.InnerBoundaries[i].Points is { Count: >= 3 })
                        rings.Add((bnd.InnerBoundaries[i], $"Inner Boundary Curve {i + 1}", -halfTool));

            foreach (var (poly, name, inset) in rings)
            {
                var v2 = new System.Collections.Generic.List<Models.Base.Vec2>(poly.Points.Count);
                foreach (var pt in poly.Points) v2.Add(new Models.Base.Vec2(pt.Easting, pt.Northing));
                // One path for both: a negative inset offsets OUTWARD (hole ring, away from it).
                var loop = RoutePlanner.BuildLapRing(v2, inset, minTurn, startPos);
                if (loop == null || loop.Count < 4) continue;

                var track = new Models.Track.Track
                {
                    Name = name,
                    Points = loop,
                    Type = Models.Track.TrackType.BoundaryCurve,   // AOG mode 32
                    IsVisible = true,
                    // A closed loop: guidance must wrap at the seam, not "end".
                    IsClosed = true
                };
                SavedTracks.Add(track);
                first ??= track;
                made++;
            }

            if (made == 0)
            {
                StatusMessage = "Tool too wide for this boundary — no ring fits";
                return;
            }
            SelectedTrack = first;
            SaveTracksToFile();   // AOG saves on OK; ours persisted only on field close before
            StatusMessage = $"Created {made} boundary ring curve{(made == 1 ? "" : "s")} ({halfTool:F1} m inside the fence, corners rounded to {minTurn:F1} m)";
        });

        CreateTracksFromAllEdgesCommand = new RelayCommand(() =>
        {
            var boundary = State.Field.CurrentBoundary?.OuterBoundary;
            if (boundary?.Points == null || boundary.Points.Count < 3)
            {
                ShowErrorDialog("No Boundary", "Load a field with a boundary first.");
                return;
            }

            var pts = boundary.Points;
            int n = pts.Count;

            // Boundary points are densified along each straight edge, so one AB line PER POINT
            // would make dozens on a 4-sided field. Find the CORNERS instead — vertices where the
            // boundary direction turns sharply — and make one AB line per edge between corners.
            const double CornerTurnThreshold = 0.35; // ~20°: real corners turn ~90°, edge noise <5°
            var corners = new System.Collections.Generic.List<int>();
            for (int i = 0; i < n; i++)
            {
                var prev = pts[(i - 1 + n) % n];
                var cur = pts[i];
                var nxt = pts[(i + 1) % n];
                double hIn = Math.Atan2(cur.Easting - prev.Easting, cur.Northing - prev.Northing);
                double hOut = Math.Atan2(nxt.Easting - cur.Easting, nxt.Northing - cur.Northing);
                double turn = hOut - hIn;
                while (turn > Math.PI) turn -= 2 * Math.PI;
                while (turn < -Math.PI) turn += 2 * Math.PI;
                if (Math.Abs(turn) > CornerTurnThreshold) corners.Add(i);
            }

            int created = 0;
            if (corners.Count >= 2)
            {
                // One AB line per edge: from each corner to the next, extended 50 m past both.
                for (int k = 0; k < corners.Count; k++)
                {
                    var pa = pts[corners[k]];
                    var pb = pts[corners[(k + 1) % corners.Count]];
                    double dx = pb.Easting - pa.Easting, dy = pb.Northing - pa.Northing;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist < 5.0) continue;

                    double heading = Math.Atan2(dx, dy);
                    var a = new Models.Base.Vec3(pa.Easting - Math.Sin(heading) * 50, pa.Northing - Math.Cos(heading) * 50, heading);
                    var b = new Models.Base.Vec3(pb.Easting + Math.Sin(heading) * 50, pb.Northing + Math.Cos(heading) * 50, heading);
                    SavedTracks.Add(new Models.Track.Track
                    {
                        Name = $"Edge {created + 1} ({dist:F0}m)",
                        Points = new System.Collections.Generic.List<Models.Base.Vec3> { a, b },
                        Type = Models.Track.TrackType.ABLine,
                        IsVisible = true
                    });
                    created++;
                }
            }

            if (created > 0)
                SelectedTrack = SavedTracks[SavedTracks.Count - 1];
            StatusMessage = created > 0
                ? $"Created {created} AB lines from boundary edges"
                : "Could not detect distinct boundary edges";
        });

        // Map zoom commands
        Toggle3DModeCommand = new RelayCommand(() =>
        {
            _mapService.Toggle3DMode();
            Is2DMode = !_mapService.Is3DMode;
        });

        ZoomInCommand = new RelayCommand(() =>
        {
            _mapService.Zoom(1.2);
        });

        ZoomOutCommand = new RelayCommand(() =>
        {
            _mapService.Zoom(0.8);
        });
    }

    /// <summary>
    /// Extend AB Line points so they pass the outer boundary by a margin.
    /// This ensures headland raycast will find an intersection for U-turn detection.
    /// (Drawn / driven / A+ AB lines. The boundary two-tap AB uses the body + tails model
    /// instead — see RemoteCreateBoundaryAB — with the same raycast, RaycastToOuterFence.)
    /// </summary>
    /// <param name="pointA">Original point A</param>
    /// <param name="pointB">Original point B</param>
    /// <param name="marginMeters">How far past the boundary to extend (default 20m)</param>
    /// <returns>Tuple of extended (pointA, pointB)</returns>
    private (Vec3 extendedA, Vec3 extendedB) ExtendABLinePastBoundary(Vec3 pointA, Vec3 pointB, double marginMeters = 20.0)
    {
        double heading = Math.Atan2(pointB.Easting - pointA.Easting, pointB.Northing - pointA.Northing);
        double sinH = Math.Sin(heading);
        double cosH = Math.Cos(heading);

        double extendA = marginMeters;
        double extendB = marginMeters;
        double hitA = RaycastToOuterFence(pointA.Easting, pointA.Northing, -sinH, -cosH); // backwards from A
        double hitB = RaycastToOuterFence(pointB.Easting, pointB.Northing, sinH, cosH);   // forwards from B
        if (hitA > 0) extendA = Math.Max(extendA, hitA + marginMeters);
        if (hitB > 0) extendB = Math.Max(extendB, hitB + marginMeters);

        var extendedA = new Vec3(
            pointA.Easting - sinH * extendA,
            pointA.Northing - cosH * extendA,
            heading);

        var extendedB = new Vec3(
            pointB.Easting + sinH * extendB,
            pointB.Northing + cosH * extendB,
            heading);

        _logger.LogDebug($"[ABLine] Extended A by {extendA:F1}m, B by {extendB:F1}m");

        return (extendedA, extendedB);
    }

    /// <summary>
    /// Extend curve endpoints so they pass the outer boundary by a margin.
    /// This ensures headland raycast will find an intersection for U-turn detection.
    /// (Drawn / recorded curves. The boundary two-tap curve uses the body + tails model
    /// instead — see RemoteCreateBoundaryCurveSegment / FenceTailLengths.)
    /// </summary>
    /// <param name="points">Original curve points</param>
    /// <param name="marginMeters">How far past the boundary to extend (default 20m)</param>
    /// <param name="maxTailMeters">Cap on each tail's length measured from the curve's end point
    /// (default unlimited).</param>
    /// <returns>New list with extended endpoints</returns>
    private List<Vec3> ExtendCurvePastBoundary(List<Vec3> points, double marginMeters = 20.0,
        double maxTailMeters = double.PositiveInfinity)
    {
        if (points.Count < 2)
        {
            return new List<Vec3>(points);
        }

        var result = new List<Vec3>(points);

        // Get headings at curve ends
        var firstPoint = points[0];
        var secondPoint = points[1];
        var lastPoint = points[^1];
        var secondLastPoint = points[^2];

        // Heading at start (backwards from first segment)
        double startHeading = Math.Atan2(secondPoint.Easting - firstPoint.Easting,
                                          secondPoint.Northing - firstPoint.Northing);
        // Heading at end (forwards along last segment)
        double endHeading = Math.Atan2(lastPoint.Easting - secondLastPoint.Easting,
                                        lastPoint.Northing - secondLastPoint.Northing);

        double extendStart = marginMeters;
        double extendEnd = marginMeters;
        double sinStart = Math.Sin(startHeading), cosStart = Math.Cos(startHeading);
        double sinEnd = Math.Sin(endHeading), cosEnd = Math.Cos(endHeading);
        double hitStart = RaycastToOuterFence(firstPoint.Easting, firstPoint.Northing, -sinStart, -cosStart);
        double hitEnd = RaycastToOuterFence(lastPoint.Easting, lastPoint.Northing, sinEnd, cosEnd);
        if (hitStart > 0) extendStart = Math.Max(extendStart, hitStart + marginMeters);
        if (hitEnd > 0) extendEnd = Math.Max(extendEnd, hitEnd + marginMeters);

        if (extendStart > maxTailMeters) extendStart = maxTailMeters;
        if (extendEnd > maxTailMeters) extendEnd = maxTailMeters;

        // Create extended start point
        var extendedStart = new Vec3(
            firstPoint.Easting - sinStart * extendStart,
            firstPoint.Northing - cosStart * extendStart,
            startHeading);

        // Create extended end point
        var extendedEnd = new Vec3(
            lastPoint.Easting + sinEnd * extendEnd,
            lastPoint.Northing + cosEnd * extendEnd,
            endHeading);

        // DENSIFY the extensions instead of replacing the endpoints with a single far point.
        // A curve's end can extend a very long way (e.g. its tangent runs along the fence, so
        // the raycast lands on the OPPOSITE fence hundreds of metres away). Left as a lone
        // 2-point segment, a tractor sitting on that long segment finds the far endpoint
        // (index 0) as its nearest curve point — and FindCurveTurnPoint's walk (`j > 0`) can't
        // advance from index 0, so it finds no crossing, the U-turn generator fails, and the
        // straight-line fallback mis-plots the turn at whatever fence the travel-heading raycast
        // hits (the "U-turn in the wrong corner" bug). Densified points keep the nearest index in
        // the interior so the walk proceeds in either direction.
        const double extSpacing = 2.0;
        var densified = new List<Vec3>(result.Count + 64);

        int leadSteps = Math.Max(1, (int)(extendStart / extSpacing));
        for (int i = 0; i < leadSteps; i++)
        {
            double f = (double)i / leadSteps; // 0 at extended tip → 1 at firstPoint (exclusive)
            densified.Add(new Vec3(
                extendedStart.Easting + (firstPoint.Easting - extendedStart.Easting) * f,
                extendedStart.Northing + (firstPoint.Northing - extendedStart.Northing) * f,
                startHeading));
        }
        densified.AddRange(result); // keeps the original first/last points and the curve body

        int tailSteps = Math.Max(1, (int)(extendEnd / extSpacing));
        for (int i = 1; i <= tailSteps; i++)
        {
            double f = (double)i / tailSteps; // firstPoint-after-last → extended tip
            densified.Add(new Vec3(
                lastPoint.Easting + (extendedEnd.Easting - lastPoint.Easting) * f,
                lastPoint.Northing + (extendedEnd.Northing - lastPoint.Northing) * f,
                endHeading));
        }

        _logger.LogDebug($"[Curve] Extended start by {extendStart:F1}m, end by {extendEnd:F1}m (densified)");

        return densified;
    }

    /// <summary>
    /// Nudge the current guidance line by a distance in meters.
    /// Positive = right, Negative = left (unadjusted — the cycle applies
    /// the heading-same-way sign flip).
    /// Phase D D5: posts an intent; the cycle drains and mutates
    /// _guidanceWorking.NudgeOffset on its own thread.
    /// </summary>
    private void NudgeTrack(double distanceMeters)
    {
        if (SelectedTrack == null)
        {
            StatusMessage = "No track selected";
            return;
        }

        _intents.RequestGuidanceNudge(distanceMeters);
        StatusMessage = $"Nudged {(distanceMeters > 0 ? "right" : "left")} {Math.Abs(distanceMeters * 100):F1}cm";
    }

    /// <summary>
    /// Delete every saved track (the confirmed action). The WEB path calls this directly:
    /// the browser already asks its own confirm() before sending track.deleteAll, and the
    /// native ShowConfirmationDialog it used to map to is host-side only — the web can
    /// never answer it, so nothing was deleted AND State.UI.ActiveDialog stayed parked on
    /// Confirmation, which made HandleHotkey bail until restart.
    /// </summary>
    public void DeleteAllTracksRemote()
    {
        if (SavedTracks.Count == 0) { StatusMessage = "No tracks to delete"; return; }
        SavedTracks.Clear();
        SelectedTrack = null;
        RebuildRecordedPathsAndContours(); // clear rec-path/contour display
        SaveTracksToFile();
        // Also remove RecPath.txt, else the recorded path reloads on next open.
        if (_fieldService.ActiveField is { } f)
            Services.RecPathFileService.DeleteRecFile(f.DirectoryPath, "RecPath.txt");
        StatusMessage = "All tracks deleted";
    }
}

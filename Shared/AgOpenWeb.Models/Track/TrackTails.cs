// AgOpenWeb
// Copyright (C) 2024-2026 AgOpenWeb Contributors
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
using System.Collections.Generic;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Track;

/// <summary>
/// "Body + tails" model for boundary-derived tracks (Bnd. AB / Bnd. Curve). The body is the
/// fixed point list from anchor A to anchor B; a tail is a straight run-out protruding PAST an
/// anchor along the body's end heading (AgOpenGPS A++/B++, FormABDraw.cs:845-876, one point
/// per metre), except that here its LENGTH is a stored number the operator adjusts, so
/// rebuilding is deterministic: the same body + tails always give the same points, and the
/// anchors and body never move.
/// </summary>
public static class TrackTails
{
    /// <summary>Metres one A++/A−−/B++/B−− press adds/removes (the host default when the
    /// command carries no delta; the web client sends the same value).</summary>
    public const double StepMeters = 5.0;   // operator: 5 m per press

    /// <summary>Spacing of the straight tail points (AOG adds one point per metre).</summary>
    public const double TailSpacingMeters = 1.0;

    /// <summary>Number of points a tail of <paramref name="tailMeters"/> adds: one per whole
    /// metre (1 m, 2 m, … n m) plus a tip point at the exact length when it isn't whole.
    /// 0 for a zero/negative tail.</summary>
    public static int TailPointCount(double tailMeters)
    {
        if (double.IsNaN(tailMeters) || tailMeters <= 1e-9) return 0;
        int whole = (int)Math.Floor(tailMeters / TailSpacingMeters + 1e-9);
        double frac = tailMeters - whole * TailSpacingMeters;
        return whole + (frac > 1e-6 ? 1 : 0);
    }

    /// <summary>Distances (m) from the anchor of each tail point, nearest first.</summary>
    private static List<double> TailDistances(double tailMeters)
    {
        int count = TailPointCount(tailMeters);
        var ds = new List<double>(count);
        if (count == 0) return ds;
        int whole = (int)Math.Floor(tailMeters / TailSpacingMeters + 1e-9);
        for (int i = 1; i <= whole; i++) ds.Add(i * TailSpacingMeters);
        if (ds.Count < count) ds.Add(tailMeters); // fractional tip
        return ds;
    }

    /// <summary>Direction of the body at one end, in [0, 2π): the end SEGMENT's bearing
    /// (atan2 of p1 − p0 at A, pn − pn−1 at B) — identical to the central-difference heading
    /// the curve builders store at the ends, and robust for a body whose headings are all 0.
    /// A degenerate end segment falls back to the end point's stored heading.</summary>
    public static double EndHeading(IReadOnlyList<Vec3> body, bool isA)
    {
        if (body.Count < 2) return body.Count == 1 ? Wrap(body[0].Heading) : 0;
        Vec3 from = isA ? body[0] : body[body.Count - 2];
        Vec3 to = isA ? body[1] : body[body.Count - 1];
        double dE = to.Easting - from.Easting, dN = to.Northing - from.Northing;
        // A short end segment (< 0.5 m) is unreliable after the 3-decimal file round-trip,
        // so prefer the end point's STORED heading when it has one (the builders and the
        // sidecar loader set it); fall back to the segment bearing only for a real segment.
        double stored = isA ? body[0].Heading : body[body.Count - 1].Heading;
        bool segmentReliable = dE * dE + dN * dN >= 0.25;
        if (!segmentReliable && !double.IsNaN(stored) && (Math.Abs(stored) > 1e-9 || dE * dE + dN * dN < 1e-12))
            return Wrap(stored);
        if (dE * dE + dN * dN < 1e-12) return Wrap(stored);
        return Wrap(Math.Atan2(dE, dN));
    }

    /// <summary>
    /// The single deterministic builder: <paramref name="body"/> (A … B, unchanged and copied
    /// verbatim) with <paramref name="tailA"/> metres of straight points prepended behind A
    /// along the body's start heading and <paramref name="tailB"/> metres appended ahead of B
    /// along its end heading. A tail of 0 adds nothing. Tail points carry the end heading.
    /// <paramref name="endpointsOnly"/> (2-point AB lines) keeps the result at exactly two
    /// points — the tips — so the track stays an AB line for guidance.
    /// </summary>
    public static List<Vec3> BuildWithTails(IReadOnlyList<Vec3> body, double tailA, double tailB, bool endpointsOnly = false)
    {
        if (body == null || body.Count == 0) return new List<Vec3>();
        if (tailA < 0 || double.IsNaN(tailA)) tailA = 0;
        if (tailB < 0 || double.IsNaN(tailB)) tailB = 0;

        var a = body[0];
        var b = body[body.Count - 1];
        double hA = EndHeading(body, true);
        double hB = EndHeading(body, false);
        double sinA = Math.Sin(hA), cosA = Math.Cos(hA);
        double sinB = Math.Sin(hB), cosB = Math.Cos(hB);

        if (endpointsOnly)
        {
            var tipA = tailA > 1e-9 ? new Vec3(a.Easting - sinA * tailA, a.Northing - cosA * tailA, a.Heading) : a;
            var tipB = tailB > 1e-9 ? new Vec3(b.Easting + sinB * tailB, b.Northing + cosB * tailB, b.Heading) : b;
            return new List<Vec3> { tipA, tipB };
        }

        var dsA = TailDistances(tailA);
        var dsB = TailDistances(tailB);
        var pts = new List<Vec3>(dsA.Count + body.Count + dsB.Count);
        // A tail, ordered tip → anchor (…, 2 m, 1 m behind A) so the list still runs A → B.
        for (int i = dsA.Count - 1; i >= 0; i--)
            pts.Add(new Vec3(a.Easting - sinA * dsA[i], a.Northing - cosA * dsA[i], hA));
        for (int i = 0; i < body.Count; i++) pts.Add(body[i]);
        // B tail, 1 m, 2 m, … ahead of B.
        for (int i = 0; i < dsB.Count; i++)
            pts.Add(new Vec3(b.Easting + sinB * dsB[i], b.Northing + cosB * dsB[i], hB));
        return pts;
    }

    /// <summary>
    /// Recover the fixed body from a persisted point list given the recorded tails (the
    /// inverse of <see cref="BuildWithTails"/>). For an AB line (<paramref name="endpointsOnly"/>)
    /// the anchors ARE the body. Returns null when the counts don't add up or the recovered
    /// ends don't sit on the anchors within <paramref name="tolerance"/> (e.g. the points file
    /// was edited by another program) — the caller then drops the anchors rather than trust
    /// a mismatched body.
    /// </summary>
    public static List<Vec3>? RecoverBody(IReadOnlyList<Vec3> points, Vec3 anchorA, Vec3 anchorB,
        double tailA, double tailB, bool endpointsOnly, double tolerance = 0.01)
    {
        if (points == null || points.Count < 2) return null;
        if (endpointsOnly)
        {
            // Check the file's two points are the anchors pushed out by the tails.
            var rebuilt = BuildWithTails(new[] { anchorA, anchorB }, tailA, tailB, endpointsOnly: true);
            if (!Near(rebuilt[0], points[0], tolerance) || !Near(rebuilt[1], points[points.Count - 1], tolerance)) return null;
            double h = EndHeading(new[] { anchorA, anchorB }, true);
            return new List<Vec3> { new(anchorA.Easting, anchorA.Northing, h), new(anchorB.Easting, anchorB.Northing, h) };
        }
        int skipA = TailPointCount(tailA);
        int skipB = TailPointCount(tailB);
        int bodyCount = points.Count - skipA - skipB;
        if (bodyCount < 2) return null;
        var body = new List<Vec3>(bodyCount);
        for (int i = skipA; i < skipA + bodyCount; i++) body.Add(points[i]);
        if (!Near(body[0], anchorA, tolerance) || !Near(body[bodyCount - 1], anchorB, tolerance)) return null;
        return body;
    }

    private static bool Near(Vec3 p, Vec3 q, double tol)
    {
        double dx = p.Easting - q.Easting, dy = p.Northing - q.Northing;
        return dx * dx + dy * dy <= tol * tol;
    }

    private static double Wrap(double h)
    {
        h %= 2.0 * Math.PI;
        if (h < 0) h += 2.0 * Math.PI;
        return h;
    }
}

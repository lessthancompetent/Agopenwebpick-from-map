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
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AgOpenWeb.Models.Base;

namespace AgOpenWeb.Models.Track;

/// <summary>
/// Track type for behavior variations and file format compatibility.
/// Values match AgOpenGPS TrackMode for file compatibility.
/// </summary>
public enum TrackType
{
    /// <summary>AB line - 2 points, infinite extension</summary>
    ABLine = 2,

    /// <summary>Curve - N points, finite</summary>
    Curve = 4,

    /// <summary>Boundary track offset outward</summary>
    BoundaryOuter = 8,

    /// <summary>Boundary track offset inward</summary>
    BoundaryInner = 16,

    /// <summary>Boundary curve</summary>
    BoundaryCurve = 32,

    /// <summary>Water pivot - circular, closed loop</summary>
    WaterPivot = 64,

    /// <summary>Recorded path - previously driven path for display only</summary>
    RecordedPath = 128,

    /// <summary>Contour - contour guidance strip</summary>
    Contour = 256
}

/// <summary>
/// Unified track representation for all guidance types.
/// Key insight: An AB line is just a curve with 2 points.
///
/// Replaces: ABLine.cs, separate curve models, and track mode switching.
/// All guidance algorithms can work with List&lt;Vec3&gt; points.
/// </summary>
public class Track : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    /// <summary>
    /// Track name for display and file storage.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Track points with Easting, Northing, Heading.
    /// AB Lines have exactly 2 points. Curves have N points.
    /// Headings are stored in radians.
    /// </summary>
    public List<Vec3> Points { get; set; } = new();

    /// <summary>
    /// Track type for behavior variations and file compatibility.
    /// </summary>
    public TrackType Type { get; set; } = TrackType.ABLine;

    /// <summary>
    /// Whether this track forms a closed loop (water pivot, boundary tracks).
    /// </summary>
    public bool IsClosed { get; set; }

    /// <summary>
    /// Drive this track directly (pass 0) instead of free-drive snapping to the nearest
    /// parallel pass. Set for boundary-follow curves — you drive the boundary itself, not
    /// offset passes of it. The guidance keeps the reference line, no pass offsetting.
    /// </summary>
    public bool NoPassOffset { get; set; }

    /// <summary>
    /// Boundary-derived tracks ("Bnd. AB" / "Bnd. Curve"): the two FIXED anchors the
    /// operator placed on the fence. Once placed they never move — A++/A−−/B++/B−− only
    /// change <see cref="TailA"/>/<see cref="TailB"/>, the straight run-out PAST each anchor.
    /// Null on every other track (drawn, driven, imported, AOG-made).
    /// </summary>
    public Vec3? AnchorA { get; set; }

    /// <summary>See <see cref="AnchorA"/>.</summary>
    public Vec3? AnchorB { get; set; }

    /// <summary>Metres of straight line protruding past anchor A (behind it along the body's
    /// start heading). 0 = the line starts exactly at A. Never negative.</summary>
    public double TailA { get; set; }

    /// <summary>Metres of straight line protruding past anchor B (ahead of it along the body's
    /// end heading). 0 = the line ends exactly at B. Never negative.</summary>
    public double TailB { get; set; }

    /// <summary>
    /// The fixed body between the anchors: first point == <see cref="AnchorA"/>, last ==
    /// <see cref="AnchorB"/>, headings included. <see cref="Points"/> is always
    /// <c>TrackTails.BuildWithTails(Body, TailA, TailB)</c>; the body itself is never edited.
    /// Null when <see cref="HasAnchors"/> is false.
    /// </summary>
    public List<Vec3>? Body { get; set; }

    /// <summary>True for a boundary-derived track whose ends are fixed anchors with
    /// adjustable tails (both anchors and the body are present).</summary>
    public bool HasAnchors => AnchorA.HasValue && AnchorB.HasValue && Body != null && Body.Count >= 2;

    /// <summary>
    /// Whether this track is currently active for guidance.
    /// </summary>
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Visibility on map.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Accumulated nudge offset in meters.
    /// Positive = right, Negative = left.
    /// </summary>
    public double NudgeDistance { get; set; }

    /// <summary>
    /// Set of path numbers that have been worked (driven with sections on).
    /// Used by skip-and-fill mode to find the next unworked path.
    /// </summary>
    public HashSet<int> WorkedPaths { get; set; } = new();

    public void MarkPathWorked(int pathNumber) => WorkedPaths.Add(pathNumber);
    public bool IsPathWorked(int pathNumber) => WorkedPaths.Contains(pathNumber);
    public void ClearWorkedPaths() => WorkedPaths.Clear();

    /// <summary>
    /// True if this is a 2-point track (AB line behavior).
    /// AB lines use infinite extension along the heading.
    /// </summary>
    public bool IsABLine => Points.Count == 2 && Type == TrackType.ABLine;

    /// <summary>
    /// True if this is a multi-point curve (finite track).
    /// </summary>
    public bool IsCurve => Points.Count > 2 || Type == TrackType.Curve;

    /// <summary>
    /// Overall heading of the track in radians.
    /// For AB lines: heading from point A to B.
    /// For curves: heading of first segment.
    /// </summary>
    public double Heading => Points.Count >= 2
        ? Math.Atan2(Points[1].Easting - Points[0].Easting,
                     Points[1].Northing - Points[0].Northing)
        : 0;

    /// <summary>
    /// Heading in degrees for display purposes.
    /// </summary>
    public double HeadingDegrees => Heading * 180.0 / Math.PI;

    /// <summary>
    /// Point A (first point) for AB line compatibility.
    /// </summary>
    public Vec3 PointA => Points.Count > 0 ? Points[0] : default;

    /// <summary>
    /// Point B (second/last point) for AB line compatibility.
    /// </summary>
    public Vec3 PointB => Points.Count > 1 ? Points[^1] : default;

    /// <summary>
    /// Create an AB line track from two points.
    /// </summary>
    /// <param name="name">Track name</param>
    /// <param name="pointA">Starting point (Easting, Northing, Heading in radians)</param>
    /// <param name="pointB">Ending point (Easting, Northing, Heading in radians)</param>
    /// <returns>A new Track configured as an AB line</returns>
    public static Track FromABLine(string name, Vec3 pointA, Vec3 pointB)
    {
        var heading = Math.Atan2(pointB.Easting - pointA.Easting,
                                  pointB.Northing - pointA.Northing);

        return new Track
        {
            Name = name,
            Type = TrackType.ABLine,
            Points = new List<Vec3>
            {
                new Vec3(pointA.Easting, pointA.Northing, heading),
                new Vec3(pointB.Easting, pointB.Northing, heading)
            },
            IsClosed = false
        };
    }

    /// <summary>
    /// Create a curve track from a list of points.
    /// </summary>
    /// <param name="name">Track name</param>
    /// <param name="points">List of points with Easting, Northing, Heading</param>
    /// <param name="isClosed">Whether the curve forms a closed loop</param>
    /// <returns>A new Track configured as a curve</returns>
    public static Track FromCurve(string name, List<Vec3> points, bool isClosed = false)
    {
        return new Track
        {
            Name = name,
            Type = isClosed ? TrackType.WaterPivot : TrackType.Curve,
            Points = new List<Vec3>(points),
            IsClosed = isClosed
        };
    }

    /// <summary>
    /// Create a recorded path track from a list of points.
    /// Recorded paths are display-only (not used for guidance).
    /// </summary>
    public static Track FromRecordedPath(string name, List<Vec3> points)
    {
        return new Track
        {
            Name = name,
            Type = TrackType.RecordedPath,
            Points = new List<Vec3>(points),
            IsClosed = false,
            IsVisible = true
        };
    }

    /// <summary>
    /// Create a contour track from a list of points.
    /// Contours are used for contour guidance mode.
    /// </summary>
    public static Track FromContour(string name, List<Vec3> points)
    {
        return new Track
        {
            Name = name,
            Type = TrackType.Contour,
            Points = new List<Vec3>(points),
            IsClosed = false,
            IsVisible = true
        };
    }

    /// <summary>
    /// Whether this is a recorded path (display only, not for guidance).
    /// </summary>
    public bool IsRecordedPath => Type == TrackType.RecordedPath;

    /// <summary>
    /// Whether this is a contour strip (for contour guidance).
    /// </summary>
    public bool IsContour => Type == TrackType.Contour;

}

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
using System.Globalization;
using System.IO;
using AgOpenWeb.Models;
using AgOpenWeb.Models.Base;
using AgOpenWeb.Models.Track;
using TrackModel = AgOpenWeb.Models.Track.Track;

namespace AgOpenWeb.Services
{
    /// <summary>
    /// Service for loading and saving track lines (AB lines and curves) to TrackLines.txt.
    /// Format matches AgOpenGPS WinForms for field compatibility.
    /// </summary>
    public static class TrackFilesService
    {
        private const string FileName = "TrackLines.txt";
        private const string Header = "$TrackLines";
        /// <summary>Sidecar next to TrackLines.txt carrying what the AgOpenGPS format can't:
        /// the fixed anchors + tail lengths of boundary-derived tracks (Track.AnchorA/B,
        /// TailA/B). TrackLines.txt stays byte-compatible with AOG; entries are keyed by
        /// list index AND name so a file AOG reordered can't attach anchors to the wrong track.</summary>
        public const string MetaFileName = "Tracks.meta.json";

        private sealed class TrackMetaFile
        {
            public int Version { get; set; } = 1;
            public List<TrackMetaEntry> Tracks { get; set; } = new();
        }

        private sealed class TrackMetaEntry
        {
            public int Index { get; set; }
            public string Name { get; set; } = string.Empty;
            public double AnchorAE { get; set; }
            public double AnchorAN { get; set; }
            public double AnchorAH { get; set; }
            public double AnchorBE { get; set; }
            public double AnchorBN { get; set; }
            public double AnchorBH { get; set; }
            public double TailA { get; set; }
            public double TailB { get; set; }
        }

        private static readonly System.Text.Json.JsonSerializerOptions MetaJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>A boundary AB line is persisted as its two tips only (endpointsOnly tails).</summary>
        private static bool IsTwoPointLine(TrackModel t) => t.Type == TrackType.ABLine && t.Points.Count == 2;

        private static void SaveMeta(string fieldDirectory, IReadOnlyList<TrackModel> tracks)
        {
            var metaPath = Path.Combine(fieldDirectory, MetaFileName);
            var file = new TrackMetaFile();
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (!t.HasAnchors) continue;
                var a = t.AnchorA!.Value; var b = t.AnchorB!.Value;
                file.Tracks.Add(new TrackMetaEntry
                {
                    Index = i, Name = t.Name ?? string.Empty,
                    AnchorAE = a.Easting, AnchorAN = a.Northing, AnchorAH = a.Heading,
                    AnchorBE = b.Easting, AnchorBN = b.Northing, AnchorBH = b.Heading,
                    TailA = t.TailA, TailB = t.TailB,
                });
            }
            if (file.Tracks.Count == 0)
            {
                // No anchored track → no sidecar, so a stale one can never mis-attach later.
                if (File.Exists(metaPath)) File.Delete(metaPath);
                return;
            }
            File.WriteAllText(metaPath, System.Text.Json.JsonSerializer.Serialize(file, MetaJsonOptions));
        }

        /// <summary>Attach anchors/tails/body from the sidecar to the tracks just read from
        /// TrackLines.txt. Silently skips a missing/corrupt sidecar and any entry whose
        /// index+name no longer match or whose recorded tails don't reproduce the file's
        /// points (the track then simply has no anchors, like any other track).</summary>
        private static void MergeMeta(string fieldDirectory, List<TrackModel> tracks)
        {
            var metaPath = Path.Combine(fieldDirectory, MetaFileName);
            if (!File.Exists(metaPath)) return;
            TrackMetaFile? file;
            try
            {
                file = System.Text.Json.JsonSerializer.Deserialize<TrackMetaFile>(File.ReadAllText(metaPath), MetaJsonOptions);
            }
            catch
            {
                return;
            }
            if (file?.Tracks == null) return;
            foreach (var m in file.Tracks)
            {
                if (m.Index < 0 || m.Index >= tracks.Count) continue;
                var t = tracks[m.Index];
                if (!string.Equals(t.Name, m.Name, StringComparison.Ordinal)) continue;
                if (double.IsNaN(m.TailA) || double.IsNaN(m.TailB)) continue;
                var a = new Vec3(m.AnchorAE, m.AnchorAN, m.AnchorAH);
                var b = new Vec3(m.AnchorBE, m.AnchorBN, m.AnchorBH);
                double tailA = Math.Max(0, m.TailA), tailB = Math.Max(0, m.TailB);
                // TrackLines.txt rounds to 3 decimals → 0.01 m tolerance on the anchor match.
                var body = TrackTails.RecoverBody(t.Points, a, b, tailA, tailB, IsTwoPointLine(t), tolerance: 0.01);
                if (body == null) continue;
                // The tail heading must survive the file round-trip: re-deriving it from the
                // recovered body's end SEGMENT can fail after 3-decimal rounding (a sub-metre
                // end segment yields a garbage bearing), so stamp the sidecar's saved anchor
                // headings onto the body ends and let EndHeading prefer them (see TrackTails).
                body[0] = new Vec3(body[0].Easting, body[0].Northing, m.AnchorAH);
                body[^1] = new Vec3(body[^1].Easting, body[^1].Northing, m.AnchorBH);
                t.AnchorA = a; t.AnchorB = b; t.TailA = tailA; t.TailB = tailB; t.Body = body;
            }
        }

        /// <summary>
        /// Map legacy TrackMode (file format) to TrackType (runtime model).
        /// </summary>
        private static TrackType MapTrackMode(TrackMode mode) => mode switch
        {
            TrackMode.AB => TrackType.ABLine,
            TrackMode.Curve => TrackType.Curve,
            TrackMode.BndTrackOuter => TrackType.BoundaryOuter,
            TrackMode.BndTrackInner => TrackType.BoundaryInner,
            TrackMode.BndCurve => TrackType.BoundaryCurve,
            TrackMode.WaterPivot => TrackType.WaterPivot,
            TrackMode.RecordedPath => TrackType.RecordedPath,
            TrackMode.Contour => TrackType.Contour,
            _ => TrackType.ABLine
        };

        /// <summary>
        /// Map TrackType (runtime model) to legacy TrackMode (file format).
        /// </summary>
        private static TrackMode MapTrackType(TrackType type) => type switch
        {
            TrackType.ABLine => TrackMode.AB,
            TrackType.Curve => TrackMode.Curve,
            TrackType.BoundaryOuter => TrackMode.BndTrackOuter,
            TrackType.BoundaryInner => TrackMode.BndTrackInner,
            TrackType.BoundaryCurve => TrackMode.BndCurve,
            TrackType.WaterPivot => TrackMode.WaterPivot,
            TrackType.RecordedPath => TrackMode.RecordedPath,
            TrackType.Contour => TrackMode.Contour,
            _ => TrackMode.AB
        };

        /// <summary>
        /// Load tracks from TrackLines.txt file as unified Track objects.
        /// </summary>
        /// <param name="fieldDirectory">Path to the field directory</param>
        /// <returns>List of Track objects</returns>
        public static List<TrackModel> Load(string fieldDirectory)
        {
            if (string.IsNullOrWhiteSpace(fieldDirectory))
                throw new ArgumentNullException(nameof(fieldDirectory));

            var result = new List<TrackModel>();
            var path = Path.Combine(fieldDirectory, FileName);

            if (!File.Exists(path))
                return result;

            using (var reader = new StreamReader(path))
            {
                // Require header
                var header = reader.ReadLine();
                if (header == null || !header.TrimStart().StartsWith("$", StringComparison.Ordinal))
                    throw new InvalidDataException("TrackLines.txt missing $ header.");

                while (!reader.EndOfStream)
                {
                    // --- Name ---
                    var name = reader.ReadLine();
                    if (name == null) break;
                    name = name.Trim();
                    if (name.Length == 0) continue;

                    // --- Heading (in radians) ---
                    var headingLine = reader.ReadLine();
                    if (headingLine == null) throw new InvalidDataException("Unexpected EOF after track name.");
                    var headingRadians = double.Parse(headingLine.Trim(), CultureInfo.InvariantCulture);

                    // --- A point (easting,northing) ---
                    var aLine = reader.ReadLine();
                    if (aLine == null) throw new InvalidDataException("Unexpected EOF reading point A.");
                    var aParts = aLine.Split(',');
                    var aEasting = double.Parse(aParts[0], CultureInfo.InvariantCulture);
                    var aNorthing = double.Parse(aParts[1], CultureInfo.InvariantCulture);

                    // --- B point (easting,northing) ---
                    var bLine = reader.ReadLine();
                    if (bLine == null) throw new InvalidDataException("Unexpected EOF reading point B.");
                    var bParts = bLine.Split(',');
                    var bEasting = double.Parse(bParts[0], CultureInfo.InvariantCulture);
                    var bNorthing = double.Parse(bParts[1], CultureInfo.InvariantCulture);

                    // --- Nudge ---
                    var nudgeLine = reader.ReadLine();
                    if (nudgeLine == null) throw new InvalidDataException("Unexpected EOF reading nudge.");
                    var nudgeDistance = double.Parse(nudgeLine.Trim(), CultureInfo.InvariantCulture);

                    // --- Mode ---
                    var modeLine = reader.ReadLine();
                    if (modeLine == null) throw new InvalidDataException("Unexpected EOF reading mode.");
                    var modeInt = int.Parse(modeLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
                    var mode = (TrackMode)modeInt;

                    // --- Visibility ---
                    var visLine = reader.ReadLine();
                    if (visLine == null) throw new InvalidDataException("Unexpected EOF reading visibility.");
                    var isVisible = bool.Parse(visLine.Trim());

                    // --- Curve count ---
                    var countLine = reader.ReadLine();
                    if (countLine == null) throw new InvalidDataException("Unexpected EOF reading curve count.");
                    var curveCount = int.Parse(countLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

                    // --- Curve points ---
                    var curvePoints = new List<Vec3>();
                    for (int i = 0; i < curveCount; i++)
                    {
                        var line = reader.ReadLine();
                        if (line == null) throw new InvalidDataException("Unexpected EOF in curve points.");
                        var parts = line.Split(',');
                        var easting = double.Parse(parts[0], CultureInfo.InvariantCulture);
                        var northing = double.Parse(parts[1], CultureInfo.InvariantCulture);
                        var pointHeading = double.Parse(parts[2], CultureInfo.InvariantCulture);
                        curvePoints.Add(new Vec3(easting, northing, pointHeading));
                    }

                    // Build Track directly from file fields
                    var track = new TrackModel
                    {
                        Name = name,
                        Type = MapTrackMode(mode),
                        IsVisible = isVisible,
                        NudgeDistance = nudgeDistance,
                        IsClosed = mode == TrackMode.WaterPivot
                    };

                    // Use curve points if available, otherwise use A/B points
                    if (curvePoints.Count > 0)
                    {
                        track.Points = new List<Vec3>(curvePoints);
                    }
                    else
                    {
                        track.Points = new List<Vec3>
                        {
                            new Vec3(aEasting, aNorthing, headingRadians),
                            new Vec3(bEasting, bNorthing, headingRadians)
                        };
                    }

                    result.Add(track);
                }
            }

            MergeMeta(fieldDirectory, result);
            return result;
        }

        /// <summary>
        /// Save tracks to TrackLines.txt file. Overwrites existing file.
        /// </summary>
        /// <param name="fieldDirectory">Path to the field directory</param>
        /// <param name="tracks">List of Track objects to save</param>
        public static void Save(string fieldDirectory, IReadOnlyList<TrackModel> tracks)
        {
            if (string.IsNullOrWhiteSpace(fieldDirectory))
                throw new ArgumentNullException(nameof(fieldDirectory));

            var filename = Path.Combine(fieldDirectory, FileName);

            SaveMeta(fieldDirectory, tracks ?? Array.Empty<TrackModel>());
            using (var writer = new StreamWriter(filename, false))
            {
                writer.WriteLine(Header);

                if (tracks == null || tracks.Count == 0)
                    return;

                foreach (var track in tracks)
                {
                    // Name
                    writer.WriteLine(track.Name ?? string.Empty);

                    // Heading in radians (Track.Heading already returns radians)
                    writer.WriteLine(track.Heading.ToString(CultureInfo.InvariantCulture));

                    // Point A (easting,northing) - first point
                    if (track.Points.Count >= 2)
                    {
                        writer.WriteLine($"{FormatDouble(track.Points[0].Easting, 3)},{FormatDouble(track.Points[0].Northing, 3)}");
                        writer.WriteLine($"{FormatDouble(track.Points[^1].Easting, 3)},{FormatDouble(track.Points[^1].Northing, 3)}");
                    }
                    else
                    {
                        writer.WriteLine("0.000,0.000");
                        writer.WriteLine("0.000,0.000");
                    }

                    // Nudge distance
                    writer.WriteLine(track.NudgeDistance.ToString(CultureInfo.InvariantCulture));

                    // Mode (as integer, mapped from TrackType)
                    writer.WriteLine(((int)MapTrackType(track.Type)).ToString(CultureInfo.InvariantCulture));

                    // Visibility
                    writer.WriteLine(track.IsVisible.ToString());

                    // Curve points - for non-AB-line tracks, store all points
                    if (track.Points.Count > 2 || track.Type != TrackType.ABLine)
                    {
                        writer.WriteLine(track.Points.Count.ToString(CultureInfo.InvariantCulture));
                        foreach (var p in track.Points)
                        {
                            writer.WriteLine($"{FormatDouble(p.Easting, 3)},{FormatDouble(p.Northing, 3)},{FormatDouble(p.Heading, 5)}");
                        }
                    }
                    else
                    {
                        writer.WriteLine("0");
                    }
                }
            }
        }

        /// <summary>
        /// Check if a TrackLines.txt file exists in the field directory
        /// </summary>
        public static bool Exists(string fieldDirectory)
        {
            if (string.IsNullOrWhiteSpace(fieldDirectory))
                return false;

            return File.Exists(Path.Combine(fieldDirectory, FileName));
        }

        private static string FormatDouble(double value, int decimalPlaces)
        {
            return value.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
        }
    }
}

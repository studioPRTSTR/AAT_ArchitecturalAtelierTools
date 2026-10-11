// #! csharp
// RSoTeSc - Rhino Soft Thermal Insulation Script (C# port).
// Run from Rhino 8's ScriptEditor (or via the _RunCSharpScript command).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;


SoftInsulation.Run(RhinoDoc.ActiveDoc);


/// <summary>
/// An infinite cutting line through Origin with direction Dir (unit, world
/// XY). A point X is kept when SideOfLine(X) == KeepSign. HalfLength is the
/// distance from Origin, along Dir, to where the line meets the band edge
/// (halfWidth / sin(theta/2) at a corner, halfWidth at a perpendicular cap).
/// </summary>
struct CutLine
{
    public Point3d Origin;
    public Vector3d Dir;
    public int KeepSign;
    public double HalfLength;

    public CutLine(Point3d origin, Vector3d dir, int keepSign, double halfLength)
    {
        Origin = origin;
        Dir = dir;
        KeepSign = keepSign;
        HalfLength = halfLength;
    }
}


static class SoftInsulation
{
    const string ConfigFilename = "RSoTeSc_config.json";

    static string LayerName = "A_Isolation_01";
    static double DefaultWidth = 0.5;
    static double DefaultSpacing = 0.0;      // 0 = auto (classic look: width / (2 + sqrt 3))
    static bool TrimCrossing = true;         // false = drop loops that cross a cut line
    static bool ContinuousRhythm = true;     // false = restart the spacing on every segment

    static readonly double Sqrt3 = Math.Sqrt(3.0);

    // Below this interior angle a corner counts as "very sharp": the miter
    // tip runs off towards infinity, so its reach is capped (see CutReach).
    const double SharpAngleDeg = 15.0;
    // Cap on how far (in band widths) a miter may reach along a segment.
    const double MaxMiterReachFactor = 4.0;


    /// <summary>
    /// Read settings from RSoTeSc_config.json next to this script, so they
    /// can be tweaked without editing the script itself - handy while
    /// testing in Rhino's script editor. Falls back to the built-in defaults
    /// if the file (or any key) is missing or malformed. Recognized keys:
    /// layer_name, default_width, default_spacing, trim_crossing,
    /// continuous_rhythm.
    /// </summary>
    static void LoadConfig([CallerFilePath] string scriptPath = "")
    {
        try
        {
            string scriptDir = Path.GetDirectoryName(scriptPath);
            if (string.IsNullOrEmpty(scriptDir))
                return;
            string configPath = Path.Combine(scriptDir, ConfigFilename);
            if (!File.Exists(configPath))
                return;

            // Tiny hand-rolled reader for the few flat keys we care about,
            // so the script needs no JSON library reference.
            string json = File.ReadAllText(configPath);

            var nameMatch = Regex.Match(json, "\"layer_name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (nameMatch.Success)
                LayerName = Regex.Unescape(nameMatch.Groups[1].Value);

            DefaultWidth = ReadNumber(json, "default_width", DefaultWidth);
            DefaultSpacing = ReadNumber(json, "default_spacing", DefaultSpacing);
            TrimCrossing = ReadBool(json, "trim_crossing", TrimCrossing);
            ContinuousRhythm = ReadBool(json, "continuous_rhythm", ContinuousRhythm);
        }
        catch (Exception)
        {
        }
    }

    static double ReadNumber(string json, string key, double fallback)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*([-+0-9.eE]+)");
        if (m.Success &&
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return v;
        return fallback;
    }

    static bool ReadBool(string json, string key, bool fallback)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(true|false)");
        return m.Success ? m.Groups[1].Value == "true" : fallback;
    }


    /// <summary>
    /// Delete any objects already on the given layer before drawing new
    /// ones, so leftover geometry from earlier test runs can never get mixed
    /// in with (or mistaken for) the current run's output.
    /// </summary>
    static void ClearLayer(RhinoDoc doc, string layerName)
    {
        int layerIndex = doc.Layers.FindByFullPath(layerName, -1);
        if (layerIndex < 0)
            return;
        var existing = doc.Objects
            .Where(obj => obj.Attributes.LayerIndex == layerIndex)
            .Select(obj => obj.Id)
            .ToList();
        foreach (Guid objId in existing)
            doc.Objects.Delete(objId, true);
    }


    /// <summary>Create (or reuse) a dedicated layer.</summary>
    static int EnsureLayer(RhinoDoc doc, string layerName, System.Drawing.Color color)
    {
        int layerIndex = doc.Layers.FindByFullPath(layerName, -1);
        if (layerIndex >= 0)
            return layerIndex;
        var layer = new Layer
        {
            Name = layerName,
            Color = color,
            PlotWeight = 0.13, // thin hairline, mm
        };
        return doc.Layers.Add(layer);
    }


    /// <summary>
    /// Let the user either pick an existing curve to use as the insulation
    /// path, or - if they press Enter without selecting anything - draw a
    /// new polyline interactively.
    /// </summary>
    static Curve GetPathCurve()
    {
        var go = new GetObject();
        go.SetCommandPrompt("Select a path curve for the insulation run (Enter to draw a new polyline instead)");
        go.GeometryFilter = ObjectType.Curve;
        go.AcceptNothing(true);
        GetResult result = go.Get();

        if (result == GetResult.Object)
            return go.Object(0).Curve()?.DuplicateCurve();

        if (result == GetResult.Nothing)
        {
            if (RhinoGet.GetPolyline(out Polyline polyline) != Result.Success || polyline == null || polyline.Count < 2)
                return null;
            return polyline.ToPolylineCurve();
        }

        return null;
    }


    /// <summary>
    /// Ask for the band width, with command-line options for the spacing,
    /// what to do with loops crossing a cut line, the rhythm across corners
    /// and debug drawing. Returns false if cancelled.
    /// </summary>
    static bool GetSettings(out double width, out double spacing, out bool trimCrossing,
                            out bool continuousRhythm, out bool debugDraw)
    {
        width = DefaultWidth;
        spacing = DefaultSpacing;
        trimCrossing = TrimCrossing;
        continuousRhythm = ContinuousRhythm;
        debugDraw = false;

        var optSpacing = new OptionDouble(DefaultSpacing, 0.0, 1.0e9);
        var optCrossing = new OptionToggle(TrimCrossing, "Remove", "Trim");
        var optRhythm = new OptionToggle(ContinuousRhythm, "Restart", "Continuous");
        var optDebug = new OptionToggle(false, "Off", "On");

        var gn = new GetNumber();
        gn.SetCommandPrompt("Width of the insulation band (tip to tip)");
        gn.SetDefaultNumber(DefaultWidth);
        gn.SetLowerLimit(0.001, false);
        gn.AcceptNothing(true);
        gn.AddOptionDouble("Spacing", ref optSpacing, "Loop spacing (0 = auto)");
        gn.AddOptionToggle("CrossingLoops", ref optCrossing);
        gn.AddOptionToggle("Rhythm", ref optRhythm);
        gn.AddOptionToggle("DebugDraw", ref optDebug);

        while (true)
        {
            GetResult res = gn.Get();
            if (res == GetResult.Option)
                continue;
            if (res == GetResult.Number)
            {
                width = gn.Number();
                break;
            }
            if (res == GetResult.Nothing)
                break; // keep DefaultWidth
            return false;
        }

        spacing = optSpacing.CurrentValue;
        trimCrossing = optCrossing.CurrentValue;
        continuousRhythm = optRhythm.CurrentValue;
        debugDraw = optDebug.CurrentValue;
        return true;
    }


    // ------------------------------------------------------------------
    // Small vector helpers
    // ------------------------------------------------------------------

    /// <summary>World-XY unit vector perpendicular to a (unit) tangent.</summary>
    static Vector3d PerpXY(Vector3d tangent)
    {
        Vector3d perp = Vector3d.CrossProduct(tangent, Vector3d.ZAxis);
        if (perp.IsTiny())
            perp = Vector3d.CrossProduct(tangent, Vector3d.XAxis);
        perp.Unitize();
        return perp;
    }


    /// <summary>
    /// Which side of the infinite line through p with direction m the point
    /// x lies on: sign of the Z component of cross(m, x - p). +1 = left of
    /// m, -1 = right of m, 0 = (numerically) on the line.
    /// </summary>
    static int SideOfLine(Point3d x, Point3d p, Vector3d m)
    {
        double c = Vector3d.CrossProduct(m, x - p).Z;
        if (c > 1e-12) return 1;
        if (c < -1e-12) return -1;
        return 0;
    }


    // ------------------------------------------------------------------
    // Cut lines
    // ------------------------------------------------------------------

    /// <summary>
    /// Bisector of a corner. a = -tanIn points from the vertex back along
    /// the incoming segment, b = tanOut forward along the outgoing one; the
    /// cut line runs along m = (a + b).Normalize(), which splits the
    /// interior angle theta (between a and b) in half - like the miter of a
    /// picture frame. If a + b is (nearly) zero the segments are collinear,
    /// and the cut falls back to the perpendicular of the segment direction.
    /// </summary>
    static Vector3d GetBisector(Vector3d tanIn, Vector3d tanOut, out double theta)
    {
        Vector3d a = -tanIn;
        Vector3d b = tanOut;
        theta = Vector3d.VectorAngle(a, b); // interior angle, 0..pi

        Vector3d m = a + b;
        if (m.Length < 1e-9)
            return PerpXY(tanIn);
        m.Unitize();
        return m;
    }


    /// <summary>
    /// The shared miter cut at a vertex p1 between an incoming segment A
    /// (tangent tanIn at p1) and an outgoing segment B (tangent tanOut).
    /// Both segments use the same line; A keeps the side its own backward
    /// ray a lies on, B keeps the side of its forward ray b - these are
    /// always opposite, since m lies between them. This works the same for
    /// left and right turns, because the side is measured, not assumed.
    /// </summary>
    static void CornerCuts(Point3d p1, Vector3d tanIn, Vector3d tanOut, double halfWidth,
                           out CutLine endOfA, out CutLine startOfB, List<Line> debugLines)
    {
        Vector3d m = GetBisector(tanIn, tanOut, out double theta);

        // Cut-line length from the vertex to the band edge: halfWidth / sin(theta/2).
        double sinHalf = Math.Sin(0.5 * theta);
        double halfLength = sinHalf > 1e-9 ? halfWidth / sinHalf : double.PositiveInfinity;

        int keepA = SideOfLine(p1 - tanIn, p1, m);
        int keepB = SideOfLine(p1 + tanOut, p1, m);

        endOfA = new CutLine(p1, m, keepA, halfLength);
        startOfB = new CutLine(p1, m, keepB, halfLength);

        double thetaDeg = RhinoMath.ToDegrees(theta);
        if (thetaDeg < SharpAngleDeg)
            RhinoApp.WriteLine("SoftInsulation: very sharp corner ({0:0.#} deg) at {1} - miter tip is capped.", thetaDeg, p1);

        if (debugLines != null)
        {
            // Check: the cut line must meet BOTH band edges at the same
            // point, i.e. the edge point sits exactly halfWidth from either
            // segment's centerline. Both errors should be ~0.
            double shown = Math.Min(halfLength, MaxMiterReachFactor * 2.0 * halfWidth);
            Point3d edge = p1 + m * shown;
            double errA = Math.Abs(Vector3d.CrossProduct(tanIn, edge - p1).Z) - halfWidth;
            double errB = Math.Abs(Vector3d.CrossProduct(tanOut, edge - p1).Z) - halfWidth;
            RhinoApp.WriteLine(string.Format(
                "  corner {0}: theta={1:0.###} deg, m={2}, keepA={3}, keepB={4}, cutHalfLength={5:0.####}, edgeErrA={6:E2}, edgeErrB={7:E2}",
                p1, thetaDeg, m, keepA, keepB, halfLength, errA, errB));
            debugLines.Add(new Line(p1 - m * shown, p1 + m * shown));
        }
    }


    /// <summary>
    /// Perpendicular cap at an open path end (first / last vertex): the cut
    /// runs across the band at right angles, keeping the side the segment
    /// itself lies on.
    /// </summary>
    static CutLine EndCap(Point3d p, Vector3d tangent, bool isStart, double halfWidth, List<Line> debugLines)
    {
        Vector3d m = PerpXY(tangent);
        Point3d inside = isStart ? p + tangent : p - tangent;
        if (debugLines != null)
            debugLines.Add(new Line(p - m * halfWidth, p + m * halfWidth));
        return new CutLine(p, m, SideOfLine(inside, p, m), halfWidth);
    }


    /// <summary>
    /// How far a cut line reaches along the segment direction within the
    /// band: 0 for a perpendicular cap, halfWidth * cot(theta/2) for a
    /// miter. Capped for very sharp corners so the loop generation stays
    /// finite (the extreme miter tip then just stays empty).
    /// </summary>
    static double CutReach(CutLine cut, Vector3d tangent, double width, out bool capped)
    {
        double reach = cut.HalfLength * Math.Abs(cut.Dir * tangent);
        double max = MaxMiterReachFactor * width;
        capped = double.IsInfinity(reach) || reach > max;
        return capped ? max : reach;
    }


    // ------------------------------------------------------------------
    // Arc / line clipping
    // ------------------------------------------------------------------

    /// <summary>
    /// Angle parameters (sorted) where the arc crosses the cut line.
    ///
    /// A loop's arc sweeps well over 180 degrees around its own circle, so
    /// it bulges past its own start and end points - an arc whose two
    /// endpoints both sit on the same side of a cut line can still swing
    /// out past it and back in between. So the test uses the arc's own
    /// circle: intersecting a circle with a line is closed-form (foot of the
    /// center on the line, +/- the half chord), and ClosestParameter tells
    /// which of the (up to two) solutions genuinely lands on this arc.
    /// </summary>
    static List<double> ArcLineCrossings(Arc arc, CutLine cut)
    {
        var parameters = new List<double>();

        Point3d c = arc.Center;
        double r = arc.Radius;
        Point3d foot = cut.Origin + cut.Dir * ((c - cut.Origin) * cut.Dir);
        double dist = c.DistanceTo(foot);
        if (dist > r)
            return parameters;

        double halfChord = Math.Sqrt(Math.Max(0.0, r * r - dist * dist));
        double tol = 1e-6 * Math.Max(1.0, r);
        foreach (Point3d candidate in new[] { foot + cut.Dir * halfChord, foot - cut.Dir * halfChord })
        {
            double t = arc.ClosestParameter(candidate);
            if (arc.PointAt(t).DistanceTo(candidate) < tol)
                parameters.Add(t);
        }
        parameters.Sort();
        return parameters;
    }


    /// <summary>
    /// Clip a list of pattern arcs against one cut line:
    /// - arcs entirely on the kept side stay untouched,
    /// - arcs entirely on the other side are discarded,
    /// - arcs that cross the line are either trimmed to the sub-arc(s) on
    ///   the kept side (ending exactly on the line) or, with
    ///   trimCrossing = false, removed completely.
    /// </summary>
    static List<Arc> ClipSegmentPattern(List<Arc> arcs, CutLine cut, bool trimCrossing)
    {
        var kept = new List<Arc>();
        foreach (Arc arc in arcs)
        {
            Interval dom = arc.AngleDomain;
            List<double> crossings = ArcLineCrossings(arc, cut)
                .Where(t => t - dom.Min > 1e-9 && dom.Max - t > 1e-9)
                .ToList();

            if (crossings.Count == 0)
            {
                if (SideOfLine(arc.MidPoint, cut.Origin, cut.Dir) == cut.KeepSign)
                    kept.Add(arc);
                continue;
            }

            if (!trimCrossing)
                continue;

            var bounds = new List<double> { dom.Min };
            bounds.AddRange(crossings);
            bounds.Add(dom.Max);

            for (int i = 0; i < bounds.Count - 1; i++)
            {
                double t0 = bounds[i], t1 = bounds[i + 1];
                if (t1 - t0 < 1e-9)
                    continue;
                Point3d mid = arc.PointAt(0.5 * (t0 + t1));
                if (SideOfLine(mid, cut.Origin, cut.Dir) == cut.KeepSign)
                    kept.Add(new Arc(new Circle(arc.Plane, arc.Radius), new Interval(t0, t1)));
            }
        }
        return kept;
    }


    // ------------------------------------------------------------------
    // Pattern generation
    // ------------------------------------------------------------------

    /// <summary>
    /// Point and unit perpendicular (world XY) at arc-length distance s from
    /// the segment's start. Outside [0, length] the frame is extrapolated
    /// straight along the start/end tangent, so loops can be generated past
    /// both ends (needed on the outer side of a miter) and trimmed back.
    /// </summary>
    static void FrameAt(Curve seg, double s, double length, out Point3d pt, out Vector3d perp)
    {
        Vector3d tangent;
        if (s < 0.0)
        {
            tangent = seg.TangentAtStart;
            pt = seg.PointAtStart + tangent * s;
        }
        else if (s > length)
        {
            tangent = seg.TangentAtEnd;
            pt = seg.PointAtEnd + tangent * (s - length);
        }
        else
        {
            if (!seg.LengthParameter(s, out double t))
                t = seg.Domain.Max;
            pt = seg.PointAt(t);
            tangent = seg.TangentAt(t);
        }
        perp = PerpXY(tangent);
    }


    /// <summary>
    /// Build the insulation loops of ONE segment, between its start cut and
    /// end cut.
    ///
    /// Loop j sits at arc length j * spacing, measured from `phase` before
    /// the segment's start (phase = length of all previous segments for a
    /// continuous rhythm, 0 to restart at the cut line); even j bulge to one
    /// side, odd j to the other. Each loop is a single arc from the pinch
    /// point before it (midpoint of its center and the previous loop's), out
    /// through its tip on the band edge, to the pinch point after it.
    ///
    /// Loops are generated from well before the start to well past the end
    /// (by each cut's reach along the segment, plus a margin), and then
    /// EVERY loop is clipped against BOTH cut lines - so it never matters
    /// how far a miter line slants into the band.
    /// </summary>
    static List<Arc> BuildSegmentLoops(Curve seg, double width, double spacing, double phase,
                                       CutLine startCut, CutLine endCut, bool trimCrossing)
    {
        var arcs = new List<Arc>();

        double length = seg.GetLength();
        if (length <= RhinoMath.ZeroTolerance)
            return arcs;

        double halfWidth = 0.5 * width;
        // Center offset k from the centerline: the circle through both pinch
        // points (+/- spacing/2 on the centerline) and the tip (halfWidth
        // out) has its center at k = (w^2 - s^2) / (4w). Its radius is
        // halfWidth - k.
        double k = (width * width - spacing * spacing) / (4.0 * width);

        double reachStart = CutReach(startCut, seg.TangentAtStart, width, out _);
        double reachEnd = CutReach(endCut, seg.TangentAtEnd, width, out _);
        double margin = 2.0 * spacing + halfWidth;
        double sMin = -reachStart - margin;
        double sMax = length + reachEnd + margin;

        if (length < reachStart + reachEnd)
            RhinoApp.WriteLine(
                "SoftInsulation: segment at {0} is shorter than its two miters reach ({1:0.###} < {2:0.###}) - the corner may look approximate.",
                seg.PointAtStart, length, reachStart + reachEnd);

        int jMin = (int)Math.Floor((phase + sMin) / spacing);
        int jMax = (int)Math.Ceiling((phase + sMax) / spacing);

        // One extra loop position on each side, for the outer pinch points.
        int count = jMax - jMin + 3;
        var centers = new Point3d[count];
        var tips = new Point3d[count];
        for (int idx = 0; idx < count; idx++)
        {
            int j = jMin - 1 + idx;
            FrameAt(seg, j * spacing - phase, length, out Point3d pt, out Vector3d perp);
            double side = (j & 1) == 0 ? 1.0 : -1.0;
            centers[idx] = pt + perp * (side * k);
            tips[idx] = pt + perp * (side * halfWidth);
        }

        for (int idx = 1; idx < count - 1; idx++)
        {
            Point3d pinchBefore = (centers[idx - 1] + centers[idx]) * 0.5;
            Point3d pinchAfter = (centers[idx] + centers[idx + 1]) * 0.5;

            var arc = new Arc(pinchBefore, tips[idx], pinchAfter);
            if (!arc.IsValid)
                continue;

            var pieces = new List<Arc> { arc };
            pieces = ClipSegmentPattern(pieces, startCut, trimCrossing);
            pieces = ClipSegmentPattern(pieces, endCut, trimCrossing);
            arcs.AddRange(pieces);
        }

        return arcs;
    }


    /// <summary>
    /// Split the path into its segments: the straight pieces of a polyline,
    /// or the line/arc pieces of a polycurve (the Rhino equivalent of a
    /// polyline with bulges). Any other curve is a single segment.
    /// Zero-length pieces are dropped.
    /// </summary>
    static List<Curve> GetSegments(Curve path, double tol)
    {
        var segments = new List<Curve>();
        if (path.TryGetPolyline(out Polyline polyline))
        {
            for (int i = 0; i < polyline.SegmentCount; i++)
            {
                Line line = polyline.SegmentAt(i);
                if (line.Length > tol)
                    segments.Add(new LineCurve(line));
            }
            return segments;
        }

        Curve[] pieces = path.DuplicateSegments();
        if (pieces != null && pieces.Length > 0)
            segments.AddRange(pieces.Where(c => c.GetLength() > tol));
        else
            segments.Add(path);
        return segments;
    }


    /// <summary>
    /// Build the full insulation symbol for a polyline.
    /// </summary>
    public static List<Arc> BuildPattern(Polyline polyline, double width, double spacing,
                                         bool trimCrossing, bool continuousRhythm, List<Line> debugLines = null)
    {
        return BuildPattern(polyline.ToPolylineCurve(), width, spacing, trimCrossing, continuousRhythm, debugLines);
    }


    /// <summary>
    /// Build the full insulation symbol for a path curve.
    ///
    /// Every segment is built independently (BuildSegmentLoops), bounded by
    /// two cut lines: a perpendicular cap at an open path's first/last
    /// vertex, and at every interior vertex (plus the seam of a closed path)
    /// a miter line along the angle bisector, shared by the two segments
    /// that meet there. A keeps everything on its side of that line, B on
    /// the other side, so the two bands meet exactly - no overlap, no gap.
    /// </summary>
    public static List<Arc> BuildPattern(Curve path, double width, double spacing,
                                         bool trimCrossing, bool continuousRhythm, List<Line> debugLines = null)
    {
        double halfWidth = 0.5 * width;
        double tol = RhinoMath.SqrtEpsilon * Math.Max(1.0, width);

        List<Curve> segments = GetSegments(path, tol);
        int n = segments.Count;
        if (n == 0)
            return new List<Arc>();

        bool closed = path.IsClosed;
        var startCuts = new CutLine[n];
        var endCuts = new CutLine[n];

        // Corner (miter) cuts: between segment i-1 and i, and - for a
        // closed path - also between the last segment and the first.
        for (int i = 0; i < n; i++)
        {
            if (i == 0 && !closed)
                continue;
            int prev = (i - 1 + n) % n;
            CornerCuts(segments[i].PointAtStart, segments[prev].TangentAtEnd, segments[i].TangentAtStart,
                       halfWidth, out endCuts[prev], out startCuts[i], debugLines);
        }

        // Perpendicular caps at an open path's first and last vertex.
        if (!closed)
        {
            startCuts[0] = EndCap(segments[0].PointAtStart, segments[0].TangentAtStart, true, halfWidth, debugLines);
            endCuts[n - 1] = EndCap(segments[n - 1].PointAtEnd, segments[n - 1].TangentAtEnd, false, halfWidth, debugLines);
        }

        var arcs = new List<Arc>();
        double cumulative = 0.0;
        for (int i = 0; i < n; i++)
        {
            double phase = continuousRhythm ? cumulative : 0.0;
            arcs.AddRange(BuildSegmentLoops(segments[i], width, spacing, phase,
                                            startCuts[i], endCuts[i], trimCrossing));
            cumulative += segments[i].GetLength();
        }

        return arcs;
    }


    public static void Run(RhinoDoc doc)
    {
        LoadConfig();

        string debugLayerName = LayerName + "_debug";
        ClearLayer(doc, LayerName);
        ClearLayer(doc, debugLayerName);

        Curve path = GetPathCurve();
        if (path == null)
        {
            RhinoApp.WriteLine("Cancelled.");
            return;
        }

        if (!GetSettings(out double width, out double spacing, out bool trimCrossing,
                         out bool continuousRhythm, out bool debugDraw))
        {
            RhinoApp.WriteLine("Cancelled.");
            return;
        }

        if (spacing <= 0.0)
            spacing = width / (2.0 + Sqrt3);
        if (spacing >= width)
        {
            RhinoApp.WriteLine("SoftInsulation: spacing ({0}) must be smaller than the width ({1}).", spacing, width);
            return;
        }

        List<Line> debugLines = debugDraw ? new List<Line>() : null;
        if (debugDraw)
            RhinoApp.WriteLine("SoftInsulation debug: width={0}, spacing={1}, loop radius={2:0.####}",
                               width, spacing, 0.5 * width - (width * width - spacing * spacing) / (4.0 * width));

        List<Arc> arcs = BuildPattern(path, width, spacing, trimCrossing, continuousRhythm, debugLines);
        if (arcs.Count == 0)
        {
            RhinoApp.WriteLine("SoftInsulation: could not build the loop chain.");
            return;
        }

        // Everything below is added as one undo step (the Rhino counterpart
        // of a database transaction).
        uint undoRecord = doc.BeginUndoRecord("Soft insulation");
        try
        {
            int layerIndex = EnsureLayer(doc, LayerName, System.Drawing.Color.FromArgb(255, 120, 170, 255));
            var attrs = new ObjectAttributes { LayerIndex = layerIndex };

            List<Curve> arcCurves = arcs.Select(a => (Curve)new ArcCurve(a)).ToList();
            Curve[] joined = Curve.JoinCurves(arcCurves, 0.01);
            IEnumerable<Curve> finalCurves = joined != null && joined.Length > 0 ? joined : arcCurves;

            var objIds = finalCurves.Select(crv => doc.Objects.AddCurve(crv, attrs)).ToList();

            if (objIds.Count > 1)
            {
                int groupIndex = doc.Groups.Add();
                foreach (Guid objId in objIds)
                    doc.Groups.AddToGroup(groupIndex, objId);
            }

            if (debugLines != null && debugLines.Count > 0)
            {
                int debugLayer = EnsureLayer(doc, debugLayerName, System.Drawing.Color.FromArgb(255, 255, 80, 80));
                var debugAttrs = new ObjectAttributes { LayerIndex = debugLayer };
                foreach (Line line in debugLines)
                    doc.Objects.AddLine(line, debugAttrs);
            }
        }
        finally
        {
            if (undoRecord != 0)
                doc.EndUndoRecord(undoRecord);
        }

        doc.Views.Redraw();

        RhinoApp.WriteLine("SoftInsulation: loop chain added on layer '{0}'.", LayerName);
    }
}

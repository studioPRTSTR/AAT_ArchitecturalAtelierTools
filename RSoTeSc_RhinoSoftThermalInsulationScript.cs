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
/// A cut line for _ClipToLine: the infinite line through Point,
/// perpendicular to Tangent, keeping the side with positive (or negative)
/// signed progress along Tangent.
/// </summary>
struct Cut
{
    public Point3d Point;
    public Vector3d Tangent;
    public bool KeepPositive;

    public Cut(Point3d point, Vector3d tangent, bool keepPositive)
    {
        Point = point;
        Tangent = tangent;
        KeepPositive = keepPositive;
    }
}


static class SoftInsulation
{
    const string ConfigFilename = "RSoTeSc_config.json";

    static string LayerName = "A_Isolation_01";
    static double DefaultWidth = 0.5;

    static readonly double Sqrt3 = Math.Sqrt(3.0);


    /// <summary>
    /// Read layer_name/default_width from RSoTeSc_config.json next to this
    /// script, so they can be tweaked without editing the script itself -
    /// handy while testing in Rhino's script editor. Falls back to the
    /// built-in defaults if the file is missing or malformed.
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

            // Tiny hand-rolled reader for the two flat keys we care about,
            // so the script needs no JSON library reference.
            string json = File.ReadAllText(configPath);

            var nameMatch = Regex.Match(json, "\"layer_name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (nameMatch.Success)
                LayerName = Regex.Unescape(nameMatch.Groups[1].Value);

            var widthMatch = Regex.Match(json, "\"default_width\"\\s*:\\s*([-+0-9.eE]+)");
            if (widthMatch.Success &&
                double.TryParse(widthMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                DefaultWidth = w;
        }
        catch (Exception)
        {
        }
    }


    /// <summary>
    /// Delete any objects already on the Insulation-Soft layer before
    /// drawing new ones, so leftover geometry from earlier test runs can
    /// never get mixed in with (or mistaken for) the current run's output.
    /// </summary>
    static void ClearPreviousOutput(RhinoDoc doc)
    {
        int layerIndex = doc.Layers.FindByFullPath(LayerName, -1);
        if (layerIndex < 0)
            return;
        var existing = doc.Objects
            .Where(obj => obj.Attributes.LayerIndex == layerIndex)
            .Select(obj => obj.Id)
            .ToList();
        foreach (Guid objId in existing)
            doc.Objects.Delete(objId, true);
    }


    /// <summary>Create (or reuse) a dedicated layer for the insulation symbol.</summary>
    static int EnsureLayer(RhinoDoc doc)
    {
        int layerIndex = doc.Layers.FindByFullPath(LayerName, -1);
        if (layerIndex >= 0)
            return layerIndex;
        var layer = new Layer
        {
            Name = LayerName,
            Color = System.Drawing.Color.FromArgb(255, 120, 170, 255),
            PlotWeight = 0.13, // thin hairline, mm
        };
        return doc.Layers.Add(layer);
    }


    /// <summary>
    /// Let the user either pick an existing curve to use as the insulation
    /// path, or - if they press Enter without selecting anything - draw a
    /// new line interactively to define the direction.
    /// </summary>
    static Curve GetPathCurve()
    {
        var go = new GetObject();
        go.SetCommandPrompt("Select a path curve for the insulation run (Enter to draw a new line instead)");
        go.GeometryFilter = ObjectType.Curve;
        go.AcceptNothing(true);
        GetResult result = go.Get();

        if (result == GetResult.Object)
            return go.Object(0).Curve()?.DuplicateCurve();

        if (result == GetResult.Nothing)
        {
            if (RhinoGet.GetLine(out Line line) != Result.Success)
                return null;
            return line.ToNurbsCurve();
        }

        return null;
    }


    static double? GetWidth()
    {
        var gn = new GetNumber();
        gn.SetCommandPrompt("Width of the insulation band (tip to tip)");
        gn.SetDefaultNumber(DefaultWidth);
        gn.SetLowerLimit(0.001, false);
        gn.Get();
        if (gn.CommandResult() != Result.Success)
            return null;
        return gn.Number();
    }


    /// <summary>Point and unit tangent at the path's actual endpoint.</summary>
    static void EndFrame(Curve path, out Point3d endPt, out Vector3d endTangent)
    {
        double tEnd = path.Domain.Max;
        endPt = path.PointAt(tEnd);
        endTangent = path.TangentAt(tEnd);
        endTangent.Unitize();
    }


    /// <summary>Point and unit tangent at the path's actual start.</summary>
    static void StartFrame(Curve path, out Point3d startPt, out Vector3d startTangent)
    {
        double tStart = path.Domain.Min;
        startPt = path.PointAt(tStart);
        startTangent = path.TangentAt(tStart);
        startTangent.Unitize();
    }


    /// <summary>World-XY unit vector perpendicular to a (unit) path tangent.</summary>
    static Vector3d Perp(Vector3d tangent)
    {
        Vector3d perp = Vector3d.CrossProduct(tangent, Vector3d.ZAxis);
        if (perp.IsTiny())
            perp = Vector3d.CrossProduct(tangent, Vector3d.XAxis);
        perp.Unitize();
        return perp;
    }


    /// <summary>
    /// Point and unit perpendicular (world XY) at arc-length distance s from
    /// the start of the path. If s runs past the path's actual length, the
    /// frame is linearly extrapolated from the end point/tangent so loop
    /// generation can safely overshoot the path.
    /// </summary>
    static void FrameAtLength(Curve path, double s, double length, Point3d endPt, Vector3d endTangent,
                              out Point3d pt, out Vector3d perp)
    {
        Vector3d tangent;
        if (s <= length)
        {
            if (!path.LengthParameter(s, out double t))
                t = path.Domain.Max;
            pt = path.PointAt(t);
            tangent = path.TangentAt(t);
            tangent.Unitize();
        }
        else
        {
            pt = endPt + endTangent * (s - length);
            tangent = endTangent;
        }

        perp = Perp(tangent);
    }


    /// <summary>
    /// How far pt sits past the path's endpoint, measured along the
    /// end tangent direction. Positive = beyond the path's end.
    /// </summary>
    static double SignedProgress(Point3d pt, Point3d endPt, Vector3d endTangent)
    {
        return (pt - endPt) * endTangent;
    }


    /// <summary>
    /// Curve parameters (sorted) where crv - a circular arc of the given
    /// center/radius, lying in the world-XY plane - crosses the infinite
    /// line through cutPt perpendicular to cutTangent.
    ///
    /// A loop's arc sweeps close to 300 degrees around its own circle, which
    /// means it actually bulges up to half a loop-width past its own nominal
    /// start and end points before settling there (needed so neighboring
    /// loops interlock cleanly) - so an arc whose two endpoints both sit
    /// safely on the same side of a cutoff line can still swing out past it
    /// and back in between. The only reliable check is the arc's own circle
    /// geometry: intersecting a circle with a line is a closed-form
    /// quadratic (no sampling needed), and Curve.ClosestPoint recovers
    /// whichever of the (up to two) solutions genuinely lands on this arc.
    /// </summary>
    static List<double> LineCrossings(Curve crv, Point3d center, double radius, Point3d cutPt, Vector3d cutTangent)
    {
        var parameters = new List<double>();

        double offset = SignedProgress(center, cutPt, cutTangent);
        double halfChordSq = radius * radius - offset * offset;
        if (halfChordSq < 0.0)
            return parameters;

        double halfChord = Math.Sqrt(halfChordSq);
        Vector3d perpDir = Perp(cutTangent);
        Point3d basePt = center - cutTangent * offset;

        foreach (Point3d candidate in new[] { basePt + perpDir * halfChord, basePt - perpDir * halfChord })
        {
            if (crv.ClosestPoint(candidate, out double t) && crv.PointAt(t).DistanceTo(candidate) < 1e-6)
                parameters.Add(t);
        }
        parameters.Sort();
        return parameters;
    }


    /// <summary>
    /// Cut crv (a circular arc of the given center/radius) against the
    /// infinite line of cut, keeping only the sub-arc(s) where signed
    /// progress along cut.Tangent is &gt; 0 if cut.KeepPositive, else &lt; 0.
    /// Returns a list of kept pieces (may be empty, exactly one, or more
    /// than one if the arc crosses the line more than once).
    /// </summary>
    static List<Curve> ClipToLine(Curve crv, Point3d center, double radius, Cut cut)
    {
        Interval dom = crv.Domain;
        var bounds = new List<double> { dom.Min };
        bounds.AddRange(LineCrossings(crv, center, radius, cut.Point, cut.Tangent));
        bounds.Add(dom.Max);

        var kept = new List<Curve>();
        for (int i = 0; i < bounds.Count - 1; i++)
        {
            double t0 = bounds[i], t1 = bounds[i + 1];
            if (t1 - t0 < 1e-9)
                continue;
            Point3d midPt = crv.PointAt(0.5 * (t0 + t1));
            if ((SignedProgress(midPt, cut.Point, cut.Tangent) > 0.0) == cut.KeepPositive)
            {
                Curve piece = crv.Trim(t0, t1);
                if (piece != null)
                    kept.Add(piece);
            }
        }
        return kept;
    }


    /// <summary>
    /// The miter cutting line for a corner: direction bisecting the angle
    /// between the incoming segment's backward ray (-tanIn) and the
    /// outgoing segment's forward ray (tanOut) - the standard line-join
    /// miter direction, same idea as a mitered corner on a picture frame.
    ///
    /// cutTangent is the normal to that line (for reuse with ClipToLine/
    /// SignedProgress), and incomingPositive says which sign of progress
    /// along it corresponds to the incoming segment's side, found by testing
    /// a point just before the corner. Returns false if the two directions
    /// are (near) identical, i.e. not really a corner.
    /// </summary>
    static bool CornerCut(Point3d cornerPt, Vector3d tanIn, Vector3d tanOut,
                          out Vector3d cutTangent, out bool incomingPositive)
    {
        cutTangent = Vector3d.Unset;
        incomingPositive = false;

        Vector3d bisector = tanOut - tanIn;
        if (bisector.IsTiny())
            return false;
        bisector.Unitize();
        cutTangent = Perp(bisector);

        Point3d probePt = cornerPt - tanIn;
        incomingPositive = SignedProgress(probePt, cornerPt, cutTangent) > 0.0;
        return true;
    }


    /// <summary>
    /// Build the FULL insulation symbol along a single (straight) segment,
    /// from startCut to endCut - each a line to clip the segment's own
    /// first/last loop against, exactly what ClipToLine expects. Passing the
    /// path's own true start/end frame (perpendicular cutoff) reproduces the
    /// ordinary end cap; passing a corner's miter line instead makes this
    /// segment's end stop exactly at the angle bisector shared with its
    /// neighbor.
    ///
    /// Every loop is first built as a clean, complete circle-arc: the arc
    /// between two consecutive "pinch points" (where neighboring loops meet,
    /// sitting exactly on this segment's centerline), bulging out through
    /// its own outer tip. A virtual center placed one loop-width before the
    /// segment's start supplies the pinch point that opens the very first
    /// loop, so every loop - including the first and last - is built
    /// identically. Only THEN, whichever loop actually reaches startCut or
    /// endCut gets cut back: its own circle (known center and radius r) is
    /// intersected exactly with the cutting line (see ClipToLine), and
    /// trimmed to the crossing point(s). Loops entirely outside the segment
    /// are dropped; every fully-interior loop is left untouched.
    /// </summary>
    static List<Curve> BuildSegmentLoops(Curve path, double width, Cut startCut, Cut endCut)
    {
        var arcs = new List<Curve>();

        double length = path.GetLength();
        if (length <= 0)
            return arcs;

        double r = width / (2.0 + Sqrt3);
        double d = r * Sqrt3 / 2.0;
        double a = r;

        EndFrame(path, out Point3d endPt, out Vector3d endTangent);
        StartFrame(path, out Point3d startPt, out Vector3d startTangent);

        // Overshoot generously past the segment's end so there's always at
        // least one full loop past it to trim back from.
        int nGuess = Math.Max((int)Math.Ceiling(length / a) + 5, 3);

        var centers = new List<Point3d>();
        var perps = new List<Vector3d>();
        var sides = new List<double>();
        for (int i = 0; i <= nGuess; i++)
        {
            double s = i * a;
            FrameAtLength(path, s, length, endPt, endTangent, out Point3d pt, out Vector3d perp);
            double side = i % 2 == 0 ? 1.0 : -1.0;
            centers.Add(pt + perp * (side * d));
            perps.Add(perp);
            sides.Add(side);
        }

        Point3d virtualPrevCenter = (startPt - startTangent * a) + Perp(startTangent) * (-1.0 * d);

        // boundaryPts[i] is the pinch point before loop i; boundaryPts[i+1]
        // is the pinch point after it - both on the segment's centerline.
        var boundaryPts = new List<Point3d> { (virtualPrevCenter + centers[0]) * 0.5 };
        for (int i = 0; i < nGuess; i++)
            boundaryPts.Add((centers[i] + centers[i + 1]) * 0.5);

        for (int i = 0; i < nGuess; i++)
        {
            double sI = i * a;
            if (sI > length + a)
            {
                // Every later loop is only further along still - nothing
                // left to build.
                break;
            }

            Vector3d perp = perps[i];
            double side = sides[i];
            Point3d center = centers[i];
            Point3d outerTip = center + perp * (side * r);
            Point3d segStart = boundaryPts[i];
            Point3d segEnd = boundaryPts[i + 1];

            var arc = new Arc(segStart, outerTip, segEnd);
            if (!arc.IsValid)
                continue;
            Curve arcCrv = arc.ToNurbsCurve();
            if (arcCrv == null)
                continue;

            // Only ever clip the loop(s) actually next to this segment's own
            // start/end, decided by arc-length position (sI vs. 0/length) -
            // never by the loop's spatial (Euclidean) position (see
            // LineCrossings for why a single loop's own geometry already
            // needs this exact-crossing treatment).
            var pieces = new List<Curve> { arcCrv };
            if (sI <= a)
                pieces = pieces.SelectMany(piece => ClipToLine(piece, center, r, startCut)).ToList();
            if (sI >= length - a)
                pieces = pieces.SelectMany(piece => ClipToLine(piece, center, r, endCut)).ToList();

            arcs.AddRange(pieces);
        }

        return arcs;
    }


    /// <summary>
    /// Build the FULL insulation symbol for the whole path.
    ///
    /// A polyline is broken into its individual straight segments, and each
    /// is built independently as its own clean, complete loop chain
    /// (BuildSegmentLoops) - the path's true start/end use the usual
    /// perpendicular cap, while every interior vertex uses a miter (angle-
    /// bisector) cap shared by the two segments that meet there, so they
    /// never overlap or leave a gap. A path with no real corners (or that
    /// isn't a literal polyline) is just built as a single segment.
    /// </summary>
    static List<Curve> BuildLoopChain(Curve path, double width)
    {
        var vertices = path.TryGetPolyline(out Polyline polyline)
            ? polyline.ToList()
            : new List<Point3d>();

        if (vertices.Count < 3)
        {
            StartFrame(path, out Point3d startPt, out Vector3d startTangent);
            EndFrame(path, out Point3d endPt, out Vector3d endTangent);
            return BuildSegmentLoops(
                path, width,
                new Cut(startPt, startTangent, true),
                new Cut(endPt, endTangent, false));
        }

        var arcs = new List<Curve>();
        for (int i = 0; i < vertices.Count - 1; i++)
        {
            var seg = new LineCurve(vertices[i], vertices[i + 1]);
            StartFrame(seg, out Point3d segStartPt, out Vector3d segStartTangent);
            EndFrame(seg, out Point3d segEndPt, out Vector3d segEndTangent);

            Cut startCut;
            if (i == 0)
            {
                startCut = new Cut(segStartPt, segStartTangent, true);
            }
            else
            {
                EndFrame(new LineCurve(vertices[i - 1], vertices[i]), out _, out Vector3d prevTangent);
                if (CornerCut(vertices[i], prevTangent, segStartTangent, out Vector3d cutTangent, out bool incomingPositive))
                    startCut = new Cut(vertices[i], cutTangent, !incomingPositive);
                else
                    startCut = new Cut(segStartPt, segStartTangent, true);
            }

            Cut endCut;
            if (i == vertices.Count - 2)
            {
                endCut = new Cut(segEndPt, segEndTangent, false);
            }
            else
            {
                StartFrame(new LineCurve(vertices[i + 1], vertices[i + 2]), out _, out Vector3d nextTangent);
                if (CornerCut(vertices[i + 1], segEndTangent, nextTangent, out Vector3d cutTangent, out bool incomingPositive))
                    endCut = new Cut(vertices[i + 1], cutTangent, incomingPositive);
                else
                    endCut = new Cut(segEndPt, segEndTangent, false);
            }

            arcs.AddRange(BuildSegmentLoops(seg, width, startCut, endCut));
        }

        return arcs;
    }


    public static void Run(RhinoDoc doc)
    {
        LoadConfig();

        ClearPreviousOutput(doc);

        Curve path = GetPathCurve();
        if (path == null)
        {
            RhinoApp.WriteLine("Cancelled.");
            return;
        }

        double? width = GetWidth();
        if (width == null)
        {
            RhinoApp.WriteLine("Cancelled.");
            return;
        }

        List<Curve> arcs = BuildLoopChain(path, width.Value);
        if (arcs.Count == 0)
        {
            RhinoApp.WriteLine("SoftInsulation: could not build the loop chain.");
            return;
        }

        int layerIndex = EnsureLayer(doc);
        var attrs = new ObjectAttributes { LayerIndex = layerIndex };

        Curve[] joined = Curve.JoinCurves(arcs, 0.01);
        IEnumerable<Curve> finalCurves = joined != null && joined.Length > 0 ? joined : arcs;

        var objIds = finalCurves.Select(crv => doc.Objects.AddCurve(crv, attrs)).ToList();

        if (objIds.Count > 1)
        {
            int groupIndex = doc.Groups.Add();
            foreach (Guid objId in objIds)
                doc.Groups.AddToGroup(groupIndex, objId);
        }

        doc.Views.Redraw();

        RhinoApp.WriteLine("SoftInsulation: loop chain added on layer '{0}'.", LayerName);
    }
}

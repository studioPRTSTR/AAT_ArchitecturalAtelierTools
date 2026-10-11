// #! csharp
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


enum CornerMode
{
    Miter,
    ButtIncoming,
    ButtOutgoing,
}


struct CutLine
{
    public Point3d Origin;
    public Vector3d Dir;
    public int KeepSign;

    public CutLine(Point3d origin, Vector3d dir, int keepSign)
    {
        Origin = origin;
        Dir = dir;
        KeepSign = keepSign;
    }
}


static class SoftInsulation
{
    const string PluginName = "ArchitecturalAtelierTools";
    const string ConfigFilename = "SoftInsulation.json";

    static string LayerName = "A_Isolation_01";
    static double DefaultWidth = 0.5;
    static double DefaultSpacing = 0.0;
    static bool TrimCrossing = true;
    static bool ContinuousRhythm = true;
    static CornerMode DefaultCornerMode = CornerMode.Miter;

    static readonly double Sqrt3 = Math.Sqrt(3.0);
    static readonly string[] CornerModeNames = { "Miter", "ButtIncoming", "ButtOutgoing" };

    const double SharpAngleDeg = 15.0;
    const double ButtMinTurnDeg = 15.0;
    const double MaxMiterReachFactor = 4.0;


    static IEnumerable<string> ConfigCandidates(string scriptPath)
    {
        yield return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), PluginName, ConfigFilename);

        string assemblyDir = null;
        try { assemblyDir = Path.GetDirectoryName(typeof(SoftInsulation).Assembly.Location); } catch (Exception) { }
        if (!string.IsNullOrEmpty(assemblyDir))
            yield return Path.Combine(assemblyDir, ConfigFilename);

        string scriptDir = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(scriptDir))
            yield return Path.Combine(scriptDir, ConfigFilename);
    }


    static void LoadConfig([CallerFilePath] string scriptPath = "")
    {
        try
        {
            string configPath = ConfigCandidates(scriptPath).FirstOrDefault(File.Exists);
            if (configPath == null)
                return;

            string json = File.ReadAllText(configPath);

            LayerName = ReadString(json, "layer_name", LayerName);
            DefaultWidth = ReadNumber(json, "default_width", DefaultWidth);
            DefaultSpacing = ReadNumber(json, "default_spacing", DefaultSpacing);
            TrimCrossing = ReadBool(json, "trim_crossing", TrimCrossing);
            ContinuousRhythm = ReadBool(json, "continuous_rhythm", ContinuousRhythm);

            switch (ReadString(json, "corner_mode", "").ToLowerInvariant().Replace("_", ""))
            {
                case "miter": DefaultCornerMode = CornerMode.Miter; break;
                case "buttincoming": DefaultCornerMode = CornerMode.ButtIncoming; break;
                case "buttoutgoing": DefaultCornerMode = CornerMode.ButtOutgoing; break;
            }
        }
        catch (Exception)
        {
        }
    }

    static string ReadString(string json, string key, string fallback)
    {
        var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : fallback;
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


    static int EnsureLayer(RhinoDoc doc, string layerName, System.Drawing.Color color)
    {
        int layerIndex = doc.Layers.FindByFullPath(layerName, -1);
        if (layerIndex >= 0)
            return layerIndex;
        var layer = new Layer
        {
            Name = layerName,
            Color = color,
            PlotWeight = 0.13,
        };
        return doc.Layers.Add(layer);
    }


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


    static bool GetSettings(out double width, out double spacing, out bool trimCrossing,
                            out bool continuousRhythm, out CornerMode cornerMode, out bool debugDraw)
    {
        width = DefaultWidth;
        spacing = DefaultSpacing;
        trimCrossing = TrimCrossing;
        continuousRhythm = ContinuousRhythm;
        cornerMode = DefaultCornerMode;
        debugDraw = false;

        var optSpacing = new OptionDouble(DefaultSpacing, 0.0, 1.0e9);
        var optCrossing = new OptionToggle(TrimCrossing, "Remove", "Trim");
        var optRhythm = new OptionToggle(ContinuousRhythm, "Restart", "Continuous");
        var optDebug = new OptionToggle(false, "Off", "On");
        int cornerIndex = (int)DefaultCornerMode;

        var gn = new GetNumber();
        gn.SetCommandPrompt("Width of the insulation band (tip to tip)");
        gn.SetDefaultNumber(DefaultWidth);
        gn.SetLowerLimit(0.001, false);
        gn.AcceptNothing(true);

        while (true)
        {
            gn.ClearCommandOptions();
            gn.AddOptionDouble("Spacing", ref optSpacing, "Loop spacing (0 = auto)");
            int cornerOpt = gn.AddOptionList("Corner", CornerModeNames, cornerIndex);
            gn.AddOptionToggle("CrossingLoops", ref optCrossing);
            gn.AddOptionToggle("Rhythm", ref optRhythm);
            gn.AddOptionToggle("DebugDraw", ref optDebug);

            GetResult res = gn.Get();
            if (res == GetResult.Option)
            {
                if (gn.OptionIndex() == cornerOpt)
                    cornerIndex = gn.Option().CurrentListOptionIndex;
                continue;
            }
            if (res == GetResult.Number)
            {
                width = gn.Number();
                break;
            }
            if (res == GetResult.Nothing)
                break;
            return false;
        }

        spacing = optSpacing.CurrentValue;
        trimCrossing = optCrossing.CurrentValue;
        continuousRhythm = optRhythm.CurrentValue;
        cornerMode = (CornerMode)cornerIndex;
        debugDraw = optDebug.CurrentValue;
        return true;
    }


    static Vector3d PerpXY(Vector3d tangent)
    {
        Vector3d perp = Vector3d.CrossProduct(tangent, Vector3d.ZAxis);
        if (perp.IsTiny())
            perp = Vector3d.CrossProduct(tangent, Vector3d.XAxis);
        perp.Unitize();
        return perp;
    }


    static int SideOfLine(Point3d x, Point3d p, Vector3d m)
    {
        double c = Vector3d.CrossProduct(m, x - p).Z;
        if (c > 1e-12) return 1;
        if (c < -1e-12) return -1;
        return 0;
    }


    static double CrossingParam(CutLine cut, Point3d q, Vector3d dir)
    {
        double denom = Vector3d.CrossProduct(cut.Dir, dir).Z;
        if (Math.Abs(denom) < 1e-12)
            return double.PositiveInfinity;
        return Vector3d.CrossProduct(cut.Dir, cut.Origin - q).Z / denom;
    }


    static void AddDebugCut(List<Line> debugLines, CutLine cut, double halfLength)
    {
        if (debugLines != null)
            debugLines.Add(new Line(cut.Origin - cut.Dir * halfLength, cut.Origin + cut.Dir * halfLength));
    }


    static Vector3d GetBisector(Vector3d tanIn, Vector3d tanOut, out double theta)
    {
        Vector3d a = -tanIn;
        Vector3d b = tanOut;
        theta = Vector3d.VectorAngle(a, b);

        Vector3d m = a + b;
        if (m.Length < 1e-9)
            return PerpXY(tanIn);
        m.Unitize();
        return m;
    }


    static void MiterCuts(Point3d p1, Vector3d tanIn, Vector3d tanOut, double halfWidth,
                          out CutLine endOfA, out CutLine startOfB, List<Line> debugLines)
    {
        Vector3d m = GetBisector(tanIn, tanOut, out double theta);

        double sinHalf = Math.Sin(0.5 * theta);
        double halfLength = sinHalf > 1e-9 ? halfWidth / sinHalf : double.PositiveInfinity;

        int keepA = SideOfLine(p1 - tanIn, p1, m);
        int keepB = SideOfLine(p1 + tanOut, p1, m);

        endOfA = new CutLine(p1, m, keepA);
        startOfB = new CutLine(p1, m, keepB);

        double thetaDeg = RhinoMath.ToDegrees(theta);
        if (thetaDeg < SharpAngleDeg)
            RhinoApp.WriteLine("SoftInsulation: very sharp corner ({0:0.#} deg) at {1} - miter tip is capped.", thetaDeg, p1);

        if (debugLines != null)
        {
            double shown = Math.Min(halfLength, MaxMiterReachFactor * 2.0 * halfWidth);
            Point3d edge = p1 + m * shown;
            double errA = Math.Abs(Vector3d.CrossProduct(tanIn, edge - p1).Z) - halfWidth;
            double errB = Math.Abs(Vector3d.CrossProduct(tanOut, edge - p1).Z) - halfWidth;
            RhinoApp.WriteLine(string.Format(
                "  miter {0}: theta={1:0.###} deg, m={2}, keepA={3}, keepB={4}, cutHalfLength={5:0.####}, edgeErrA={6:E2}, edgeErrB={7:E2}",
                p1, thetaDeg, m, keepA, keepB, halfLength, errA, errB));
            AddDebugCut(debugLines, endOfA, shown);
        }
    }


    static void ButtCuts(Point3d p1, Vector3d tanIn, Vector3d tanOut, double halfWidth, bool incomingRunsThrough,
                         out CutLine endOfA, out CutLine startOfB, List<Line> debugLines)
    {
        Vector3d nToB = PerpXY(tanIn);
        if (nToB * tanOut < 0.0)
            nToB = -nToB;
        Vector3d nToA = PerpXY(tanOut);
        if (nToA * -tanIn < 0.0)
            nToA = -nToA;

        if (incomingRunsThrough)
        {
            Point3d farEdgeB = p1 - nToA * halfWidth;
            endOfA = new CutLine(farEdgeB, tanOut, SideOfLine(farEdgeB + nToA, farEdgeB, tanOut));
            Point3d nearEdgeA = p1 + nToB * halfWidth;
            startOfB = new CutLine(nearEdgeA, tanIn, SideOfLine(nearEdgeA + nToB, nearEdgeA, tanIn));
        }
        else
        {
            Point3d nearEdgeB = p1 + nToA * halfWidth;
            endOfA = new CutLine(nearEdgeB, tanOut, SideOfLine(nearEdgeB + nToA, nearEdgeB, tanOut));
            Point3d farEdgeA = p1 - nToB * halfWidth;
            startOfB = new CutLine(farEdgeA, tanIn, SideOfLine(farEdgeA + nToB, farEdgeA, tanIn));
        }

        if (debugLines != null)
        {
            double sinTheta = Math.Abs(Vector3d.CrossProduct(tanIn, tanOut).Z);
            double shown = Math.Min(2.0 * halfWidth / sinTheta + halfWidth, MaxMiterReachFactor * 2.0 * halfWidth);
            RhinoApp.WriteLine(string.Format(
                "  butt {0}: runsThrough={1}, nToA={2}, nToB={3}, keepA={4}, keepB={5}",
                p1, incomingRunsThrough ? "incoming" : "outgoing", nToA, nToB, endOfA.KeepSign, startOfB.KeepSign));
            AddDebugCut(debugLines, endOfA, shown);
            AddDebugCut(debugLines, startOfB, shown);
        }
    }


    static void CornerCuts(Point3d p1, Vector3d tanIn, Vector3d tanOut, double halfWidth, CornerMode mode,
                           out CutLine endOfA, out CutLine startOfB, List<Line> debugLines)
    {
        if (mode != CornerMode.Miter)
        {
            double thetaDeg = RhinoMath.ToDegrees(Vector3d.VectorAngle(-tanIn, tanOut));
            if (thetaDeg >= SharpAngleDeg && 180.0 - thetaDeg >= ButtMinTurnDeg)
            {
                ButtCuts(p1, tanIn, tanOut, halfWidth, mode == CornerMode.ButtIncoming,
                         out endOfA, out startOfB, debugLines);
                return;
            }
            RhinoApp.WriteLine("SoftInsulation: no butt joint possible at {0} ({1:0.#} deg) - using miter.", p1, thetaDeg);
        }

        MiterCuts(p1, tanIn, tanOut, halfWidth, out endOfA, out startOfB, debugLines);
    }


    static CutLine EndCap(Point3d p, Vector3d tangent, bool isStart, double halfWidth, List<Line> debugLines)
    {
        Vector3d m = PerpXY(tangent);
        Point3d inside = isStart ? p + tangent : p - tangent;
        var cut = new CutLine(p, m, SideOfLine(inside, p, m));
        AddDebugCut(debugLines, cut, halfWidth);
        return cut;
    }


    static double CutReach(CutLine cut, Point3d endPt, Vector3d tangent, double halfWidth, double width)
    {
        Vector3d perp = PerpXY(tangent);
        double reach = 0.0;
        foreach (double e in new[] { -halfWidth, halfWidth })
        {
            double u = CrossingParam(cut, endPt + perp * e, tangent);
            reach = Math.Max(reach, Math.Abs(u));
        }
        double max = MaxMiterReachFactor * width;
        return double.IsInfinity(reach) || reach > max ? max : reach;
    }


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


    static List<Arc> BuildSegmentLoops(Curve seg, double width, double spacing, double phase,
                                       CutLine startCut, CutLine endCut, bool trimCrossing)
    {
        var arcs = new List<Arc>();

        double length = seg.GetLength();
        if (length <= RhinoMath.ZeroTolerance)
            return arcs;

        double halfWidth = 0.5 * width;
        double k = (width * width - spacing * spacing) / (4.0 * width);

        double reachStart = CutReach(startCut, seg.PointAtStart, seg.TangentAtStart, halfWidth, width);
        double reachEnd = CutReach(endCut, seg.PointAtEnd, seg.TangentAtEnd, halfWidth, width);
        double margin = 2.0 * spacing + halfWidth;
        double sMin = -reachStart - margin;
        double sMax = length + reachEnd + margin;

        if (length < reachStart + reachEnd)
            RhinoApp.WriteLine(
                "SoftInsulation: segment at {0} is shorter than its two cuts reach ({1:0.###} < {2:0.###}) - the corner may look approximate.",
                seg.PointAtStart, length, reachStart + reachEnd);

        int jMin = (int)Math.Floor((phase + sMin) / spacing);
        int jMax = (int)Math.Ceiling((phase + sMax) / spacing);

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


    public static List<Arc> BuildPattern(Polyline polyline, double width, double spacing, bool trimCrossing,
                                         bool continuousRhythm, CornerMode cornerMode, List<Line> debugLines = null)
    {
        return BuildPattern(polyline.ToPolylineCurve(), width, spacing, trimCrossing, continuousRhythm, cornerMode, debugLines);
    }


    public static List<Arc> BuildPattern(Curve path, double width, double spacing, bool trimCrossing,
                                         bool continuousRhythm, CornerMode cornerMode, List<Line> debugLines = null)
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

        for (int i = 0; i < n; i++)
        {
            if (i == 0 && !closed)
                continue;
            int prev = (i - 1 + n) % n;
            CornerCuts(segments[i].PointAtStart, segments[prev].TangentAtEnd, segments[i].TangentAtStart,
                       halfWidth, cornerMode, out endCuts[prev], out startCuts[i], debugLines);
        }

        if (!closed)
        {
            startCuts[0] = EndCap(segments[0].PointAtStart, segments[0].TangentAtStart, true, halfWidth, debugLines);
            endCuts[n - 1] = EndCap(segments[n - 1].PointAtEnd, segments[n - 1].TangentAtEnd, false, halfWidth, debugLines);
        }

        var arcs = new List<Arc>();
        double cumulative = 0.0;
        for (int i = 0; i < n; i++)
        {
            double phase;
            if (continuousRhythm)
            {
                phase = cumulative;
            }
            else
            {
                double startOffset = CrossingParam(startCuts[i], segments[i].PointAtStart, segments[i].TangentAtStart);
                phase = double.IsInfinity(startOffset) ? 0.0 : -startOffset;
            }

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
                         out bool continuousRhythm, out CornerMode cornerMode, out bool debugDraw))
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

        List<Arc> arcs = BuildPattern(path, width, spacing, trimCrossing, continuousRhythm, cornerMode, debugLines);
        if (arcs.Count == 0)
        {
            RhinoApp.WriteLine("SoftInsulation: could not build the loop chain.");
            return;
        }

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

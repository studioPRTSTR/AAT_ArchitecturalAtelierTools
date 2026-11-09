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
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;


AATDim.Run(RhinoDoc.ActiveDoc);


enum ChainDirection
{
    Auto,
    Horizontal,
    Vertical,
    Aligned,
}


enum PickResult
{
    Done,
    Cancel,
}


enum HandleKind
{
    MovePoint,
    RemovePoint,
    DeleteRow,
    AddRow,
    Menu,
    SubText,
}


// A clickable button drawn next to a selected chain.
class ChainHandle
{
    public readonly int Group;
    public readonly string Tag;
    public readonly HandleKind Kind;
    public readonly int Row;
    public readonly double Along;
    public readonly Point3d Point;
    public readonly Guid DimId;      // SubText: the dimension the text belongs to
    public readonly bool HasText;    // SubText: the dimension already has a text below

    public ChainHandle(int group, string tag, HandleKind kind, int row, double along, Point3d point,
                       Guid dimId = default(Guid), bool hasText = false)
    {
        Group = group;
        Tag = tag;
        Kind = kind;
        Row = row;
        Along = along;
        Point = point;
        DimId = dimId;
        HasText = hasText;
    }

    public string Label =>
        Kind == HandleKind.SubText ? (HasText ? "T" : "+")
        : Kind == HandleKind.MovePoint ? "<>" : Kind == HandleKind.RemovePoint ? "x" : Kind == HandleKind.DeleteRow ? "-"
        : Kind == HandleKind.Menu ? "..." : "+";

    public System.Drawing.Color Color =>
        Kind == HandleKind.SubText ? System.Drawing.Color.FromArgb(230, 140, 30)
        : Kind == HandleKind.Menu ? System.Drawing.Color.FromArgb(40, 40, 40)
        : Kind == HandleKind.MovePoint ? System.Drawing.Color.FromArgb(40, 150, 80)
        : Kind == HandleKind.RemovePoint ? System.Drawing.Color.FromArgb(200, 60, 50)
        : Kind == HandleKind.DeleteRow ? System.Drawing.Color.FromArgb(110, 110, 110)
        : System.Drawing.Color.FromArgb(47, 111, 220);
}


// One row of a chain: sorted measure points and the distance of its dimension line from the frame.
class ChainRow
{
    public int Index;
    public double Offset;
    public List<Point3d> Points = new List<Point3d>();
}


// A multi-row dimension chain. Frame X = measuring direction, frame Y = towards the dimension lines.
class DimChain
{
    public Plane Frame;
    public string Tag;
    public Guid StyleId;
    public double Spacing;   // distance between rows in model units, 0 = from the style
    public List<ChainRow> Rows = new List<ChainRow>();

    public double Along(Point3d p) => (p - Frame.Origin) * Frame.XAxis;
    public double Across(Point3d p) => (p - Frame.Origin) * Frame.YAxis;
}


class ChainMember
{
    public int Row;
    public Point3d P1;
    public Point3d P2;
    public Point3d DimLinePoint;
}


static class AATDim
{
    const string PluginName = "ArchitecturalAtelierTools";
    const string ConfigFilename = "AATDim.json";

    // Stored on every dimension of a chain. Chain = same tag + same group.
    public const string KeyChain = "AAT.Dim.Chain";
    public const string KeyRow = "AAT.Dim.Row";
    public const string KeySpacing = "AAT.Dim.Spacing";
    // Text below a dimension: stored on the dimension, drawn by a text object that names its dimension.
    public const string KeySub = "AAT.Dim.Sub";
    public const string KeySubFor = "AAT.Dim.SubFor";

    static string LayerName = "A_Bemassung_01";
    static string StyleName = "AAT_Bemassung";
    static string FontName = "Arial";
    static double TextHeightMm = 2.0;
    static double ModelSpaceScale = 50.0;
    static double MeterThreshold = 1.0;
    static int CmDecimals = 0;
    static int MDecimals = 2;
    static string DecimalSeparator = ".";
    static double RowSpacingMm = 7.0;
    static double TickSizeMm = 2.0;
    static double TextGapMm = 1.0;
    static double ExtensionOffsetMm = 1.0;
    static double ExtensionOvershootMm = 1.5;
    static double DimLineOvershootMm = 1.5;
    static int DefaultRows = 2;
    static bool LastRowOverall = true;

    static readonly string[] DirectionNames = { "Auto", "Horizontal", "Vertical", "Aligned" };

    // Set while the watcher runs, so all its changes land in one lazily opened undo record.
    static RhinoDoc UndoDoc;
    static uint UndoSerial;


    // ------------------------------------------------------------------ config

    static IEnumerable<string> ConfigCandidates(string scriptPath)
    {
        yield return Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), PluginName, ConfigFilename);

        string assemblyDir = null;
        try { assemblyDir = Path.GetDirectoryName(typeof(AATDim).Assembly.Location); } catch (Exception) { }
        if (!string.IsNullOrEmpty(assemblyDir))
            yield return Path.Combine(assemblyDir, ConfigFilename);

        string scriptDir = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(scriptDir))
            yield return Path.Combine(scriptDir, ConfigFilename);
    }


    public static void LoadConfig([CallerFilePath] string scriptPath = "")
    {
        try
        {
            string configPath = ConfigCandidates(scriptPath).FirstOrDefault(File.Exists);
            if (configPath == null)
                return;

            string json = File.ReadAllText(configPath);

            LayerName = ReadString(json, "layer_name", LayerName);
            StyleName = ReadString(json, "style_name", StyleName);
            FontName = ReadString(json, "font", FontName);
            TextHeightMm = ReadNumber(json, "text_height_mm", TextHeightMm);
            ModelSpaceScale = ReadNumber(json, "model_space_scale", ModelSpaceScale);
            MeterThreshold = ReadNumber(json, "meter_threshold", MeterThreshold);
            CmDecimals = (int)ReadNumber(json, "cm_decimals", CmDecimals);
            MDecimals = (int)ReadNumber(json, "m_decimals", MDecimals);
            DecimalSeparator = ReadString(json, "decimal_separator", DecimalSeparator);
            RowSpacingMm = ReadNumber(json, "row_spacing_mm", RowSpacingMm);
            TickSizeMm = ReadNumber(json, "tick_size_mm", TickSizeMm);
            TextGapMm = ReadNumber(json, "text_gap_mm", TextGapMm);
            ExtensionOffsetMm = ReadNumber(json, "extension_offset_mm", ExtensionOffsetMm);
            ExtensionOvershootMm = ReadNumber(json, "extension_overshoot_mm", ExtensionOvershootMm);
            DimLineOvershootMm = ReadNumber(json, "dimline_overshoot_mm", DimLineOvershootMm);
            DefaultRows = Math.Max(1, (int)ReadNumber(json, "default_rows", DefaultRows));
            LastRowOverall = ReadBool(json, "last_row_overall", LastRowOverall);
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


    // ------------------------------------------------------------------ layer and style

    static int EnsureLayer(RhinoDoc doc, string layerName, System.Drawing.Color color)
    {
        int layerIndex = doc.Layers.FindByFullPath(layerName, -1);
        if (layerIndex >= 0)
            return layerIndex;
        int sep = layerName.LastIndexOf("::", StringComparison.Ordinal);
        var layer = new Layer
        {
            Name = sep < 0 ? layerName : layerName.Substring(sep + 2),
            Color = color,
            PlotWeight = 0.13,
        };
        if (sep >= 0)
            layer.ParentLayerId = doc.Layers[EnsureLayer(doc, layerName.Substring(0, sep), color)].Id;
        return doc.Layers.Add(layer);
    }


    public static DimensionStyle FindStyle(RhinoDoc doc)
    {
        DimensionStyle style = doc.DimStyles.FindName(StyleName);
        return style != null && !style.IsDeleted ? style : null;
    }


    static double MmToModel(RhinoDoc doc) => RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem);


    // Creates the style from the config, or resets it when reset is true. Makes it the current style.
    static DimensionStyle EnsureStyle(RhinoDoc doc, bool reset)
    {
        DimensionStyle existing = FindStyle(doc);
        if (existing != null && !reset)
            return existing;

        double mm = MmToModel(doc);
        var st = new DimensionStyle();
        st.CopyFrom(existing ?? doc.DimStyles.Current);
        st.Name = StyleName;

        // Sizes are paper sizes in model units; DimensionScale enlarges them in model space.
        st.TextHeight = TextHeightMm * mm;
        st.DimensionScale = ModelSpaceScale;
        st.TextGap = TextGapMm * mm;
        st.ArrowType1 = DimensionStyle.ArrowType.Tick;
        st.ArrowType2 = DimensionStyle.ArrowType.Tick;
        st.ArrowLength = TickSizeMm * mm;
        st.ExtensionLineOffset = ExtensionOffsetMm * mm;
        st.ExtensionLineExtension = ExtensionOvershootMm * mm;
        st.DimensionLineExtension = DimLineOvershootMm * mm;
        st.BaselineSpacing = RowSpacingMm * mm;
        st.DimTextLocation = DimensionStyle.TextLocation.AboveDimLine;
        st.DimTextAngleType = DimensionStyle.LeaderContentAngleStyle.Aligned;

        // Base format = centimetres; dimensions from the threshold on get a metre override.
        st.DimensionLengthDisplay = DimensionStyle.LengthDisplay.Centimeters;
        st.LengthResolution = CmDecimals;
        st.LengthFactor = 1.0;
        st.Roundoff = 0.0;
        st.ZeroSuppress = DimensionStyle.ZeroSuppression.None;
        st.AlternateUnitsDisplay = false;
        st.Prefix = "";
        st.Suffix = "";
        if (!string.IsNullOrEmpty(DecimalSeparator))
            st.DecimalSeparator = DecimalSeparator[0];

        try
        {
            Font font = Font.FromQuartetProperties(FontName, false, false);
            if (font != null)
                st.Font = font;
        }
        catch (Exception)
        {
        }

        int index;
        if (existing != null)
        {
            doc.DimStyles.Modify(st, existing.Index, true);
            index = existing.Index;
        }
        else
        {
            index = doc.DimStyles.Add(st, false);
        }
        if (index < 0)
            return null;

        doc.DimStyles.SetCurrent(index, false);
        if (!doc.ModelSpaceAnnotationScalingEnabled)
            RhinoApp.WriteLine("AATDim: model space annotation scaling is off - the style scale {0} only applies when it is on.", ModelSpaceScale);
        return doc.DimStyles[index];
    }


    // ------------------------------------------------------------------ cm / m format

    static bool WantsMeters(RhinoDoc doc, double value)
    {
        double meters = Math.Abs(value) * RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Meters);
        double cm = Math.Round(meters * 100.0, Math.Max(0, CmDecimals), MidpointRounding.AwayFromZero);
        return cm >= MeterThreshold * 100.0 - 1e-9;
    }


    // eff = the effective style of dim (its override style, or parent when it has none).
    // Changes dim in place. Returns false when the format already fits.
    static bool ApplyFormat(RhinoDoc doc, LinearDimension dim, DimensionStyle eff, DimensionStyle parent)
    {
        bool meters = WantsMeters(doc, dim.NumericValue);
        var display = meters ? DimensionStyle.LengthDisplay.Meters : DimensionStyle.LengthDisplay.Centimeters;
        int resolution = meters ? MDecimals : CmDecimals;

        if (eff.DimensionLengthDisplay == display && eff.LengthResolution == resolution)
            return false;

        DimensionStyle ov = eff.Duplicate();
        ov.DimensionLengthDisplay = display;
        ov.LengthResolution = resolution;

        bool parentFits = parent.DimensionLengthDisplay == display && parent.LengthResolution == resolution;
        foreach (var field in new[] { DimensionStyle.Field.DimensionLengthDisplay, DimensionStyle.Field.LengthResolution })
        {
            if (parentFits)
                ov.ClearFieldOverride(field);
            else
                ov.SetFieldOverride(field);
        }

        if (ov.HasFieldOverrides)
            dim.SetOverrideDimStyle(ov);
        else
            dim.ClearPropertyOverrides();
        return true;
    }


    // Only call GetDimensionStyle on geometry that lives in the document - on free geometry it crashes Rhino.
    static DimensionStyle EffectiveStyle(LinearDimension docDim, DimensionStyle parent) =>
        docDim.HasPropertyOverrides ? docDim.GetDimensionStyle(parent) : parent;


    static void FormatInDoc(RhinoDoc doc, Guid id, DimensionStyle parent)
    {
        RhinoObject obj = doc.Objects.FindId(id);
        if (obj == null || obj.IsDeleted || !(obj.Geometry is LinearDimension docDim))
            return;
        var copy = (LinearDimension)docDim.Duplicate();
        if (ApplyFormat(doc, copy, EffectiveStyle(docDim, parent), parent))
            ReplaceDim(doc, id, copy);
    }


    static bool IsAatStyle(RhinoDoc doc, Guid styleId)
    {
        DimensionStyle style = FindStyle(doc);
        return style != null && style.Id == styleId;
    }


    // ------------------------------------------------------------------ document changes

    static void TouchUndo(RhinoDoc doc)
    {
        if (UndoDoc == doc && UndoSerial == 0)
            UndoSerial = doc.BeginUndoRecord("AATDim update");
    }

    static void ReplaceDim(RhinoDoc doc, Guid id, LinearDimension dim)
    {
        TouchUndo(doc);
        doc.Objects.Replace(id, dim, true);
    }

    static Guid AddDim(RhinoDoc doc, LinearDimension dim, ObjectAttributes attrs)
    {
        TouchUndo(doc);
        return doc.Objects.Add(dim, attrs);
    }

    static void DeleteDim(RhinoDoc doc, Guid id)
    {
        TouchUndo(doc);
        doc.Objects.Delete(id, true);
    }


    // ------------------------------------------------------------------ chain model

    static double Tol(RhinoDoc doc) => Math.Max(doc.ModelAbsoluteTolerance, 1e-9);


    static void ReadPoints(LinearDimension dim, out Point3d p1, out Point3d p2, out Point3d dimLinePoint)
    {
        dim.Get3dPoints(out p1, out p2, out Point3d _, out Point3d _, out dimLinePoint, out Point3d _);
    }


    static int RowOf(RhinoObject obj) =>
        int.TryParse(obj.Attributes.GetUserString(KeyRow), NumberStyles.Integer, CultureInfo.InvariantCulture, out int row) ? row : 0;


    static bool ChainKey(RhinoObject obj, out int group, out string tag)
    {
        group = -1;
        tag = obj?.Attributes.GetUserString(KeyChain);
        if (string.IsNullOrEmpty(tag) || !(obj.Geometry is LinearDimension))
            return false;
        int[] groups = obj.Attributes.GetGroupList();
        if (groups == null || groups.Length == 0)
            return false;
        group = groups[0];
        return true;
    }


    static List<RhinoObject> ChainObjects(RhinoDoc doc, int group, string tag)
    {
        RhinoObject[] members = doc.Groups.GroupMembers(group);
        if (members == null)
            return new List<RhinoObject>();
        return members
            .Where(o => o != null && !o.IsDeleted && o.Geometry is LinearDimension && o.Attributes.GetUserString(KeyChain) == tag)
            .OrderBy(RowOf)
            .ToList();
    }


    static Plane FrameOf(LinearDimension dim)
    {
        ReadPoints(dim, out Point3d p1, out Point3d _, out Point3d _);
        return new Plane(p1, dim.Plane.XAxis, dim.Plane.YAxis);
    }


    static void NormalizeRow(DimChain chain, ChainRow row, double tol)
    {
        var sorted = row.Points
            .Select(p => chain.Frame.ClosestPoint(p))
            .OrderBy(chain.Along)
            .ToList();
        var unique = new List<Point3d>();
        foreach (Point3d p in sorted)
        {
            if (unique.Count == 0 || chain.Along(p) - chain.Along(unique[unique.Count - 1]) > tol)
                unique.Add(p);
        }
        row.Points = unique;
    }


    static DimChain BuildChain(List<ChainMember> members, Plane frame, string tag, Guid styleId, double tol)
    {
        var chain = new DimChain { Frame = frame, Tag = tag, StyleId = styleId };
        foreach (var rowMembers in members.GroupBy(m => m.Row).OrderBy(g => g.Key))
        {
            var row = new ChainRow { Index = rowMembers.Key, Offset = chain.Across(rowMembers.First().DimLinePoint) };
            foreach (ChainMember m in rowMembers)
            {
                row.Points.Add(m.P1);
                row.Points.Add(m.P2);
            }
            NormalizeRow(chain, row, tol);
            chain.Rows.Add(row);
        }
        return chain;
    }


    static ChainMember MemberOf(RhinoObject obj)
    {
        ReadPoints((LinearDimension)obj.Geometry, out Point3d p1, out Point3d p2, out Point3d dl);
        return new ChainMember { Row = RowOf(obj), P1 = p1, P2 = p2, DimLinePoint = dl };
    }


    static DimChain ChainFromDoc(RhinoDoc doc, List<RhinoObject> objs, string tag)
    {
        var first = (LinearDimension)objs[0].Geometry;
        DimChain chain = BuildChain(objs.Select(MemberOf).ToList(), FrameOf(first), tag, first.DimensionStyleId, Tol(doc));
        chain.Spacing = ReadSpacing(objs[0]);
        return chain;
    }


    static LinearDimension MakeDim(DimChain chain, DimensionStyle style, Point3d a, Point3d b, double offset)
    {
        double mid = 0.5 * (chain.Along(a) + chain.Along(b));
        Point3d dimLinePoint = chain.Frame.PointAt(mid, offset);
        var plane = new Plane(a, chain.Frame.XAxis, chain.Frame.YAxis);
        return LinearDimension.Create(AnnotationType.Rotated, style, plane, chain.Frame.XAxis, a, b, dimLinePoint, 0.0);
    }


    static bool Matches(RhinoObject obj, DimChain chain, Point3d a, Point3d b, double offset, double tol)
    {
        var dim = (LinearDimension)obj.Geometry;
        if (dim.Plane.XAxis.IsParallelTo(chain.Frame.XAxis, 1e-6) != 1)
            return false;
        ReadPoints(dim, out Point3d p1, out Point3d p2, out Point3d dl);
        bool same = (p1.DistanceTo(a) < tol && p2.DistanceTo(b) < tol) || (p1.DistanceTo(b) < tol && p2.DistanceTo(a) < tol);
        return same && Math.Abs(chain.Across(dl) - offset) < tol;
    }


    // Makes the document match the chain: keeps dimensions that already fit, reuses the rest, adds or deletes as needed.
    static void Regenerate(RhinoDoc doc, DimChain chain, List<RhinoObject> existing, ObjectAttributes baseAttrs)
    {
        double tol = Tol(doc);
        DimensionStyle parent = doc.DimStyles.FindId(chain.StyleId) ?? FindStyle(doc) ?? doc.DimStyles.Current;
        bool format = IsAatStyle(doc, parent.Id);

        var byRow = existing.GroupBy(RowOf).ToDictionary(g => g.Key, g => g.ToList());

        foreach (ChainRow row in chain.Rows)
        {
            var pool = byRow.TryGetValue(row.Index, out List<RhinoObject> rowObjs) ? new List<RhinoObject>(rowObjs) : new List<RhinoObject>();
            var missing = new List<Tuple<Point3d, Point3d>>();

            for (int i = 0; i + 1 < row.Points.Count; i++)
            {
                Point3d a = row.Points[i];
                Point3d b = row.Points[i + 1];
                RhinoObject hit = pool.FirstOrDefault(o => Matches(o, chain, a, b, row.Offset, tol));
                if (hit != null)
                {
                    pool.Remove(hit);
                    if (format)
                        FormatInDoc(doc, hit.Id, parent);
                }
                else
                {
                    missing.Add(Tuple.Create(a, b));
                }
            }

            foreach (var pair in missing)
            {
                LinearDimension dim = MakeDim(chain, parent, pair.Item1, pair.Item2, row.Offset);
                if (dim == null)
                    continue;

                if (pool.Count > 0)
                {
                    // Reuse the old dimension closest to this segment, so its text below goes with it.
                    double mid = 0.5 * (chain.Along(pair.Item1) + chain.Along(pair.Item2));
                    RhinoObject reuse = pool.OrderBy(o =>
                    {
                        ChainMember m = MemberOf(o);
                        return Math.Abs(0.5 * (chain.Along(m.P1) + chain.Along(m.P2)) - mid);
                    }).First();
                    pool.Remove(reuse);
                    var old = (LinearDimension)reuse.Geometry;
                    DimensionStyle eff = parent;
                    if (old.HasPropertyOverrides)
                    {
                        eff = old.GetDimensionStyle(parent);
                        dim.SetOverrideDimStyle(eff);
                    }
                    if (format)
                        ApplyFormat(doc, dim, eff, parent);
                    ReplaceDim(doc, reuse.Id, dim);
                }
                else
                {
                    if (format)
                        ApplyFormat(doc, dim, parent, parent);
                    ObjectAttributes attrs = baseAttrs.Duplicate();
                    attrs.ObjectId = Guid.Empty;
                    attrs.SetUserString(KeyChain, chain.Tag);
                    attrs.SetUserString(KeyRow, row.Index.ToString(CultureInfo.InvariantCulture));
                    attrs.DeleteUserString(KeySub);
                    AddDim(doc, dim, attrs);
                }
            }

            foreach (RhinoObject leftover in pool)
                DeleteDim(doc, leftover.Id);
        }

        foreach (var kv in byRow)
        {
            if (!chain.Rows.Any(r => r.Index == kv.Key))
                foreach (RhinoObject obj in kv.Value)
                    DeleteDim(doc, obj.Id);
        }

        int[] groups = baseAttrs.GetGroupList();
        if (groups != null && groups.Length > 0)
            SyncSubTexts(doc, groups[0], chain.Tag);
    }


    // ------------------------------------------------------------------ text below a dimension

    static List<RhinoObject> SubTextObjects(RhinoDoc doc, int group, string tag)
    {
        RhinoObject[] members = doc.Groups.GroupMembers(group);
        if (members == null)
            return new List<RhinoObject>();
        return members
            .Where(o => o != null && !o.IsDeleted && o.Geometry is TextEntity
                        && o.Attributes.GetUserString(KeyChain) == tag && !string.IsNullOrEmpty(o.Attributes.GetUserString(KeySubFor)))
            .ToList();
    }


    // Readable text plane under the dimension line, centred on the segment.
    static Plane SubTextPlane(RhinoDoc doc, LinearDimension dim, DimensionStyle style)
    {
        ReadPoints(dim, out Point3d p1, out Point3d p2, out Point3d dl);
        // Reads left to right and upright, like the dimension text.
        Vector3d z = dim.Plane.ZAxis;
        if (z * Vector3d.ZAxis < 0.0)
            z = -z;
        Vector3d x = dim.Plane.XAxis;
        if (x.X < -1e-9 || (Math.Abs(x.X) <= 1e-9 && x.Y < 0.0))
            x = -x;
        Vector3d y = Vector3d.CrossProduct(z, x);

        var probe = new DimChain { Frame = new Plane(p1, dim.Plane.XAxis, dim.Plane.YAxis) };
        double mid = 0.5 * (probe.Along(p1) + probe.Along(p2));
        Point3d onLine = probe.Frame.PointAt(mid, probe.Across(dl));
        double gap = TextGapMm * PaperMm(doc, style);
        return new Plane(onLine - y * gap, x, y);
    }


    // One text object per dimension that has a text below; follows the dimension, removed when the text is cleared.
    static void SyncSubTexts(RhinoDoc doc, int group, string tag)
    {
        List<RhinoObject> dims = ChainObjects(doc, group, tag);
        var texts = SubTextObjects(doc, group, tag);
        var used = new HashSet<Guid>();

        foreach (RhinoObject obj in dims)
        {
            string sub = obj.Attributes.GetUserString(KeySub);
            if (string.IsNullOrWhiteSpace(sub))
                continue;
            var dim = (LinearDimension)obj.Geometry;
            DimensionStyle style = doc.DimStyles.FindId(dim.DimensionStyleId) ?? doc.DimStyles.Current;
            TextEntity text = TextEntity.Create(sub, SubTextPlane(doc, dim, style), style, false, 0.0, 0.0);
            if (text == null)
                continue;
            text.Justification = TextJustification.TopCenter;

            RhinoObject existing = texts.FirstOrDefault(t => t.Attributes.GetUserString(KeySubFor) == obj.Id.ToString());
            if (existing != null)
            {
                used.Add(existing.Id);
                var old = (TextEntity)existing.Geometry;
                if (old.PlainText == sub && old.Plane.Origin.DistanceTo(text.Plane.Origin) < Tol(doc)
                    && old.Plane.XAxis.IsParallelTo(text.Plane.XAxis, 1e-6) == 1)
                    continue;
                TouchUndo(doc);
                doc.Objects.Replace(existing.Id, text);
            }
            else
            {
                ObjectAttributes attrs = obj.Attributes.Duplicate();
                attrs.ObjectId = Guid.Empty;
                attrs.DeleteUserString(KeySub);
                attrs.DeleteUserString(KeyRow);
                attrs.DeleteUserString(KeySpacing);
                attrs.SetUserString(KeySubFor, obj.Id.ToString());
                TouchUndo(doc);
                doc.Objects.Add(text, attrs);
            }
        }

        foreach (RhinoObject t in texts)
            if (!used.Contains(t.Id))
                DeleteDim(doc, t.Id);
    }


    // ------------------------------------------------------------------ watcher entry point

    // Called after a command (or grip drag) changed dimensions. old = geometry before the change.
    public static void ProcessEdits(RhinoDoc doc, Dictionary<Guid, LinearDimension> old, HashSet<Guid> added)
    {
        LoadConfig();
        UndoDoc = doc;
        UndoSerial = 0;
        try
        {
            DimensionStyle aat = FindStyle(doc);
            var chains = new HashSet<Tuple<int, string>>();

            foreach (Guid id in old.Keys.Union(added).ToList())
            {
                RhinoObject obj = doc.Objects.FindId(id);
                if (obj == null || obj.IsDeleted || !(obj.Geometry is LinearDimension dim))
                    continue;
                if (ChainKey(obj, out int group, out string tag))
                    chains.Add(Tuple.Create(group, tag));
                else if (aat != null && dim.DimensionStyleId == aat.Id)
                    FormatInDoc(doc, id, aat);
            }

            foreach (var key in chains)
                UpdateChainAfterEdit(doc, key.Item1, key.Item2, old);
        }
        finally
        {
            if (UndoSerial != 0)
            {
                doc.EndUndoRecord(UndoSerial);
                doc.Views.Redraw();
            }
            UndoDoc = null;
            UndoSerial = 0;
        }
    }


    // Moved measure points are moved in every row that shares them; a moved dimension line moves its whole row.
    static void UpdateChainAfterEdit(RhinoDoc doc, int group, string tag, Dictionary<Guid, LinearDimension> old)
    {
        List<RhinoObject> objs = ChainObjects(doc, group, tag);
        if (objs.Count == 0)
            return;

        var replaced = objs.Where(o => old.ContainsKey(o.Id)).ToList();
        if (replaced.Count == 0 || replaced.Count == objs.Count)
        {
            // New (copied / pasted) or transformed as a whole: only the format can be off.
            foreach (RhinoObject obj in objs)
            {
                Guid styleId = ((LinearDimension)obj.Geometry).DimensionStyleId;
                if (IsAatStyle(doc, styleId))
                    FormatInDoc(doc, obj.Id, doc.DimStyles.FindId(styleId));
            }
            return;
        }

        double tol = Tol(doc);
        RhinoObject stable = objs.First(o => !old.ContainsKey(o.Id));
        var frameDim = (LinearDimension)stable.Geometry;
        var probe = new DimChain { Frame = FrameOf(frameDim) };

        var moves = new List<Tuple<Point3d, Point3d>>();
        var newOffsets = new Dictionary<int, double>();
        foreach (RhinoObject obj in replaced)
        {
            ReadPoints(old[obj.Id], out Point3d o1, out Point3d o2, out Point3d odl);
            ReadPoints((LinearDimension)obj.Geometry, out Point3d n1, out Point3d n2, out Point3d ndl);
            if (o1.DistanceTo(n1) > tol)
                moves.Add(Tuple.Create(o1, n1));
            if (o2.DistanceTo(n2) > tol)
                moves.Add(Tuple.Create(o2, n2));
            int row = RowOf(obj);
            if (Math.Abs(probe.Across(ndl) - probe.Across(odl)) > tol && !newOffsets.ContainsKey(row))
                newOffsets[row] = probe.Across(ndl);
        }

        Point3d Map(Point3d p)
        {
            foreach (var mv in moves)
                if (p.DistanceTo(mv.Item1) < tol)
                    return mv.Item2;
            return p;
        }

        var members = new List<ChainMember>();
        foreach (RhinoObject obj in objs)
        {
            ChainMember m = MemberOf(obj);
            if (!old.ContainsKey(obj.Id))
            {
                m.P1 = Map(m.P1);
                m.P2 = Map(m.P2);
            }
            members.Add(m);
        }

        // Rows take their offset from a moved dimension line, else from an unchanged member.
        DimChain chain = BuildChain(members, probe.Frame, tag, frameDim.DimensionStyleId, tol);
        foreach (ChainRow row in chain.Rows)
        {
            if (newOffsets.TryGetValue(row.Index, out double offset))
                row.Offset = offset;
            else
            {
                RhinoObject rowStable = objs.FirstOrDefault(o => RowOf(o) == row.Index && !old.ContainsKey(o.Id));
                if (rowStable != null)
                    row.Offset = chain.Across(MemberOf(rowStable).DimLinePoint);
            }
        }

        Regenerate(doc, chain, objs, objs[0].Attributes);
    }


    // ------------------------------------------------------------------ chain helpers

    static int OutwardSign(DimChain chain)
    {
        ChainRow first = chain.Rows.OrderBy(r => r.Index).FirstOrDefault();
        if (first == null || first.Points.Count == 0)
            return 1;
        double mean = first.Points.Average(chain.Across);
        return first.Offset - mean >= 0.0 ? 1 : -1;
    }


    static double ReadSpacing(RhinoObject obj) =>
        double.TryParse(obj.Attributes.GetUserString(KeySpacing), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0.0 ? v : 0.0;


    static double ChainSpacing(RhinoDoc doc, DimChain chain, DimensionStyle style) =>
        chain.Spacing > 0.0 ? chain.Spacing : RowSpacing(doc, style);


    // Model-space length of one paper millimetre for this style.
    static double PaperMm(RhinoDoc doc, DimensionStyle style) =>
        MmToModel(doc) * (doc.ModelSpaceAnnotationScalingEnabled ? style.DimensionScale : 1.0);


    static double RowSpacing(RhinoDoc doc, DimensionStyle style)
    {
        double spacing = style.BaselineSpacing > 0.0 ? style.BaselineSpacing : RowSpacingMm * MmToModel(doc);
        return spacing * (doc.ModelSpaceAnnotationScalingEnabled ? style.DimensionScale : 1.0);
    }


    static ChainRow NewOuterRow(RhinoDoc doc, DimChain chain, DimensionStyle style, IEnumerable<Point3d> points)
    {
        int sign = OutwardSign(chain);
        double outer = sign > 0 ? chain.Rows.Max(r => r.Offset) : chain.Rows.Min(r => r.Offset);
        var row = new ChainRow
        {
            Index = chain.Rows.Max(r => r.Index) + 1,
            Offset = outer + sign * ChainSpacing(doc, chain, style),
            Points = points.ToList(),
        };
        NormalizeRow(chain, row, Tol(doc));
        return row;
    }


    static List<Point3d> OverallPoints(DimChain chain)
    {
        var all = chain.Rows.SelectMany(r => r.Points).ToList();
        if (all.Count < 2)
            return all;
        return new List<Point3d> { all.OrderBy(chain.Along).First(), all.OrderBy(chain.Along).Last() };
    }


    static Vector3d ChainAxis(ChainDirection direction, List<Point3d> points, Plane cplane)
    {
        switch (direction)
        {
            case ChainDirection.Horizontal:
                return cplane.XAxis;
            case ChainDirection.Vertical:
                return cplane.YAxis;
            case ChainDirection.Aligned:
            {
                Vector3d v = points[points.Count - 1] - points[0];
                v -= (v * cplane.ZAxis) * cplane.ZAxis;
                if (v.Unitize())
                    return v;
                return cplane.XAxis;
            }
            default:
            {
                double spanX = points.Max(p => (p - cplane.Origin) * cplane.XAxis) - points.Min(p => (p - cplane.Origin) * cplane.XAxis);
                double spanY = points.Max(p => (p - cplane.Origin) * cplane.YAxis) - points.Min(p => (p - cplane.Origin) * cplane.YAxis);
                return spanX >= spanY ? cplane.XAxis : cplane.YAxis;
            }
        }
    }


    static List<LinearDimension> PreviewDims(DimChain chain, DimensionStyle style)
    {
        var dims = new List<LinearDimension>();
        foreach (ChainRow row in chain.Rows)
            for (int i = 0; i + 1 < row.Points.Count; i++)
            {
                LinearDimension dim = MakeDim(chain, style, row.Points[i], row.Points[i + 1], row.Offset);
                if (dim != null)
                    dims.Add(dim);
            }
        return dims;
    }


    static void DrawPreview(DrawEventArgs e, IEnumerable<LinearDimension> dims)
    {
        System.Drawing.Color color = Rhino.ApplicationSettings.AppearanceSettings.FeedbackColor;
        foreach (LinearDimension dim in dims)
            e.Display.DrawAnnotation(dim, color);
    }


    // Picks points until Enter.
    static PickResult PickPoints(string prompt, List<Point3d> points, Action<DrawEventArgs, List<Point3d>, Point3d> draw)
    {
        while (true)
        {
            var gp = new GetPoint();
            gp.SetCommandPrompt(prompt + " (" + points.Count + " picked, Enter when done)");
            gp.AcceptNothing(true);
            int optUndo = points.Count > 0 ? gp.AddOption("Undo") : -1;
            if (points.Count > 0)
            {
                gp.SetBasePoint(points[points.Count - 1], true);
                gp.DrawLineFromPoint(points[points.Count - 1], true);
            }
            gp.DynamicDraw += (sender, e) =>
            {
                foreach (Point3d p in points)
                    e.Display.DrawPoint(p, PointStyle.X, 4, Rhino.ApplicationSettings.AppearanceSettings.FeedbackColor);
                draw?.Invoke(e, points, e.CurrentPoint);
            };

            GetResult res = gp.Get();
            if (res == GetResult.Point)
            {
                points.Add(gp.Point());
                continue;
            }
            if (res == GetResult.Option)
            {
                if (gp.OptionIndex() == optUndo && points.Count > 0)
                    points.RemoveAt(points.Count - 1);
                continue;
            }
            if (res == GetResult.Nothing)
                return PickResult.Done;
            return PickResult.Cancel;
        }
    }


    // Rows drawn so far plus a candidate row built from the points being picked.
    static Action<DrawEventArgs, List<Point3d>, Point3d> RowPreview(RhinoDoc doc, DimChain chain, DimensionStyle style)
    {
        List<LinearDimension> fixedDims = PreviewDims(chain, style);
        return (e, picked, cursor) =>
        {
            DrawPreview(e, fixedDims);
            var candidate = new List<Point3d>(picked) { cursor };
            if (candidate.Count < 2)
                return;
            var temp = new DimChain { Frame = chain.Frame, Rows = new List<ChainRow> { NewOuterRow(doc, chain, style, candidate) } };
            DrawPreview(e, PreviewDims(temp, style));
        };
    }


    // ------------------------------------------------------------------ on-screen buttons

    // Buttons of every selected chain: "x" on each inner point (merge the two segments),
    // "-" at the start of each row (delete row), "+" beyond the outermost row (add row).
    public static List<ChainHandle> Handles(RhinoDoc doc, RhinoViewport viewport)
    {
        var handles = new List<ChainHandle>();
        if (doc == null || viewport == null)
            return handles;

        var keys = new HashSet<Tuple<int, string>>();
        foreach (RhinoObject obj in doc.Objects.GetSelectedObjects(false, false))
            if (ChainKey(obj, out int group, out string tag))
                keys.Add(Tuple.Create(group, tag));

        foreach (var key in keys)
        {
            List<RhinoObject> objs = ChainObjects(doc, key.Item1, key.Item2);
            if (objs.Count == 0)
                continue;
            DimChain chain = ChainFromDoc(doc, objs, key.Item2);
            DimensionStyle style = doc.DimStyles.FindId(chain.StyleId) ?? doc.DimStyles.Current;
            var all = chain.Rows.SelectMany(r => r.Points).ToList();
            if (all.Count == 0)
                continue;

            if (!viewport.GetWorldToScreenScale(chain.Frame.Origin, out double pixelsPerUnit) || pixelsPerUnit <= 0.0)
                continue;
            double side = all.Min(chain.Along) - 22.0 / pixelsPerUnit;

            // One move handle per measure point, at the point itself (shared by all rows).
            var seen = new List<double>();
            foreach (Point3d p in all.OrderBy(chain.Along))
            {
                double along = chain.Along(p);
                if (seen.Any(s => Math.Abs(s - along) < Tol(doc)))
                    continue;
                seen.Add(along);
                handles.Add(new ChainHandle(key.Item1, key.Item2, HandleKind.MovePoint, -1, along, p));
            }

            foreach (ChainRow row in chain.Rows)
            {
                for (int i = 1; i + 1 < row.Points.Count; i++)
                {
                    double along = chain.Along(row.Points[i]);
                    handles.Add(new ChainHandle(key.Item1, key.Item2, HandleKind.RemovePoint, row.Index, along,
                        chain.Frame.PointAt(along, row.Offset)));
                }
                if (chain.Rows.Count > 1)
                    handles.Add(new ChainHandle(key.Item1, key.Item2, HandleKind.DeleteRow, row.Index, side,
                        chain.Frame.PointAt(side, row.Offset)));
            }

            // "+" right next to each dimension text: add / edit the text below that dimension.
            foreach (RhinoObject obj in objs)
            {
                var dim = (LinearDimension)obj.Geometry;
                ReadPoints(dim, out Point3d d1, out Point3d d2, out Point3d dl);
                double right = Math.Max(chain.Along(d1), chain.Along(d2)) - 0.25 * Math.Abs(chain.Along(d2) - chain.Along(d1));
                double across = chain.Across(dl);
                // The text rectangle is in text space; GetTextTransform brings it into the model.
                DimensionStyle parent = doc.DimStyles.FindId(dim.DimensionStyleId) ?? style;
                DimensionStyle eff = EffectiveStyle(dim, parent);
                double textScale = doc.ModelSpaceAnnotationScalingEnabled ? eff.DimensionScale : 1.0;
                Transform toModel = dim.GetTextTransform(new ViewportInfo(viewport), eff, textScale, eff.DrawForward);
                if (dim.GetTextRectangle(out Point3d[] corners) && corners != null && corners.Length > 0)
                {
                    var placed = corners.Select(c => { Point3d q = c; q.Transform(toModel); return q; }).ToList();
                    right = placed.Max(chain.Along);
                    across = placed.Average(chain.Across);
                }
                double along = right + 9.0 / pixelsPerUnit;
                bool hasText = !string.IsNullOrWhiteSpace(obj.Attributes.GetUserString(KeySub));
                handles.Add(new ChainHandle(key.Item1, key.Item2, HandleKind.SubText, RowOf(obj), along,
                    chain.Frame.PointAt(along, across), obj.Id, hasText));
            }

            int sign = OutwardSign(chain);
            double outer = sign > 0 ? chain.Rows.Max(r => r.Offset) : chain.Rows.Min(r => r.Offset);
            handles.Add(new ChainHandle(key.Item1, key.Item2, HandleKind.AddRow, -1, side,
                chain.Frame.PointAt(side, outer + sign * ChainSpacing(doc, chain, style))));

            ChainRow firstRow = chain.Rows.OrderBy(r => r.Index).First();
            double menuAlong = side - 26.0 / pixelsPerUnit;
            handles.Add(new ChainHandle(key.Item1, key.Item2, HandleKind.Menu, firstRow.Index, menuAlong,
                chain.Frame.PointAt(menuAlong, firstRow.Offset)));
        }
        return handles;
    }


    public static ChainHandle HitHandle(RhinoDoc doc, RhinoViewport viewport, System.Drawing.Point screen, double radius)
    {
        ChainHandle best = null;
        double bestDist = radius;
        foreach (ChainHandle h in Handles(doc, viewport))
        {
            Point2d s = viewport.WorldToClient(h.Point);
            double d = Math.Sqrt((s.X - screen.X) * (s.X - screen.X) + (s.Y - screen.Y) * (s.Y - screen.Y));
            if (d < bestDist)
            {
                bestDist = d;
                best = h;
            }
        }
        return best;
    }


    // Moves the point h in every row of the chain to "to". Points that land on a neighbour merge with it.
    static void MovePointInChain(RhinoDoc doc, DimChain chain, ChainHandle h, Point3d to)
    {
        double tol = Tol(doc);
        foreach (ChainRow row in chain.Rows)
        {
            for (int i = 0; i < row.Points.Count; i++)
                if (Math.Abs(chain.Along(row.Points[i]) - h.Along) < tol)
                    row.Points[i] = to;
            NormalizeRow(chain, row, tol);
        }
        chain.Rows.RemoveAll(r => r.Points.Count < 2);
    }


    static DimChain CloneChain(DimChain chain) => new DimChain
    {
        Frame = chain.Frame,
        Tag = chain.Tag,
        StyleId = chain.StyleId,
        Spacing = chain.Spacing,
        Rows = chain.Rows.Select(r => new ChainRow { Index = r.Index, Offset = r.Offset, Points = new List<Point3d>(r.Points) }).ToList(),
    };


    // Asks for the new location (with osnaps and a live preview). Changes chain; false on cancel.
    static bool PickMovedPoint(RhinoDoc doc, DimChain chain, DimensionStyle style, ChainHandle h)
    {
        var gp = new GetPoint();
        gp.SetCommandPrompt("New location of the dimension point");
        gp.SetBasePoint(h.Point, true);
        gp.DrawLineFromPoint(h.Point, true);
        gp.DynamicDraw += (sender, e) =>
        {
            DimChain preview = CloneChain(chain);
            MovePointInChain(doc, preview, h, e.CurrentPoint);
            DrawPreview(e, PreviewDims(preview, style));
        };

        bool wasBusy = DimWatcher.Busy;
        DimWatcher.Busy = true;
        GetResult res;
        try { res = gp.Get(); }
        finally { DimWatcher.Busy = wasBusy; }

        if (res != GetResult.Point)
            return false;
        MovePointInChain(doc, chain, h, gp.Point());
        return chain.Rows.Count > 0;
    }


    // Chain menu: distance between the dimension lines and a text below each dimension.
    static void ShowMenu(RhinoDoc doc, List<RhinoObject> objs, DimChain chain, DimensionStyle style, ChainHandle h)
    {
        double paperMm = PaperMm(doc, style);
        List<ChainRow> rows = chain.Rows.OrderBy(r => r.Index).ToList();
        double current = chain.Spacing > 0.0 || rows.Count < 2
            ? ChainSpacing(doc, chain, style)
            : Math.Abs(rows[1].Offset - rows[0].Offset);

        var spacingBox = new Eto.Forms.NumericStepper
        {
            Value = Math.Round(current / paperMm, 1),
            MinValue = 1.0,
            MaxValue = 100.0,
            DecimalPlaces = 1,
            Increment = 0.5,
            Width = 90,
        };

        var segments = objs
            .OrderBy(RowOf)
            .ThenBy(o => { ChainMember m = MemberOf(o); return Math.Min(chain.Along(m.P1), chain.Along(m.P2)); })
            .ToList();
        var subBoxes = new List<Eto.Forms.TextBox>();
        var table = new Eto.Forms.TableLayout { Spacing = new Eto.Drawing.Size(10, 4) };
        foreach (RhinoObject obj in segments)
        {
            var dim = (LinearDimension)obj.Geometry;
            DimensionStyle parent = doc.DimStyles.FindId(dim.DimensionStyleId) ?? style;
            string value = dim.GetDistanceDisplayText(doc.ModelUnitSystem, EffectiveStyle(dim, parent));
            int rowNumber = rows.FindIndex(r => r.Index == RowOf(obj)) + 1;
            var box = new Eto.Forms.TextBox { Text = obj.Attributes.GetUserString(KeySub) ?? "", Width = 160 };
            subBoxes.Add(box);
            table.Rows.Add(new Eto.Forms.TableRow(
                new Eto.Forms.Label { Text = "Row " + rowNumber, VerticalAlignment = Eto.Forms.VerticalAlignment.Center },
                new Eto.Forms.Label { Text = value, VerticalAlignment = Eto.Forms.VerticalAlignment.Center, Width = 60 },
                box));
        }

        var dialog = new Eto.Forms.Dialog<bool> { Title = "Dimension chain", Padding = 12, Resizable = true };
        var ok = new Eto.Forms.Button { Text = "OK" };
        var cancel = new Eto.Forms.Button { Text = "Cancel" };
        ok.Click += (sender, e) => dialog.Close(true);
        cancel.Click += (sender, e) => dialog.Close(false);
        dialog.DefaultButton = ok;
        dialog.AbortButton = cancel;
        dialog.Content = new Eto.Forms.StackLayout
        {
            Spacing = 8,
            HorizontalContentAlignment = Eto.Forms.HorizontalAlignment.Stretch,
            Items =
            {
                new Eto.Forms.TableLayout(new Eto.Forms.TableRow(
                    new Eto.Forms.Label { Text = "Distance between dimension lines (mm on paper)", VerticalAlignment = Eto.Forms.VerticalAlignment.Center },
                    spacingBox)) { Spacing = new Eto.Drawing.Size(10, 0) },
                new Eto.Forms.Label { Text = "Text below the dimension line (e.g. door height):" },
                new Eto.Forms.Scrollable { Content = table, Height = Math.Min(360, 30 * segments.Count + 10), Border = Eto.Forms.BorderType.None },
                new Eto.Forms.TableLayout(new Eto.Forms.TableRow(new Eto.Forms.TableCell(null, true), ok, cancel)) { Spacing = new Eto.Drawing.Size(6, 0) },
            },
        };

        bool accepted;
        bool wasBusy = DimWatcher.Busy;
        DimWatcher.Busy = true;
        try { accepted = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow); }
        finally { DimWatcher.Busy = wasBusy; }
        if (!accepted)
            return;

        double spacing = spacingBox.Value * paperMm;
        DimWatcher.Busy = true;
        uint undo = doc.BeginUndoRecord("AATDim chain settings");
        try
        {
            for (int i = 0; i < segments.Count; i++)
            {
                ObjectAttributes attrs = segments[i].Attributes.Duplicate();
                attrs.SetUserString(KeySpacing, spacing.ToString("R", CultureInfo.InvariantCulture));
                string sub = (subBoxes[i].Text ?? "").Trim();
                if (sub.Length > 0)
                    attrs.SetUserString(KeySub, sub);
                else
                    attrs.DeleteUserString(KeySub);
                doc.Objects.ModifyAttributes(segments[i], attrs, true);
            }

            // Rows keep their order from the inside out; the first row stays where it is.
            List<RhinoObject> fresh = ChainObjects(doc, h.Group, h.Tag);
            DimChain updated = ChainFromDoc(doc, fresh, h.Tag);
            int sign = OutwardSign(updated);
            List<ChainRow> ordered = updated.Rows.OrderBy(r => sign * r.Offset).ToList();
            for (int k = 1; k < ordered.Count; k++)
                ordered[k].Offset = ordered[0].Offset + sign * k * spacing;

            Regenerate(doc, updated, fresh, fresh[0].Attributes);
        }
        finally
        {
            doc.EndUndoRecord(undo);
            DimWatcher.Busy = false;
        }

        foreach (RhinoObject obj in ChainObjects(doc, h.Group, h.Tag))
            obj.Select(true);
        doc.Views.Redraw();
    }


    // Asks for the text below one dimension. Empty text removes it.
    static void EditSubText(RhinoDoc doc, ChainHandle h)
    {
        RhinoObject obj = doc.Objects.FindId(h.DimId);
        if (obj == null || obj.IsDeleted)
            return;
        string current = obj.Attributes.GetUserString(KeySub) ?? "";

        bool ok;
        string text;
        bool wasBusy = DimWatcher.Busy;
        DimWatcher.Busy = true;
        try { ok = Rhino.UI.Dialogs.ShowEditBox("Text below dimension", "Text below the dimension line (empty = remove), e.g. RPH 2.10:", current, false, out text); }
        finally { DimWatcher.Busy = wasBusy; }
        if (!ok)
            return;
        text = (text ?? "").Trim();
        if (text == current)
            return;

        DimWatcher.Busy = true;
        uint undo = doc.BeginUndoRecord("AATDim text below");
        try
        {
            ObjectAttributes attrs = obj.Attributes.Duplicate();
            if (text.Length > 0)
                attrs.SetUserString(KeySub, text);
            else
                attrs.DeleteUserString(KeySub);
            doc.Objects.ModifyAttributes(obj, attrs, true);
            SyncSubTexts(doc, h.Group, h.Tag);
        }
        finally
        {
            doc.EndUndoRecord(undo);
            DimWatcher.Busy = false;
        }

        foreach (RhinoObject member in ChainObjects(doc, h.Group, h.Tag))
            member.Select(true);
        doc.Views.Redraw();
    }


    public static void ApplyHandle(RhinoDoc doc, ChainHandle h)
    {
        List<RhinoObject> objs = ChainObjects(doc, h.Group, h.Tag);
        if (objs.Count == 0)
            return;
        DimChain chain = ChainFromDoc(doc, objs, h.Tag);
        DimensionStyle style = doc.DimStyles.FindId(chain.StyleId) ?? doc.DimStyles.Current;
        ChainRow row = chain.Rows.FirstOrDefault(r => r.Index == h.Row);

        switch (h.Kind)
        {
            case HandleKind.Menu:
                ShowMenu(doc, objs, chain, style, h);
                return;
            case HandleKind.SubText:
                EditSubText(doc, h);
                return;
            case HandleKind.MovePoint:
                if (!PickMovedPoint(doc, chain, style, h))
                    return;
                break;
            case HandleKind.RemovePoint:
                if (row == null || row.Points.Count < 3)
                    return;
                row.Points.Remove(row.Points.OrderBy(p => Math.Abs(chain.Along(p) - h.Along)).First());
                break;
            case HandleKind.DeleteRow:
                if (row == null || chain.Rows.Count < 2)
                    return;
                chain.Rows.Remove(row);
                break;
            case HandleKind.AddRow:
                chain.Rows.Add(NewOuterRow(doc, chain, style, chain.Rows.SelectMany(r => r.Points)));
                break;
        }

        DimWatcher.Busy = true;
        uint undo = doc.BeginUndoRecord("AATDim edit");
        try
        {
            Regenerate(doc, chain, objs, objs[0].Attributes);
        }
        finally
        {
            doc.EndUndoRecord(undo);
            DimWatcher.Busy = false;
        }

        // Keep the chain selected so the next button can be clicked right away.
        foreach (RhinoObject obj in ChainObjects(doc, h.Group, h.Tag))
            obj.Select(true);
        doc.Views.Redraw();
    }


    // ------------------------------------------------------------------ commands

    public static void Run(RhinoDoc doc)
    {
        LoadConfig();
        DimWatcher.Install();

        int dirIndex = 0;
        var optRows = new OptionInteger(DefaultRows, 1, 9);
        Point3d first;
        while (true)
        {
            var gp = new GetPoint();
            gp.SetCommandPrompt("First point of the dimension chain");
            gp.AddOptionInteger("Rows", ref optRows, "Number of dimension rows");
            int optDir = gp.AddOptionList("Direction", DirectionNames, dirIndex);
            int optEdit = gp.AddOption("Edit");
            int optUpdate = gp.AddOption("Update");
            int optStyle = gp.AddOption("ResetStyle");

            GetResult res = gp.Get();
            if (res == GetResult.Option)
            {
                int index = gp.OptionIndex();
                if (index == optDir)
                {
                    dirIndex = gp.Option().CurrentListOptionIndex;
                    continue;
                }
                if (index == optEdit)
                {
                    EditChain(doc);
                    return;
                }
                if (index == optUpdate)
                {
                    UpdateAll(doc);
                    return;
                }
                if (index == optStyle)
                {
                    DimWatcher.Busy = true;
                    try { EnsureStyle(doc, true); }
                    finally { DimWatcher.Busy = false; }
                    RhinoApp.WriteLine("AATDim: style '{0}' reset from {1} and set current.", StyleName, ConfigFilename);
                    UpdateAll(doc);
                    continue;
                }
                continue;
            }
            if (res != GetResult.Point)
                return;
            first = gp.Point();
            break;
        }

        // The style is only created when a chain is drawn, so Update at startup leaves files untouched.
        DimensionStyle style;
        DimWatcher.Busy = true;
        try { style = EnsureStyle(doc, false); }
        finally { DimWatcher.Busy = false; }
        if (style == null)
        {
            RhinoApp.WriteLine("AATDim: could not create dimension style '{0}'.", StyleName);
            return;
        }

        CreateChain(doc, style, first, (ChainDirection)dirIndex, optRows.CurrentValue);
    }


    // Rows 2..n repeat all points of row 1; with last_row_overall the outermost row is first-to-last.
    static void AddGeneratedRows(RhinoDoc doc, DimChain chain, DimensionStyle style, int rowCount)
    {
        ChainRow row0 = chain.Rows[0];
        chain.Rows.RemoveRange(1, chain.Rows.Count - 1);
        for (int i = 1; i < rowCount; i++)
        {
            bool overall = LastRowOverall && i == rowCount - 1;
            chain.Rows.Add(NewOuterRow(doc, chain, style, overall ? OverallPoints(chain) : new List<Point3d>(row0.Points)));
        }
    }


    static void CreateChain(RhinoDoc doc, DimensionStyle style, Point3d first, ChainDirection direction, int rowCount)
    {
        Plane cplane = doc.Views.ActiveView?.ActiveViewport.ConstructionPlane() ?? Plane.WorldXY;

        var points = new List<Point3d> { first };
        if (PickPoints("Next point", points, null) == PickResult.Cancel)
            return;
        if (points.Count < 2)
        {
            RhinoApp.WriteLine("AATDim: a row needs at least two points.");
            return;
        }

        Vector3d axis = ChainAxis(direction, points, cplane);
        var frame = new Plane(cplane.ClosestPoint(points[0]), axis, Vector3d.CrossProduct(cplane.ZAxis, axis));
        var chain = new DimChain { Frame = frame, Tag = Guid.NewGuid().ToString(), StyleId = style.Id };

        var row0 = new ChainRow { Index = 0, Points = points };
        NormalizeRow(chain, row0, Tol(doc));
        if (row0.Points.Count < 2)
        {
            RhinoApp.WriteLine("AATDim: all points lie on one line across the measuring direction.");
            return;
        }

        chain.Rows.Add(row0);
        var gl = new GetPoint();
        gl.SetCommandPrompt("Location of the first dimension line");
        gl.DynamicDraw += (sender, e) =>
        {
            row0.Offset = chain.Across(e.CurrentPoint);
            AddGeneratedRows(doc, chain, style, rowCount);
            DrawPreview(e, PreviewDims(chain, style));
        };
        if (gl.Get() != GetResult.Point)
            return;
        row0.Offset = chain.Across(gl.Point());
        AddGeneratedRows(doc, chain, style, rowCount);

        DimWatcher.Busy = true;
        uint undo = doc.BeginUndoRecord("AATDim");
        try
        {
            var attrs = doc.CreateDefaultAttributes();
            attrs.LayerIndex = EnsureLayer(doc, LayerName, System.Drawing.Color.Black);
            attrs.AddToGroup(doc.Groups.Add());
            Regenerate(doc, chain, new List<RhinoObject>(), attrs);
            int count = chain.Rows.Sum(r => Math.Max(0, r.Points.Count - 1));
            RhinoApp.WriteLine("AATDim: {0} row(s), {1} dimension(s) on layer '{2}'.", chain.Rows.Count, count, LayerName);
        }
        finally
        {
            doc.EndUndoRecord(undo);
            DimWatcher.Busy = false;
        }
        doc.Views.Redraw();
    }


    static void EditChain(RhinoDoc doc)
    {
        var go = new GetObject();
        go.SetCommandPrompt("Select a dimension of an AATDim chain");
        go.GeometryFilter = ObjectType.Annotation;
        go.SetCustomGeometryFilter((obj, geometry, componentIndex) => ChainKey(obj, out int _, out string _));
        go.EnablePreSelect(true, true);
        if (go.Get() != GetResult.Object)
            return;

        RhinoObject picked = go.Object(0).Object();
        if (!ChainKey(picked, out int group, out string tag))
            return;
        int rowIndex = RowOf(picked);
        doc.Objects.UnselectAll();

        var opNames = new[] { "AddPoints", "RemovePoints", "AddRow", "AddOverall", "DeleteRow" };
        while (true)
        {
            List<RhinoObject> objs = ChainObjects(doc, group, tag);
            if (objs.Count == 0)
                return;
            DimChain chain = ChainFromDoc(doc, objs, tag);
            DimensionStyle style = doc.DimStyles.FindId(chain.StyleId) ?? FindStyle(doc) ?? doc.DimStyles.Current;
            List<ChainRow> rows = chain.Rows.OrderBy(r => r.Index).ToList();
            int rowPos = Math.Max(0, rows.FindIndex(r => r.Index == rowIndex));
            ChainRow row = rows[rowPos];
            rowIndex = row.Index;

            var gx = new GetOption();
            gx.SetCommandPrompt(string.Format("Edit row {0} of {1}", rowPos + 1, rows.Count));
            gx.AcceptNothing(true);
            var optRow = new OptionInteger(rowPos + 1, 1, rows.Count);
            int iRow = gx.AddOptionInteger("Row", ref optRow);
            var opIndex = opNames.Select(n => gx.AddOption(n)).ToArray();

            GetResult res = gx.Get();
            if (res != GetResult.Option)
                return;
            int chosen = gx.OptionIndex();
            if (chosen == iRow)
            {
                rowIndex = rows[Math.Min(rows.Count, Math.Max(1, optRow.CurrentValue)) - 1].Index;
                continue;
            }
            string op = opNames[Array.IndexOf(opIndex, chosen)];

            var picks = new List<Point3d>();
            Action<DrawEventArgs, List<Point3d>, Point3d> preview = (e, p, c) => { };
            switch (op)
            {
                case "AddPoints":
                case "RemovePoints":
                    if (PickPoints(op == "AddPoints" ? "Points to add to this row" : "Points to remove from this row", picks, preview) == PickResult.Cancel)
                        return;
                    if (picks.Count == 0)
                        continue;
                    if (op == "AddPoints")
                    {
                        row.Points.AddRange(picks);
                        NormalizeRow(chain, row, Tol(doc));
                    }
                    else
                    {
                        foreach (Point3d p in picks)
                        {
                            if (row.Points.Count == 0)
                                break;
                            Point3d nearest = row.Points.OrderBy(q => Math.Abs(chain.Along(q) - chain.Along(p))).First();
                            row.Points.Remove(nearest);
                        }
                        if (row.Points.Count < 2)
                            chain.Rows.Remove(row);
                    }
                    break;
                case "AddRow":
                    if (PickPoints("Points of the new row", picks, RowPreview(doc, chain, style)) == PickResult.Cancel)
                        return;
                    if (picks.Count < 2)
                        continue;
                    ChainRow added = NewOuterRow(doc, chain, style, picks);
                    if (added.Points.Count < 2)
                        continue;
                    chain.Rows.Add(added);
                    rowIndex = added.Index;
                    break;
                case "AddOverall":
                    ChainRow overall = NewOuterRow(doc, chain, style, OverallPoints(chain));
                    if (overall.Points.Count < 2)
                        continue;
                    chain.Rows.Add(overall);
                    rowIndex = overall.Index;
                    break;
                case "DeleteRow":
                    chain.Rows.Remove(row);
                    break;
            }

            DimWatcher.Busy = true;
            uint undo = doc.BeginUndoRecord("AATDim edit");
            try
            {
                Regenerate(doc, chain, objs, objs[0].Attributes);
            }
            finally
            {
                doc.EndUndoRecord(undo);
                DimWatcher.Busy = false;
            }
            doc.Views.Redraw();
        }
    }


    // Re-applies the cm / m format to every dimension that uses the style.
    static void UpdateAll(RhinoDoc doc)
    {
        DimensionStyle style = FindStyle(doc);
        if (style == null)
            return;
        var ids = doc.Objects.GetObjectList(ObjectType.Annotation)
            .Where(o => o.Geometry is LinearDimension dim && dim.DimensionStyleId == style.Id)
            .Select(o => o.Id)
            .ToList();

        DimWatcher.Busy = true;
        uint undo = doc.BeginUndoRecord("AATDim update");
        try
        {
            foreach (Guid id in ids)
                FormatInDoc(doc, id, style);
        }
        finally
        {
            doc.EndUndoRecord(undo);
            DimWatcher.Busy = false;
        }
        doc.Views.Redraw();
        RhinoApp.WriteLine("AATDim: {0} dimension(s) with style '{1}' checked. Edit watcher is on for this session.", ids.Count, StyleName);
    }
}


// Watches dimension edits for the whole Rhino session and fixes format and chains once the command is done.
static class DimWatcher
{
    const string DomainKey = "ArchitecturalAtelierTools.AATDim.Watcher";

    class DocState
    {
        public readonly Dictionary<Guid, LinearDimension> Old = new Dictionary<Guid, LinearDimension>();
        public readonly HashSet<Guid> Added = new HashSet<Guid>();
    }

    static readonly Dictionary<uint, DocState> States = new Dictionary<uint, DocState>();
    public static bool Busy;

    static readonly ChainButtonsConduit Conduit = new ChainButtonsConduit();
    static readonly ChainButtonsMouse Mouse = new ChainButtonsMouse();

    // A clicked button, run on the next idle (not inside the mouse event).
    public static uint ClickedDoc;
    public static ChainHandle Clicked;


    public static void Install()
    {
        // Script runs can load this class more than once - drop the older handlers first.
        if (AppDomain.CurrentDomain.GetData(DomainKey) is Action previous)
        {
            try { previous(); } catch (Exception) { }
        }
        RhinoDoc.ReplaceRhinoObject += OnReplace;
        RhinoDoc.AddRhinoObject += OnAdd;
        RhinoApp.Idle += OnIdle;
        Conduit.Enabled = true;
        Mouse.Enabled = true;
        AppDomain.CurrentDomain.SetData(DomainKey, (Action)Uninstall);
    }


    static void Uninstall()
    {
        RhinoDoc.ReplaceRhinoObject -= OnReplace;
        RhinoDoc.AddRhinoObject -= OnAdd;
        RhinoApp.Idle -= OnIdle;
        Conduit.Enabled = false;
        Mouse.Enabled = false;
        States.Clear();
        Clicked = null;
    }


    static bool Ignore(RhinoDoc doc) =>
        Busy || doc == null || doc.UndoActive || doc.RedoActive || doc.IsOpening;


    static DocState State(RhinoDoc doc)
    {
        if (!States.TryGetValue(doc.RuntimeSerialNumber, out DocState state))
        {
            state = new DocState();
            States[doc.RuntimeSerialNumber] = state;
        }
        return state;
    }


    static void OnReplace(object sender, RhinoReplaceObjectEventArgs e)
    {
        try
        {
            if (Ignore(e.Document) || !(e.NewRhinoObject?.Geometry is LinearDimension))
                return;
            DocState state = State(e.Document);
            if (!state.Old.ContainsKey(e.ObjectId) && e.OldRhinoObject?.Geometry is LinearDimension old)
                state.Old[e.ObjectId] = (LinearDimension)old.Duplicate();
        }
        catch (Exception)
        {
        }
    }


    static void OnAdd(object sender, RhinoObjectEventArgs e)
    {
        try
        {
            RhinoDoc doc = e.TheObject?.Document;
            if (Ignore(doc) || !(e.TheObject.Geometry is LinearDimension))
                return;
            State(doc).Added.Add(e.ObjectId);
        }
        catch (Exception)
        {
        }
    }


    static void OnIdle(object sender, EventArgs e)
    {
        if (Busy || Command.InCommand())
            return;

        if (Clicked != null)
        {
            ChainHandle handle = Clicked;
            Clicked = null;
            RhinoDoc clickedDoc = RhinoDoc.FromRuntimeSerialNumber(ClickedDoc);
            if (clickedDoc != null)
            {
                try { AATDim.ApplyHandle(clickedDoc, handle); }
                catch (Exception ex) { RhinoApp.WriteLine("AATDim: button failed - {0}", ex.Message); }
            }
        }

        if (States.Count == 0)
            return;

        foreach (uint serial in States.Keys.ToList())
        {
            DocState state = States[serial];
            States.Remove(serial);
            RhinoDoc doc = RhinoDoc.FromRuntimeSerialNumber(serial);
            if (doc == null)
                continue;

            Busy = true;
            try
            {
                AATDim.ProcessEdits(doc, state.Old, state.Added);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine("AATDim: could not update dimensions - {0}", ex.Message);
            }
            finally
            {
                Busy = false;
            }
        }
    }
}


// Draws the buttons next to selected chains.
class ChainButtonsConduit : DisplayConduit
{
    protected override void DrawForeground(DrawEventArgs e)
    {
        try
        {
            if (DimWatcher.Busy || Command.InCommand())
                return;
            foreach (ChainHandle h in AATDim.Handles(e.RhinoDoc, e.Viewport))
                e.Display.DrawDot(h.Point, h.Label, h.Color, System.Drawing.Color.White);
        }
        catch (Exception)
        {
        }
    }
}


// Clicks on a button run its action instead of selecting.
class ChainButtonsMouse : Rhino.UI.MouseCallback
{
    const double HitRadiusPixels = 11.0;
    bool swallowUp;

    protected override void OnMouseDown(Rhino.UI.MouseCallbackEventArgs e)
    {
        try
        {
            if (e.MouseButton != Rhino.UI.MouseButton.Left || DimWatcher.Busy || Command.InCommand() || e.View == null)
                return;
            RhinoDoc doc = e.View.Document;
            ChainHandle hit = AATDim.HitHandle(doc, e.View.ActiveViewport, e.ViewportPoint, HitRadiusPixels);
            if (hit == null)
                return;
            e.Cancel = true;
            swallowUp = true;
            DimWatcher.ClickedDoc = doc.RuntimeSerialNumber;
            DimWatcher.Clicked = hit;
        }
        catch (Exception)
        {
        }
    }

    protected override void OnMouseUp(Rhino.UI.MouseCallbackEventArgs e)
    {
        if (swallowUp)
        {
            swallowUp = false;
            e.Cancel = true;
        }
    }
}

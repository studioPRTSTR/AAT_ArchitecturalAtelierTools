import json
import math
import os

import Rhino
import Rhino.Geometry as rg
import Rhino.Input as ri
import Rhino.Input.Custom as ric
import Rhino.DocObjects as rd
import scriptcontext as sc
import System.Drawing as sd


CONFIG_FILENAME = "RSoTeSc_config.json"

_CONFIG_DEFAULTS = {
    "layer_name": "A_Isolation_01",
    "default_width": 0.5,
}


def _load_config():
    """
    Read layer_name/default_width from RSoTeSc_config.json next to this
    script, so they can be tweaked without editing the script itself -
    handy while testing in Rhino's script editor. Falls back to the
    built-in defaults if the file is missing or malformed.
    """
    config = dict(_CONFIG_DEFAULTS)
    try:
        script_dir = os.path.dirname(os.path.abspath(__file__))
        config_path = os.path.join(script_dir, CONFIG_FILENAME)
        with open(config_path, "r") as f:
            config.update(json.load(f))
    except Exception:
        pass
    return config


_config = _load_config()
LAYER_NAME = _config["layer_name"]
DEFAULT_WIDTH = _config["default_width"]

SQRT3 = math.sqrt(3.0)


def _clear_previous_output(doc):
    """
    Delete any objects already on the Insulation-Soft layer before
    drawing new ones, so leftover geometry from earlier test runs can
    never get mixed in with (or mistaken for) the current run's output.
    """
    layer_index = doc.Layers.FindByFullPath(LAYER_NAME, True)
    if layer_index < 0:
        return
    existing = [
        obj.Id for obj in doc.Objects
        if obj.Attributes.LayerIndex == layer_index
    ]
    for obj_id in existing:
        doc.Objects.Delete(obj_id, True)


def _ensure_layer(doc):
    """Create (or reuse) a dedicated layer for the insulation symbol."""
    layer_index = doc.Layers.FindByFullPath(LAYER_NAME, True)
    if layer_index >= 0:
        return layer_index
    layer = rd.Layer()
    layer.Name = LAYER_NAME
    layer.Color = sd.Color.FromArgb(255, 120, 170, 255)
    layer.PlotWeight = 0.13  # thin hairline, mm
    return doc.Layers.Add(layer)


def _get_path_curve():
    """
    Let the user either pick an existing curve to use as the insulation
    path, or - if they press Enter without selecting anything - draw a
    new line interactively to define the direction.
    """
    go = ric.GetObject()
    go.SetCommandPrompt("Select a path curve for the insulation run (Enter to draw a new line instead)")
    go.GeometryFilter = rd.ObjectType.Curve
    go.AcceptNothing(True)
    result = go.Get()

    if result == Rhino.Input.GetResult.Object:
        obj_ref = go.Object(0)
        return obj_ref.Curve()

    if result == Rhino.Input.GetResult.Nothing:
        ok, line = ri.RhinoGet.GetLine()
        if not ok:
            return None
        return line.ToNurbsCurve()

    return None


def _get_width():
    gn = ric.GetNumber()
    gn.SetCommandPrompt("Width of the insulation band (tip to tip)")
    gn.SetDefaultNumber(DEFAULT_WIDTH)
    gn.SetLowerLimit(0.001, False)
    gn.Get()
    if gn.CommandResult() != Rhino.Commands.Result.Success:
        return None
    return gn.Number()


def _end_frame(path):
    """Point and unit tangent at the path's actual endpoint."""
    t_end = path.Domain.Max
    end_pt = path.PointAt(t_end)
    end_tangent = path.TangentAt(t_end)
    end_tangent.Unitize()
    return end_pt, end_tangent


def _start_frame(path):
    """Point and unit tangent at the path's actual start."""
    t_start = path.Domain.Min
    start_pt = path.PointAt(t_start)
    start_tangent = path.TangentAt(t_start)
    start_tangent.Unitize()
    return start_pt, start_tangent


def _perp(tangent):
    """World-XY unit vector perpendicular to a (unit) path tangent."""
    perp = rg.Vector3d.CrossProduct(tangent, rg.Vector3d.ZAxis)
    if perp.IsTiny():
        perp = rg.Vector3d.CrossProduct(tangent, rg.Vector3d.XAxis)
    perp.Unitize()
    return perp


def _frame_at_length(path, s, length, end_pt, end_tangent):
    """
    Point and unit perpendicular (world XY) at arc-length distance s from
    the start of the path. If s runs past the path's actual length, the
    frame is linearly extrapolated from the end point/tangent so loop
    generation can safely overshoot the path.
    """
    if s <= length:
        ok, t = path.LengthParameter(s)
        if not ok:
            t = path.Domain.Max
        pt = path.PointAt(t)
        tangent = path.TangentAt(t)
        tangent.Unitize()
    else:
        pt = end_pt + end_tangent * (s - length)
        tangent = end_tangent

    return pt, _perp(tangent)


def _signed_progress(pt, end_pt, end_tangent):
    """How far pt sits past the path's endpoint, measured along the
    end tangent direction. Positive = beyond the path's end."""
    return (pt - end_pt) * end_tangent


def _line_crossings(crv, center, radius, cut_pt, cut_tangent):
    """
    Curve parameters (sorted) where `crv` - a circular arc of the given
    center/radius, lying in the world-XY plane - crosses the infinite
    line through cut_pt perpendicular to cut_tangent.

    A loop's arc sweeps close to 300 degrees around its own circle, which
    means it actually bulges up to half a loop-width past its own nominal
    start and end points before settling there (needed so neighboring
    loops interlock cleanly) - so an arc whose two endpoints both sit
    safely on the same side of a cutoff line can still swing out past it
    and back in between. The only reliable check is the arc's own circle
    geometry: intersecting a circle with a line is a closed-form
    quadratic (no sampling needed), and Curve.ClosestPoint recovers
    whichever of the (up to two) solutions genuinely lands on this arc.
    """
    offset = _signed_progress(center, cut_pt, cut_tangent)
    half_chord_sq = radius * radius - offset * offset
    if half_chord_sq < 0.0:
        return []

    half_chord = math.sqrt(half_chord_sq)
    perp_dir = _perp(cut_tangent)
    base = center - cut_tangent * offset

    params = []
    for candidate in (base + perp_dir * half_chord, base - perp_dir * half_chord):
        ok, t = crv.ClosestPoint(candidate)
        if ok and crv.PointAt(t).DistanceTo(candidate) < 1e-6:
            params.append(t)
    params.sort()
    return params


def _clip_to_line(crv, center, radius, cut_pt, cut_tangent, keep_positive):
    """
    Cut `crv` (a circular arc of the given center/radius) against the
    infinite line through cut_pt perpendicular to cut_tangent, keeping
    only the sub-arc(s) where signed progress along cut_tangent is > 0
    if keep_positive, else < 0. Returns a list of kept pieces (may be
    empty, exactly one, or more than one if the arc crosses the line
    more than once).
    """
    dom = crv.Domain
    cuts = _line_crossings(crv, center, radius, cut_pt, cut_tangent)
    bounds = [dom.Min] + cuts + [dom.Max]

    kept = []
    for i in range(len(bounds) - 1):
        t0, t1 = bounds[i], bounds[i + 1]
        if t1 - t0 < 1e-9:
            continue
        mid_pt = crv.PointAt(0.5 * (t0 + t1))
        if (_signed_progress(mid_pt, cut_pt, cut_tangent) > 0.0) == keep_positive:
            piece = crv.Trim(t0, t1)
            if piece:
                kept.append(piece)
    return kept


def _corner_cut(corner_pt, tan_in, tan_out):
    """
    The miter cutting line for a corner: direction bisecting the angle
    between the incoming segment's backward ray (-tan_in) and the
    outgoing segment's forward ray (tan_out) - the standard line-join
    miter direction, same idea as a mitered corner on a picture frame.

    Returns (cut_tangent, incoming_positive): cut_tangent is the normal
    to that line (for reuse with _clip_to_line/_signed_progress), and
    incoming_positive says which sign of progress along it corresponds
    to the incoming segment's side, found by testing a point just before
    the corner. Returns None if the two directions are (near) identical,
    i.e. not really a corner.
    """
    bisector = tan_out - tan_in
    if bisector.IsTiny():
        return None
    bisector.Unitize()
    cut_tangent = _perp(bisector)

    probe_pt = corner_pt - tan_in
    incoming_positive = _signed_progress(probe_pt, corner_pt, cut_tangent) > 0.0
    return cut_tangent, incoming_positive


def _build_segment_loops(path, width, start_cut, end_cut):
    """
    Build the FULL insulation symbol along a single (straight) segment,
    from start_cut to end_cut. Each cut is (cut_pt, cut_tangent,
    keep_positive) - a line to clip the segment's own first/last loop
    against, exactly what _clip_to_line expects. Passing the path's own
    true start/end frame (perpendicular cutoff) reproduces the ordinary
    end cap; passing a corner's miter line instead makes this segment's
    end stop exactly at the angle bisector shared with its neighbor.

    Every loop is first built as a clean, complete circle-arc: the arc
    between two consecutive "pinch points" (where neighboring loops meet,
    sitting exactly on this segment's centerline), bulging out through
    its own outer tip. A virtual center placed one loop-width before the
    segment's start supplies the pinch point that opens the very first
    loop, so every loop - including the first and last - is built
    identically. Only THEN, whichever loop actually reaches start_cut or
    end_cut gets cut back: its own circle (known center and radius `r`)
    is intersected exactly with the cutting line (see _clip_to_line), and
    trimmed to the crossing point(s). Loops entirely outside the segment
    are dropped; every fully-interior loop is left untouched.
    """
    length = path.GetLength()
    if length <= 0:
        return []

    r = width / (2.0 + SQRT3)
    d = r * SQRT3 / 2.0
    a = r

    end_pt, end_tangent = _end_frame(path)
    start_pt, start_tangent = _start_frame(path)

    start_cut_pt, start_cut_tangent, start_keep_positive = start_cut
    end_cut_pt, end_cut_tangent, end_keep_positive = end_cut

    # Overshoot generously past the segment's end so there's always at
    # least one full loop past it to trim back from.
    n_guess = max(int(math.ceil(length / a)) + 5, 3)

    centers = []
    perps = []
    for i in range(n_guess + 1):
        s = i * a
        pt, perp = _frame_at_length(path, s, length, end_pt, end_tangent)
        side = 1.0 if i % 2 == 0 else -1.0
        centers.append(pt + perp * (side * d))
        perps.append((perp, side))

    virtual_prev_center = (
        (start_pt - start_tangent * a) + _perp(start_tangent) * (-1.0 * d)
    )

    # boundary_pts[i] is the pinch point before loop i; boundary_pts[i+1]
    # is the pinch point after it - both on the segment's centerline.
    boundary_pts = [(virtual_prev_center + centers[0]) * 0.5]
    for i in range(n_guess):
        boundary_pts.append((centers[i] + centers[i + 1]) * 0.5)

    arcs = []
    for i in range(n_guess):
        s_i = i * a
        if s_i > length + a:
            # Every later loop is only further along still - nothing
            # left to build.
            break

        perp, side = perps[i]
        center = centers[i]
        outer_tip = center + perp * (side * r)
        seg_start = boundary_pts[i]
        seg_end = boundary_pts[i + 1]

        try:
            arc_crv = rg.Arc(seg_start, outer_tip, seg_end).ToNurbsCurve()
        except Exception:
            continue

        # Only ever clip the loop(s) actually next to this segment's own
        # start/end, decided by arc-length position (s_i vs. 0/length) -
        # never by the loop's spatial (Euclidean) position (see
        # _line_crossings for why a single loop's own geometry already
        # needs this exact-crossing treatment).
        pieces = [arc_crv]
        if s_i <= a:
            pieces = [
                p for piece in pieces
                for p in _clip_to_line(piece, center, r, start_cut_pt, start_cut_tangent, start_keep_positive)
            ]
        if s_i >= length - a:
            pieces = [
                p for piece in pieces
                for p in _clip_to_line(piece, center, r, end_cut_pt, end_cut_tangent, end_keep_positive)
            ]

        arcs.extend(pieces)

    return arcs


def _build_loop_chain(path, width):
    """
    Build the FULL insulation symbol for the whole path.

    A polyline is broken into its individual straight segments, and each
    is built independently as its own clean, complete loop chain
    (_build_segment_loops) - the path's true start/end use the usual
    perpendicular cap, while every interior vertex uses a miter (angle-
    bisector) cap shared by the two segments that meet there, so they
    never overlap or leave a gap. A path with no real corners (or that
    isn't a literal polyline) is just built as a single segment.
    """
    ok_poly, polyline = path.TryGetPolyline()
    vertices = list(polyline) if ok_poly else []

    if len(vertices) < 3:
        start_pt, start_tangent = _start_frame(path)
        end_pt, end_tangent = _end_frame(path)
        return _build_segment_loops(
            path, width,
            (start_pt, start_tangent, True),
            (end_pt, end_tangent, False),
        )

    arcs = []
    for i in range(len(vertices) - 1):
        seg = rg.LineCurve(vertices[i], vertices[i + 1])
        seg_start_pt, seg_start_tangent = _start_frame(seg)
        seg_end_pt, seg_end_tangent = _end_frame(seg)

        if i == 0:
            start_cut = (seg_start_pt, seg_start_tangent, True)
        else:
            prev_tangent = _end_frame(rg.LineCurve(vertices[i - 1], vertices[i]))[1]
            cut = _corner_cut(vertices[i], prev_tangent, seg_start_tangent)
            if cut is None:
                start_cut = (seg_start_pt, seg_start_tangent, True)
            else:
                cut_tangent, incoming_positive = cut
                start_cut = (vertices[i], cut_tangent, not incoming_positive)

        if i == len(vertices) - 2:
            end_cut = (seg_end_pt, seg_end_tangent, False)
        else:
            next_tangent = _start_frame(rg.LineCurve(vertices[i + 1], vertices[i + 2]))[1]
            cut = _corner_cut(vertices[i + 1], seg_end_tangent, next_tangent)
            if cut is None:
                end_cut = (seg_end_pt, seg_end_tangent, False)
            else:
                cut_tangent, incoming_positive = cut
                end_cut = (vertices[i + 1], cut_tangent, incoming_positive)

        arcs.extend(_build_segment_loops(seg, width, start_cut, end_cut))

    return arcs


def run_soft_insulation():
    doc = sc.doc

    _clear_previous_output(doc)

    path = _get_path_curve()
    if path is None:
        print("Cancelled.")
        return

    width = _get_width()
    if width is None:
        print("Cancelled.")
        return

    arcs = _build_loop_chain(path, width)
    if not arcs:
        print("SoftInsulation: could not build the loop chain.")
        return

    layer_index = _ensure_layer(doc)
    attrs = rd.ObjectAttributes()
    attrs.LayerIndex = layer_index

    joined = rg.Curve.JoinCurves(arcs, 0.01)
    final_curves = list(joined) if joined and len(joined) > 0 else arcs

    obj_ids = [doc.Objects.AddCurve(crv, attrs) for crv in final_curves]

    if len(obj_ids) > 1:
        group_index = doc.Groups.Add()
        for obj_id in obj_ids:
            doc.Groups.AddToGroup(group_index, obj_id)

    doc.Views.Redraw()

    print("SoftInsulation: loop chain added on layer '{}'.".format(LAYER_NAME))


if __name__ == "__main__":
    run_soft_insulation()

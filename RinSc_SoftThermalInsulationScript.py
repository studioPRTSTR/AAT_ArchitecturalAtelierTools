import math

import Rhino
import Rhino.Geometry as rg
import Rhino.Input as ri
import Rhino.Input.Custom as ric
import Rhino.DocObjects as rd
import scriptcontext as sc
import System.Drawing as sd


LAYER_NAME = "Insulation-Soft"
DEFAULT_WIDTH = 5.0

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


def _frame_at_length(path, s, length, end_pt, end_tangent):
    """
    Point and unit perpendicular (world XY) at arc-length distance s from
    the start of the path. If s runs past the path's actual length, the
    frame is linearly extrapolated from the end point/tangent so loop
    generation can safely overshoot the path and be trimmed afterward.
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

    perp = rg.Vector3d.CrossProduct(tangent, rg.Vector3d.ZAxis)
    if perp.IsTiny():
        perp = rg.Vector3d.CrossProduct(tangent, rg.Vector3d.XAxis)
    perp.Unitize()
    return pt, perp


def _signed_progress(pt, end_pt, end_tangent):
    """How far pt sits past the path's endpoint, measured along the
    end tangent direction. Positive = beyond the path's end."""
    return (pt - end_pt) * end_tangent


def _find_exit_parameter(crv, end_pt, end_tangent, samples=300):
    """
    Precisely find the parameter along crv where it first crosses from
    the near side of the cutoff to the far side, via dense sampling plus
    bisection refinement. This is done directly on the loop's own arc
    (not the joined multi-loop curve, and not Rhino's generic
    Intersection.CurvePlane), which avoids the imprecision that showed
    up when cutting the assembled chain - the assembled curve's
    parameterization isn't uniform, especially at shallow crossing
    angles, and that was landing cuts visibly short of the true line.

    Returns None if the curve never crosses (stays on one side the
    whole way).
    """
    dom = crv.Domain
    step = dom.Length / samples

    prev_t = dom.Min
    prev_p = _signed_progress(crv.PointAt(prev_t), end_pt, end_tangent)

    for i in range(1, samples + 1):
        t = dom.Min + step * i
        p = _signed_progress(crv.PointAt(t), end_pt, end_tangent)
        if prev_p <= 0.0 < p:
            lo, hi = prev_t, t
            for _ in range(50):
                mid = 0.5 * (lo + hi)
                pm = _signed_progress(crv.PointAt(mid), end_pt, end_tangent)
                if pm > 0.0:
                    hi = mid
                else:
                    lo = mid
            return hi
        prev_t, prev_p = t, p

    return None


def _clip_loop_chain_to_end(arcs_in_order, end_pt, end_tangent, tol):
    """
    Walk the loops (already-built, untrimmed arcs) in path order. Each
    loop is checked - and trimmed if needed - on its OWN geometry, so
    if more than one loop near the boundary pokes past the cutoff, each
    one is cut precisely where it exits, not just the last one.

    The start-point check uses a very tight epsilon rather than the
    document's (coarser) modeling tolerance: a loop's start point that's
    only a hair past the cutoff (floating-point noise from the previous
    loop's own trim) was comparing as "not > tol" against a tolerance
    like 0.001, so it slipped through as "still before the cutoff" and
    got kept whole - even though the rest of that loop clearly wasn't.
    """
    kept = []
    start_eps = 1e-9

    for crv in arcs_in_order:
        start_progress = _signed_progress(crv.PointAtStart, end_pt, end_tangent)
        if start_progress > start_eps:
            # This loop's own start already sits at/past the cutoff -
            # nothing further along the chain can be valid either.
            break

        t_exit = _find_exit_parameter(crv, end_pt, end_tangent)
        if t_exit is None:
            kept.append(crv)
            continue

        trimmed = crv.Trim(crv.Domain.Min, t_exit)
        if trimmed and trimmed.GetLength() > tol:
            kept.append(trimmed)

    return kept


def _safety_clip(crv, end_pt, end_tangent, tol):
    """
    Final backstop: re-check a (possibly already-clipped, possibly
    joined) curve against the cutoff one more time. If any part of it
    still exceeds the cutoff - whatever the cause - trim it there. This
    runs after the main per-loop clipping and after joining, so it's the
    last line of defense against anything slipping through.
    """
    t_exit = _find_exit_parameter(crv, end_pt, end_tangent, samples=500)
    if t_exit is None:
        return crv
    trimmed = crv.Trim(crv.Domain.Min, t_exit)
    return trimmed if trimmed else crv


def _build_loop_chain(path, width):
    """
    Build the FULL insulation symbol from a single width value,
    overshooting well past the path's end. Trimming back to the path's
    exact endpoint happens afterward, loop by loop (see
    _clip_loop_chain_to_end) - not here.
    """
    length = path.GetLength()
    if length <= 0:
        return None

    r = width / (2.0 + SQRT3)
    d = r * SQRT3 / 2.0
    a = r

    end_pt, end_tangent = _end_frame(path)

    # Overshoot generously so there are always several full loops past
    # the path's end to trim back from.
    n = max(int(math.ceil(length / a)) + 5, 3)

    centers = []
    perps = []
    for i in range(n):
        s = i * a
        pt, perp = _frame_at_length(path, s, length, end_pt, end_tangent)
        side = 1.0 if i % 2 == 0 else -1.0
        centers.append(pt + perp * (side * d))
        perps.append((perp, side))

    tangents = []
    for i in range(n - 1):
        tangents.append((centers[i] + centers[i + 1]) * 0.5)

    arcs = []
    for i in range(n):
        perp, side = perps[i]
        outer_tip = centers[i] + perp * (side * r)

        if i == 0:
            start_pt = 2 * centers[i] - tangents[0]
            end_pt_arc = tangents[0]
        elif i == n - 1:
            start_pt = tangents[-1]
            end_pt_arc = 2 * centers[i] - tangents[-1]
        else:
            start_pt = tangents[i - 1]
            end_pt_arc = tangents[i]

        try:
            arc = rg.Arc(start_pt, outer_tip, end_pt_arc)
            arcs.append(arc.ToNurbsCurve())
        except Exception:
            continue

    return arcs, end_pt, end_tangent


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

    result = _build_loop_chain(path, width)
    if not result:
        print("SoftInsulation: could not build the loop chain.")
        return
    arcs, end_pt, end_tangent = result
    if not arcs:
        print("SoftInsulation: could not build the loop chain.")
        return

    layer_index = _ensure_layer(doc)
    attrs = rd.ObjectAttributes()
    attrs.LayerIndex = layer_index

    tol = doc.ModelAbsoluteTolerance if doc else 0.001

    clipped_arcs = _clip_loop_chain_to_end(arcs, end_pt, end_tangent, tol)
    if not clipped_arcs:
        print("SoftInsulation: could not build the loop chain.")
        return

    joined = rg.Curve.JoinCurves(clipped_arcs, 0.01)
    final_curves = list(joined) if joined and len(joined) > 0 else clipped_arcs
    final_curves = [_safety_clip(crv, end_pt, end_tangent, tol) for crv in final_curves]

    obj_ids = [doc.Objects.AddCurve(crv, attrs) for crv in final_curves]

    if len(obj_ids) > 1:
        group_index = doc.Groups.Add()
        for obj_id in obj_ids:
            doc.Groups.AddToGroup(group_index, obj_id)

    doc.Views.Redraw()

    print("SoftInsulation: loop chain added on layer '{}'.".format(LAYER_NAME))


if __name__ == "__main__":
    run_soft_insulation()

# RSoTeSc – Soft Thermal Insulation for Rhino 8

Draws the soft / batt insulation symbol along a curve. Pick a path, type the band width, done.

![Loop construction](docs/principle-loops.svg)

## Use

1. Run the command `SoftInsulation` (or the script, see below).
2. Pick a curve – or press **Enter** to draw a polyline.
3. Type the band width **W** (tip to tip) and press **Enter**.

The result goes on layer `A_Isolation_01`, joined and grouped. Each run clears that layer first.

### Options

| Option          | Values                                   | Effect                                                        |
|-----------------|------------------------------------------|---------------------------------------------------------------|
| `Spacing`       | number, `0` = auto                       | Distance **s** between loops. Auto = `W / (2 + √3)`.          |
| `Corner`        | `Miter`, `ButtIncoming`, `ButtOutgoing`  | How two segments meet at a corner (see below).               |
| `CrossingLoops` | `Trim`, `Remove`                         | Loops hitting a cut line are trimmed exactly or dropped.     |
| `Rhythm`        | `Continuous`, `Restart`                  | Keep loop spacing across corners, or restart at each corner. |
| `DebugDraw`     | `On`, `Off`                              | Draws cut lines on `A_Isolation_01_debug` and logs values.    |

## Principle

- Loops alternate left / right of the path. Neighbouring loops meet in a **pinch point** on the path.
- Each loop is one arc: pinch point → tip on the band edge → next pinch point.
- Center offset `k = (W² − s²) / 4W`, radius `R = W/2 − k`. Spacing must be smaller than W.
- Every segment is clipped by two cut lines: a perpendicular cap at the path ends, and a corner cut at each vertex.

![Corner modes](docs/principle-corners.svg)

- **Miter** – both segments are cut on the angle bisector.
- **ButtIncoming** – the incoming segment runs through the corner; the outgoing one starts at its edge.
- **ButtOutgoing** – the reverse.

Corners sharper than 15° or straighter than 165° always use Miter.

Supported paths: lines, polylines (open or closed), polycurves with arc segments, single curves. The path is expected in the XY plane.

## Settings file

`RSoTeSc_config.json` sets the defaults. First file found wins:

1. `%APPDATA%\RSoTeSc\RSoTeSc_config.json`
2. next to the installed plugin
3. next to the script

```json
{
    "layer_name": "A_Isolation_01",
    "default_width": 0.5,
    "default_spacing": 0,
    "corner_mode": "miter",
    "trim_crossing": true,
    "continuous_rhythm": true
}
```

All keys are optional. `corner_mode`: `miter`, `butt_incoming`, `butt_outgoing`.

## Install

**As a package (recommended):** Rhino → Options → Package Manager → Sources → add the shared package folder. Then `_PackageManager` → search `RSoTeSc` → Install. Updates show up there too.

**As a script:** `_ScriptEditor` → open `RSoTeSc_RhinoSoftThermalInsulationScript.cs` → Run.

## Build & release

One-time: in `_ScriptEditor` → File → Create Project → save as `RSoTeSc.rhproj` in this folder → add the `.cs` as command `SoftInsulation`.

Release (Rhino must be running):

```powershell
.\build.ps1 -Version 1.0.1 -Feed "\\SERVER\share\RhinoPackages"
git tag v1.0.1
```

This builds `build\<version>\*.yak` and copies it to the feed. Each release needs a higher version number (`1.0.1` fix, `1.1.0` feature).

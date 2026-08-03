Soft Thermal Insulation - Rhino 8 
=================================
Draws the classic "soft" / batt / flexible insulation symbol along a path
you draw, driven by a SINGLE parameter: the width of the insulation band.

CONSTRUCTION PRINCIPLE:
  - All circles have the same radius R.
  - Consecutive circles (alternating side to side across the path) are
    exactly TANGENT to one another - they touch at exactly one point,
    and that point lands exactly ON the path itself.
  - Centers are offset from the path by d = R*sqrt(3)/2, alternating
    left/right, spaced A = R apart along the path (this ratio makes 3
    consecutive centers form an equilateral triangle of side R).
  - Tip-to-tip width of the finished band = R*(2 + sqrt(3)), so R is
    solved directly from the width you give: R = width / (2 + sqrt(3)).

HANDLING VARIABLE PATH LENGTH:
  Since the path length is essentially never an exact multiple of the
  loop spacing A, loops are always drawn at their correct, fixed size
  (never stretched or squashed to force-fit the path). The whole chain
  is first built well past the end of the path, then walked loop by
  loop: each loop is checked directly against its OWN geometry for
  where it crosses the cutoff, using a precise sampling + bisection
  search (not a generic intersection on the assembled multi-loop
  curve, which turned out to land cuts visibly short of the true line
  at shallow crossing angles). If more than one loop near the boundary
  pokes past the cutoff, each one is trimmed individually, right at its
  own exact crossing point.

Workflow:
  1. Draw (or pick) a single curve = the direction/centerline of the
     insulation run (e.g. along a wall or roof cavity).
  2. Type the width of the insulation band (tip to tip). That's it.
  3. A single continuous curve (joined from the arcs), cut off cleanly
     at the end of your path, is added on layer "Insulation-Soft".

------------------------------------------------------------------
HOW TO USE THIS FILE
------------------------------------------------------------------
Option A - Run it directly as a script (no install needed):
  1. In Rhino 8, type: ScriptEditor
  2. File > Open... and open this file, OR drag-and-drop this .py file
     straight onto an open Rhino viewport.
  3. Press Run (or type RunPythonScript and pick this file).
  4. Follow the command-line prompts.

Option B - Turn it into an installable plug-in (.rhi):
  1. In Rhino 8, type: ScriptEditor
  2. File > New > Python 3 > "Command Plug-in" template.
  3. Rename the generated command to "SoftInsulation".
  4. Copy the body of run_soft_insulation() below into that command's
     RunCommand() method (keep the helper functions alongside it).
  5. Use "Create Installer" / Build to produce a .rhi you can install.

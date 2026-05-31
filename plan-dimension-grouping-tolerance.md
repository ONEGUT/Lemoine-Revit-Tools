# Plan — Group clash dimensions by distance-to-edge with a settable tolerance

## Goal

When several clashes line up at about the same perpendicular distance from a
given grid line or slab edge, place a **single chained dimension** for the
whole group instead of one stacked, overlapping dimension per clash. Filled
regions and cross lines stay exactly as they are today — only the **placement
of dimensions** changes.

Grouping is decided **per edge** (each grid line; each closest slab face per
direction), keyed on the clash's perpendicular offset to *that* edge, so pipes
at the same elevation but on opposite sides of the building are never merged.

## Decisions already confirmed by the user

1. **Drawing style** — one chained dimension string per group (witness lines at
   each clash plus the edge).
2. **Tolerance** — a single new `Group Tolerance (mm)` setting applied to both
   axes (not two separate X/Y values).
3. **Grouping rule** — same perpendicular offset to *that specific edge* within
   tolerance, evaluated per edge, same side only. Mirrors the existing
   closest-edge logic. Never merge opposite sides of the building.

## Proposed geometry (the one thing to confirm at approval)

Take a horizontal slab edge / horizontal grid running in X. Three pipes clash at
roughly the same Y (≈ same distance `d` from the edge), at X = x1 < x2 < x3.

```
   edge ───────────────────────────────────────────   (Y = ye)
            │            │            │
            │ d          │ d          │ d        ← TODAY: 3 separate, identical,
            ●            ●            ●            overlapping perpendicular dims
           x1           x2           x3

   PROPOSED — one chained dimension, line PARALLEL to the edge, offset to the
   pipe-row side by the existing Dimension Line Offset; witness lines drop to
   the edge and to each clash:

   ╞════════╪════════════╪════════════╡    one dimension element
   edge    x1           x2           x3
```

- The dimension line runs **parallel to the edge**, positioned `DimLineOffset`
  beyond the row of clashes (same offset setting as today).
- `ReferenceArray` = the edge reference + each grouped clash's cross-line
  reference, ordered along the edge. Revit renders this as one chained string.
- A clash joins the group for an edge only if its signed perpendicular offset to
  that edge is within `GroupTolerance` of the group's offset **and** it is on the
  same side / nearest the same face. Otherwise it keeps its own dimension.
- Vertical grids / vertical edges are the mirror image (dimension line parallel
  to a vertical edge).

### Final decision table (confirmed)

Per edge (each grid line; each clash's closest slab face per direction), group
clashes by **signed perpendicular distance** to that edge (same side only — opposite
sides of the building never merge), then:

| Run vs. edge | Treatment |
|---|---|
| **Parallel** (equidistant row spread along the edge) | one **single** dimension for the group |
| **Perpendicular** (one along-band, varying distance) | one **chained** dimension (edge → each clash) |
| **Skew / isolated** (neither within tolerance) | perpendicular **single** dimension per clash, both X and Y |

`GroupToleranceMm` is the single mm tolerance for both axes; `0` disables grouping
(one dimension per clash, current behaviour).

## Files to change

### 1. `Source/Tools/Testing/ClashDimension/ClashDimensionSettings.cs`
- Add `public double GroupToleranceMm { get; set; } = 50.0;` (mm, both axes;
  `0` = grouping off → current per-clash behaviour). Public DTO, already
  `XmlSerializer`-safe.

### 2. `Source/Tools/Testing/ClashDimension/ClashDimensionViewModel.cs`
- New field `_groupToleranceMm = ClashDimensionSettings.Instance.GroupToleranceMm;`
- New settings row in `BuildS5` ("Group Tolerance (mm)") — added via the existing
  `AddSettingRow` helper, parsed `>= 0`. **WPF change → invoke
  `/revit-navisworks-ui` before editing.**
- Persist in the `Save` block (≈ line 988) and push to handler (≈ line 1010):
  `s.GroupToleranceMm = _groupToleranceMm;` and
  `_handler.GroupToleranceMm = _groupToleranceMm;`
- Add a "Group Tol" review card / S5 summary line for parity.

### 3. `Source/Tools/Testing/ClashDimension/ClashDimensionEventHandler.cs`
- Add `public double GroupToleranceMm { get; set; } = 0.0;`
- **Restructure the annotation flow (the core change):**
  - Split `CreateClashAnnotation` so marker creation (filled region + cross
    lines) is separate from dimension placement. Marker creation returns the
    clash's `hRef`, `vRef`, `cx`, `cy` for that view.
  - In the per-view pass: build all markers first and collect a
    `List<ClashMarker>` (cx, cy, hRef, vRef). Fill regions and cross lines are
    untouched.
  - New grouping pass per view:
    - For each grid: compute each clash's **signed** perpendicular offset to the
      grid line; bucket clashes by (same side, offset within `GroupTolerance`);
      emit one chained `NewDimension` per bucket (refs = grid + member cross-line
      refs ordered along the grid). Singletons fall back to today's single dim.
    - For each floor: reuse the existing closest-face-per-direction logic
      (`FaceClosestDist2D`) to assign each clash a face + perpendicular offset;
      bucket by (same face reference, offset within `GroupTolerance`); emit one
      chained dimension per bucket.
  - `GroupToleranceMm <= 0` preserves exactly the current one-dim-per-clash output.
- Tag every grouped dimension `Mark = "LemoineCD"` so existing cleanup keeps working.

## Risks / notes
- Cannot build on Linux (per CLAUDE.md) — verification is by code review + the
  user's Windows build.
- Revit rejects zero-length dimension segments; ordering refs along the edge and
  de-duplicating near-coincident clashes avoids this.
- Silent-failure scan will run on the diff before reporting complete.

## Branch
- Designated feature branch: `claude/dimension-grouping-tolerance-O9d74`.
- **Need:** which branch to base it from (e.g. `main`)?

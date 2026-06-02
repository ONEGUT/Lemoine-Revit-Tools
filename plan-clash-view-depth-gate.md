# Plan — Clash Finder per-view depth gate

## Problem

In a multi-story building with identical stacked levels, the Clash Finder draws
every clash into every selected view, flattened to `Z = 0`. A clash that lives
on another level is still stamped onto this plan, producing:

- ~5 markers stacked on the same XY (one per identical level), and
- markers "over nothing" (the clashing element is on a level not visible here).

### Root cause (confirmed in `ClashEngine.cs`)

1. Detection is document-wide, not level-aware — `ScanRules` / `ScanCategories`
   collect with `new FilteredElementCollector(srcDoc)` (`:227`, `:278`), no view
   or level scope, so all levels' elements enter the candidate pool.
2. The marker loop (`:147–164`) stamps each clash into each selected view with
   **no visibility/elevation test**.
3. `CreateClashGraphics` flattens markers to `Z = 0` (`:646`, `:661–670`), so a
   clash at any elevation lands on this plan.

All target elements are in **linked models**. `GetHostBBox` (`:444–453`) already
transforms link bboxes into host world coordinates, so a per-view depth gate
keyed on the clash's host-world Z is link-robust and needs no link-visibility
logic. (A view-scoped collector — the rejected alternative — would return zero
linked elements.)

## Change

Add a **per-view visible Z-range gate** to the marker loop: a clash is drawn in
a view only if its overlap Z-interval intersects that view's visible Z-range.

### Files

- `Source/Tools/Testing/ClashFinder/ClashEngine.cs`
  - New helper `TryGetViewZRange(View, out double zMin, out double zMax)`:
    - `ViewPlan` (FloorPlan / CeilingPlan): resolve `GetViewRange()` →
      top clip and view-depth planes → absolute Z via
      `level.Elevation + GetOffset(plane)`. Unlimited / unresolved planes →
      treat that bound as ±∞.
    - `View3D` with an active section box: use the section-box Z extent
      (transformed to world).
    - Any other view type (sections, elevations, unbounded plans): return
      `false` → **no gate** (draw as today) so we never wrongly hide marks.
  - In `Run`, precompute each selected view's range once into a dictionary
    before the clash loop (avoids recomputing per clash).
  - In the inner loop, skip a clash whose overlap Z-interval misses the view's
    range by more than a small tolerance (reuse the marking tolerance / a small
    buffer so clashes sitting on the cut plane aren't dropped).
  - Count skips per view and **log** `View 'X': N clash(es) outside the view's
    depth range — skipped.` (no silent skipping, per CLAUDE.md).

### Not in this change

- XY clustering of same-level multiples (cosmetic; layer on later if needed).
- Any change to detection scope, tagging, or the auto-dimension pipeline.

## Verification

- Run the Clash Finder on one level's plan in the stacked-level model: marker
  count should drop to this level's clashes only; the "over nothing" marks and
  the stacked duplicates disappear.
- Log shows the skipped-out-of-range count matching the other levels' clashes.

## Post-change

Run the silent-failure scan over the diff before reporting complete.

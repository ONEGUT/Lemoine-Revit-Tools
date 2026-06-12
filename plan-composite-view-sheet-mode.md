# Plan — Composite Mode for Place Dependent Views

## Goal

Add a second mode to **Place Dependent Views**: pick a source view, and the tool
creates **one sheet** holding that view plus every callout / section / elevation
whose marker is **visible in** that view. Layout preference:

- The **source view** anchors at **top-center** (when it is wide relative to the
  drawing area) or **left-center** (when it is tall), chosen automatically from
  its measured size and the drawing-area shape.
- The **sub views** — which will have mixed scales and shapes — are organized
  into **aligned rows** (below a top-anchored source) or **aligned columns**
  (right of a left-anchored source).

## Mode selection (UI)

Step 1 gains a `LemoineSingleSelect` above the view picker with two modes:

- **"Dependents → one sheet per parent"** (existing behaviour, default)
- **"View + its callouts/sections → one sheet"** (new)

Switching modes rebuilds the picker list in place (same step content — no new
step, no conditional-step machinery). Steps 2–5 (title block, naming, layout,
review) are shared by both modes; review text and the S1 summary become
mode-aware.

### View list for the new mode

Candidates = non-template, non-dependent graphical views of types that can host
markers (FloorPlan, CeilingPlan, AreaPlan, EngineeringPlan, Section, Elevation,
Detail). We do **not** pre-count each view's markers at launch — that would need
one view-scoped collector per view (expensive in large models). Sub views are
discovered at run time; a source view with no visible callouts/sections is
skip-and-logged.

## Sub-view discovery (run time, Revit thread)

For each selected source view:

1. `new FilteredElementCollector(doc, sourceView.Id).OfCategory(OST_Viewers)`
   returns the section/callout marker elements actually visible in that view
   (hidden markers are excluded by the scoped collector). Each marker maps to
   its view **by name** (view names are unique in Revit); unresolved names are
   skip-and-logged.
2. `ElevationMarker`s visible in the view (scoped collector, class filter) map
   to real views via `marker.GetViewId(i)` per used index.
3. Drop the source view itself, duplicates, view templates, and any view that
   fails `Viewport.CanAddViewToSheet` (already on a sheet) — skip-and-log, same
   as the existing mode. If the **source view** itself is already on a sheet,
   the whole item fails with a clear log line (no sheet created).

## Layout math — new `CompositeSheetLayout` (Revit-free)

New static class beside `SheetLayoutPacker` (same folder, same conventions:
sheet feet, centers relative to drawing-area bottom-left, overflow flag).

`Pack(parentRect, childRects, areaW, areaH, gap)`:

1. Build **two candidate layouts**:
   - **TopBand**: parent centered horizontally at the top edge of the area;
     children shelf-packed into **rows** in the band below (full area width),
     each row's items center-aligned vertically within the row, rows centered
     as a block in the leftover band.
   - **LeftBand**: parent centered vertically at the left edge; children packed
     into **columns** in the band to the right (full area height), each
     column's items center-aligned horizontally within the column.
2. Children are sorted by decreasing height (rows) / decreasing width (columns)
   before shelf packing so shelves come out visually aligned; placements are
   returned keyed to input order.
3. **Scoring**: prefer the candidate that fits; if both fit, pick the one whose
   parent orientation matches (wide parent → TopBand, tall parent → LeftBand),
   tie-broken by less wasted band area; if neither fits, least overflow —
   mirroring `SheetLayoutPacker`'s fit-first scoring. Overflow logs the same
   "[WARN] … don't fit the drawing area" message.

Sizes come from the **existing measure pipeline** unchanged: accurate mode
places all viewports provisionally → one regen per sheet → `GetBoxOutline()`;
estimate mode uses the crop-box estimate (`EstimateRect`).

## Event handler changes

`PlaceDependentViewsEventHandler` gains a `Mode` input (enum:
`DependentsPerParent` / `CompositeOneSheet`). The per-item loop branches after
sheet creation: composite mode discovers sub views (above), places source + subs,
then calls `CompositeSheetLayout` instead of `SheetLayoutPacker`. Everything
else is reused as-is: sheet numbering (`NextFreeNumber`), naming tokens
(`{ParentViewName}` = the source view), Sheet Series write, failure
preprocessor, progress/complete callbacks.

**Bubble trim** applies to the **sub views only** in composite mode — trimming
the source view's annotation crop could clip the very section heads / callout
boundaries the user wants to see on the source. The S4 note gains one sentence
saying so.

## Files changed

| File | Change |
|---|---|
| `Source/Tools/Testing/PlaceDependentViews/CompositeSheetLayout.cs` | **New** — Revit-free parent-anchored row/column layout |
| `Source/Tools/Testing/PlaceDependentViews/PlaceDependentViewsViewModel.cs` | Mode select in S1, mode-aware picker/summary/review, pass `Mode` to handler |
| `Source/Tools/Testing/PlaceDependentViews/PlaceDependentViewsEventHandler.cs` | `Mode` input, sub-view discovery, composite placement branch |
| `Source/Commands/Testing/PlaceDependentViewsCommand.cs` | Also gather composite-mode candidate views (cheap class/type filter, no marker counting) |
| `Source/Tools/Testing/PlaceDependentViews/ParentViewEntry.cs` | Allow a "subs unknown" entry (no dep-count suffix) for composite candidates |

No changes to `SheetLayoutPacker`, `StepFlowWindow`, or any shared control.

## Out of scope

- Nested discovery (callouts visible inside the placed sections) — one level only.
- Manual per-view layout overrides; rotation of viewports.
- Changing the existing dependents mode's behaviour in any way.

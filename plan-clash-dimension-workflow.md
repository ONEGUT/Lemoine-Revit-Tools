# Plan: Clash Dimension Workflow

## Overview

New tool that identifies element clashes between two filter-defined groups, draws a colour-coded cross annotation over each clash zone in user-selected views, then places locating dimensions from user-selected grids and/or slab edges to those annotations. A configurable tolerance expands the annotation on all sides for sleeve estimation.

---

## Workflow (5-step StepFlowWindow)

| Step | Label | Content |
|------|-------|---------|
| 1 | Select Views | Multi-select view picker (same pattern as BatchDimension step 1) |
| 2 | Group 1 Filters | Multi-select from existing AutoFilters rules — source of clash colour; applies to host document and optionally linked models |
| 3 | Group 2 Filters | Multi-select from existing AutoFilters rules — clash targets (must not overlap Group 1); same host + link scope |
| 4 | References | User picks grids (checkbox list by name) and/or floor slabs (checkbox list by level + type) to dimension from. Optional — skip to run annotations only |
| 5 | Settings & Run | Tolerance (mm, default 25.4), dimension style picker, dimension line offset (mm), dimension target toggle (Edge / Centre), fill style toggle (Solid / Outline), cross line type picker (dropdown from document line styles), clear previous output checkbox, review cards, Run button |

---

## Color Logic

- Each clash annotation is coloured with the **SurfColor** of the Group 1 filter that owns the clashing element.
- Multiple Group 1 filters each keep their own distinct colour.

---

## Linked Model Support

- Step 2 and Step 3 filter pickers show filters from the host document only (filters are defined per-document).
- The event handler collects elements from three sources: the host document, and all loaded `RevitLinkInstance` documents.
- Each element is tracked as a `ClashElement` record: `{ Document, LinkInstance (nullable), ElementId, FilterRuleConfig }`.
- Geometry for any element is retrieved from its source document, then transformed into host coordinates via `linkInstance.GetTotalTransform()` (identity transform for host elements).

### Clash detection strategy by pair type

| G1 source | G2 source | Pre-screen | Confirm |
|-----------|-----------|------------|---------|
| Host | Host | `ElementIntersectsElementFilter(g2Id)` on host collector | Boolean intersect in host coords |
| Host | Link | BBox of link element transformed to host coords vs G1 BBox | Boolean intersect in host coords |
| Link | Host | BBox of link element transformed to host coords vs G2 BBox | Boolean intersect in host coords |
| Link A | Link A | BBox comparison in link coords (same transform) | Boolean intersect in host coords |
| Link A | Link B | Both BBoxes transformed to host coords, then compared | Boolean intersect in host coords |

All solid Boolean intersections happen in **host coordinates**. For link elements, the solid is retrieved from the link document and transformed via `GeometryElement.GetTransformed(linkTransform)` before the Boolean operation.

### Filter expression transfer to linked documents

`ParameterFilterElement.GetElementFilter()` returns rules based on `BuiltInParameter` or shared parameter GUIDs — both of which are document-independent and transfer correctly to linked documents. Project parameters (document-specific ElementIds) will not transfer; this is a known limitation and will be noted in the run log if a filter cannot be applied to a linked document.

---

## Tolerance

- A single user-configurable value (default 25.4 mm / 1 inch) applied uniformly to all four sides of the clash bounding box **before** creating the annotation.
- Expands the FilledRegion and cross arms outward so the annotated zone represents the estimated sleeve/penetration footprint.
- Stored in `ClashDimensionSettings` and exposed in Step 5.

---

## Annotation Geometry

For each clash, in each selected view:

1. Project the clash contact solid onto the view plane to get a bounding box (minX, maxX, minY, maxY in view coordinates).
2. Apply tolerance: expand each side by the configured amount.
3. Compute centre: `(cx, cy) = ((minX+maxX)/2, (minY+maxY)/2)`.
4. Create `FilledRegion` from a rectangle `CurveLoop` of the expanded bounding box:
   - **FilledRegionType reuse:** before duplicating, check whether a type named `"LemoineClash_{RRGGBB}"` already exists in the document. Reuse it if found; duplicate and rename if not. This prevents type accumulation across multiple runs.
   - **Solid fill mode:** set `ForegroundPatternId` to the built-in solid fill pattern obtained via `FillPatternElement.GetFillPatternElementId(doc, FillPatternTarget.Drafting, true)`, set `ForegroundPatternColor` to Group 1 SurfColor.
   - **Outline-only mode:** set `ForegroundPatternId` to `ElementId.InvalidElementId` and `BackgroundPatternId` to `ElementId.InvalidElementId` — only the boundary line is drawn. This prevents visual stacking when multiple clash zones overlap.
5. Tag created `FilledRegion` and `DetailLine` elements using the built-in `Mark` parameter (`BuiltInParameter.ALL_MODEL_MARK = "LemoineCD"`). No shared parameter file or category binding required — `Mark` exists on all relevant element types out of the box.
6. Place **two `DetailLine` elements** forming a cross:
   - Horizontal arm: `(minX, cy) → (maxX, cy)`
   - Vertical arm: `(cx, minY) → (cx, maxY)`
   - Also set `Mark = "LemoineCD"` on these.
7. Dimension reference points — chosen by the **dimension target** setting:
   - **Edge (default):** cross arm endpoints `(minX, cy)`, `(maxX, cy)`, `(cx, minY)`, `(cx, maxY)`.
   - **Centre:** midpoints of the cross arms, i.e. the intersection point `(cx, cy)`. To get a dimensionable Reference at the centre, split each arm into two segments so the join point is an endpoint: `(minX, cy) → (cx, cy)` and `(cx, cy) → (maxX, cy)`.

## Duplicate Prevention

- Before creating any annotations, if "Clear previous output" is checked in Step 5:
  - Collect all `FilledRegion` and `DetailCurve` elements in each target view whose `Mark` parameter equals `"LemoineCD"`.
  - Delete them in a sub-transaction before placing new ones.
- This allows a clean re-run without manual cleanup.

---

## Dimensioning Logic

### Grid dimensions

From the user's selected grids, the event handler picks **at most one grid per axis** — the closest vertical grid and the closest horizontal grid to the clash centre. This produces a maximum of two grid dimensions per clash regardless of how many grids are selected, keeping the view uncluttered.

For each chosen grid:

1. Determine grid orientation in view coordinates (near-vertical or near-horizontal) by projecting `grid.Curve` direction onto the view's right/up axes.
2. Get the grid's curve `Reference` via `grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).First().Reference`. This returns the stable geometry reference required for `NewDimension` — do **not** use `new Reference(gridElement)` which references the element, not its curve.
3. For a **vertical grid** (runs up/down): dimension horizontally from the grid to the left or right cross arm reference point (edge or centre per setting) — whichever side is closer to the grid. Dimension line placed at `cy`, offset above/below by the configured amount.
4. For a **horizontal grid** (runs left/right): dimension vertically from the grid to the top or bottom cross arm reference point. Dimension line placed at `cx`, offset left/right.
5. Call `doc.Create.NewDimension(view, dimLine, refArray, dimType)`.

### Slab edge dimensions

For each user-selected floor element:

1. Get the floor's geometry solid via `floor.get_Geometry(options)`.
2. Iterate faces — find vertical faces (face normal is horizontal) that are nearest to the clash centre point.
3. Use `PlanarFace.Reference` as the reference for the dimension.
4. Pick the nearest cross arm endpoint on the side facing that face.
5. Call `doc.Create.NewDimension(view, dimLine, refArray, dimType)`.

---

## Files to Create

### Command
- `Source/Commands/Testing/ClashDimensionCommand.cs`
  - Implements `IExternalCommand`
  - Creates `StepFlowWindow` with `ClashDimensionViewModel`
  - Same lifecycle pattern as `BatchDimensionCommand.cs`

### ViewModel
- `Source/Tools/Testing/ClashDimension/ClashDimensionViewModel.cs`
  - Implements `ILemoineTool` only — no `ILemoineToolSettings`, no settings overlay panel
  - 5-step wizard; all configuration lives inside the wizard steps
  - On open, pre-populates all inputs from `ClashDimensionSettings.Instance` (last-used values)
  - On Run, writes current values back to `ClashDimensionSettings` before firing the event
  - Reads filter list from `AutoFiltersSettings.Instance`
  - Reads available grids and floors from the active document
  - Validates: ≥1 view, ≥1 Group 1 filter, ≥1 Group 2 filter, groups non-overlapping
  - References (Step 4) are optional — if none selected, annotations are placed without dimensions
  - Fires `ClashDimensionEvent` on Run

### Event Handler
- `Source/Tools/Testing/ClashDimension/ClashDimensionEventHandler.cs`
  - Implements `IExternalEventHandler`
  - **Phase 1 — Element Collection**
    - Build `group1Elements` (ElementId → FilterRuleConfig) and `group2Elements` dictionaries using `ParameterFilterElement.GetElementFilter()` + `FilteredElementCollector.WherePasses()`
  - **Phase 2 — Clash Detection**
    - Pre-screen with `ElementIntersectsElementFilter(elem)`
    - Confirm with `BooleanOperationsUtils.ExecuteBooleanOperation(solid1, solid2, BooleanOperationsType.Intersect)`
    - Skip pairs with zero-volume result
  - **Phase 3 — View Projection + Annotation**
    - For each (view, clash):
      - Project contact solid bounding box to view plane using `View.ViewDirection` / `View.UpDirection`
      - Apply tolerance expansion
      - Create `FilledRegion` (rectangle CurveLoop, coloured SurfColor)
      - Create two `DetailLine` cross arms
  - **Phase 4 — Dimensioning**
    - For each selected grid: `NewDimension` from grid Reference to nearest cross arm endpoint Reference
    - For each selected floor: find nearest vertical face Reference, `NewDimension` to nearest cross arm endpoint Reference

### Settings
- `Source/Tools/Testing/ClashDimension/ClashDimensionSettings.cs`
  - XML-persisted singleton (same pattern as `BatchDimensionSettings.cs`)
  - Stores: tolerance value, dimension style name, dimension line offset, dimension target (edge/centre), fill style (solid/outline), cross line type name, clear-previous flag, last selected filter IDs (group 1 & 2), last selected grid/floor ElementIds

---

## Files to Modify

### `Source/App.cs`
- Register `ExternalEvent` / `ClashDimensionEventHandler` pair in `OnStartup()`
- Add static properties (`ClashDimensionHandler`, `ClashDimensionEvent`)
- Add ribbon button to the **Testing** panel

---

## Key Revit API Calls

| Purpose | API |
|---------|-----|
| Get elements matching a filter | `ParameterFilterElement.GetElementFilter()` + `FilteredElementCollector.WherePasses()` |
| Iterate linked models | `FilteredElementCollector` for `RevitLinkInstance`; `linkInstance.GetLinkDocument()` |
| Transform link geometry to host | `linkInstance.GetTotalTransform()` |
| BBox pre-screen (cross-link) | `BoundingBoxIntersectsFilter` in host coordinates |
| Compute contact solid | `BooleanOperationsUtils.ExecuteBooleanOperation(..., Intersect)` |
| Project geometry to view | `View.ViewDirection`, `View.UpDirection` |
| Tag created elements | `element.get_Parameter(BuiltInParameter.ALL_MODEL_MARK).Set("LemoineCD")` — no shared parameter setup required |
| Clear previous output | `FilteredElementCollector` filtered by `Mark == "LemoineCD"`, `doc.Delete(ids)` |
| Transform link geometry | `GeometryElement.GetTransformed(linkInstance.GetTotalTransform())` |
| Create filled region | `FilledRegion.Create(doc, typeId, viewId, IList<CurveLoop>)` |
| Reuse/create FilledRegionType | Check for existing type named `"LemoineClash_{RRGGBB}"` before duplicating |
| Solid fill pattern | `FillPatternElement.GetFillPatternElementId(doc, FillPatternTarget.Drafting, true)` |
| Colour annotation | `FilledRegionType.ForegroundPatternColor` |
| Outline-only fill | `FilledRegionType.ForegroundPatternId = ElementId.InvalidElementId` |
| Create cross arms | `doc.Create.NewDetailCurve(view, line)` |
| Set cross line style | `detailLine.LineStyle = lineStyleCategory` — resolved from `doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories` by name |
| Grid curve reference | `grid.GetCurvesInView(DatumExtentType.ViewSpecific, view).First().Reference` — **not** `new Reference(grid)` |
| Nearest grid per axis | Sort user-selected grids by perpendicular distance to clash centre; take closest vertical + closest horizontal |
| Slab edge reference | `PlanarFace.Reference` from floor geometry solid |
| Create dimension | `doc.Create.NewDimension(view, line, refArray, dimType)` |

---

## Out of Scope (this branch)

- Section/elevation view support (plan views only — projection is simpler)
- Level-based dimensioning
- Clash deduplication across runs (each run recreates annotations)
- Export to schedule or report

---

## Branch

Base: `main`  
Name: `clash-dimension-workflow`

# Plan: Clash Dimension Workflow

## Overview

New tool that identifies element clashes between two filter-defined groups, draws a coloured filled-region annotation over each contact area in user-selected views, then places dimensions to those annotations.

---

## Workflow (4-step StepFlowWindow)

| Step | Label | Content |
|------|-------|---------|
| 1 | Select Views | Multi-select view picker (same pattern as BatchDimension step 1) |
| 2 | Group 1 Filters | Multi-select from existing AutoFilters rules — these elements are the "source" of the clash colour |
| 3 | Group 2 Filters | Multi-select from existing AutoFilters rules — these elements are the clash targets |
| 4 | Settings & Run | Dimension style picker, annotation offset, summary, Run button |

---

## Color Logic

- Each clash annotation (FilledRegion) is coloured with the **SurfColor** of the specific Group 1 filter that owns the clashing element.
- Multiple Group 1 filters each keep their own distinct colour.
- Group 2 filter colour is not used for the annotation.

---

## Files to Create

### Commands
- `Source/Commands/Testing/ClashDimensionCommand.cs`
  - Implements `IExternalCommand`
  - Creates `StepFlowWindow` with `ClashDimensionViewModel`
  - Same lifecycle pattern as `BatchDimensionCommand.cs`

### ViewModel
- `Source/Tools/Testing/ClashDimension/ClashDimensionViewModel.cs`
  - Implements `ILemoineTool`
  - 4-step wizard (views → group 1 → group 2 → settings)
  - Reads filter list from `AutoFiltersSettings.Instance`
  - Validates: ≥1 view, ≥1 Group 1 filter, ≥1 Group 2 filter, groups must not overlap
  - Fires `ClashDimensionEvent` on Run

### Event Handler
- `Source/Tools/Testing/ClashDimension/ClashDimensionEventHandler.cs`
  - Implements `IExternalEventHandler`
  - **Phase 1 — Element Collection**
    - For each selected filter, call `ParameterFilterElement.GetElementFilter()` then `FilteredElementCollector` to build `group1Elements` and `group2Elements` dictionaries (ElementId → FilterRuleConfig for colour lookup)
  - **Phase 2 — Clash Detection**
    - For each element in group1, apply `ElementIntersectsElementFilter(elem)` to pre-screen group2 candidates
    - For confirmed candidates, get geometry solids and call `BooleanOperationsUtils.ExecuteBooleanOperation(solid1, solid2, BooleanOperationsType.Intersect)` to obtain the contact solid
    - Skip pairs where the result solid has zero volume
  - **Phase 3 — View Projection**
    - For each (view, clash pair):
      - Build a transform from the view's `ViewDirection` and `UpDirection`
      - Extract the face(s) of the contact solid whose normal is closest to the view direction
      - Project face curves into view coordinates to produce a `CurveLoop`
      - Fall back to projected bounding box rectangle if face extraction fails
  - **Phase 4 — Annotation Creation**
    - Duplicate a base `FilledRegionType`, set `ForegroundPatternColor` to the Group 1 filter's SurfColor
    - Call `FilledRegion.Create(doc, frtId, view.Id, curveLoops)` to place the clash zone marker
    - Place `DetailLine` elements along the bounding edges of the CurveLoop (these carry dimension references)
  - **Phase 5 — Dimensioning**
    - Build a `ReferenceArray` from the bounding detail lines
    - Call `doc.Create.NewDimension(view, dimLine, refArray, dimType)` using the style selected in step 4
    - Offset the dimension line by the configured amount from the annotation boundary

### Settings
- `Source/Tools/Testing/ClashDimension/ClashDimensionSettings.cs`
  - XML-persisted singleton (same pattern as `BatchDimensionSettings.cs`)
  - Stores: last used dimension style name, annotation offset, last selected filter IDs for group 1 & 2

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
| Fast clash pre-screen | `ElementIntersectsElementFilter(ElementId)` |
| Compute contact solid | `BooleanOperationsUtils.ExecuteBooleanOperation(..., Intersect)` |
| Project geometry to view | `View.ViewDirection`, `View.UpDirection`, `Transform` |
| Create annotation | `FilledRegion.Create(doc, typeId, viewId, IList<CurveLoop>)` |
| Colour the annotation | `FilledRegionType.ForegroundPatternColor` |
| Create dimension | `doc.Create.NewDimension(view, line, refArray, dimType)` |

---

## Out of Scope (this branch)

- Section/elevation view support (plan views only for the initial cut — projection is simpler)
- Clash grouping/deduplication across runs (each run recreates annotations)
- Exporting clash results to a schedule or report

---

## Open Questions for User

- None — all design decisions confirmed.

---

## Branch

To be decided by user before implementation begins.

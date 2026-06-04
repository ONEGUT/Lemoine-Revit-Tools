# Plan: Make Ceiling Grids — name-based hide filters + model tabs

**Designated branch:** `claude/ceiling-grids-filter-tabs-53qOq`

## Problem

1. **Excluded ceiling types are not actually hidden.** `MakeCeilingGridsRunHandler`
   resolves only **host** `CeilingType` ids (Phase 1 sets linked `TypeIdValue = 0`) and
   hides ceilings per-instance via `view.HideElements`, collected with
   `FilteredElementCollector(doc, view.Id)`. That collector never returns ceilings that
   live **inside links**, so linked ceilings of an "excluded" type stay visible in the
   RCP view and the exported DWG.
2. The Filter step is a flat scrolling list with two search boxes (Family / Type),
   which the user wants reorganized into **tabs per source model** with the search
   boxes removed.

## Fix

### A. Hide via the Ceiling-Heatmap filter mechanism — `MakeCeilingGridsRunHandler.cs`

Replicate `CeilingHeatmapEventHandler`'s exact pattern instead of a single combined
filter:

- Receive `ExcludedTypeNames : List<(string Family, string Type)>` from the ViewModel.
- Register a `"Ceiling Grids — Hidden"` trade (id `CG`, `ExternallyManaged = true`) with
  **one rule per excluded ceiling type** — `Parameter = "Type Name"`, `MatchType =
  "equals"`, `Match = [typeName]`, `Visible = false` — via `RegisterHideTrade`
  (mirrors `RegisterCeilingHeatmapTrade`).
- Create **one `ParameterFilterElement` per rule** (reuse-by-name) matching ceilings by
  `ALL_MODEL_TYPE_NAME` (a link-safe built-in parameter, like the heatmap's height
  parameter), and apply it to every created RCP view with `SetFilterVisibility(false)`.
- `DeleteHideFilters` removes prior `CG_` filters from all views before recreating.
- `ReportLinkDisplayModes` warns about any link not shown "By Host View" (the same
  accommodation the heatmap makes — that is what lets host filters cascade onto linked
  ceilings).
- Per-view and per-filter `try/catch`, `ConfigureFailures` on every transaction.
- Phases: create views (0–40%) → hide filters + trade (40–70%) → DWG export (70–100%),
  so the export reflects the applied filters.

### B. Reorganize Filter step into model tabs — `MakeCeilingGridsViewModel.cs`

- Remove `BuildSearchBox`, `MakeFilterLabel`, `_filterFamilyBox`, `_filterTypeBox`,
  `RefreshTypeRows`, and the flat row/search-grid layout.
- Replace with `LemoineMultiSelectTabs`: one **group (tab) per source model**
  (host + each selected link), items = that model's `"{Family}  —  {Type}"` rows.
  Checked = **included**; unchecked = excluded.
- Exclusion is **name-based** (`"{Family}|{Type}"`), so the same family/type checked in
  one model applies everywhere — consistent with the name-based hide filter.
- Subscribe to `SelectionChanged` **before** `SetGroups` (per the
  `LemoineMultiSelectTabs` contract in CLAUDE.md); recompute `_excludedTypeKeys` from
  the selection on every change.
- Update `ReviewValues` / `SummaryFor` to count distinct name keys.

### C. ViewModel.Run wiring

- Pass `ExcludedTypeNames` (resolved from `_excludedTypeKeys`) instead of
  `IncludedTypes`.

## Files changed

- `Source/Tools/T02-Ceilings/MakeCeilingGridsRunHandler.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs`

No XAML changes (controls are built programmatically). Phase 1 handler unchanged
(its per-source scan already supplies everything the tabs need).
</content>
</invoke>

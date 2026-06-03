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

### A. Hide via `ParameterFilterElement` (name-based) — `MakeCeilingGridsRunHandler.cs`

- Drop the host-type-id math (`allHostTypeIds` / `includedHostIds` / `excludedTypeIds`).
- Receive `ExcludedTypeNames : List<(string Family, string Type)>` from the ViewModel.
- Before the per-level loop, build **one** `ParameterFilterElement`
  (`"Lemoine — Hidden Ceiling Types"`, category `OST_Ceilings`) whose `ElementFilter`
  is an OR of `(Family Name == F  AND  Type Name == T)` per excluded pair, using the
  codebase-proven params `ELEM_FAMILY_PARAM` + `ALL_MODEL_TYPE_NAME`
  (mirrors `AutoFiltersEventHandler`). Delete + recreate any existing one each run so
  the definition stays current.
  - If `Create` throws, retry with **Type-Name-only** matching; if that also throws,
    log a clear `fail` and continue (no silent swallow).
- Per view (in the existing visibility transaction): `view.AddFilter(id)` +
  `view.SetFilterVisibility(id, false)`. View filters apply to linked elements when
  the link is shown "By Host View" (the default for newly created views), which is
  what covers the linked-ceiling case.
- Keep a **host-only per-instance `HideElements` fallback by name** so host hiding
  never regresses even if filter creation fails.

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

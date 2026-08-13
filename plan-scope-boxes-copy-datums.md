# Plan — Scope Boxes in Copy Datums

## 1. Current state

Copy Datums (`Source/Tools/CopyFromLink/CopyDatums*.cs`) is a bespoke, standalone tool — it does
**not** share `CopyCategoryGroups`/`AutoFiltersSettings.CaptureCategoryMap` with Copy Elements from
Link / Copy Linear (the tools fixed in PR #146). It hardcodes exactly two categories end to end:

- `CopyDatumsCommand.CollectDatumLinks` collects host + link `Grid`/`Level` elements via
  `OfClass(typeof(Grid))` / `OfClass(typeof(Level))`.
- `CopyDatumsViewModel` renders a `MultiSelectTabs` with a "Grids" tab and a "Levels" tab.
- `CopyDatumsRunHandler` copies the selected grids/levels, skip-and-logging any whose name already
  exists in the host (Revit enforces unique names for both).

Scope Boxes were never part of this tool at any layer — not a PR #146-style "wired but dead" bug,
just never added. The prior PR's fix (surfacing Scope Boxes in the generic category picker) does not
reach Copy Datums because Copy Datums never calls `CaptureCategoryMap`.

## 2. Change

Add Scope Boxes (`OST_VolumeOfInterest`) as a third copyable kind, following the two patterns already
proven in this codebase:

- Collection: `new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_VolumeOfInterest).WhereElementIsNotElementType()`
  — same call `ScopeBoxCreatorScanHandler.CollectScopeBoxes` and
  `CopyFromLinkRunHandler.SkipClashingScopeBoxes` already use. Scope boxes have no dedicated class
  (unlike `Grid`/`Level`), so they're read as plain `Element` and identified by category.
- Uniqueness: scope box names are unique per document in Revit (the Scope Box Creator already
  skip-and-logs a duplicate name), so a link scope box whose name is already used in the host is
  disabled in the picker and skipped at run time — exactly the existing grid/level rule.

### 2.1 `CopyDatumsModels.cs`
`CopyDatumLinkInfo` gains a third `List<CopyDatumItem> ScopeBoxes`.

### 2.2 `CopyDatumsCommand.cs`
`CollectDatumLinks` reads host scope box names and, per link, the link's scope boxes (ordered like
grids/levels), flagging `ExistsInHost`. The link-has-content filter becomes
`Grids || Levels || ScopeBoxes`.

### 2.3 `CopyDatumsViewModel.cs`
Third `_scopeBoxDisplayToId` map + `_selectedScopeBoxIds` set, mirroring grids/levels exactly:
- `RebuildDatums` adds a "Scope Boxes" tab when the link has any, with the same
  `existingSuffix`/`DisabledItems` treatment.
- A scope box display name colliding with a grid or level name (the MultiSelectTabs flat
  cross-tab selection-string constraint — same reason a level colliding with a grid name gets a
  "(Level)" suffix today) gets a "(Scope Box)" suffix.
- `IsValid`, `SummaryFor`, review values, and `Run` all extend to include the scope box count/ids.

### 2.4 `CopyDatumsRunHandler.cs`
New `ScopeBoxElemIds` input list. Host scope box names are read the same way the host grid/level
names already are; a selected scope box whose name clashes is skipped and logged
(`copy.datums.log.scopeBoxExists`), the same as `gridExists`/`levelExists`. Copy runs through the
same cross-document `ElementTransformUtils.CopyElements` batch-then-per-element-fallback shape as
grids/levels, after a `RunState.CancelRequested` check matching the one already between the grids
and levels stages.

### 2.5 Text
`Strings/en/copy.datums.json` gains the new tab label, collision-suffix, and log strings, and the
existing "no links / no datums" and review-count strings are widened from two categories to three.
`ribbon.json`'s Copy Datums tip and `overview.json`'s Copy Datums blurb are corrected to mention
scope boxes so they stay accurate.

## 3. Not in scope

- The Overview page's illustrative step-by-step mockup (`ToolsOverviewDemos.cs` /
  `overviewDemos.json`, key `"Copy Datums"`) is a decorative preview, not the live tool — left
  as is.
- Reference Planes: Copy Datums has never covered them and this request is scope-boxes-specific.

## 4. Needs a Windows/Revit run to confirm

- Scope boxes copy correctly cross-document via `ElementTransformUtils.CopyElements` with the link
  transform (same call already relied on for grids/levels and, per PR #146, for scope boxes in the
  other copy tools).
- Revit's actual behavior on a clashing scope box name — the skip-and-log guard makes this moot in
  practice but is untested on a real model (same caveat PR #146 shipped with).

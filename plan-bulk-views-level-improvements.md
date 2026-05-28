# Plan: Bulk Views by Level — Improvements

## Summary of changes

### 1. Remove default print-set creation
`GetOrCreateViewSheetSet` currently runs after every execution unconditionally.
- **`LinkViewsLevelRunHandler.cs`**: Add `CreatePrintSets { get; set; } = false` property; gate all three `GetOrCreateViewSheetSet` calls behind `if (CreatePrintSets)`.
- **`LinkViewsLevelViewModel.cs`**: Add `_createPrintSets = false` field; add a "Create print sets" toggle inside `PopulateS2()` alongside the existing view-type toggles; wire to `_runHandler.CreatePrintSets` in `Run()`.

> No change to the settings page (gear icon). The option lives per-run in S2.

---

### 2. Rename to "Bulk Views by Level"
- **`LinkViewsLevelViewModel.cs`**: `Title => "Bulk Views by Level"`.
- **`App.cs`**: Change ribbon button label from `"Link Views\nby Level"` to `"Bulk Views\nby Level"` and panel title from `"T03  Link Views"` to `"T03  Bulk Views"`.
- No file renames needed — internal class/file names unchanged.

---

### 3. Move "By Discipline" to Testing ribbon
**API verification**: `LinkViewsDisciplineRunHandler` hides other link instances using `view.HideElements(linkInstanceIds)`. `View.HideElements()` with `RevitLinkInstance` element IDs is a valid Revit API call that exists in Revit 2024 — link instances are full `Element` subclasses and are hidden at the instance level in the view. No deprecated or 2025-only API is used.

However, the overall approach (creating views that hide all-but-one link instance) can behave unexpectedly with nested links, pinned links, or links loaded via worksharing. Moving it to Testing is the right call regardless.

- **`App.cs`**: Remove `Btn("LT_LinkViewsDiscipline", ...)` from the Link Views panel `AddStackedItems` call. Add it as a stacked item in the Testing panel.
- The T03 panel will then contain only the large "Bulk Views by Level" button and "Replicate Dep. Views" as a large button (see §4).

---

### 4. Make "Replicate Dep. Views" a large button with an icon
- **`App.cs`**: Remove `"LT_ReplicateDependentViews"` from `AddStackedItems` and add it as a standalone `AddItem` call with a glyph icon (Segoe MDL2 `""` — Copy / Duplicate Pages).

---

### 5. Building naming improvements (S3)

#### 5a. Cluster letter only when multiple clusters
- **`LinkViewsLevelRunHandler.cs`** `RunViews`: The inner loop already has `clusters.Count`. Change `baseName` assignment:
  - If `clusters.Count == 1`: `baseName = $"L{lname}"` (no letter)
  - If `clusters.Count > 1`: `baseName = $"L{lname} - {BuildingLabel} {BldgLetter(bi)}"` where `BuildingLabel` is a new string property (default `"Bldg"`).

#### 5b. Building label input in S3
- **`LinkViewsLevelViewModel.cs`** `BuildS3Naming()`: Add a "Building label" text input row below the existing slot rows (visible only when relevant; default `"Bldg"`). Wire to `_runHandler.BuildingLabel` in `Run()`.

#### 5c. View type suffix is optional
Currently `typeLabel` is always appended as the final segment in `BuildViewName`. Change to:
- **`LinkViewsLevelRunHandler.cs`**: Add `AppendViewType { get; set; } = true` property. In `BuildViewName`, only append `typeLabel` when `AppendViewType == true`.
- **`LinkViewsLevelViewModel.cs`** `BuildS3Naming()`: Add "Append view type (3D / FP / RCP)" toggle (default on). Wire to `_runHandler.AppendViewType` in `Run()`. Update `UpdatePreview()` accordingly.

#### 5d. Fallback when everything is "None" and no view type
In `BuildViewName`, when `parts` would be empty after resolving all slots (all "None") and `AppendViewType == false`:
- Default to `[$"{baseName} - {typeLabel}"]` so views always have a meaningful name.
- Communicate this in the preview ("If no slots selected, name defaults to Level - View Type").

---

## Files changed

| File | Reason |
|------|--------|
| `Source/Tools/T03-LinkViews/LinkViewsLevelRunHandler.cs` | Print-set gate, `BuildingLabel`, `AppendViewType`, cluster-letter logic |
| `Source/Tools/T03-LinkViews/LinkViewsLevelViewModel.cs` | Title rename, print-set toggle in S2, building label + append-type in S3 |
| `Source/App.cs` | Panel/button rename, move Discipline to Testing, Replicate to large button |

---

## Not in scope this iteration
- Per-cluster individual custom names (requires Phase 2 clustering scan before S3 loads; deferred)
- Merging `LinkViewsDisciplineCommand` execution path fixes (moved to Testing as-is)

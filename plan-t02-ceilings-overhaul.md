# Plan: T02 Ceilings Overhaul

**Base branch:** `main`
**Work branch:** `t02-ceilings-overhaul` (to be created from `main`)

---

## 1. Ceiling Heatmap — Color Ramp Step & Settings Cleanup

### 1a. New "Color Ramp" wizard step

Inserted before the existing run/options step:

- **Save/Load row** — dropdown listing saved ramp names via `LemoineTemplateStore<CeilingColorRamp>`. Save button captures current three colours with a user-provided name. Same pattern as filter templates in T01.
- **3 colour chips** — horizontal row of three clickable coloured squares (Low / Mid / High). Each opens the Lemoine colour picker. Labels below each chip.
- **Live gradient preview** — narrow `LinearGradientBrush` rectangle immediately below the chips. Updates live as any chip changes.

`CeilingColorRamp` — new serialisable record: `Low`, `Mid`, `High` (three `Color` values).

**Files:**
- `Source/Tools/T02-Ceilings/CeilingColorRamp.cs` — new record
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` — add step, colour-ramp state, save/load
- New WPF UserControl: `Source/Views/T02-Ceilings/ColorRampStepView.xaml` + `.cs`

### 1b. Run Options step additions

Add to the existing run/review step:

- `elevTolerance` — number input (in inches, default 1/8"). Moves from global settings into the wizard.
- `includeLinks` toggle — "Include linked ceilings"
- `placeTags` toggle — "Place ceiling tags"

All three initialised from `CeilingHeatmapSettings` and saved back on run.

**Files:**
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs`
- Run-step view XAML

### 1c. Remove T02 from GlobalSettings

Remove the T02 ceiling group (G1 Color Ramp, G2 Detection, G3 Diagnostics) from:
- `Source/Lemoine/T02-Ceilings/GlobalSettingsWindow.CeilingHeatmap.cs` — remove or delete
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` — remove `GetSettingsSpec()` / `ILemoineToolSettings`

---

## 2. New Tool — Make Ceiling Grids

### Purpose

Create Reflected Ceiling Plan views showing only ceilings (filtered by type/family), then export those views as DWG files.

### Architecture

Two-phase execution (modelled on `LinkViewsLevelViewModel` T03).

**Command:** `Source/Commands/T02-Ceilings/MakeCeilingGridsCommand.cs`

**ViewModel:** `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs` (implements `ILemoineTool`)

**Handlers:**
- `MakeCeilingGridsPhase1Handler` — scans host + linked docs for levels and ceiling types
- `MakeCeilingGridsRunHandler` — creates RCP views, applies visibility, exports DWG

**Settings:** `MakeCeilingGridsSettings.cs` — XML singleton at `%AppData%\LemoineTools\MakeCeilingGridsSettings.xml`

### Wizard Steps

| # | Step ID | Label | Content |
|---|---------|-------|---------|
| 1 | `documents` | Select Documents | Host + linked doc multi-select (`DocEntry` pattern from T03). Phase 1 scan runs after selection. |
| 2 | `filter` | Filter Ceiling Types | Table of all discovered ceiling types — columns: Source (host/link name), Family Name, Type Name. Two filter text boxes (one per column). Checkbox per row to include/exclude. |
| 3 | `naming` | View & File Naming | `LemoineTokenInput` with ceiling-plan tokens: `{Level}`, `{ProjectNumber}`, `{ProjectName}`, `{Year}`, `{Month}`, `{Day}`. Live preview of resolved name. View naming pattern and export filename pattern (same or separate). |
| 4 | `export` | Export Location | Folder picker (WinForms `FolderBrowserDialog` + editable TextBox, same as Batch Export). Toggle: "Place all files in one folder" vs "Organise into subfolders by level" (mirrors Batch Export's `SplitByFormat` checkbox pattern). |
| 5 | `run` | Review & Run | Counts summary + Run button |

### Naming Scheme (for round-trip with Project/Reproject)

Views and DWG files are named using the resolved pattern. The default pattern is `{Level}_CeilingGrid`. This deterministic, level-keyed name allows `Project Ceiling Grids` to match each DWG to its corresponding ceiling plan view by level name automatically.

### Phase 1 Scan Logic

- Collect all `Ceiling` elements from host document.
- For each selected linked document: collect via `RevitLinkInstance`.
- Deduplicate type rows by (FamilyName, TypeName, SourceDocName).
- Report back to ViewModel via dispatcher.

### Phase 2 Run Logic

1. For each level: find or create an RCP `ViewPlan`.
2. Hide all categories except `OST_Ceilings` (and `OST_CeilingTags` if desired).
3. Apply `ParameterFilterElement` to exclude unchecked ceiling types by Type Name / Family Name.
4. Name the view using the resolved naming pattern.
5. Export to DWG using `DWGExportOptions` with `FileVersion = DWGVersion.DWG2018` as default. If the project has `ExportDWGSettings` defined, offer a dropdown to select a setup (same as Batch Export); otherwise fall back to the default options object.
6. Place files in single folder or level-keyed subfolders per the export toggle.

**Files created:**
- `Source/Commands/T02-Ceilings/MakeCeilingGridsCommand.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsSettings.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsPhase1Handler.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsRunHandler.cs`
- Step views (programmatic, no XAML, matching Batch Export pattern)

---

## 3. Project Ceiling Grids — Batch Mode

The existing `ProjectedCeilingGridsViewModel` currently has two steps: DWG source file + Review & Run.

### Changes

**Add a mode toggle at the top of Step 1:** "Single file" vs "Batch from folder".

- **Single file mode** (existing): user picks one DWG. Projects onto active ceiling plan. Behaviour unchanged.
- **Batch mode** (new): user picks a folder. The tool scans for DWG files whose names match the level-keyed naming pattern from Make Ceiling Grids (`{Level}_CeilingGrid.dwg` or the stored pattern). For each matched DWG, it finds the corresponding ceiling plan view in the project (by resolving the level name from the filename), opens or activates that view, and projects the DWG curves onto the ceilings in that view.

**Step content changes:**
- Step 1: mode toggle + file/folder picker (conditional on mode)
- Step 2: matched pairs table — "DWG filename → View name" — for user review before running
- Step 3: Review & Run

**Files changed:**
- `Source/Tools/T02-Ceilings/ProjectedCeilingGridsViewModel.cs` — add batch mode, folder scan, match logic
- `Source/Tools/T02-Ceilings/CeilingGridEventHandler.cs` — extend to loop over matched pairs in batch mode

---

## 4. Reproject Ceiling Grids — Picked Views + Batch

The existing `ReprojectCeilingGridsViewModel` works off the currently active view. Replace this with a picked-view selection.

### Changes

**Step 1 — Select Ceiling Plans** (replaces "active view" assumption):
- `LemoineMultiSelectTabs` listing all ceiling plan views in the project, grouped by level prefix or by view family.
- Single-select or multi-select (enabling batch).
- Selecting multiple views enables batch reprojection.

**Step 2 — Elevation Tolerance** (unchanged): number input for `elevTolerance`.

**Step 3 — Review & Run**: shows list of selected views + summary.

**Batch execution:** `CeilingGridEventHandler` loops over each selected view, performing the reproject logic per view sequentially.

**Files changed:**
- `Source/Tools/T02-Ceilings/ReprojectCeilingGridsViewModel.cs` — replace active-view logic with multi-select view picker
- `Source/Tools/T02-Ceilings/CeilingGridEventHandler.cs` — extend to iterate selected views
- `Source/Commands/T02-Ceilings/ReprojectCeilingGridsCommand.cs` — collect all ceiling plan views before opening window

---

## 5. Ribbon — Ceiling Grids Dropdown Button

Replace the current stacked pair ("Project Grids" / "Reproject Grids") with a single large `PulldownButtonData` labelled **"Ceiling Grids"** containing three items:

| Order | Label | Command | Glyph |
|-------|-------|---------|-------|
| 1 | Make Ceiling Grids | `MakeCeilingGridsCommand` | `` (Grid / table icon, Segoe MDL2) |
| 2 | Project Grids | `ProjectedCeilingGridsCommand` | `` (Download / import arrow) |
| 3 | Reproject Grids | `ReprojectCeilingGridsCommand` | `` (Refresh / sync arrows) |

Pulldown button large glyph: `` (or nearest sensible ceiling/grid icon).

**Files changed:**
- `Source/App.cs` — replace two stacked `PushButtonData` with `PulldownButtonData` block

---

## Files Summary

| Action | File |
|--------|------|
| New | `Source/Tools/T02-Ceilings/CeilingColorRamp.cs` |
| New | `Source/Commands/T02-Ceilings/MakeCeilingGridsCommand.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsSettings.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsPhase1Handler.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsRunHandler.cs` |
| Modify | `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` |
| Modify | `Source/Lemoine/T02-Ceilings/GlobalSettingsWindow.CeilingHeatmap.cs` |
| Modify | `Source/Tools/T02-Ceilings/ProjectedCeilingGridsViewModel.cs` |
| Modify | `Source/Tools/T02-Ceilings/ReprojectCeilingGridsViewModel.cs` |
| Modify | `Source/Tools/T02-Ceilings/CeilingGridEventHandler.cs` |
| Modify | `Source/Commands/T02-Ceilings/ReprojectCeilingGridsCommand.cs` |
| Modify | `Source/App.cs` |
| Delete (or empty) | `Source/Lemoine/T02-Ceilings/GlobalSettingsWindow.CeilingHeatmap.cs` |

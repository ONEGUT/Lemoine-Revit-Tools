# Plan: T02 Ceilings Overhaul

## Overview

Three areas of work:

1. **Ceiling Heatmap — Color Ramp Step & Settings Cleanup**
2. **New Tool — Make Ceiling Grids**
3. **Ribbon — Grids Dropdown Button**

---

## 1. Ceiling Heatmap — Color Ramp Step & Settings Cleanup

### 1a. New "Color Ramp" step in CeilingHeatmapViewModel

Add a step (inserted before the existing run/options step) with:

- **Save/Load row** — a labelled dropdown (mirroring the filter template save/load UI from T01) using `LemoineTemplateStore<CeilingColorRamp>`. A `CeilingColorRamp` record holds the three `System.Windows.Media.Color` values (Low, Mid, High). The dropdown shows saved ramp names; a Save button captures the current ramp with a name.
- **3 colour squares** — horizontal row of three clickable square colour chips (Low / Mid / High), each opening the existing Lemoine colour picker. The chips render with their current colour fill and a thin border.
- **Live gradient preview** — a narrow `LinearGradientBrush` rectangle immediately below the chips, with three gradient stops bound to the three colours; updates live as any chip changes.

**Files changed:**
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` — add step, colour-ramp state, save/load logic
- `Source/Tools/T02-Ceilings/` — new `CeilingColorRamp.cs` (simple serialisable record: Low, Mid, High)
- New WPF UserControl: `Source/Views/T02-Ceilings/ColorRampStepView.xaml` + `.cs`

### 1b. Move "Include Linked Ceilings" and "Place Ceiling Tags" to Run Options step

These two settings currently live in the global settings page (G2 Detection group in `CeilingHeatmapSettings`). Move them into the run-options/review step of the tool wizard so they are visible without opening Settings.

- Add `IncludeLinks` and `PlaceTags` boolean properties to `CeilingHeatmapViewModel` (initialised from `CeilingHeatmapSettings`, saved back on run).
- Render two toggles in the existing run/review step view.

**Files changed:**
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs`
- Run-step view XAML

### 1c. Remove ceiling settings page from GlobalSettings

Remove the T02 ceiling group (G1 Color Ramp, G2 Detection, G3 Diagnostics) from `GlobalSettingsWindow.CeilingHeatmap.cs` and from `CeilingHeatmapViewModel.GetSettingsSpec()`.

The colour ramp is now managed in-tool (step 1a). `includeLinks` and `placeTags` are in the run step (1b). `elevTolerance` stays (or moves to the tolerance step inside the tool — TBD; default in code is sufficient if the tolerance step already exists in the wizard).

**Files changed:**
- `Source/Lemoine/T02-Ceilings/GlobalSettingsWindow.CeilingHeatmap.cs` — remove or empty
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` — remove `GetSettingsSpec()` / `ILemoineToolSettings`

---

## 2. New Tool — Make Ceiling Grids

### Purpose

Create Reflected Ceiling Plan views showing only ceilings, filtered by ceiling type/family, then export those views as DWG files.

### Architecture

Modelled after `LinkViewsLevelViewModel` (T03) — two-phase execution via ExternalEventHandlers.

**Command:** `Source/Commands/T02-Ceilings/MakeCeilingGridsCommand.cs`

**ViewModel:** `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs` (implements `ILemoineTool`)

**Handlers:**
- `MakeCeilingGridsPhase1Handler` — scans documents for available levels and ceiling types
- `MakeCeilingGridsRunHandler` — creates RCP views, applies ceiling-only visibility, exports DWG

### Steps (wizard)

| # | Step ID | Label | Content |
|---|---------|-------|---------|
| 1 | `documents` | Select Documents | Multi-select list of host + linked documents (same `DocEntry` pattern as LinkViewsLevel) |
| 2 | `filter` | Filter Ceiling Types | Table of discovered ceiling types (Type Name + Family Name). Two search/filter text boxes (one per column). Checkboxes to include/exclude. Linked and native ceilings shown with a source indicator. |
| 3 | `export` | Export Location | `LemoineFileBrowser` for folder path. Option to name views by level. |
| 4 | `run` | Review & Run | Summary of counts + Run button |

### Phase 1 Scan Logic

- Collect all `Ceiling` elements from host document filtered by view (or all ceilings if no active ceiling plan).
- For each selected linked document: collect ceilings via `RevitLinkInstance`.
- Deduplicate type rows by (FamilyName, TypeName, Source).
- Report back to ViewModel via dispatcher.

### Phase 2 Run Logic

1. For each level in the host: find or create an RCP view (`ViewPlan` with `ViewType.CeilingPlan`).
2. Apply a `VisiblityGraphicsOverride` or `ParameterFilterElement` to hide everything except ceilings (hide all categories except `OST_Ceilings` and `OST_CeilingTags` if tags requested).
3. Filter out excluded ceiling types using `ParameterFilterElement` on Type Name / Family Name.
4. Export each view to DWG using Revit's `DWGExportOptions` + `Document.Export()`.

**Files created:**
- `Source/Commands/T02-Ceilings/MakeCeilingGridsCommand.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsPhase1Handler.cs`
- `Source/Tools/T02-Ceilings/MakeCeilingGridsRunHandler.cs`
- `Source/Views/T02-Ceilings/MakeCeilingGrids/` — step XAML views (Documents, Filter, Export, Run)

---

## 3. Ribbon — Ceiling Grids Dropdown Button

Replace the current stacked pair ("Project Grids" / "Reproject Grids") with a single large `PulldownButton` that contains three commands:

| Button | Command |
|--------|---------|
| Make Ceiling Grids (new) | `MakeCeilingGridsCommand` |
| Project Ceiling Grids | `ProjectedCeilingGridsCommand` |
| Reproject Ceiling Grids | `ReprojectCeilingGridsCommand` |

The pulldown button label: **"Ceiling Grids"**. Large button with a single representative glyph (TBD — grid/ceiling icon from Segoe MDL2).

**Files changed:**
- `Source/App.cs` — replace two stacked `PushButtonData` entries with a `PulldownButtonData` block containing three items

---

## Files Summary

| Action | File |
|--------|------|
| New | `Source/Tools/T02-Ceilings/CeilingColorRamp.cs` |
| New | `Source/Views/T02-Ceilings/ColorRampStepView.xaml` + `.cs` |
| New | `Source/Commands/T02-Ceilings/MakeCeilingGridsCommand.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsViewModel.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsPhase1Handler.cs` |
| New | `Source/Tools/T02-Ceilings/MakeCeilingGridsRunHandler.cs` |
| New | `Source/Views/T02-Ceilings/MakeCeilingGrids/` (step views) |
| Modify | `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` |
| Modify | `Source/Lemoine/T02-Ceilings/GlobalSettingsWindow.CeilingHeatmap.cs` |
| Modify | `Source/App.cs` |

---

## Open Questions

1. Should `elevTolerance` remain accessible somewhere (inline in the tool wizard, or removed entirely with a hard-coded default)?
2. For the RCP views created by Make Ceiling Grids: overwrite existing views with the same name, or skip/version them?
3. DWG export — any specific DWG version or export settings preset required?
4. Glyph choice for the new "Ceiling Grids" pulldown button?

# Plan — Bulk Export Overhaul

## Root cause
Bulk Export was built **sheet-first**; Views were bolted on and left half-wired.
Every reported symptom comes from that:

| Symptom | Cause |
|---|---|
| DWG/NWC/IFC files all named `-` for views | `BuildTokens` only reads `SHEET_NUMBER`/`SHEET_NAME`; a View has neither, so the pattern collapses to `-`. The degenerate name is **never reported** — silent. |
| Selected views absent from "Build Packs" | `BuildS2` blocks Views mode; `BuildPackEditor` filters through `_sheetById` (sheets only). |
| PDF settings always present; other formats' settings buried in the Formats step | Asymmetric step layout — only PDF has its own step (S4, shown even when PDF off). |

Plus latent issues found in the pass:
- **Handler leak** — `BuildNwcOptions` adds `ValidationChanged += UpdateNote` on every S3 rebuild, never removed (flagged `// ⚠` in source).
- **Multiselect contract** — `BuildMultiSelect` subscribes `SelectionChanged` *after* `SetGroups` (contract says before).
- **DWG setup mismatch** — combo shows setup `[0]` while `_dwgSetup` stays `""` until touched.
- **Preview always `.pdf`** even when only DWG/NWC selected.

## Decisions (approved)
1. **True conditional steps** — extend the shared `StepFlowWindow` so a step can be genuinely hidden when its format is off.
2. **Packs support Views** — Build Packs organises/orders selected views as well as sheets.
3. **View naming** — fall back to the view's name, and **never silently** emit a junk filename: report to the run log *and* `diagnostics.log` the moment a pattern resolves to nothing usable.

## Changes

### 1. Framework — `StepFlowWindow` (additive, gated)
- New optional interface `ILemoineConditionalSteps { bool IsStepVisible(string stepId); }` in `ILemoineTool.cs`.
- `RefreshStepVisibility()` collapses/show step rows + pips per `IsStepVisible`; called on `ActivateStep` and on `ValidationChanged`.
- Navigation (`ConfirmStep`, Back, step counter, circle numbers) skips hidden steps via `NextVisible`/`PrevVisible`.
- Tools that don't implement the interface are unaffected (predicate returns `true`, formulas reduce to current behaviour). Conditional steps are never the last step, so the run/log/review machinery is untouched.

### 2. `BulkExportViewModel` — step restructure
New step list:
`S1 Select · S2 Build Packs · S3 Filename & Formats · S4 PDF · S5 DWG · S6 NWC · S7 IFC · S8 Output · S9 Review`
- S3 keeps **only** the filename pattern + format toggles. DWG/NWC/IFC option builders move to their own steps (S5/S6/S7), matching PDF (S4).
- `IsStepVisible`: S4=`_pdfOn`, S5=`_dwgOn`, S6=`_nwcOn`, S7=`_ifcOn`, else true.
- Packs (S2) work in both modes; `HasActivePacks` drops the Sheets-only gate; `availableForPack` keyed by sheet number (sheets) or view name (views).
- Preview shows the correct extension for the active formats; mode-aware default pattern.
- Fix multiselect subscribe-before-`SetGroups`; remove the `ValidationChanged += UpdateNote` leak; initialise `_dwgSetup` to the shown setup.

### 3. `BulkExportEventHandler` — naming + non-silent failure
- `BuildTokens`: for a View, populate name from `element.Name` (and a new `{ViewName}` token); `{SheetName}` falls back to the view name.
- New `ResolveFileName(...)`: if the resolved pattern has no alphanumeric character, push a `warn` to the run log **and** `LemoineLog.Warn(...)` to diagnostics.log, then use a deterministic fallback (view/sheet name, else element id).
- `ExportPackMode` generalised to sheets *or* views (combined PDF + ordered DWG); titleblock pre-flight stays sheets-only. Dispatch in `Execute` drops the `ExportMode == "Sheets"` pack gate.

### 4. `SheetPackLayout` / `SheetPackLayoutEditor`
- Editor treats entries as generic key→display; when key == display (views) it renders the name without the sheet-number chip. Saved-pack XML schema unchanged (still `SheetNumbers`, reused as the key list).

### 5. `BulkExportSettings` / new token
- Add `{ViewName}` to `ExportTokens`; default pattern stays `{SheetNumber}-{SheetName}` for sheets, `{ViewName}` for views.

## Files touched
- `Source/Lemoine/ILemoineTool.cs` (new interface)
- `Source/Lemoine/StepFlowWindow.xaml.cs` (conditional-step support)
- `Source/Tools/BulkExport/BulkExportViewModel.cs`
- `Source/Tools/BulkExport/BulkExportEventHandler.cs`
- `Source/Tools/BulkExport/SheetPackLayoutEditor.cs`

## Out of scope / notes
- Cannot build on Linux (net48 + WPF) — verification is by inspection per CLAUDE.md.
- NWC/IFC remain 3D-views-only and per-view (no combine); packs only affect PDF/DWG.

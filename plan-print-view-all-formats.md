# Plan — Print View supports all Bulk Export formats

## Goal
Make **Print View** (single active view/sheet) export to every format **Bulk Export**
supports: **PDF, DWG, NWC, IFC** — not just PDF.

## Constraints (inherited from Bulk Export behaviour)
- **PDF / DWG** — valid for any view or sheet.
- **NWC / IFC** — 3D views only. NWC additionally needs the Navisworks exporter loaded
  (`OptionalFunctionalityUtils.IsNavisworksExporterAvailable()`).
- DWG needs a named **export setup** that already exists in the project.

## UX approach
Mirror Bulk Export's pattern (multi-format toggles + per-format conditional settings
steps), scaled to a single view. Only formats **valid for the active view** are offered:
PDF/DWG always; NWC/IFC only when the active view is a `View3D`. This keeps the UI
unambiguous (no greyed-out impossible options).

New Print View step flow (`ILemoineConditionalSteps`):
1. **S1 Formats** — toggles (PDF, DWG, + NWC, IFC when active view is 3D). At least one required.
2. **S2 PDF Settings** — shown only when PDF on (page setup, quality, advanced).
3. **S3 DWG Settings** — shown only when DWG on (export setup dropdown).
4. **S4 NWC Settings** — shown only when NWC on (coordinates, mesh, content toggles).
5. **S5 IFC Settings** — shown only when IFC on (IFC version).
6. **S6 Output** — output folder (required, always visible).
7. **S7 Review & Run** — always last.

## Files changed / added

### New — `Source/Tools/BulkExport/ExportOptionsFactory.cs`
Shared, Revit-API option builders so Print View and Bulk Export use one source of truth:
- `BuildPdfOptions(...)`, `BuildDwgOptions(doc, setupName)`,
  `BuildNwcOptions(NwcOptionSet, viewId, pushLog)`, `BuildIfcOptions(version, viewId)`
- `MapColorDepth`, `MapRasterQuality` enum mappers
- `NwcOptionSet` DTO (14 NWC values) + `SanitizeFilename`

### Changed — `Source/Commands/BulkExport/PrintViewCommand.cs`
- Collect DWG export-setup names (like `BulkExportCommand`).
- Determine `isThreeD` (active view is `View3D`).
- Pass both into `PrintViewViewModel`.

### Changed — `Source/Tools/BulkExport/PrintViewViewModel.cs`
- Implement `ILemoineConditionalSteps`; new 7-step flow above.
- Add format toggles + DWG/NWC/IFC settings UI (mirrors Bulk Export S5/S6/S7).
- Validation: ≥1 format selected; output folder set.
- Persist all chosen settings to `BulkExportSettings`; push all flags/options to handler.

### Changed — `Source/Tools/BulkExport/PrintViewEventHandler.cs`
- Add input properties for every format flag + DWG setup + NWC options + IFC version.
- Export the single element to each enabled format via `ExportOptionsFactory`.
- Skip-and-log NWC/IFC when not a `View3D`; skip-and-log NWC when exporter unavailable.
- Per-format pass/fail logged; degenerate/empty results reported, never silent.

### Changed — `Source/Tools/BulkExport/BulkExportEventHandler.cs`
- Replace its private `BuildPdfOptions`/`BuildDwgOptions`/mappers and the inline
  NWC/IFC option construction with calls to `ExportOptionsFactory` (option construction
  only — the export/transaction/progress logic stays put). Mechanical, no behaviour change.

## Notes
- Output stays flat (single view → at most one file per format, distinct extensions —
  no collisions, no per-format subfolders needed).
- No new settings DTO — reuse `BulkExportSettings` (already shared).
- Windows-only build; cannot compile here — code reviewed against the silent-failure
  and Revit-API rules in CLAUDE.md before commit.

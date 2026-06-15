# Plan: Ribbon "Print View" Button

## Goal
Add a ribbon button to the **T03 Bulk Views** panel that prints the active view to PDF — same logic as Bulk Export, but pre-targets the active view so no view-selection step is needed.

## Files to Create

### `Source/Commands/BulkExport/PrintViewCommand.cs`
- `IExternalCommand` that reads `commandData.Application.ActiveUIDocument.ActiveView`
- Creates `PrintViewViewModel` with `App.PrintViewHandler` / `App.PrintViewEvent`
- Launches `StepFlowWindow` on a new background STA thread (same pattern as `BulkExportCommand`)
- Caches the window in a static field to prevent duplicates

### `Source/Tools/BulkExport/PrintViewViewModel.cs`
- Implements `ILemoineTool, ILemoineReviewable, ILemoineToolCleanup`
- Three steps (no view picker — view is fixed at construction):
  - **S1: PDF Settings** — color depth, raster quality, hidden line mode (same controls as BulkExport S4)
  - **S2: Output** — output folder picker
  - **S3: Review & Run**
- On `Run()`: populates `App.PrintViewHandler` properties and raises `App.PrintViewEvent`

### `Source/Tools/BulkExport/PrintViewEventHandler.cs`
- Dedicated `IExternalEventHandler` (avoids data collisions with the shared `BulkExportHandler`)
- Inputs: `ViewId`, `OutputFolder`, PDF options, callbacks
- Reuses the same `PDFExportOptions` construction pattern from `BulkExportEventHandler`
- Exports the single view to PDF, logs pass/fail, calls `OnComplete`

## Files to Modify

### `Source/App.cs`
- In `OnStartup`: create and register `PrintViewHandler` and `PrintViewEvent`
- In ribbon setup: add `LT_PrintView` button to **T03 Bulk Views** panel after `LT_BulkExport`
  - Icon: Segoe MDL2 `` (Print)
  - Label: `"Print\nView"`
  - Tooltip: "Export the active view to PDF using the same settings as Bulk Export."

## Branch
Already on `claude/ribbon-print-view-button-uxpt3e` (pre-created).

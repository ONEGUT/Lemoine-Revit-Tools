# Plan (PREP ONLY — do not execute yet) — Batch Export → "Bulk Export", move to Bulk Views

> Hold until the user has merged their other branch into main. This file is the
> staged plan; nothing here is applied yet.

## Goal
Rename the **Batch Export** tool to **Bulk Export** and move its ribbon button
out of the experimental **Testing** panel into the **T03 Bulk Views** panel,
alongside "Bulk Views by Level" and "Replicate Dep. Views".

## Current footprint
- Ribbon: `Source/App.cs:355` — `LT_BatchExport` button in the `testingPanel`
  stacked group (BatchExport, CreateSheets, By Discipline).
- T03 panel: `Source/App.cs:274` (`"T03  Bulk Views"`), two large buttons today.
- Wiring: `App.cs:64-65` (handler/event props), `App.cs:146-147` (init).
- Tool code: `Source/Tools/Testing/BatchExport/` (ViewModel, EventHandler, Settings),
  command `Source/Commands/Testing/BatchExportCommand.cs`.
- Display strings: `BatchExportViewModel.Title => "Batch Export"`.
- Settings tab: only present in **LemoinePreview** (`PreviewMainWindow.cs` tab
  `"tx"` + `BuildBatchExportProxy`). No "tx" tab in the live `GlobalSettingsWindow`.
- Settings persistence: `BatchExportSettings.xml` (XmlRoot `BatchExportSettings`),
  holds users' saved export packs.

## Ribbon changes (App.cs)
1. Remove `LT_BatchExport` from the `testingPanel.AddStackedItems(...)` call.
   Testing panel then holds CreateSheets + By Discipline (still a valid 2-item stack).
2. Add a large button to `linkViewsPanel` (T03 Bulk Views), e.g.:
   ```
   linkViewsPanel.AddItem(Btn(
       "LT_BatchExport", "Bulk\nExport", "BatchExportCommand",
       "Export sheets and views to PDF or DWG in bulk with token-based filenames.",
       char.ConvertFromUtf32(0xEDE1)));  // Segoe MDL2: Share/Export
   ```
   (New glyph uses `char.ConvertFromUtf32` per CLAUDE.md — no Python pass needed.)

## Display rename to "Bulk Export"
- `BatchExportViewModel.Title => "Bulk Export"` (window/header title).
- Ribbon button label `"Bulk\nExport"` (above).
- LemoinePreview settings tab label + proxy `Label` → "Bulk Export".

## Scope decision — code-level rename depth (DECIDED: Full graduation)
Per the NEW TOOL POLICY, the tool graduates out of `Testing/` into its own named
folder with a descriptive namespace, and all `BatchExport` identifiers become
`BulkExport`. A one-time settings migration preserves users' saved export packs.

### File moves + renames
- `Source/Tools/Testing/BatchExport/BatchExportViewModel.cs`
  → `Source/Tools/BulkExport/BulkExportViewModel.cs`
- `Source/Tools/Testing/BatchExport/BatchExportEventHandler.cs`
  → `Source/Tools/BulkExport/BulkExportEventHandler.cs`
- `Source/Tools/Testing/BatchExport/BatchExportSettings.cs`
  → `Source/Tools/BulkExport/BulkExportSettings.cs`
- `Source/Commands/Testing/BatchExportCommand.cs`
  → `Source/Commands/BulkExport/BulkExportCommand.cs`

### Identifier renames (all references)
- Classes: `BatchExportViewModel`→`BulkExportViewModel`,
  `BatchExportEventHandler`→`BulkExportEventHandler`,
  `BatchExportSettings`→`BulkExportSettings`,
  `BatchExportCommand`→`BulkExportCommand`.
- Namespace: tool files `LemoineTools.Tools.Testing` → `LemoineTools.Tools.BulkExport`
  (command stays `LemoineTools.Commands`, matching the other commands).
- `BatchExportViewModel.Title => "Bulk Export"`; `EventHandler.GetName() => "BulkExport"`.
- App.cs: `BatchExportHandler`/`BatchExportEvent` → `BulkExportHandler`/`BulkExportEvent`
  (props at `App.cs:64-65`, init at `App.cs:146-147`), `using` for the new namespace.

### Settings persistence + migration
- `[XmlRoot("BatchExportSettings")]` → `[XmlRoot("BulkExportSettings")]`;
  file `BatchExportSettings.xml` → `BulkExportSettings.xml`.
- **One-time migration in `Load()`**: if the new file is absent but the legacy
  `BatchExportSettings.xml` exists, deserialize it via an `XmlSerializer` built with
  an explicit `XmlRootAttribute("BatchExportSettings")` override, then `Save()` to the
  new path (and optionally leave the old file in place). This carries saved packs over.
  Route any failure through `LemoineLog.Swallowed` (no silent empty catch).

### LemoinePreview
- `PreviewMainWindow.cs`: tab `"tx"` label → "Bulk Export"; `BuildBatchExportProxy`
  → `BuildBulkExportProxy`; `BatchExportSettings` → `BulkExportSettings`; proxy `Label`.
- `LemoinePreview.csproj`: update the explicit `<Compile Include>` path to
  `..\Source\Tools\BulkExport\BulkExportSettings.cs`.

### Comments referencing the old name (cosmetic)
- `LemoineTokenInput.cs` and `LemoineFolderBrowser.xaml.cs` mention "Batch Export"/
  "BatchExport" in doc comments — update to "Bulk Export" for accuracy.
- `LemoineTools.csproj` Testing folder-list comment — drop `BatchExport`.

## Post-change (when executed)
- Run the CLAUDE.md silent-failure scan on the diff.
- Commit + push to the working branch.

## Notes
- `LemoineTools.csproj` globs sources, so option A needs no `<Compile>` edits;
  options B/C require updating `LemoinePreview.csproj`'s explicit
  `BatchExportSettings.cs` include path.

# Plan — PDF Vector / Raster Control for Bulk Export & Print View

## Problem

Bulk Export already presents a **Hidden Line Views: Vector Processing / Raster
Processing** dropdown, but the choice is **silently discarded** — it is collected,
persisted and reviewed, then never reaches the Revit API. Print View has no such
control at all. Neither tool exposes the PDF's raster DPI.

### Evidence

| Where | What happens today |
|---|---|
| `BulkExportViewModel.cs:993` | Combo box built, writes `_hiddenLines` |
| `BulkExportViewModel.cs:1253` | Persisted to `BulkExportSettings.HiddenLinesVector` |
| `BulkExportViewModel.cs:1211` | Shown in the S4 accordion summary |
| `BulkExportViewModel.cs:1309` | Assigned to `_handler.HiddenLines` |
| `BulkExportEventHandler.cs:29` | Property declared — **and never read anywhere** |
| `ExportOptionsFactory.cs:19` | `BuildPdfOptions` has no such parameter |

So every PDF exports with Revit's default processing regardless of the selection.

## Revit API (verified against `libs/RevitAPI.dll` metadata, Revit 2024)

`Autodesk.Revit.DB.PDFExportOptions` carries:

| Property | Type | Used today |
|---|---|---|
| `AlwaysUseRaster` | `bool` | ❌ never set |
| `ExportQuality` | `PDFExportQualityType` (`DPI72`…`DPI4000`) | ❌ never set |
| `RasterQuality` | `RasterQualityType` | ✅ both tools |
| `ColorDepth` | `ColorDepthType` | ✅ both tools |

`PDFExportOptions` has **no** `HiddenLineViews` property — that lives on
`PrintParameters` (`HiddenLineViewsType.VectorProcessing / RasterProcessing`),
which is the legacy `PrintManager` path and is not used by either tool.
`AlwaysUseRaster` is therefore the correct handle:

- `false` → vector processing; Revit rasterizes only views that require it
  (shaded / realistic / transparency).
- `true` → force raster for every view.

`PDFExportQualityType` values are `DPI72`, `DPI144`, `DPI300`, `DPI600`,
`DPI1200`, `DPI2400`, `DPI3600`, `DPI4000`.

**Scope note:** this is PDF-only. DWG is vector by nature, and NWC/IFC are 3D
model formats with no raster/vector concept — no changes there.

## Changes

### 1. `Source/Tools/Export/ExportOptionsFactory.cs`

Add two parameters to `BuildPdfOptions` and set the two properties:

```csharp
opts.AlwaysUseRaster = alwaysUseRaster;
opts.ExportQuality   = MapExportQuality(exportQualityDpi);
```

New mapper `MapExportQuality(string)` — `"72 DPI"` … `"4000 DPI"` →
`PDFExportQualityType`, defaulting to `DPI300`. Because the factory is shared,
this single edit serves both tools.

### 2. `Source/Tools/Export/BulkExportSettings.cs`

- Keep `HiddenLinesVector` (bool) as-is — already persisted, already migrated.
- Add `public string PdfExportQualityDpi { get; set; } = "300 DPI";`

Both are machine-wide preferences naming nothing inside a model, so they belong
in the existing `%AppData%` settings file (no `DocScoped` bucket needed).

### 3. `Source/Tools/Export/BulkExportEventHandler.cs`

- Add `public string ExportQualityDpi { get; set; } = "300 DPI";`
- `BuildPdfOptions` (line 629) passes `HiddenLines == "Raster Processing"` and
  `ExportQualityDpi` through to the factory — this is the line that fixes the
  dead control.

### 4. `Source/Tools/Export/PrintViewEventHandler.cs`

- Add `HiddenLines` and `ExportQualityDpi` properties.
- Pass both into the `BuildPdfOptions` call at line 120.

### 5. `Source/Tools/Export/PrintViewViewModel.cs`

- New fields `_hiddenLines`, `_exportQualityDpi` seeded from `BulkExportSettings`.
- Add the two combo boxes to the OUTPUT QUALITY section (line 276) via the
  existing `AddLabeledComboBox` helper, plus a hint line under Hidden Line Views.
- Include both in the S2 summary (line 494) and the `quality` review value
  (line 466).
- Persist both in `Run()` (line 526) and assign both to the handler (line 559).

### 6. `Source/Tools/Export/BulkExportViewModel.cs`

- New field `_exportQualityDpi`; add its combo box to OUTPUT QUALITY (line 993).
- Move **Hidden Line Views above Raster Quality** so the mode sits above the two
  settings that only apply when rasterizing; add the same hint line.
- Add DPI to the S4 summary (line 1211) and the `quality` review value.
- Persist in `ApplySettings`/`Run` and add an `exportquality` `SettingDef` to the
  `G4 — PDF Options` group in `GetSettingsSpec` (line 1374).

### 7. `Strings/en/export.printView.json` / `Strings/en/export.bulkExport.json`

New label keys: `lblHiddenLines` (Print View only — Bulk Export already has it),
`lblExportQuality`, `hiddenLinesHint`. Bulk Export's `summaries.s4` gains a
fifth `{4}` token. Combo option **values** (`"Vector Processing"`,
`"300 DPI"`, …) stay hardcoded — they are persisted setting tokens compared with
`==`, per the CLAUDE.md text-externalization rules.

## UI

Output Quality section, both tools, final order:

```
OUTPUT QUALITY
  Color Depth            [Color ▾]
  Hidden Line Views      [Vector Processing ▾]
    ↳ hint: vector keeps linework as vectors where possible; shaded and
      transparent views always rasterize. Raster forces every view to an image.
  Raster Quality         [High ▾]
  Export Quality (DPI)   [300 DPI ▾]
```

Both combos are always visible — DPI matters even in Vector mode, because Revit
still rasterizes shaded/transparent views.

## Assumption to confirm on a Windows/Revit run

`ExportQuality` defaults to `DPI300` on a freshly constructed `PDFExportOptions`.
The native default cannot be read from metadata, so the setting defaults to
`"300 DPI"`. If Revit's real default differs, the user now sees a labeled control
stating exactly what will be used — which is better than today's invisible
default either way.

## Not in scope

`MaskCoincidentLines`, `HideCropBoundaries`, `HideScopeBoxes`,
`HideUnreferencedViewTags`, `HideReferencePlane`, `PaperFormat`,
`PaperOrientation` and `StopOnError` are also unset on `PDFExportOptions`.
They are separate features, not part of this change.

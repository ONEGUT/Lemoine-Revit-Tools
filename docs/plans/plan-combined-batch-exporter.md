# Plan: Combined Batch Exporter

## Goal

Merge the **Batch Export** and **Sheet Pack** tools into a single **Batch Export** wizard.

- Remove both settings pages (no more "tx" / "tw" tabs in GlobalSettingsWindow).
- Remove all stamping from Sheet Pack (no parameter writing, no IssuePurpose, no RevisionCode).
- All options become run-time choices in the wizard steps.
- One ribbon button ("Batch Export") — SheetPackCommand and its ribbon entry are deleted.
- Keep: pack builder (two-column editor, named packs, ordered sheets, combined PDF per pack).
- Keep: Views export mode (Sheets / Views toggle in S1).
- PDF export uses **Revit's native PDFExportOptions API** (Option A) — no printer driver dependency.

## Defaults

| Option | Default |
|---|---|
| Split output by file format | ON |
| PDF on by default | ON |
| DWG on by default | OFF |
| Combine into single PDF | ON |
| Filename pattern | `{SheetNumber}-{SheetName}` |
| Paper placement | Offset from Corner (LowerLeft) |
| Color depth | Color |
| Raster quality | High |
| Hidden line views | Vector Processing |
| Zoom type | Fit to page |
| View links in blue | OFF |
| Replace halftone with thin lines | OFF |

## New Wizard Steps (5 steps)

| Id | Label | Required |
|---|---|---|
| S1 | Select Sheets / Views | Yes |
| S2 | Build Packs | No (optional) |
| S3 | Filename & Formats | Yes |
| S4 | PDF Settings | Yes (if PDF on) |
| S5 | Output & Review | Yes |

### S1 — Select Sheets / Views
Same as current Batch Export S1: Sheets/Views mode toggle, LemoineMultiSelectTabs grouped by prefix.

### S2 — Build Packs
- Only active in **Sheets** mode. In Views mode, show a note explaining packs are sheets-only.
- Pack tabs row with `+ New Pack` button (from current SheetPackViewModel.BuildS2).
- Each pack: pack name field only (no Issue Purpose, no Revision Code — stamping removed).
- SheetPackLayoutEditor (two-column available/in-pack editor) seeded from S1 selection.
- **Optional** — if no packs are defined, sheets export individually (Batch Export behaviour).
- If packs are defined, combined PDF output uses pack name as filename.

### S3 — Filename & Formats
- LemoineTokenInput for filename pattern (used for individual exports and DWG naming).
  - Token preview showing resolved filename.
  - When packs are active, a note explains the pack name overrides the PDF filename.
- Format toggles: PDF / DWG (IFC/NWC remain visible but disabled/greyed).
- DWG Options section (visible when DWG is on): Export Setup dropdown.
- No PDF options here — they move to the dedicated S4 step.

### S4 — PDF Settings
Dedicated step exposing all `PDFExportOptions` properties. Hidden/collapsed if PDF is OFF.

**Page setup:**
- Paper placement: Offset from Corner (default) / Center
  - Hint: "Offset from Corner is recommended for mixed landscape/portrait exports."
- Zoom: Fit to page (default) / Scale % (LemoineNumberStepper, 10–500%, shown only when Scale is selected)

**Output quality:**
- Color depth: Color (default) / Grayscale / Black & White
- Raster quality: Draft / Low / Medium / High (default) / Presentation
- Hidden line views: Vector Processing (default) / Raster Processing

**Combine:**
- Combine into one PDF toggle (ON by default)
  - When packs are active: shown but note explains each pack is already combined as one PDF.

**Advanced:**
- View links in blue (toggle, OFF by default)
- Replace halftone with thin lines (toggle, OFF by default)

**Paper size note (non-interactive):**
> "Paper size is read automatically from each sheet's titleblock. Sheets without a titleblock will be flagged in the export log."

### S5 — Output & Review
- Output folder picker (text box + Browse button).
- Split by format checkbox (ON by default) — creates PDF\ and DWG\ subfolders.
- Review summary cards:
  - Sheets / Views count
  - Packs count (if any)
  - Formats active
  - Color depth & raster quality
  - Filename pattern
  - Output folder

## PDF Paper Size & Orientation Handling

### Orientation (landscape vs portrait)
Revit's PDF export API determines orientation **automatically** from each sheet's physical width vs height — no explicit setting is needed or exposed. Default paper placement is **Offset from Corner (LowerLeft)**, which is the safer choice for mixed-size/orientation exports. Center placement can mis-position content when Revit's size detection is ambiguous.

### Paper size errors
Non-standard or custom titleblock sizes may not map to a recognised paper format. Mitigation:

1. **Pre-flight check** in the event handler before the export loop: scan all selected sheets/views for missing or corrupt titleblocks. Log a warning per sheet, do not abort.
2. **Per-sheet try/catch** (already in place) — any single-sheet failure is logged as a fail and the loop continues.
3. **Completion summary** already reports pass/fail/skip counts to the UI.

## Event Handler Logic

The merged `BatchExportEventHandler` gains a `Packs` property and all new PDF option properties.

**Pack mode** (`Packs.Count > 0`, Sheets mode):
- Pre-flight: log warnings for sheets with no titleblock.
- For each pack: export the pack's sheet IDs as one combined PDF (filename = sanitised pack name) using `PDFExportOptions` with all S4 settings applied.
- If DWG is on, each sheet in the pack also exports individually with the token filename.

**Individual mode** (`Packs.Count == 0` or Views mode):
- Pre-flight: log warnings for any missing titleblocks.
- Per-element loop (unchanged from current) — each sheet/view exports with the token filename.
- All S4 PDF settings applied to `PDFExportOptions`.

**Fix existing bug:** `HiddenLineViews` is currently stored in state but never passed to `PDFExportOptions`. Wire it up as part of this work.

## Files Changed

### Modified
| File | Change |
|---|---|
| `BatchExportSettings.cs` | Add `SavedPacks`, all new PDF option fields; update defaults to match spec |
| `BatchExportViewModel.cs` | Full rewrite: 5-step wizard, pack builder in S2, PDF settings in S4, no settings spec |
| `BatchExportEventHandler.cs` | Add `Packs` + all PDF option properties; pack-mode export; pre-flight check; fix HiddenLineViews bug |
| `BatchExportCommand.cs` | Also collect `allSheets` as `Dictionary<string,string>` for pack mode |
| `App.cs` | Remove SheetPackHandler/Event registration; remove Sheet Pack ribbon button |
| `GlobalSettingsWindow.xaml.cs` | Remove `"tx"` and `"tw"` from `_navDefs`; remove their `case` branches in `SwitchTab` |
| `SheetPackLayout.cs` | Remove `IssuePurpose` and `RevisionCode` properties (stamping gone) |

### Deleted
| File | Reason |
|---|---|
| `SheetPackViewModel.cs` | Merged into BatchExportViewModel |
| `SheetPackCommand.cs` | Tool consolidated under BatchExportCommand |
| `SheetPackSettings.cs` | Merged into BatchExportSettings |
| `SheetPackEventHandler.cs` | Pack export logic merged into BatchExportEventHandler |

### Kept as-is
| File | Reason |
|---|---|
| `SheetPackLayoutEditor.cs` | Clean reusable control; used in S2 |
| `SheetPackLayout.cs` | Kept but stripped of stamping fields |

## Implementation Order

1. Update `SheetPackLayout.cs` (remove stamping fields).
2. Update `BatchExportSettings.cs` (add SavedPacks, all PDF option defaults).
3. Rewrite `BatchExportViewModel.cs` (5-step wizard: S1 select, S2 packs, S3 formats, S4 PDF settings, S5 output/review).
4. Update `BatchExportEventHandler.cs` (pack-mode, pre-flight, PDF options, HiddenLineViews fix).
5. Update `BatchExportCommand.cs` (collect sheet dict for pack mode).
6. Update `App.cs` (remove Sheet Pack wiring + ribbon button).
7. Update `GlobalSettingsWindow.xaml.cs` (remove tx/tw tabs).
8. Delete the four Sheet Pack files.

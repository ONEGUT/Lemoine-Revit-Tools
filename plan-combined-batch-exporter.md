# Plan: Combined Batch Exporter

## Goal

Merge the **Batch Export** and **Sheet Pack** tools into a single **Batch Export** wizard.

- Remove both settings pages (no more "tx" / "tw" tabs in GlobalSettingsWindow).
- Remove all stamping from Sheet Pack (no parameter writing, no IssuePurpose, no RevisionCode).
- All options become run-time choices in the wizard steps.
- One ribbon button ("Batch Export") — SheetPackCommand and its ribbon entry are deleted.
- Keep: pack builder (two-column editor, named packs, ordered sheets, combined PDF per pack).
- Keep: Views export mode (Sheets / Views toggle in S1).

## Defaults (from provided screenshot)

| Option | Default |
|---|---|
| Split output by file format | ON |
| PDF on by default | ON |
| DWG on by default | OFF |
| Combine into single PDF by default | ON |
| Filename pattern | `{SheetNumber}-{SheetName}` |

## New Wizard Steps (4 steps)

| Id | Label | Required |
|---|---|---|
| S1 | Select Sheets / Views | Yes |
| S2 | Build Packs | No (optional) |
| S3 | Filename & Formats | Yes |
| S4 | Output & Review | Yes |

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
Same as current Batch Export S2:
- LemoineTokenInput for filename pattern (used for individual exports; irrelevant when packs override filenames).
- Format toggles: PDF / DWG (IFC/NWC remain visible but disabled).
- PDF Options section (visible when PDF is on): Combine PDF checkbox, Paper Placement, Hidden Lines.
- DWG Options section (visible when DWG is on): Export Setup dropdown.

### S4 — Output & Review
Same as current Batch Export S3:
- Output folder picker (text box + Browse button).
- Split by format checkbox.
- Review summary cards (Sheets/Views, Packs, Formats, Filename Pattern, Output Folder).

## Event Handler Logic

The merged `BatchExportEventHandler` gains a `Packs` property.

- If `Packs.Count > 0` (pack mode):
  - For each pack: export the pack's sheet IDs as one combined PDF (filename = pack name) if PDF is on.
  - If DWG is on, each sheet in the pack still exports individually with the token filename.
- If `Packs.Count == 0` (individual mode):
  - Existing per-element loop unchanged (each sheet/view exports with token filename).

## Files Changed

### Modified
| File | Change |
|---|---|
| `BatchExportSettings.cs` | Add `SavedPacks` and `SplitByFormat` defaults, update default booleans to match spec |
| `BatchExportViewModel.cs` | Full rewrite: 4-step wizard, integrated pack builder in S2, no settings spec |
| `BatchExportEventHandler.cs` | Add `Packs` property and pack-mode PDF export branch |
| `BatchExportCommand.cs` | Also collect `allSheets` as `Dictionary<string,string>` for pack mode |
| `App.cs` | Remove SheetPackHandler/Event registration; remove Sheet Pack ribbon button from Testing panel |
| `GlobalSettingsWindow.xaml.cs` | Remove `"tx"` and `"tw"` entries from `_navDefs`; remove their `case` branches in `SwitchTab` |
| `SheetPackLayout.cs` | Remove `IssuePurpose` and `RevisionCode` properties (stamping gone) |

### Deleted
| File | Reason |
|---|---|
| `SheetPackViewModel.cs` | Merged into BatchExportViewModel |
| `SheetPackCommand.cs` | Tool consolidated under BatchExportCommand |
| `SheetPackSettings.cs` | Merged into BatchExportSettings (SavedPacks added there) |
| `SheetPackEventHandler.cs` | Pack export logic merged into BatchExportEventHandler |

### Kept as-is
| File | Reason |
|---|---|
| `SheetPackLayoutEditor.cs` | Clean reusable control; used in S2 |
| `SheetPackLayout.cs` | Kept but stripped of stamping fields |

## Implementation Order

1. Update `SheetPackLayout.cs` (remove stamping fields).
2. Update `BatchExportSettings.cs` (add SavedPacks, update defaults).
3. Rewrite `BatchExportViewModel.cs` (4-step wizard + pack builder).
4. Update `BatchExportEventHandler.cs` (pack-mode export).
5. Update `BatchExportCommand.cs` (collect sheet dict for pack mode).
6. Update `App.cs` (remove Sheet Pack wiring + ribbon button).
7. Update `GlobalSettingsWindow.xaml.cs` (remove tx/tw tabs).
8. Delete the four Sheet Pack files.

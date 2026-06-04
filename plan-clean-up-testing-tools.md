# Plan — Clean Up Testing Tools

## Goal
Trim the experimental **Testing** panel: remove the Coordination Drawing Set and
Batch Dimension tools entirely, keep Create Sheets, and gate the "By Discipline"
tool behind a Revit-2025-only notice.

## 1. Remove the Coordination Drawing Set ("Coord Drawing Set")
Delete the whole tool — it needs a complete rework.

**Files deleted**
- `Source/Tools/Testing/CoordSet/CoordSetLegendEventHandler.cs`
- `Source/Tools/Testing/CoordSet/CoordSetViewModel.cs`
- `Source/Tools/Testing/CoordSet/CoordSetSettings.cs`
- `Source/Tools/Testing/CoordSet/CoordSetRunHandler.cs`
- `Source/Commands/Testing/CoordSetCommand.cs`

**References removed**
- `Source/App.cs`: `using LemoineTools.Tools.Testing.CoordSet;`, the
  `CoordSetRunHandler`/`CoordSetRunEvent` static properties, their init in
  `OnStartup`, and the `LT_CoordSet` ribbon button.

## 2. Remove Batch Dimension (replaced)
**Files deleted**
- `Source/Tools/Testing/BatchDimension/BatchDimensionEventHandler.cs`
- `Source/Tools/Testing/BatchDimension/BatchDimensionViewModel.cs`
- `Source/Tools/Testing/BatchDimension/BatchDimensionSettings.cs`
- `Source/Commands/Testing/BatchDimensionCommand.cs`

**References removed**
- `Source/App.cs`: `BatchDimensionHandler`/`BatchDimensionEvent` properties, their
  init, and the `LT_BatchDimension` ribbon button (keep `LT_BatchExport`).
- `Source/Lemoine/GlobalSettingsWindow.xaml.cs`: the `("ty", "Batch Dimension")`
  settings tab and its `case "ty":` content.
- `LemoinePreview/PreviewMainWindow.cs`: the `("ty", …)` tab, `case "ty":`, and
  `BuildBatchDimensionProxy()` method.
- `LemoinePreview/LemoinePreview.csproj`: the `BatchDimensionSettings.cs`
  `<Compile Include>`.
- `LemoineTools.csproj`: drop BatchDimension/CoordSet from the folder-list comment.

**Ribbon result** — the Testing panel keeps Batch Export, Create Sheets, and
By Discipline (re-grouped into a clean stacked layout).

## 3. Keep Create Sheets
No change — left in place for further development.

## 4. "By Discipline" — Revit 2025 gate
In `Source/Tools/T03-LinkViews/LinkViewsDisciplineViewModel.cs`:
- Replace the step-1 ("Assign Disciplines") content with a notice:
  *"This tool only works on Revit 2025."* (themed `LemoineWarning`/italic text,
  resource-referenced — no inline styles).
- Make `IsValid("S1")` return `false` so the step's Continue button stays disabled
  (`StepFlowWindow.RefreshStepState` disables the confirm button when a required
  step is invalid).

> Note: implemented as an **unconditional** gate (always shows the notice / disables
> Continue), matching the literal request. If you'd rather it detect the running
> Revit version and only block below 2025, say so and I'll plumb the version through.

## Post-change
- Run the CLAUDE.md silent-failure scan on the diff.
- Commit + push to the designated branch `claude/busy-hopper-SOjrM`.

## Notes
- `LemoineTools.csproj` uses default (globbed) compile items, so deleting the
  `.cs` files needs no `<Compile>` edits there.
- No other source references the deleted types (verified by grep).

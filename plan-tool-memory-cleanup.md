# Plan — Tool Memory Cleanup (release per-run RAM after tools finish)

## Problem

Audit findings (Bulk View Creation, Clash Dimension, then full sweep):

1. **Closed tool windows stay rooted in memory.** Every tool's `IExternalEventHandler`
   is a session-long static on `App`. ViewModels park callback closures
   (`PushLog`, `OnProgress`, `OnComplete`, phase-1 `OnLevelsLoaded`/`OnError`, …) on
   those handlers; the closures capture the whole ViewModel (and the WPF step content
   it built). The `ILemoineToolCleanup.OnWindowClosed` hook exists and is called by
   `StepFlowWindow`, but only ClashFinder, ClashElevationFinder, CeilingHeatmap, and
   BulkExport implement it. Every other tool's last ViewModel survives window close
   until the same tool runs again — or for the rest of the Revit session.
2. **Handler payloads persist session-long.** Run inputs (`ViewIds`, `Definitions`,
   `SlabScopes`, `SourceEntry`/`TargetEntries`, category/level/grid id lists, …) are
   never cleared after `Execute`, so the last run's data sits on the static handlers
   forever. Mostly cheap ElementIds/strings, but some hold live Revit `View` objects.
3. **Clash dimension pickers leave views open in Revit.** `SlabScopePicker` and
   `ManualDatumPicker` set `uidoc.ActiveView = view` per dimensioned view to enable
   `PickObject`. Every activated view stays open in the Revit UI after the run, and
   Revit holds each open view's graphics in native RAM — the largest contributor to
   the "holding RAM after running" symptom, and unfixable by GC.
4. **`AutoFiltersEventHandler._allViewsCache`** holds live `View` objects between runs.

## Changes

### 1. Handlers clear their own per-run payload (one consistent pattern, ~18 files)

At the end of each handler's `Execute`, in a `finally`, reset every per-run input
property to a fresh empty collection / `null` (after callbacks have consumed results,
since `Complete`/`OnResultChips` fire inside `Execute`). Callback delegates are NOT
cleared here — they belong to the window-close path (change 2).

Files (all in `Source/Tools/`):

- `T03-LinkViews/LinkViewsLevelRunHandler.cs` (`LinkInstIds`, `SelectedLevelIds`, `LevelModelNames`, …)
- `T03-LinkViews/LinkViewsLevelPhase1Handler.cs` (scan inputs)
- `T03-LinkViews/ViewsByTemplateRunHandler.cs`
- `T03-LinkViews/ViewsBulkDuplicateRunHandler.cs`
- `T03-LinkViews/LinkViewsDisciplineRunHandler.cs`
- `T03-LinkViews/ReplicateDependentViewsRunHandler.cs` (`SourceEntry`, `TargetEntries` — hold View refs)
- `T05-Clash/ClashFinder/ClashFinderEventHandler.cs` (`ViewIds`, `Definitions`, `SlabScopes`)
- `T05-Clash/ClashElevationFinder/ClashElevationFinderEventHandler.cs`
- `T04-ModifyElements/SplitByLevelEventHandler.cs`
- `T04-ModifyElements/SplitByGridEventHandler.cs`
- `T04-ModifyElements/SplitByReferencePlaneEventHandler.cs`
- `T04-ModifyElements/SplitByCellEventHandler.cs`
- `T04-ModifyElements/ExtendWallsEventHandler.cs`
- `T02-Ceilings/CeilingHeatmapEventHandler.cs`
- `T02-Ceilings/CeilingGridEventHandler.cs`
- `T01-AutoFilters/AutoFiltersEventHandler.cs` (also `_allViewsCache = null`)
- `BulkExport/BulkExportEventHandler.cs` (`SelectedIds`, `Packs`)
- `Testing/PlaceDependentViews/PlaceDependentViewsEventHandler.cs`
- `Testing/CreateSheets/CreateSheetsEventHandler.cs`
- `Testing/LegendCreator/LegendCreatorEventHandler.cs`

Caveat handled per-handler: a handler must not clear state that a *second* phase of
the same tool still needs (e.g. phase-1 results consumed by the run handler are passed
via the ViewModel, not retained on the phase-1 handler — verify per tool before
clearing; anything legitimately needed across phases stays).

### 2. Implement `ILemoineToolCleanup` on every ViewModel that wires a static handler

`OnWindowClosed()` nulls **all** callbacks the ViewModel parked on its handler(s)
(run + phase/scan/pick handlers), mirroring `ClashFinderViewModel.OnWindowClosed`.
This unroots the closed window's ViewModel graph immediately instead of at next run.

ViewModels to update (those wiring static handlers without the interface):

- `T03-LinkViews`: `LinkViewsLevelViewModel` (run + phase-1 `OnLevelsLoaded`/`OnError`),
  `ViewsByTemplateViewModel`, `ViewsBulkDuplicateViewModel`, `LinkViewsDisciplineViewModel`,
  `ReplicateDependentViewsViewModel`
- `T04-ModifyElements`: `SplitByLevelViewModel`, `SplitByGridViewModel`,
  `SplitByReferencePlaneViewModel`, `SplitByCellViewModel`, `ExtendWallsViewModel`
- `T02-Ceilings`: `CeilingGridViewModel` (heatmap already done)
- `T01-AutoFilters`: `DiscoverViewModel`, `ApplyFiltersToViewsViewModel`, and any other
  VM wiring `AutoFilters*`/`DeleteFilters*`/`LegendCreator` handlers
- `Testing`: `PlaceDependentViewsViewModel`, `CreateSheetsViewModel`, `LegendCreatorViewModel`

(Exact list confirmed during implementation by grepping every `App.*Handler` use from
a ViewModel; the four existing implementations are left as-is.)

### 3. Close views the clash-dimension pickers opened

In `SlabScopePicker.PickForViews` and `ManualDatumPicker` (same pattern):

- Before the loop, record the currently active view and the set of open `UIView`
  view ids (`uidoc.GetOpenUIViews()`).
- After picking completes (`finally`), restore the original active view via
  `uidoc.ActiveView`, then `UIView.Close()` every view the picker opened that was
  **not** open beforehand. Views the user already had open are never closed; the
  restored active view is never closed.
- Each `Close()` wrapped in try/catch → `LemoineLog.Swallowed` (closing can fail for
  the last open view); a failure leaves the view open rather than aborting.

### 4. No other changes

Confirmed clean in the audit (no action): `LemoineLog` ring (bounded at 500),
theme/UI-size event detach on window close, DispatcherTimers, `LemoineDragGhost`
adorner lifecycle, `WindowSink` severing, static settings singletons, AutoFilters
category string caches (small, overwritten per capture).

## Branch

`claude/great-bardeen-dze1k3`, already based at `main` head `f7e6807` (this remote
session may only push to its designated branch).

## Risks

- Clearing a payload a second phase still reads → mitigated by per-tool verification
  before adding the `finally` reset.
- Closing a picker-opened view the user wanted to stay in → mitigated by only closing
  views not open before the run and logging each close to the run log.
- Behaviour otherwise unchanged: no UI, no Revit-data changes; cannot be built or
  smoke-tested on this Linux container (Windows-only build) — compile/test on Windows
  after push.

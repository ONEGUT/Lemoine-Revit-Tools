# Plan — Surface Revit Errors in the Step Flow Window

## Problem

When a tool runs, Revit's own modal dialogs (transaction **failure** dialogs and
**TaskDialogs**) can appear **behind** the StepFlowWindow, and most failures never reach the
run log. Two root causes:

1. **No owner link.** StepFlowWindow runs on its own dedicated STA thread and never sets a
   window owner (`StepFlowWindow.xaml.cs` has no `WindowInteropHelper`). Revit's dialogs are
   modal to Revit's main window on the main thread, so they can z-order *below* the tool window.
2. **No universal failure capture.** A good `IFailuresPreprocessor` exists in only 3 handlers
   (`PlaceDependentViews` `SuppressWarningsPreprocessor`, `CopyLinear` `SilentFailureHandler`,
   `ReplicateDependentViews`). The other ~25 transactions configure only
   `SetClearAfterRollback` / `SetDelayedMiniWarnings`, so their warnings/errors still pop native
   dialogs and are not logged.

All needed APIs are present in `libs/RevitAPI*.dll`: `UIApplication.MainWindowHandle`,
`ControlledApplication.FailuresProcessing`, `UIControlledApplication.DialogBoxShowing`.

## Goal

Any Revit error/warning raised during a run is (a) **shown in the run log**, and (b) when Revit
*does* show a dialog, it is **forced on top of** the StepFlowWindow. Warning dialogs (noise) are
suppressed and logged; real **error** dialogs are shown on top *and* logged so the user can act.

## Design

### 1. Z-order — own the window to Revit's main window
`StepFlowWindow.OnSourceInitialized`: set `new WindowInteropHelper(this).Owner = <Revit main HWND>`.
- HWND source: `App.RevitMainWindowHandle` (new static `IntPtr`), populated lazily from
  `commandData.Application.MainWindowHandle` the first time any command runs; fall back to
  `System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle` if unset. (Per CLAUDE.md:
  use `WindowInteropHelper` with a Revit HWND — **never** `ComponentManager.ApplicationWindow`.)
- Effect: Revit's modal dialogs (modal to the main window) render above the owned tool window;
  also keeps the tool pinned to Revit on Alt-Tab. Cross-thread owner-by-HWND is the supported path.

### 2. Run-log sink for the active run
New static `LemoineRunLog` (alongside `LemoineRun`): holds the **active run's** `pushLog`
callback. `StepFlowWindow.StartRun` sets it; `CompleteRun`/`ResetAll`/window-close clear it.
Thread-safe (volatile reference). Because Revit serialises work on one main thread, exactly one
run's sink is active at a time.

### 3. Global failure capture — `ControlledApplication.FailuresProcessing`
Subscribe once in `App.OnStartup` (unsubscribe in `OnShutdown`) to a named handler:
- Only act when a run sink is active (otherwise return `Continue` and touch nothing — never
  interfere with non-Lemoine transactions).
- Log each **distinct** failure (warning + error) to the run log via the sink, and to
  `LemoineLog`.
- **Delete resolvable warnings** → suppresses their dialogs entirely.
- **Errors:** log them; leave them for Revit to display its dialog (now forced on top by the
  owner fix) so the user can decide — do **not** silently swallow (matches the project's
  no-silent-failure rule). [Alternative behaviour B below: auto-rollback + log, no dialog.]

This generalises the existing 3 preprocessors. The 3 local copies can later be retired in favour
of the global path (left in place for now to avoid behaviour churn; noted as follow-up).

### 4. TaskDialogs — `UIControlledApplication.DialogBoxShowing`
Subscribe once in `App.OnStartup`. When a run sink is active, **log** the dialog id/message to
the run log (so the user sees what popped). Default: do **not** auto-dismiss (could hide a real
prompt); the owner fix already pulls it on top. Known pure-noise dialogs may be auto-OK'd later
if the user wants.

## Files changed

| File | Change |
|------|--------|
| `Source/Lemoine/LemoineRunLog.cs` *(new)* | Active-run `pushLog` sink (thread-safe) |
| `Source/Lemoine/LemoineFailureCapture.cs` *(new)* | Global `FailuresProcessing` + `DialogBoxShowing` handlers that log to the sink and suppress warning dialogs |
| `Source/Lemoine/StepFlowWindow.xaml.cs` | `OnSourceInitialized` owner = Revit HWND; set/clear `LemoineRunLog` in StartRun/CompleteRun/ResetAll/close |
| `Source/App.cs` | Subscribe/unsubscribe the global handlers; `RevitMainWindowHandle` static |
| Commands *(light, optional)* | Set `App.RevitMainWindowHandle = commandData.Application.MainWindowHandle` once (or rely on the Process fallback — may need only 1 shared touch) |

No per-handler edits required (global capture covers all transactions). Existing per-transaction
preprocessors keep working.

## Build / test
Windows-only build. Smoke test: run a tool that throws a Revit warning (e.g. duplicate
name) and a tool that errors — confirm (1) the warning is logged and no dialog pops, (2) the
error dialog appears **on top** of the tool window and the error text is in the log.

## Decisions needed
1. **Error behaviour** — *show Revit's error dialog on top + log it* (recommended) vs *suppress
   all dialogs, log only + rollback*.
2. **Which branch to base from.**

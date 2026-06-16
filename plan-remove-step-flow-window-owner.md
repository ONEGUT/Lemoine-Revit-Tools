# Plan — Remove Step Flow Window Owner

## Problem
PR #74 owned `StepFlowWindow` to Revit's main HWND (in `OnSourceInitialized`)
so Revit's modal dialogs render on top of the tool window. The side effect is
that the window is now glued to Revit's z-order — it can't sit behind Revit,
move independently, or minimize/restore on its own. The user wants the window
to be a fully independent top-level window again.

## Decision
Remove the owner entirely. Accepted trade-off: Revit's modal failure dialogs
may again render behind the tool window. Failures are still captured into the
run's Output log via `LemoineFailureCapture` / `LemoineRunLog`, so they are not
lost — only the native dialog z-order regresses.

## Changes
- `Source/Lemoine/StepFlowWindow.xaml.cs`
  - Remove the `OnSourceInitialized` override that sets
    `new WindowInteropHelper(this).Owner = Revit HWND` and its comment block.
  - Remove the now-unused `using System.Windows.Interop;`.
  - Leave `LemoineRunLog` / `LemoineFailureCapture` wiring and cancellation
    logic untouched.
- `CLAUDE.md`
  - Reword the "Own the tool window to Revit's main HWND…" bullet under
    *Run Lifecycle* to record the decision: the window is intentionally
    independent (not owned); dialogs surfacing behind it is the accepted
    trade-off; failures are still routed to the run log.

## Out of scope
- No change to failure routing or cancellation behavior.

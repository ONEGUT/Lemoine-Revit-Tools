# Plan — Center "Rerun" button in StepFlowWindow footer

## Goal
Add a single **Rerun** button in the centre of the footer of every step-flow tool.
Clicking it re-runs the tool with all the same settings and selections — no
reconfiguration. Because every tool shares `StepFlowWindow`, this is one change
that covers all of them.

## Why this is simple
`StartRun()` calls `_tool.Run(...)`, and every tool's `Run()` reads its
selections/settings from its **own ViewModel instance fields**. Those fields are
not cleared after a run, so calling `StartRun()` a second time reuses the exact
same configuration. The Rerun button just needs to call `StartRun()` again.

## Files changed
- `Source/Lemoine/StepFlowWindow.xaml.cs` (only file)

## Changes

### 1. New field
- Add `private Button _rerunBtn = null!;` alongside `_resetBtn` / `_closeBtn`.

### 2. `BuildFooter()` — add the centred button
- Keep `Reset` (left) and `Close` (right) exactly as they are.
- Add `_rerunBtn = BuildButton("Rerun", false);` (neutral/ghost style, matching Reset).
- Give it `HorizontalAlignment.Center` and add it to the DockPanel as the
  **last (fill) child** so it sits in the centre of the footer.
- Start it `Visibility.Collapsed` (nothing to re-run until a run has completed).
- `_rerunBtn.Click += (s, e) => StartRun();`

### 3. `StartRun()` — make it re-entrant (so a rerun looks correct)
At the top, reset the "done" visuals back to the running baseline so a second run
doesn't show stale green:
- `_isDone = false;`
- progress fill + status text colour back to `LemoineAccent`
- `SetProgress(0);`
- `_rerunBtn.IsEnabled = false;` (disabled while running)

### 4. `CompleteRun()` — reveal the button
- `_rerunBtn.Visibility = Visibility.Visible; _rerunBtn.IsEnabled = true;`

### 5. `ResetAll()` — hide it again
- `_rerunBtn.Visibility = Visibility.Collapsed;` (back to configuring; Reset/Confirm
  flow takes over).

## Open UX decision (asked separately)
**When is Rerun shown?** Recommended: appears only after a run completes (centre
of footer), since "rerun" only makes sense once there's a prior run. Alternative:
always visible but disabled until the first run finishes.

## Silent-failure scan
No new `catch`, async, or Revit-boundary code is introduced — only WPF button
wiring on the existing STA thread. A scan will still be run after the edit.

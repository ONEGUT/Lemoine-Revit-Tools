# Plan — Fix Ceiling Heatmap "Load color ramp/gradient" crash

## What the user reported
Clicking **Load** on a saved color ramp in the Ceiling Heatmap **Color Ramp** step
(`S_RAMP`) closes Revit instantly, with no `diagnostics.log` entry. Asked to keep
looking for additional reasons after the first.

## Root cause found by reading (decisive — no harness needed)

### Cause 1 — WPF re-parenting throw in `RebuildRampStep` (the crash)
`CeilingHeatmapViewModel.cs:345-356`

```csharp
var rebuilt = BuildS_RAMP();              // new StackPanel `outer`; children owned by it
if (rebuilt is StackPanel sp)
    foreach (UIElement child in sp.Children)
        container.Children.Add(child);    // child still belongs to `sp`
```

`UIElementCollection.Add` calls `SetLogicalParent`; a child that already has a
different logical parent makes it throw
`InvalidOperationException: Specified element is already the logical child of
another element. Disconnect it first.` It throws on the **first** child.

This runs inside `loadBtn.Click` (`CeilingHeatmapViewModel.cs:229-245`), which has
**no try/catch**. There is **no `DispatcherUnhandledException` handler anywhere in
the codebase** (verified by grep), and every tool window runs on its own dedicated
STA thread. An unhandled exception on that dispatcher tears the thread down and
**terminates Revit**. Because it is never routed through `LemoineLog`, there is no
`diagnostics.log` entry — exactly the "Revit just closed" symptom. Trigger =
clicking **Load** on a saved ramp = "load a color gradient."

### Cause 2 — wrong refresh architecture (latent; would still break after fixing #1)
The hand-rolled `RebuildRampStep` is the wrong pattern. Even if re-parenting were
fixed by detaching children, the rebuilt buttons close over a **new orphan `outer`**
that is never in the visual tree, and `_gradientRect` would point at that orphan.
The second Load/Save/Delete would mutate a detached panel. The correct mechanism is
`IStepAware` + `SetContentRefreshCallback`, which `StepFlowWindow.RefreshStepContent`
swaps in place — exactly how `MakeCeilingGridsViewModel` refreshes its filter step.
`CeilingHeatmapViewModel` does not implement `IStepAware` today.

### Cause 3 — systemic gap that makes this whole class of bug fatal (the "keep looking" reason)
There is no last-resort `DispatcherUnhandledException` guard on the tool windows'
STA threads. Any unhandled exception in *any* tool's event handler crashes Revit
silently with no log. A named handler that routes to `LemoineLog` and keeps the
window alive would downgrade this — and future handler bugs — from a hard crash to
a logged error.

## Proposed changes

1. **`Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs`**
   - Implement `IStepAware`: add `SetContentRefreshCallback(Action<string>)`
     (store the callback) and `OnStepActivated(string)` (no-op / rebuild as needed).
   - Replace `loadBtn.Click`'s `RebuildRampStep(outer)` call with
     `_rebuildContent?.Invoke("S_RAMP")` so the framework rebuilds the step in place.
   - Delete `RebuildRampStep` entirely (it is the only re-parenting site in the repo).
   - Keep the `_gradientRect` / `_rampCombo` handles being reset inside `BuildS_RAMP`
     itself (they are reassigned each build), so a framework rebuild is self-consistent.

2. **STA dispatcher safety net (Cause 3)** — *scope to confirm with user.*
   Add a named `DispatcherUnhandledException` handler where each tool window's STA
   thread is created (the `Thread`/`Dispatcher.Run()` launch site), routing to
   `LemoineLog.Error(...)` and setting `e.Handled = true` so a stray handler
   exception is logged instead of killing Revit. Recommended but larger blast radius;
   may be split into its own branch.

## Files touched
- `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` (required fix)
- Tool-window STA launch site for Cause 3 (optional, pending approval)

## Verification
Cannot build on Linux (net48 + WPF, Windows-only per CLAUDE.md). Verification is by
inspection plus the post-change silent-failure scan. A `CrashProbe`-style harness is
**not** needed: the cause is a guaranteed managed exception located by reading, not a
mysterious native/hard fault. (Offered if the user wants live confirmation.)

## Silent-failure note
The new refresh path introduces no swallowed exceptions; it removes a guaranteed
throw. Cause 3, if approved, explicitly routes the swallowed dispatcher exception
through `LemoineLog.Error`.

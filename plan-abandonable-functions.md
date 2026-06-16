# Plan — Make All Functions Abandonable (Cancel mid-run, preserve work)

## Goal

Every tool that runs work must be **abandonable**. While a function is running, the footer
**"Reset"** button becomes a **"Cancel"** button. Clicking it requests a stop; the running
function halts at the **next break point** while **preserving any work already committed**.
Break points sit **at every point the function reports to the Output log**. When a function is
stopped short, the Output log says so explicitly, and the run finalises with the partial counts.

---

## How execution works today (context)

- `ILemoineTool.Run(pushLog, onProgress, onComplete)` is the single entry point
  (`Source/Lemoine/ILemoineTool.cs:97`). The ViewModel sets payload on a long-lived
  `IExternalEventHandler`, wires the three callbacks, and calls `ExternalEvent.Raise()`.
- The handler's `Execute(UIApplication)` runs on **Revit's main thread**, loops over items,
  and calls `pushLog` / `onProgress`, then `onComplete`. The window marshals every callback
  back to its dedicated STA UI thread via `SafeBeginInvoke` (`StepFlowWindow.xaml.cs:1028-1035`).
- The footer **Reset** button (`StepFlowWindow.xaml.cs:844-848`, handler `ResetAll()` at
  `:1108`) is **disabled during a run** (`StartRun()` sets `_resetBtn.IsEnabled = false` at
  `:1022`) and only clears the log/counters after completion.
- **There is no cancellation mechanism today** — once `Raise()` fires, the UI cannot stop the
  loop.

Because the Cancel click lands on the UI thread while the handler loops on the Revit main
thread, the stop signal must be a **thread-safe flag** the handler polls. Work is preserved by
a **cooperative break** that lets the handler reach its existing `Transaction.Commit()` — never
by throwing out of the loop (that would dispose the transaction uncommitted and lose the work).

---

## Design

### 1. New cancellation primitive — `LemoineRun` (ambient, thread-safe)

New file `Source/Lemoine/LemoineRun.cs`:

```csharp
public static class LemoineRun
{
    private static volatile bool _cancelRequested;

    /// <summary>Called by the window just before a run starts.</summary>
    public static void Begin()          => _cancelRequested = false;
    /// <summary>Called by the Cancel button (UI thread).</summary>
    public static void RequestCancel()  => _cancelRequested = true;
    /// <summary>Called by the window when the run finishes or is reset.</summary>
    public static void End()            => _cancelRequested = false;

    public static bool CancelRequested => _cancelRequested;

    /// <summary>
    /// Log a line AND test for cancellation in one call. Returns true when the caller
    /// should stop now (preserving work done so far). This is the canonical break point:
    /// wherever a handler reports to the log inside a processing loop, route it through here.
    /// </summary>
    public static bool Checkpoint(Action<string, string> pushLog, string msg, string status = "info")
    {
        pushLog?.Invoke(msg, status);
        return _cancelRequested;
    }
}
```

**Why ambient static rather than a new `Run` parameter:** Revit serialises every
`ExternalEvent.Execute` on its single main thread, so exactly one handler runs at a time. A
process-wide `volatile bool` is sufficient and avoids changing the `ILemoineTool.Run` signature
across ~35 ViewModels and ~36 handlers (which would be a large, error-prone churn). The
limitation — if two tool windows were open and one cancels while the other runs, the global flag
applies to whichever handler is currently executing — is acceptable given the one-tool-at-a-time
usage and is documented. (Alternative considered: thread a `Func<bool>`/`CancellationToken`
through `Run`; rejected for churn. See "Decision needed".)

### 2. `StepFlowWindow` — Reset becomes Cancel during a run

`Source/Lemoine/StepFlowWindow.xaml.cs`:

- **Footer wiring (`BuildFooter`, ~:846):** change the click handler to be mode-aware:
  ```csharp
  _resetBtn.Click += (s, e) => { if (_isRunning) RequestCancelRun(); else ResetAll(); };
  ```
- **`StartRun()` (:1016):**
  - `LemoineRun.Begin();` before `_tool.Run(...)`.
  - Keep `_resetBtn` **enabled** (remove `_resetBtn.IsEnabled = false` at :1022); relabel to
    **"Cancel"** with the red/warn emphasis (`LemoineWarnBorder`/`LemoineWarnText` keys, which
    already exist — `LemoineSettings.cs:132-134`).
- **New `RequestCancelRun()`:** set `_resetBtn.IsEnabled = false` + label "Cancelling…",
  `LemoineRun.RequestCancel()`, and `PushLog("Cancelling — will stop at the next checkpoint…", "warn")`.
- **`CompleteRun()` (:1038):** `LemoineRun.End();` and restore the button to **"Reset"** (default,
  non-accent styling) and re-enable. `CompleteRun` already runs for both normal and stopped-short
  finishes because the handler always calls `onComplete` after the loop.
- **`ResetAll()` (:1108):** `LemoineRun.End();` and restore the button label/style to "Reset"
  (it is already the reset path).
- **`PushLog()` (:1145):** extend the status map so `"warn"` renders distinctly (icon `"■"`, colour
  `LemoineWarnText`) — currently only `pass`/`fail`/`info` are styled and everything else falls to
  dim. This gives the "stopped" notice a deliberate, non-error look.

### 3. Per-handler break points (the bulk of the work)

For **every** `IExternalEventHandler.Execute` that loops over items, apply the standard pattern at
each point it reports per-item progress to the log:

```csharp
// before:
pushLog($"✓ Sheet {number} — {name}", "pass");

// after (inside the processing loop):
if (LemoineRun.Checkpoint(pushLog, $"✓ Sheet {number} — {name}", "pass"))
{
    pushLog($"Stopped by user — {done} of {total} processed; work so far preserved.", "warn");
    break;   // falls through to the existing Commit() / Regenerate() so done work is kept
}
```

Rules applied per handler:
- The cancellation check is placed so the loop body **completes the current item** (and its
  transaction/commit) before breaking — never mid-mutation.
- Handlers using one outer transaction committed **after** the loop: breaking out reaches the
  commit, so committed-so-far work is preserved (verify each commits post-loop, not per-abort).
- After the loop, the existing `onComplete(pass, fail, skip)` reports the **partial** counts; the
  run summary therefore reflects what was actually done.
- Handlers with **multiple** sequential loops/phases check at the top of each phase too, so a
  cancel between phases stops cleanly.
- A non-mutating **scan/discover** handler simply stops scanning and returns the partial results
  it has gathered, with the stopped-short log line.
- Read-only single-shot **pick** handlers (`ClashPickEventHandler`, `SlabPickEventHandler`) and
  the **debug** handler (`CeilingHeatmapDebugHandler`) have no long loop — no change (a single
  `PickObject` is abandoned by the user pressing Esc). Noted, not silently skipped.

### Handlers to convert (loops → checkpoints)

T01 AutoFilters: `DiscoverEventHandler`, `AutoFiltersEventHandler`, `ApplyFiltersToViewsEventHandler`,
`AutoFiltersLegendEventHandler`, `DeleteFiltersEventHandler`, `DeleteFiltersFromProjectEventHandler`.
T02 Ceilings: `CeilingGridEventHandler`, `CeilingHeatmapEventHandler`, `MakeCeilingGridsPhase1Handler`,
`MakeCeilingGridsRunHandler`.
T03 LinkViews: `LinkViewsLevelPhase1Handler`, `LinkViewsLevelRunHandler`, `LinkViewsDisciplineRunHandler`,
`ViewsByTemplateRunHandler`, `ViewsBulkDuplicateRunHandler`, `ReplicateDependentViewsRunHandler`,
`BulkRename/BulkRenameRunHandler`.
T04 ModifyElements: `SplitByGridEventHandler`, `SplitByLevelEventHandler`, `SplitByReferencePlaneEventHandler`,
`SplitByCellEventHandler`, `ExtendWallsEventHandler`.
T05 Clash: `ClashFinderEventHandler`, `ClashElevationFinderEventHandler`.
T06 CopyLinear: `CopyLinearScanHandler`, `CopyLinearRunHandler`, `CopyGridsRunHandler`.
BulkExport: `BulkExportEventHandler`, `PrintViewEventHandler`.
Testing: `CreateSheetsEventHandler`, `LegendCreatorEventHandler`, `PlaceDependentViewsEventHandler`.

(~31 looping handlers. Pick/debug handlers excluded as above.)

---

## Files changed

| File | Change |
|------|--------|
| `Source/Lemoine/LemoineRun.cs` *(new)* | Thread-safe cancel flag + `Checkpoint` helper |
| `Source/Lemoine/StepFlowWindow.xaml.cs` | Reset↔Cancel button mode, `Begin/End`, `RequestCancelRun()`, `PushLog` warn styling |
| ~31 `*EventHandler.cs` / `*RunHandler.cs` | Insert `LemoineRun.Checkpoint(...) → break` at each in-loop log point + stopped-short notice |

No new theme keys required (`LemoineWarnText/Border` already exist).

---

## Build / test

Windows-only build (per CLAUDE.md). After the infra + a first handler are done, smoke-test one
tool (Cancel mid-run, confirm partial work is committed and the log shows the stop notice), then
roll the same pattern across the rest. A post-change silent-failure scan will be run before
reporting complete.

---

## Decision needed

1. **Cancellation delivery mechanism** — ambient `LemoineRun` static (recommended, low churn) vs.
   threading a `CancellationToken` through `ILemoineTool.Run` (explicit, but touches every tool).
2. **Scope** — do all ~31 looping handlers in one pass, or land infra + a few high-traffic tools
   first and follow up.
3. **Which branch to base this work from** (per branch workflow).

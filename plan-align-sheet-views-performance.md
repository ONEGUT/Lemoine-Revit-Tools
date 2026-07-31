# Plan — Align Sheet Views: cut run time and end the silent tail

## Problem

A large batch reported ~25 min of visible progress, then sat with the progress bar at
100% and no output for a further ~10 min before printing
`"N non-fatal issue(s) recorded — see diagnostics log."` and finishing.

Two separate causes, diagnosed from `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs`.

### Cause 1 — full-model regenerations inside the per-viewport loop (the 25 min)

`doc.Regenerate()` recomputes the whole model. It is currently called **per matched
viewport pair**, not per sheet:

| Line | Call | Frequency |
|------|------|-----------|
| `TrimGrids:692` | `if (writes.Count > 0) doc.Regenerate();` | once per pair |
| `MirrorElbows:1090` | `if (created) doc.Regenerate();` | once per pair |
| `DriveTargets:361` | `if (geomChanged) doc.Regenerate();` | once per sheet |

With grid inheritance on, a 100-sheet × 4-view batch issues roughly
`100 × (1 + 2×4)` ≈ **900 whole-model regenerations**. This directly violates the
CLAUDE.md rule *"never call it per item in a loop"*.

### Cause 2 — an unreported tail phase ending in one monolithic commit (the 10 min)

`DriveTargets` reports `Pct(i + 1, total)` after each sheet, so the bar hits 100% when the
last sheet is processed. Everything below then runs with **no log line and no progress
update**:

1. `doc.Regenerate()` (`:181`)
2. `CapturePlacement` — `GetBoxCenter` + `GetBoxOutline` per pair
3. `MatchTitleLineLengths` → `doc.Regenerate()` (`:188`) → `ReportPlacement` (`GetBoxOutline` per pair)
4. `AlignTitleOffsets` (2× `GetLabelOutline` per pair) → `doc.Regenerate()` (`:194`) → `ReportPlacement`
5. **`tx.Commit()` (`:198`)** — one transaction covering the entire batch

`ConfigureFailures` sets `SetDelayedMiniWarnings(true)` (`:1912`), which defers every
mini-warning to commit time, so all of Revit's failure processing (each message routed
through `RevitFailureCapture.OnFailuresProcessing` and `fa.DeleteWarning`) lands in that
one silent block.

The closing `issuesRecorded` line is only a **count** printed after the commit returns —
the diagnostics entries were written during the run. It reads as a cause but is not one.

### Cause 3 — secondary per-item costs

- `TrimGrids:562` re-runs a view-scoped `FilteredElementCollector` for the **source**
  view's grids on every target pair, though only a handful of source views exist.
  `TrimGrids:576` runs a second view-scoped collector for the target's grid count.
  View-scoped collectors force a visibility computation.
- `ReconcileVisibility:832` enumerates `tv.GetFilters()`, resolves each
  `ParameterFilterElement` and evaluates `PassesFilter` **per grid**, though the filter
  set is a property of the view.
- `DiagnosticsLog.Write` (`Source/Framework/DiagnosticsLog.cs:148`) performs
  `Directory.CreateDirectory` + `new FileInfo` + `File.AppendAllText` (open/write/close)
  **per entry**, under a process-wide lock.
- `StepFlowWindow.PushLog` (`Source/Framework/StepFlowWindow.xaml.cs:1330`) builds three
  `TextBlock`s with `SetResourceReference` and calls `ScrollToEnd()` per line, with no cap
  on `_logStack`. Off the Revit thread, so it does not slow the run, but it grows the
  visual tree without bound and makes the window feel frozen.

## Proposed changes

### 1. Batch the grid regenerations — largest single win

Restructure so the regen count is driven by **sheets**, not viewports.

- `TrimGrids` gains a "collect only" mode: it performs the reads, the visibility
  reconcile, bubble mirroring and the extent-mode switches, and returns its pending
  `GridWrite` list and pending elbow list instead of regenerating and writing.
- `DriveTargets` accumulates those across every pair on the sheet, then runs **one**
  `doc.Regenerate()`, then all curve writes, then **one** regen for created leaders, then
  all elbow positioning.
- Per-pair `GridTally` / `ReportGridTally` output is preserved by keying the tally by pair.

Expected: ~900 regens → ~200 (2 per sheet). Regens per sheet become constant rather than
proportional to view count.

**Files:** `AlignSheetViewsEventHandler.cs` (`TrimGrids`, `MirrorElbows`, `DriveTargets`).

### 2. Report progress and log through the tail phases

The phases after the loop currently produce nothing until the commit returns.

- Reserve the last slice of the progress range for the tail: cap the loop at 90% and step
  the title/placement/commit phases across 90→100.
- Push a log line entering each phase, including an explicit
  "Committing N sheet(s) — Revit is writing the changes; this can take several minutes"
  immediately before `tx.Commit()`.

This does not make the commit faster, but it removes the "it finished then hung" reading
and satisfies the CLAUDE.md steady-cadence rule, which the tail currently ignores.

**Files:** `AlignSheetViewsEventHandler.cs`, `Strings/en/testing.alignSheetViews.json`.

### 3. Gate the placement-verification passes

`CapturePlacement` plus the two `ReportPlacement` passes cost an outline read per viewport
and force two of the three tail regens. They exist to catch the title-drift class of bug
(the centre-vs-size discriminator recorded in CLAUDE.md), which is a diagnostic concern.

- Run them in **Preview mode** always; in a real run only behind a new
  "Verify placement after titles" option (default off).
- A production run then does: align → set line lengths → regen once → set offsets →
  commit. One regen saved, plus 3 outline reads per viewport.

**Files:** `AlignSheetViewsEventHandler.cs`, `AlignSheetViewsViewModel.cs`,
`Strings/en/testing.alignSheetViews.json`.

### 4. Chunk the transaction — needs a decision

Commit every N sheets (proposed 25) instead of one transaction for the whole batch.

- Pro: spreads the commit cost so progress keeps moving; gives the cancel path a real
  checkpoint (today a cancel still has to sit through the entire commit); bounds how much
  deferred warning processing piles up.
- Con: the run is no longer a single Ctrl+Z. Title alignment must then run per chunk, and
  `MatchedPair` state has to be scoped to the chunk.

**Not implemented without an explicit yes** — the undo-granularity change is a real
behavioural trade-off, not a pure optimisation.

### 5. Buffer DiagnosticsLog file I/O

Replace per-entry open/append/close with a retained `StreamWriter` (`AutoFlush = false`)
flushed on a short timer and on process exit, keeping the existing lock and the
roll-at-1 MB behaviour. Ring buffer, `EntryLogged` fan-out and `IssueCount` unchanged.

**Files:** `Source/Framework/DiagnosticsLog.cs`.

### 6. Cap the on-screen run log

Trim `_logStack.Children` to the most recent ~2000 rows, and skip `ScrollToEnd()` when the
user has scrolled away from the bottom.

**Files:** `Source/Framework/StepFlowWindow.xaml.cs`.

### 7. Hoist per-view work out of the per-grid loops

- Cache each source view's grid list (`ViewId` → `List<Grid>`) for the run; source views
  are few and are re-collected once per target pair today.
- Compute the target view's hiding-filter set once per view and pass it into
  `ReconcileVisibility` rather than re-reading `GetFilters()` per grid.

**Files:** `AlignSheetViewsEventHandler.cs`.

## Not proposed

- Turning off `SetDelayedMiniWarnings(true)` — it would spread warning processing through
  the run instead of concentrating it at commit, but it can also let Revit surface warning
  dialogs mid-run. Worth measuring on a Windows plot before changing; flagged, not changed.

## Verification

Cannot be built or run on Linux (`UseWPF` + net48 requires the Windows-only
`Microsoft.NET.Sdk.WindowsDesktop`). Needs a Windows build and a re-run of the same batch,
comparing: total elapsed, elapsed between the last per-sheet line and `Done`, and the
per-sheet grid tallies (which must be unchanged — this is a performance change, not a
behavioural one).

## Order of work

1 → 2 → 7 → 3 → 5 → 6, with 4 held pending a decision. Each is independently revertable.

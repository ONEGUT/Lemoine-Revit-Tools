# Plan — Fix the issues found auditing the last 5 bug branches

Audit of `8323db9`, `5a1d1db`, `a3f51a8`, `e39762e`, `845246e` for the same
patterns elsewhere. Three fixes proposed (Phases 1–3) plus one deferred
candidate (Phase 4) that needs a decision.

---

## Phase 1 — Stale `RunState` cancel flag silently kills bespoke-window runs

**Severity: high — live regression introduced by `5a1d1db`.**

### The bug

`RunState.Begin()` (the only thing that clears the cancel flag) is called in
exactly two places: `StepFlowWindow.xaml.cs:1137` and `WebStepFlowWindow.cs:308`.

`CancelRunOnClose` (`StepFlowWindow.xaml.cs:1515`) deliberately leaves the flag
**set** on close — `CompleteRun`'s `RunState.End()` (`:1236`) is never reached
because `_sink.Win` was nulled first. Its comment ("cleared by `RunState.Begin()`
at the next run's start") holds only for step-flow tools.

Bespoke windows raise their handlers with no `Begin`, and those handlers *do*
read the flag:

| Raise site | Handler | Reads flag at |
|---|---|---|
| `FiltersSettingsWindow.RaiseAutoCreate` (~:440) | `AutoFiltersEventHandler` | `:289`, `:340`, `:363` |
| `FiltersSettingsWindow.RemoveTradesFromView` (~:553) | `DeleteFiltersEventHandler` | `:77` |
| `FiltersSettingsWindow.ApplyTradesToView` (~:612) | `AutoFiltersEventHandler` | as above |
| `WebAutoFiltersWindow.ApplyTradesToView` (~:148) | `AutoFiltersEventHandler` | as above |
| `WebAutoFiltersWindow.RemoveTradesFromView` (~:176) | `DeleteFiltersEventHandler` | `:77` |
| `WebAutoFiltersWindow` close-flush auto-create (~:264) | `AutoFiltersEventHandler` | as above |
| `LegendSettingsWindow` create/update (~:1325) | `LegendCreatorEventHandler` | `:434` (per block) |

Sequence: close any step-flow tool mid-run → flag stays set indefinitely → the
next Auto Filters apply / remove / close-time create, or the next Legend create,
bails at its first checkpoint. `RaiseAutoCreate` and the close-flush set
`PushLog = null`, so the "Stopped by user" line goes nowhere — **filters are
silently never created**. Legend Creator produces a legend view with zero blocks.

The flag stays stale until some step-flow tool runs to completion.

### The fix

Call `RunState.Begin()` immediately before `evt.Raise()` at each of the seven
run-starting sites above — the same "the window that owns the run resets the
flag" pattern `StepFlowWindow.StartRun` already uses. No `End()` is needed:
these windows have no Cancel affordance, so nothing sets the flag during their
runs, and the next `Begin` clears it regardless.

Files changed:

- `Source/Tools/FiltersLegends/Windows/FiltersSettingsWindow.xaml.cs` — 3 sites
- `Source/Framework/Web/WebAutoFiltersWindow.cs` — 3 sites
- `Source/Tools/FiltersLegends/LegendCreator/Windows/LegendSettingsWindow.xaml.cs` — 1 site

Each gets a one-line comment pointing at the reason (a step-flow window closed
mid-run leaves the flag set by design).

**Not changed:** `LaunchDiscover` / `OpenDeleteFromProject` in both filters
windows (they only open a window, no run) and both `ScopeBoxManager*Handler`s
(they never read the flag).

### Alternatives considered

- *`Begin`/`End` inside each handler's `Execute`.* Rejected: it would also clear
  a cancel that arrived while the event was queued, and would reset the flag
  mid-chain for `DiscoverViewModel`'s commit → create hand-off
  (`DiscoverViewModel.cs:1375`), where the chained create *should* stay cancelled.
- *Every handler calling `RunState.End()` in a `finally`.* Correct but touches
  ~30 files for a problem the run's owner can fix in seven lines.

**Known trade-off (unchanged from before `5a1d1db`):** the flag is process-wide,
so if a step-flow run were somehow in flight when a bespoke window raises,
`Begin` would clear that run's pending cancel. Revit serialises `Execute` on the
main thread, so the two runs still execute one after the other; this is the same
granularity the file already documents.

### Related gap (documented in the plan, not fixed here)

Those bespoke windows never call `RunLogSink.Set` / `RevitFailureCapture.BeginRun`,
so Revit's own failure dialogs during those runs are captured nowhere. Pre-existing,
out of scope — flagging it so it's a deliberate decision, not an oversight.

---

## Phase 2 — `ElevationTagRunner` has the exact interleave `e39762e` fixed

**Severity: medium (performance).**

`Source/Tools/Dimensioning/ElevationTag/ElevationTagRunner.cs:91–145`: the
per-marker loop reads `ce.GeometryCurve` (`:95`) and `ce.GeometryCurve.Reference`
(`:112`), then writes — `doc.Create.NewSpotElevation` (`:134`), `ChangeTypeId`,
`ClashTagSchema.StampTag` — then loops back to the next geometry read. Each write
dirties the model, so every read forces a full regeneration: one regen per clash
marker per view, the same symptom the ceiling-tag fix removed.

### The fix

Split each view's marker loop into the two phases `e39762e` established:

- **Phase A (reads):** for every marker compute `(reference, anchor, bend, end,
  groupKey)` and collect into a list. Keep the existing per-marker `try/catch`
  around the reference build (`:112–121`) — per `a3f51a8`, one bad marker must
  fail only its own tag, not the phase.
- **Phase B (writes):** create every spot elevation from the precomputed list,
  each in its own `try/catch`, incrementing `result.Placed` / `result.Failures`
  exactly as today.

`ClashTagSchema.ReadGroup(ce)` moves into Phase A with the other reads.
Failure counts, log keys, and the per-view cancel checkpoint (`:53`) are
unchanged, so no `Strings/en/*.json` edits.

Files changed: `Source/Tools/Dimensioning/ElevationTag/ElevationTagRunner.cs`.

---

## Phase 3 — Unguarded per-item calls in loops (`a3f51a8` pattern)

**Severity: low.**

1. `Source/Tools/Dimensioning/AutoDimension/Resolvers/GridTargetResolver.cs:102`
   — `new Reference(grid)` sits outside any `try` inside the per-grid loop (the
   collector above it *is* guarded at `:88–95`). A grid that won't produce a
   reference aborts the whole grid cache instead of skipping itself. Wrap it,
   route through `DiagnosticsLog.Swallowed("GridTargetResolver: build grid reference", ex)`,
   and `continue`.
2. `Source/Tools/Views/LinkViewsLevelRunHandler.cs:119` — `el.get_BoundingBox(null)`
   unguarded per scope box. Lower impact (runs before the transaction, so nothing
   committed is lost), but a throw kills the run with a raw exception instead of
   the existing `linkviews.level.log.boxMissing` skip-and-log. Wrap it and reuse
   that same skip path.

Both keep the run alive and log the skip, matching the standard in CLAUDE.md
("a survey/collector that finds zero items must say so").

---

## Phase 4 — `AutoDimensionCommit` read-back per dimension — **recommend deferring**

`Source/Tools/Dimensioning/AutoDimension/AutoDimensionCommit.cs`: `NewDimension`
(`:122–123`) is immediately followed by `ApplyTextStates`, which reads
`dim.Segments`, `seg.TextPosition`, `seg.ValueString` (`:231`) and
`dim.ValueString` (`:259`) for that same dimension before the loop places the
next one — one regen per dimension, same class as Phase 2.

Unlike Phase 2 it does **not** split cleanly: the fix is create-all → one regen →
read-all-defaults → apply-all `TextPosition` writes, and the moved-tag column
layout is order-dependent (each tag clears the tags already placed this run —
`placedTags`, `PlaceColumn`). Getting that wrong changes where dimension text
lands, and CLAUDE.md already flags the column geometry as Windows-tunable and
unverified on a real plot.

**Recommendation:** leave as-is for now; it needs its own branch and a Windows
plot to verify no layout regression. Say the word and I'll fold it in.

---

## Also audited — clean, no change

- **Ceiling Grids trade** (`MakeCeilingGridsRunHandler.cs:487–505`) — the stored
  rule (`Visible = false`, all overrides off) exactly reproduces the applied
  behaviour (`AddFilter` + `SetFilterVisibility(false)`, `:394`), so it does not
  have the `8323db9` mismatch.
- **Composite step merging** — Bulk Views is the only composite tool; nothing
  else merges inner step lists.
- **Conditional last step** — `PrintView` (S7 last) and `BulkExport` (S9 last)
  keep every conditional format step in the middle, per the `IConditionalSteps` rule.
- **Ceiling Grids create/reproject loops** (`CeilingGridEventHandler.cs:224, 309`)
  — already pre-read all faces/curves before the write loop.
- **Clash marker placement, ScopeBoxCreator, ExplodeViewByTrade, CopyDatums** —
  per-item guards and batched reads all correct.

Residual nit (not fixed): the heatmap's applied OGS also sets a black foreground
pattern *colour* (`CeilingHeatmapEventHandler.cs:291–293`), which
`ApplyRuleOverride` cannot express without a pattern id — so Auto Filters'
re-apply restores the background colour but not the black foreground.

---

## Sequencing, verification, and branch note

Commits, in order — one logical change each:

1. `Reset the run cancel flag when a bespoke window starts a run`
2. `Batch elevation-tag geometry reads before creating spot elevations`
3. `Guard per-item grid reference and scope-box bounds reads`

CLAUDE.md wants one logical change per branch, but this session is pinned to
`claude/similar-issues-audit-yqjwnu`; the plan is to keep them as three separate,
independently revertable commits on that one branch unless you'd rather split.

Cannot be compiled here (Linux — `UseWPF` needs `Microsoft.NET.Sdk.WindowsDesktop`),
so verification is: the post-change silent-failure scan required by CLAUDE.md
(numbered findings presented for warn/rethrow/log/leave-as-is), then a Windows
build. Phase 2's behaviour is best confirmed on a real project — the per-tag
"graphics regeneration" flicker should be gone.

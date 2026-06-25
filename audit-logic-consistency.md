# Logic Audit — Consistency, Optimization & Output-Log

Scope: all 24 `*EventHandler` run loops + the shared run infrastructure
(`LemoineRun`, `LemoineRunLog`, `LemoineFailureCapture`, `RunProgressReporter`).
Axes (per request): **output-log consistency**, **logic consistency**,
**optimization**. Read-only audit — no code changed yet.

Severity: 🔴 real bug / user-visible gap · 🟡 consistency drift, works but diverges
from the documented house pattern · 🟢 verified clean (recorded so we don't re-audit).

---

## A. Output-log consistency

### A1 🔴→✅ Bulk inner-loops drive the progress *bar* but emit no ~5% *log* cadence
CLAUDE.md (*Run Lifecycle*): "Report long bulk runs to the Output log every ~5%,
**not just on the progress bar**." The canonical mechanism is `RunProgressReporter`
(5% buckets, 20-item threshold).

On close reading, only one handler genuinely went silent:

- ✅ **FIXED** `CeilingGridEventHandler.cs` — three inner loops iterate **CAD curves**
  (a DWG can hold thousands): RunProject per-curve (:211), RunReproject project (:303)
  and create (:335). They drove only the bar via `Progress(...)` and emitted no
  per-item line, so the log sat silent between "Extracted N curve(s)" and "Complete".
  Threaded a `RunProgressReporter` into each (nouns "curves" / "source curves" /
  "new curves"); the 20-item threshold keeps small DWGs quiet. Bar `Progress(...)`
  unchanged.

Re-examined and **not** silent (no change needed — adding bucket lines would just
interleave redundant noise with their existing per-unit lines):

- `BulkExportEventHandler.cs` — logs a line per export (`"DWG: name.dwg"`,
  `"PDF (pack): name.pdf [...]"`); cadence is per file/pack.
- `PlaceDependentViewsEventHandler.cs:472-475` — logs a `✓ Sheet … (N view(s) placed)`
  line for **every** sheet, plus warn/skip/fail on every other path; cadence is per
  sheet.

### A2 🟢 Empty-input / empty-result reporting — present
Spot-checked the handlers the keyword scan flagged: `CeilingGrid:197` ("No curves
found in DWG"), `RefineDimensions:44`, `ClashElevationFinder:47/52`,
`DeleteFilters:33`, `CreateSheets` all guard empty inputs and report. No silent
empty collectors found in the sampled set. (Not exhaustively verified across every
collector — see "Open items".)

### A3 🟢 Cancellation reporting + commit fall-through — correct
Verified `CeilingGrid:213-217`, `SplitByGrid:105-109`: cancel is checked with
`LemoineRun.CancelRequested`, logs "Stopped by user — N of M processed; work so far
preserved", `break`s, and falls through to the existing `Transaction.Commit()`. No
throw-out-of-loop. Matches the documented cooperative-break contract.

### A4 🟢 Revit failure routing — centralized and correct
`LemoineFailureCapture` (warnings de-duped + `DeleteWarning`'d, errors logged+shown,
dialogs logged) feeds `LemoineRunLog`, both no-op outside a run. Single source of
truth; no per-handler divergence.

---

## B. Logic consistency

### B1 🟡 `LemoineRun.Checkpoint` is dead code — zero call sites
CLAUDE.md and the `LemoineRun.Checkpoint` XML doc call it "the canonical break
point" that bundles *log line + cancel test* in one call, specifically to keep the
5%-log and the cancel boundary on the same line. **No handler uses it** — all 24
check `LemoineRun.CancelRequested` directly and log separately. Behaviour is correct
(A3), but the helper that was meant to *enforce* A1's "log at the cancel checkpoint"
is bypassed everywhere. Decision needed: either adopt `Checkpoint` at the
progress/cancel sites, or delete it and update the docs to describe the actual
`CancelRequested` + `RunProgressReporter` idiom.

### B2 🟡 Three divergent progress idioms for the same job
1. `RunProgressReporter` (canonical) — 10 sites (all `T04` split tools,
   `CeilingHeatmap`, `Discover`, `ClashEngine`).
2. Raw inline `onProgress(pct,…)` — `BulkExport`, `PlaceDependentViews`,
   `ApplyFiltersToViews` (the last hand-rolls a "log only when integer % changes"
   throttle at :250).
3. Private `Progress(...)` wrapper over `OnProgress` — `CeilingGrid:441`,
   `ExplodeViewByTrade:621`, `LegendCreator:678`.

All three "work", but a reader can't assume one pattern, and only idiom (1) gets the
5% log for free (root of A1). Recommend converging bulk loops on `RunProgressReporter`
for the log + `onProgress` for the bar.

### B3 🟡 Per-run payload not cleared in `finally` (memory/lifetime)
CLAUDE.md (*Memory & Lifetime Discipline*): every handler must clear per-run payload
in a `finally`. Handlers with **no `finally`**:
`AutoFiltersLegend`, `Discover`, `SlabPick`, `ClashElevationFinder`, `ClashPick`,
`ExplodeViewByTrade`.
Nuance — not all are leaks:
- `Discover` and `ClashElevationFinder` reset inputs at the **start** of `Execute`
  (`Discover:130-131`, `ClashElevationFinder:152-153`) and expose `ScanResults` /
  results the ViewModel reads **after** `Execute`, so a finally-clear would wipe the
  output. Legit exception — but they still hold the prior run's `View`/`Element`
  refs through the idle period.
- `SlabPick`, `ClashPick` are read-only single-shot pickers with local-only state —
  likely nothing to clear (confirm).
- `AutoFiltersLegend`, `ExplodeViewByTrade` — confirm whether they hold cached
  `View`/`Element`/filter refs that should be dropped in `finally`.
Action: per-handler decision (clear-in-finally vs documented output-read exception).

---

## C. Optimization

### C1 🟢 No per-item `doc.Regenerate()` — verified clean
Every `Regenerate()` is per logical unit, the documented acceptable granularity:
- `ClashFinder:334,461` — once per new view / before the marker pass.
- `AlignSheetViews:267` — once for the whole run.
- `PlaceDependentViews:315,395` — once per sheet, and **gated** so a sheet whose
  view groups are all cached skips it entirely (`anyMeasure || !areaKnown`). This is
  exemplary; use it as the reference.

### C2 🟢 Cross-document copy batching
`RunProgressReporter` use in `ClashEngine` + the split-tools share core
(`SplitElementsShared`) indicates the documented batch-then-regenerate discipline is
followed. (Copy-from-link batching not re-verified this pass — see Open items.)

---

## Recommended fix order (after triage)

1. **A1 + B2** together: introduce `RunProgressReporter` into the three bar-only bulk
   loops (`CeilingGrid` per-curve, `BulkExport`, `PlaceDependentViews`). One pattern,
   restores the 5% log cadence. *(highest user value)*
2. **B1**: decide adopt-or-delete `Checkpoint`; align docs to reality.
3. **B3**: per-handler `finally` cleanup pass on the six handlers, respecting the
   output-read exception.

## Open items (not fully audited this pass — 74k LOC, sampled)
- Exhaustive empty-collector scan across every `FilteredElementCollector` site.
- Copy-from-link / copy-linear batch-size + idempotent-stamp re-verification.
- Per-handler confirmation of B3 cleanup needs.

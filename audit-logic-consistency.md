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

### B1 🟡→✅ `LemoineRun.Checkpoint` was dead code — removed
`Checkpoint` bundled *log line + cancel test* in one call and logged **every**
iteration. It had **zero call sites** — all handlers test `LemoineRun.CancelRequested`
directly and get their log cadence from `RunProgressReporter` (5% buckets). The
per-iteration logging in `Checkpoint` directly conflicts with that 5%-cadence design,
so it could never be adopted without re-introducing the log flooding `RunProgressReporter`
exists to prevent. **Deleted** the method from `LemoineRun.cs` and updated the docs to
describe the real idiom (`CancelRequested` break + `RunProgressReporter` cadence):
- `Source/Lemoine/LemoineRun.cs` — method removed; `CancelRequested` doc expanded.
- `CLAUDE.md` — *Run Lifecycle* cancellation bullet + Key Files row corrected.
- `Source/Lemoine/StepFlowWindow.xaml.cs:1312` — comment now points at `CancelRequested`.

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

### B3 🟡→✅ Per-run payload not cleared in `finally` (memory/lifetime)
CLAUDE.md (*Memory & Lifetime Discipline*): every handler must clear per-run payload
in a `finally`. Six handlers had **no `finally`**. On close reading they split two ways:

**Already clean — no payload to clear (no change):**
- `ClashPickEventHandler` — only `InLinks` (bool) + callbacks; result is a **local**
  handed off via `OnPicked`.
- `SlabPickEventHandler` — same shape (`InLinks` + callbacks; local `scope`/`name`).
- `AutoFiltersLegendEventHandler` — only layout constants + callbacks; all inputs come
  from `doc.ActiveView` / the `AutoFiltersSettings` singleton, work is in locals.

**✅ FIXED — cleared payload but outside a `finally`** (a throwing completion callback
could skip the clear; these are session-long App statics):
- `ClashElevationFinderEventHandler` — `ViewIds` / `Definitions`.
- `DiscoverEventHandler` — `ScanSpecs` / `CommitSpecs` / `ScanResults`. Special case:
  `Complete()` lets the ViewModel read `ScanResults` **synchronously before** the clear,
  so `Complete()` now runs *inside* the `finally`, ahead of the clear (ordering preserved).
- `ExplodeViewByTradeEventHandler` — `SourceViewId` / `SelectedTradeIds`.

Fix shape (all three): wrap the result-reporting + payload clear in `finally`; the
reporting/callbacks sit in a nested `try` routed to `LemoineLog.Swallowed`, so a
throwing callback can never skip the clear.

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

## Status

1. **A1** ✅ — `RunProgressReporter` threaded into CeilingGrid's three curve loops
   (the only genuinely silent case; BulkExport/PlaceDependentViews already log per unit).
2. **B1** ✅ — `Checkpoint` deleted; docs aligned to `CancelRequested` + `RunProgressReporter`.
3. **B3** ✅ — guarded `finally` payload-clear added to ClashElevationFinder, Discover,
   ExplodeViewByTrade; ClashPick/SlabPick/AutoFiltersLegend confirmed to hold no payload.
4. **B2** — partially addressed via A1 (log cadence now converges on `RunProgressReporter`);
   the three bar-driving idioms remain by design (the bar still needs `onProgress`).

## Deep-dive results (follow-up pass)

### D1 — Empty-collector scan (all 216 `FilteredElementCollector` sites) ✅ clean
Triaged every collector site; verified the run-facing "gather work" collectors report
zero results. The empty-result discipline is solid — every run/event handler logs a
distinct "Nothing to do / No X found / 0 …" line before doing nothing:
- LinkViews family (`ReplicateDependentViews`, `ViewsBulkDuplicate`, `ViewsByTemplate`,
  `LinkViewsDiscipline`), the Split tools, `CeilingGrid`, `AutoFiltersLegend`,
  `ClashElevationFinder`, `CreateSheets`, `RefineDimensions`, all copy tools — confirmed.
- Only borderline: `MakeCeilingGridsPhase1Handler` is a **picker-populating scan** (hands
  results to `OnTypesLoaded`, no run log); an empty result shows as an empty picker, which
  is visible, so it's not a run-log silent failure. Left as-is.
- The stamp-schema collectors (`Copy*StampSchema`) are internal `ExtensibleStorageFilter`
  reads, not user-facing surveys — correctly silent.

### D2 — Copy tools batch + idempotency re-verification
- `CopyFromLinkRunHandler` ✅ exemplary: chunked batch (`ceil(total/20)`) with per-element
  fallback, `UseDestinationTypes`, single end-regen, world-position **hash attribution**
  for stamping with an "unattributed" warning, empty-result warn + early return, cancel→commit,
  **5% log cadence** (`{pct}% — {done} of {total}`), `finally` cleanup.
- `CopyGridsRunHandler` ✅ clean: pre-checks host grid names + skip-and-log per the grid
  uniqueness rule, empty-result warn, batch + per-grid fallback, single regen, cleanup.
- `Copy*StampSchema` ✅ both use a constant `SchemaGuid` + `Schema.Lookup` guard +
  `ExtensibleStorageFilter` reads — matches the idempotency discipline exactly.
- `CopyLinearRunHandler` 🔴→✅ **FIXED**: correct on batching (same-doc `CopyElement` vs
  cross-doc `CopyElements` via `ReferenceEquals`), single end-regen + one align-calibration
  regen, stamping, cancel→commit, `finally`. **But** its main per-source loop drove only the
  bar (`OnProgress`) and logged just the first source — a large clean run went silent in the
  log (same A1 class as CeilingGrid, and inconsistent with CopyFromLink's per-chunk cadence).
  Threaded a `RunProgressReporter` (noun "source runs") so the log now reports 5% cadence.

## Open items (still not exhaustively audited)
- Per-handler confirmation of B3 cleanup needs beyond the six already reviewed.

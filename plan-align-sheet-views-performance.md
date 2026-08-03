# Plan — Align Sheet Views: cut run time and end the silent tail

> **Revision 2.** The first revision proposed seven changes off an unmeasured diagnosis.
> An adversarial review found three blocking correctness defects in its headline proposal,
> one blocking defect in the diagnostics proposal, and arithmetic biased in favour of the
> conclusion. This revision measures first, ships the output-neutral wins second, and holds
> the restructure until there are numbers. Findings are recorded inline so the reasoning is
> not lost.

## Symptom

A large batch showed ~25 min of visible progress, then sat at 100% with no output for a
further ~10 min before printing `"N non-fatal issue(s) recorded — see diagnostics log."`
and finishing.

## What is established

### The tail is real, and it is log-*suppressed*, not log-*less*

`DriveTargets` reports `Pct(i + 1, total)` at `:391`, so the bar reaches 100% when the last
sheet is processed. What follows, inside the one batch-wide transaction:

| Line | Work |
|------|------|
| `:181` | `doc.Regenerate()` — full model |
| `:182` | `CapturePlacement` — `GetBoxCenter` + `GetBoxOutline` per pair |
| `:186` | `MatchTitleLineLengths` |
| `:187` | `doc.Regenerate()` |
| `:188` | `ReportPlacement` |
| `:192` | `AlignTitleOffsets` — 2× `GetLabelOutline` per pair |
| `:193` | `doc.Regenerate()` |
| `:194` | `ReportPlacement` |
| `:198` | **`tx.Commit()`** — one transaction covering the whole batch |

**Correction to revision 1:** this phase is not silent because it lacks log calls.
`ReportPlacement` logs at `:1291`/`:1293` and `AlignTitleOffsets` at `:1375` — but at
`"info"` tone, and `Log` at `:81` drops every `"info"` line outside preview mode:

```csharp
if (s == "info" && !PreviewOnly) return;
```

On a clean run every tail line is discarded on the way out. That is a one-line fix, not a
new-instrumentation job, and it invalidates revision 1's proposal 2 as written (adding more
`Log(..., "info")` calls would change nothing).

`ConfigureFailures:1912` sets `SetDelayedMiniWarnings(true)`, which by design defers every
mini-warning to commit time, where each is routed through
`RevitFailureCapture.OnFailuresProcessing` and `fa.DeleteWarning`.

The closing `issuesRecorded` line at `:204` is a **count** printed after the commit
returned. Not a cause.

### Regenerating per matched viewport is real

`TrimGrids:692` (`if (writes.Count > 0)`) and `MirrorElbows:1090` (`if (created)`) each
call `doc.Regenerate()` once **per matched viewport pair**, violating CLAUDE.md's
"never call it per item in a loop".

**Correction to revision 1:** the claimed "100 sheets × 4 views ≈ 900 regens" was wrong at
both ends and biased the same direction each time.

- The per-sheet term (`:361`) fires only when `geomChanged` is set, and `geomChanged` is
  assigned only at `:347` (`InheritScopeBox`) and `:357` (`InheritCropSize`). Grid
  inheritance never sets it, so in the scenario quoted that term is **zero**.
- `writes` is appended only at `:681`, past four early-outs — `ModelSource` `:622-627`,
  `NoSourceCurve` `:632`, `Unbound` `:645`, `AlreadyMatching` `:659-663`. A re-run, or a
  project whose grids already line up, produces **zero** writes and zero regens.
- `created` is set only at `:1077`, when the target has no leader and the source has one.
  Repositioning an existing elbow does not set it.

"2 per pair" is a ceiling requiring both guards to fire on every pair. The true count is
unknown, which is the point of step 1 below.

## What is NOT established

**That regeneration dominates the runtime.** There is no `Stopwatch` anywhere in
`Source/Tools/Sheets/AlignSheetViews/`. Revision 1 inferred the hot spot from a symptom and
then proposed restructuring ~700 lines of ordering-sensitive Revit code on that inference,
on a machine that cannot compile the project. Unweighed candidates:

- **`MatchSheet` is O(sources × targets × views²).** `:261` sits inside
  `foreach (var src in sources)` inside the target loop; `OverlapComponents:1712-1725`
  transforms 8 corners per candidate pair; `:1652`'s `FirstOrDefault` scans `cands` from
  inside the `foreach` over `cands` at `:1646`.
- **`RevitFailureCapture` fires on every regen, not only at commit.** `_seen` (`:68`)
  de-dups the *logging*, not the `GetFailureMessages` / `GetSeverity` /
  `GetDescriptionText` / `DeleteWarning` work. Regen cost and failure-processing cost are
  entangled and cannot be separated after the fact.
- **47 `DiagnosticsLog.*` calls in the handler**, several inside the per-grid loop
  (`:608`, `:635`, `:648`, `:686`, `:703`, `:712`), each an open/write/close under a
  process-wide lock.

## Step 1 — Instrument, ship alone, measure on Windows

The only change in the first commit. Nothing else is decided without its output.

- `Stopwatch` around: the per-sheet loop body, `TrimGrids`, `MirrorElbows`, the align
  phase, each tail phase, and `tx.Commit()`.
- A process-wide counter incremented at every `doc.Regenerate()` call site in this handler,
  reported per sheet and as a run total.
- Report through `PushLog` with a **non-`info`** tone (see `:81`) so the numbers survive a
  real run.

Cost: trivial and trivially revertable. Payoff: turns every remaining item from a guess
into a decision.

## Step 2 — Output-neutral wins (safe to ship with step 1's results)

### 2a. Make the existing tail lines visible

Give the tail phases a tone that survives `:81`, and add one explicit pre-commit line —
"Committing N sheet(s); Revit is writing the changes, this can take several minutes" —
pushed the same way. Progress cannot animate *during* `tx.Commit()` (the Revit thread is
blocked), so the honest fix is a line before it, not a progress ramp through it.

### 2b. Hoist per-view work out of the per-grid loops

- **Source grid list:** cache `:562`'s view-scoped collector in a
  `Dictionary<long, List<Grid>>` **local to `DriveTargets`**, dropped on return. It must
  not become a handler field — the `finally` at `:217-222` clears only the sheet-id lists,
  so a cached live-element list would be pinned on a session-long static (CLAUDE.md,
  Memory & Lifetime Discipline).
- **Not `:576`.** That is a *target*-view collector feeding the per-pair `targetOnly` tally
  at `:767-768`. It is genuinely per-pair. Revision 1 conflated the two.
- **Filter candidates only:** hoist `tv.GetFilters()` `:832`, `GetFilterVisibility` `:834`,
  `doc.GetElement(fid)` `:835` and `SourceHidesToo` `:837` to per-view.
  **Keep `FilterCatches(pf, g)` / `ef.PassesFilter(g)` (`:836`, `:864-877`) per grid** —
  hoisting it into a per-view "hiding filter set" reintroduces the false-blame bug the
  method's own docstring at `:786-794` records as already having shipped once.
- Also per-view, missed in revision 1: `Category.GetCategory(doc, OST_Grids)` `:811` and
  `doc.IsWorkshared` `:819`.

All of 2b is pure speed — identical model writes, identical tallies, identical log lines.

## Step 3 — Batch the grid regenerations (only with step 1's numbers, and only as specified)

Revision 1's version of this would have written grid curves and leader elbows into the
**wrong views**. Three defects, all of which the implementation must resolve:

1. **The records cannot identify their own target view.** `GridWrite` (`:1986-1996`) is
   `(Grid, Curve, Mode0, Mode1)`; the elbow tuple (`:1028`) is
   `(Grid, DatumEnds, XYZ, XYZ)`. Every consumer takes the view from the enclosing closure —
   `:699`, `:706`, `:715`, `:1096`, `:1104`, `:1108` — and the failure logs at `:716` and
   `:1112-1113` take `pr.Target.ViewName` and `label` the same way. Batching across pairs
   destroys that closure. **Both records need `View TargetView`, `MatchedPair Pair`,
   `string Label`, and that pair's `GridTally`.** This matters most when one grid appears
   in several target views on the same sheet — the common case.
2. **Elbow decisions cannot precede the curve writes.** `:720-722` says so at the call
   site, and the dependency is real: `MirrorElbows:1033` → `LeaderPlaneDelta:1130-1136` →
   `DisplayedCurve:1166-1171` reads the mode set at `:679-680` and the curve written at
   `:706`. Deciding elbows before the writes computes the delta against a view-specific
   mode with no view-specific curve. `:1064` and `:1069-1072` read the same pre-write state.
3. **"Collect only" is a misnomer** — `MirrorElbows` writes to the model in its decision
   loop, at `:1077` (`AddLeader`) and `:1051` (`SetLeader(..., null)`), and `created`
   (`:1090`) cannot be known until those have run.

So the restructure is **three externally-sequenced phases per sheet**, not two:

```
per pair: reads + visibility reconcile + bubbles + extent-mode switches   → collect writes
          doc.Regenerate()                                    ← once per sheet
per pair: curve writes
per pair: elbow decisions (incl. AddLeader / SetLeader-null)  → collect positions
          doc.Regenerate()                                    ← once per sheet
per pair: elbow positioning
per pair: ReportGridTally
```

Two further consequences to handle:

- **`ReportGridTally` (`:724`) must move** out of `TrimGrids` into the sheet-level block —
  `t.Changed` is incremented both at `:625` (pass 1) and `:707` (write phase), and
  `t.Rejected` at `:711`. That reorders grid lines relative to the `aligned` lines (`:368`)
  and the per-sheet roll-up (`:386-389`). Revision 1 claimed ordering was "preserved"; it
  is not, and the reordering must be accepted deliberately.
- **Preview mode has no write block.** `:325` calls `TrimGrids(..., apply: false)` and
  `writes` is never populated (`:665-673` returns before `:681`). If the tally report moves
  to the batched write site, preview — whose entire purpose is the grid report — emits
  nothing for grids. Preview must keep its own reporting path.

**Also required while these three loops are being rewritten:** `RunState.CancelRequested`
is tested only at `:236` and `:177`. The per-grid loop `:587`, the write loop `:695` and
the elbow loop `:1031` have none, and CLAUDE.md requires a test at each per-progress log
point. `RunProgressReporter` exists and is used by nine other tools; this one uses it
nowhere.

## Held pending an explicit decision

### H1. Gate the placement-verification passes — **changes observable output**

Revision 1 proposed defaulting `CapturePlacement`/`ReportPlacement` off. This fails the
plan's own acceptance bar: it removes `placementStable` `:1291`, `placementTitleOnly`
`:1293`, `placementMoved` `:1295` and the per-viewport drift entries `:1281-1282`, which
feed `IssuesSince` `:202` and therefore change the `issuesRecorded` line `:204`. It also
disables the detector CLAUDE.md credits with catching the ~0.14 sheet-feet title-drift bug
in this exact tool.

Revision 1 also misdescribed it — "run them in Preview mode always" is impossible; these
passes live inside the non-preview `else` (`:168-199`) and preview returns at `:162-166`.
And the saving was inflated: `ReportPlacement` reads an outline only when the centre moved
(`:1265`), so a clean run costs one outline read (`:1226`) plus two `GetBoxCenter` calls,
not three outline reads per viewport.

What *is* safe, and is folded into step 3: dropping the `:181` and `:193` regens. **`:187`
is load-bearing** — it separates every `SetBoxCenter` (`:1771`) and `LabelLineLength` write
(`:1333`) from the first `GetLabelOutline()` read (`:1357-1358`), exactly as CLAUDE.md
requires. It stays.

### H2. Chunk the transaction — **changes undo granularity**

Commit every ~25 sheets. Spreads commit cost, gives cancel a real checkpoint (today a
cancel still sits through the whole commit), bounds deferred-warning pile-up. Costs the
single-Ctrl+Z contract, and requires `allPairs` (`:232`) and `_sheetIssues` (`:67`, `:244`)
to be re-scoped per chunk. Not implemented without a yes.

### H3. Cap the on-screen run log — **changes observable output**

If capped at all: exempt preview runs (CLAUDE.md: "Preview / diagnostic modes keep every
line"), and print an explicit "older lines trimmed" marker rather than dropping silently.
A preview run emits ~8 lines per pair from `DiagnosePair` (`:1398-1456`) alone.

## Dropped

**Buffering `DiagnosticsLog`.** The log's stated purpose is forensics after a hard crash —
CLAUDE.md: *"Hard crashes (no entry in diagnostics.log) are native/WPF/message-loop or
stack-overflow faults that try/catch cannot catch"*. `File.AppendAllText` (`:183`) is
durable per entry by design, and a native AV or stack overflow does not run
`ProcessExit`, so buffering discards precisely the tail an investigation needs. Three
further defects made it worse than a wash:

- A retained handle breaks `RollIfTooLarge:198-200` — `File.Move` on an open file throws
  `IOException` on Windows, caught at `:203` and reported only via `Debug.WriteLine`, which
  is `[Conditional("DEBUG")]` and compiles out in Release. The log would grow unbounded,
  silently.
- `new FileInfo(path).Length` `:195` under-reports by the unflushed buffer, so the 1 MB
  trigger fires late or never.
- A timer callback taking `_gate` (`:152`) and throwing would terminate the process.

Grep confirms **nothing subscribes to `EntryLogged`** (only `:19`, `:61`, `:162`), so the
comment at `:175-177` — "the in-memory ring and live sink still carry the entry" — is false
in practice. The file is the record. If per-entry append proves hot in step 1: keep a
`FileStream` open `FileShare.ReadWrite`, `Flush(true)` on every Warning/Error, batch only
`Info`, and close/reopen around the roll.

**Not touching `SetDelayedMiniWarnings(true)` (`:1912`)** until step 1 reports. Turning it
off would spread warning processing into the visible run, but can let Revit surface warning
dialogs mid-run.

## Pre-existing silent failures found during review (not introduced by this plan)

Out of scope for a performance branch; listed for a decision — warn / rethrow / log /
leave as-is:

1. `:407` `return prm.Set(...)` — a `false` return is discarded at `:344-349`; a refused
   scope-box assignment produces no log line and no diagnostics entry.
2. `:493` `tgtActive.Set(1)` — return ignored; the method writes all four annotation
   offsets and returns `true` regardless.
3. `:812-816` + `:847-853` — `cleared.Add` `:814` is unconditional but `SetCategoryHidden`
   `:815` is guarded by `CanCategoryBeHidden`; when the guard fails, `t.Restored++` and the
   `gridRestored` line claim a restore that never happened.
4. `:841-845` — any throw inside `ReconcileVisibility` is reported as `gridCauseTemplate`
   (a view-template blocker) whichever of the four checks actually threw. The method written
   to avoid false leads emits one.

## Verification

Cannot be built or run on Linux (`UseWPF` + net48 needs the Windows-only
`Microsoft.NET.Sdk.WindowsDesktop`). Needs a Windows build and a re-run of the same batch.
Compare: total elapsed; elapsed between the last per-sheet line and `Done`; regen count;
and — as the correctness gate — per-sheet grid tallies and per-sheet aligned counts, which
must be **identical** before and after every step except the ones explicitly held above.

## Order

**1** (instrument, ship alone, measure) → **2a**, **2b** (output-neutral) → **3** (only with
numbers, only as specified above). **H1**, **H2**, **H3** await a decision. Buffering dropped.

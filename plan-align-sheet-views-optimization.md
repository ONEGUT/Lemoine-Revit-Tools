# Plan — Align Sheet Views: speed optimization + option simplification

Status: **awaiting approval** — no code written yet.

## Files touched

| File | Change |
|---|---|
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs` | Restructure the run for regen batching; delete preview mode, placement diagnostics and dead info logging; cache/hoist per-run reads |
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsViewModel.cs` | Remove all note text, the align-titles checkbox, the MODE section; make crop size implied by scope box |
| `Strings/en/testing.alignSheetViews.json` | Delete the keys the above removals orphan |

No new files. No changes to `AlignSheetViewsCommand.cs`, `App.cs`, or the ribbon.

---

## 1. Remove preview-only mode

`PreviewOnly` property, the `if (PreviewOnly)` branch in `Execute`, and the whole
`applyMoves: false` code path come out. Deleted with it:

- `DiagnosePair`, `DiagnoseMissing`, `LegacyTargetBoxCentre`, `TryOutlineSize`
- the `apply` parameter threaded through `TrimGrids` / `ReconcileVisibility` /
  `MirrorBubbles` / `MirrorElbows` / `ResetEndsToModel`, and every
  `if (!apply)` branch inside them
- the `gridsWould*` / `gridWouldMove` / `gridWouldRestore` / `wouldAlign*` string pairs
- `OverlapComponents`' split-return signature — it exists only so preview could report
  *which* of in-plane vs depth vetoed a candidate; it collapses back into
  `OverlapInSourcePlane`

## 2. Remove all descriptive note text from every step

Every `Note(...)` call in S1, S2 and S3 goes, and the `Note()` helper with it.
That is 8 note blocks. The `Hint()` helper stays — its two uses are empty-state
messages ("No sheets were found in this project"), not descriptive chrome.

**One decision for you:** `ReviewNote` on S4 is the same class of text (a dim
paragraph explaining how matching works). My recommendation is to remove it too,
for consistency. `ReviewWarning` stays — it is a caution about model changes, not
a description.

## 3. View-title alignment always on

`AlignTitles` property, its checkbox, the `titles` review row and the
`s3Titles` summary fragment are removed. `MatchTitleLineLengths` +
`AlignTitleOffsets` run unconditionally. The review warning folds the title
clause in permanently instead of composing it from a fragment.

## 4. Scope box implies crop size

Today "Crop size + annotation crop" is an independent checkbox, and the handler
silently skips the crop resize for any view whose crop is scope-box governed.
Proposed: ticking **Scope box assignment** replaces the crop-size checkbox with a
static `✓ Crop size inherited from the scope box` row, and the handler runs the
crop-inheritance path for those views automatically — which writes only the
annotation-crop margins, because the scope box governs the crop rectangle itself.
Unticking scope box brings the normal crop-size checkbox back.

Alternatives considered: (B) keep the checkbox but force it checked and disabled —
rejected, CLAUDE.md's UX rule says hide an option that can't apply rather than
showing it disabled; (C) drop the crop-size option entirely and always inherit —
rejected, it is a real model change some runs won't want.

See `plan-align-sheet-views-optimization.png` for the rendered mockup.

---

## 5. Speed — where the time actually goes

`doc.Regenerate()` dominates: it recomputes the whole model, and the current code
calls it **once per matched viewport pair** in two places.

### 5a. Regen count

Current, for `S` target sheets × `P` matched pairs each:

```
per sheet   1 (scope/crop geometry)
          + P (TrimGrids — after the extent-mode switches)
          + P (MirrorElbows — after AddLeader)
run end     3 (placement baseline, after line lengths, after title offsets)
          = S·(1 + 2P) + 3
```

Proposed — restructure each sheet into two phases with **one** regen between them,
and defer every elbow position to the single run-end regen:

```
Phase A (per sheet, no regen): scope box + crop size writes,
                               grid visibility reconcile, bubble mirrors,
                               grid extent-mode switches, source-curve reads
   ── doc.Regenerate() ── (only if Phase A changed something)
Phase B (per sheet, no regen): align viewports, write grid curves,
                               AddLeader for new elbows, crop visibility
run end: MatchTitleLineLengths for all pairs
   ── doc.Regenerate() ──   (one, for the whole run)
         AlignTitleOffsets + position every pending elbow
          = S·1 + 1
```

50 sheets × 4 pairs with grid inheritance on: **453 regens → 51**.
A plain alignment with no inheritance ticked: **3 → 1**.

This keeps every ordering rule CLAUDE.md documents: extent mode switched before
the curve is validated/written with a regen in between; `AddLeader` regenerated
before the leader is positioned; `LabelLineLength` regenerated before
`GetLabelOutline` is read. It only stops repeating those regens per pair.

*Not doing:* batching Phase A across **all** sheets for a 2-regen total run. That
would leave a cancelled run with crop changes written but viewports unaligned —
per-sheet keeps each sheet self-consistent.

### 5b. Delete the placement-verification pass

`CapturePlacement` / `ReportPlacement` / `TryOutlineBox` and the four
`MatchedPair.Placed*` fields are pure diagnostics. They cost one extra regen plus
a `GetBoxOutline()` per pair per phase (three phases), and they move nothing.

⚠ **This is a real, if small, loss:** the `placementMoved` warn line is visible
today. The bug it was built to catch was the *correction* pass that used to sit
here, and that was already deleted — nothing remaining can reintroduce it. Say
the word if you'd rather keep the report and pay the regen.

### 5c. Cache the source view's grids across targets

`TrimGrids` runs `new FilteredElementCollector(doc, sourceView.Id).OfClass(typeof(Grid))`
per pair. A view-scoped collector forces Revit to compute that view's visible
element set. Aligning 50 targets to 1 reference re-does that 50 times, plus every
per-grid source read (extent type, curves, bubble state, leaders).

Cache per source view id for the run: the grid list and each grid's source-side
state. The target-side collector stays — each pair has a distinct target view.

### 5d. Hoist the per-grid visibility reconcile

`ReconcileVisibility` currently runs, **for every grid**: `Category.GetCategory(doc, …)`,
`doc.IsWorkshared`, a full walk of `tv.GetFilters()` with a `doc.GetElement` +
`GetCategories()` + `PassesFilter` per filter, and a `sv.GetFilters()` call per
filter inside `SourceHidesToo`. With 20 filters × 50 grids that is ~1000 redundant
`GetFilters()` calls on the source view alone.

Hoist to once per view pair: resolve the Grids category once per run, read
`doc.IsWorkshared` once, and pre-compute the short list of filters that are hidden
in the target, present-and-not-hidden in the source, and name the Grids category.
Only that short list gets a per-grid `PassesFilter`. Same verdicts, same log lines.

### 5e. Stop re-reading state already in hand

- `UsesViewSpecific` / `DisplayedCurve` / the `m0`/`m1` capture call
  `GetDatumExtentTypeInView` ~6–8× per grid per pair. Read each end's mode once
  per (grid, view) and pass it down.
- `MirrorElbows` → `LeaderPlaneDelta` re-reads both views' displayed curves,
  which Pass 1 already computed. Carry them forward.
- `TargetBoxCentre` re-reads the target's annotation crop live for every pair.
  Only needed when crop-size inheritance actually rewrote that view — otherwise
  reuse the values captured in `CaptureSheet`.
- Skip the source-sheet scoring list allocation when only one source sheet is
  selected (the common case).

### 5f. Drop two API calls whose results are ignored

`IsCurveValidInView` and `IsLeaderValid` are both called, logged on a negative,
and then the write is attempted regardless — CLAUDE.md documents both as
advisory-only for exactly this reason. They cost one API call per grid end and
produce a diagnostics line no one can act on. Removing them changes no behaviour.

### 5g. Delete the dead `info` logging

`Log()` drops every `"info"` line unless `PreviewOnly` is set. With preview gone,
**all 36 info calls are unconditionally dead** — but their `AppStrings.T(...)`
arguments are still formatted on every sheet, pair and grid before being thrown
away. Deleting the call sites (and the orphaned keys) is free.

**No user-visible log output changes:** these lines are already invisible in a
real run. Conditionally-toned logs keep their warn branch — e.g. the grid tally
still warns when a grid pass changed nothing, and `viewTitlesSome` still warns on
a title failure. The per-sheet roll-up, all warnings, all failures and the final
`done` line are untouched.

### 5h. Dead members removed

`VpEntry.CropActive` (assigned, never read), `MatchedPair.IntendedCentre`
(written, never read), `noteNoCrop` (an info line about a view with no active
crop).

---

## What is deliberately NOT changed

- Matching quality: scope-box-first pairing, the global best-first overlap
  fallback, the ambiguity test, the missing/extra/ambiguous reporting and the
  sheet scoring all stay exactly as they are.
- The alignment maths: `FootprintCentre`, `SourceAnchorOnSheet`, `ApplyRotation`
  and the annotation-crop compensation are untouched.
- Cancellation, progress cadence, the single transaction, `RunState`,
  `RevitFailureCapture` wiring, and `IToolCleanup`.
- Grid inheritance semantics: model-vs-view-specific handling, the coincidence
  translation, bubble mirroring, elbow mirroring in both directions, and the
  restore-on-rejection path.

## Verification

The project cannot build on Linux (`UseWPF` + net48 needs the Windows-only SDK),
so this ships for a Windows build. Post-change I will run the CLAUDE.md silent-
failure scan over the diff and report it before committing.

## Branch

The designated branch `claude/sheet-align-view-optimization-60man6` already exists
and currently sits exactly on `origin/main` (nothing ahead, nothing behind), so it
is ready to take this work as-is. Confirm that is the base you want.

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

`ReviewNote` on S4 goes too — it is the same class of text (a dim paragraph
explaining how matching works). `ReviewWarning` stays: it is a caution about
model changes, not a description.

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

Current, for `S` target sheets × `P` matched pairs each:

```
per sheet   1 (scope/crop geometry, so the align phase can read the new crop)
          + P (TrimGrids — after the extent-mode switches)
          + P (MirrorElbows — after AddLeader)
run end     3 (placement baseline, after line lengths, after title offsets)
          = S·(1 + 2P) + 3
```

### 5a. Predict the post-inheritance crop instead of regenerating to read it

The per-sheet geometry regen exists for one reason: `TryAlign` reads
`view.CropBox` **live**, so the crop has to be recomputed after a scope box or a
crop resize is written. But every one of those writes has a result we can derive
without asking Revit — and `SetBoxCenter` is absolute ("go to X"), so a stale
read can never accumulate error the way a relative move would.

Working through `TargetBoxCentre`, which needs `SourceAnchorOnSheet(src)`
(captured source values only — no live read), `local` (the source anchor in
target crop-local coords) and `(fx, fy)` (the target's footprint centre):

**Scope box inherited.** After assignment the target's crop covers the scope
box's footprint — the same world footprint the source's crop already covers,
since the source carries that scope box. A footprint has one centre, so
`AnchorWorld(src)` lands exactly on the target's crop centre:
`local.X = (min.X+max.X)/2`, `local.Y = (min.Y+max.Y)/2`. Substituting into
`FootprintCentre`, the whole offset collapses to

```
local.X − fx = −(annoRight − annoLeft) / 2
local.Y − fy = −(annoTop   − annoBottom) / 2
```

— which depends only on the annotation-crop offsets, and those are either the
ones we just wrote from the source or the ones captured before the run. **Nothing
live is needed.** (This holds for an anti-parallel view direction too — the
crop-local frames mirror, but the shared normal means the anchor's depth
component maps to target-local Z, never to X/Y.)

**Crop size inherited, no scope box.** We compute `nb.Min`/`nb.Max` ourselves,
keeping the target's own crop centre, and a resize does not touch the crop
`Transform`. So `local` is what the *captured* `Transform` gives, and the crop
centre is the captured one. Both already in hand.

**No inheritance.** Everything comes from `CaptureSheet` unchanged.

So `TryAlign` stops fetching the target `View` and its `CropBox` altogether — two
API calls saved per pair on top of the regen — and the **per-sheet geometry regen
disappears entirely**.

### 5b. Verify the predictions once, at the end, and correct what missed

Assume the writes land, then check. After the single run-end regen, re-read each
aligned view's real crop and annotation crop, recompute the centre, and compare
against what was predicted. A mismatch beyond tolerance gets a fresh absolute
`SetBoxCenter` with the true value — exact, because the call is "go to X" — and a
warn line naming the sheet and view.

Cost is one `View` + `CropBox` + annotation read per pair, once per run, and no
extra regen unless a correction actually fires. Corrections run **before**
`AlignTitleOffsets`, since moving a box drags its title with it.

### 5c. Delete the placement-verification passes

`CapturePlacement` / `ReportPlacement` / `TryOutlineBox`, the four
`MatchedPair.Placed*` fields and the `placement*` strings come out, as you asked.
They cost a regen plus a `GetBoxOutline()` per pair per phase across three phases
and move nothing. The prediction check in 5b is a different animal and replaces
them: it verifies the placement against the maths rather than reporting whether a
box centre drifted since it was written.

### 5d. Resulting regen schedule

```
per target sheet (no regen unless grid inheritance is on):
  Phase A  scope box / crop size / annotation crop / crop visibility writes
           grids: visibility reconcile, bubble mirrors, extent-mode switches
  ── regen ──  ONLY if grid extent-mode switches were queued for this sheet
  Phase B  write this sheet's grid curves
           predicted SetBoxCenter for every pair      ← no live crop read
           LabelLineLength writes, AddLeader for elbows
  roll-up + progress

── regen #1 (one, whole run) ──
  verify predicted centres → correct + report mismatches
── regen #2 (only if a correction fired) ──
  AlignTitleOffsets, position every pending elbow
```

| Run | Today | Proposed |
|---|---|---|
| 50 sheets × 4 views, no inheritance | 3 | **1** |
| 50 sheets × 4 views, scope box + crop size | 53 | **1** |
| 50 sheets × 4 views, + grid inheritance | 453 | **51** |

Every ordering rule CLAUDE.md documents still holds: extent mode switched before
the curve is written with a regen between; `AddLeader` regenerated before the
leader is positioned; boxes moved and `LabelLineLength` written before
`GetLabelOutline` is read.

*Offered, not taken:* batching the grid mode-switch regen across all sheets too
would put even the grid run at 2 regens. It would push every grid warning
(blocked, rejected, unbound, target-only…) past the per-sheet roll-up lines, so a
sheet's problems would no longer print above the line that points at them. That
is a quality cost, so I've kept grids at one regen per sheet — say the word if
you want the trade.

### 5e. Cache the source view's grids across targets

`TrimGrids` runs `new FilteredElementCollector(doc, sourceView.Id).OfClass(typeof(Grid))`
per pair. A view-scoped collector forces Revit to compute that view's visible
element set. Aligning 50 targets to 1 reference re-does that 50 times, plus every
per-grid source read (extent type, curves, bubble state, leaders).

Cache per source view id for the run: the grid list and each grid's source-side
state. The target-side collector stays — each pair has a distinct target view.

### 5f. Hoist the per-grid visibility reconcile

`ReconcileVisibility` currently runs, **for every grid**: `Category.GetCategory(doc, …)`,
`doc.IsWorkshared`, a full walk of `tv.GetFilters()` with a `doc.GetElement` +
`GetCategories()` + `PassesFilter` per filter, and a `sv.GetFilters()` call per
filter inside `SourceHidesToo`. With 20 filters × 50 grids that is ~1000 redundant
`GetFilters()` calls on the source view alone.

Hoist to once per view pair: resolve the Grids category once per run, read
`doc.IsWorkshared` once, and pre-compute the short list of filters that are hidden
in the target, present-and-not-hidden in the source, and name the Grids category.
Only that short list gets a per-grid `PassesFilter`. Same verdicts, same log lines.

### 5g. Stop re-reading state already in hand

- `UsesViewSpecific` / `DisplayedCurve` / the `m0`/`m1` capture call
  `GetDatumExtentTypeInView` ~6–8× per grid per pair. Read each end's mode once
  per (grid, view) and pass it down.
- `MirrorElbows` → `LeaderPlaneDelta` re-reads both views' displayed curves,
  which Pass 1 already computed. Carry them forward.
- Skip the source-sheet scoring list allocation when only one source sheet is
  selected (the common case).

### 5h. Drop two API calls whose results are ignored

`IsCurveValidInView` and `IsLeaderValid` are both called, logged on a negative,
and then the write is attempted regardless — CLAUDE.md documents both as
advisory-only for exactly this reason. They cost one API call per grid end and
produce a diagnostics line no one can act on. Removing them changes no behaviour.

### 5i. Delete the dead `info` logging

`Log()` drops every `"info"` line unless `PreviewOnly` is set. With preview gone,
**all 36 info calls are unconditionally dead** — but their `AppStrings.T(...)`
arguments are still formatted on every sheet, pair and grid before being thrown
away. Deleting the call sites (and the orphaned keys) is free.

**No user-visible log output changes:** these lines are already invisible in a
real run. Conditionally-toned logs keep their warn branch — e.g. the grid tally
still warns when a grid pass changed nothing, and `viewTitlesSome` still warns on
a title failure. The per-sheet roll-up, all warnings, all failures and the final
`done` line are untouched.

### 5j. Dead members removed

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

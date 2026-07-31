# Plan — Align Sheet Views: grid heads/tails not moving

## Symptom

Target views that share a scope box with their source counterpart, showing the same
grids, keep their original grid head/tail positions after a run with **Inherit grid
2D extents** enabled.

## Diagnosis

All findings are in `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs`.
Ranked by how likely each is to be the cause.

### 1. `IsCurveValidInView` is asked before the target is switched to 2D mode — likely 100% rejection

`TrimGrids` line 540 validates:

```csharp
if (!g.IsCurveValidInView(DatumExtentType.ViewSpecific, tv, c)) { rejected++; continue; }
```

…and only *then*, at lines 549-550, switches the target's ends to `ViewSpecific`.

`SetCurveInView` is documented as valid only when the datum's extent type in that view
is already `ViewSpecific`. The validator mirrors that precondition, so for any grid that
has never been dragged in the target view — precisely the population this feature exists
to fix — the check is being asked about a state the view is not in yet. Every such grid
falls into `rejected` and is skipped.

This is a regression introduced by `2a37525`, which added the pre-check while fixing the
previous skip-everything bug.

**Log signature:** `Grids on '<view>': N rejected the source curve as invalid for this view — not trimmed.`

**Fix:** switch the extent type first, validate second, and restore the original extent
type per end if validation fails — same "leave the view untouched on rejection" intent,
without the chicken-and-egg. If validation still fails, attempt `SetCurveInView` inside
the existing try/catch anyway and report the throw, rather than skipping silently on an
advisory check.

### 2. The source curve is never projected onto the target view's plane

Lines 525 and 552 read a curve from the source view and write it straight into the target
view. A datum's 2D extent curve lies in the *view's* plane, so a curve read from a Level 1
plan sits at Level 1's elevation and is being written into a Level 5 plan. Cross-level
alignment is this tool's headline use case.

**Fix:** translate the curve along `tv.ViewDirection` onto the target view's plane before
validating or writing:

```
n     = tv.ViewDirection.Normalize()
delta = -((c.GetEndPoint(0) - tv.Origin).DotProduct(n)) * n
c     = c.CreateTransformed(Transform.CreateTranslation(delta))
```

A pure translation along the shared normal is correct for lines and arcs alike, and matches
the projection pattern CLAUDE.md already documents for section/elevation geometry.

### 3. Grid bubbles (heads and tails) are never copied at all

The tool copies extents only. `IsBubbleVisibleInView` / `ShowBubbleInView` /
`HideBubbleInView` exist on `Grid` (verified present in `libs/RevitAPI.dll`) and are never
called anywhere in the codebase.

If a source view shows a bubble at End0 and the target does not, the extents can match
perfectly and the *head* still will not appear. This is the most literal reading of
"heads or tails not in the right locations".

**Fix:** after writing the curve, mirror each end's bubble visibility from source to target,
guarded by `HasBubbleInView`, counted and reported like every other outcome.

### 4. A Model-extent source silently copies the shared 3D extent and reports it as "trimmed"

`ReadDisplayedCurves` (lines 588-598) falls back to `DatumExtentType.Model` when the source
view carries no 2D override. The Model curve is the grid's *view-independent* 3D extent — so
writing it as the target's 2D override changes nothing visible, yet increments `trimmed` and
reports success.

It also flips the target's ends into 2D mode for no benefit, permanently detaching them from
scope-box and model-extent updates.

**Fix:** when the source is on Model extents, leave the target alone and count it under a
named cause ("source view uses model extents — nothing view-specific to copy"). Never convert
a target end to 2D to write a curve it already had.

### 5. The `??` fallback substitutes wrong geometry and calls it success

Line 597: `TryGetCurves(g, first, view) ?? TryGetCurves(g, second, view)`.

If the source view genuinely uses `ViewSpecific` but the call returns nothing or throws, this
falls back to the Model curve — copying the *wrong* extents under a "trimmed" message. The
swallow at line 622 goes to `DiagnosticsLog` only, never to the run log.

**Fix:** report the fallback in the run log, and count it separately from a clean copy.

### 6. `trimmed` counts writes, not changes

There is no accounting for "wrote a curve identical to the one already there". The user cannot
distinguish "the code did nothing" from "the code wrote the same thing again" — which is exactly
the ambiguity in the current bug report.

**Fix:** compare the target's existing displayed curve to the incoming one and split the count
into `changed` and `already matching`.

### 7. Preview mode reports nothing whatsoever about grids

`DriveTargets` `continue`s at line 291 when `applyMoves` is false, before Phase C, so `TrimGrids`
never runs in preview. `DiagnosePair` (lines 694-771) reports crop, annotation crop and box
centres in fine detail and says nothing about grids.

The one mode built to answer "is it my layout or the code?" is silent on the feature in question.

**Fix:** add a read-only grid section to the preview report — per grid: source extent mode,
target extent mode, source and target endpoints, the delta, whether the curve validates, and
whether it would change anything.

### 8. Grids visible in the target but not the source are never touched or reported

Line 499 collects from the source view only. A grid the target shows and the source does not is
silently left alone. Per the project's "a survey that finds zero must say so" rule, it should be
counted and named.

### 9. Multi-segment grids copy only segment 0

Already acknowledged and reported at line 535, but worth restating: the remaining segments leave
the target with mismatched heads. No fix proposed — `SetCurveInView` takes a single curve.

## Not a code bug — worth checking in your layout

If **several views on one sheet share the same scope box**, `MatchSheet` Pass 1 (lines 918-934)
cannot use the scope box as a unique key and defers them to the overlap pass, which flags them
**Ambiguous** when a rival scores within 80%. Ambiguous pairs are dropped entirely — no alignment
*and* no grid trim.

**Log signature:** `Ambiguous match for '<view>'`. If you see this, the grid pass never ran on
those views and no grid-side fix will help them.

## Files to change

| File | Change |
|---|---|
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs` | Reorder validate-vs-switch (1); project curve to target plane (2); copy bubble visibility (3); stop converting Model-extent sources (4); surface the fallback (5); count changed-vs-identical (6); grid section in `DiagnosePair` + run `TrimGrids` in preview as read-only (7); report target-only grids (8) |
| `Strings/en/testing.alignSheetViews.json` | New keys for the added causes, bubble counts, changed/identical split, and the preview grid report |

Sequencing inside `TrimGrids`: switch every grid's extent mode, `doc.Regenerate()` **once per
target view**, then write the curves — never a regen per grid (CLAUDE.md: regen is the dominant
cost of bulk sheet tooling).

## Verification

Cannot be built or run on Linux. Verification is a Windows/Revit plot:

1. Run in **preview** on the failing sheets and read the new grid report — it names which of
   causes 1-8 is firing, per grid.
2. Run for real with **Inherit grid 2D extents** on; confirm `changed` > 0 and the heads move.
3. Confirm no `Ambiguous match` lines for the views in question.

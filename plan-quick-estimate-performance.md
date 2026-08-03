# Plan — Why "Quick estimate" is slower than "Accurate (measured)" in Create Sheets

## Summary of the finding

Quick estimate was supposed to be fast because it skips the per-sheet
`doc.Regenerate()` that Accurate needs to measure viewport outlines. It doesn't
get that saving, because it **interleaves a geometry read with model writes** —
the exact pattern that already caused a "one full regeneration per item"
slowdown twice in this repo (`e39762e` ceiling tags, `b84cd3f` elevation tags).
A read issued while the model is dirty forces Revit to regenerate the whole
model before it can answer.

### The interleave, in `PlaceDependentViewsEventHandler.Execute`

Per sheet, the estimate branch runs:

| Step | Lines | Kind |
|------|-------|------|
| `ViewSheet.Create` + sheet number/name `Set` | 273–275 | **write** |
| `WriteSeries` → `p.Set(...)` | 288 | **write** |
| `TrimAnnotationCrop` × N (`CropBoxActive`, anno param, 4 offsets) | 293–299 | **write** |
| `EstimateRect` → `v.CropBox`, `v.Scale` × N | 350–355 | **read ← forces a full regen** |
| `Viewport.Create` × N | 363 | **write** |

Accurate has no read in that position — it creates the viewports, calls one
explicit `doc.Regenerate()`, and only then reads `GetBoxOutline()`. So Accurate
pays **one** regen per sheet; Quick estimate pays a *forced* regen per sheet for
the crop reads **and still leaves every `Viewport.Create` unregenerated until
`tx.Commit()`**, where they all land in one monolithic pass. That is why the
"fast" mode is not faster.

`TrimAnnotationCrop` does not touch `View.CropBox` or `View.Scale` (it writes
`CropBoxActive` and the four *annotation* crop offsets), so the estimate reads
can move ahead of the trim writes with **no change in the computed rects**.

### Second instance — "Accurate (grouped)"

Same bug, worse shape. Lines 385–402:

```csharp
foreach (var dv in toPlace)
{
    vp = Viewport.Create(doc, sheet.Id, dv.Id, XYZ.Zero);   // write
    bool cacheable = grouped && dv.CropBoxActive;           // read  ← per view
    var key = cacheable ? GroupKey(dv) : default;           // read v.CropBox, v.Scale
}
```

In Measured mode `grouped` is `false`, so `&&` short-circuits and the reads never
happen. In **Grouped** mode they do — write/read/write/read once per view, i.e. a
forced full-model regeneration *per viewport*. Grouped is advertised as "much
faster when many views match"; it is currently the slowest of the three on any
sheet with several views.

### Third instance — `TrimAnnotationCrop` itself (all modes)

Lines 603–632 interleave within one view: read `view.Scale` → write
`CropBoxActive` → read `get_Parameter` → write param → read
`GetCropRegionShapeManager()` → write 4 offsets. Shared by all three modes so it
does not explain the *difference*, but it is a per-view forced regen × N × M and
is likely the single largest cost in the tool.

### Fourth — a genuine per-sheet regen loop when the title block has no bbox

Lines 330–346: if `TryGetDrawingArea` fails, `areaKnown` stays `false`, so
**every remaining sheet calls a full `doc.Regenerate()`** and then places
nothing. Accurate doesn't degrade this way (it regenerates per sheet anyway).
Low-probability path, but it is literally the "never regenerate per item in a
loop" anti-pattern from CLAUDE.md.

---

## Proposed changes

All in `Source/Tools/Sheets/PlaceDependentViews/PlaceDependentViewsEventHandler.cs`.

1. **Estimate: read before write.** Compute every `EstimateRect` for the sheet
   *before* the `TrimAnnotationCrop` loop, and carry the rects forward. Removes
   the forced regen per sheet. No behavioural change — `CropBox`/`Scale` are not
   affected by the annotation-crop writes.

2. **Estimate: flush per sheet.** Keep one `doc.Regenerate()` per sheet after the
   `Viewport.Create` batch, so the deferred work is bounded per sheet instead of
   dumped on `tx.Commit()` in one pass with a frozen UI. (Estimate still skips
   the *measure* regen and the outline reads — the real saving — while the
   commit stays cheap and progress stays live, matching the CLAUDE.md note that
   per-sheet regen reads as faster than one all-at-once regen.)

3. **Grouped: split read and write phases.** Read `CropBoxActive` and the
   `GroupKey` for every view *before* the `Viewport.Create` loop, then create all
   viewports. Removes the per-viewport forced regen; each phase keeps its own
   per-item `try/catch` so one bad view still fails only itself.

4. **`TrimAnnotationCrop`: hoist the reads.** Read `Scale`, the annotation-crop
   parameter and `GetCropRegionShapeManager()` up front, then apply all writes.

5. **Latch the drawing-area failure.** Try to resolve the area once; on failure
   set a `areaFailed` flag, report it once, and abort the run with a clear
   message instead of regenerating and creating empty sheets for every remaining
   view.

6. **Phase timing in the run log.** `Stopwatch` totals for trim, sizing
   (estimate/measure), `Viewport.Create`, explicit regens (count + ms),
   positioning, and `tx.Commit()`, printed as one summary line at the end of
   every run. This project cannot be built or profiled on Linux, so this is the
   only way to confirm the fix on a real Windows/Revit model — and it makes any
   future "which mode is actually fastest" question answerable from the Output
   log. New `AppStrings` keys in `Strings/en/testing.placeDependentViews.json`
   (user-facing run-log text is externalized).

## Files touched

| File | Change |
|------|--------|
| `Source/Tools/Sheets/PlaceDependentViews/PlaceDependentViewsEventHandler.cs` | Items 1–6 |
| `Strings/en/testing.placeDependentViews.json` | Timing-summary keys |

No UI changes, no new files, no change to the three modes' meaning or output
geometry.

## Risks

- Item 1 assumes `View.CropBox` is unaffected by activating the crop and setting
  annotation-crop offsets. It is a stored property, not derived from those
  offsets, so the rects should be byte-identical — but this is the one item
  worth eyeballing on the first Windows run.
- Item 2 changes Quick estimate's regen cadence from "one at commit" to "one per
  sheet". If the timing readout from item 6 shows the commit was never the
  problem, this is trivially revertible on its own.
- Everything else is a pure reorder with no semantic change.

## Branch

Already on `claude/quick-estimate-performance-qnd5u3` (created by the harness).
Confirm whether to keep it based on `main` as-is.

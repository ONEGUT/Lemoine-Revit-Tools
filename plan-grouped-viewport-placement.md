# Plan — Grouped Viewport Placement ("Accurate (grouped)" layout mode)

## Goal

Speed up **Place Dependent Views on Sheets** when many placed views share the
same on-sheet footprint (the common case: a parent plan's dependents are often
identical crops at the same scale). Measure the **first view of each size group**
for real, then reuse that measured footprint for every other view in the group —
collapsing today's "one measure-regen per sheet" into "measure only until every
distinct group has been seen."

## Why the needed data is cheap (confirmed against source)

`Viewport.GetBoxOutline()` (the real footprint read in
`TryGetOutlineSize`, `PlaceDependentViewsEventHandler.cs:571`) is fully
determined by three values that are all readable off the `View` with **no
`Viewport.Create` and no regen** — the code already reads them in
`EstimateRect` (`:629`):

```csharp
double scale = v.Scale > 0 ? v.Scale : 1.0;   // :631
BoundingBoxXYZ cb = v.CropBox;                 // :632
```

- crop region size in model feet (`cb.Max - cb.Min`)
- view scale
- annotation-crop extents (the bubbles/heads past the crop)

So the grouping key is **`(cropWidthPaper, cropHeightPaper, scale)`**,
tolerance-bucketed. Two views in the same bucket place to the same box outline.
(View *location* in the model is irrelevant to footprint — size + scale only.)

### Reliability gate — Trim must be ON

The key reliably predicts the *real* outline only when the annotation crop is
**uniform** across the group, which is exactly what **Trim bubbles** enforces
(it floors all four `CropRegionShapeManager` offsets to the same
`trimPaperFt × scale`, `:532`). With trim **off**, the real outline includes
whatever annotations stick out, which can differ between two otherwise-identical
crops — so the key is only approximate.

**Decision:** Grouped mode requires Trim ON. If the user selects Grouped with
Trim off, fall back to Measured for that run and log a one-line warning (rather
than silently producing a wrong-but-fast layout).

## Algorithm (new third branch, modeled on the Accurate branch)

A run-level cache `Dictionary<GroupKey,(double w,double h)>`:

1. Resolve the title-block drawing area once (one regen on the first sheet),
   same as Estimate/Accurate today.
2. Per sheet:
   a. Create every viewport at `XYZ.Zero` (`Viewport.Create` needs no regen).
   b. Compute each view's `GroupKey`. Partition into **cached** vs **unseen**.
   c. **Only if there are unseen keys** (or uncacheable views — see edge cases):
      `doc.Regenerate()` once, then `GetBoxOutline()` the unseen ones and store
      their real size in the cache.
   d. Look up every view's size from the cache, `LayOutSheet(...)`, then
      `SetBoxCenter(...)` each (needs no regen).
   - A sheet whose groups are **all already cached** does **zero regens** — that
     is the win. A 100-sheet run of mostly-identical dependents collapses to a
     handful of measured sheets.
3. Log the grouping per the survey-must-report rule:
   `"Grouped N view(s) into G size group(s) — measured G, reused N−G."`

### `GroupKey`
Bucket the paper dimensions to a fixed grid (nearest **1/64"**) and combine with
the integer scale:

```csharp
private static (long w, long h, int scale) GroupKey(View v)
{
    double scale = v.Scale > 0 ? v.Scale : 1.0;
    BoundingBoxXYZ cb = v.CropBox;
    double wPaper = Math.Abs(cb.Max.X - cb.Min.X) / scale;
    double hPaper = Math.Abs(cb.Max.Y - cb.Min.Y) / scale;
    long bucket(double ft) => (long)Math.Round(ft * 12.0 * 64.0); // 1/64" units
    return (bucket(wPaper), bucket(hPaper), (int)Math.Round(scale));
}
```

### Edge cases
- **No active crop** (`!v.CropBoxActive`): footprint isn't crop-bound — treat as
  **uncacheable** (always measure that view, never store/reuse its key).
- **Composite mode source view**: it is never trimmed and anchors the layout, so
  always measure it per sheet (treat as uncacheable). The grouped win still
  applies to the sub-views; composite gains less because the source forces a
  per-sheet regen anyway. Document this; don't block composite.
- A view that reports no outline when measured is handled exactly as today
  (warn, leave at origin) and is not cached.

## Files to change

1. **`Source/Tools/Testing/PlaceDependentViews/PlaceDependentViewsEventHandler.cs`**
   - Replace the `EstimateMode` bool input with a `LayoutMode { Measured, Grouped, Estimate }`
     enum (default `Measured`). Add the enum next to `PlaceViewsMode`.
   - Add the new **Grouped** branch alongside the existing `if (EstimateMode) … else …`.
   - Add `GroupKey(...)` and the run-level size cache (local to `Execute`, cleared
     with the rest of the per-run payload — no new statics).
   - Trim-off → grouped: fall back to Measured + one warning log.
   - Update the class XML doc to mention the three modes.

2. **`Source/Tools/Testing/PlaceDependentViews/PlaceDependentViewsViewModel.cs`**
   - Layout step (`BuildS4`, `:333`): 3-item `LemoineSingleSelect`
     — `Accurate (measured)`, `Accurate (grouped)`, `Quick estimate (fast)`.
   - Replace the `_estimateMode` bool field with a `LayoutMode` field; update the
     selector wiring (`:340-342`), the explanatory `Note`, and the review-summary
     strings (`:442`, `:477`).
   - Pass `_handler.LayoutMode` instead of `_handler.EstimateMode` (`:505`).
   - Add a short note on the Grouped item that it requires Trim and reuses the
     first measured view of each identical size.

No XAML, no new control, no new statics, no public-DTO/XmlSerializer changes.

## Silent-failure scan (pre-commit)
Will run the required scan on the diff before committing — the new code adds only
pure cache lookups and reuses the existing measure/place/log paths; no new
`catch`, no unawaited tasks, no unchecked Revit nulls expected, but I'll confirm
and report.

## Out of scope (your earlier list — deferred)
Pack-N-per-sheet, place-onto-existing-sheets, re-layout, group-by-parameter, etc.
This change is only the grouped measurement optimization.

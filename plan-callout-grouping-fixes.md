# Plan — Callout grouping fixes + close-after-run crash work

Branch: `claude/fervent-pascal-5fay2s` (continuation).

## Issues from testing

1. **Overlapping callouts** — two dense clusters in (or near) the same room now grow
   to the same room footprint, so their callout rectangles overlap.
2. **Clashes inside the callout's room still mark/dimension in the parent view** —
   only the dense-cluster members were deferred; everything else in the room stayed.
3. **Callout dimensions should target what is visible in the callout** — nearest
   slab edge / grid / reference inside the callout crop, never something off-crop.
4. **Revit crashed on closing the wizard after a successful run.**

## Changes

### 1. Merge overlapping callout requests — `AutoDimensionEngine.SurveyDenseAreas`
Restructure: compute each extreme cluster's room-grown, margin-padded rectangle
(`Core.Box2`), then iteratively merge any two rectangles that intersect (union the
boxes and member sets). One `DenseCalloutRequest` per merged area; its scale is
recomputed from the merged/swept member points via the existing `DemandRatio`.

### 2. Sweep the whole room into the callout
- After the final rectangle is known, EVERY ingested source whose anchor falls
  inside it joins `SourceKeys` (not just the dense-cluster members) — so all
  clashes in the room are excluded from the parent view's dimension pass and
  dimension inside the callout (the callout's crop+storey volume gate already
  marks them there).
- **Parent markers removed**: after a callout is successfully created and marked,
  `CreateDenseCallouts` deletes the parent view's marker elements (tagged cross
  lines + filled regions whose `ClashTagSchema` group is in `SourceKeys`) — the
  clashes are shown only in the callout; the parent keeps the callout bubble.
- **Stale callout cleanup**: existing `"{parent} - Dense …"` views are reused in
  order; with Clear-previous on, leftovers from earlier runs are deleted so stale
  bubbles can't pile up / overlap.

### 3. Callout dimensions target only what the callout shows
- `ResolveContext.TargetBounds` (`Core.Box2?`, view-2D): when set, a candidate
  whose dimension landing point falls outside it is skipped.
  `GridTargetResolver` + `SlabEdgeTargetResolver` enforce it in their candidate
  loops; the slab targeted path (stamped Group 2 element) falls back to the
  nearest-visible-edge scan when the stamped edge is off-crop.
- `AutoDimensionEngine.BuildPlan` gets `boundTargetsToCrop`; when true it builds
  `TargetBounds` from the view's crop box. `AutoDimensionRunner.Run` takes the
  set of view ids to constrain; the Clash Finder passes its callout view ids.
- `Box2` gains `Contains(Vec2)`.

### 4. Close-after-run crash
Per CLAUDE.md the harness comes first; one concrete deviation also gets fixed now:
- **Fix found deviation**: `ClashFinderViewModel.StartSlabPick` parks an
  **unguarded** `disp.BeginInvoke` on the static `SlabPickEventHandler.OnPicked`
  and never clears it — exactly the documented leaked-callback → terminated
  dispatcher crash class. Guard it with the dispatcher-shutdown check and sever
  it (plus `_activateWindow`) in `OnWindowClosed`.
- **Probe harness**: new `CrashProbeViewModel` step "Close-after-run replay" —
  opens a throwaway StepFlowWindow (own STA thread) with a dummy tool that
  captures the run callbacks; after the user runs + closes it, separate buttons
  replay `pushLog` / `onProgress` / `onComplete` / `ValidationChanged` /
  `NavigateRequested` / activate-callback against the closed window. The button
  that kills Revit names the crashing path.

## Files

| File | Change |
|---|---|
| `Core/Box2.cs` | `Contains(Vec2)` |
| `Resolvers/ResolveContext.cs` | `TargetBounds` |
| `Resolvers/GridTargetResolver.cs` | bounds check |
| `Resolvers/SlabEdgeTargetResolver.cs` | bounds check + targeted→scan fallback |
| `AutoDimensionEngine.cs` | merged areas, room sweep, crop target bounds |
| `AutoDimensionRunner.cs` | crop-bounded view set parameter |
| `ClashFinderEventHandler.cs` | parent marker removal, stale callout cleanup, pass callout ids |
| `ClashFinderViewModel.cs` | slab-pick callback guard + sever |
| `Debuggers/CrashProbeViewModel.cs` | close-after-run replay probe |

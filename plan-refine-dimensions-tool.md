# Plan — Refine Dimensions tool

## Goal
A new, standalone **Refine Dimensions** tool that re-dimensions views which **already have clash
markers**, *without* detecting or re-marking clashes. It:

1. Dimensions each existing marker to the **nearest edge/grid visible in that view** (same crop-bound
   mechanism the dense-area callouts already use).
2. **Never creates callouts** (dense or user-adopted).
3. **Never changes a view's scale** (a consequence of never creating callouts).
4. Defaults the destination to **Slab Edge**, with **Grid** also selectable.

## Why this is clean (key findings)
- `AutoDimensionRunner.Run(doc, viewIds, cfg, log, progress, datums, slabScopes, excludeSourceKeys, cropBoundedViewIds)`
  builds its plan entirely from the **existing in-view markers** via `SourceIngest.Collect` — it does
  **not** need clash detection or the `dets` list. (`AutoDimensionRunner.cs`, `AutoDimensionEngine.BuildPlan`)
- The runner **does not create callouts** — `SurveyDenseAreas` / `SurveyUserCallouts` are invoked only by
  `ClashFinderEventHandler`. So calling the runner directly yields *no callouts* and *no scale changes* with
  no extra flag. (requirements 2 & 3 satisfied structurally)
- Passing the selected views in `cropBoundedViewIds` sets `ctx.TargetBounds = CropBounds2D(view)`, which makes
  the resolvers reject any grid/slab target whose dimension landing point falls outside what's shown in the
  view — i.e. "nearest edge/grid **visible in view**". This is exactly the callout-view path
  (`AutoDimensionEngine.cs:122-128`, `SlabEdgeTargetResolver.cs:83-87,125-126`). (requirement 1)
- `AutoDimensionCommit` deletes the tool's **prior owned** dimensions (AutoDimOwnerSchema) and re-places, so a
  refine run cleanly **replaces** the dimensions a previous Clash Finder / Refine run made — markers and other
  annotations are untouched.

> Note on uncropped views: `CropBounds2D` returns null when `CropBoxActive` is false, so an uncropped plan
> falls back to the standard "nearest target within the distance cap" behaviour (still view-correct, just not
> crop-limited). Mentioned for transparency; no special handling needed.

## Files

### New
1. **`Source/Tools/T05-Clash/AutoDimension/Refine/RefineDimensionsEventHandler.cs`**
   `IExternalEventHandler`. Inputs (set by the VM before `Raise()`): `ViewIds`, `DimTargetType`
   ("SlabEdge" | "Grid"), plus the `PushLog / OnProgress / OnComplete / OnResultChips` callbacks.
   `Execute`:
   - `LemoineFailureCapture.BeginRun()` + `LemoineRunLog.Set(pushLog)` at start; `LemoineRunLog.Clear()` not
     needed here (window-close clears it) — mirror Clash Finder.
   - Override `AutoDimensionConfig.Instance.TargetType = DimTargetType` inside try/`finally` (restore the
     snapshot), exactly like `ClashFinderEventHandler`.
   - Call `AutoDimensionRunner.Run(doc, ViewIds, cfg, Log, Progress, datums:null, slabScopes:null,
     excludeSourceKeys:null, cropBoundedViewIds: new HashSet<ElementId>(ViewIds))`.
   - Honour `LemoineRun.CancelRequested` — the runner commits per its own transaction; for a single
     all-views run we check cancel before raising and rely on the runner's existing behaviour (read-only
     build is cheap). Report placed/failed/deleted via result chips and `OnComplete`.
   - `finally`: clear the per-run payload (`ViewIds = new List<>()`) — memory discipline.

2. **`Source/Tools/T05-Clash/AutoDimension/Refine/RefineDimensionsViewModel.cs`**
   `ILemoineTool, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup`. Three steps:
   - **S1 Select Views** — `LemoineBrowserTreePicker` (subscribe `SelectionChanged` before `SetTree`).
     Required.
   - **S2 Destination** — `LemoineSingleSelect`: "To Slab Edge" (default) / "To Grid". A dim note that
     refine never makes callouts and never changes scale, and that it replaces the tool's existing
     dimensions on those views.
   - **S3 Review & Run** — framework-rendered review.
   `Run()` sets the handler inputs and `Raise()`s. `OnWindowClosed()` nulls the parked callbacks.
   Built with the `/revit-navisworks-ui` skill (invoked before writing this file).

3. **`Source/Commands/T05-Clash/RefineDimensionsCommand.cs`**
   Mirrors `ClashFinderCommand`: collect non-template FloorPlan/CeilingPlan views on the main thread,
   `BrowserTreeCapture.Capture(doc)`, construct the VM with `App.RefineDimensionsHandler` /
   `App.RefineDimensionsEvent`, launch `StepFlowWindow` on a dedicated STA thread (single-instance guard).

### Edited
4. **`Source/App.cs`**
   - Add statics `RefineDimensionsHandler` + `RefineDimensionsEvent` next to the Clash ones (lines ~86-91).
   - Create them in `OnStartup` (lines ~196-201).
   - Add a ribbon button to `clashPanel` (after Clash Finder, ~line 446):
     `"LT_RefineDimensions", "Refine\nDimensions", "RefineDimensionsCommand"`, Segoe MDL2 glyph via
     `char.ConvertFromUtf32(...)` (e.g. `0xE70F` Edit / `0xE890` Ruler-like). `clashPanel` currently uses
     `AddItem` per button, so a 4th `AddItem` is fine (the 2-or-3 `AddStackedItems` limit does not apply).

## Out of scope / not doing
- No Manual-datum destination in refine (it needs per-view interactive picking).
- No change to the Clash Finder, the engine, the resolvers, or the config schema — refine reuses them as-is.
- No new settings; chaining/grouping/spacing stay on the saved Settings → Dimensions values.

## Verification after coding
- Build on Windows (cannot build on Linux per CLAUDE.md).
- Post-change silent-failure scan over the new files.

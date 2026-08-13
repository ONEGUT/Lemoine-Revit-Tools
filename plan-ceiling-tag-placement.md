# Plan — Rebuild Ceiling Tag Placement (prebuild model, no per-tag regen)

**Decided:** standalone **Tag Ceilings** tool (not revived inside Ceiling Heatmap).
**Decided:** the core is built around a category-agnostic taggable-region abstraction so
**rooms** can be added to the tag list later under the same placement rules — see
"Room-ready seam". Rooms are **not implemented in this pass**; ceilings get validated first.

## Why

The old ceiling tagging lived inside `CeilingHeatmapEventHandler.PlaceCeilingTags` and was
disabled because `IndependentTag.Create` against linked ceilings ran ~2s per tag (20 min for
580): every create was interleaved with geometry reads, which forced Revit to regenerate the
whole model before each read. This rebuild follows the Clash Finder / Auto Dimension
architecture — **read the model exactly once into an internal Revit-free model, do all
geometry work there, then commit all mutations in one batch** — so the run performs at most
one regeneration (the transaction commit), never one per tag.

## What the tool does

A standalone **Tag Ceilings** step-flow tool:

1. **Read phase** (`CeilingTagEngine`, read-only): per selected RCP view, one collector pass
   gathers host + linked ceilings (reusing the heatmap's collector pattern and
   `GetViewBoundsFilter`). For each ceiling it extracts, once:
   - the bottom face (normal Z < −0.9, same detection as the old code),
   - its boundary loops via `Face.GetEdgesAsCurveLoops()` (verified present in the 2024
     metadata), tessellated into 2D polygons (world XY) with holes,
   - the bottom face's world Z (for occlusion ordering),
   - the tag `Reference` (`new Reference(el)`, linked: `.CreateLinkReference(link)`).
   After this phase the Revit model is never read again.

2. **Plan phase** (`TagCore/`, Revit-free, mirrors `AutoDimension/Core/`): all geometry work
   runs on the internal model — pure C#, no Revit types, unit-testable.
   - **Visible region (covered ceilings)**: each ceiling's footprint is rasterized into a
     local grid (6" cells, internal constant). Cells covered by any other collected ceiling
     whose bottom Z is **lower** are removed — an RCP looks up, so the lower ceiling hides
     the higher one where they overlap in plan. The remainder is the visible region; all
     centroid/corridor math runs on it. A fully covered ceiling is skipped **and logged**
     ("fully hidden by lower ceilings"); a visible region that splits into islands tags each
     island independently (tiny slivers below a fixed min area are skipped-and-logged).
   - **Compact ceilings**: one tag at the visible region's centroid. If the centroid falls
     outside the region (L-shape, hole), the point snaps to the interior cell with the most
     clearance nearest the centroid — the tag center is always ON the ceiling, guaranteed by
     construction rather than by post-placement nudging (which would need reads/regen).
   - **Corridor ceilings**: distance transform + thinning gives the region's centerline
     skeleton; the skeleton polyline is split at corners (direction change ≳ 40°). Each
     straight leg is a stretch: a ring corridor around a room yields 4 legs → 4 tags, one
     centered per side; an L yields 2. A region whose skeleton is short relative to its
     width is classed compact (single centroid tag). Legs longer than the **max tag
     spacing** setting (default 30′) are subdivided evenly — `n = ceil(length / spacing)`
     tags at the midpoints of `n` equal sub-segments — so a straight 90′ corridor gets 3
     tags ~30′ apart. Every emitted point snaps to the nearest interior cell (same
     guarantee as above).

3. **Commit phase** (`CeilingTagCommit`, one transaction): optionally delete existing
   ceiling-category tags in each view (replace mode, as before), then for every planned
   point call
   `IndependentTag.Create(doc, tagTypeId, viewId, reference, addLeader:false, TagOrientation.Horizontal, point)` —
   the overload that takes the tag **type id** directly (verified in `libs/RevitAPI.dll`
   metadata: `Create(document, symId, ownerDBViewId, referenceToTag, addLeader, tagOrientation, pnt)`).
   No `TagMode`/default-tag dependency, no `ChangeTypeId` second pass, no reads between
   creates. Cancellation via `RunState` at the 5% progress cadence, committing work done so
   far.

## Room-ready seam (designed now, built later)

The placement rules — visible-region occlusion, centroid, corridor stretches, 30′ spacing,
snap-to-interior — must apply unchanged to rooms. So the core never mentions ceilings:

```
TaggableRegion            // Revit-free: Id, Kind, outer polygon + holes,
                          // OcclusionLayer, SortDepth, DisplayName
  → TagPointPlanner       // shared: occlusion → centroid / stretches / spacing → points
  → TagPlacement[]        // Revit-free: RegionId, 2D point, StretchIndex
```

Three seams, one per phase:

- **Read** — `IRegionSource` produces `TaggableRegion`s plus a parallel Revit bundle
  (reference/element id/link). `CeilingRegionSource` (this pass) extracts bottom-face loops
  via `Face.GetEdgesAsCurveLoops()`. A future `RoomRegionSource` extracts
  `SpatialElement.GetBoundarySegments(options)` (verified present) — cheaper, no solid
  needed, and linked rooms transform the same way.
- **Plan** — untouched by kind. Occlusion is computed **per `OcclusionLayer`**, so ceilings
  only ever hide ceilings; rooms would form their own layer and never interact with ceiling
  coverage. `SortDepth` (bottom-face Z for ceilings) decides who hides whom within a layer.
- **Commit** — `ITagEmitter` per kind, because **the Revit call genuinely differs**
  (confirmed in `libs/RevitAPI.dll` metadata):
  - ceilings → `IndependentTag.Create(doc, symId, viewId, reference, addLeader, orientation, XYZ)`
  - rooms → `Autodesk.Revit.Creation.Document.NewRoomTag(LinkElementId, UV, viewId)` →
    returns a `RoomTag`, takes a **UV** (not an `XYZ`) and a `LinkElementId` (not a
    `Reference`)
  Putting the seam at the emitter keeps that difference out of the planner entirely.

The Options step's tag-type picker becomes per-kind when rooms land (a ceiling tag type and
a room tag type); this pass ships the ceiling row only. Nothing room-specific is written
now — only the abstraction boundaries above, so adding rooms is a new source + a new
emitter, not a refactor of the placement math.

## Tag family

The tag to use **already lives in the project**: the Options step lists the project's loaded
ceiling-tag types (`FamilySymbol`s in `OST_CeilingTags`) in a single-select picker and passes
the chosen id into `Create`. No auto-loading of the embedded `Ceiling Tag.rfa`; if the
project has no ceiling tag types the run says so and stops. The last-used type persists
**by name** (machine-wide settings never store ElementIds), resolved against the project at
run time; symbol is activated if inactive (own small transaction, as before).

The point handed to `Create` is the tag head position, and this ceiling tag family anchors
at its center, so "the center of the tag is on the ceiling" holds because every planned
point is inside the visible region. (Needs one confirmation on a Windows plot; if the family
turned out to anchor off-center, the fix is in the family, not per-tag geometry reads.)

## Settings (`CeilingTagSettings.xml`, same pattern as `CeilingHeatmapSettings`)

| Setting | Default | Notes |
|---|---|---|
| `MaxTagSpacingFt` | 30 | the "tag about every 30′" knob, `InlineStepper` |
| `ReplaceExisting` | true | delete existing ceiling tags in the view first |
| `LastTagTypeName` | "" | convenience default for the picker, name-based |

## UI

`IStepFlowTool` in `StepFlowWindow`, three steps:
1. **Views** — `BrowserTreePicker` (RCPs), same capture pattern as the heatmap.
2. **Options** — tag type picker, spacing stepper, replace toggle.
3. **Run** — review summary, Run button, log.

Per CLAUDE.md: `/revit-navisworks-ui` skill + a rendered mockup image for approval **before**
any UI code is written.

## Files

New — `Source/Tools/Ceilings/CeilingTags/`:
- `TagCore/TaggableRegion.cs` — Revit-free region DTO (kind, polygons, occlusion layer,
  sort depth) — the room-ready abstraction
- `TagCore/PolyRaster.cs` — grid model: rasterize polygon+holes, occlusion subtract,
  connected components, distance transform
- `TagCore/RegionSkeleton.cs` — thinning, centerline trace, corner split → legs
- `TagCore/TagPointPlanner.cs` — compact vs corridor, spacing subdivision, snap-to-interior
- `TagCore/TagPlan.cs` — Revit-free plan DTO + per-region diagnostics
- `CeilingRegionSource.cs` — `IRegionSource` for ceilings (read side: collect, extract
  bottom-face loops, parallel `Reference` bundle keyed like `PlannedRefBundle`)
- `CeilingTagEngine.cs` — drives sources → planner, returns plan + Revit bundles
- `CeilingTagCommit.cs` — mutation side, ceiling `ITagEmitter`
- `CeilingTagEventHandler.cs` — `IExternalEventHandler`, parked on `App`, payload cleared in
  `finally`
- `CeilingTagViewModel.cs` — step-flow tool, `IToolCleanup`
- `CeilingTagSettings.cs`

New elsewhere:
- `Source/Commands/Ceilings/TagCeilingsCommand.cs` (`DocumentKey.SetCurrent` first line,
  main-thread captures handed to the ViewModel)
- `Strings/en/ceilings.tags.json`

Modified:
- `Source/App.cs` — handler/event statics + ribbon button
- `Source/Tools/Ceilings/CeilingHeatmapEventHandler.cs` — **remove** the dead disabled tag
  path (`PlaceTags`, `PlaceCeilingTags`, `GetOrLoadTagSymbol`, `GetTagPoint`,
  `ComputeOuterLoopCentroidUV`, the tags result chip) — superseded by this tool
- `Strings/en/ceilings.heatmap.json` — drop the now-unreferenced tag keys

## Open decisions

1. **Corner sensitivity** for stretch splitting (~40° threshold) and the compact-vs-corridor
   ratio are internal constants to start; promote to settings only if real models demand it.

## Honest limits

- Performance can't be measured on Linux; the architecture removes the known regen trigger
  (interleaved reads), but the 580-tag model needs a Windows/Revit timing run to confirm.
- Curved-boundary ceilings work through tessellation + rasterization (the raster doesn't
  care), but their "stretches" follow the skeleton, which is the sensible reading of
  "corner" for curves.

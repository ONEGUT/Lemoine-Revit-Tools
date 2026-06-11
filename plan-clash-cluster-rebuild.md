# Plan — Cluster-Based Rebuild of the Clash Dimensioning Engine

## Problem

The current engine solves dimensions **one at a time** (greedy placement + worst-first repair).
It has no concept of the empty space around a group of clashes, so dense pockets produce
overlapping, badly spaced strings; callout grouping is bolted on afterwards (density clusters →
sweep → callout) and uses **model-feet** tolerances that break at callout scales. Marker arms
extend past the marker, cramped tags are left inline unless they hit a neighbour, and several
per-run toggles duplicate settings.

## Core Rebuild — Clusters as the unit of work

### 1. `ClashClusterer` (new, Revit-free, replaces `DensityClusterer` as the entry tier)

- Group clashes into **clusters** by single-link proximity measured in **paper space**
  (paper inches × `view.Scale` → model ft per view). The model-feet length tolerance is removed.
- Tolerance lives **only in Settings → Dimensions** ("Cluster grouping distance (paper in)").
  The per-run "Group reach along run (ft)" / "Line tolerance (ft)" steppers are removed.
- Within a cluster, `ClashRunGrouper` still finds collinear runs for chaining — its tolerances
  also convert to paper-space, settings-only.

### 2. Cluster working regions — the solve canvas

For each cluster, build a boundary box in view-2D:

- Seed with the **tightest possible box** around the cluster's clashes, grown to cover their
  resolved dimension targets (grid lines / slab edge / manual datum).
- Then **balloon every cluster's box outward at the same rate until neighbouring edges touch**
  — each boundary lands halfway between clusters, so every group gets an equal share of the
  surrounding empty space as its working area (isolated clusters cap at a max pad).
- The layout treats the region as a soft container: a cluster's lines/texts pay per square foot
  they spill outside it, so each group fills its own empty space.

### 3. `ClusterLayoutEngine` (rework of `GreedyLayoutEngine`)

Optimize each cluster **as a group** instead of dim-by-dim:

- Jointly assign rows/sides/offsets for all the cluster's dimensions against the free-space map:
  shared rows aligned, offsets evenly stepped, strings distributed into empty space rather than
  marching outward one at a time.
- Keep the deterministic greedy + repair machinery as the inner search, but the score gains
  group-level readability terms (even spacing within the cluster, row alignment, free-space
  utilisation) and the search runs per cluster region, not per lone dimension.
- The layout snapshot harvester keeps working (cluster id + region recorded per dim) so the
  offline SVG renderer can show region boundaries for tuning.

### 4. Callouts = clusters (one callout per cluster)

- A cluster whose **text demand exceeds its region's free space** becomes a callout candidate.
- **Minimum 8 markers**: a cluster with fewer than 8 members never becomes a callout
  (settings value, default 8).
- When a cluster is promoted to a callout: delete its parent markers, then **fully reassess** —
  re-cluster the remaining parent clashes, rebuild regions, re-run layout (iterate until no new
  callout is promoted). The callout view itself is dimensioned by the same cluster pipeline at
  the callout's scale (paper-space tolerances now scale correctly — fixes "callout dimensions
  way out of wack").

## Fixed rules (no options)

1. **Marker arms end at the marker edge.** `ClashEngine.CreateClashGraphics` /
   `CreateClashGraphicsVertical`: drop `armLen` (the 1.5× extension); cross lines span
   centre → marker edge (`mHalfW`/`mHalfH`, radius for rounds). Plan + section/elevation.
2. **Any tag wider than its crossbar is pulled off the dimension** like a chained-dimension tag
   (`ResolveSegments`: `IsCramped` → moved state always, never left inline).
3. **A tag whose own witness lines cross its text is dragged below the dimension** — new
   own-witness-vs-text check in the scorer/segment resolver forces the tag into the below-side
   column.

## Settings / UI moves

| Change | From | To |
|---|---|---|
| Remove "Scan all documents" | ClashFinder S3 + ClashElevationFinder toggles, `ShowAllDocuments` plumbing, `ClashDimensionSettings.ShowAllDocuments` | deleted (saved source filters always honoured) |
| Storey depth margin | ClashFinder S3 per-run stepper | Settings → Dimensions; persisted on `ClashDimensionSettings.StoreyMarginMm` |
| Chain aligned clash dimensions | ClashFinder S4 toggle | settings-only (`ChainAligned`, already there, default **on**) |
| Enlarged callouts | ClashFinder S4 toggle | settings-only (`DenseCalloutsEnabled`, already there, default **on**) |
| Grouping tolerances | S4 steppers, model ft | settings-only, paper inches (defaults match old 5 ft / 0.5 ft at 1:96 → 0.625" / 0.0625") |
| Callout minimum markers | — (new) | Settings → Dimensions, default 8 |

`AutoDimensionConfig` schema v6 → v7 migration converts stored model-ft knobs to paper inches.
S4 keeps: "Place dimensions after marking", destination picker, slab pick. S3 keeps: clear
previous, marker oversize.

## Files

| File | Change |
|---|---|
| `AutoDimension/ClashClusterer.cs` | **new** — paper-space cluster formation (subsumes `DensityClusterer`) |
| `AutoDimension/Core/ClusterRegion.cs` | **new** — region boundary + free-space map |
| `AutoDimension/Core/GreedyLayoutEngine.cs` | rework → cluster-scoped group optimization; fixed tag rules |
| `AutoDimension/Core/LayoutScorer.cs` | group readability terms; own-witness-vs-text hard rule |
| `AutoDimension/Core/RowPlanner.cs`, `TagColumnPlanner.cs`, `DimAnatomy.cs`, `LayoutSnapshot.cs` | cluster-aware adjustments |
| `AutoDimension/AutoDimensionEngine.cs` | pipeline rebuild: ingest → cluster → region → resolve → layout; callout survey from clusters; min-8 |
| `AutoDimension/AutoDimensionConfig.cs` | paper-space knobs, `CalloutMinClashes`, v7 migration |
| `AutoDimension/ClashRunGrouper.cs` | paper-space tolerances |
| `ClashFinder/ClashFinderEventHandler.cs` | callout-per-cluster loop with full reassessment; drop per-run overrides + `ShowAllDocuments`; storey margin from settings |
| `ClashFinder/ClashEngine.cs` | marker arms clamped to marker edge |
| `ClashFinder/ClashFinderViewModel.cs` | remove S3 scan-all/storey rows, S4 chain/callout toggles + steppers |
| `ClashElevationFinder/*` | remove scan-all toggle + `ShowAllDocuments`; storey margin from settings |
| `ClashShared/ClashDimensionSettings.cs` | drop `ShowAllDocuments`, add `StoreyMarginMm` |
| `Lemoine/GlobalSettingsWindow.Dimensions.cs` | add storey depth, paper-space cluster knobs, callout min-markers |

## Verification

- Layout snapshots (`DumpLayoutSnapshots`) + offline SVG renderer extended to draw cluster
  regions/free space — primary tuning loop, no Revit needed for the core.
- Core stays Revit-free and deterministic; build/test on Windows.

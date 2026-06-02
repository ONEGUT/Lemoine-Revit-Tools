# Plan — Auto-Dimension Tier 2: dual-axis, manual datum, chaining, leadered strings

Goal: produce clash dimensioning that matches the target image — clashes
dimensioned to slab edges in **both X and Y**, nearby in-line clashes merged
into **chained** dimensions, and cramped strings **dragged off to the side with
leaders** so they read cleanly. Plus a **manual datum** mode for when auto edge
detection picks the wrong edge.

## Where we are today
- `SlabEdgeTargetResolver` / `GridTargetResolver` resolve **one** target per
  source, always along `ViewProjection.HorizontalAxis` (paper +X). Y is never
  measured.
- `AutoDimensionEngine.BuildPlan` emits **one** `PlannedDimension` per source,
  with a **single** segment and a 2-reference array `[source, target]`.
- `GreedyLayoutEngine` already chooses `OffsetFt`, `Side`, and per-segment
  `TextState` (inline / staggered / leader-out) — but…
- `AutoDimensionCommit` only applies `OffsetFt`/`Side` to the dim line. It
  **ignores `TextState`**, so planned leaders/staggers are never realized.

## Phasing (each phase is an independent commit, all on one branch)

### Phase 1 — Dual-axis resolution (the "C" accuracy win)
- Add `Core.Vec2 Axis` to `ResolveContext` (default +X); resolvers measure along
  `ctx.Axis` instead of hard-coded `HorizontalAxis`.
- `BuildPlan` runs each source through the resolver **twice** — axis (1,0) and
  (0,1) — emitting up to two `PlannedDimension`s (X and Y) when each resolves
  within the distance cap. `TargetKey` gains an axis suffix so the two never
  collide for stale-cleanup.
- Widen `SlabEdgeTargetResolver` element scope beyond `Floor` (add structural
  floors/foundations already covered by `Floor`; optionally `RoofBase`) — config
  gated, conservative default.
- Files: `Resolvers/ResolveContext.cs`, `Resolvers/SlabEdgeTargetResolver.cs`,
  `Resolvers/GridTargetResolver.cs`, `AutoDimensionEngine.cs`,
  `AutoDimensionConfig.cs` (`bool DimensionBothAxes = true`).

### Phase 2 — Chain aligned, nearby clashes
- New `Core/DimensionChainer.cs`: after resolution, cluster `PlannedDimension`s
  that share **axis + side + a common target baseline** (collinear within
  `ChainCollinearTolFt`) and whose source anchors are **adjacent within
  `ChainMaxGapFt`**. Each cluster becomes ONE `PlannedDimension` with references
  sorted along the baseline and **one `PlannedSegment` per inter-reference gap**
  (this is what the existing per-segment cramping logic consumes).
- `PlannedRefBundle.Source` (single) → `Sources` (`List<Reference>`); commit
  builds `refArray = sources… + target`, yielding a Revit multi-segment chained
  dimension from a single `NewDimension` call.
- Files: new `Core/DimensionChainer.cs`, `AutoDimensionEngine.cs` (PlannedRefBundle),
  `AutoDimensionCommit.cs`, `AutoDimensionConfig.cs`
  (`bool ChainAligned = true`, `ChainMaxGapMm`, `ChainCollinearTolMm`).

### Phase 3 — Realize layout on the Revit dimension (drag strings off)
- In `AutoDimensionCommit`, after `NewDimension`, walk `dim.Segments` and apply
  the planned `TextState`: `LeaderOut`/`Staggered` → set
  `DimensionSegment.TextPosition` off to the side and enable the segment leader
  so the value is readable with an elbow leader (exact API — `TextPosition`
  setter + leader visibility param — verified on Windows before relying on it).
- Single-segment dims use `Dimension.TextPosition` / `LeaderEndPosition`.
- Files: `AutoDimensionCommit.cs`, possibly `Core/DimGeometry.cs` (expose the
  paper-space text anchor the commit needs to convert to world).

### Phase 4 — Manual datum mode (the "A" fallback)
- New `Resolvers/ManualDatumResolver.cs`: given one user-picked datum (its 2D
  line + `Reference`), project every source perpendicular onto it → target point
  + the datum reference. All clashes in the view dimension to that datum;
  chaining still applies along it.
- Picking happens on Revit's main thread inside the event handler:
  `uidoc.Selection.PickObject(ObjectType.Edge | Element, filter)` once per view
  (one pick, not 1,600). Picked refs flow into `ResolveContext`.
- `AutoDimensionConfig.TargetType` accepts `"ManualDatum"`; the destination
  picker gains a **Manual datum** entry.
- Files: new `Resolvers/ManualDatumResolver.cs`, `AutoDimensionEventHandler.cs`
  + `AutoDimensionRunner.cs` (pick step before BuildPlan), `ResolveContext.cs`.

## UI (WPF — invoke /revit-navisworks-ui before coding)
On the Options step of **both** the standalone Auto Dimension tool and the Clash
Finder dimension pass:
- Destination picker gains **Manual datum**.
- Toggles: "Dimension X and Y" (default on), "Chain aligned clashes" (default on).
- Steppers (mm): chain max gap, chain collinear tolerance.
Pushed through `AutoDimensionConfig` so both tools share one config.

## Risks / unknowns to verify on Windows (cannot build on Linux)
1. `DimensionSegment.TextPosition` leader behaviour — Revit auto-elbows vs needs
   an explicit leader flag; confirm before committing Phase 3.
2. `PickObject` from a modeless-tool ExternalEvent across multiple views — UX of
   sequential prompts; confirm focus returns to the viewport.
3. Multi-reference `NewDimension` ordering — refs must be sorted along the line
   or Revit rejects/var the chain.
4. Per-clash dual-axis at 1,600 clashes → up to 3,200 candidate resolutions and
   slab-geometry scans; watch performance, cache floor geometry per source doc.

## Out of scope (for now)
- Non-planar / curved slab edges.
- Auto-grouping across different target baselines.

# Plan — Run-Aware Clash Grouping for Auto-Dimensioning

## Problem

The auto-dimension grouping is wrong in two opposite ways:

1. **Over-grouping** — clashes far apart get chained into one dimension string even
   when they are not the same physical run.
2. **Under-grouping** — near-coincident clashes (~0.05″ apart) draw stacked dimensions
   that should have been merged into one.

Root cause: the pipeline has **no concept of a physical run**. Grouping is approximated
in `DimensionChainer.Build` by three independent fixed tolerances applied **per-axis**,
plus a post-hoc duplicate-collapse band-aid:

- Perpendicular bucket onto a baseline within `ChainCollinearToleranceMm` (150 mm runtime)
  — greedy `FirstOrDefault`, order-dependent (`DimensionChainer.cs:63`).
- Along-axis chain when gap ≤ `ChainMaxGapMm` (1500 mm runtime / 3000 mm config)
  — chains unrelated clashes up to ~5–10 ft apart (`DimensionChainer.cs:77`).
- `CollapseDuplicates` within `DuplicateToleranceMm` (25 mm), keyed on witness-axial
  layout equality — fails when two near-coincident clashes resolve to different
  `TargetKey`s, land in different greedy buckets, or end up in chains of different
  witness count (`DimensionChainer.cs:99`).

### Target behaviour (user's example)

3 pipes in an E–W row, 7″ apart, one pipe 0.25″ south of the other two:

- **E–W (along the run):** one **chained** dimension hitting all 3 pipes → slab edge.
- **N–S (across the run):** **one single** dimension at the run's representative offset.
  The 0.25″-off pipe is treated as "meant to be in line" and gets **no** dimension of its
  own.

## Approach

Introduce an explicit **run** model computed once from the 2D clash points, before axis
resolution, and make `DimensionChainer` group by run identity instead of re-deriving
membership per-axis from fixed tolerances.

A *run* = a cluster of clashes that travel together, with a principal (**long**) axis and
a perpendicular (**short/cross**) axis derived from the cluster geometry (PCA on the 2D
anchor points), not from the view's X/Y.

### Stage 1 — New `ClashRunGrouper` (Revit-free, 2D)

New file: `Source/Tools/T05-Clash/AutoDimension/ClashRunGrouper.cs`

- Input: `IReadOnlyList<SourceLine>` (one per clash, each carries `Anchor2d`, `SourceKey`).
- Cluster the 2D anchor points into runs:
  - Build runs by collinear-proximity: a clash joins a run if its perpendicular distance
    to the run's fitted line ≤ `RunCrossToleranceMm` **and** its along-line gap to the
    nearest run member ≤ `RunGapMm`. Re-fit the run line (least-squares / PCA) as members
    are added so the baseline is a running fit, not anchored to the first member (fixes the
    order-dependence in today's greedy `FirstOrDefault`).
  - A lone clash is a run of one.
- Output: `Dictionary<string sourceKey, RunInfo>` where
  `RunInfo { string RunId; Vec2 LongAxis; Vec2 CrossAxis; double CrossOffsetMedian; }`.
- Deterministic: clusters and members sorted by `SourceKey` / along-axis position; no hash
  ordering (matches the existing "all grouping is sorted" contract).

### Stage 2 — Wire the grouper into the engine

Edit: `AutoDimensionEngine.BuildPlan` (`AutoDimensionEngine.cs`)

- After `SourceIngest.Collect` (`:77`) and before the resolve loop (`:113`), call
  `ClashRunGrouper.Build(sources, …)` to get the `sourceKey → RunInfo` map.
- Carry `RunId` (and the run's long/cross axis) onto each `ResolvedItem` it produces in the
  resolve loop (`:121`). Add `RunId`, `RunLongAxis`, `RunCrossAxis` fields to `ResolvedItem`
  (`DimensionChainer.cs:10`).
- Log run count alongside the existing ingest/resolve logging.

### Stage 3 — Rewrite `DimensionChainer.Build` to be run-aware

Edit: `DimensionChainer.cs`

- **Group by `RunId` first**, then by axis role within the run:
  - For each run, classify each resolved item's measurement axis as **along** (dot of the
    view axis with the run long axis is dominant) or **across** (dominant with the cross
    axis).
  - **Along-run axis** → `EmitChain` over **all** run members sharing that target
    (`EmitChain` is reused unchanged; membership now comes from the run, not from
    `ChainMaxGapMm`). This yields the chained E–W string to the slab edge.
  - **Across-run axis** → emit **one** representative single dimension at the run's median
    cross offset (reuse `EmitSingle` with a representative member; members within the run's
    cross tolerance are absorbed and emit nothing). This yields the single N–S dimension and
    drops the 0.25″-off pipe's redundant dimension.
- **Remove `CollapseDuplicates`** and its helpers (`WitnessAxials`, `SameLayout`) — the run
  model makes the post-hoc collapse unnecessary. (Keep the method only if a safety net is
  wanted; default plan is to delete it.)
- Keep `ChainAligned` as the on/off switch: when off, fall back to one dimension per clash
  per axis (current ungrouped behaviour), bypassing the run grouping.

### Stage 4 — Config & knob cleanup

Edit: `AutoDimensionConfig.cs`, `ClashFinderEventHandler.cs`, `ClashFinderViewModel.cs`

- Replace the two fragile knobs with run-oriented ones:
  - `ChainCollinearToleranceMm` → `RunCrossToleranceMm` (how far off the run line a clash may
    sit and still belong; also the cross-axis "treat as in line" snap). Default ~75–100 mm
    (covers the 0.25″ ≈ 6.35 mm case with margin, but tight enough not to swallow a real
    second run).
  - `ChainMaxGapMm` → `RunGapMm` (max along-run gap between adjacent members of one run).
    Keep a sane default (~1500 mm) but it now only splits genuinely disconnected runs.
- `DuplicateToleranceMm` becomes unused → remove it (config field, handler override `:33`,
  VM mirror `:70`, and the `dupTolFt` argument threaded through
  `AutoDimensionEngine.cs:158,160` and `DimensionChainer.Build`).
- Add an `AutoDimensionConfig` schema migration (v3 → v4) mapping old field values onto the
  new names so existing `%AppData%\LemoineTools\AutoDimension.xml` files keep working.

### Stage 5 — Remove stale, unwired grouping knobs

Edit: `ClashShared/ClashDimensionSettings.cs`

- `GroupToleranceMm` (`:22`) and `ClusterGapMm` (`:25`) are dead — defined but never read by
  the chainer (legacy from an earlier dimensioning approach). Remove them so they stop
  implying behaviour that doesn't exist. Verify no UI binding references them before deleting.

## Files touched

| File | Change |
|------|--------|
| `AutoDimension/ClashRunGrouper.cs` | **new** — 2D run clustering (PCA fit, deterministic) |
| `AutoDimension/AutoDimensionEngine.cs` | call grouper after ingest; stamp `RunId`/axes onto `ResolvedItem`; drop `dupTolFt` |
| `AutoDimension/DimensionChainer.cs` | group by `RunId`; along-run chain / across-run single; remove `CollapseDuplicates` |
| `AutoDimension/AutoDimensionConfig.cs` | rename knobs, drop `DuplicateToleranceMm`, add v4 migration |
| `ClashFinder/ClashFinderEventHandler.cs` | rename overrides, drop `DimDuplicateTolMm` |
| `ClashFinder/ClashFinderViewModel.cs` | rename mirrored fields, drop dup-tol wiring |
| `ClashShared/ClashDimensionSettings.cs` | remove dead `GroupToleranceMm` / `ClusterGapMm` |

## Explicitly NOT changed

- **`ClashEngine` / marking** — markers are correctly 1-per-clash with a per-clash group
  GUID; that's the clean input the grouper relies on. No change.
- **`SourceIngest`** — still produces one source per clash. No change.
- **`GreedyLayoutEngine` / commit** — consume `PlannedDimension`s unchanged.

## Risks / open questions

1. **PCA on 2-point runs** is degenerate — for a run of 2, the long axis is just the line
   between them; for a run of 1, fall back to the view axes. Handle both explicitly.
2. **Run cross tolerance vs. real second run** — too loose merges two parallel pipe runs
   0.5″ apart into one; too tight re-introduces the 0.25″-off split. Default chosen to favour
   the user's stated intent (snap small offsets), exposed as a knob for tuning.
3. **Diagonal runs** — a run not aligned to view X/Y: the along/across classification uses the
   run's own axes, so a diagonal run dimensions sensibly, but the *target* resolvers still
   measure to slab edges along the view axes. Confirm this is acceptable (likely yes — pipes
   are nearly always orthogonal to the slab edge being dimensioned to).
4. **No Linux build** — per CLAUDE.md this cannot be compiled here; correctness is by
   inspection + the user's Windows build/test.

## Validation

- Build on Windows.
- Test the 3-pipe scenario: expect 1 chained E–W dim (3 pipes + edge) and 1 single N–S dim.
- Re-test a dense view that previously showed (a) far-apart clashes wrongly chained and
  (b) ~0.05″ stacked dims — both should resolve.

## Post-change silent-failure scan

Will run the CLAUDE.md silent-failure scan over the diff before committing and report
findings (catch blocks in the new grouper route through `LemoineLog`).

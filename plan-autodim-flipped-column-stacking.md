# Plan — AutoDimension Flipped state + column tag stacking

Implements the long-dead `SegmentTextState.Flipped` and reworks crowded-tag
placement from a sideways march into an aligned vertical column, mirrored
below the line when the dimension sits below the element. Audit item **J**.

## Behavior (confirmed with user, see diagrams)

1. **Only clashing tags move.** A cramped segment (text wider than its own
   span) moves *only when its text actually overlaps an adjacent segment's
   text*. A cramped tag whose overflow lands in empty space (a large/fitting
   neighbour) stays inline. Trigger is real text–text overlap, never "cramped"
   by itself.
2. **Clash chains group; large gaps break them.** Maximal runs of consecutive
   clashing segments form one group. A fitting segment (text ≤ gap) between two
   clashes splits the run → multiple small columns when the run is spread out.
3. **Each group → one aligned column off to the side.** Tags stack directly on
   top of one another (shared axial position just past the group's far end),
   climbing perpendicular. Arc leaders swing to the side of each tag.
4. **Direction follows the dimension-line side.** Above the element → stack up
   (`Staggered`). Below the element → stack down (`Flipped`). The geometry is
   already carried by `sign` (`DimSide`); `Flipped` makes the below case
   explicit for the model, scorer, and report.
5. **Non-crossing leaders.** Within a column the tag nearest the column (max
   axial) is lowest; farther anchors stack higher, so arcs nest.

## Changes

### `Core/GreedyLayoutEngine.cs` — `ResolveSegments`
- Replace the "every cramped segment picks inline/stagger/leader" logic with:
  default all `Inline`; for each cramped segment, set the moved state only when
  it overlaps a neighbour (`AdjacentTextOverlap`). Moved state = `Staggered`
  when `Side == Positive`, `Flipped` when `Side == Negative`. Leader-out chosen
  instead when its fixed penalty beats the moved (half-overflow) penalty.
- Add `AdjacentTextOverlap(a,b)`: `(a.TextWidthFt+b.TextWidthFt)/2 >
  (a.LengthFt+b.LengthFt)/2`.
- Fix the misleading comment (Flipped is no longer "treated as inline").

### `Core/LayoutScorer.cs`
- Cramped soft factor: `Staggered` **or** `Flipped` → 0.5 (was Staggered only).

### `Core/DimGeometry.cs`
- Widen the text band when any segment is `Staggered` **or** `Flipped`.

### `AutoDimensionCommit.cs`
- `IsMoved` includes `Flipped`.
- `ApplyTextStates`: collect moved segment indices, split into consecutive
  runs, place each run as a column.
- Replace `PlaceTag` (along-axis march) with `PlaceColumn`: shared axial anchor
  just past the group's far (+axis) end; stack each tag perpendicular from a
  base clearance, nearest-anchor lowest, bumping the level past any already
  placed tag (cross-dimension `placedTags` avoidance preserved). Direction via
  `sign`; base clearance larger for `LeaderOut`.

## Not in scope
- Merging tags from *different* dimensions into one shared column (each
  dimension still forms its own column; global clash-avoidance keeps them from
  overlapping). Revisit if dense multi-string clusters need it.

## Verification
Cannot build on Linux — needs a Windows/Revit pass on a crowded run (above and
below) to confirm arc clearance heights and that no leaders cross.

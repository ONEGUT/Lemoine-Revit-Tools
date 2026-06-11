# Plan — Size dense-area callouts to the room containing the clashes

Branch: `claude/fervent-pascal-5fay2s`, based on `claude/clash-dimensioning-optimization-7fhkob`.

## Goal

A dense-area callout (the callout tier added on the base branch) must always be a
little larger than the room(s) its clashes sit in — never a tight crop around just
the clash cluster. The rooms are usually not in the host model: on MEP projects
they live in the linked architectural model, so the lookup must search loaded
links too.

## Current behaviour

`AutoDimensionEngine.SurveyDenseAreas` builds each `DenseCalloutRequest` rectangle
from the clash cluster's view-2D bounding box plus a text-width margin
(`nominal × 0.75`). Rooms are never consulted, so a callout over a 3-ft pocket of
clashes in the middle of a room crops out the rest of the room and reads without
context.

## Changes

### 1. New `Source/Tools/T05-Clash/AutoDimension/Resolvers/RoomBoundsResolver.cs`

Read-only, Revit-aware helper that finds the room containing a world point:

- Searches the **host document first, then every loaded `RevitLinkInstance`**
  (point transformed into link coordinates via `GetTotalTransform().Inverse`).
- Phase-aware: uses the view's phase (`BuiltInParameter.VIEW_PHASE`) in the host;
  in a link, a phase **matched by name**, else the parameterless
  `GetRoomAtPoint` (last phase) — phases are per-document objects and must never
  cross documents.
- A clash anchor can sit **above the room's upper limit** (duct near the ceiling),
  so when the anchor itself misses, re-probe at the plan view's level elevation
  + 2 ft.
- Returns the room's bounding box as **8 world-space corners** (link rotation
  applied via the link transform and `BoundingBoxXYZ.Transform`), plus a label
  for the run log. Corner sets cached per room. Unplaced/unbounded rooms
  (`Area ≤ 0`) are ignored. Never throws — every Revit call is guarded and
  routed through `LemoineLog.Swallowed`.

### 2. `AutoDimensionEngine.SurveyDenseAreas`

- Build a `SourceKey → Anchor3d` map from the ingested sources.
- For each extreme cluster, look up the room at every member anchor (resolver
  built lazily, deduped per room), project the room corners into view-2D via
  `ViewProjection.To2D`, and **union them with the cluster box** before applying
  the existing margin. The callout therefore always extends a little past the
  room boundary; clashes outside any room still keep the cluster-box guarantee.
- Run-log one line per cluster: which room(s) (and which link) the callout grew
  to, or that no room was found (cluster-extent fallback).

No UI/settings change. The existing reuse path (`GetOrCreateCalloutView` crop
refresh) picks the new rectangle up automatically because it re-reads
`req.MinWorld/MaxWorld` every run. Scale selection is untouched — a larger crop
only adds breathing room around the same text demand.

## Files touched

| File | Change |
|---|---|
| `Source/Tools/T05-Clash/AutoDimension/Resolvers/RoomBoundsResolver.cs` | new |
| `Source/Tools/T05-Clash/AutoDimension/AutoDimensionEngine.cs` | grow callout rect to room bounds; update `DenseCalloutRequest` doc comment |
| `plan-callout-room-bounds.md` | this plan |

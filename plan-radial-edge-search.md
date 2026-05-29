# Plan: Radial Edge Search for Slab Dimensioning

## Problem

`CreateFloorDimension` selects slab edges using **perpendicular distance** from the
clash centre to each face plane. For a Y-facing face (normal ≈ (0, ±1, 0)) this
reduces to `|cy − origin.Y|` — it picks the edge closest in Y, ignoring X.

On irregular (L-shaped, T-shaped) slabs an edge on the far side of the building can
share nearly the same Y coordinate as the clash, giving it a tiny perpendicular
distance and causing it to be chosen over the nearby correct edge.

## Root Cause

`ClashDimensionEventHandler.cs` lines 833–843:

```csharp
double dist = Math.Abs(faceN.X * (cx - origin.X) + faceN.Y * (cy - origin.Y));
```

Uses signed projection (perpendicular distance only). No spatial proximity guard.

## Proposed Fix

### 1. Replace scoring metric

For each candidate face compute the **closest 2D XY distance from the clash centre
to the face's boundary edges** (using tessellated segments). This gives the true
spatial distance between the clash and the edge, regardless of orientation.

### 2. Add a configurable maximum search radius

Only consider a face if `closestEdgeDist ≤ EdgeSearchRadiusFt`. Among qualifying
faces, pick the one with the **minimum closest-edge distance**.

### 3. Add `EdgeSearchRadiusMm` to `ClashDimensionSettings`

Default: `10 000 mm` (10 m ≈ 33 ft). Persisted in XML so users can tune it.

## Files Changed

| File | Change |
|------|--------|
| `Source/Tools/Testing/ClashDimension/ClashDimensionSettings.cs` | Add `EdgeSearchRadiusMm` property (default 10 000) |
| `Source/Tools/Testing/ClashDimension/ClashDimensionEventHandler.cs` | (a) Add private helper `FaceClosestDist2D` that walks tessellated boundary segments; (b) replace `perpDist` scoring with `closestDist`; (c) apply max-radius filter |

## Detailed Algorithm

```
foreach face in slab solids (vertical faces only):
    closestDist = FaceClosestDist2D(face, tx, cx, cy)
    if closestDist > maxRadiusFt → skip
    if face is Y-facing:
        if closestDist < bestYDist → update bestY
    else:
        if closestDist < bestXDist → update bestX
```

`FaceClosestDist2D` helper:
```
foreach loop in face.EdgeLoops:
    foreach edge in loop:
        pts = edge.Tessellate()  // world coords
        for each segment (pts[i], pts[i+1]):
            project (cx, cy) onto segment in XY → t clamped [0,1]
            compute 2D distance
            track minimum
return minimum distance found
```

## No UI Changes Required

`EdgeSearchRadiusMm` is added as a persisted setting. It is not exposed in the wizard
UI in this change — that can be added as a follow-up if users need to tune it
interactively.

## Scope

Strictly minimal — two files, no refactoring, no new UI. The helper is a small
private static method added at the bottom of the event handler class.

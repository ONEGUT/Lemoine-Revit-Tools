# Plan — Dimension to the clashed Group 2 element's edge (folded into "To Slab Edge")

> Supersedes `plan-slab-edge-link-source-selection.md`. The clash already knows the exact partner
> element, so the link-dropdown / largest-floor-in-view machinery is unnecessary.

## Idea
The clash detector already pairs each Group 1 penetration with the exact Group 2 element it hits
(`ClashResult.Group2` carries `ElementId` + `RevitLinkInstance` + `Document`). Today that identity is
discarded. We **stamp it onto the clash marker**, recover it in the dimension pass, and dimension from
the marker out to **that specific element's** nearest edge.

"To Slab Edge" keeps its name but, when a marker carries a stamped target, means **"to the clashed
element's edge."** This is exact per-clash, scales to multiple views/levels/buildings automatically, and
generalizes to walls/beams (anything in Group 2) for free.

## Resolution precedence (slab-edge mode, per source line)
1. Explicit picked floor (`ctx.SlabScopes` non-empty) → existing scoped path (manual override, all clashes).
2. Else the source line carries a stamped Group 2 target → **new** per-element edge resolution.
3. Else → existing scan-all fallback.

## Carrying the target (no schema change)
`ClashTagSchema` stamps one pipe-delimited string: `LemoineCD|<group>`. Append a third segment
`…|<linkId>:<elemId>` (`linkId = 0` for host). Old markers (no third segment) read back as "no target"
and fall through to the fallback — fully backward compatible, no new Schema GUID.

## Files

### `Source/Tools/T05-Clash/ClashShared/ClashTagSchema.cs`
- `StampTag` gains an optional target `(ElementId linkId, ElementId elemId)`; writes the third segment.
- New `ReadTarget(Element) → (ElementId linkInstanceId, ElementId elementId)?`. `ReadGroup` unchanged
  (still reads segment 1).

### `Source/Tools/T05-Clash/ClashFinder/ClashEngine.cs`
- In `CreateClashGraphics`, pass `clash.Group2.LinkInstance?.Id ?? InvalidElementId` and `clash.Group2.Id`
  to `StampTag` for the cross-line markers (the dimension source lines).

### `Source/Tools/T05-Clash/AutoDimension/Resolvers/SourceLine.cs`
- Add `ElementId TargetElementId` and `ElementId TargetLinkInstanceId` (both `InvalidElementId` default).

### `Source/Tools/T05-Clash/AutoDimension/Resolvers/SourceIngest.cs`
- In `BuildClashSource`, call `ClashTagSchema.ReadTarget` on the marker and populate the two new fields.

### `Source/Tools/T05-Clash/AutoDimension/Resolvers/SlabEdgeTargetResolver.cs`
- New per-element path in `Resolve`: when `source.TargetElementId` is valid and no explicit pick,
  resolve faces for **that one element** and pick the nearest vertical edge along the axis.
  - Look up the doc/transform: `TargetLinkInstanceId` valid → `host.GetElement(id) as RevitLinkInstance`
    → `GetLinkDocument()` + `GetTotalTransform()` (resolves even if `IncludeLinks` is off — the user
    explicitly clashed it); else host/identity.
  - Extract vertical planar faces from the element's geometry, **recursing into `GeometryInstance`**
    so family-based Group 2 elements (beams, etc.) work, not just slabs/walls. Reuse `ProjectSegment`,
    the in-plane normal test, `LinkRefHelper.ToHostReference`, scoring, and `DistanceToSegment`.
  - Per-element face cache keyed `"<linkId>:<elemId>"` so many penetrations through one slab extract once.
  - No ambiguity guard needed (single known element); nearest edge along the axis wins.
- Existing `EnsureCache` scoped/scan-all paths unchanged. `DiagnoseSlabEdge` logging extended to the
  targeted path (element id, faces found, winner).

## Reused unchanged
- `AutoDimensionRunner`, `AutoDimensionCommit`, `SlabScope`, the wizard, and `ClashFinderEventHandler`
  inputs — the target rides on the marker, independent of run options.
- The standalone Auto Dimension tool benefits automatically (it reads the same stamped markers).
- The earlier diagnostics stay as a safety net.

## Dropped
- `SlabInViewFinder`, `SlabPreviewEventHandler`, the link dropdown, and preview UI (superseded plan).

## Verify
Cannot build on Linux. On Windows: clash MEP (Group 1) against the slabs/walls you want (Group 2) →
run with dimension pass + "To Slab Edge" → confirm each dimension reaches the exact clashed element's
edge across several views/levels/buildings, including a wall in Group 2.

## Branch
Continue on `claude/tender-allen-vbhwsv`.

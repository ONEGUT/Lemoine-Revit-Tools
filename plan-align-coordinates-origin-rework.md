# Plan — Align Coordinates: Internal-Origin-first anchor rework

## Problem

`AlignCoordinatesRunHandler`/`AlignCoordinatesViewModel` (tool "Align
Coordinates", command `T08-Coordinates`) only knows one alignment input: pick
two **same-named** grids in the host, intersect them, pick a **same-named**
level for Z, then require every link to carry grids with those exact names
too. Two real problems fall out of that:

1. It doesn't match how these projects are actually set up — disciplines are
   modeled off their own Internal Origin and linked in with Auto—Origin-to-
   Origin, so the "same location" concept should default to each document's
   own Internal Origin, not a picked grid intersection.
2. Any link that doesn't carry both exact grid names is silently unusable:
   `AlignCoordinatesCommand.CollectData` (lines 96–103) drops a link from
   `AlignCoordinatesData.Links` entirely if it has **zero** grids, and
   `AlignCoordinatesViewModel.RebuildLinks` (lines 172–210) hides any link
   missing **either** picked grid name from the Step 2 picker. That's almost
   certainly the "link missing from selection even after refreshing" you saw
   — it's a name-match filter, not a stale-data bug, so refreshing can't fix
   it.

Out of scope for this pass (per your last message): the `PublishCoordinates`
"not active document" failure. Left exactly as-is.

## Design

### `AnchorSource` (new enum) — resolved independently for the host and for
each link

- **`InternalOrigin` (default)** — Point = `(0,0,Z)` in that document's own
  internal coordinates; Direction = that document's own internal +X axis.
  Rotating a link's world-transformed +X axis back to bearing 0 and
  translating its transformed origin back to `(0,0,Z)` is exactly Revit's
  native "Auto – Origin to Origin" positioning — so this is a zero-picking
  default that matches normal project convention.
- **`GridIntersection`** — today's mechanism, unchanged, becomes a per-link
  override: two named grids, Point = intersection, Direction = grid 1's
  bearing.
- **`ProjectBasePoint` / `SurveyPoint`** — Point = that base point element's
  current position; translation only (no forced rotation — logged as such).
- **`ManualPick`** — user clicks a point, then a second point for direction,
  in the active Revit view. For a link override, the click is taken in host
  world space (snapping onto the link's linked geometry, which Revit already
  supports) and inverse-transformed through the link's *current* total
  transform into that link's internal coordinates, so it composes with the
  same rotate-then-translate math as every other source.

### `ZSource` (new enum) — independent of `AnchorSource`, host + each link

- **`InternalOriginZ` (default)** — elevation 0 in each document's own
  internal coordinates.
- **`MatchedLevel`** — today's mechanism: host picks a `Level` by name,
  matched by same name per link.
- **`ManualOffset`** — explicit numeric height difference.

### Resolution abstraction

Refactor host-resolution and `AlignOneLink` in `AlignCoordinatesRunHandler`
around one small result type:

```csharp
private readonly struct AnchorResolution
{
    public XYZ  PointInternal;
    public XYZ? DirectionInternal;   // null = don't rotate this element
}
```

resolved once for the host and once per link (that link's own source: the
run default, or its override). The existing rotate-then-translate transform
logic (lines 217–240 today) doesn't change at all — it already consumes an
internal point + internal direction and moves/rotates the link instance.
Only *how* those two values get produced changes per source.

### UI changes (`AlignCoordinatesViewModel`)

- **Step 1 "Alignment Method"** (replaces "Host Reference"): `AnchorSource`
  picker, default Internal Origin, with conditional sub-fields — Grid 1/Grid
  2 pickers only for Grid Intersection, Survey/Project choice only for Base
  Point, pick-point buttons only for Manual Pick. `ZSource` picker alongside
  it, same conditional pattern (Level pickers only for Matched Level, a
  `LemoineInlineStepper` only for Manual Offset). Keep the existing "move
  host Survey/PBP point(s)" toggle — it now targets whatever the resolved
  host anchor is, not always a grid intersection.
- **Step 2 "Links to Align"**: list **every** loaded link (this is the fix
  for the exclusion bug above — grids become optional per-link metadata for
  the override picker, never a filter). Each link defaults to "Use default
  method"; an inline override lets a specific link use a different
  `AnchorSource`, with that source's sub-fields appearing inline only when
  overridden. Rotate/Publish toggles unchanged.
- **Step 3 "Review & Run"**: summary reflects the resolved method mix, e.g.
  "14 via Internal Origin, 2 via Grid Intersection, 1 manual."

### Data/collection changes

- `AlignCoordinatesCommand.CollectData` — stop dropping links with zero
  grids from `AlignCoordinatesData.Links`.
- `CoordinatesModels.cs` — add `AnchorSource`/`ZSource` enums and a
  `LinkAlignSpec` (link id + its resolved/overridden source + params);
  replace the handler's single uniform `Grid1Name`/`Grid2Name`/`LevelName`
  payload with a per-link spec list plus the run-level defaults.

### New Revit-side pick handler (Manual Pick only)

`AlignCoordinatesPickPointHandler : IExternalEventHandler`, same shape as
the existing `ClashPickEventHandler` (`Source/Tools/T05-Clash/ClashShared/`)
— wraps `uidoc.Selection.PickPoint(...)`, catches
`OperationCanceledException` as a no-op cancel, hands the result back via an
`Action<XYZ>` callback. Registered alongside the other `T08-Coordinates`
handlers wherever `App.cs` wires up `AlignCoordinatesRunEvent` today.

## Phasing

This is a meaningful rework; I'd suggest landing it in two passes so the
first review stays manageable:

- **Phase 1 (this branch)**: `AnchorSource.InternalOrigin` (default) +
  `AnchorSource.GridIntersection` (existing, now a per-link override) + the
  matching `ZSource` split + the Step 2 "show every link" fix. This alone
  fixes both problems you raised — origin-first default, no more silent
  link exclusion.
- **Phase 2 (follow-up)**: `ProjectBasePoint`/`SurveyPoint` and `ManualPick`
  sources — these need the new pick-point handler and a two-click UI flow,
  and are lower-value for the common (properly-set-up) case.

Say the word if you'd rather I do all four sources in one pass instead.

## Files touched

- `Source/Tools/T08-Coordinates/AlignCoordinatesRunHandler.cs` — anchor
  resolution refactor, per-link spec list
- `Source/Tools/T08-Coordinates/AlignCoordinatesViewModel.cs` — new Step 1
  fields, per-link override UI in Step 2
- `Source/Tools/T08-Coordinates/CoordinatesModels.cs` — new enums,
  `LinkAlignSpec`, drop the "must have grids" filter
- `Source/Commands/T08-Coordinates/AlignCoordinatesCommand.cs` —
  `CollectData` stops filtering out zero-grid links
- `Source/Tools/T08-Coordinates/CoordinatesGeometry.cs` — unchanged, maybe
  one small helper for the Internal Origin direction constant
- Phase 2 only: new `AlignCoordinatesPickPointHandler.cs`, `App.cs` wiring

## Silent-failure discipline to hold while implementing

- Removing the zero-grid link filter must not let a link with an
  unresolvable `AnchorSource` (e.g. overridden to Grid Intersection but the
  chosen grids don't exist in that link) silently no-op — every link still
  logs Aligned/Skipped/Failed with a reason, same as today.
- `ManualPick` with two coincident/near-coincident points must be rejected
  with a clear message, not divide into a garbage bearing.

## UI work note

Per CLAUDE.md, the `/revit-navisworks-ui` skill gets invoked (and a mockup
rendered for approval) before any WPF code is written for the new Step 1/
Step 2 layouts — not part of this planning pass.

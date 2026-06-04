# Plan — Clash Finder for Sections & Elevations (elevation-tag variant)

## Goal
A sibling of the existing **Clash Finder & Dimension** tool that runs in **section
and elevation views**. Same clash detection and coloured round-fill marker, but
instead of placing a dimension to grid/slab, it places an **elevation tag** at the
**top / centre / bottom** of each round-fill area (user-selectable).

## What already works and is reused unchanged
- `ClashEngine` detection: group scanning, solid–solid boolean intersection,
  bbox fallback, clash limit — all world-coordinate, view-agnostic.
- `ClashTagSchema` Extensible-Storage tagging of markers (for clear-previous + grouping).
- `ClashDefinitionsSettings` saved definitions, `LemoineMultiSelectTabs`,
  `LemoineToggleSwitches`, `LemoineInlineStepper`, `StepFlowWindow` STA host pattern.

## What is plan-specific today and must change
1. **Round-fill geometry** — `ClashEngine.CreateClashGraphics()` builds the circle
   in the world **XY** plane (`Arc.Create(ctr, r, 0, π, BasisX, BasisY)`,
   `ctr = (cx, cy, 0)`). For a section/elevation the loop must live in the view's
   **vertical cut plane**, built from `view.RightDirection` / `view.UpDirection`
   and the clash centre projected into view 2D. (This is exactly the
   "projection change, not a core rewrite" the `ViewProjection` comment predicted.)
2. **Annotation** — replace the AutoDimension pass with an **elevation-tag pass**.
3. **View collection** — command gathers **Section + Elevation** views, not plans.
4. **View-volume gate** — uncropped sections/elevations already return "no gate"
   (`TryGetViewWorldBox`), so clashes mark wherever they fall in the view. Cropped
   sections still get XY gating. Acceptable for v1; no change needed.

## Proposed architecture (mirrors the existing tool, no duplication of detection)
- `Source/Commands/T05-Clash/ClashElevationFinderCommand.cs`
  Collects `ViewType.Section` + `ViewType.Elevation` views; launches `StepFlowWindow`.
- `Source/Tools/T05-Clash/ClashElevationFinder/ClashElevationFinderViewModel.cs`
  Steps: **Select Definitions → Select Views → Options & Run**. New options:
  tag anchor (Top / Centre / Bottom), elevation datum, spot-elevation type, round size,
  clear-previous.
- `Source/Tools/T05-Clash/ClashElevationFinder/ClashElevationFinderEventHandler.cs`
  Detection transaction (reusing `ClashEngine`) then the elevation-tag pass.
- Generalise `ClashEngine.CreateClashGraphics()` to be view-orientation-aware so the
  round fill is drawn in any view's plane. Plan output stays byte-identical
  (right=BasisX, up=BasisY), so the existing Clash Finder is unaffected.
- `Source/Tools/T05-Clash/ElevationTag/` — new placement pass (runner + commit),
  tagging each tag via `ClashTagSchema`. Clear-previous extended to also delete
  tagged `SpotDimension` elements.
- `App.cs` — register a new handler/event + a ribbon button under the **T05 Clash** panel.

## Open design decision (asking before coding)
**How to realise the "elevation tag":** native Revit `NewSpotElevation` (live, reads
like the drawing, but needs a valid geometry `Reference`) vs a computed elevation
`TextNote` (fully controllable, robust for linked elements, but not auto-updating).
See chat for options + recommendation.

## Build / test note
Windows-only build (net48 + UseWPF). Cannot compile or run on Linux — code will be
written to the project's conventions and the post-change silent-failure scan run, but
final compile/test is on the user's Windows machine.

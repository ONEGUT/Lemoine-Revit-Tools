# Plan — Streamlined Slab-Edge Targeting: pick a link, auto-find largest floor in the view box

## Goal
Replace the unpredictable scan-all default for "To Slab Edge" with an explicit, scrutable flow:
**pick a slab source (Host model or a loaded link) → for each view, auto-select the largest floor whose
footprint falls in that view's crop box, level-aware → preview the per-view choice before running.**

This reuses the **scoped resolver path the user confirmed works** (pick → one floor in one doc): we only
change *how* the per-view `SlabScope` is produced — from a manual canvas pick / silent scan-all to a
deterministic "largest floor in the view box of the chosen source."

## Decisions (confirmed)
1. Link selection: **dropdown** in the wizard (Host model + each loaded link).
2. Floor rule: **largest by area whose footprint overlaps the view crop box, preferring floors at/below
   the view's level within the storey margin** (so a stacked floor from another level isn't grabbed).
3. Scrutability: **in-wizard "Preview" button** lists the chosen floor (name + area) per selected view.
4. Manual specific-floor pick: **kept** as an override/fallback.

## Resolution precedence (per view, slab-edge mode)
1. Explicit picked floor (`_pickedSlab`) → use it for every view (current behaviour).
2. Else a chosen source (Host / link) → `SlabInViewFinder` resolves the largest floor in *that view's* box.
3. Else "Scan all floors" → legacy scan-all (unchanged).

## New files
### `Source/Tools/T05-Clash/AutoDimension/SlabInViewFinder.cs`
Revit-main-thread helper. One method:
```csharp
public sealed class SlabPick { public SlabScope Scope; public string FloorName; public double AreaSqFt; public string Reason; }
public static SlabPick? LargestFloorInView(Document host, View view, RevitLinkInstance? link, double storeyMarginMm);
```
- Source doc/transform: host → (host, Identity); link → (link.GetLinkDocument(), link.GetTotalTransform()).
- View box: if `view.CropBoxActive`, project the 8 corners of `view.CropBox` (via its `Transform`) to world,
  take the XY rectangle. If no active crop → no XY filter (whole source), still level-filtered.
- Level: `(view as ViewPlan)?.GenLevel?.Elevation`. Prefer floors whose top (`bbox.Max.Z`, link-transformed)
  is within `storeyMarginMm` of the level; if none qualify, fall back to all crop-overlapping floors.
- Floors: `FilteredElementCollector(sourceDoc).OfClass(typeof(Floor))`; transform each bbox to world,
  test XY overlap with the crop rectangle. Area from `BuiltInParameter.HOST_AREA_COMPUTED` (fallback: bbox XY area).
- Pick the largest by area; return its `SlabScope { LinkInstanceId = link?.Id ?? Invalid, FloorId = floor.Id }`,
  name, area, and a human reason (e.g. "largest of 3 in-crop floors at level 02"). Null + reason when none.
- All Revit calls guarded; failures via `LemoineLog.Swallowed/Warn` (no silent drops).

### `Source/Tools/T05-Clash/AutoDimension/SlabPreviewEventHandler.cs`
`IExternalEventHandler` for the wizard's Preview. Inputs: `ViewIds`, `SourceMode` (Host/Link/None),
`SourceLinkId`, `StoreyMarginMm`. Runs `SlabInViewFinder` per view; returns `Action<IList<(string view, string result)>>`
via an `OnResult` callback (marshalled back to the wizard dispatcher). Pure read — no PickObject, no focus steal.

## Modified files
### `Source/Tools/T05-Clash/ClashFinder/ClashFinderViewModel.cs`
S4 slab section becomes:
- **Slab source** `LemoineSingleSelect`: items `["Scan all floors", "Host model", <link names…>]`, default
  "Scan all floors". Selection sets `_slabSourceMode` (`ScanAll`/`Host`/`Link`) + `_slabSourceLinkId`.
- **Preview slab per view** button → raises the preview event; result populates a per-view status list
  (`view → "Floor 2 Slab (4,210 sf)"` / `"none in crop"`).
- **Advanced — pick a specific floor**: keep existing "Pick host slab…/Pick linked slab…/Clear"; when a
  specific floor is picked it overrides the source (status shows "Specific: …").
- New ctor params: `List<(ElementId id, string name)> links` and the preview handler/event.
- `Run()` sets new handler fields: `SlabSourceMode`, `SlabSourceLinkId` (plus existing `SlabScopes`).
- Review chips/values updated to show the source ("slab src: <link>") instead of just "all floors".

### `Source/Tools/T05-Clash/ClashFinder/ClashFinderEventHandler.cs`
- New inputs: `string SlabSourceMode` (`"ScanAll"|"Host"|"Link"`), `ElementId SlabSourceLinkId`.
- In the dimension pass (SlabEdge): if `SlabScopes` non-empty → as today. Else if source is Host/Link →
  for each view resolve `SlabInViewFinder.LargestFloorInView` (link looked up by `SlabSourceLinkId`),
  build the per-view `slabScopes` dict, and `Log` each pick/miss. Else → null (scan-all).

### `Source/Commands/T05-Clash/ClashFinderCommand.cs`
- On the main thread, collect loaded links: `FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance))`
  with `GetLinkDocument() != null` → `(Id, Name)` list. Pass to the VM ctor.
- Pass `App.SlabPreviewHandler` / `App.SlabPreviewEvent`.

### `Source/App.cs`
- Register `SlabPreviewHandler` + `SlabPreviewEvent` next to `SlabPickHandler`.

## Reused unchanged
- `AutoDimensionRunner.Run` already accepts a per-view `slabScopes` dict — no change.
- `SlabScope` and the scoped branch of `SlabEdgeTargetResolver.EnsureCache` — no change (this is exactly
  the path that already works when a floor is picked).
- The diagnostics added earlier remain: if the auto pick still misbehaves, flip `DiagnoseSlabEdge`.

## Out of scope
- The standalone Auto Dimension tool's own UI (this targets the Clash Finder wizard, as requested).
  The shared helper makes adding it there trivial later.

## Verify
Cannot build on Linux. On Windows: open Clash Finder → Dimensioning → choose a link as slab source →
Preview (confirm it lists the right floor per view) → run and confirm dimensions reach the linked slab.

## Branch
Continue on `claude/tender-allen-vbhwsv` (same feature area as the diagnostics already pushed).

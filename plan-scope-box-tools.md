# Plan — Scope Box Tools + Bulk View Creator Rework

## Goal

Move the room-search/clustering and slot-naming logic out of **Bulk Views by Level** into a new
**Scope Box Creator**, add a **Scope Box Manager**, and simplify the view creator to create views
**by level or by scope box only** (no more area searching).

## Feasibility constraint (drives the design)

The Revit API **cannot create a scope box from scratch** (confirmed through 2025; no 2026 API found).
The only viable path is **duplicating an existing seed scope box** (`ElementTransformUtils.CopyElement`),
then renaming/moving/rotating the copies. Community consensus: Name, Position, Rotation settable;
Height likely settable via parameter; **Width/Depth resizing is disputed** and must be probed
empirically per Revit year before the Creator's sizing behavior is locked.

User decisions (from Q&A):
- Creator uses **seed + duplicate**, seed **picked from a list** of existing boxes (told to draw one if none exist); seed left untouched.
- Box layout: **both modes, chosen per run** — one box per building cluster at full height, or one box per cluster per level.
- Box naming: **Front/Center/End slot system** with box tokens (Building letter, Level, Level Range, Model Name, Custom) + live preview.
- Manager features: **bulk assign to views, bulk rename, assign to grids/levels/ref planes, usage report + cleanup**.
- View creator: **drop room searching entirely**; modes = By Level / By Scope Box; scope-box mode **assigns the Scope Box parameter** (plans) and copies box bounds into the 3D section box; **keep** naming step, view templates + sub-discipline, 3D views; **drop** print sets and the buffer/cluster geometry settings.

## Work items (implementation order)

### 1. Extract shared logic (no behavior change yet)
- `Source/Tools/T10-ScopeBoxes/RoomClusterSearch.cs` — move `RoomInfo`, `CollectRooms`,
  `AssignHostLevelsByElevation`, `ClusterRooms`, `ClusterBoundsXY`, `BldgLetter` out of
  `LinkViewsLevelHelpers` (view helpers keep only view-specific pieces).
- `Source/Lemoine/Controls/LemoineNamingSlots.cs` — reusable Front/Center/End slot control
  (configurable token list + preview callback), extracted from the S3 naming UI so the view
  creator, scope box creator, and manager rename all share one implementation.

### 2. Probe harness (Developer panel, `Source/Tools/Debuggers/ScopeBoxProbe/`)
`ILemoineTool` in `StepFlowWindow`, each suspect behind its own button (per CLAUDE.md crash/debug
discipline). Buttons: dump all parameters of a picked scope box; copy + rename; move; rotate;
set Height param; attempt Width/Depth writes; attempt uniform-scaled copy transform; verify
name-uniqueness behavior. Results go to the run log. **The probe's Windows results gate how the
Creator sizes boxes** — until then the Creator logs the exact required footprint for any box whose
size differs from the seed so the user can adjust handles manually.

### 3. Scope Box Creator (`Source/Tools/T10-ScopeBoxes/`, new step-flow tool)
- **S1 — Sources**: document multi-select (host + links, same as today) + Buffer XY / Cluster
  Threshold steppers (these settings move here from the view tool; persisted in new `ScopeBoxSettings`).
- **S2 — Scan & layout**: room scan → clusters (reusing RoomClusterSearch, same Phase1
  ExternalEvent pattern); level multi-select; mode toggle **Full building height** (one box per
  cluster, lowest→highest selected level) vs **Per level** (one box per cluster per level, height =
  floor-to-floor). "Found N clusters / no rooms found" logged explicitly.
- **S3 — Seed & naming**: seed box picked from a list of existing scope boxes (clear message +
  blocked run when none exist); naming slots with tokens Building Letter / Level / Level Range /
  Model Name / Custom + live preview.
- **S4 — Review & Run**: duplicate seed per computed box inside one transaction, rename
  (pre-check name collisions, skip-and-log), move to computed center, set height where the API
  allows, rotate not needed for axis-aligned cluster bounds. Any box whose computed width/depth
  differs from the seed's is logged with the exact required size ("resize manually to W × D").
  Cancellation + ~5% progress cadence per house rules.

### 4. Scope Box Manager (`Source/Tools/T10-ScopeBoxes/`, second step-flow tool)
- **S1 — Boxes & usage**: list every scope box with usage counts — views using it (via
  `BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP`) and datums (grids/levels/ref planes via the
  datum scope-box parameter); flag unused boxes.
- **S2 — Action** (single-choice): *Assign to views* (LemoineBrowserTreePicker for view pick,
  set/clear the parameter in bulk — plan views only; non-plan views hidden per UX rule),
  *Assign to grids/levels/ref planes*, *Bulk rename* (naming slots), *Delete unused* (explicit
  confirm; report exactly what was deleted).
- **S3 — Review & Run.**

### 5. Bulk Views by Level rework (`Source/Tools/T03-LinkViews/`)
- Remove the room scan/cluster phase (Phase1 handler goes away or shrinks to a level+scope-box scan).
- **Mode choice per run**: **By Level** (one view set per selected level, uncropped — today's
  no-rooms fallback becomes the only level behavior) or **By Scope Box** (pick scope boxes;
  FP/RCP created per box × level with the Scope Box parameter assigned; 3D gets its section box
  copied from the box bounds since 3D views can't take the parameter).
- Keep: 3D/FP/RCP toggles, per-type view template + Sub Discipline, naming step (slot control,
  tokens now Level / Scope Box Name / Model Name / View Type / Custom).
- Drop: print sets, Buffer XY / Cluster Threshold / building-label logic.
- Ordering rule respected: template assigned **before** crop/section-box geometry.

### 6. Wiring & text
- `App.cs`: new ExternalEvent handlers (creator phase1 + run, manager scan + run, probe) with
  per-run payload clearing in `finally`; ribbon: "Scope Boxes" pulldown (Creator | Manager) on the
  Views panel (respecting the AddStackedItems 2/3-item constraint); probe behind the reserved
  Developer button.
- Strings: `Strings/en/scopeBoxCreator.json`, `scopeBoxManager.json`, `scopeBoxProbe.json`;
  update `linkviews` strings; key-existence verification pass before commit.
- New `ScopeBoxSettings` (public DTO, XmlSerializer rule) for buffer/threshold/naming defaults.

## Files touched (summary)
- **New**: `Source/Tools/T10-ScopeBoxes/*` (creator VM/handlers, manager VM/handlers, RoomClusterSearch),
  `Source/Tools/Debuggers/ScopeBoxProbe/*`, `Source/Lemoine/Controls/LemoineNamingSlots.cs`,
  `Source/Commands/T10-ScopeBoxes/*`, `ScopeBoxSettings`, three `Strings/en/*.json`.
- **Modified**: `LinkViewsLevel*` (rework), `LinkViewsLevelHelpers.cs` (slimmed), `App.cs`,
  `GlobalSettingsWindow.ToolGroups.cs`, `Strings/en/linkviews*.json`.

## Risks / notes
- **Width/depth sizing is the open risk** — mitigated by the probe harness plus explicit
  "resize manually to W × D" run-log lines; design adapts once probe results come back from Windows.
- Cannot build on Linux — compile verification happens on the user's Windows machine per year
  configuration.
- UI work goes through the `/revit-navisworks-ui` skill with mockup-first approval per house rules.

# Plan — Project Zones ("view templates for 3D space")

**Status:** proposal, awaiting approval. No code written yet.
**Branch:** `claude/revit-3d-view-templates-wwq3sk` (base TBC — see §12).

**Decisions taken** (§12): concept is **Zone**; structure is the **Areas × Levels matrix**;
extents default to **adopting existing scope boxes**; sheet placements are **solved by default
and capturable to override**.

---

## 1. What this is

A **Zone** is a named, per-project record describing a chunk of the building in 3D space
*plus the documentation conventions that go with it*. It is stored in the `.rvt` only
(Extensible Storage), so it travels with the project and every team member sees the same one.

The gap it fills, stated precisely:

| Revit already gives you | What it does NOT carry |
|---|---|
| **View template** | graphics, visibility, some view parameters — but **no position, no extent, no Z range, no scale-that-fits, no sheet location** |
| **Scope box** | an XY extent + a Z span — but **no view range, no scale, no naming, no sheet position, no per-view-type recipe** |
| **Title block** | a paper size — but **no relationship to a place in the building** |

A Zone is the missing record that ties all three together and adds the one thing nothing in
Revit stores: **where this piece of the building lands on a sheet, in exact sheet
coordinates, for a given sheet size.** It *references* a Revit view template for graphics
rather than replacing it — Zones and view templates are complementary, not competing.

The one-line test for whether something belongs in a Zone: *does it describe a place in the
building, or how that place gets drawn?* If yes, it is Zone data. If it is graphics (line
weights, category visibility, filters), it stays in a Revit view template and the Zone just
names it.

---

## 2. The addressing model

Two independent axes, with a sparse override matrix:

```
Building  ──┬── Level   (the Z axis:  "L01", "L02", "Roof")
            └── Area    (the XY axis: "Area 1", "East Wing", "Core")

Cell = (Building, Level, Area)          ← the addressable unit tools ask for
```

Areas are **not** duplicated per level. `Area.AppliesToLevelIds` says which levels an area
exists on, and a sparse `ZoneCell` record overrides a single (Area, Level) pair when it needs
its own scope box or extents (e.g. Area 1 is bigger on L01 than on L05). A building with a
uniform footprint therefore has *no* cell records at all — the matrix stays empty until
something genuinely differs.

Rejected: nesting areas under levels (duplicates every area N times and makes "rename Area 1"
an N-place edit) and nesting levels under areas (makes "what is on L03?" a scan of every area).

---

## 3. Data model

New file set under `Source/Framework/Zones/` (framework, not `Tools/` — many tools consume it):

```
ZoneLibrary                       root DTO, XmlSerializer, PUBLIC type (CLAUDE.md)
├─ SchemaVersion : int
├─ Buildings   : List<ZoneBuilding>
├─ Levels      : List<ZoneLevel>
├─ Areas       : List<ZoneArea>
├─ Cells       : List<ZoneCell>            sparse overrides only
├─ Recipes     : List<ZoneViewRecipe>
└─ Placements  : List<ZoneSheetPlacement>
```

### 3.1 `ZoneBuilding`
`Id` (GUID), `Name`, `Code`, `SortIndex`.

### 3.2 `ZoneLevel` — the Z axis
| Field | Notes |
|---|---|
| `Id` (GUID), `Name`, `Code` | |
| `HostLevelName` | **name, never ElementId** — CLAUDE.md's cross-document identity rule |
| `SourceLinkKey`, `SourceLevelName` | provenance when discovered from a link |
| `ElevationFt` | recorded for the elevation fallback match, and shown in the UI |
| `BuildingId` | |
| `BandBaseOffsetFt`, `BandTopOffsetFt` | the **3D band**: where this level's box bottom/top sit relative to the level. Drives section boxes and scope-box Z. |

### 3.3 `ZoneArea` — the XY axis
| Field | Notes |
|---|---|
| `Id`, `Name`, `Code`, `BuildingId` | |
| `Definition` | `ScopeBox` *(default)* \| `Grids` \| `Manual` \| `RoomCluster` (see §4) |
| `ScopeBoxName` | the adopted/driven scope box; **name is the key**, id is stamped (§6) |
| `GridXMin/XMax/YMin/YMax`, `GridMarginFt` | when `Definition = Grids` |
| `MinX/MinY/MaxX/MaxY` | resolved world extents, cached from the last solve; re-checked against the live box on load (§4.1) |
| `AnchorMode` | `ExtentsCentre` *(default)* \| `GridIntersection` \| `Manual` — orthogonal to `Definition` (§4.2) |
| `AnchorGridX`, `AnchorGridY`, `AnchorX`, `AnchorY` | the world point that pins this area to a sheet |
| `AppliesToLevelIds` | |

### 3.4 `ZoneViewRecipe` — the "3D view template" payload
One recipe per view type you want a zone to be able to produce.

| Field | Notes |
|---|---|
| `Id`, `Name` | "Floor Plan", "RCP", "3D Coordination", "Demolition Plan" |
| `Kind` | `FloorPlan` \| `CeilingPlan` \| `ThreeD` \| `Section` \| `AreaPlan` (maps to `ViewFamily`) |
| `ViewFamilyTypeName`, `ViewTemplateName` | **names**, resolved to ids at run time |
| `ScaleMode` | `Fixed` \| `FitToTitleBlock` |
| `Scale` | integer denominator (`View.Scale`) when `Fixed` |
| `ViewRange` | `ZoneViewRange`, below — plans only |
| `Discipline`, `DetailLevel`, `PhaseName` | optional overrides |
| `AnnotationCropPaperFt` | paper gap → converted with `view.Scale` (CLAUDE.md) |
| `NamePattern` | a `TokenInput` pattern |
| `SectionBoxFromBand` | 3D only: use the level band for Z |

### 3.5 `ZoneViewRange` — the RCP/floor-plan payload
Five planes, each a `(LevelRef, OffsetFt)` pair, mapping **directly** onto the confirmed API:

```
PlanViewRange.SetLevelId(PlanViewPlane.CutPlane,      <levelId | Current | LevelAbove | LevelBelow | Unlimited>)
PlanViewRange.SetOffset (PlanViewPlane.CutPlane,      offsetFt)
```

`PlanViewPlane` values confirmed present in `libs/RevitAPI.dll`: `CutPlane`, `TopClipPlane`,
`BottomClipPlane`, `ViewDepthPlane`, `UnderlayBottom`. `PlanViewRange` exposes
`GetLevelId` / `SetLevelId` / `GetOffset` / `SetOffset` and the four special level tokens as
properties `Current`, `LevelAbove`, `LevelBelow`, `Unlimited`. `ViewPlan` exposes
`GetViewRange`, `SetViewRange` and **`CheckPlanViewRangeValidity`** — that last one is used to
validate a stored range *before* writing it, so an impossible range is reported rather than
thrown.

`LevelRef` is stored as a token (`"Current"`, `"Above"`, `"Below"`, `"Unlimited"`, or a level
**name**) — never an ElementId.

### 3.6 `ZoneSheetPlacement` — the exact-location record
**One record per (Area, TitleBlockType).** This is the answer to *"any sheet that has that
area is always placed on that sheet size in the exact correct location."*

| Field | Notes |
|---|---|
| `AreaId` | |
| `TitleBlockTypeName` | the key — a different sheet size is a different placement |
| `SheetWidthFt`, `SheetHeightFt` | recorded from `BuiltInParameter.SHEET_WIDTH` / `SHEET_HEIGHT` (both confirmed present), used to detect a title block that changed under us |
| `AnchorWorldX/Y` | the area's world anchor (§3.3) |
| `AnchorSheetX/Y` | **the sheet coordinate that world point must occupy** |
| `Scale` | the scale this pair was solved/captured at |
| `Source` | `Solved` \| `Captured` |
| `CapturedFromSheetNumber`, `CapturedUtc` | provenance when captured off a real sheet |

Placing *any* view of that area on *any* sheet carrying that title block then reduces to one
formula — the one this repo already has, debugged, in `AlignSheetViewsEventHandler`:

```
boxCentre = AnchorSheet − rotate( (localAnchor − footprintCentre) / scale )
```

See §5 for how that gets shared rather than re-written.

---

## 4. How an Area's extents get defined

Four modes. **`ScopeBox` is the default** — a project that already has boxes drawn is the
common case, and adopting them means a zone library can be stood up in one pass over what
exists rather than re-authoring it.

| Mode | Source | Why |
|---|---|---|
| **ScopeBox** *(default)* | adopt an existing box's extents | Zero re-authoring on a project that is already set up. The box stays the source of truth for XY; the zone adds everything the box cannot hold. |
| **Grids** | grid bubble names — "A→F × 1→8" + margin | How people describe areas verbally, and re-solvable when the model moves. Reads grids from host or link. |
| **RoomCluster** | `RoomClusterSearch` union-find over link rooms | Already implemented and shipping in the Scope Box Creator — reused verbatim for auto-discovery. |
| **Manual** | typed coordinates / picked points | Escape hatch. |

### 4.1 Adoption is a live link, not a one-time copy
Adopting a box records `ScopeBoxName` and caches its extents. On every load the cached extents
are re-read from the named box, and a **divergence is reported, never silently corrected**:
the box may have been resized by someone who never opened this tool. Three outcomes, all
logged: box unchanged → nothing; box resized → warn, offer re-adopt; box missing or renamed →
warn and mark the area unresolved (never delete it — the library outlives a deleted box).

### 4.2 Anchor mode is orthogonal to extent mode — and this matters
The anchor is **not** forced to follow the definition. `AnchorMode` is a separate choice, and
`GridIntersection` is available (and recommended) even when `Definition = ScopeBox`.

The reason is worth stating plainly: if the anchor is the extents centre, resizing a scope box
by a foot silently moves every sheet placement built from that area — drawings that were
correct yesterday shift with no error anywhere. A named grid intersection does not move when
extents change. So the default is `ExtentsCentre` (it works with no extra input and matches
what adoption implies), but the Zone Manager surfaces "pin anchor to grid intersection" on
every area and **warns when an adopted box's extents change while the anchor is
`ExtentsCentre`**, because that is exactly the case where placements drift unannounced.

Per CLAUDE.md's bucketing rule: extents solved from grids are the **exact** grid coordinates
plus the user's exact typed margin. Nothing is snapped to a tolerance grid, and no bucket
midpoint is ever persisted or shown.

---

## 5. Scale-fit and sheet placement

### 5.1 `ZoneScaleFit` — Revit-free, testable
```csharp
ZoneScaleFit.Solve(extentsW, extentsD, drawingAreaW, drawingAreaH, ladder)
    → (scale, fits, slackXft, slackYft)
```
Picks the largest scale off the standard ladder where `extents/scale` fits the drawing area.
The ladder is **hardcoded** — CLAUDE.md records that Revit exposes no API to enumerate its
predefined scales.

Kept Revit-free and pure so it can be exercised against real numbers the way the ceiling-tag
placement core was, rather than only being observed inside Revit.

### 5.2 Drawing area
- **Preferred:** the placed title block instance's bounding box, minus per-side margins —
  `PlaceDependentViewsEventHandler.TryGetDrawingArea` already does exactly this, and is valid
  only after a regen.
- **Fallback (no sheet exists yet):** `SHEET_WIDTH` / `SHEET_HEIGHT` on the title block
  **type**, minus margins.

Both paths, preferring the measured one, and the fallback is reported in the log so an
estimate is never mistaken for a measurement.

### 5.3 Sheet-anchor math — **extract, do not re-write**
`AlignSheetViewsEventHandler` already contains the correct, hard-won world→sheet math:
`TargetBoxCentre`, `SourceAnchorOnSheet`, `FootprintCentre`, `ApplyRotation`, `AnchorWorld`.
It handles annotation-crop asymmetry, per-side scale, and viewport rotation.

Plan: lift those into `Source/Framework/Zones/SheetAnchorMath.cs` (a plain static class taking
values, not Revit objects, wherever it already does) and have **both** Align Sheet Views and
the Zone placement path call it. Align Sheet Views' behaviour must not change — the extraction
is a move, and its existing call sites keep passing the source viewport's anchor; the Zone path
passes the *stored* anchor instead. One formula, two anchor sources.

### 5.4 Authoring a placement — two ways
1. **Solve** — centre the area in the drawing area at the fitted scale. Deterministic, zero
   user effort, available before any sheet exists.
2. **Capture** — the user places one view by hand on a real sheet, nudges it until it is right,
   and hits *Record placement from this sheet*. The viewport is read back and the anchor pair
   stored. This is what makes the location genuinely *theirs* rather than merely centred.

Capture obeys the constraints already recorded in CLAUDE.md:
- `GetBoxCenter` / `GetBoxOutline` are valid only after a `doc.Regenerate()` following
  `Viewport.Create` **or** a `SetBoxCenter` — so capture regenerates first.
- Those outlines **track the view title**, so a placement is never inferred from a centre
  comparison alone; capture reads the box centre it is given, it does not diff two centres.

### 5.5 Revit's own `ViewportPositioning` — considered
`Viewport.ViewportPositioning` (`ViewportCenter` | `ViewOrigin`) is confirmed present, and
`ViewOrigin` is Revit's native "same view lands in the same spot" mechanism. It is **not**
sufficient here: it pins to the sheet origin, so it cannot express "this area sits here on an
A1 and there on an A3", and it offers no control over where. Zones keep `ViewportCenter` and
own the position via `SetBoxCenter`.

**UNVERIFIED — needs a Windows/Revit run:** whether leaving a viewport on `ViewOrigin` causes
Revit to fight a subsequent `SetBoxCenter`. If it does, the Zone writer must force
`ViewportCenter` before positioning. Flagged rather than assumed.

---

## 6. Storage

**Extensible Storage only**, exactly as the user asked, and the mechanism already exists.

`ProjectLibraryStore` (`Source/Framework/Project/`) holds one `DataStorage` element per
document with **one field per library section** — deliberately separate fields so two open tool
windows cannot overwrite each other. Adding Zones is:

1. `ProjectLibraryStore.SectionZones = "Zones"`, added to `AllSections`.
   *Backwards-compatible in both directions:* an older build reading a newer file ignores the
   unknown field, and a newer build reading an older file gets `""`, which the loader treats as
   "seed me", not "empty library".
2. `ZoneSettings.LoadProjectLibrary(xml)` / `SerializeProjectLibrary()`, following
   `LegendCreatorSettings` line for line.
3. Register in `App.OnStartup` beside the other three sections.
4. Every Zone-aware command calls `DocumentKey.SetCurrent(doc)` then
   `ProjectLibraries.LoadForDocument(doc)` as its first two lines — already the house pattern.

**Nothing Zone-related goes in `%AppData%`.** No ElementIds are persisted anywhere: levels and
scope boxes are keyed by **name**, per CLAUDE.md's rule that `%AppData%`-shaped mistakes have
already shipped four times and once deleted another project's elements.

**Scope-box ownership** uses the stamp pattern rather than a settings list: a `ZoneOwnerSchema`
Extensible Storage entity written onto each scope box a Zone creates, carrying the area's GUID.
The document becomes authoritative, lookup runs document→library, and a box the user deletes
simply stops appearing. Stamps are written **inside the run's existing transaction**.

> **Carried forward from CLAUDE.md:** no existing ES schema in this repo calls
> `SchemaBuilder.SetVendorId`, and every `Finish()` sits inside a swallowing try/catch. If
> Revit requires a vendor id, every stamp in the plugin is failing silently. `ZoneOwnerSchema`
> inherits that risk and it needs confirming on a Windows run before stamped ownership is
> relied on.

---

## 7. Surfaces

### 7.1 Zone Manager — a bespoke `Window`
The library editor. A step flow is the wrong shape for it (it is not a run-once wizard), so it
follows `LegendSettingsWindow` / `FiltersSettingsWindow`.

Layout: a left tree (Building → Level → Area, with the nesting order switchable) and a right
detail pane whose content depends on the selection — building, level (band + view-range
defaults), area (definition, extents, anchor, scope box), recipe, or placement.

Two constraints that bite bespoke windows specifically, both already in CLAUDE.md:
- **No dispatcher safety net.** `StepFlowWindow` installs `Dispatcher.UnhandledException`;
  a bespoke window does not, so any *auto-firing* callback (timer tick, async continuation)
  must guard its own body or an unhandled throw hard-crashes Revit with no log entry.
- **`IToolCleanup.OnWindowClosed` is never invoked** for bespoke windows — only
  `StepFlowWindow` calls it. Persistence hangs off `OnClosed` directly.

### 7.2 Zone Discover — an `IStepFlowTool`
Scan → propose → confirm → create. Reads the chosen link(s) and proposes buildings, levels,
areas and grids; the user confirms; the run creates/updates scope boxes and writes the library.

Per CLAUDE.md: **a zero-result scan must say so in the run log.** "Found 0 levels in
<link>" — never a silent empty list.

### 7.3 `ZonePicker` — the reusable control
`Source/Framework/Controls/Input/ZonePicker.xaml`, same contract family as
`BrowserTreePicker` and `MultiSelectTabs`:
- `SetLibrary(...)` fires `SelectionChanged` **once** at the end (subscribe *before* calling).
- `SingleSelect` for one-pick consumers.
- Children nest under a parent expand caret; deselecting a parent auto-clears its children, so
  a selected cell can never sit under an unselected level.
- Built-in search, unconditional, matching `MultiSelectTabs`' behaviour.

This is the thing that makes zones "easy to call on": every consuming tool drops in this one
control instead of hand-rolling a level list plus a scope-box list.

---

## 8. Part A — core scope (build first)

| # | Deliverable | Files |
|---|---|---|
| A1 | Data model + XML DTOs | `Source/Framework/Zones/ZoneModels.cs` |
| A2 | ES section + load/save | `ProjectLibraryStore.cs` (+`SectionZones`), `ZoneSettings.cs`, `App.cs` registration |
| A3 | Extent solving from grids / scope box / rooms | `ZoneExtentSolver.cs` (reuses `RoomClusterSearch`) |
| A4 | Scale fit — Revit-free | `ZoneScaleFit.cs` |
| A5 | Sheet-anchor math extraction | `SheetAnchorMath.cs`; `AlignSheetViewsEventHandler` re-pointed, behaviour unchanged |
| A6 | View-range apply/read | `ZoneViewRangeApplier.cs` (`GetViewRange`/`SetViewRange`/`CheckPlanViewRangeValidity`) |
| A7 | Scope-box sync + ownership stamp | `ZoneScopeBoxSync.cs`, `ZoneOwnerSchema.cs` |
| A8 | `ZonePicker` control | `Source/Framework/Controls/Input/ZonePicker.xaml(.cs)` |
| A9 | Zone Manager window | `Source/Tools/Zones/Windows/ZoneManagerWindow.*` |
| A10 | Zone Discover step-flow tool | `Source/Tools/Zones/ZoneDiscover*.cs` |
| A11 | Zone naming tokens | `{Building}` `{Level}` `{Area}` `{ZoneCode}` as `TokenOrigin.Computed`, passed as `extraComputed` — **not** global registry entries |
| A12 | Strings | `Strings/en/zones.json` — every user-facing string and run-log line via `AppStrings.T` |
| A13 | Ribbon | a **Zones** pulldown (Manager / Discover) on the Views panel, placed *before* Scope Boxes |

**A5 is the highest-risk item** and it is deliberately early: it touches a 2 753-line file that
is already correct. It is a pure extraction with no behaviour change, and it is what buys the
exact-placement feature for free rather than re-deriving math that took real debugging to get
right.

**Not in Part A:** sheet creation, bulk view generation, and every consumer integration. Part A
ends with a library you can author, that owns real scope boxes, and one picker — plus §9's first
row so it is provably useful rather than only structurally complete.

---

## 9. Part B — the integration map

### 9.1 Tools that CREATE or FEED zones

| Tool | Change | Why it is the right home |
|---|---|---|
| **Scope Box Creator** | new step: "also register these clusters as Zone Areas" | It already clusters link rooms into boxes. It is the single best place to *create* zones because the hard work is done there already. |
| **Scope Box Manager** | Zone column; "adopt this box as an Area"; repair a zone whose box was renamed/deleted | It already maps box → views + datums. Natural home for reconciliation. |
| **Align Sheet Views** | "Record placement into the Zone" and, inverted, "align to the Zone's stored placement" | Its capture *is* the placement record. The inverse removes this tool's need to pick a reference sheet at all — the zone becomes the reference. |
| **Copy Datums** | feed discovered link levels/grids into the Zone level axis | It already reads and reconciles link datums. |
| **Align / Push Coordinates** | Zone Manager warns when links are uncoordinated | Zone anchors are world coordinates — meaningless before links are coordinated. A warning, not a block. |

### 9.2 Tools that CONSUME zones

Ordered by value:

| Tool | Zone input | Payoff |
|---|---|---|
| **Bulk Views** | a 6th mode, "By Zone" | Flagship. Pick cells → get views with the right family type, template, scale, view range, crop, section box and name. The existing By-Level and By-Scope-Box modes are already 90% of this shape. |
| **Place Dependent Views** | title block + scale + exact placement from the zone | Replaces packing with a known-correct position. |
| **Align Sheet Views** | the zone's stored placement as target | See above. |
| **Clash Finder / Clash Elevation Finder** | survey scoped to a zone's 3D box | "Run clash detection on Area 1, Level 3" — currently view-scoped only. High practical value. |
| **Ceiling Heatmap / Make Ceiling Grids / Tag Ceilings** | zone (level + area) scoping + the RCP recipe's view range | All three are already per-level; area scoping and a shared view range is a small delta. |
| **Bulk Export** | export sets defined by zone | Sets stop being hand-picked sheet lists. |
| **Bulk Rename** | `{Building}` `{Level}` `{Area}` tokens | |
| **Explode View by Trade** | per-zone trade views | |
| **Auto Filters** | apply a trade's filters to every view of a zone | |
| **Smart Legend** | legend per zone view | |
| **Print View** | zone → correct sheet size | |
| **Split by Grid / Split by Cell** | share the grid-defined extents | |

### 9.3 The tool that does not exist yet
**Build Sheets from Zones** — the natural end of the chain: pick cells + recipes → create views
→ create sheets with the right title block → place each view at its stored placement → name and
number from tokens. Everything it needs is Part A plus §9.2's first two rows. Worth planning
separately once Part A is real; listing it here so the architecture leaves room for it.

---

## 10. Risks and constraints, stated up front

1. **A5 touches a large, correct file.** Pure extraction, no behaviour change, Align Sheet
   Views re-verified before commit.
2. **Cross-document level identity is the level NAME, elevation second.** Already the recorded
   rule; Zones depend on it heavily since levels are usually discovered from an arch link.
3. **Sheet placement is unverifiable on Linux.** This project cannot build on Linux and
   placement correctness is a plotted-output question. Every placement claim in this plan is
   provisional until a Windows/Revit run confirms it — that will be stated in the code comments
   the way the existing provisional notes are.
4. **`ZoneOwnerSchema` inherits the unconfirmed `SetVendorId` risk** (§6).
5. **`ViewportPositioning` interaction with `SetBoxCenter` is unverified** (§5.5).
6. **Scope of Part A is a library and a picker, not a generator.** If that reads as too little
   to be useful, the fix is to pull "By Zone" from §9.2 into Part A — say so and I will.

---

## 11. Silent-failure discipline

Applied throughout, per CLAUDE.md:
- Every discover/scan that finds zero items logs "Found 0 …" explicitly.
- Every Zone option is traced from the UI control **through to the Revit call that consumes
  it** — the Bulk Export `HiddenLines` failure (fully plumbed, never read) is the pattern being
  guarded against here, and a recipe has many such fields.
- No empty `catch {}`; everything deliberately swallowed goes through
  `DiagnosticsLog.Swallowed(context, ex)`.
- A post-change silent-failure scan is run and reported before any commit.

---

## 12. Decisions

| # | Question | Decision |
|---|---|---|
| 1 | Name of the concept | **Zone** — `ZoneLibrary`, `ZonePicker`, Zone Manager. Avoids colliding with Revit's own Areas. |
| 2 | Addressing model | **Areas × Levels matrix** (§2), sparse cell overrides only. |
| 3 | Default extent definition | **Adopt existing scope boxes** (§4); grids / room clusters / manual also available. Anchor mode stays orthogonal (§4.2). |
| 4 | Placement authoring | **Both** — solved by default, capturable to override (§5.4). |

### Still open
**Base branch.** `claude/revit-3d-view-templates-wwq3sk` currently sits *ahead* of
`origin/main`, carrying merged ceiling-tag work (PRs #154/#155) that has not landed on main.
Basing on it is fine, but the diff will not be clean against main — worth a deliberate choice
rather than a default.

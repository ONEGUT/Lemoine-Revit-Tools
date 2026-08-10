# Plan — Copying Functions Cleanup

Branch: `claude/copying-functions-cleanup-zgdud7` (from `main`)

Covers three requests against the Copy tools:

1. Source link + worksets become step 1; category selection becomes step 2.
2. The "Architectural / Other" tab holds 100+ entries — make it only real architectural categories.
3. Add an **Annotation** group, and a home for **section boxes and similar**.

---

## 1. Findings (verified, not assumed)

### 1a. Why the Arch tab has 100+ entries

`CopyFromLinkViewModel.DisciplineOf` (`:431`) and the byte-identical
`CopyLinearViewModel.DisciplineOf` (`:838`) classify by substring against the `OST_` string, with
**everything unmatched falling through to `"Architectural / Other"`**:

```csharp
if (C("Duct", "MechanicalEquipment", "FlexDuct"))   return "Mechanical";
if (C("Pipe", "Plumbing", "Sprinkler", …))          return "Piping";
if (C("Cable", "Conduit", "Electrical", …))         return "Electrical";
if (C("Structural", "Rebar", "Reinforcement", …))   return "Structural";
return "Architectural / Other";     // ← catch-all
```

The input is `AutoFiltersSettings.KnownCategoryMap`. With a document open,
`CaptureFilterableCategories` replaces that map with **every filterable `CategoryType.Model`
category Revit reports** — including the whole Revit 2023+ bridge/infrastructure family
(`OST_BridgeDecks`, `OST_AbutmentWalls`, `OST_PierColumns`, …), `OST_Toposolid*`, and every
model sub-category. Four narrow needle lists match a small slice; the rest lands in Arch.

Measured against the shipped fallback map (95 entries — the *small* case):

| Tab | Count |
|---|---|
| **Architectural / Other** | **49** |
| Electrical | 14 |
| Piping | 12 |
| Structural | 11 |
| Mechanical | 9 |

Over half the list, before a document is even open. Things currently sitting in the Arch tab that
are not architectural:

- **Grids, Levels** — datums
- **Wires** (`OST_Wire`), **Audio Visual Devices** — electrical
- **Fire Protection** — fire protection / piping
- **Mechanical Control Devices** — `OST_MechanicalControlDevices` misses the `MechanicalEquipment` needle
- **Structural Rebar Couplers** (`OST_Coupler`) — structural
- **Site, Topography, Planting, Parking, Roads, Hardscape** — site / civil
- **Rooms, Areas, Spaces** — spatial, not built elements

Plus a live-document count of **31** bridge/infra/toposolid categories dumping into the same tab, and
one outright mis-route: **`OST_BridgeCables` → Electrical**, because the string contains "Cable".

### 1b. Dead entries found in `DefaultKnownCategoryMap`

Five OST strings in `AutoFiltersSettings.DefaultKnownCategoryMap` **do not exist** in the Revit 2024
`BuiltInCategory` enum (read from `libs/RevitAPI.dll` metadata, not string-searched). The static
constructor's `IsResolvableBuiltInCategory` filter silently drops them, so these categories are
simply missing from the no-document picker:

| Shipped (dead) | Real name |
|---|---|
| `OST_AreaReinforcement` | `OST_AreaRein` |
| `OST_PathReinforcement` | `OST_PathRein` |
| `OST_FabricArea` | `OST_FabricAreas` |
| `OST_StructuralConnectionHandlers` | `OST_StructConnections` |
| `OST_PipeLinings` | *no such Revit category* — remove |

### 1c. Annotation categories cannot be copied by the current code path

Two facts, both verified:

- `CaptureFilterableCategories` (`:1189`) keeps `CategoryType.Model` only, plus an explicit
  two-entry allowlist (`OST_Grids`, `OST_Levels`). Every annotation category is filtered out, which
  is why there is no Annotation group today. CLAUDE.md forbids widening that allowlist, because the
  same map feeds Auto Filters' model-element pickers.
- Both copy tools run the **document→document** overload
  `ElementTransformUtils.CopyElements(Document, ids, Document, Transform, CopyPasteOptions)`
  (`CopyFromLinkRunHandler.cs:156`). Annotation elements are view-specific and this overload cannot
  copy them. `RevitAPI.dll` metadata confirms a **second 5-argument overload taking `View`
  source/destination** — that is the only path that works, and it needs a source-view → target-view
  choice the tool does not currently have.

So an Annotation tab wired to today's run path would be a **dead option** — exactly the failure mode
CLAUDE.md calls out ("a tool option is not wired until it reaches the API call").

### 1d. "Section boxes and other stuff similar" — what actually exists

Verified against the enum:

| Wanted | Real category | Copyable via current path? |
|---|---|---|
| Scope Boxes | `OST_VolumeOfInterest` | **Yes** — model element with 3D extents |
| Reference Planes | `OST_CLines` | **Yes** (there is **no** `OST_ReferencePlanes`) |
| Grids / Levels | `OST_Grids` / `OST_Levels` | Yes (already in the map) |
| Section Boxes | `OST_SectionBox` | No — it is a 3D view's crop, not a standalone element |
| Matchline | `OST_Matchline` | No — view-specific |

The copyable four form a natural **"Datums & Reference"** tab. Note the literal "Section Boxes"
category is not a copyable element; **Scope Boxes** is what serves that intent.

---

## 2. Changes

### 2.1 New shared file — `Source/Tools/CopyFromLink/CopyCategoryGroups.cs`

One source of truth for copy-tool category grouping, replacing the two duplicated `DisciplineOf` /
`BuildCategoryGroups` pairs.

- Explicit `OST_` → discipline dictionary (allowlist), **not** a substring catch-all.
- Ordered substring rules as a *secondary* pass only, so an uncatalogued category still lands
  sensibly instead of in Arch.
- Anything still unmatched → **`"Other"`**, never `"Architectural"`.
- Every `OST_` string in the file validated against `libs/RevitAPI.dll` metadata before commit.

Tabs produced (`MultiSelectTabs.SetGroups` auto-sorts alphabetically, `"Other"` pinned last):

| Tab | Contents |
|---|---|
| Architectural | Walls, Floors, Roofs, Ceilings, Doors, Windows, Stairs, Ramps, Railings, Columns, Curtain Panels/Mullions/Systems, Wall Sweeps, Slab Edges, Fascias, Gutters, Roof Soffits, Furniture, Furniture Systems, Casework, Specialty Equipment, Generic Models, Mass, Mass Floors, Parts, Assemblies, Signage, Vertical Circulation, Medical / Food Service Equipment, Temporary Structures, Shaft Openings |
| Structural | Structural Framing / Columns / Foundations / Trusses / Stiffeners, `OST_StructConnections`, Rebar, `OST_AreaRein`, `OST_PathRein`, `OST_FabricReinforcement`, `OST_FabricAreas`, `OST_Coupler`, Tendons |
| Mechanical | Ducts + fittings/accessories/insulation/linings, Flex Ducts, Air Terminals, Mechanical Equipment, **Mechanical Control Devices**, Fabrication Ductwork, HVAC Zones |
| Piping | Pipes + fittings/accessories/insulation, Flex Pipes, Plumbing Fixtures, Plumbing Equipment, Sprinklers, **Fire Protection**, Fabrication Pipework / Hangers / Containment |
| Electrical | Cable Trays, Conduits (+ fittings), Electrical Equipment / Fixtures, Lighting Fixtures / Devices, **Wires**, **Audio Visual Devices**, Comms / Data / Fire Alarm / Security / Telephone / Nurse Call Devices |
| Site & Civil | Site, Topography, Toposolid, Planting, Entourage, Parking, Roads, Hardscape, Building Pads, `OST_SiteProperty`, Alignments |
| Spatial | Rooms, Areas, Spaces, HVAC Zones |
| Bridge & Infrastructure | `OST_Bridge*`, `OST_Abutment*`, `OST_Pier*` (fixes `OST_BridgeCables` → Electrical) |
| Datums & Reference | Grids, Levels, **Scope Boxes** (`OST_VolumeOfInterest`), **Reference Planes** (`OST_CLines`) |
| Other | Genuinely unmatched — honest, and no longer mislabelled "Architectural" |

Measured over the fallback map with this grouping applied: Architectural **49 → 32**, every one of
the 96 categories classified, and **Other lands at 0**. All `OST_` strings machine-validated against
`libs/RevitAPI.dll`.

### 2.2 Step split — `CopyFromLinkViewModel.cs`

`Steps` becomes:

| # | id | Title | Content |
|---|---|---|---|
| 1 | `source` | Source Link & Worksets | link `SingleSelect` + workset `MultiSelectTabs` |
| 2 | `categories` | Categories | category `MultiSelectTabs` |
| 3 | `types` | Families to Copy | unchanged |
| 4 | `changes` | Change Detection | unchanged |
| 5 | `run` | Review & Run | unchanged |

- `BuildSourceStep` splits into `BuildSourceStep` (link + worksets) and `BuildCategoriesStep`.
- `IsValid`: `source` → a link is resolved; `categories` → `Categories.Count > 0`.
- `SummaryFor` gains a `categories` case; the `source` summary drops its category count.
- Changing the link still calls `ResetScan()` + `RebuildWorksets()`.

### 2.3 Step split — `CopyLinearViewModel.cs`

Same split, inserted before the existing `filters` step:
`source` → `categories` → `filters` → `operation` → `changes` → `run`.
`OnStepActivated("filters")` still drives the scan, so confirming **Categories** now leads into it.

### 2.4 Strings

New keys in `Strings/en/copy.fromLink.json` and `Strings/en/copy.linear.json`
(`steps.categories`, `summaries.categories`, `labels.*` for the new step). Discipline tab labels
stay hardcoded — they key the grouping dictionary (CLAUDE.md: persisted/logic tokens are not
externalized).

### 2.5 Fix the dead `DefaultKnownCategoryMap` entries (§1b)

Correct the five OST strings so the no-document fallback picker stops silently dropping Area
Reinforcement, Path Reinforcement, Fabric Areas and Structural Connections; drop `OST_PipeLinings`.

---

## 3. Decisions (answered)

**Q1 — Annotation group → grouping only for now.** Annotation elements need the `View`→`View`
`CopyElements` overload (§1c), which neither tool has. No Annotation tab ships in this change — an
Annotation tab on today's run path would be a dead option. The **Datums & Reference** tab ships
instead (Scope Boxes / Reference Planes / Grids / Levels all copy fine through the existing path).
Full annotation copy stays a separate future feature.

**Q2 — Scope Boxes / Reference Planes → copy-tools-only capture.**
`AutoFiltersSettings.AllowedNonModelCategories` is **not** widened; Auto Filters' model-element
pickers are untouched, preserving the CLAUDE.md constraint. The copy tools get their own capture
that additionally admits `OST_VolumeOfInterest` and `OST_CLines`.

**Q3 — Source links → keep single-select.** Step 1 keeps the existing one-link `SingleSelect`;
only the step ordering changes. No multi-link spec/scan/run rework.

### Consequences for §2

- §2.1 drops the Annotation tab from the tab table; **Datums & Reference** stays.
- New capture entry point (copy-tools-only) rather than an edit to `AllowedNonModelCategories`.
  It reuses `CaptureFilterableCategories`' logic with a wider non-Model allowlist, writing to a
  separate snapshot the copy tools read — Auto Filters keeps reading the existing one.

---

## 4. Verification before commit

- Every `OST_` literal added re-validated against `libs/RevitAPI.dll` metadata via `dnfile`.
- Every `AppStrings.T("copy.…")` key cross-checked against the flattened JSON.
- Post-change silent-failure scan per CLAUDE.md.
- Cannot be compiled here (Linux); needs a Windows build.

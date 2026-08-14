# Plan — Corridor corner tagging + per-level ceiling isolation

Three independent changes to the ceiling tools:

1. **Restore the corridor corner logic** in the Revit-free tag planner.
2. **Level-scoped ceiling isolation**: a per-level view filter set (a new `Ceiling Levels`
   Auto Filters trade), applied by Ceiling Heatmap (per-level generation) and Make Ceiling
   Grids, plus a level cross-check in Tag Ceilings that warns about strays.
3. **Open the target views before the commit** so a closed view is not recomputed per tag.

Base branch: the designated branch `claude/ceiling-tagging-logic-a9h7wh` is currently
identical to `origin/main` (`5fb6941`), so no rebase or re-basing decision is needed.

---

## 1. Corridor corners

### What changed in `5fb6941` (and is being reverted)

`TagPointPlanner.PlaceAlongRun` used to place `ceil(len / spacing)` tags per **leg** — a
corner-to-corner straight stretch produced by `RegionSkeleton.ExtractLegs` — each centred
on its own share of that leg. `5fb6941` replaced this with a single continuous run: the
accumulated distance carries across corners, so a corner never restarts the count.

That change was made because fixture-laden ceilings were mis-measured (a 40x30 office read
as a corridor with elongation 19.3), which produced far too many legs and therefore far
too many tags. The same commit also added `TagPlanConfig.MinHoleAreaFt2`, which fixed the
underlying measurement error — so the per-leg rule can come back on honest geometry.

### Change

`Source/Tools/Ceilings/CeilingTags/TagCore/TagPointPlanner.cs`

- `PlaceAlongRun` → back to per-stretch: for each leg, `n = ceil(len / spacing)` tags at
  `len * (k + 0.5) / n`. A short stretch gets one tag at its midpoint; a 90 ft stretch at
  30 ft spacing gets 3.
- The `carried` / `isFirst` continuous-run bookkeeping is removed.
- Keep the existing `island.NearestInterior(...)` snap (a tag point is always a real
  interior cell) and the `PlaceSingle` fallback when thinning yields no usable leg.
- Keep `CollapseNearDuplicates` at `MinTagSeparationFt = 6.0` — this is what stops the two
  legs meeting at a corner from putting two tags a couple of feet apart.
- Update the class-level doc comment (rule 3) and `MaxTagSpacingFt`'s summary.

**Rules 1 and 2 are NOT touched.** A room still gets exactly one tag, and a region that
*encloses a hole* (a ring corridor, a band around other ceilings) still gets ONE tag at the
top centre — CLAUDE.md records that "one tag per side of a ring" was explicitly superseded.
The corner logic therefore applies to open corridors (L, U, Z, T runs), not to loops.

Also updated: `Strings/en/ceilings.tags.json` — `labels.spacingHint` ("measured
continuously around corners" → per-stretch wording) and the file header comment.

---

## 2. Per-level ceiling isolation

### 2a. New shared component

**New file** `Source/Tools/Ceilings/CeilingLevelFilters.cs`

An externally-managed Auto Filters trade, built exactly like `CH` (Ceiling Heatmap) and
`CG` (Ceiling Grids — Hidden):

| | |
|---|---|
| Trade id | `CL` |
| Label | `Ceiling Levels` |
| Filter name | `AutoFiltersSettings.MakeFilterName("CL", levelName)` |
| Category | `OST_Ceilings` |
| Rule | `Level` equals *this level* |

API surface (confirmed against `libs/RevitAPI.dll` metadata, not a string search):

- `ParameterFilterRuleFactory.CreateEqualsRule(ElementId parameter, ElementId value)` —
  exists; this is the `LEVEL_PARAM` rule.
- `ParameterFilterElement.Create(doc, name, categories, elementFilter)` /
  `SetElementFilter(ElementFilter)` — both take an `ElementFilter`, so a `LogicalOrFilter`
  is acceptable.
- `LogicalOrFilter(IList<ElementFilter>)` and `(f1, f2)` — both exist.
- `Element.LevelId` — exists (used as the fallback when `LEVEL_PARAM` is absent).

Methods:

- `EnsureLevelFilters(doc, log)` → one filter per `Level` in the document, reused by name
  and **updated in place** with `SetElementFilter` (never delete + recreate, so existing
  view assignments survive). Returns `levelId → ParameterFilterElement`.
- `ApplyIsolation(doc, view, levelId, filters, log)` → adds every level filter to the view,
  sets visibility `true` for the view's own level and `false` for every other level. A
  ceiling on any other level is hidden; visibility-off wins over any other filter that
  would show it.
- `RegisterLevelTrade(levels, log)` → mirrors the filters into the `CL` trade
  (`ExternallyManaged = true`, one rule per level, `Parameter = "Level"`,
  `MatchType = "equals"`, `Match = { levelName }`).

Rules are created for **every** level in the document, not only the levels being viewed —
otherwise a ceiling on a level with no generated view is matched by no filter at all and
stays visible.

#### Linked ceilings — the one risky part, handled explicitly

`Level` is an **ElementId-valued** parameter, and a linked ceiling's level id belongs to the
*link's* document, so a host filter holding the host's level id is not expected to match it.
That matters here more than usual: CLAUDE.md and the heatmap both record that in this
project ceilings typically live in **linked** architectural models.

Mitigation, built into `EnsureLevelFilters`:

- For each host level, find the corresponding level in every loaded link document (match on
  name first, then on world elevation through the link transform within a small tolerance).
- Build the rule as a `LogicalOrFilter` over `{host level id} ∪ {each link's matching level
  id}`, so the filter can match linked ceilings too.
- Wrap it in try/catch: if Revit rejects a foreign document's ElementId, fall back to the
  host-only rule and **log the reason** rather than shipping a filter that silently matches
  nothing.
- Log one line naming how many links contributed a level, so a link that contributed none
  is visible in the run log.

This is unverified on Windows/Revit (it cannot be built or run on Linux) and will be flagged
as such in CLAUDE.md. Tag Ceilings' level check (2d) is the independent, API-side safety net:
it reads each ceiling's own level directly and will report anything the filter failed to
hide.

### 2b. Ceiling Heatmap — per-level generation

`Source/Tools/Ceilings/CeilingHeatmapEventHandler.cs`

- `GenerateRcpViews` already knows `level → view`. After the views are found/created, call
  `EnsureLevelFilters` + `ApplyIsolation` per generated view, then `RegisterLevelTrade`.
- Scoped to the **generate-per-level** mode only. When the user hand-picks existing views,
  their filter sets are left alone — silently rewriting the filters on a user's own views is
  not what "generate heatmap RCPs per level" asked for.

### 2c. Make Ceiling Grids

`Source/Tools/Ceilings/MakeCeilingGridsRunHandler.cs`

- `RunGrids` already creates one RCP per level and keeps `(view, name)`; it will also carry
  the `levelId` so isolation can be applied per view.
- Isolation runs after the views are created and scoped, before the `CG_` type-hide filters,
  in its own transaction. The two filter sets compose correctly: a ceiling is drawn only if
  it is on this level **and** its type was not excluded.
- This replaces reliance on the RCP's default view range, which is what the request called
  out ("instead of using any arbitrary view range").

### 2d. Tag Ceilings — level cross-check (warning only)

`Source/Tools/Ceilings/CeilingTags/CeilingRegionSource.cs`

- `CeilingSourceRef` gains `LevelName`, `LevelWorldZ` and `HasLevel`.
- `AddRegion` reads the ceiling's level from **its own document** (`el.Document`), so a
  linked ceiling resolves against the link, and converts the level elevation to world feet
  through the link transform. `LEVEL_PARAM` first, `Element.LevelId` as fallback.

`Source/Tools/Ceilings/CeilingTags/CeilingTagEngine.cs`

- Per view, compare every collected ceiling against `vp.GenLevel`:
  - match on level **name** (trimmed, case-insensitive) first — this is the cross-document
    safe comparison;
  - otherwise match on world elevation within a small tolerance;
  - otherwise it is a stray.
- Report: one `warn` line with the total, then one line per distinct offending level with
  its count (bounded by the level count, so it cannot flood the log). Ceilings with no level
  parameter at all are counted and reported separately.
- A view with no `GenLevel` logs one line saying the check was skipped, rather than passing
  silently.
- **Strays are still tagged.** The request was "just show a warning", so the run is not
  narrowed — the warning tells the user the isolation did not fully take, which is exactly
  the signal that the linked-level caveat above would produce.

---

---

## 3. Tag commit speed — open the target views first

### Observed

With the target view **open** in Revit a full run places every tag in ~10 s. With it
**closed**, Revit appears to regenerate the view for each tag placed.

### Cause

Revit maintains a computed element/geometry set for each **open** view.
`IndependentTag.Create` needs its owner view computed in order to resolve the tagged
`Reference` and anchor the tag:

- open view → that state already exists, so each create is an incremental delta;
- closed view → no maintained state, so Revit computes the view, and each create
  re-dirties it, so the next create recomputes from scratch.

This is the same ~2 s/tag regeneration CLAUDE.md already records for interleaved
geometry *reads*, reached by a different trigger. The read-interleaving cause was already
fixed (read → plan → write); this one is not, because `CeilingTagCommit.Place` takes only a
`Document` and never touches the views' UI state.

Inferred from the API surface plus the measured behaviour — it cannot be executed or proven
on Linux, so it needs a Windows/Revit confirmation before being written up as settled.

### Constraints (confirmed from `libs/RevitAPIUI.dll` metadata)

- `UIDocument.ActiveView` (setter) is the **only** way to open a view.
  `UIDocument.RequestViewChange` exists but is deferred until after the API context
  returns, so it cannot open a view inside an ExternalEvent handler. There is no
  background/hidden open — Revit visibly flips through the views.
- `UIDocument.GetOpenUIViews()` → `IList<UIView>`, and `UIView.Close()` — the snapshot and
  cleanup surface.
- The active view **cannot be changed while a transaction is open**, so every target view
  must be opened *before* `tx.Start()`, not per view inside the loop.
- Every open view pins native graphics RAM for the rest of the session (CLAUDE.md), so the
  run must close what it opened.

### Change

`Source/Tools/Ceilings/CeilingTags/CeilingTagCommit.cs` +
`Source/Tools/Ceilings/CeilingTags/CeilingTagEventHandler.cs`

- `Place` takes the `UIDocument` (the handler already receives `UIApplication`).
- Sequence: `PickerViewGuard.Snapshot(uidoc)` → activate every view that has planned
  placements (each stays open as a tab) → **one** transaction places all tags → commit →
  `PickerViewGuard.CloseOpenedViews(uidoc, before, log)` in a `finally`, which restores the
  user's original active view and closes only the views this run opened. Views the user
  already had open are never closed.
- `PickerViewGuard` moves from `Source/Tools/Dimensioning/AutoDimension/` to
  `Source/Framework/` (it is `internal` and Revit-generic, and would otherwise be a
  ceiling tool reaching into the dimensioning tool's folder). Its one existing caller,
  `ManualDatumPicker`, is updated; behaviour is unchanged.
- Log one line naming how many views were opened and how many were already open, so the
  fast path versus the slow path is visible in the run log rather than being a mystery.

### Second regen source, fixed at the same time

The `replaceExisting` pass runs a view-scoped `FilteredElementCollector` per view,
interleaved with the *previous* view's writes — so an N-view run pays N-1 extra full
regenerations on top of the per-tag cost. Hoisting every view's stale-tag collection into a
single read phase, before any delete or create, removes it. Same read-everything →
write-everything discipline the engine already follows.

---

## Files touched

| File | Change |
|---|---|
| `Source/Tools/Ceilings/CeilingTags/TagCore/TagPointPlanner.cs` | per-stretch corridor tags |
| `Source/Tools/Ceilings/CeilingTags/CeilingTagCommit.cs` | open target views; single read phase |
| `Source/Tools/Ceilings/CeilingTags/CeilingTagEventHandler.cs` | pass the `UIDocument` through |
| `Source/Framework/PickerViewGuard.cs` | **moved** from the AutoDimension folder |
| `Source/Tools/Dimensioning/AutoDimension/ManualDatumPicker.cs` | follow the move |
| `Source/Tools/Ceilings/CeilingLevelFilters.cs` | **new** — `CL` trade, level filters, isolation |
| `Source/Tools/Ceilings/CeilingHeatmapEventHandler.cs` | isolate generated per-level RCPs |
| `Source/Tools/Ceilings/MakeCeilingGridsRunHandler.cs` | isolate created per-level RCPs |
| `Source/Tools/Ceilings/CeilingTags/CeilingRegionSource.cs` | capture each ceiling's level |
| `Source/Tools/Ceilings/CeilingTags/CeilingTagEngine.cs` | level audit + warnings |
| `Strings/en/ceilings.tags.json` | spacing hint + level-check log keys |
| `Strings/en/ceilings.heatmap.json` | isolation log keys |
| `Strings/en/ceilings.makeGrids.json` | isolation log keys |
| `Strings/en/ceilings.levelFilters.json` | **new** — shared helper's log strings |
| `Strings/en/clash.autoDim.json` | `closedViews` key moves to a shared key |
| `CLAUDE.md` | corridor rule reversal; level isolation; closed-view regen cost |

No UI/XAML changes — no new options, toggles or steps. (The isolation is the requested
behaviour, not an option, so no `/revit-navisworks-ui` mockup pass is needed.)

## Validation

The placement core is Revit-free and this project cannot build on Linux, so per CLAUDE.md
the corridor change will be **ported to Python and run against real shapes** (straight,
L, U, Z and ring corridors, plus a fixture-laden office) to confirm tag **positions**, not
just counts, before committing.

The Revit-side work (filters, level reads) cannot be executed here; it will be checked by
reading the API metadata directly, which is already done for every call listed above.

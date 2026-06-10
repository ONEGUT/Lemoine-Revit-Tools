# Plan — Phase filtering for clash marking & dimensioning

## Goal
Let a clash definition restrict its markers to elements that exist in a given
**phase**, mapping linked-model phases to the host **by name**. Default off, so
existing definitions are unchanged.

## Design (decided in chat)
Phase is a **per-view** property, and marking already runs **per view**
(`ClashEngine.PlaceInView`, gated today by the view's volume). So the phase gate
is a sibling of that volume gate — driven by each view's phase. Detection stays
model-wide and phase-agnostic; an optional "specific phase" mode culls at
detection for the scoping/performance case.

Per-definition mode (stored on `ClashDefinition`):
- **All phases** (default) — no phase filtering (current behaviour).
- **Match view phase** — at `PlaceInView`, keep a clash only when **both** its
  elements exist in that view's phase. Zero config; each view self-selects.
- **Specific phase** *(optional scope — see decision 3)* — cull at detection to
  one chosen host phase (single phase for the whole run).

"Exists in phase P": `createdSeq ≤ P` and (`demolished == none` or
`demolishedSeq > P`). Honouring the full `VIEW_PHASE_FILTER` presentation
(New/Existing/Demolished/Temporary) is a later refinement, not v1.

## Linked-model phase mapping
A link's phase parameters are `ElementId`s in the **link** document, meaningless
against host phases, and Revit's link Phase Mapping isn't cleanly exposed. Map
**by phase name**: capture each element's *Phase Created* / *Phase Demolished*
**name** in its own document at scan time; compare those names against the host
phase sequence (the host is the source of truth; the view phase is a host phase).
A link phase name absent from the host sequence is **passed through + logged**
(`pushLog`/`LemoineLog.Warn`), never silently dropped.

## Files changed

### 1. `Source/Tools/T05-Clash/ClashDefinitions/ClashDefinition.cs`
- `[XmlAttribute] string PhaseMode = "All";`  // "All" | "MatchView" | "Specific"
- `[XmlAttribute] string SpecificPhaseName = "";`  // used only in Specific mode
- Defaults keep existing saved definitions identical.

### 2. `Source/Tools/T05-Clash/ClashFinder/ClashEngine.cs`
- **`ClashMarkingOptions`**: add `string PhaseMode` and `string SpecificPhaseName`.
- **`ClashElement`**: add `string CreatedPhaseName` and `string DemolishedPhaseName`
  (empty = "None"), captured in `ScanRules` / `ScanCategories` / `ScanElements`
  via a small `ReadElementPhases(el)` helper (reads `PHASE_CREATED` /
  `PHASE_DEMOLISHED`, resolves each id to its `Phase.Name` in `el.Document`).
- **`ClashDetection`**: add `Dictionary<string,int> HostPhaseSeq` (host phase
  name → sequence index), built once in `Detect` from
  `doc.Phases` (ordered). Mirrors how `LevelElevs` is precomputed.
- **`Detect`** (Specific mode): when `PhaseMode == "Specific"`, drop any scanned
  element not present in `SpecificPhaseName` **before** the boolean-intersection
  pass (cheap cull, the performance path).
- **`PlaceInView`** (MatchView mode): resolve the view's phase name
  (`view.get_Parameter(VIEW_PHASE)` → `Phase.Name`); for each clash, skip
  (`pr.Skipped++`, same as the volume gate) unless **both** elements pass
  `PhasePresent(name, targetSeq, HostPhaseSeq)`. A view with no phase, or an
  unmapped element phase name → pass-through + a one-line log.
- **`PhasePresent`** helper: name → seq via the host map; missing name → log +
  return true (don't drop).

### 3. `Source/Commands/T05-Clash/OpenClashDefinitionsCommand.cs`
- Capture host phase names (`doc.Phases` in order) on the main thread and pass
  them to the window (for the Specific-phase dropdown), alongside the existing
  line-styles / docs context.

### 4. `Source/Lemoine/T05-Clash/ClashDefinitions/ClashDefinitionsWindow.xaml.cs`
- In **Marking Settings**, add a "Phase" `LemoineSingleSelect`
  (All phases / Match view phase / Specific phase). When "Specific", show a
  second `LemoineSingleSelect` of the captured host phase names bound to
  `def.SpecificPhaseName`. Auto-save on change (house pattern — no Apply button).

### 5. Run handlers — copy the new fields into `ClashMarkingOptions`
- `Source/Tools/T05-Clash/ClashFinder/ClashFinderEventHandler.cs`
- `Source/Tools/T05-Clash/ClashElevationFinder/ClashElevationFinderEventHandler.cs`
  (uses `engine.Run`, which routes through Detect + PlaceInView, so phase flows
  through `opts` automatically once the fields are set).

## Semantics / decisions baked in
- **Both** clash elements must exist in the phase for the clash to mark (a clash
  is only real when both parties are present). Stated, not configurable in v1.
- Unmapped/blank phases → pass-through + log, never a silent drop.
- Default `PhaseMode = "All"` → no behaviour change for existing definitions.

## Silent-failure scan
Phase reads (`PHASE_CREATED`/`PHASE_DEMOLISHED`, `VIEW_PHASE`, `Phase.Name`) wrapped
and routed to `LemoineLog`; the mandated post-change scan runs before completion.

## Open decisions (confirm before building)
1. **Branch** — stack on the current workset branch, or a new branch off main.
2. **Default mode** — "All phases" (back-compat) vs "Match view phase" as default.
3. **Scope** — ship Match-view only (simplest), or include the Specific-phase
   detection-stage cull now as well.

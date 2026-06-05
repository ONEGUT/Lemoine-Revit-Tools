# Plan — Functional Code Audit Fixes (Prioritized)

This plan turns the deep functional (non-UI) audit into an ordered fix program.
Findings were produced by a full read of every engine/handler/runner/resolver/
parser/settings file (~52K LOC). Items marked **✅verified** were re-confirmed
directly in source; items marked **(suspicion)** need a Revit-side confirm before
the fix is finalized.

Per CLAUDE.md: **one logical change per branch**. The tiers below are the
recommended *order*; each lettered group is one branch. No code is written until
the base branch and starting group are approved.

---

## P0 — Data loss / destructive / wrong-target (fix first)

### A. Apply-to-Views targets every same-named view (RCP / Floor Plan / 3D)  ✅verified
- **Symptom:** Selecting "Level 1" (Floor Plan) also applies the filter to the
  "Level 1" Ceiling Plan (RCP); two 3D views with the same name can't be told
  apart. The filter lands on views the user never picked.
- **Root cause:** the whole chain is name-string based.
  - `ApplyFiltersToViewsEventHandler.cs:76–81` resolves views with
    `selectedViewSet.Contains(v.Name)`.
  - `ApplyFiltersToViewsViewModel.cs:36,39,118–128,216` carries only
    `(Name, ViewType)` and selects/sends name strings.
  - `LemoineMultiSelectTabs` selection is keyed by the display string, so even
    the UI can't distinguish two identical names.
- **Fix:** thread a stable `ElementId` (as `long`) end-to-end. Build a unique
  display label per view (disambiguate collisions, e.g. append view type / a
  counter) mapped to its ElementId; pass selected ElementIds to the handler;
  resolve by id, not name. Touches the launch command (view collection), the
  VM ctor/state, and the handler's `SelectedView*` contract.
- **Note:** S2 step is UI — invoke `/revit-navisworks-ui` before editing the VM.

### B. Corrupt clash-definitions file wipes the whole library  ✅verified
- `ClashDefinitions/ClashDefinitionsSettings.cs:113–137` — `Load()` catches any
  deserialize failure and falls through to the *seed-one-definition* path; the
  next `Save()` overwrites the recoverable file. **Seed only when the file does
  not exist.** Also reconsider `TryImportFrom` (`:186`) replacing the library
  with no backup.

### C. Reproject ceiling grids deletes originals on zero overlap  ✅verified
- `T02/CeilingGridEventHandler.cs:287` — `doc.Delete(sourceGeom.Select(g=>g.Id))`
  removes *all* source curves unconditionally, including `noMatch` ones, with no
  replacement created. Only delete sources that actually projected; preserve the
  rest (and warn).

### D. Split-by-(grid/level/refplane) leaves overlapping geometry
- `T04/SplitElementsShared.cs:485–525` — a split point within 0.01 ft of the
  start endpoint leaves the original at *full length* while copies cover the
  sub-ranges → overlapping duplicates. Reassign the original to `[seq[1], B]`
  or abort the run. Related: `:521` partial-success leaves a **gap** but reports
  success.

### E. Template slug collisions overwrite templates
- `Templates/LemoineTemplateStore.cs:108–120` — `ToSlug` maps spaces→`_`, so
  "Pump A" and "Pump_A" share a path; saving one destroys the other. Use a
  reversible/escaped slug or store the display name in the file and key on a
  hash/guid.

---

## P1 — Dead / contradictory logic (silently wrong behavior)

### F. Discipline section-box buffer is a hard-coded tautology  ✅verified
- `T03/LinkViewsDisciplineRunHandler.cs:190–191` —
  `LinkViewsDisciplineSettings.Instance is var _ ? 3.0 : 3.0`. Read the real
  `SectionBoxBuffer` setting (it's serialized but never used).

### G. Legend / delete discipline grouping is dead code  ✅verified
- `AutoFiltersLegendEventHandler.cs:106` and
  `DeleteFiltersFromProjectViewModel.cs:158` split filter names on `" - "`, but
  `MakeFilterName` emits `{TRADEID}_{RULE_NAME}` (underscores) — so every row
  falls into `"Other"`. Group by the owning trade (reuse
  `GroupFilterNamesByTrade`) instead of string-splitting.

### H. RebuildFilter detaches filter from all views in create-only pass
- `AutoFiltersEventHandler.cs:533–540` — whole-category/catch rebuild deletes the
  old `ParameterFilterElement` and `Create`s a new id, but the create-only path
  skips the view re-add block → filter silently orphaned. Re-add to the views it
  was on (matches the documented CLAUDE.md hazard).

### I. Heatmap negative-offset filter-name collision
- `T02/CeilingHeatmapEventHandler.cs:810–813` — `FormatFtIn` drops the sign, so
  +6″ and −6″ both yield `0'-6"` → same filter name → buckets merge. Preserve
  sign in the label/name.

### J. AutoDimension `SegmentTextState.Flipped` is dead
- `GreedyLayoutEngine.cs` / `DimensionPlan.cs` — `Flipped` is never assigned, yet
  comments claim the offset loop selects it. Either implement the flip or remove
  the state + correct the comments (decide intent first).

### K. V2→V3 migration overwrites a deliberate "System Type" parameter
- `AutoFiltersSettings.cs:850` — can't distinguish a real `"System Type"` from the
  unset default; rebinds the filter. Gate the migration on a version flag rather
  than value-sniffing.

---

## P2 — Silent failures / hidden errors / miscounts

- **BulkExport non-compliant catch** — `BulkExportEventHandler.cs:525–530`
  catch-and-discard with unused `ex` (only the lone violation of the CLAUDE.md
  error policy). Route through `LemoineLog`.
- **Duplicate sheet numbers abort export** — `BulkExportEventHandler.cs:226`
  `ToDictionary(s=>s.SheetNumber)` throws on dup → whole pack aborts. Use a
  grouping/guard.
- **CreateSheets unguarded doc** — `CreateSheetsEventHandler.cs:42`
  `app.ActiveUIDocument.Document` NREs with no project open; add the guard the
  sister tool has.
- **ExtendWalls ignores `Parameter.Set` result** — `ExtendWallsEventHandler.cs:122–128`
  silent `false` counted as success.
- **SuppressWarnings deletes ALL warnings** —
  `ReplicateDependentViewsRunHandler.cs:233–242` hides real "view range invalid"
  warnings; restrict to copy-monitor.
- **Legend update-by-title misses `(n)` suffix** —
  `LegendCreatorEventHandler.cs:244`.
- **`pass++` miscounts** across T02/T04 (per sub-curve / per type + per view)
  inflate the success totals shown to the user. Tally per source element.
- **LemoineLog disk I/O under `_gate`** — `LemoineLog.cs:152–158` couples ring
  update to disk latency across STA threads; `:166` bare `catch {}`.
  **(suspicion/contention)** — move I/O outside the lock.

---

## P3 — Geometry correctness

- **Arcs/splines flattened to chords** — `CeilingGridHelpers.cs:128–149` only
  projects endpoints; full-circle arcs dropped. Project along the curve.
- **Sutherland-Hodgman degenerate edges** — `SplitByCellHelpers.cs:345–376`
  boundary-coincident (grid-aligned) edges can produce NaN/zero-length curves →
  whole cell silently dropped. **(suspicion)** — guard the denominator + dedup
  the closing seam.
- **Cell enumeration FP drift** — `SplitByCellHelpers.cs:59–68` `x += cellX`
  drops the trailing column after `doc.Delete`. Compute an integer cell count.
- **Heatmap bucket-edge equals-rule** —
  `CeilingHeatmapEventHandler.cs` equals-rule centered on first-seen value, not
  bucket midpoint → edge ceilings uncolored; missing height param treated as 0.0.
- **ExtendWalls shortens intentionally taller walls** —
  `ExtendWallsEventHandler.cs:90–93` a wall at nextLevel + positive offset gets
  reset to offset 0.
- **Level reconciliation by string name (larger)** —
  `LinkViewsLevelPhase1Handler.cs:78–106` + `LinkViewsLevelRunHandler.cs:213–239`
  links whose level names differ from host produce duplicate / "(No rooms)" rows
  and linked rooms never attach. Reconcile by id/elevation. *(May warrant its own
  design discussion — biggest behavioral change.)*
- **SlabEdge ambiguity guard: score-vs-distance** —
  `SlabEdgeTargetResolver.cs:104–124` distance threshold applied to score-sorted
  top-2. **(suspicion)**
- **Grid resolver along-axis-only distance** — `GridTargetResolver.cs:52–59`
  synthesizes the target point on-axis unlike the slab resolver's radial dist.

---

## P4 — Lower severity / cleanup

- Duplicate grid/level names collapsed via `GroupBy(Name).First()` in
  `SplitBy*ViewModel` and clash pickers — silently unsplittable / unselectable.
- Case-sensitive view-existence checks misclassify existing views as failures —
  `LinkViewsLevelHelpers.cs:162`, `ReplicateDependentViewsRunHandler.cs:182`.
- `MepColorMap.cs:25,31` over-broad `"CW"` / `"Supply"` substring matches.
  **(suspicion)**
- `ManualDatumResolver.cs:21,44,50` failure records stamped wrong
  `TargetType.SlabEdge`.
- `ClashFinderEventHandler.cs:112–117` run-level overrides mutate the shared
  `AutoDimensionConfig.Instance` singleton (leaks into the standalone tool).
- `ClashGroupEditor.cs:144–164` orphaned rule keys silently pruned on save.
- AutoFilters apply/delete case-sensitivity on filter-name `.Contains` vs the
  `OrdinalIgnoreCase` rule match.
- Misc dead/double calls (`MakeCeilingGridsRunHandler.cs:184` double `EnsureDir`;
  `AutoDimensionConfig.cs` V2 `FirstOffsetFt` overwritten by V3).

---

## Open questions before coding
1. **Branching:** keep everything on the designated `claude/code-audit-bugs-lBNZu`
   branch, or one branch per lettered group (per CLAUDE.md "one logical change per
   branch")? Recommendation: per-group branches, starting with **P0/A**.
2. **Level-name reconciliation (P3)** is the one item that changes user-facing
   behavior materially — confirm whether to reconcile by elevation, by an
   explicit host↔link level mapping, or leave as-is with a warning.
3. Suspicion items (Sutherland-Hodgman, MepColorMap, SlabEdge, LemoineLog) —
   confirm on Windows/Revit before committing fixes.
</content>
</invoke>

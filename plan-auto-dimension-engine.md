# Plan — Dense-MEP Auto-Dimension Engine (Tier 1)

A new, standalone dimensioning tool that consumes the **cross-section marker lines
output by tool 2 (Clash Finder / `ClashEngine`)** and places collision-aware dimensions
from each source line out to a chosen destination (GRID or SLAB_EDGE). Built from scratch
with a strict separation between target resolution (Part A) and layout (Part B), an
abstract serializable `DimensionPlan` as the only seam to Revit, and engine-owned
idempotency via a dedicated Extensible Storage owner schema.

This document is also the requested **architecture + scoring design + honest complexity
assessment**. Code follows only after approval.

---

## Confirmed open question

**Source lines = the tagged `DetailCurve` cross-lines from tool 2.** Verified in
`ClashDimensionPass.cs` (`ClashTagSchema.IsOurs` → `DetailCurve` →
`GeometryCurve.GetEndPointReference(1)`). They are real curve elements with usable
endpoint references, so the source-side assumption holds. Any line that yields **no**
end-point reference is reported as an unresolved source — never silently skipped.

---

## Architecture — three layers + a serializable boundary

```
Source/Tools/Testing/AutoDimension/
├─ Core/                         ← Revit-FREE (no Autodesk.Revit using). Unit-testable.
│   ├─ Vec2.cs                   ← 2D primitive (double X,Y) + dot/proj/angle helpers
│   ├─ Box2.cs                   ← axis-aligned 2D bbox (paper-space collision math)
│   ├─ DimensionPlan.cs          ← THE BOUNDARY OBJECT (serializable, inspectable)
│   ├─ PlannedDimension.cs       ← one dim: source/target identity, line, segments, side, offset
│   ├─ PlannedSegment.cs         ← per-segment text state: flip/stagger/leader
│   ├─ ICollisionSource.cs       ← pluggable injected predicate (returns obstacle Box2 list)
│   ├─ LayoutScorer.cs           ← hard + soft scoring (see "Layout scoring")
│   └─ GreedyLayoutEngine.cs     ← Tier 1: abstract 2D arrange loop, deterministic
│
├─ Resolvers/                    ← Revit I/O for Part A (target resolution)
│   ├─ ITargetResolver.cs        ← Resolve(SourceLine, config) → ResolvedTarget | Unresolved | Ambiguous
│   ├─ SourceIngest.cs           ← find tagged cross-lines, validate endpoint refs
│   ├─ GridTargetResolver.cs     ← ~95% path. Grid reference. NO slab logic.
│   ├─ SlabEdgeTargetResolver.cs ← vertical slab/opening FACE refs, host+linked, scored
│   └─ LinkRefHelper.cs          ← Reference.CreateLinkReference / stable-rep handling
│
├─ AutoDimOwnerSchema.cs         ← ES owner schema (hardcoded GUID, version + target id)
├─ AutoDimensionCommit.cs        ← dumb consumer: delete owned + stale, place plan, stamp
├─ AutoDimensionConfig.cs        ← versioned config (schemaVersion=1) + XML settings file
├─ AutoDimensionEngine.cs        ← orchestrates ingest→resolve→layout→Plan (Revit read side)
├─ AutoDimensionEventHandler.cs  ← IExternalEventHandler; single commit transaction
└─ AutoDimensionViewModel.cs     ← ILemoineTool wizard (StepFlow) + report

Source/Commands/Testing/AutoDimensionCommand.cs   ← opens StepFlowWindow on STA thread
Source/App.cs                                     ← register event + ribbon button
```

**The `DimensionPlan` is the seam.** The Revit *read* side (ingest + resolvers + engine)
produces a `DimensionPlan` containing every dimension to place, every unresolved target,
and every flagged ambiguity. `AutoDimensionCommit` is a *dumb consumer* of an approved
plan. This gives preview, failure reporting, an ambiguity-resolution surface, and
replayable regression tests from one place. The Core folder takes only plain doubles/Vec2
so it compiles and runs without Revit (the in-app debug harness in
`Source/Tools/Debuggers/` is how we exercise it, since the repo is Windows-only and has no
unit-test project — noted as a limitation, not a blocker).

---

## Idempotency & ownership

- **New** schema `AutoDimOwnerSchema` — hardcoded constant GUID, name `LemoineAutoDimOwner`,
  fields: `Version` (int, =1), `RunId` (string), `TargetKey` (string identity used for
  stale detection). Registered defensively via `Schema.Lookup` before `Create` (mirrors
  the existing `ClashTagSchema.GetOrCreateTagSchema`).
- **Separate from `ClashTagSchema`** so Clash Finder's clear-pass (which deletes
  `ClashTagSchema`-tagged dimensions) never touches auto-dimensions, and vice-versa.
- **On run:** query the view's `Dimension`s, keep only those carrying a valid owner Entity.
- **Reconcile = clear-then-replace (Tier 1):** delete all prior engine-owned dimensions,
  then place the freshly computed set. Update-in-place noted as a future enhancement only.
- **Stale cleanup:** an owned dimension whose `TargetKey` no longer resolves this run is
  deleted and reported. No orphans left behind.
- Owner stamp + clear-then-replace + stale cleanup + placement all run in **one
  transaction** inside `AutoDimensionEventHandler`.
- User-placed dimensions (no owner Entity) are never deleted or modified.

---

## Part A — target resolution (two first-class peers)

### GRID (~95%)
`GridTargetResolver`: resolve to the grid's `Reference` (`new Reference(grid)`, guarded by
bubble/visibility like `BatchDimensionEventHandler` does). Clean, no slab dependency.

### SLAB_EDGE — horizontal offset (the real complexity), quarantined
`SlabEdgeTargetResolver` only. Measurement axis is a **parameter** (default = horizontal
view axis) so a vertical-clearance variant is later a config change.

- **Candidate generation:** `get_Geometry(new Options { ComputeReferences = true,
  IncludeNonVisibleObjects = true, View = view })` over floors/slabs AND floor-opening /
  shaft boundary faces, **host and linked** docs. Collect `Face.Reference` (face refs, not
  edge refs). Linked refs go through `LinkRefHelper`.
- **Filters (hard reject):**
  - **AXIS MATCH** — keep faces whose normal is parallel to the horizontal measurement
    axis within ~15°; **reject** top/bottom slab faces (vertical normal). Mirror of a
    clearance dim — not inverted.
  - **SIDE** — face must lie on the side the dimension is pulled toward
    (`sign((faceMid − source)·axis) == pullSign`).
  - **DISTANCE CAP** — reject projected distance > configurable cap.
  - **VALIDITY** — prefer true slab/opening boundary faces over joints/thickenings/stubs.
- **Scoring** (lower = better): `S = projDist + kAxis·axisDevDeg − kLen·faceSpan`.
  Primary key projected distance; tiebreak longer/more-primary boundary.
- **Ambiguity:** if `|S₁ − S₂| < ambiguityThreshold`, **do not guess** — record both in the
  plan as an ambiguity for user resolution.
- Dimension via the **face** reference.

---

## Layout scoring (Part B, Tier 1 greedy)

Two coordinate spaces: dimension geometry in **view/model** coords; spacing is **paper**
space scaled by view scale (`view.Scale`). Collision set is a small, **injected
predicate** (`ICollisionSource`): existing MEP dims, MEP tags/text, the source lines —
**arch/struct halftone background excluded entirely**. No plan-dimensioning tiering.

- **Hard (must reach 0):** dim-string overlaps another dim/tag/text bbox; string off-crop;
  witness line crosses text.
- **Soft (minimise):** segment text cramped (text width > segment length); uneven stacked
  spacing vs target (default **3/8"**); excessive leadering.
- **Operators, applied greedily per cramped segment in order:** flip → stagger → leader-out;
  pick the first that clears overlap / lowest penalty. String-level overlap → shift string
  side/offset by one spacing increment; split if still cramped.
- **Convergence:** loop until hard == 0 and soft delta < ε for 2 iterations, or
  iteration/time cap. Emit the `DimensionPlan`.
- **Precision/tolerance:** default **1/8"** imperial (round metric to 0/5 mm); never inherit
  1/256". Applied to the dimension type / display, not by mutating model geometry.
- **Determinism:** order every collection by stable keys (ElementId, then coordinates), no
  `HashSet` iteration order dependence, no parallel loops → identical input ⇒ identical plan.

---

## Pipeline (matches the eight steps in the brief)

1. `SourceIngest` — collect tagged cross-lines, validate endpoint refs, report invalid.
2. Resolve targets (grid OR nearest-reasonable slab face; resolve linked refs).
3. `AutoDimensionEngine` builds the abstract view-space layout model + collision set.
4. `LayoutScorer` evaluates hard + soft.
5. `GreedyLayoutEngine` applies operators.
6. Loop to plateau / cap → emit `DimensionPlan`.
7. `AutoDimensionCommit` (one transaction): delete prior owned + stale, place new, stamp.
8. Report: unresolved targets, ambiguous faces, missing link refs, stale deletions,
   unsatisfiable layout.

---

## Honest complexity assessment

- **Tier 1 (this plan):** greedy, single-pass-with-bounded-retries layout + nearest-face
  scoring. Predictable, deterministic, fast. It will *not* find the globally tightest
  arrangement in the most congested clusters — it resolves each conflict locally and stops.
- **Tier 2 (future, not now):** scored local search — hill-climb over operator combinations
  using the same `LayoutScorer`, with backtracking when a local move raises the global
  penalty. Adds: a move-generation/undo model, a global-vs-local acceptance test, and
  tunable weights. Materially more code and tuning; **not** justified until Tier 1's greedy
  output is seen failing on real dense views.
- We deliberately do **not** jump to a metaheuristic/ILP solver.

---

## Files changed / added

**Added:** everything under `Source/Tools/Testing/AutoDimension/` (Core, Resolvers, schema,
commit, config, engine, handler, viewmodel) + `Source/Commands/Testing/AutoDimensionCommand.cs`.

**Modified:** `Source/App.cs` — create `AutoDimensionEvent`/handler, add one ribbon button
in the Testing/Developer area next to Clash Finder.

**No changes** to `ClashEngine`, `ClashTagSchema`, or any existing tool — the new engine is
a pure downstream consumer of the tagged cross-lines.

---

## Risk register → mitigation

| Risk | Mitigation |
|---|---|
| Vertical slab/opening face refs in section/plan view | `Options{ComputeReferences,IncludeNonVisibleObjects,View}`; filter by normal; use `Face.Reference` not edge refs |
| Linked refs for slabs/grids/openings | `LinkRefHelper` (CreateLinkReference / stable-rep); report when unobtainable |
| Source-line ref validity | validate `GetEndPointReference`; report invalid lines |
| ES schema lifecycle | hardcoded constant GUID; `Schema.Lookup` before `Create` |
| Regeneration cost | abstract model, commit once |
| Read-only move semantics | final geometry known before create; relocate via recreate at commit |

---

## Notes on process

- The wizard/report UI step will be built **with the `/revit-navisworks-ui` skill** per
  CLAUDE.md before any XAML/ViewModel code.
- This is a large Tier-1 deliverable; suggest landing it on its own branch as one logical
  change.
- Post-change silent-failure scan will run before reporting complete.

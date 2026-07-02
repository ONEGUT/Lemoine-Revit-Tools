# Plan — Make the Split Element Tools Work Better and Be More Universal

Audit of the four split tools (`Split by Levels`, `Split by Grid Lines`, `Split by Ref Plane`,
`Split by Cell` — `Source/Tools/T04-ModifyElements/`) explaining why they underperform, and a
staged plan to fix them.

---

## Part 1 — Why they "don't all seem to be working the best"

### 1.1 The pickers promise everything; the engine supports almost nothing (worst offender)

The ByLevel / ByGrid / ByRefPlane commands fill the category picker with **every category in the
document — Model AND Annotation** (`SplitByGridCommand.cs:46-52`, same in the other two), grouped
by discipline. But the engine (`SplitElementsShared`) only has strategies for:

- Walls (parameter or curve strategy)
- Level-constrained columns (parameter strategy, level split only)
- Elements with a **straight-line** `LocationCurve` (curve strategy)

Everything else — floors, doors, fittings, rooms, generic models, all annotation categories —
runs and produces `— skipped: no applicable strategy`. To the user that reads as "the tool
doesn't work."

The curated supported-category lists that document exactly what IS supported —
`SplitElementsShared.LevelSplitCategories` / `GridSplitCategories` (lines 36-91) — are **dead
code**: nothing references them. This directly violates the repo UX rule: *hide invalid options
rather than show-all-then-skip-and-log*.

### 1.2 MEP splits destroy connectivity

`SplitCurveElement` calls `DisconnectAllConnectors(seg)` on **every** segment **including the
original** (`SplitElementsShared.cs:544`), so even the two retained run endpoints lose their
elbow/tee/equipment connections. After a split, the whole run is orphaned from its network and
the new segments are butt-jointed with no unions. Systems break, connected-move breaks.

Revit has purpose-built APIs the tools don't use: `MechanicalUtils.BreakCurve` (ducts) and
`PlumbingUtils.BreakCurve` (pipes) split **in place**, preserving end connectivity, no
copy/delete dance. (No equivalent exists for conduit/cable tray/framing — copy-trim stays for
those, but it must stop disconnecting the retained ends.)

### 1.3 Straight lines only

- The curve strategy gates on `lc.Curve is Line` — **arc walls and curved MEP runs are skipped**.
- `TryBuildGridPlane` only handles `Line` grids — **radial/arc grids and multi-segment grids are
  silently dropped** (no plane, no log naming the grid).

### 1.4 Unconnected-height walls can't be split by level

`SplitWallByLevel` requires both a base **and top level constraint**; a wall whose top is
"Unconnected" (extremely common) is skipped as "not level-constrained". The top elevation is
trivially computable from base + `WALL_USER_HEIGHT_PARAM`.

### 1.5 Split by Cell is a separate universe with fidelity losses

- Floors/ceilings/roofs/foundations/filled regions can **only** be split into uniform cells. You
  cannot split a floor at grid lines or a roof at a ref plane — even though the boolean-intersect
  + recreate machinery in `SplitByCellHelpers` would take arbitrary cutting planes just as easily
  as cell boxes.
- Recreated elements keep only: level, height offset, structural flag. **Lost:** all other
  instance parameters (Mark, Comments, phase created/demolished, workset), floor shape edits /
  slope arrows, roof slopes.
- `CreateRoof` rebuilds from `loops[0]` only — **roof holes are dropped**.
- `ExtractTopFaceLoops` requires a horizontal top face — **sloped slabs/roofs fail outright**
  (`NoCellsIntersected`).
- Wall footings (`WallFoundation`) fall inside the offered "Structural Foundations" category but
  throw `NotSupportedException` at run time.

### 1.6 Inconsistencies between the four tools (drift from 4× duplication)

Three near-identical handlers + three near-identical ViewModels + four near-identical commands
(~1,800 lines differing only in the boundary type). They have already drifted:

| Concern | ByLevel/ByGrid/ByRefPlane | ByCell |
|---|---|---|
| Category matching | Localized `Category.Name` strings (breaks non-English Revit) | `BuiltInCategory` (explicitly "so matching works in non-English Revit") |
| Transactions | One transaction for the whole run — a commit-time failure rolls back **everything** | Per-element transaction inside a `TransactionGroup` — one bad element can't sink the run |
| Progress bar | `onProgress` fired only once at 100% (bar dead during run) | Ticked per element |
| Default scope | Whole document (active-view opt-in toggle) | Active view always |

### 1.7 Missing guards (repo-standard)

- **No unpin/restore** around curve reassignment / parameter sets (CLAUDE.md: "Unpin before
  transforming") — pinned elements just fail.
- **No group pre-check** — copy/delete of group members throws and logs a raw Revit message
  instead of a clear "in a group — skipped".
- Splitting a wall by grid copies the wall N times, so **hosted doors/windows are duplicated per
  copy** and culled by Revit as each copy's curve shrinks — outcome depends on Revit warnings the
  user never confirmed; no pre-warning is given. Stacked/curtain/profile-edited walls aren't
  pre-filtered either.

---

## Part 2 — Plan

### Phase A — Truthful UI + de-duplication (highest value/effort ratio)

1. **Drive the category pickers from the curated supported lists.** Resurrect
   `LevelSplitCategories` / `GridSplitCategories` (add a ref-plane list = grid list), keyed by
   `BuiltInCategory`, shown with per-category element counts and the strategy note as the row
   description. No Annotation tab. Pre-selection filters to supported categories and logs what it
   excluded ("12 of 40 selected elements are splittable; 28 excluded: Doors (5), …").
2. **Match categories by `BuiltInCategory`**, not `Category.Name` (adopt the ByCell approach).
3. **Collapse the three plane-split handlers/VMs/commands into one** parameterized by a small
   "boundary source" config: display keys, boundary noun, refresh collector, and a
   planes-from-selection function (levels → horizontal planes; grids → vertical planes;
   ref planes → the plane itself). One code path, no more drift; ~1,200 lines removed.
4. **Adopt the ByCell transaction pattern** (per-element `Transaction` in a `TransactionGroup`)
   and per-element `onProgress` ticks in the unified handler.

### Phase B — Engine correctness (make supported things actually good)

5. **Ducts/pipes: switch to `MechanicalUtils.BreakCurve` / `PlumbingUtils.BreakCurve`** —
   in-place split, end connectivity preserved. Add an optional "insert unions at cuts" toggle
   (`doc.Create.NewUnionFitting`) — skip-and-log where the routing preferences have no union.
6. **Conduit/cable tray/framing: keep copy-trim but preserve retained-end connections** — only
   disconnect connectors that fall strictly inside the removed span of each segment; never touch
   the original's surviving endpoint.
7. **Unconnected-height walls**: compute top elevation from base + height, split into segments
   with explicit top constraints/heights.
8. **Guards**: unpin → modify → restore pin; pre-check `Element.GroupId` and skip with a clear
   reason; pre-warn (log) when a wall to be curve-split hosts inserts; skip stacked/curtain walls
   with named reasons.
9. **Arc support**: generalize plane intersection to any bound curve (parameter-space sign-change
   bisection against the plane) and build sub-segments with `Curve.MakeBound(t0, t1)` clones
   instead of `Line.CreateBound` — arc walls, curved framing and curved conduit become
   splittable. Multi-segment grids contribute one plane per straight segment; arc grids that
   still can't produce a plane are skip-logged **by name**.

### Phase C — Universality: one engine = targets × cutters

10. **Unify the plane machinery and the cell machinery** behind a strategy registry keyed by
    element kind, and a cutter abstraction:
    - Strategies: `LevelConstrained` (walls/columns), `LinearCurve` (MEP/framing),
      `SheetSolid` (floors/ceilings/roofs/foundations/filled regions — the existing
      boolean-intersect + recreate path, generalized from cell boxes to arbitrary half-space
      plane sets).
    - Cutters: Levels, Grids, Reference Planes, Cell grid — and cheaply extensible to
      "every N feet along the element" (stick-length/spool splitting) and "picked points" later.
    - Each tool window = one cutter + the shared target picker, which lists **only** categories
      that have a strategy compatible with that cutter.
    - Immediate wins: split floors/roofs at grid lines, split filled regions at ref planes,
      split walls by cell — no new geometry code, just the cross-wiring.
11. **Recreation fidelity** (`SheetSolid` path): copy all writable non-geometry instance
    parameters + phases + workset to the new cells; carry **all** loops for roofs (holes);
    detect shape-edited/sloped sources and skip-and-log rather than silently flattening.

### Suggested order & scope

- Phase A is one branch (`split-tools-unify-ui`), pure consolidation + picker truthfulness — no
  behavior change for currently-working cases.
- Phase B is one branch (`split-engine-fixes`) — behavior changes need a Windows/Revit smoke
  test (BreakCurve, unions, unpin).
- Phase C is one branch (`split-universal-engine`) after A+B land.

### Files touched (A+B)

| File | Change |
|---|---|
| `Source/Tools/T04-ModifyElements/SplitElementsShared.cs` | BreakCurve dispatch, connector preservation, unpin/group guards, arc generalization, unconnected walls |
| `Source/Tools/T04-ModifyElements/SplitByLevel/Grid/ReferencePlane{EventHandler,ViewModel}.cs` | collapse into `SplitByPlanesEventHandler` + `SplitByPlanesViewModel` + per-tool config |
| `Source/Commands/T04-ModifyElements/SplitBy*.cs` | thin per-tool configs over one shared command body |
| `Strings/en/modify.splitBy*.json` | new keys for exclusion logs, union toggle, skip reasons |
| `Source/Tools/T04-ModifyElements/SplitByCellHelpers.cs` (Phase C) | plane-set cutters, param/phase/workset copy, roof holes |

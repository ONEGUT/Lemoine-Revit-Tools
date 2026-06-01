# Plan — Dimension overlap avoidance for ClashDimension

## Goal

Stop placed dimensions from landing on top of (a) each other, (b) the clash
markers (filled-region circles + cross lines) this tool just drew, and
(c) pre-existing annotations already in the view. Today every dimension is
placed at exactly `marker position + DimLineOffsetMm`, with no collision check,
so dimensions can stack and overlap markers/text.

Two mechanisms, layered. Mechanism A (stagger) is cheap and always on.
Mechanism B (bbox probe) is the accurate, opt-in layer on top.

---

## Where the current placement happens

`Source/Tools/Testing/ClashDimension/ClashDimensionEventHandler.cs`

- `EmitSingle` (≈ line 1034): `alongFixed = (measureIsX ? it.My : it.Mx) + dimLineOffsetFt;`
- `EmitChain`  (≈ line 1056): `alongFixed = alongAbs + dimLineOffsetFt;`
- `MakeDimLine` (≈ line 1073): builds the `Line` at `alongFixed`.
- `doc.Create.NewDimension(view, dimLine, refs, dimType)` is the actual place call.

`alongFixed` is the offset axis (the Y of a horizontal dim, the X of a vertical
dim). Overlap is resolved by **increasing `alongFixed`** (pushing the dimension
line further from the markers) until it is clear.

---

## Mechanism A — Deterministic stagger (cheap, no geometry queries)

### Idea
Give each successive dimension on the same edge/axis its own lane: add
`laneIndex × step` to `dimLineOffsetFt`. Two dimensions at the same base offset
fan out instead of stacking.

### API used
- `DimensionType.get_Parameter(BuiltInParameter.TEXT_SIZE).AsDouble()` → text
  height in paper-space feet (same call already used in `LegendCreatorEventHandler.ModelFontH`).
- `View.Scale` (int) → multiply paper-space height to model space.
- `step = textHeightFt × view.Scale × StaggerFactor` (factor ≈ 2–3 so lanes
  clear text + arrowheads).

### Changes
1. In `EmitEdgeDimensions`, track an `int lane` counter; increment each time a
   dimension is actually placed (single or chain).
2. Pass `lane` into `EmitSingle` / `EmitChain`; add `lane × step` to the offset.
3. Reset `lane` per edge call (one X edge, one Y edge) — so the single X dim and
   single Y dim each start at lane 0; they only stagger against siblings on the
   **same** axis (e.g. multiple parallel clusters / skew singletons).

### Cost
Zero API queries, no `Regenerate`. Deterministic, fast even for 500 clashes.

### Limitation
Doesn't *know* where text actually lands — it assumes a uniform lane height. Two
dims of very different length can still clip if the shorter one's text sits under
the longer one's witness line. Good enough for the common case; Mechanism B
covers the rest.

---

## Mechanism B — Bounding-box probe (accurate, opt-in)

### Idea
Before keeping a dimension, place it, ask Revit for its real bounding box, test
it against an obstacle set, and if it overlaps, push `alongFixed` out by `step`
and retry — up to a capped number of lanes. The marker circles/cross lines are
known to us already (no query needed); existing annotations are collected once
per view.

### API used
- **Obstacle collection (existing annotations), once per view:**
  ```csharp
  var cats = new List<BuiltInCategory> {
      BuiltInCategory.OST_Dimensions,
      BuiltInCategory.OST_TextNotes,
      BuiltInCategory.OST_GenericAnnotation,
      // tags: OST_*Tags as needed
  };
  var filter = new ElementMulticategoryFilter(cats);
  var existing = new FilteredElementCollector(doc, view.Id)
      .WherePasses(filter)
      .WhereElementIsNotElementType()
      .Where(e => e.LookupParameter("Mark")?.AsString() != "LemoineCD") // skip our own
      .Select(e => e.get_BoundingBox(view))
      .Where(bb => bb != null)
      .ToList();
  ```
  `Element.get_BoundingBox(View)` returns a model-space `BoundingBoxXYZ` for a
  view-specific annotation (already used elsewhere in this repo, e.g.
  `BatchDimensionEventHandler.cs:146`).

- **Our own markers** — no API needed. We already know each marker's `Cx, Cy`
  and the circle `radius` + cross `armLen`. Store the marker's XY bbox in the
  `ClashMarker` struct when it's created (add `MinX/MinY/MaxX/MaxY`), so the
  probe tests against the exact drawn extents with zero extra geometry calls.

- **The just-placed dimension's real bbox:**
  ```csharp
  var dim = doc.Create.NewDimension(view, dimLine, refs, dimType);
  doc.Regenerate();                       // make geometry/text valid this txn
  BoundingBoxXYZ db = dim.get_BoundingBox(view);
  ```
  `Document.Regenerate()` is required — until it runs, a freshly created
  dimension reports no/stale geometry.

- **Move instead of recreate (cheaper retry):**
  ```csharp
  ElementTransformUtils.MoveElement(doc, dim.Id, shiftVector);
  doc.Regenerate();
  db = dim.get_BoundingBox(view);
  ```
  `shiftVector` is `(0, step, 0)` for a horizontal dim or `(step, 0, 0)` for a
  vertical dim. Loop until clear or `MaxLanes` reached.

- **Overlap test** — 2-D AABB intersection in XY (plan view), reusing the
  existing `BBoxOverlap` helper already in this file (extend to ignore Z).

### Control flow (per dimension)
```
place at base offset → Regenerate → get bbox
while (overlaps any obstacle) and (lane < MaxLanes):
    MoveElement by step → Regenerate → get bbox; lane++
if still overlapping at MaxLanes: keep last position, log "could not fully clear"
add the final bbox to the obstacle set  ← so later dims avoid it too
```

### Cost / tuning
- `Regenerate()` per move is the expensive part. With at most **2 dimensions per
  run** (post the last fix) and a small `MaxLanes` (e.g. 6), worst case is ~12
  regenerations — negligible. If grouping is off and many singletons are placed,
  cost scales with dim count × lanes; `MaxLanes` caps it.
- Add each kept dimension's bbox to the obstacle list so the second axis avoids
  the first.

---

## New settings (the "additional options" this introduces)

Add to `ClashDimensionSettings.cs` (public DTO, XmlSerializer-safe), mirror as
per-run properties on the handler, and surface in `ClashDimensionViewModel`
Step 5 (WPF — `/revit-navisworks-ui` before editing XAML/rows):

| Setting | Type | Default | Meaning |
|---|---|---|---|
| `AvoidOverlaps` | bool | `true` | Master toggle. Off = today's behaviour. |
| `OverlapMode` | string | `"Stagger"` | `"Stagger"` (A only) or `"Probe"` (A + B). |
| `StaggerFactor` | double | `2.5` | Lane height = textHeight × scale × factor. |
| `MaxLanes` | int | `6` | Probe retry cap before giving up. |
| `AvoidExisting` | bool | `true` | Probe also dodges pre-existing annotations (B), not just our own markers. |

Each maps to: DTO field → handler property → VM field + Step 5 row + review
card + persist/push (same three-touch pattern used for `GroupToleranceMm`,
`ClusterGapMm`, `FallbackColorHex`).

---

## Files to change

1. **`ClashDimensionEventHandler.cs`**
   - Extend `ClashMarker` struct with `MinX/MinY/MaxX/MaxY` (marker extents).
   - `EmitEdgeDimensions`: lane counter + step computation; build obstacle list.
   - `EmitSingle` / `EmitChain`: accept `lane`/`step` + obstacle list; implement
     stagger always, probe loop when `OverlapMode == "Probe"`.
   - New helpers: `BuildObstacles(doc, view)`, `DimStepFt(doc, view, dimType)`,
     `Overlaps2D(BoundingBoxXYZ, List<...>)`, `MarkerBBoxes(markers)`.
   - Reuse existing `BBoxOverlap` (add an XY-only overload).
2. **`ClashDimensionSettings.cs`** — 5 new fields above.
3. **`ClashDimensionViewModel.cs`** — 5 new Step-5 rows + review cards + persist
   + push to handler. (WPF — invoke `/revit-navisworks-ui` first.)

---

## Risks / notes

- **Cannot build on Linux** (per CLAUDE.md) — verify by code review + your
  Windows build.
- `get_BoundingBox(view)` on annotations is **approximate** (Revit pads it);
  the probe may leave small visual gaps or, rarely, a slight clip. Acceptable
  for an auto-placement aid the user can still nudge.
- `Regenerate()` mid-transaction is supported but must stay inside the existing
  single `Transaction`; no new transaction is opened.
- Probe with `AvoidExisting` makes placement depend on what's already in the
  view, so re-running after manual edits can move dimensions — documented as
  intended behaviour of the toggle.
- Silent-failure scan will run on the diff before reporting complete
  (`get_BoundingBox` null returns guarded; `Regenerate`/`MoveElement` wrapped).

---

## Branch

Designated feature branch: `claude/dimension-grouping-tolerance-O9d74`
(continuation of the current dimension work). **Confirm:** continue on this
branch, or base a fresh branch off `main`?

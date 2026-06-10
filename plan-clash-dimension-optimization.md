# Plan — Clash & Auto-Dimension Tuning: Grouping, Layout v2, UI

Covers the requested optimization pass over the clash + dimensioning functions:
grouping consistency, a deeper "estimation space" for the layout (witness lines,
dimension lines, arc leaders, crossing minimization, shared-row alignment,
multi-pass stacking), protection of pre-existing annotations, drafting-standard
defaults, and UI/options cleanup.

---

## 1. Diagnosis — what the code does today and where it falls short

### 1.1 Grouping (`ClashRunGrouper.cs`)

Runs are grown greedily: seed on the lowest-ElementId unused clash, take its
nearest neighbour within `RunGapMm` to set the axis, then absorb the best
collinear point until none qualifies. Four concrete causes of the
"should have grouped but didn't" feeling:

1. **First-come-first-served membership.** Points are consumed by whichever run
   grows first (iteration order = ElementId string order, not spatial fit). A
   clash sitting between two racks joins the run that reached it first — or gets
   absorbed into a *perpendicular* neighbour's run and is then unavailable to
   its true rack.
2. **Seed-axis sensitivity.** The run direction comes from the single nearest
   neighbour. If the nearest clash is sideways (a parallel pipe in the next
   rack, a crossing branch), the axis starts wrong; growth then rejects the real
   run members (they fail the 0.5 ft cross test against the wrong line) and the
   run fossilizes as a pair. The principal-axis refit can't recover because the
   wrong member is already in.
3. **No merge / reassignment pass.** Two halves of one physical run that were
   seeded separately (because a middle clash got claimed elsewhere) are never
   reunified, and a solo clash that clearly sits on an existing run's line is
   never re-attached.
4. **Zero explainability.** The log reports only "N clashes → M runs". When a
   pair fails to group, nothing says whether it was the along-gap (> 5 ft) or
   the off-line tolerance (> 0.5 ft), so the knobs can't be tuned with
   confidence — exactly the "could be me not understanding the distance limit"
   experience.

### 1.2 Layout estimation space (`Core/` + `AutoDimensionCommit.cs`)

The Tier-1 model is a single fat AABB per dimension (`DimGeometry.RecomputeBounds`)
plus **one** witness box at the source anchor (`LayoutScorer.WitnessBox`). Gaps:

1. **Witness lines are barely modeled.** A chained string has one witness per
   reference (sources + target); none of them exist in the model except a
   single box at the chain's source point. The two lines that drop from each
   dimension and the line running across are invisible to the scorer.
2. **No crossing tests at all.** Everything is AABB overlap-area; a dimension
   line crossing another string's witness scores zero if their fattened boxes
   happen not to overlap, and heavily if they merely sit near each other. The
   drafting rule — *dimension lines must never cross extension lines or other
   dimension lines* (ASME Y14.5 §1.7.2) — is not representable with boxes alone.
3. **Moved tags and their arc leaders are invisible at plan time.** Stagger /
   flip / leader-out columns are realized only in `AutoDimensionCommit.PlaceColumn`,
   after layout scoring is over. The plan claims a band; the realized tags + arcs
   can land well outside it.
4. **Commit-time tag columns ignore static annotations.** `PlaceColumn` slides
   tags only against `placedTags` (tags placed this run). Existing text notes,
   independent tags, prior dimensions, and the clash markers are never tested —
   this is the "covers annotations placed before it" failure.
   `CollectObstacles` also omits **FilledRegions** (our own clash markers) and
   **SpotDimensions**, so even the plan-time band can sit on a marker.
5. **Stacking order is backwards vs the standard.** The greedy sorts *more
   segments first* so long chains claim the closest offsets. Drafting standard
   is *shortest dimension nearest the object, longest farthest out* — that
   ordering is what structurally prevents dimension lines crossing extension
   lines (McGill / Engineering Essentials; ASME Y14.5).
6. **No shared-row alignment.** Two dimensions measuring opposite directions
   from the same run (left to grid A, right to grid B) each pick an offset
   independently. The NCS rule is "align dimensions in one line and group
   dimension lines as much as possible" — they should share one line level.
7. **Single greedy ordering, no repair pass.** `Arrange` re-runs whole passes
   but always places in the same order; nothing revisits the worst offender
   with full freedom once everyone else is down. With `MaxOffsetSteps = 8`,
   dense areas saturate and the plan just notes "some strings may still overlap".

### 1.3 UI / options

1. **Split + duplicated surfaces.** `GlobalSettingsWindow.Dimensions.cs` edits
   persisted defaults; wizard step 4 re-exposes ChainAligned / RunGap / RunCross /
   target as per-run overrides via mutate-then-restore of the global singleton
   (`ClashFinderEventHandler.cs:86–181`) — workable but fragile and visually
   disconnected ("are these the same settings?").
2. **Opaque knob names.** "Run gap" and "Run cross tolerance" don't explain what
   they group; nothing shows the *outcome* of a chosen value.
3. **mm-backed fields edited in feet** (`RunGapMm` ÷ 304.8 in four places) —
   noise and a standing unit-bug risk.

---

## 2. Drafting standards to encode (research summary)

| # | Rule | Source |
|---|------|--------|
| R1 | First dimension line ≥ 3/8" (10 mm) off the object; uniform 1/4" (6 mm) between subsequent parallel lines | ASME Y14.5 §1.7.1; ISO 129-1 |
| R2 | Witness gap ~1/16" from object; witness extends ~1/8" past outermost dimension line | ASME Y14.5 §1.7.2 |
| R3 | Witness lines may cross each other/object lines; **dimension lines never cross witness lines or each other**; break the witness, never the dimension, at unavoidable crossings | ASME Y14.5 §1.7.2 |
| R4 | Shortest dimension nearest the object, longest farthest out | Y14.5 convention; McGill drafting guide |
| R5 | Align dimensions on a common line; group dimension lines (esp. those sharing a datum) | US National CAD Standard via SourceCAD |
| R6 | Stagger stacked values so they don't column-collide; degradation order: text inline → moved beside → leader out | ASME Y14.5 §1.7.5 |
| R7 | Leaders short, 30°–60°, never crossing each other | ToolNotes / McGill |
| R8 | MEP coordination: dimension to the nearest gridline(s), element/sleeve **centerline** located to two nearest grids | AGC MEP Spatial Coordination; United-BIM |
| R9 | Annotation never obscures other annotation | NCS |

The current pipeline already honours R6's degradation order and R8's
grid-target model. R1–R5, R7, R9 are the new work.

---

## 3. Proposed changes

### Phase 1 — Grouping v2: best-fit runs + explainability
*(`ClashRunGrouper.cs`, `AutoDimensionEngine.cs`; Revit-free)*

1. **Best-fit growth instead of first-come-first-served.** Replace per-seed
   greedy growth with a global agglomerative pass: start every clash as its own
   cluster, repeatedly merge the *best* qualifying pair of clusters
   (qualification: merged principal-axis line keeps every member within
   `crossTol` and adjacent along-axis gaps ≤ `runGap`; best = lowest combined
   perpendicular residual, ties by key). This removes seed-order and
   seed-axis sensitivity in one move and naturally performs the merge pass —
   two collinear half-runs satisfy the merge test and reunify.
2. **Member-to-member gap semantics.** Measure the along-gap between *adjacent
   members*, not from the centroid-projected extent — matches the intuitive
   reading of "distance limit".
3. **Reassignment pass.** After clustering, re-test every solo clash against
   each run's fitted line; attach if within both tolerances (nearest line wins).
4. **Near-miss diagnostics.** During clustering record the best rejected merge
   per cluster; emit run-log lines like
   `Run grouping: clash 12345 ↔ run0003 not merged — along gap 6.3 ft > 5.0 ft (off-line 0.2 ft ok)`
   plus a `plan.Notes` summary. This makes the two knobs self-teaching.
5. Determinism preserved (sorted keys everywhere), same public surface
   (`Build(sources, crossTolFt, gapFt)`), so the engine call sites don't change.

### Phase 2 — Estimation space v2: true dimension anatomy
*(new `Core/Seg2.cs`, new `Core/DimAnatomy.cs`; `DimensionPlan.cs`,
`DimensionChainer.cs`, `LayoutScorer.cs`, `DimGeometry.cs`,
`AutoDimensionEngine.cs`, `AutoDimensionCommit.cs`)*

1. **Per-reference anchors in the plan.** `PlannedDimension` gains
   `RefAnchors : List<Vec2>` (the deduped axial positions the chainer already
   computes). This is the data every later feature needs.
2. **`DimAnatomy` — computed line/box primitives** for a dimension at its
   current side/offset/text states:
   - dimension line as a `Seg2` (segment with segment×segment intersection);
   - one witness `Seg2` per ref anchor, from anchor (+1/16" paper gap) to the
     dimension line (+1/8" overshoot) — R2;
   - per-segment text boxes at their *actual* planned spots: inline at segment
     centre, or at the moved-column slot (see 3);
   - leader approximations: a 2-segment polyline from each moved tag's front
     edge to its anchor on the dimension line (good enough to count crossings
     of the real arc leaders).
3. **Plan-time tag columns.** Port `PlaceColumn`'s column math (front-edge
   alignment, nearest-lowest stacking) into the core so the layout *plans* the
   moved-tag boxes and leaders. `AutoDimensionCommit` then realizes the planned
   slots, re-measuring only against the realized `ValueString` width — and now
   also slides against the **static obstacle set**, which the engine passes
   into commit (fixes 1.2-4).
4. **Scorer v2** (keeps AABB broad-phase for cheap rejection):
   - HARD: dimension line crosses any witness of another dimension or another
     dimension line (R3);
   - HARD: any text box (inline or moved) overlaps obstacles / other text;
   - SOFT-high: leader × leader crossing (R7); SOFT: leader × witness/dim-line
     crossing, leader length;
   - existing overlap/off-crop/cramped/uneven terms stay.
5. **Obstacle set completed:** add `FilledRegion` (clash markers) and
   `SpotDimension` to `CollectObstacles` so "annotations placed before"
   includes our own markers and elevation tags (R9).

### Phase 3 — Stacking: row manager, alignment, multi-pass repair
*(`GreedyLayoutEngine.cs` + new `Core/RowPlanner.cs`)*

1. **Standards-true ordering.** Within each *corridor* (dims with the same axis
   whose perpendicular extents overlap), place **shortest span first** so short
   dims take the inner rows (R4). Across corridors keep the current
   deterministic order.
2. **Discrete rows.** Offsets become lanes at `FirstOffset + k·Spacing`
   (the scorer's snap-to-grid term becomes structural). Lane spacing per R1
   defaults (see Phase 4).
3. **Shared-row alignment (R5).** After initial placement, a snap pass: dims on
   the same axis & side whose lanes differ by < half a lane and whose spans
   don't overlap along-axis (e.g. opposite-direction pairs off one run) are
   pulled onto one shared lane → their dimension lines read as a single
   aligned line. Implemented as a soft "alignment reward" plus a deterministic
   post-snap, so it never creates new hard violations.
4. **Multi-pass repair.** After the greedy pass: rank dimensions by local score,
   re-place the worst with a full (side × lane × text-state × column-slot)
   search against the frozen rest, accept on total improvement, repeat until
   plateau / `TimeCapMs`. Deterministic (no randomness), bounded, and exactly
   the "multiple passes at stacking in estimation space before committing".
5. **Honest saturation.** If hard violations remain, prefer leader-out /
   farther lane over overlap, and note *which* constraint failed per dimension.
6. **Stagger stacked values (R6):** soft bonus for alternating along-axis text
   offsets on adjacent lanes in a corridor.

### Phase 4 — Standards defaults + config v6
*(`LayoutConfig.cs`, `AutoDimensionConfig.cs`)*

- `FirstOffsetFt`: 1/4" → **3/8"** paper (R1); `StringSpacingFt`: 1/2" → **1/4"**
  paper (R1 minimum; current 1/2" reads loose in dense areas — tunable as today).
- New paper constants: witness gap 1/16", witness overshoot 1/8" (R2), leader
  angle band 30°–60° (R7) for the anatomy/leader planner.
- New weights: `CrossingWeight` (hard), `LeaderCrossWeight`, `AlignmentReward`,
  `StaggerReward`. `SchemaVersion` → 6 with a migration that refreshes the two
  spacing defaults and seeds the new fields (same shape as v2–v5 migrations).
- Store run-grouping knobs natively in feet (`RunGapFt`, `RunCrossToleranceFt`)
  with v6 migration from the mm fields; delete the ÷304.8 conversions at the
  four call sites.

### Phase 5 — UI tuning
*(wizard step 4 in `ClashFinderViewModel.cs`, `GlobalSettingsWindow.Dimensions.cs`;
will invoke `/revit-navisworks-ui` before any code)*

1. **Plain-language knobs.** "Run gap (ft)" → "Group reach — clashes within this
   distance along a line join one chained dimension"; "Run cross tolerance (ft)"
   → "Line tolerance — how far off the line a clash may sit and still join".
   Same steppers, clearer labels/descriptions in both surfaces.
2. **Grouping feedback where the user looks.** The run log already prints
   "N clashes → M runs"; add the Phase-1 near-miss lines and surface a one-line
   summary in the step-5 review ("Grouping: 41 clashes → 9 runs; 3 near-misses
   — see log").
3. **Make the override relationship explicit.** Step 4 gets a small caption
   "Per-run overrides — saved defaults are in Settings → Dimensions" and an
   *edited* chip when a value differs from the saved default (no behavioural
   change to the snapshot/restore mechanism).
4. **Advanced expander** in Settings → Dimensions for: max rows
   (`MaxOffsetSteps`), shared-row alignment on/off, value staggering on/off.
   Scorer weights stay internal.

---

## 4. Files touched

| File | Phase | Change |
|------|-------|--------|
| `AutoDimension/ClashRunGrouper.cs` | 1 | agglomerative best-fit clustering, member-gap semantics, reassignment, near-miss capture |
| `AutoDimension/AutoDimensionEngine.cs` | 1, 2 | grouping diagnostics to log/notes; pass obstacles to commit; collect FilledRegions/SpotDimensions |
| `AutoDimension/Core/Seg2.cs` *(new)* | 2 | segment primitive + intersection |
| `AutoDimension/Core/DimAnatomy.cs` *(new)* | 2 | witness/dim-line/text-box/leader model |
| `AutoDimension/Core/DimensionPlan.cs` | 2 | `RefAnchors`, planned column slots |
| `AutoDimension/DimensionChainer.cs` | 2 | fill `RefAnchors` |
| `AutoDimension/Core/LayoutScorer.cs` | 2, 3 | crossing/leader/alignment/stagger terms |
| `AutoDimension/Core/DimGeometry.cs` | 2 | bounds from anatomy |
| `AutoDimension/AutoDimensionCommit.cs` | 2 | realize planned column slots; obstacle-aware `PlaceColumn` |
| `AutoDimension/Core/RowPlanner.cs` *(new)* | 3 | lanes, corridors, shortest-first, snap-align |
| `AutoDimension/Core/GreedyLayoutEngine.cs` | 3 | ordering, repair passes |
| `AutoDimension/Core/LayoutConfig.cs` | 4 | new constants/weights, new defaults |
| `AutoDimension/AutoDimensionConfig.cs` | 4 | v6 migration, ft-native run knobs |
| `ClashFinder/ClashFinderViewModel.cs` | 5 | step-4 labels, override caption, review summary |
| `ClashFinder/ClashFinderEventHandler.cs` | 4 | ft-native knob plumbing |
| `Lemoine/GlobalSettingsWindow.Dimensions.cs` | 5 | labels, advanced expander |

## 5. Out of scope (deliberately)

- **Clash detection itself** (`ClashEngine`): the inconsistent grouping lives in
  the dimension run grouper, not detection; markers stay one-per-element-pair.
- **Elevation finder dimensioning** — unchanged (spot-elevation pass only);
  it inherits the obstacle-set fix automatically if shared code is touched.
- Scorer-weight UI exposure; baseline-vs-chain mode switching (chain stays the
  model, per the existing hand-drawing reference).

## 6. Risks & verification

- The core (`Core/`, `ClashRunGrouper`) is Revit-free — Phase 1–3 logic can be
  exercised through a Developer-panel debug step with synthetic point sets if
  desired, but real verification is **Windows + Revit only** (per CLAUDE.md the
  project cannot build on Linux); dense-model behaviour, leader appearance, and
  commit-time text positions must be checked on a real plot.
- Phase 2/3 change layout output everywhere — existing auto-dimensions get
  re-laid-out on the next run (clear-then-replace already owns this).
- Phases land as separate commits on this branch so each can be built and
  eyeballed in Revit independently; phase order is dependency order
  (1 standalone; 2 before 3; 4/5 anytime after 2).

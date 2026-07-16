# Function Review — Split Elements (Modify panel)

Reviewed per `plan-function-review-framework.md` (all eight passes). Tools covered:
**Split by Cell** (deep-dive — reported as no longer cutting anything), **Split by
Grid**, **Split by Level**, **Split by Reference Plane**, their launch commands, the
shared engine `SplitElementsShared.cs`, and the `NumberRange` input control (Cell's
S2 depends on it). Extend Walls is a different operation and was left for its own run.

Every finding is tagged **Confirmed** (provable from the code) or **Needs Windows
test** (script in the appendix). Nothing has been changed — pick the findings you
want fixed and I'll apply them on this branch.

**Totals: 0 Critical · 6 High · 8 Medium · 6 Low.**

---

## Why "By Cell" probably cuts nothing — read this first

The split geometry itself (`SplitByCellHelpers.cs`) is **byte-identical to the version
that worked** — every commit since introduction only renamed types, externalized
strings, added the cancel break, and added payload clearing. So the break is not in the
cutting math; it's in what the run is *fed* or how failures are *hidden*. Three
confirmed mechanisms can each produce "runs, cuts nothing," and the Output log tells
them apart:

| What your Output log shows | Cause | Finding |
|---|---|---|
| `Operating on N pre-selected element(s).` then immediately `Done — 0 cell(s) created, N skipped` with **no per-element lines** | Stale pre-selection: the window replayed element ids that no longer exist (already split/deleted). Null lookups are skipped **silently**. | Cell-1-1 + Cell-1-2 |
| `No elements found for the selected categories in the active view.` | Scope is the view active in Revit **at the moment you click Run**, not when you opened the tool. A sheet, schedule, or different view active at run time → nothing found. | Cell-1-3 |
| Every element: `✗ … Cell element recreation failed.` or a Revit message | One bad cell aborts the **whole element** (solid path is all-or-nothing); grouped/owned elements also fail per element with Revit's raw message. | Cell-4-1 / Cell-4-3 |
| Every element: `✗ … boolean intersection returned no cells …` | Sloped roofs/floors have no flat top face, so cell extraction finds nothing — only dead-flat elements can split. The real reason (per-cell exceptions) is only in `diagnostics.log`. | Cell-4-2 + Cell-4-4 |
| Every element: `— … fits in one cell, skipped` | Cell size ≥ element size. Note S2's boxes render **empty** even though the tool silently runs 10 ft × 10 ft defaults (NR-1) — what you typed may not be what ran if a parse failed. | NR-1 / NR-3 |

Appendix test **T1** is a five-minute Windows script that walks this table.

---

## Split by Cell

### Cell-1-1 — Stale pre-selection ids are replayed and silently skipped
**High · Confirmed · Pass 1/7**
`SplitByCellCommand.cs:24-36` — the command is a singleton: clicking the ribbon button
while the window is still open (even minimized, behind Revit) only re-activates the old
window, whose `_preSelectedIds` were captured when it was **first** opened
(`SplitByCellViewModel.cs:49,82-83`, readonly). After a successful from-selection run
the originals are deleted, so a Reset + re-run (or any later run from that window)
re-sends dead ids — `doc.GetElement(id)` returns null for each
(`SplitByCellEventHandler.cs:104-111`) and the run ends `0 created, N skipped` with no
explanation. If the tool worked once and "stopped working," this is the most likely
mechanism.
**Fix:** log every null-id skip (see Cell-1-2); at run start in pre-selected mode,
resolve the ids and log "N of M selected elements no longer exist — reopen the tool to
re-capture the selection"; after a successful from-selection run, drop into a state that
requires Reload (the Reload button already re-captures on the main thread — plain Reset
does not).

### Cell-1-2 — Null-element skip has no log line
**High · Confirmed · Pass 3/4**
`SplitByCellEventHandler.cs:104-111` — the `el == null` branch increments `skipped` and
ticks progress but never calls `pushLog`. This is the exact branch that fires in the
Cell-1-1 scenario, which is why the failure presents as "didn't even register" —
violating the CLAUDE.md rule that a zero/skip result must say so.
**Fix:** `pushLog("— element {id}: no longer exists in the model, skipped", "info")`.

### Cell-1-3 — Run scope is the active view at Run-click time, with no indication and no alternative
**High · Confirmed · Pass 1/2**
`SplitByCellEventHandler.cs:31-32` reads `app.ActiveUIDocument.ActiveView` inside
`Execute` — i.e. whatever view is active in Revit when the ExternalEvent fires. The
window is modeless on its own thread, so between configuring and clicking Run the user
can (and does) change views; land on a sheet/schedule/3D-with-floors-hidden and the
collector finds nothing. Meanwhile the S1 count strip shows counts from the view that
was active **at open time** (`SplitByCellCommand.cs:47-57`) — the UI promises elements
the run then can't see. The three sibling tools already capture `_activeViewId` at open
time and offer an "active view / whole document" toggle; Cell has neither.
**Fix:** capture the view id at open (like the siblings), pass it through the handler
(`doc.GetElement(ActiveViewId) as View`), and log which view was used; optionally add
the same document-wide toggle for parity.

### Cell-4-1 — Solid path is all-or-nothing per element; one sliver cell kills the whole split
**High · Confirmed · Pass 4**
`SplitByCellHelpers.cs:134-139` — any cell whose recreation returns
`InvalidElementId` throws, rolling back **every** cell of that element ("Cell element
recreation failed."). The FilledRegion path does the opposite — it skips-and-logs
degenerate cells and keeps the good ones (`SplitByCellHelpers.cs:304-316`). A sliver
cell (grid line landing within short-curve tolerance of an element edge — common with
"Align grid to project origin" on a project modeled on the same module) makes
`Floor.Create` throw, so an element that would split 95% clean instead fails 100%.
**Decision needed:** keep atomic-per-element (current, safest) or match the 2D path —
skip-and-log unrecreatable cells (with a minimum-area guard) and only fail the element
if *no* cell could be built. My recommendation: skip-and-log with an explicit warn line
per dropped cell, since the original is deleted only after at least one cell exists.

### Cell-4-2 — Sloped roofs/floors can never split, and the log misdiagnoses them
**High · Needs Windows test (T2) · Pass 1/3**
`SplitByCellHelpers.cs:202-219` — `ExtractTopFaceLoops` only accepts planar faces with
normal Z ≥ 1−1e-6. A pitched roof (or sloped floor/ramp slab) has no such face → zero
loops for every cell → `NoCellsIntersected` → logged as "boolean intersection returned
no cells — the element's geometry may be non-planar…". Roofs are an advertised category
(`SupportedCategories`), but only dead-flat roofs can ever succeed — and `CreateRoof`
(`SplitByCellHelpers.cs:496-535`) rebuilds a *flat* footprint roof anyway, so even a
"successful" sloped split would flatten the roof.
**Fix:** detect the sloped case (element solid has no up-facing planar face) before the
cell loop and skip-and-log it honestly ("sloped — not supported for cell splitting");
consider dropping Roofs from the picker until slope is supported (UX rule: hide invalid
options).

### Cell-1-4 — S1 count strip counts Filled Regions with a category filter the repo itself flags as broken
**Medium · Confirmed (inconsistency) / count itself Needs Windows test (T4) · Pass 1**
`SplitByCellCommand.cs:49-56` uses `OfCategoryId(OST_FilledRegion)` for the count
strip, while `BuildCategoryMap` special-cases `OfClass(typeof(FilledRegion))`
(`SplitByCellHelpers.cs:580-586`) precisely because the CLAUDE.md gotcha table says
category filters miss FilledRegions. The strip can show "Filled Regions: 0" for a view
where the run then finds and processes them (or vice versa).
**Fix:** reuse `SplitByCellHelpers.BuildCategoryMap` for the counts so UI and runtime
share one collector.

### Cell-1-5 — "Align grid to project origin" reads the shared E/W–N/S values, not the base point's internal position
**Medium · Needs Windows test (T3) · Pass 1**
`SplitByCellHelpers.cs:543-563` reads `BASEPOINT_EASTWEST_PARAM` /
`BASEPOINT_NORTHSOUTH_PARAM` — the PBP's **shared-coordinate** readout — and uses it as
an internal-coordinates grid origin. CLAUDE.md's own guidance: `BasePoint.Position` is
the internal location. In any project with shared coordinates set (rotated/offset
site), the grid anchors to a meaningless point.
**Fix:** `BasePoint.GetProjectBasePoint(doc).Position` (X/Y only).

### Cell-4-3 — Grouped / checked-out / uneditable elements fail with raw Revit messages, uncounted as a class
**Medium · Confirmed · Pass 4/7**
No pre-check for `el.GroupId` or worksharing checkout before splitting; deleting a
group member throws, so a model where the floors were grouped since the last successful
run fails 100% with per-element `✗ {id}: {Revit's message}`. Functionally correct
(skip-and-log via the catch) but the user gets no "these are in groups — ungroup first"
diagnosis.
**Fix:** pre-check `GroupId != InvalidElementId` (skip-and-log "in group") and, in
workshared docs, `WorksharingUtils.GetCheckoutStatus` (skip-and-log "owned by …").

### Cell-4-4 — Per-cell boolean failures are invisible to the run log
**Medium · Confirmed · Pass 3/4**
`SplitByCellHelpers.cs:113-117` routes each failed cell intersection to
`DiagnosticsLog.Swallowed` only. When *every* cell fails, the run log's only line is
the generic `boolFail` message — the actual exception text lives solely in
`diagnostics.log`, so the log can't explain the failure it reports.
**Fix:** count intersect failures per element and include the count + first exception
message in the `boolFail` run-log line.

### Cell-5-1 — Category map collects all five categories, then filters
**Low · Confirmed · Pass 5**
`SplitByCellEventHandler.cs:53-63` builds `BuildCategoryMap` over every supported
category, then intersects with the selection. Harmless at this scale; collect only the
selected BICs.

### Cell-2-1 — `SelectionChanged` subscribed after `SetGroups`
**Low · Confirmed · Pass 2**
`SplitByCellViewModel.cs:128-133` violates the documented MultiSelectTabs contract
(subscribe **before** `SetGroups` — the setup-time callback is what populates mirror
fields). Benign today because the initial selection is empty; a future default-selection
change would silently desync `_selectedCats`.

### Cell-2-2 — Review note claims active-view scope even in from-selection mode
**Low · Confirmed · Pass 2/8**
`modify.splitByCell.review.note` always says "Sketch-based elements **in the active
view** will be split…" — wrong when running on a pre-selection. See string table.

---

## NumberRange (S2's input control — findings surface through Cell)

### NR-1 — `SetValues` before `Loaded` is a no-op: S2 renders empty fields while the run silently uses 10 × 10
**High · Confirmed · Pass 1/2**
`NumberRange.xaml.cs:36-46` — the text boxes are only created in `Build()` on
`Loaded`, but step content is built (and `SetValues(_cellX, _cellY)` called —
`SplitByCellViewModel.cs:208`) at window construction, before `Loaded`. The values are
dropped: the user sees **blank** Cell X / Cell Y boxes while the VM silently holds
10.00 × 10.00 (and the Review step then claims "10.00 ft × 10.00 ft" for fields the
user saw empty). Direct violation of the "UI state must be unambiguous" rule, and it
poisons any diagnosis of a "wrong cell size" symptom. Affects every NumberRange in the
app.
**Fix:** cache pending values from `SetValues` and apply them at the end of `Build()`.

### NR-2 — `AbsMin`/`AbsMax`/`Step` are declared but never enforced; duplicate `BuildSide` call
**Medium · Confirmed · Pass 1/5**
`NumberRange.xaml.cs:130-135` parses raw text with no clamping — `AbsMin = 0.1` on
Cell's S2 is decorative. A typo like `0.01` ft yields ~10⁶ boolean intersections per
floor: Revit appears to hang (performance/crash class). Also `Build()` line 55 creates
a complete min-side, sets its grid column, and discards it (line 56 builds it again).
**Fix:** clamp on parse (and on focus loss snap the text to the clamped value); delete
the stray line-55 call.

### NR-3 — Culture-sensitive parse; decimal feet only
**Low · Confirmed · Pass 1**
`double.TryParse` uses the current culture — on comma-decimal locales "10.5" fails (or
misparses), and a parse failure silently keeps the previous value while the box shows
the new text. Labels say "(ft)" but feet-inches input (`10'-6"`) is rejected the same
silent way. **Fix:** parse with `CultureInfo.InvariantCulture` plus the current culture
as fallback; on parse failure mark the box (red border) instead of silently ignoring.

---

## Shared engine & siblings (Split by Grid / Level / Reference Plane)

### Shared-1 — Category matching by localized `Category.Name` against English labels
**Medium · Confirmed · Pass 1**
`SplitByGridEventHandler.cs:130-140`, `SplitByLevelEventHandler.cs:131-141`, and the
Reference Plane twin match `e.Category?.Name` against the hardcoded English labels
("Walls", "Ducts", …). Non-English Revit finds zero elements in every category. Split
by Cell was deliberately keyed by `BuiltInCategory` for exactly this reason
(`SplitByCellHelpers.cs:567-571` comment) — the siblings never got the fix.
**Fix:** carry the BIC alongside the label (the `(Cat, Label, Note)` tuples already
exist in `SplitElementsShared`) and collect with `OfCategory`.

### Shared-2 — Partial curve-split failure on segment 0 leaves overlapping duplicates, reported as "a gap"
**Medium · Confirmed · Pass 4**
`SplitElementsShared.cs:520-562` — `segIds[0]` is the **original** element, re-curved
to its first sub-segment. If setting the *original's* curve throws but the copies
succeeded, the original keeps its full A→B length alongside copies covering sub-ranges
— overlapping duplicate geometry — yet the outcome logs as
"{n}/{m} segments (some failed — result may have a gap)". The i>0 failure path
correctly deletes the orphan copy; the i==0 path has no remediation.
**Fix:** if segment 0 fails, delete all copies and fail the element whole (the original
is still intact and un-moved), rather than reporting a partial success.

### Shared-3 — `CopyTimes` discards the root exception
**Medium · Confirmed · Pass 4**
`SplitElementsShared.cs:691-695` — the bare `catch` cleans up copies (each cleanup
failure *is* logged) but the exception that caused the bail-out is never routed
anywhere; the caller then logs only "Copy failed". Route it through
`DiagnosticsLog.Swallowed("SplitElements: CopyElement failed", ex)` and append
`ex.Message` to the stats fail line.

### Shared-4 — Per-element outcome lines arrive only after the whole transaction commits
**Low · Confirmed · Pass 3**
`SplitByGridEventHandler.cs:97-103` (and Level/RefPlane twins) push `stats.Log` after
`tx.Commit()`. During a long run the Output log shows only the 5% ticks, then a flood
of ✓/✗ lines at the end. Cell logs per element live — sibling inconsistency. Cheap fix:
pass `pushLog` into the engine (it already takes `RunProgressReporter`).

### Shared-5 — Fail tally double-counts
**Low · Confirmed · Pass 3**
`SplitCurveElement` records a `stats.Fail` per failed **segment** *and* the
wall/column level path can record several per element — the "Failed" figure in the done
line can exceed the number of elements processed. Count failed *elements* (dedupe by
id) for the tally; keep per-segment lines in the log.

---

## String table (Pass 8)

| Key | Current | Proposed |
|---|---|---|
| `modify.splitByCell.labels.preselCount` | `{0} element(s) across {1} categor(ies)` | `{0} elements across {1} categories` |
| `modify.splitByCell.log.boolFail` | `✗ {0} {1}: boolean intersection returned no cells — the element's geometry may be non-planar or the solid could not be intersected` | `✗ {0} {1}: no cells could be cut — the element has no flat top face (sloped or non-planar geometry isn't supported yet)` *(pairs with Cell-4-2; keep the old wording if 4-2 isn't taken)* |
| `modify.splitByCell.review.note` | `Sketch-based elements in the active view will be split into a regular grid of cells. …` | `Sketch-based elements in scope will be split into a regular grid of cells. Elements that fit inside one cell are skipped. The whole run undoes with a single Ctrl+Z.` |
| `modify.splitByCell.summaries.cellSize` | *(orphan key — never referenced; S2 summary uses `review.cellValue`)* | delete the key |
| `modify.splitByCell.log.noElements` | `No elements found for the selected categories in the active view.` | `No elements found for the selected categories in the active view ({view name}). Make sure the view showing your elements is active when you click Run.` *(pairs with Cell-1-3)* |

Everything else in the four `modify.splitBy*.json` files reads fine (plain US phrasing,
imperial-first). Key completeness was verified: all 158 `AppStrings.T` keys referenced
across `Source/Tools/Modify` + `Source/Commands/Modify` exist in `Strings/en`.

---

## Appendix — Windows test scripts

### T1 — By-Cell triage (the "cuts nothing" complaint)
1. New blank project (imperial template). Draw **one generic 40 ft × 40 ft flat floor**.
   Stay in the plan view that shows it. **Click in empty space so nothing is selected.**
2. Open Split Elements by Cell fresh (close any existing instance of the window first —
   check the taskbar/other monitor). S1: check **Floors** (strip should read
   `Floors: 1`). S2: type **10** and **10**. Run — *without touching Revit views in
   between*.
3. Expected: `Found 1 element(s) to process.` → `✓ Floors <id> → 16 cell(s)` →
   `Done — 16 cell(s) created, 0 skipped, 0 failed.`
4. If step 3 works, repeat **in your real model** on ONE floor: select just that floor,
   open the tool (it will say "FROM CURRENT SELECTION"), run, and read its single
   ✗/—/skip line. Match the line against the table at the top of this report — that
   names the finding. Also open `%AppData%\LemoineTools\diagnostics.log` and search
   `SplitByCell:` for swallowed per-cell exceptions (Cell-4-4).
5. Extra checks while there: does the log say `Operating on N pre-selected element(s)`
   when you expected category mode (Cell-1-1)? Were you on a different view/sheet when
   you clicked Run (Cell-1-3)? Are the floors in a **group** (Cell-4-3)? Are they
   **sloped** (Cell-4-2)?

### T2 — Sloped roof/floor (Cell-4-2)
Blank project: one flat roof, one pitched roof (any slope), one floor with a slope
arrow. Run By Cell on Roofs + Floors, 10×10. Expected today: flat roof splits, pitched
roof and sloped floor fail with the `boolFail` line. Confirms the diagnosis and the
proposed skip-and-log wording.

### T3 — Project-origin alignment under shared coordinates (Cell-1-5)
Project with shared coordinates acquired (rotated site, PBP not at internal origin).
One 40×40 floor. Run with "Align grid to project origin" ON. Measure where the first
interior cut line lands relative to the PBP marker: it should be a multiple of 10 ft
from the PBP. If it's a multiple of the *shared* E/W value instead, the finding is
confirmed.

### T4 — FilledRegion count strip (Cell-1-4)
A drafting view + a plan view, two filled regions in each. Open the tool from each
view and compare the S1 "Filled Regions: N" strip with what the run then reports in
`Found N element(s)` when only Filled Regions is checked.

### T5 — Module-aligned sliver (Cell-4-1)
One floor exactly 40.00 × 40.00 with "Align grid to project origin" ON and the floor's
corner exactly on the PBP; then repeat with the floor nudged 1/64" off the module. If
the nudged case fails whole-element with "Cell element recreation failed." while the
exact case passes, the sliver mechanism is confirmed.

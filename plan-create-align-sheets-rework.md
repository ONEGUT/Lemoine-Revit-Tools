# Plan — Create Sheets rework + Align Sheet Views scope-box handling

Two tools. **Part A (Align Sheet Views — scope box)** is small and self-contained;
**Part B (Create Sheets — full step + UI rework)** is the bulk of the work.

Decisions taken with the user are recorded at the bottom and are already folded into the plan.

---

## Part A — Align Sheet Views: source has a scope box, targets don't

### What happened

`AlignSheetViewsEventHandler.MatchSheet` pairs a source view to a target view in two passes:

1. **Pass 1 — exact scope box.** Requires `t.ScopeBoxId == src.ScopeBoxId`. A target with *no*
   scope box can never satisfy this, so every pair falls through.
2. **Pass 2 — crop overlap** (`OverlapInSourcePlane`, line 2184). This is where the run dies.
   Before it measures any in-plane overlap it applies a **hard depth veto**:

   ```csharp
   // Depth veto: two levels' plan-view cut ranges do not necessarily overlap.
   if (Overlap1D(sNmin, sNmax, nMin, nMax) <= 0) return 0;
   ```

   A view governed by a scope box takes its crop *from the scope box*, and a scope box has a
   finite **vertical** extent. The source's crop Z-range is therefore the scope box's band, while
   an unscoped target's Z-range comes from its own view range. When those two bands don't
   intersect, the candidate scores **0** — not "worse", but "ineligible" — so the source has no
   candidates at all, the sheet reports `noCounterpart` ("[FAIL] … no counterpart on any of N
   reference sheets") and nothing is aligned. The in-plane footprint is never even measured.

   The veto is not wrong in intent (it stops a Level 1 plan claiming a Level 5 plan on a sheet
   holding several plans) — it is wrong as a **veto**. It should rank, not disqualify.

### The alignment maths is already model-space correct — the job is to stop corrupting it

Verified while planning: `TryAlign` → `TargetBoxCentre` (line 2336) does **not** align crop-centre
to crop-centre. It takes a shared **world** anchor (`AnchorWorld(src)`), transforms it into the
**target's own** crop frame (`crop.Transform.Inverse.OfPoint(anchor)`), and derives the box centre
so that world point lands at `SourceAnchorOnSheet(src)` — the exact paper coordinate it occupies
on the reference sheet, each side using its own scale and annotation crop. A grid line therefore
lands in the identical paper position on the target sheet regardless of how big each view's crop
is, which is precisely the required behaviour.

What breaks it is the tool **changing the target's crop** — assigning a scope box
(`AssignScopeBox` → `PredictScopeBoxCrop`) or resizing the crop box (`InheritCropGeometry`) — and
then computing the placement from a *predicted* rectangle. When the prediction is wrong the view
lands wrong. So Part A is mostly subtraction: stop writing to the target's crop, keep the real
captured geometry, and let the existing world-anchor maths do its job.

### Changes

**A1 — Depth becomes a ranking tier, never a veto.** `OverlapInSourcePlane` returns the in-plane
overlap fraction plus a `DepthOverlaps` flag instead of collapsing to 0. In `MatchSheet`'s pass-2
candidate ordering, depth-overlapping candidates rank strictly above non-overlapping ones (sort
key `(depthOverlaps, score)`), so a same-level counterpart still wins whenever one exists — but
when none exists, the best in-plane match is used instead of failing the sheet. The overlap
threshold still gates the match ("so long as they have enough overlap"). A pair matched without
depth overlap is logged once.
*File: `AlignSheetViewsEventHandler.cs` — `OverlapInSourcePlane`, `MatchSheet`.*

**A2 — Measure the model area, and report what was measured.** New `MatchedPair.AreaMatch`
(0..1 — the in-plane overlap fraction already computed in pass 2, computed on demand for pass-1
scope-box pairs). This is the "actually measure the view to see if it's still the same model area"
number, and it is what the new log lines quote.
*File: `AlignSheetViewsEventHandler.cs` — `MatchedPair`, `MatchSheet`.*

**A3 — Scope-box difference is reported, and the scope box is never written on a mismatch.**
New per-pair flag `ScopeBoxMismatch`, set in `MatchSheet` when
`src.ScopeBoxId != InvalidElementId && tgt.ScopeBoxId != src.ScopeBoxId` — covering both "target
has none" and "target has a different one". On a mismatch the scope-box phase **skips the
assignment** and logs one `warn` naming both sides and the measured area:

```
[WARN] A-102 - Level 2 Power — 'Level 2 Power': reference uses scope box 'SB-Core',
       target has none. Scope box not applied; aligned from model space (98% area match).
```

Because nothing is written, `PredictScopeBoxCrop` never runs, the pair keeps its **captured real**
crop, and `TargetBoxCentre` places the viewport from live geometry.
*File: `AlignSheetViewsEventHandler.cs` — `Execute` Phase A, `AssignScopeBox`.*

**A4 — Never resize a mismatched target's crop box.** `InheritCropGeometry` skips the crop
**resize** for any `ScopeBoxMismatch` pair. Annotation-crop inheritance still runs (it is a paper
margin, not a model area, and `FootprintCentre` accounts for it either way).
*File: `AlignSheetViewsEventHandler.cs` — `InheritCropGeometry`.*

**A5 — Decouple crop size from scope box.** `AlignSheetViewsViewModel.CropSizeInherited` currently
forces `InheritCropSize = true` whenever `InheritScopeBox` is ticked, on the reasoning "a scope box
governs the crop, so crop size comes with it". With A3/A4 that reasoning no longer holds, and the
coupling would silently resize crops on exactly the pairs that must be left alone. The coupling,
the `ImpliedRow` UI and the `impliedCropSize` string are removed; **"Match crop size" becomes a
plain independent checkbox** (default off, as today).
*Files: `AlignSheetViewsViewModel.cs`, `Strings/en/testing.alignSheetViews.json`.*

**A6 — "Inherit scope box" is kept, off by default, and relabelled to name its consequence.**
With A3 it only ever fires where source and target already agree — i.e. it is now a genuine
opt-in to *change the target view's crop*, not part of the alignment path. Relabelled to make that
explicit ("Assign the reference view's scope box to target views — changes their crop"), and the
assignment is **verified by read-back** rather than predicted. *If you would rather this option
disappear entirely, say so and it goes — with A3 in place, the alignment never needs it.*
*Files: `AlignSheetViewsEventHandler.cs`, `AlignSheetViewsViewModel.cs`, strings.*

**A7 — Strings.** New `log` keys in `Strings/en/testing.alignSheetViews.json`:
`scopeBoxMismatch`, `matchedDifferentDepth`, `areaMismatch` (a pair that matched but whose
`AreaMatch` is below the overlap threshold — the views no longer cover the same model area).
Removed: `impliedCropSize`.

### Files touched (Part A)

| File | Change |
|---|---|
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs` | depth veto → ranking tier; `AreaMatch` + `ScopeBoxMismatch` on `MatchedPair`; skip-and-warn scope-box phase; crop-resize guard; verified scope-box write |
| `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsViewModel.cs` | crop size decoupled from scope box; scope-box option relabelled |
| `Strings/en/testing.alignSheetViews.json` | 3 log keys added, 1 label removed, 1 relabelled |

---

## Part B — Create Sheets (`PlaceDependentViews`) rework

### B0 — Step map

| # | Today | After |
|---|---|---|
| 1 | Views to Place *(mode **+** picker)* | **Mode** *(picker only)* |
| 2 | Title Block | **Views to Place** *(the picker, moved out of S1)* |
| 3 | Sheet Naming *(number + prefix/suffix + pattern + series ×2 + preview)* | **Title Block** *(+ margins on a rendered sheet, + gap between views)* |
| 4 | Layout *(mode, trim, trim distance, gap, margins)* | **Sheet Numbering** *(start, prefix, digits, preview)* |
| 5 | Review & Run | **Sheet Series** |
| 6 | — | **Sheet Naming** *(pattern + large preview)* |
| 7 | — | **Order** *(replaces Layout)* |
| 8 | — | **Review & Run** |

Eight steps — Bulk Export already ships 8, and the pip row is a `UniformGrid`, so nothing in
`StepFlowWindow` needs to change.

Steps 3, 4, 6 and 7 all read state owned by earlier steps, so the ViewModel gains `IStepAware`:
**`OnStepActivated` rebuilds S3, S4, S6 and S7** through the content-refresh callback. Step content
is built eagerly at window construction — a step that reads the selection would otherwise render
once, empty, and never update (the exact bug Bulk Export's "Build Packs" step shipped with).

### B1 — Step 1: Mode only

Picker list reordered and re-defaulted to **One view per sheet** → **Dependents** → **View +
callouts** (`_mode` default changes from `DependentsPerParent` to `OneViewPerSheet`). The mode note
stays under the selector; the view picker moves to S2.

### B2 — Step 2: Views to place

Unchanged behaviour — `BrowserTreePicker` fed by whichever candidate list the mode selects, just no
longer sharing a step with the mode selector. Its `SelectionChanged` now also **seeds the Order
step's list** (B7): newly-checked views append to the end of the order, unchecked ones drop out, so
an order already arranged survives a small selection edit.

### B3 — Step 3: Title Block + margins + gap

The step becomes a picture rather than a column of numbers:

- Title block `SingleSelect` at the top (unchanged).
- **A proportional render of the selected title block** in the centre: a themed rectangle at the
  sheet's real aspect ratio, from the `FamilySymbol`'s `BuiltInParameter.SHEET_WIDTH` /
  `SHEET_HEIGHT` (both confirmed present in the 2024 enum), captured on the Revit thread at command
  launch with the title-block list. A dashed inner rectangle shows the drawing area the margins
  leave; the paper size is printed under it (e.g. `30" × 42"`). A family exposing neither parameter
  renders a generic landscape sheet with an explicit "size unknown — margins still apply" note —
  never a silently faked size.
- **The four margin steppers sit on the four sides of that render** — top above, bottom below, left
  at the left, right at the right — so which box is which side is unambiguous.
- **Gap between views** moves here from the old Layout step, as its own `InlineStepper` under the
  render.

**Margins are stored per title block, machine-wide; the gap is one global default.** New store
`Source/Framework/Sheets/SheetMarginStore.cs`, modelled on `NamingPatternStore`:
`%AppData%\LemoineTools\SheetMargins.xml`, holding one record per title-block label
(`"{FamilyName} : {TypeName}"` → top/bottom/left/right) plus a single global `Gap` element.
**Public** DTO types (`XmlSerializer` throws on a non-public root — the cause of the theme-reset
bug). Correct storage tier by the CLAUDE.md test: a title-block *name* is not an `ElementId` and
names nothing inside one model, and cross-project reuse is the explicitly requested behaviour.
Selecting a title block loads its saved margins; any change saves immediately (settings auto-save —
no Apply button). An unseen title block starts at the current defaults (0.5" margins, 0.25" gap).

### B4 — Step 4: Sheet Numbering

- **Starting number** (`InlineStepper`, unchanged).
- **Number prefix** (`TextField`, unchanged).
- **Total number of digits** (`InlineStepper`, 1–8, default 3) — replaces the number **suffix**,
  which is deleted from the ViewModel, the handler, the review row and the strings file.
  `1` at 3 digits → `001`; the prefix is applied outside the padding (`A-` + `001` → `A-001`).
- **A live preview** of the first three numbers plus the last: `A-001, A-002, A-003 … A-024`.

`_numberSuffix` / `NumberSuffix` are removed everywhere rather than left dead.

### B5 — Step 5: Sheet Series (rewritten — it does not work today)

Today the step asks for a **free-text parameter name** plus a value, then writes via
`sheet.GetParameters(name)` picking the first shared/writable/String match. CLAUDE.md already
records this as **known-unresolved**: name lookup returns duplicates in an unspecified order and
the write silently doesn't land. The screenshot confirms the real target is a **shared parameter**,
group *Project*, type *Text*.

New design:

- **Captured at command launch, on the Revit thread** (same pattern as `BrowserTreeCapture`): every
  writable **Text** parameter available on sheets, read off a sample `ViewSheet` —
  `p.Definition.Name`, `p.IsShared`, `p.GUID`, `p.StorageType`, `p.IsReadOnly`, all confirmed
  present on `Parameter`. With no sheets in the project yet, fall back to walking
  `doc.ParameterBindings` for definitions bound to `OST_Sheets`. A zero-result capture is reported
  explicitly ("No writable text parameters found on sheets"), never a silent empty list.
- **A `SingleSelect` of those parameters**, each row tagged `Shared` or `Project` — not a free-text
  box. The user picks the real parameter instead of spelling its name.
- **The value field offers the existing distinct values** already on sheets in this project
  (captured in the same pass) via `SearchAutocomplete`, with free text still allowed.
- **The write binds by GUID for shared parameters** — `sheet.get_Parameter(Guid)`, confirmed in
  `RevitAPI.dll` as `Element.get_Parameter(System.Guid) -> Parameter` — falling back to the captured
  `Definition` (`get_Parameter(Definition)`) for non-shared project parameters. Name lookup is not
  used at all.
- **The write is verified**: read the value back after `Set` and report `[FAIL]` per sheet if it did
  not take. A silently-dropped series value is the failure being fixed; it cannot fail quietly a
  second time.
- The step stays optional (`required: false`); a blank value skips the write entirely.

Removes: the `SeriesParamName` free-text field, the `seriesParamMissing` string, and the
`GetParameters(name)` heuristic in `WriteSeries`.

### B6 — Step 6: Sheet Naming (UI rework)

Top to bottom, each group inside its own bordered `SectionCard` with an uppercase title:

1. **`TokenInput`** — the naming scheme, at the very top of the step.
2. **The preview**, directly below it, in **large clear text** (`LemoineFS_LG`, not today's italic
   mono `LemoineFS_SM` line), showing `number  |  resolved name` for the first selected view.

The same card treatment is applied to Steps 4 and 5 so the three "pattern" steps read as one
family. The preview updates on `ValidationChanged` (already wired) **and** on step activation, so a
selection change on S2 is reflected.

### B7 — Step 7: Order (replaces Layout)

A two-column list, one row per sheet-to-be:

```
   ┌──────────────────────────────┬───────────────────────────────────┐
   │ VIEW                         │ SHEET                             │
   ├──────────────────────────────┼───────────────────────────────────┤
   │ Level 1 - Power              │ A-001   Level 1 - Power           │
   │ Level 2 - Power              │ A-002   Level 2 - Power           │
   │ Level 3 - Power              │ A-003   Level 3 - Power           │
   └──────────────────────────────┴───────────────────────────────────┘
              [ ▲ Move up ]  [ ▼ Move down ]
```

- **The number belongs to the row position, not to the view.** Reordering moves the view name and
  its resolved sheet name; the number column stays `A-001, A-002, A-003…` top to bottom.
- **Selection follows Revit's convention**: click selects, `Ctrl`+click toggles one, `Shift`+click
  selects the range from the anchor. Move Up / Move Down shift every selected row by one and keep
  the selection on the moved rows.
- Scrolls internally, bubbling at its limits (in-page nested scroller, not a popup scroller).

**New reusable control** `Source/Framework/Controls/Layout/ReorderList.cs` — rows of
`(left, right)` text, multi-select on the Revit modifier convention, `MoveUp()` / `MoveDown()`,
`OrderChanged`. Nothing in the repo does this today (`ListReorder` is drag-based and single-item;
the request is specifically up/down buttons with Shift/Ctrl), so it belongs in the framework rather
than buried in one ViewModel.

**The numbers shown here must be the numbers the run assigns.** Today `NextFreeNumber` skips
already-used sheet numbers *inside the handler*, so preview and result can disagree. Change: the
**command captures every existing sheet number at launch**, the ViewModel computes the full number
list (skipping collisions) so the Order preview is truthful, and passes an explicit
`List<string> SheetNumbers` parallel to `ParentViewIds`. The handler uses the supplied number and,
if it collides at write time (the document changed since the window opened), reports
`[FAIL] Sheet number 'A-003' is already in use — sheet for 'Level 3 - Power' not created` rather
than silently shifting to a number the user never saw.

### B8 — Layout: grouped-only, no trim

- **Remove the placement-mode selector.** The only mode is **Accurate (grouped)**.
- **Remove the trim toggle and trim distance**, and turn trimming off: `TrimBubbles`, `TrimInches`,
  `TrimAnnotationCrop`, the `optTrim` / `noteTrim` / `secTrimDistance` strings and the `trim` review
  row all go.
- **Remove `LayoutMode.Estimate`** and with it `EstimateRect` + `DefaultAnnotationPadIn` — the
  estimate path existed only as the fast alternative to the mode being standardised on.
- **`LayoutMode` collapses to nothing**: grouped *is* measured-with-a-cache, so the enum, the
  `Layout` handler property, the three `announce*` strings and the `layoutLabel` block are deleted
  and the one remaining path stays.

> **Conflict this creates, and its fix.** The handler currently degrades grouped → measured whenever
> trim is off:
> ```csharp
> if (layout == LayoutMode.Grouped && !TrimBubbles) { …fell back to measured… }
> ```
> because the group key `(cropW, cropH, scale)` only predicts a real outline when the annotation
> crop is uniform — which trimming guaranteed. With trim gone that fallback would fire on **every**
> run and "grouped" would be a label on a full measured run.
> **Fix:** extend `GroupKey` to include the view's own annotation-crop state — `annoActive` and the
> four offsets, bucketed to 1/64" alongside the existing crop/scale terms. Two views then share a
> cached footprint only when crop, scale **and** annotation margins all match, which is exactly when
> their outlines really are identical. The fallback is deleted; grouping becomes correct without
> trim. Crop-inactive views stay uncacheable, as today.

### B9 — Review & Run

Rows become: Mode · Views (count + "in the order set on step 7") · Title Block (+ margins, gap) ·
Numbering (`A-001 … A-024`) · Series (`"Power" → Sheet Series (shared)`) · Naming (pattern + first
resolved name). The `trim` and `layout` rows are removed.

### Files (Part B)

**Added**

| File | Purpose |
|---|---|
| `Source/Framework/Controls/Layout/ReorderList.cs` | two-column reorder list, Ctrl/Shift multi-select, up/down |
| `Source/Framework/Sheets/SheetMarginStore.cs` | per-title-block margins + global gap (`SheetMargins.xml`) |
| `Source/Tools/Sheets/PlaceDependentViews/TitleBlockPreview.cs` | proportional sheet render with the four margin steppers around it |
| `Source/Tools/Sheets/PlaceDependentViews/SheetSeriesParam.cs` | captured sheet-parameter record (name, GUID, isShared, existing values) |

**Changed**

| File | Change |
|---|---|
| `…/PlaceDependentViewsViewModel.cs` | 5 → 8 steps; mode split out and re-defaulted; margins/gap on S3 + persisted; digits replace suffix; series step rewritten; naming step rebuilt; Order step; layout options deleted; `IStepAware` refresh for S3/S4/S6/S7 |
| `…/PlaceDependentViewsEventHandler.cs` | explicit `SheetNumbers`; `NumberSuffix`/`TrimBubbles`/`TrimInches`/`Layout` + `TrimAnnotationCrop` + `EstimateRect` removed; `GroupKey` gains annotation-crop terms; grouped-needs-trim fallback deleted; `WriteSeries` rewritten to GUID + verified write |
| `Source/Commands/Sheets/PlaceDependentViewsCommand.cs` | capture title-block sheet sizes, existing sheet numbers, sheet text parameters + their existing values |
| `Strings/en/testing.placeDependentViews.json` | 8 step titles, new numbering/series/order/title-block keys; trim + layout-mode + suffix keys deleted |
| `LEMOINE_UI.md` | document `ReorderList` under §8.3 |

---

## Cross-cutting

- **`/revit-navisworks-ui` first, mockups before code.** Every step in Part B is a UI build, so the
  skill is invoked before any code, and each reworked step (3, 4, 5, 6, 7) gets a rendered HTML
  mockup screenshotted with headless Chromium against the real `ThemePalette`, delivered for
  approval. Iterate on the image, not on compiled C#.
- **All new user-facing text goes through `AppStrings.T`** — no hardcoded literals. Existing
  literals being rewired are edited with a counted Python `str.replace()` pass, not the Edit tool.
- **No build verification is possible on Linux** (`UseWPF` needs `Microsoft.NET.Sdk.WindowsDesktop`).
  Everything here is compile-checked by reading; the first Windows build is the real test and I
  expect to fix compiler errors on this branch afterwards.
- **Post-change silent-failure scan** runs over the whole diff before the work is reported complete,
  with its numbered findings brought back for a warn / rethrow / log / leave-as-is decision on each.

## Unverified — needs a Windows/Revit run

1. That the depth veto is what killed the align run. The reasoning is sound and the fix is correct
   either way (it also fixes the plain cross-level case), but the exact scope-box crop Z-range
   behaviour cannot be confirmed from Linux.
2. That `SHEET_WIDTH` / `SHEET_HEIGHT` are populated on the title-block families in use. The
   parameters exist in the 2024 enum; whether a given family fills them in is a per-family fact,
   hence the explicit "size unknown" fallback.
3. That the GUID-bound series write lands. It is the mechanism CLAUDE.md recommends, and the
   read-back verification reports the truth either way instead of failing silently.

## Decisions taken

| # | Decision |
|---|---|
| Base branch | Branch from **`main`**, as `claude/create-align-sheets-plan-225u9e`. |
| A-i | On a scope-box mismatch the target's crop box is **left untouched** — no resize. |
| A-ii | A target carrying a **different** scope box is treated the same as one carrying none: measure and align **from model space**, so a grid line lands at the identical paper coordinate, provided the in-plane overlap clears the threshold. Nothing is written to the target's crop or scope box. |
| B-i | **Gap between views is one global default**, not per title block. Only the four margins are stored per title block. |
| B-ii | "Clear boxes and clear titles" = every input group on the numbering / series / naming steps gets a bordered `SectionCard` with an uppercase title — to be confirmed on the mockup. |

**Open, flagged for a yes/no:** with A3 in place the "Inherit scope box" option no longer
participates in alignment at all. The plan keeps it as an explicit, off-by-default opt-in to change
the target view's crop. Say the word and it is removed entirely.

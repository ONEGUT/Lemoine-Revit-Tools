# Plan — Broaden "Align Sheet Views"

## Goal

Extend the existing **Align Sheet Views** tool (no new tool) so it:

1. Accepts **multiple source sheets** and, per target sheet, aligns to the
   **single best-matching source sheet** (strategy **A — best whole sheet**).
2. Matches a source view to a target view by **scope box first, crop overlap
   fallback** (strategy **A — scope box, then overlap**).
3. Optionally **inherits** four properties from the source view onto the
   matched target view:
   - **Grid 2D extents** — trim target grids to the source grid endpoints.
   - **Scope box assignment** — assign the source view's scope box.
   - **Crop region visibility** — match `CropBoxVisible`.
   - **Annotation crop / crop size** — match crop extents + annotation-crop offsets.
   - (View-title alignment already exists and is unchanged.)

## Confirmed decisions

- Best-source: **A** — score each source sheet against the whole target sheet,
  align the target to the one best source sheet.
- Match key: **A** — shared scope-box `ElementId` first (exact, no threshold);
  fall back to the current crop-overlap heuristic only for views with no scope box.
- Inherit: **all four** properties above.
- **Ordering constraint (explicit):** scope-box assignment is applied **before**
  any viewport alignment. Assigning a scope box rewrites the view's crop box,
  which is the geometry the alignment math reads, so scope boxes are assigned,
  the doc is regenerated once, crop boxes are re-read, and only then are
  viewports moved.

## Why scope box is the better match key (and needs no render)

A view's scope box is read via
`view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP).AsElementId()`.
A scope box (`OST_VolumeOfInterest`) is one shared model element at a fixed
world position, so two views referencing the same scope box at the same scale
crop to identical world extents and overlay exactly. Matching by scope-box id
is exact (no overlap threshold, no "ambiguous" failure), and the existing
`SetBoxCenter` math already needs no render — it reads `CropBox.Transform` +
`View.Scale` only, regenerating once at commit.

## Run order (one transaction)

1. **Capture** every source sheet's viewports and every target sheet's viewports
   (`VpEntry` gains `ScopeBoxId`, `CropBoxVisible`).
2. **Per target sheet — pick best source sheet:** run the matcher against each
   source sheet, score = number of confident matches (scale + orientation
   matching weighted higher; scope-box matches over overlap matches as
   tie-break). Choose the highest-scoring source sheet; log the choice and the
   runners-up.
3. **Inherit scope boxes first** (if enabled): for each matched pair where the
   target should inherit, set `VIEWER_VOLUME_OF_INTEREST_CROP` to the source's
   scope box id and `CropBoxActive = true`. Collect changed views.
4. **`doc.Regenerate()` once** — only if any scope box was assigned — then
   **re-read** the affected target crop boxes (their transforms changed).
5. **Align** each matched target viewport with `SetBoxCenter` (no regen).
6. **Grid 2D extents** (if enabled): per matched pair, map source grids to target
   grids by `ElementId`, read source endpoints via
   `GetCurvesInView(DatumExtentType.ViewSpecific|Model, sourceView)`, apply to the
   target grid with `SetCurveInView(DatumExtentType.ViewSpecific, targetView, line)`.
   Endpoints are collinear (same model grid) → satisfies the coincidence rule;
   ViewSpecific works even when the grid is scope-box-locked. Each grid wrapped in
   try/catch, skip-and-log if not visible / not collinear / locked.
7. **Crop visibility / annotation crop / crop size** (if enabled): match
   `CropBoxVisible`; set annotation-crop offsets and crop extents. When a view
   also inherited a scope box, its crop is scope-box-governed — skip the explicit
   crop-size set for those and log it (annotation-crop offsets still applied).
8. **View titles last** (existing `AlignAllTitles`) — needs its own regen.
9. **Commit.**

## File changes

Edited (existing tool, no new tool):

- `Source/Tools/Testing/AlignSheetViews/AlignSheetViewsViewModel.cs`
  - Step 1 "Source Sheet" → **"Source Sheets"** (multi-select picker).
    `_sourceSheetId` → `_sourceSheetIds` (List).
  - Step 3 Options: add four inheritance checkboxes (grid extents, scope box,
    crop visibility, annotation/crop). Keep overlap %, title alignment, preview.
  - Update `ReviewItems` / `ReviewValues` / `SummaryFor` / `IsValid` / `Run`.
  - Targets exclude any sheet that is also a source.
- `Source/Tools/Testing/AlignSheetViews/AlignSheetViewsEventHandler.cs`
  - Inputs: `SourceSheetIds` (List), `InheritGridExtents`, `InheritScopeBox`,
    `InheritCropVisibility`, `InheritCropSize` flags.
  - `VpEntry`: add `ScopeBoxId`, `CropBoxVisible`, `ViewId`.
  - New best-source-sheet scoring; matcher prefers scope-box equality, falls back
    to `OverlapInSourcePlane`.
  - New apply passes (scope box → regen → re-read → align → grids → crop →
    titles), all cooperatively cancellable with ~5% log cadence and partial-work
    preservation per CLAUDE.md run-lifecycle rules.

No `App.cs` change (registration/labels unchanged). No CLAUDE.md change required
unless new constraints surface during build.

## Edge cases & failure routing

- Target view has no scope box and inherit-scope-box is on → assign the matched
  source's scope box (that is the point), then realign off the new crop.
- A source sheet that matches zero target views is reported as a non-winner.
- Scale/orientation/rotation mismatches: keep current warnings (anchor-only overlay).
- Grids: not visible / not collinear / scope-box-locked Model extents → skip-and-log
  (use ViewSpecific to avoid the lock); zero grids found in a view says so.
- Every "Found N / No … found" summary line per CLAUDE.md silent-failure rules.
- Handler clears its per-run payload in `finally`; ViewModel already nulls parked
  callbacks in `OnWindowClosed`.

## Out of scope

- Creating missing views or scope boxes (a missing counterpart is reported).
- Changing `DatumExtentType.Model` (3D) grid extents (would alter every view) —
  trimming is per-view ViewSpecific only.
- Cross-document / linked-model grid matching (assumes source + target in one doc).

## Build / test note

Windows-only build (`UseWPF` + `net48`). The `/revit-navisworks-ui` skill is
invoked before any WPF/XAML edit.

# Plan — Place Dependent Views on Sheets

## Goal

A new, standalone tool ("Place Dependent Views") that, for each selected parent
view that has dependents, **creates one sheet** and places **all of that parent's
dependent views** on it — trimmed, non-overlapping, and centered with even edge
spacing. Separate from the existing Create Sheets tool.

Confirmed design decisions (from clarifying questions):

1. **Sheets** — the tool *creates* one new sheet per selected parent view.
2. **Layout** — *auto best-fit* (compute rows×cols / shelf-pack the real trimmed
   viewport sizes, then center with even spacing).
3. **Margins** — *per-side* (top / bottom / left / right), user-set.
4. **View titles** — *ignored* in the packing math (pack by `GetBoxOutline`, the
   crop box only; the viewport label is not reserved).

---

## How it fits the existing architecture

This mirrors the **Create Sheets** tool exactly (same trio):

- `…ViewModel` implements `ILemoineTool` (+ `ILemoineReviewable`) — drives the
  `StepFlowWindow` accordion. Runs on the WPF STA thread.
- `…EventHandler` implements `IExternalEventHandler` — all Revit API work, runs
  on Revit's main thread after `ExternalEvent.Raise()`.
- `…Command` implements `IExternalCommand` — collects doc data on the Revit
  thread, opens `StepFlowWindow` on a dedicated STA thread (copied verbatim from
  `CreateSheetsCommand`).
- Registered in `App.cs` (static handler + `ExternalEvent`, one ribbon button on
  the **Testing** panel).

Reference files studied: `CreateSheetsViewModel/EventHandler/Command`,
`ReplicateDependentViewsRunHandler` (dependent-view + crop patterns), `ILemoineTool`,
`LemoineTokenInput`, `LemoineMultiSelectTabs`, `LemoineInlineStepper`,
`LemoineSingleSelect`, `App.cs` ribbon/registration.

---

## The two hard problems (researched)

### 1. Trimming the bubbles "to just past the crop"

This is the **annotation crop region**. Each view has a model crop and a larger
annotation crop; grid bubbles, section heads, tags etc. are drawn out to the
annotation crop boundary, which is what makes a placed viewport far bigger than
the model crop. Trimming the annotation crop tight to the model crop pulls the
bubbles in to just past the crop and makes the viewport's footprint predictable.

Verified API (Revit 2024):

```csharp
// 1. ensure model crop is on
view.CropBoxActive = true;
// 2. enable the annotation crop
view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(1);
// 3. set the four offsets (MODEL feet) = paper gap × view scale
var mgr = view.GetCropRegionShapeManager();
double m = trimPaperFeet * view.Scale;          // e.g. (0.125"/12) × 96
mgr.TopAnnotationCropOffset    = m;
mgr.BottomAnnotationCropOffset = m;
mgr.LeftAnnotationCropOffset   = m;
mgr.RightAnnotationCropOffset  = m;
```

`Left/Right/Top/BottomAnnotationCropOffset` are read/write doubles on
`ViewCropRegionShapeManager` (confirmed). Notes / edge cases:

- Offsets are **model space (feet)**; the user-facing trim is **paper inches**, so
  convert with the view's `Scale` (model feet per paper foot). This keeps the
  on-sheet gap constant regardless of view scale.
- Revit enforces a small minimum and rejects 0 / negatives — clamp to a tiny
  positive floor and wrap each `set` in try/catch → `LemoineLog.Swallowed`, then
  place the view untrimmed rather than aborting.
- This **permanently modifies the dependent views** (intended — the user asked to
  trim "before placing"). Will be stated plainly in the Review step.
- Some view types can't carry an annotation crop — skip-and-log, place untrimmed.
- A bubble-trim **toggle** lets the user disable trimming entirely.

### 2. Packing the viewports — non-overlap + even edge spacing

The only reliable way to know a viewport's true on-sheet footprint (after the
trim) is to **place it, regenerate, then read `Viewport.GetBoxOutline()`** —
inferring size from the crop box alone is unreliable. So the run is two-pass
inside one transaction:

1. Trim the parent's dependents (above) and `doc.Regenerate()`.
2. Create every dependent's viewport on the sheet (provisional center),
   `doc.Regenerate()`, read each `GetBoxOutline()` → real `(w, h)` in sheet feet.
3. Feed sizes + drawing area + gap into a pure packer → target centers.
4. `Viewport.SetBoxCenter()` for each; `doc.Regenerate()`.

The packing math lives in a **Revit-free** helper (`SheetLayoutPacker`) so the
hard logic and edge cases are isolated and unit-reasoned without Revit:

- **Drawing area**: title-block instance bounding box on the sheet
  (`tbInstance.get_BoundingBox(sheet)`), minus the four per-side margins.
- **Auto best-fit**:
  - *Uniform case* (matchline dependents are usually identical size): try every
    column count `1..N`, `rows = ceil(N/cols)`; keep arrangements whose block fits
    the area; pick the one whose block aspect ratio best matches the area aspect
    (fullest, most balanced). 
  - *Mixed sizes*: shelf / first-fit-decreasing-height packing — sort by height
    desc, lay into rows, wrap when the next rect would exceed area width.
- **Even spacing**: after packing into the minimal block, **center the block** in
  the drawing area so left-margin == right-margin and top == bottom (on top of the
  user's per-side minimums); optionally distribute internal gaps equally.
- Returns `centerX/centerY` per input rect (sheet feet), plus an `Overflow` flag.

Packer edge cases (all reported through `pushLog` + `LemoineLog`, never silent):

- Parent view with **no dependents** → skip parent, warn.
- Dependent **already placed** on a sheet (`Viewport.CanAddViewToSheet == false`)
  → skip that dependent, warn (can't place a view twice).
- A **single dependent larger than the drawing area** → place centered, warn
  overflow. Dependents inherit the primary's scale and can't be rescaled
  independently, so we cannot shrink to fit — we warn instead of silently clipping.
- **Total won't fit** even packed → place via best-fit, set `Overflow`, warn
  clearly (views may run past the margins). No silent overlap.
- **Sheet number collision** → same handling as Create Sheets (skip + warn).
- **Title block instance / bbox unreadable** → warn, fall back to the title-block
  symbol's sheet size; if still unknown, skip that sheet with a clear message.
- Degenerate/empty `GetBoxOutline` → warn, skip that viewport.

---

## Steps (StepFlowWindow accordion)

| id | Title | Content |
|----|-------|---------|
| S1 | Views to place | `LemoineMultiSelectTabs` (multi-select) of **only** views that have dependents, grouped by view type. Each row labelled `"<view name>  (<n> deps)"`. |
| S2 | Title block | `LemoineSingleSelect` of `OST_TitleBlocks` symbols (`Family : Type`). |
| S3 | Sheet naming | `LemoineTokenInput` chips + starting-number `LemoineInlineStepper` + live preview. Tokens: `{ParentViewName}`, `{ViewType}`, `{Level}`, `{SheetNumber}`. Default `{ParentViewName}`. |
| S4 | Layout | Bubble-trim toggle + trim distance (inches, default 1/8"); per-side margins T/B/L/R (inches, default 0.5"); gap between views (inches, default 0.25"). All `LemoineInlineStepper`. |
| S5 | Review & Run | `ILemoineReviewable` summary — counts, title block, naming, layout, and the "dependents will be permanently trimmed" note. Carries the Run button (always last). |

No conditional/hidden steps; no `IStepAware` needed (no step's content depends on
an earlier selection — the naming preview uses a placeholder parent name, exactly
like Create Sheets).

---

## Files

**New**

| Path | Purpose |
|------|---------|
| `Source/Tools/Testing/PlaceDependentViews/PlaceDependentViewsViewModel.cs` | `ILemoineTool` + `ILemoineReviewable`; builds S1–S5, wires controls, `Run()` sets handler props + `Raise()`. |
| `Source/Tools/Testing/PlaceDependentViews/PlaceDependentViewsEventHandler.cs` | `IExternalEventHandler`: trim → create sheet per parent → place dependents → pack → `SetBoxCenter`, all in one transaction with `ConfigureFailures`. |
| `Source/Tools/Testing/PlaceDependentViews/SheetLayoutPacker.cs` | **Revit-free** packing math (rects + area + gap + margins → centers + overflow flag). |
| `Source/Tools/Testing/PlaceDependentViews/ParentViewEntry.cs` | Small DTO: parent name, id, view type, level name, dependent `ElementId`s (built on the Revit thread in the command). |
| `Source/Commands/Testing/PlaceDependentViewsCommand.cs` | `IExternalCommand`: collect parent-views-with-dependents + title blocks, open `StepFlowWindow` (STA-thread pattern copied from `CreateSheetsCommand`). |

**Edited**

| Path | Change |
|------|--------|
| `Source/App.cs` | Add `PlaceDependentViewsHandler` + `…Event` statics; create them in `OnStartup`; add a ribbon button (`LT_PlaceDepViews`, "Place Dep. Views") to the **Testing** stacked panel. |

---

## Conventions / known pitfalls honored

- Aliases in the ViewModel file (`WpfGrid`, `WpfTextBox`, etc.) since it imports
  both WPF and `Autodesk.Revit.DB`.
- All sizes via `SetResourceReference`, transparent backgrounds by direct
  assignment, reuse `LemoineInlineStepper`/`LemoineTokenInput`/`LemoineMultiSelectTabs`
  — no hand-rolled controls. `MultiSelectTabs`: subscribe `SelectionChanged`
  **before** `SetGroups`.
- Transaction uses a failure preprocessor that reports distinct warnings then
  resolves them (pattern from `ReplicateDependentViewsRunHandler`).
- No silent failures: every skip/clamp/fallback routes through `pushLog` +
  `LemoineLog`. A post-change silent-failure scan will be run before commit.

## Not building (out of scope unless you ask)

- Rescaling dependents to fit (not possible — dependents share the primary scale).
- Placing onto existing sheets (we chose create-per-view).
- Reserving the title-block strip automatically (handled by per-side margins).

---

## Branch / workflow

- Develop on `claude/serene-ptolemy-3s45u6` (per session mandate).
- **Need: which branch should this be based off of (e.g. `main`)?**
- Cannot build on Linux (WPF/net48) — code authored to the conventions above;
  you compile/test on Windows. Commit + push to the feature branch when done.

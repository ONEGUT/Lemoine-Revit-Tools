# Plan — Align Sheet Views: alignment math fix + diagnostics

## Problem

Align Sheet Views places most viewports correctly but produces "weird crops" and wrong
moves **on some views and not others**. The user inputs were reviewed and are accurate and
in full WPF/web parity, so the fault is in the alignment/matching math, not the inputs.

Two defects explain "wrong on some views, not others" **with default settings** (no
Inherit option ticked, which is the only configuration that can misplace a viewport
without the user having asked for a crop change):

### Defect A — the alignment mixes two different "centres"

`TryAlign` (`AlignSheetViewsEventHandler.cs:765-791`) derives its world anchor from
`view.CropBox` — the **model crop** box — but positions the viewport with
`Viewport.GetBoxCenter()` / `SetBoxCenter()`, which act on the **viewport box outline**
(the view's real on-sheet footprint).

The current formula assumes the model-crop centre and the viewport-box centre are the same
point. They coincide only when the view has **no active annotation crop, or a perfectly
symmetric one**. An active, asymmetric annotation crop (extra annotation room on one side —
very common on plans) shifts the viewport footprint relative to the model crop, so the
assumption breaks.

The resulting placement error is:

```
error_sheet_ft = (source annotation asymmetry / source scale)
               - (target annotation asymmetry / target scale)
```

which is **zero for views with no/symmetric annotation crop and non-zero for the rest** —
exactly the reported symptom, and it happens with every Inherit toggle off.

Working the algebra also shows that differing **crop sizes** do *not* break the alignment
(crop size and crop centre cancel), so annotation-crop centring is the standout candidate
rather than crop size.

A second, smaller part of the same defect: the source-side term is currently assumed to be
zero, so the **source view's own scale is never used**. When source and target scales
differ, the source term must be divided by the *source* scale.

### Defect B — viewport rotation is warned about, then ignored

The offset is computed in crop-plane X/Y and applied straight to sheet X/Y
(`AlignSheetViewsEventHandler.cs:781`). That is only valid when
`Viewport.Rotation == ViewportRotation.None`. On a rotated viewport the crop X axis maps to
sheet ∓Y, so the offset is applied in the wrong direction.

The code detects and logs the rotation (`:276-277`) but **still applies the uncompensated
move** (`:319-332`). It should either compensate or skip — currently it warns and then
moves the viewport wrongly anyway.

## Approach

Because this project cannot be built or run on Linux, and the annotation-crop hypothesis
(Defect A) is a *hypothesis about Revit's behaviour*, the plan ships a **diagnostic pass
that proves or disproves it on a real Windows plot** alongside the fix — rather than
changing geometry on the strength of code-reading alone.

## Changes

### 1. `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsEventHandler.cs`

**1a. Capture annotation-crop state per viewport.**
Extend `VpEntry` with `AnnoCropActive` and the four annotation offsets
(`AnnoTop/Bottom/Left/Right`, model feet), read in `CaptureSheet` via
`BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE` +
`View.GetCropRegionShapeManager()`. Wrapped per-view in try/catch →
`DiagnosticsLog.Swallowed` (some view types carry no annotation crop).

**1b. New helper `FootprintCentreInCropCoords(...)` — the actual fix for Defect A.**
Returns the centre of the viewport's on-sheet footprint expressed in crop-local coords:

- annotation crop inactive → the model crop centre (today's behaviour, unchanged)
- annotation crop active   → `cropCentre + ((right - left) / 2, (top - bottom) / 2)`

`TryAlign` then uses this reference point for **both** source and target instead of the raw
crop centre, and applies each view's **own** scale:

```
srcAnchorOnSheet = src.BoxCenter + (srcCropCentre - srcFootprintCentre) / src.Scale
off              = (anchorInTargetCropCoords - tgtFootprintCentre) / tgt.Scale
vp.SetBoxCenter(srcAnchorOnSheet - off)
```

For a view with no annotation crop this reduces **exactly** to today's formula, so
currently-correct views do not move.

Target-side annotation offsets are read **live** (not from the captured entry), so any
`InheritCropSize` change applied earlier in the same run is honoured — matching how
`TryAlign` already reads the live crop box.

**1c. Rotation compensation — the fix for Defect B.**
Map the crop-plane offset through the viewport rotation before applying it to sheet axes
(`Clockwise: (x,y) → (y,-x)`, `Counterclockwise: (x,y) → (-y,x)`), for source and target
independently.

> The sign convention cannot be verified without Revit. The diagnostic pass (below) prints
> both candidate mappings and the resulting box centres for every rotated viewport, so one
> Preview run on a rotated sheet confirms or flips it. Until confirmed, this is the one
> change in this plan that is **provisional** — flagged as such in the code comment.

**1d. Diagnostic pass (auto-enabled in Preview-only mode).**
Per matched pair, emit an indented `[diag]` block to the run log:

| Line | Purpose |
|---|---|
| scale (src / tgt), viewport rotation, orientation dot products | context |
| model crop size, src + tgt (model ft) | context |
| annotation crop active + 4 offsets, src + tgt | Defect A inputs |
| **actual `GetBoxOutline()` size (sheet ft), src + tgt** | **decisive test** |
| predicted outline size *without* annotation (`crop / scale`) | **decisive test** |
| predicted outline size *with* annotation (`(crop + offsets) / scale`) | **decisive test** |
| `BoxCenter` src + tgt (current) | context |
| new `BoxCenter` under **old** formula vs **new** formula, and the delta | names the misplaced views |

Whichever *predicted* outline size matches the *actual* `GetBoxOutline()` size settles
definitively whether the viewport footprint includes the annotation crop — i.e. whether
Defect A is the real cause. The old-vs-new delta lists exactly which views were being
misplaced and by how much.

`GetBoxOutline()` is read-only, so the whole diagnostic block runs inside the existing
`PreviewOnly` path with **no transaction and no moves**.

**1e. Guard `InheritCropSize` against a pre-existing target scope box.**
`InheritCropGeometry` currently skips the crop resize only when the view inherited a scope
box *this run*. If the target **already had its own** scope box, `tv.CropBox = nb` fights
it and yields an unpredictable crop — a real "weird crop" source whenever that toggle is
on. Read the target's `VIEWER_VOLUME_OF_INTEREST_CROP`; if set, skip the resize and log it
(reusing the existing `cropScopeGoverned` log line).

### 2. `Strings/en/testing.alignSheetViews.json`

Add the `log.diag*` keys for the diagnostic block. No existing key changes.
(Edited with a Python `str.replace()` pass per the CLAUDE.md rule, count-checked.)

### 3. `Source/Tools/Sheets/AlignSheetViews/AlignSheetViewsWebTool.cs`

One-line input-accuracy fix: `OnState`'s `overlap` case accepts `if (v > 0)`, which would
admit 1–4 % even though the stepper minimum is 5. Clamp to the stepper's `[5, 100]` so the
web input cannot express a value the WPF input cannot. **This is the only input-layer
discrepancy found in the whole review** — everything else is in full parity.

## Deliberately NOT in this pass

No UI change, so no new WPF control, no `/revit-navisworks-ui` invocation and no mockup
round-trip is required. Diagnostics ride on the existing **Preview only** checkbox, which
is already the "run this first to check before committing" control — the natural home.

> Alternative if you'd rather have diagnostics during a **real** run too: a separate
> "Log alignment diagnostics" checkbox. That *is* a UI change and would go through the
> skill + mockup workflow first. Say the word and I'll do that instead.

Also **not** included — found during the review, listed so nothing is silently dropped:

1. **Matching can pair the wrong views.** The overlap fallback pairs on
   `intersection / min-area ≥ threshold`; two adjacent plan regions can exceed 50 % and
   pair wrongly. The ambiguity guard only *skips*, it never improves the pick. A wrong pair
   is the main amplifier that turns into a "weird crop" when **Inherit scope box** is on,
   because that toggle rewrites the target's crop wholesale.
2. **Cross-level plans are eligible for matching** (same `ViewType`, parallel `ViewDirection`);
   they are rejected only when the crop Z/cut-depth ranges miss each other, and plan view
   ranges are often deep enough that two different levels still overlap.
3. **Annotation-crop margins are copied in model feet without rescaling**, so at a different
   scale the *paper* margin differs. Only a scale warning fires today.

Items 1 and 2 are matching-quality work; item 3 changes `InheritCropSize` behaviour. All
three are separate logical changes and belong on their own branch — say if you want any
folded in here instead.

## Verification

Cannot be compiled or run on Linux (`UseWPF` + net48 is Windows-only), so:

1. Build on Windows.
2. Run **Preview only** against a sheet set that reproduces the bad behaviour.
3. Read the `[diag]` block: confirm which predicted outline size matches the actual one
   (settles Defect A), and read the old-vs-new deltas to confirm they are non-zero exactly
   on the views that were moving wrongly.
4. Confirm the rotation sign convention on any rotated viewport.
5. Then run for real.

A post-change silent-failure scan will be run and reported before committing, per CLAUDE.md.

## Branch

Designated branch `claude/sheet-views-alignment-review-j2qog1` already exists, is checked
out, clean, and is **ahead of `main`** (it carries the latest merged work), so it is
already a current base. **Confirm you want to build on it as-is** rather than restarting it
from `main` — restarting would drop that merged work, so building on it as-is is the
recommendation.

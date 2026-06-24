# Plan — Callout scale cap + dimension layout fixes

Four issues from the latest review. Each is scoped below with the exact code involved.

---

## 1. Run option: cap how far callouts may enlarge (max callout scale)

**Now:** the callout scale is auto-picked in `AutoDimensionEngine.SurveyDenseAreas` and
`SurveyUserCallouts` from `CalloutScales = {64,48,32,24,16,12}` (coarsest→finest), choosing the
coarsest standard scale whose text demand fits and that is ≤ ½ the parent scale; very dense areas fall
through to **1:12** (the largest/most-zoomed). That's why some callouts come out too big.

**Change:** add a configurable **finest allowed callout scale** (smallest denominator the auto-pick may
reach). Callouts can still go *coarser*; they just can't zoom in past the cap.
- Add `int MaxCalloutScale` to `AutoDimensionConfig` (denominator, e.g. 24), persisted, with a schema
  bump + migration (default keeps current behaviour = 12).
- In both survey methods, clamp the candidate list to `CalloutScales.Where(s => s >= cfg.MaxCalloutScale)`
  and floor the fallback (`chosen`) to `cfg.MaxCalloutScale` instead of 12.
- Expose it as a **run option** in the Clash Finder *Dimensioning* step (S4): a `LemoineSingleSelect` or
  `LemoineInlineStepper` ("Finest callout scale 1:N"), wired like the existing `DimTargetType` override —
  set on `AutoDimensionConfig.Instance` before the run, restored in `finally`. Add the matching field +
  handler property + review summary entry.

> Confirming direction with you before coding (see questions): "max scale to increase to" = the most
> zoomed-in the callout may get (smallest denominator). A cap of 1:24 means no 1:16/1:12 callouts.

---

## 2. User-callout takeover "acting weird" — moved clashes to another area, left the original undimensioned

**Symptom:** adopting a user-drawn callout relocated its clashes to a different group/area and the
original callout location ended up undimensioned.

**Suspected area:** `AutoDimensionEngine.SurveyUserCallouts` + `ClashFinderEventHandler.AdoptUserCalloutViews`.
The crop is **grown to the containing room** (`grown`) while membership stays the user's drawn rectangle;
the callout view's crop is then set to `grown`, markers are placed, non-members pruned, parent markers
deleted, and the group dimensions crop-bounded to the grown crop. A room that is large or offset from the
drawn boundary can move the visible/dimensioned region away from where the callout was drawn — a strong
candidate for "moved to another area," and crop-vs-membership mismatch could leave the drawn spot bare.

**Approach:** this is ambiguous from code-reading alone, so per the project's "build a debugger first"
rule I will **not** guess a fix. First step is diagnosis:
- Add targeted run-log lines (drawn rect, grown-to-room rect, membership rect, # members, callout crop,
  where each dimension landed) so a single run pinpoints the divergence, **or**
- A small `Debuggers/` harness that runs `SurveyUserCallouts` on a chosen view and dumps the rectangles.

Then fix the specific divergence (likely: dimension/crop to the **drawn** membership rather than the
room-grown crop, or stop growing the crop past the drawn boundary for user callouts). Decide the fix once
the diagnosis shows the exact mismatch.

---

## 3. Dragged-text offset below the line ≠ above the line (should be symmetric)

**Now:** `TagColumnPlanner.PlanColumn` (plan) and `AutoDimensionCommit.PlaceColumn` (commit, source of
truth) both place a moved tag at `centre = pointOnLine + perp * (sign * level)`, `sign = +1` above /
`-1` below — *symmetric in our math*. So the asymmetry is almost certainly Revit anchoring the
dimension text relative to its **baseline**, not its vertical centre (the same baseline gotcha CLAUDE.md
records for `TextNote`): a below tag whose centre-point we set at `-level` renders closer to the line
than an above tag at `+level`.

**Change:** when `sign < 0` (below), add an extra perpendicular push of ~one text height so the below
tag's near edge mirrors the above tag's gap. Apply identically in `PlaceColumn` and `PlanColumn` so the
scorer and commit stay in lock-step.

> ⚠ Cannot verify on Linux (no Revit). I need to know which way it's off (below sits **too close** to the
> line, or too far?) to set the sign/magnitude correctly the first time — see questions. Will flag this as
> Windows-verify.

---

## 4. Moved text still covers other lines — check text vs sibling lines AND prefer sliding the tag

**Now:**
- The **scorer** (`LayoutScorer.Score`) *does* penalise a moved text box crossing other dimensions'
  witness lines (`WitnessThroughText`, hard) and their line bands — so the layout sees it.
- But the **commit** (`AutoDimensionCommit.PlaceColumn`, the source of truth for final tag positions)
  only bumps a tag **perpendicular** (`level += step`) to clear `placedTags` + static `obstacles`
  **boxes**. It has **no representation of sibling dimensions' witness/dimension *lines***, and never
  slides the tag **along the axis**. So realized tags land on witness/dim lines that aren't boxes.

**Change (matches your stated preference — "moving the dragged text is the most preferable change"):**
- Feed `PlaceColumn` the sibling dimensions' witness + dimension-line **segments** (build them from the
  committed plan, view-2D), and treat a line-through-the-tag-box as a clash alongside the existing box
  checks.
- Extend the clash-avoidance search to also slide the tag **along the axis** (in the column direction),
  preferring an along-axis move that clears lines before falling back to extra perpendicular steps —
  i.e. move the dragged text rather than leave it overlapping.
- Keep the plan-time `TagColumnPlanner` in parity so the scorer still predicts the realized position.

---

## Files touched (summary)
- `Core/...` config: `AutoDimensionConfig.cs` (new `MaxCalloutScale` + migration).
- Engine: `AutoDimensionEngine.cs` (clamp scale pick in both survey methods; #2 diagnostics).
- Layout: `Core/TagColumnPlanner.cs`, `AutoDimensionCommit.cs` (#3 below-offset; #4 line-aware + along-axis slide).
- UI/handler: `ClashFinderViewModel.cs`, `ClashFinderEventHandler.cs` (#1 run option; #2 diagnostics; possible #2 fix).
- Possibly `Source/Tools/Debuggers/` (a #2 survey harness).

## Constraints
- Cannot build/verify on Linux — #3 and #4 need a Windows plot to confirm. Will flag clearly.
- One logical area (callout/dimension engine), separate from the Refine Dimensions tool.

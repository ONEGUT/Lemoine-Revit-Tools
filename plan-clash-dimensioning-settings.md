# Plan — Clash Dimension/Elevation: settings split, units, fixes

Base branch: **off `main`** (has the merged audit + run-aware dimensioning).
Branch name: `clash-dimensioning-settings-split`.

## 1. Fix diagonal dimensions (bug)
`DimensionChainer.EmitSingle` draws the line from the representative clash's
anchor to the majority-voted target's point, which was computed on-axis from a
*different* member — skewing the line when their cross-axis positions differ
(the `4'-4 1/2"` in the report). Fix: project the target onto the axis line
through the representative (`TargetPoint = it.Source2d + axis*(tgtA - srcA)`) so
every single dimension is orthogonal. `EmitChain` already builds on one
baseline — unchanged.

## 2. Tolerances in feet, oversize in inches (both tools)
Wizard steppers stay backed by mm internally (engine still divides by 304.8);
the UI shows/edits feet (tolerances) or inches (oversize) and converts on
read/write. Clean defaults (stored as exact mm equivalents):
- Run gap → **5 ft** (1524 mm), Run cross → **0.5 ft** (152.4 mm),
  Storey margin → **2 ft** (609.6 mm).
- Marker oversize → **inches**, default 0 (= exact element size).
- `AutoDimensionConfig` defaults updated to match + **v4→v5 migration** resets
  `RunGapMm`/`RunCrossToleranceMm` to the new clean-feet defaults (matches the
  existing v2/v3/v4 migration pattern).

## 3. Five-step wizards (ClashFinder + ClashElevationFinder)
- S1 Select Definitions · S2 Select Views (unchanged).
- **S3 Marker settings:** clear-previous, scan-all-docs, marker oversize (in),
  storey margin (ClashFinder, ft); tag position + spot-elevation type
  (ClashElevation).
- **S4 Per-run dimensioning settings (ClashFinder):** place-dimensions toggle,
  chain-aligned, run gap (ft), run cross (ft), dimension target
  (Grid/SlabEdge/ManualDatum), slab pick. (ClashElevation S4 = its tag options
  if any beyond S3; otherwise S4 folds into S3 and S5 follows — confirm during
  build.)
- **S5 Review & Run:** implement `ILemoineReviewable` so the framework renders
  the review + run button + log, like the other tools. Run trigger moves from
  the old combined options step to S5.

## 4. Refocus the wizard after a slab pick
After `SlabPickEventHandler` returns, the `OnPicked` dispatcher callback brings
the step-flow window back to front/focus (it currently only updates the label).
Add a window-activate callback from `StepFlowWindow` to the tool (small hook
alongside the existing `IStepAware` content-refresh wiring); call
`window.Activate()` on the wizard's dispatcher after the pick resolves.

## 5. Dedicated Dimension Settings window (Settings menu)
New window exposing the AutoDimension tune variables currently hidden in the
background, separate from the Clash Definitions (clash/marker) window:
`MaxDistanceFt`, `AxisToleranceDeg`, `AmbiguityThresholdFt`, `SlabAxisWeight`,
`SlabLengthWeight`, `DimensionTypeName`, `DimensionBothAxes`, `IncludeLinks`,
and the `CoreLayout` numbers — plus the run defaults (target, chain, run
gap/cross). Reached from the Settings menu; persists to `AutoDimension.xml`
(`AutoDimensionConfig`). Built with house controls (`LemoineInlineStepper`,
toggles) per the WPF skill.

## Files (expected)
- `Source/Tools/T05-Clash/AutoDimension/DimensionChainer.cs` — bug fix.
- `Source/Tools/T05-Clash/AutoDimension/AutoDimensionConfig.cs` — defaults + v5.
- `Source/Tools/T05-Clash/ClashFinder/ClashFinderViewModel.cs` — steps, units,
  reviewable, slab-pick refocus.
- `Source/Tools/T05-Clash/ClashElevationFinder/ClashElevationFinderViewModel.cs`
  — steps, units, reviewable.
- `Source/Lemoine/StepFlowWindow.xaml.cs` — window-activate hook.
- New `DimensionSettingsWindow.xaml(.cs)` + Settings-menu wiring (`App.cs` /
  GlobalSettings).

## Verification
Windows-only build; not compiled here. Needs a Revit pass on a crowded clash
run (confirm the skewed dimension is now orthogonal) and the new step/window UX.

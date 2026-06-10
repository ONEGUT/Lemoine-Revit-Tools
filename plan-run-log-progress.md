# Plan — Run-time step hiding + log expansion, and progress-reporting audit

Branch: `claude/gracious-galileo-yzij9j` (already assigned).

## Part 1 — Hide all steps but the last during a run; expand the output log

**File:** `Source/Lemoine/StepFlowWindow.xaml.cs`

When a function run starts (`StartRun`), true-hide (`Visibility.Collapsed`) every step
accordion row except the last — the last step hosts the run controls + output log — and
extend the output log (`_logScroll.Height`) to its max height (`_logMaxH = 600`). On
`ResetAll`, restore the rows and return the log to its default height
(`LemoineH_LogArea`). This reuses the same row array used by the bulk-export
conditional-step mechanism (`_stepRows`), so navigation and conditional hiding are
untouched.

- New helper `HideStepsForRun(bool hide)`.
- `StartRun`: call `HideStepsForRun(true)` + `_logScroll.Height = _logMaxH`.
- `ResetAll`: call `HideStepsForRun(false)` + restore `LemoineH_LogArea` resource ref
  before `ActivateStep(0)` (which re-applies conditional hiding via `RefreshStepVisibility`).

## Part 2 — Progress-reporting audit

Most tools already log big steps, a pass/fail/skip summary, and per-item/per-batch
progress. The audit found a set of genuinely large loops that report **no** incremental
progress. Fix: a reusable, Revit-free `RunProgressReporter` (new file
`Source/Lemoine/RunProgressReporter.cs`) that emits a log line at every 5% interval for
collections ≥ 20 items, and apply it (plus an `onProgress` bump where missing) to the
gap loops:

| Tool | File | Gap |
|---|---|---|
| Split by Level/Grid/Ref-Plane/Cell | `Source/Tools/.../Split*/SplitElementsShared.cs` + handlers | engine loop has no progress; bar jumps 0→100 |
| Extend Walls | `ExtendWallsEventHandler.cs` | per-element loop, no `onProgress` until end |
| Discover Rules | `DiscoverEventHandler.cs:212` | inner per-element param scan over a linked category |
| Clash detection | `ClashEngine.cs:~828` | O(n×m) element-pair loop, silent |
| Ceiling Heatmap | `CeilingHeatmapEventHandler.cs` | inner ceiling scan + tag-placement loops |
| Link Views by Discipline | `LinkViewsDisciplineRunHandler.cs:216` | `GetLinkBoundingBox` enumerates all link elements |

Each fix: add a start-of-phase log line if missing, drive `RunProgressReporter.Tick()`
inside the large loop, and ensure a pass/fail/skip summary line is present at the end.
No behavioural change to the Revit work — logging only.

## Build note
Cannot build on Linux (per CLAUDE.md). Edits are logging-only and additive; reviewed by
hand for signature compatibility.

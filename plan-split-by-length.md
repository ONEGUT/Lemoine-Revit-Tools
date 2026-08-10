# Plan — Split Elements by Length

Add a **Split by Length** tool to the existing *Split Elements* ribbon pulldown. It cuts
**native (in-host) ducts and pipes** into regular lengths, reusing the station maths already
built for Copy Linear Elements' Split mode.

---

## Why this is a new tool, not a mode of an existing one

The four existing split tools all cut at a **plane or set of planes** supplied by the model
(levels, grids, reference planes, a cell grid). Split-by-length has no planes at all — the cut
positions are derived from the element's own length. It shares the scope/category picker and the
`SplitStats` reporting of its siblings, but nothing of their geometry input, so it slots in as a
fifth sibling rather than an option inside one of them.

## What already exists (the groundwork)

`CopyLinearEngine` (`Source/Tools/CopyFromLink/CopyLinearEngine.cs`) already contains the exact
maths, written Revit-light and transaction-free:

- `SplitStations(totalLen, segLen, gapFeet, keepRemainder)` → `[(start,end)]` station pairs,
  gap taken off interior cut faces only so the run's two outer ends are preserved.
- `PointAlong(a, b, station)` → the point that many feet along the run.

Copy Linear consumes these **after** copying an element out of a link
(`CopyLinearRunHandler.BuildSplit`). The new tool consumes the same two functions against an
element that is **already in the host**, so the copy step disappears entirely. Neither function
is moved or changed — the new handler just calls them.

## The one genuinely new decision: how the cut is made

Two mechanisms, verified against `libs/RevitAPI.dll` metadata (dnfile — declaring-type-scoped,
per CLAUDE.md "Research Discipline"):

| Mechanism | Signature / source | Keeps run connected | Gap support | Applies to |
|---|---|---|---|---|
| **Native break** | `ElementId MechanicalUtils.BreakCurve(Document, ElementId ductId, XYZ ptBreak)` and `ElementId PlumbingUtils.BreakCurve(Document, ElementId pipeId, XYZ ptBreak)` — both confirmed present | **Yes** — Revit rejoins the two halves | No | Ducts and Pipes only |
| **Copy-and-recurve** | `SplitElementsShared.SplitCurveElement` / `CopyLinearRunHandler.BuildSplit` pattern | No — connectors are disconnected | Yes | Any linear `LocationCurve` element |

There is **no** `BreakCurve` equivalent for Conduit or Cable Tray — the
`Autodesk.Revit.DB.Electrical` namespace has none (also verified).

**Chosen behaviour: gap decides the mechanism.**
- Gap = 0 → native `BreakCurve`. Pieces stay a connected system, keep their type, size,
  insulation and system assignment. This is the normal case for cutting a run into joints.
- Gap > 0 → copy-and-recurve. Physically separated pieces are by definition not connected, so
  the disconnect is inherent to what was asked for, not a side effect.

Both paths are logged per element so the run log always says which one ran.

## Scope — categories

**Ducts** (`OST_DuctCurves`) and **Pipes** (`OST_PipeCurves`) only, as requested. Flex duct/pipe
are excluded (not straight lines — `BreakCurve` does not apply). Fabrication parts are excluded
(they are `FabricationPart`, not `MEPCurve`, and already come in fabrication lengths).

The shared core is written against any linear element so Conduit / Cable Tray can be added later
by extending one list — but they are **not** exposed now.

---

## Files

### New

| Path | Purpose |
|---|---|
| `Source/Tools/Modify/SplitByLengthEngine.cs` | Revit-free planning: wraps `CopyLinearEngine.SplitStations`, applies the remainder mode, rejects runs with nothing to do. Keeps the maths reviewable and out of the transaction. |
| `Source/Tools/Modify/SplitByLengthEventHandler.cs` | `IExternalEventHandler` — collects scope, opens one transaction, dispatches per element to the break or recurve path, streams `SplitStats` to the run log, honours `RunState.CancelRequested`, clears its payload in `finally`. |
| `Source/Tools/Modify/SplitByLengthViewModel.cs` | `IStepFlowTool, IReviewableTool, IRunResult, IToolCleanup` — 3 steps, mirroring `SplitByGridViewModel`. No `IStepAware`: step 2 depends on nothing from step 1, so the eager build is correct and the only live element (the hint line) is re-worded in place. |
| `Source/Tools/Modify/SplitByLengthSettings.cs` | `public` XML settings singleton (segment length, gap, remainder mode) in `%AppData%\LemoineTools\`. Public root — an internal one fails silently (CLAUDE.md). |
| `Source/Commands/Modify/SplitByLengthCommand.cs` | Launch command — STA thread + `Dispatcher.Run()`, same shape as `SplitByGridCommand`. |
| `Strings/en/modify.splitByLength.json` | All user-facing text and run-log lines (JSONC). |

### Modified

| Path | Change |
|---|---|
| `Source/Tools/Modify/SplitElementsShared.cs` | Add `SplitByLength(doc, elements, opts, progress, liveLog)` plus the two per-element strategies. Nothing existing is changed — the four current tools keep their code paths untouched. |
| `Source/App.cs` | `SplitByLengthHandler` / `SplitByLengthEvent` statics + creation in `OnStartup`; a 5th `AddPushButton` on the existing `LT_SplitElements` pulldown. |
| `Strings/en/ribbon.json` | `splitByLength` label + tip; widen the `splitElements` pulldown tip to mention length. |

No existing tool's behaviour changes.

---

## UI — 3 steps in `StepFlowWindow`

Structurally identical to `SplitByGridViewModel`, so the tool reads as a sibling of the other
four splits.

```
Window 500 × 720  (StepFlowWindow — chrome, threading, resources all inherited)
└── Row 2: step accordion
    ├── S1  Select Elements      required
    │     ├── pre-selection card   (when the user had elements selected at launch)
    │     └── otherwise: MultiSelectTabs { "MEP": [Ducts, Pipes] }
    │                    + ToggleSwitches [ Active view elements only ]
    ├── S2  Segment Length       required
    │     ├── "Segment length"           InlineStepper  0.5–1000, step 1, 1 dp   + "ft" caption
    │     ├── "Remainder"                SingleSelect   [ Offcut at end | Even lengths ]
    │     ├── "Gap between pieces"       InlineStepper  0–48, step 0.25, 2 dp    + "in" caption
    │     └── hint card — states which mechanism the current gap value selects
    └── S3  Review & Run         optional — framework renders it (IReviewableTool)
```

- Every variable-height section is inside the accordion's own `ScrollViewer` — no new scroll host.
- No new bespoke window, no new control: `MultiSelectTabs`, `InlineStepper`, `SingleSelect`,
  `ToggleSwitches` are all existing house controls (CLAUDE.md "Reusable Components").
- Nav (Back / Confirm / Run) lives in each step's own row — supplied by `StepFlowWindow`.
- S3 is last and always visible, so no `IConditionalSteps` is needed.

### Remainder modes

| Mode | Behaviour on a 34 ft run at 10 ft |
|---|---|
| **Offcut at end** (default) | 10 + 10 + 10 + 4 — exact lengths, one short tail |
| **Even lengths** | 8.5 + 8.5 + 8.5 + 8.5 — equal pieces, none longer than the segment length |

Copy Linear's `keepRemainder: false` (drop the tail) is deliberately **not** offered: there it
merely skipped creating a piece, but here it would delete real modelled ductwork.

---

## Safety and reporting

- One transaction for the whole run, one `doc.Regenerate()` at the end — never per element.
- `RunState.CancelRequested` tested per element; on cancel it logs "Stopped by user — N of M"
  and falls through to the commit so completed work is preserved.
- `RunProgressReporter` gives the steady ~5% log cadence.
- An element with nothing to do (shorter than one segment) is **skipped and counted**, never
  silently dropped; a zero-result collection says so explicitly.
- Every failure path is `DiagnosticsLog.Error` / `.Swallowed` with a context string — no empty
  catches.
- A `BreakCurve` call that returns `InvalidElementId` is treated as a failure and reported, not
  assumed to have worked.

## Cannot be verified here

The project does not build on Linux (CLAUDE.md "Build Environment"), so this ships compile-checked
by inspection only. `BreakCurve`'s presence and signature are confirmed from assembly metadata;
its runtime behaviour on a real duct run needs a Windows/Revit plot.

---

## Branch

Working on `claude/element-split-by-length-l0ykza`, already checked out and already a superset of
`origin/main`. No new branch needed.

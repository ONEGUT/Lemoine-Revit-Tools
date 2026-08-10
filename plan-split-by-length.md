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

**Any line-based element**: the picker is built by scanning the document for elements that carry
a straight `LocationCurve` (`SplitElementsShared.HasStraightCurve`) and grouping them by
discipline through `CategoryDisciplineHelper.GroupByDiscipline`. That covers MEP curves,
structural framing, walls, and any line-based family instance — and only lists categories that
actually have splittable content, so nothing is ever shown-then-skipped.

A curated list was rejected: which line-based families a project loads is not knowable in
advance, and a fixed map would go stale against every new one.

Consequence worth stating plainly: **only ducts and pipes can stay connected**, because they are
the only categories with a `BreakCurve` API. Every other category is cut into separate elements
even at gap 0. The step-3 hint, the review row and the per-element run log all say which
happened.

Walls reach the copy-and-recurve path too, so it now releases wall end joins
(`DisallowWallJoins`) exactly as `SplitCurveElement` does — without it Revit re-joins the pieces
and warps the geometry back.

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

## UI — 4 steps in `StepFlowWindow`, one of them conditional

```
Window 500 × 720  (StepFlowWindow — chrome, threading, resources all inherited)
└── Row 2: step accordion
    ├── S1  Select Elements      required
    │     ├── pre-selection card   (when the user had elements selected at launch)
    │     └── otherwise: MultiSelectTabs, discipline-grouped line-based categories
    │                    + ToggleSwitches [ Active view elements only ]  ← ON by default
    ├── S2  Select Views         required · CONDITIONAL (hidden unless the toggle is off)
    │     ├── BrowserTreePicker, height 300, pruned to model-bearing views
    │     └── note — elements in any selected view are cut once, not once per view
    ├── S3  Segment Length       required
    │     ├── "Segment length"           InlineStepper  0.5–1000, step 1, 1 dp   + "ft" caption
    │     ├── "Remainder"                SingleSelect   [ Offcut at end | Even lengths ]
    │     ├── "Gap between pieces"       InlineStepper  0–48, step 0.25, 2 dp    + "in" caption
    │     └── hint card — states what the current gap does to each category
    └── S4  Review & Run         optional — framework renders it (IReviewableTool)
```

- The scope defaults to the active view, so the common path is three visible steps; the picker
  appears only when that is switched off. `IConditionalSteps.IsStepVisible` also hides S2 when the
  user launched with a selection — that selection *is* the scope.
- S2 sits second so **S4 stays last**, as the interface requires (the final step carries Run, the
  log and the review summary and must always be visible).
- Turning the toggle raises `ValidationChanged`, which is what makes `StepFlowWindow` re-evaluate
  visibility, so the step appears and disappears live.
- **There is no whole-document scope.** Previously "toggle off" meant the entire document; it now
  means "these views". Selecting the browser tree's root folders is the equivalent.
- No new bespoke window, no new control: `MultiSelectTabs`, `BrowserTreePicker`, `InlineStepper`,
  `SingleSelect`, `ToggleSwitches` are all existing house controls (CLAUDE.md "Reusable
  Components"). `BrowserTreePicker` is specifically mandated for view selection.
- The browser tree and eligible view list are captured in the launch command, which runs on the
  Revit main thread both on launch and on reload (`StepFlowWindow` routes reload through
  `App.ReloadEvent`), so the window's own STA thread never queries the document.

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
- An element with nothing to do is **skipped and counted**, never silently dropped; a zero-result
  collection says so explicitly.
- The two bulk skip reasons — shorter than one segment, and no straight line (curved/flex) — are
  **rolled up into one counted line each** via `SplitStats.SkipQuiet`, rather than one line per
  element. Hundreds of identical notices would bury the warnings and failures worth reading. The
  reason and the total are still stated, so nothing becomes silent. Per-element lines remain for
  everything else.
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

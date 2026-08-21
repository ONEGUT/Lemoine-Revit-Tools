# Plan — Auto-Dimension Placement Quality + a Feedback Loop for "I know it when I see it"

## 1. Diagnosis: where drawing quality is actually decided today

Every decision that shapes a dimensioned view, and whether the layout engine can revisit it:

| Decision | Decided in | Searched / scored? |
|---|---|---|
| Which clashes group together | `ClashClusterer` — single-link at a fixed paper radius | **No** — fixed rule, pre-layout |
| Which clashes form a collinear run | `ClashRunGrouper` — agglomerative best-fit | **No** |
| **Chain vs. separate strings** | `DimensionChainer` — `along = abs(axis·longAxis) >= abs(axis·crossAxis)`, plus dense pockets | **No** |
| Which grid / slab edge to measure to | resolver + `ChooseTarget` majority vote | **No** |
| Collapse across-run members to one representative | `DimensionChainer.Representative` (median) | **No** |
| Inline vs. moved text | `GreedyLayoutEngine.ResolveSegments` — `IsCramped` only | **No** — fixed rule |
| Side (+/−) | layout | Yes |
| Row offset (8 steps of `StringSpacingFt`) | layout | Yes |
| Tag-column direction | layout | Yes |

The layout engine searches **side x offset x column-direction**. Everything that determines *how many
lines get drawn and how much information each one carries* is a fixed upstream heuristic that the
scorer never sees and can never undo. That is the structural reason quality plateaus: the search
space excludes the decisions that matter most.

### The objective function does not contain the goal

`LayoutScorer` measures collisions, crossings, leader slack, spacing evenness, stagger, region
overflow. It has **no term** for:

- number of dimension strings drawn (fewer is better),
- total ink (dimension-line + witness length),
- rows consumed in a corridor,
- **information coverage** — is every clash locatable from what's drawn?
- **redundancy** — a clash whose position is derivable twice is over-dimensioned (ASME: never
  close a chain),
- distance to the chosen datum (measuring to a far grid when a near one exists).

"As few lines as possible carrying as much information" is literally not measured, so it cannot
be optimised. This is the single biggest gap.

### Two sub-problems are being solved by hill-climbing that have exact algorithms

- **Row assignment** is interval-graph colouring. Strings whose along-axis spans overlap need
  distinct rows; disjoint ones share a row for free. Greedy colouring by left endpoint is *optimal*
  on interval graphs. Today it is an 8-step offset ladder per string, plus `RowPlanner.SnapSharedRows`
  as a post-hoc pairwise patch trying to recover the sharing the ladder destroyed.
- **What to draw** is a minimum-cost covering problem on a line (below), not a grouping heuristic.

---

## 2. Proposed algorithm — four stages

### Stage 0 — Reference graph (per axis, per cluster)
Nodes = every clash anchor **and** every candidate datum (grid / slab edge / manual datum),
positioned by axial coordinate. Requirement: every clash node must be **located** — reachable from
a datum node through drawn segments.

### Stage 1 — Selection: minimum-cost information cover  *(this is where "chain or not" is decided)*
A candidate string is a contiguous run of nodes drawn at one row. Cost:

```
cost(string) = StringCost + k * TagCost + Ink * length + CrampPenalty(tags that will not fit)
             + GapPenalty(largest internal gap) + DatumPenalty(distance to the datum used)
```

Choose the set of strings that locates every clash **exactly once** at minimum total cost.
On one axis with sorted nodes this is a shortest-path DP over node intervals — exact and fast,
not another heuristic.

Chaining stops being a rule and becomes an *outcome*: a chain wins when the marginal tag cost
beats a new string's fixed cost; it loses when it forces cramped text, or spans a gap so long the
eye cannot follow it. Datum choice falls out of the same DP (a near grid is a cheaper node).
Redundancy is excluded by construction ("exactly once"), so ASME's don't-close-the-chain rule is
structural rather than a penalty.

### Stage 2 — Row assignment: interval colouring
Colour the selected strings per corridor per side; shortest span innermost (ASME). Two sides give
two colour stacks — pick the side that minimises total rows and keeps the string near its clashes.
`SnapSharedRows` disappears: shared rows are the *default* output of colouring, not a repair.

### Stage 3 — Text / tag placement
Keep the current mechanism — `DimAnatomy` + `TagColumnPlanner` is the strongest part of the code
and models real drafted anatomy. Drive it from a per-string fit pass: inline where it fits,
column/stagger where it does not, leader as last resort.

### Stage 4 — Polish: structure-aware local search
Reuse `LayoutScorer` (extended with the Stage-1 terms) but widen the move set beyond
side/offset/column to **structural** moves: split a chain at a joint, merge two strings, re-target
a string to a different datum, move a string across sides, change row. Accept on improvement,
deterministic as today.

---

## 3. The feedback loop — turning "I know it when I see it" into data

Taste cannot be written down, but it can be **sampled**. The loop must make each sample cheap
and each judgement binary. Five pieces:

### Piece 1 — a picture that looks like a drawing
`devtools/render_layout_snapshot.py` currently draws diagnostic primitives. Extend it to render a
plausible sheet: grid lines and bubbles, arrowheads, real feet-inches value strings at real text
height, cropped to the sheet, black on white, at the true view scale. The eye being consulted is
calibrated on drawings, not on debug SVG — an honest picture is what makes the taste transferable.

### Piece 2 — snapshot the PROBLEM, not just the solution
`LayoutSnapshot` today is written *after* layout, so an offline replay can only redraw the answer
the engine already committed to. Add a **problem snapshot** taken before the chainer: resolved
items (clash anchors, axes, resolved targets + alternates), obstacles, crop, scaled config. That
one addition makes every stage above replayable and tunable with no Revit in the loop.

### Piece 3 — a Python design lab
Port the Revit-free core (`Core/` — ~1,500 lines of pure geometry) to `devtools/layoutlab/`, so
variants run and render in milliseconds on any machine. Repo precedent: the ceiling-tag placement
core was ported to Python and validated on real shapes before shipping. C# remains the production
implementation; a parity check replays each fixture in both and diffs the metrics.
*(This project cannot build on Linux, and no .NET toolchain exists in the cloud session — without
the Python lab, every algorithm idea is blind until a Windows build.)*

### Piece 4 — a corpus
15-25 named fixtures: real problem snapshots harvested from live models, plus synthetic edge cases
— single pipe crossing a bay, parallel rack of 6, dense mechanical room, shaft with 20
penetrations, clash equidistant between two grids, clash at the crop edge, mixed short/long chain,
sparse scatter.

### Piece 5 — verdict capture (the fork — see the question in chat)

| | Mechanism | Best at | Cost |
|---|---|---|---|
| **A** | **A/B forced choice** — same fixture rendered two ways side by side; click Left / Right / Same + a reason chip | Tuning trade-offs between rules that already exist | ~40 comparisons calibrates the weights |
| **B** | **Defect markup** — one render; click a spot, pick a tag from a fixed vocabulary ("crossed lines", "should be chained", "shouldn't be chained", "wrong grid", "too many strings", "text collision", "too far from clash") | *Discovering rules that are missing* | Cheapest per insight |
| **C** | **Golden corpus + diff review** — accepted fixtures are locked; every change re-renders and reports only what changed, with before/after images | Never regressing; keeping the loop sustainable | Cheap once built |

**Why A works mathematically:** `LayoutScorer` is *linear in its weights* — each term is a
count or area multiplied by a weight. Every layout therefore has a feature vector; a verdict
"A better than B" is a linear constraint `w·phi(A) < w·phi(B)`. Fitting `w` to satisfy the most
constraints is a RankSVM / Bradley-Terry fit — it produces actual numbers for `OverlapWeight`,
`LeaderWeight`, a new `StringCountWeight`, and so on. Better still, when the constraints turn out
**unsatisfiable**, that is a proof that a rule is missing from the feature set — the loop tells us
what we forgot instead of us guessing.

**Recommended order:** render the corpus as-is -> a review pass where defects get tagged (B) ->
that vocabulary names the missing objective terms -> build them -> A/B to weight them -> C to lock
it down. B first because there is no baseline of "good" yet; A is worth little until the rule set
is stable.

---

## 4. Phasing

| Phase | Work | Production behaviour |
|---|---|---|
| **0** | Problem snapshot; drawing-grade renderer; Python core port; corpus scaffolding; metrics dashboard (strings, ink, rows, moved tags, coverage, redundancy, hard violations) | **Unchanged** |
| **1** | Corpus review pass with the user -> defect vocabulary -> objective terms defined | Unchanged |
| **2** | Stage 1 selection DP in the Python lab, A/B against current behaviour | Unchanged |
| **3** | Stage 2 interval-colouring rows in the lab | Unchanged |
| **4** | Port the settled algorithm to C# behind a config toggle, keep the parity test, delete the superseded paths only once the toggle proves out on a real model | New algorithm, old one recoverable |

## 5. Files

**Phase 0 additions**
- `Source/Tools/Dimensioning/AutoDimension/Core/ProblemSnapshot.cs` — pre-chainer problem dump
- `devtools/layoutlab/` — Python port of the core (`geom.py`, `anatomy.py`, `scorer.py`,
  `chainer.py`, `layout.py`, `metrics.py`, `run.py`)
- `devtools/layoutlab/fixtures/` — the corpus
- `devtools/render_sheet.py` — drawing-grade renderer (or a rewrite of `render_layout_snapshot.py`)
- `devtools/review.py` — builds the review/verdict HTML page for the chosen capture mechanism

**Phase 0 edits**
- `AutoDimensionEngine.cs` — write the problem snapshot alongside the existing layout snapshot
- `AutoDimensionConfig.cs` — a `DumpProblemSnapshots` flag beside `DumpLayoutSnapshots`

**Later phases** touch `GreedyLayoutEngine`, `RowPlanner`, `DimensionChainer`, `LayoutScorer`,
`LayoutConfig` — none before the lab shows the change is an improvement.

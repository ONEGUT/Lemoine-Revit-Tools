# Plan — Complete the Tools Overview + document-sampled dummy runs

## Why the overview feels incomplete

The **Coordination ribbon panel** (Align Coordinates, Compare Grids — merged in PR #97) was added
*after* the overview was built. It is absent from `ToolsOverviewCatalog` (8 categories vs 9 ribbon
panels), absent from the workflow-stage strip, and has no dummy-run specs. So the guide silently
under-represents the plugin — that's the "something's missing" feeling.

## Changes

### 1. Complete the catalog
- `Source/Lemoine/ToolsOverviewCatalog.cs` — add a `coordination` category (Align Coordinates,
  Compare Grids) at the end of the left rail (ribbon order), with blurbs, feeds/fed-by chips
  ("Compare Grids" ← "Align Coordinates", feeds → Copy from Link / Clash), and mono examples.
- Stage strip: add `coordination` to stage 01 *Set Up* (aligning links is a setup task; stage 04
  *Coordinate* stays model-coordination/clash) and update its tagline.
- `Strings/en/overview.json` — new `cat.coordination.*` keys + updated `stages.s01.tagline`.

### 2. Dummy runs sample the live document
- New `Source/Lemoine/OverviewSamples.cs` — Revit-free `OverviewSampleSnapshot` (plain string
  lists: plan/ceiling/3D/section views, sheets, levels, view templates, grids, named ref planes,
  title blocks, filters, linked documents, host title, category groups actually present) + static
  holder (`Set`/`Clear`).
- New `Source/Lemoine/ToolsOverviewSampleCapture.cs` — Revit-facing capture, run on the main
  thread in `OpenOverviewCommand` before the window opens. Each pool wrapped in
  try/catch → `LemoineLog.Swallowed`; capped (~12/pool) and sorted. Category groups are probed per
  `BuiltInCategory` count (cheap) — never a whole-document element sweep. Trade names come from
  `AutoFiltersSettings.Instance.Trades`, clash definition names from
  `ClashDefinitionsSettings.Instance.Definitions` (settings, no doc needed).
- `Source/Commands/OpenOverviewCommand.cs` — capture on open, clear on window `Closed`
  (memory-lifetime discipline). No document → snapshot null → demos fall back to today's canned
  JSON pools.
- `Source/Lemoine/ToolsOverviewDemos.cs` — sample pools become snapshot-backed properties with the
  existing JSON strings as fallback; the spec dictionary is rebuilt when the snapshot changes
  (today it's baked at static-init and would go stale across documents). Specs record
  `SampledFrom` (doc title) so the run banner can say where the data came from.

### 3. Dummy runs look like the tool they copy
- `Source/Lemoine/OverviewDemoTool.cs`:
  - **Composite steps** (`Kind = "composite"`, `Parts`) so one step can stack several real
    controls (labelled), matching tools whose single step hosts grid pickers + level + toggles.
  - **Single-select preselection** (`PreselectIndex`) mirroring ViewModels that default to the
    first grid/level.
  - **Run-log tokens** — `{count:step}`, `{sel:step}`, `{first:step}`, `{value:step}`,
    `{file:step}` expand from the user's actual demo selections, so the simulated log echoes the
    real names picked from the real document instead of canned counts.
  - Banner line states "inputs sampled from <doc>" vs "no document open — canned data".
- `Strings/en/overviewDemos.json` — run-log lines rewritten with tokens where a clean mapping
  exists (Python `str.replace` with per-tuple count checks, preserving JSONC comments); new
  `alignCoordinates` / `compareGrids` demo specs mirroring the real step flows
  (`Host Reference` / `Links to Align` / `Review & Run`; `Files & Tolerances` / `Review & Run`).
- `Strings/en/overviewDemo.json` — sampled/canned banner keys + externalized title suffix.

## Out of scope (noted, not changed)
- Externalizing the hardcoded Coordination ribbon strings in `App.cs` and the T08 ViewModel step
  titles (PR #97 gap — separate branch).
- Swapping demo view/sheet pickers to `LemoineBrowserTreePicker` (bigger fidelity step, later).

## Verification
- Flatten JSON key sets and diff against every `LemoineStrings.T("overview…")` reference.
- Post-change silent-failure scan; Windows build/Revit run remains on your side.

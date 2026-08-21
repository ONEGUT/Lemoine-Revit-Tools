# Plan — Navisworks Level Models (rewrite of the Floor Splitter)

## What exists today

The old tool is **"Floor Splitter"**, on `origin/navisworks-floor-splitter`
(last touched 2026-07-22, ~80 commits behind `main`). It is a standalone
Navisworks add-in in `LemoineNavisworks/`:

```
LemoineNavisworks/
├── LemoineNavisworks.csproj          — net48, x64, links the shared UI framework
├── Source/
│   ├── NavisLauncherPlugin.cs        — Add-Ins ribbon button (scaffold demo)
│   ├── NavisToolWindow.cs            — one live StepFlowWindow per tool type
│   ├── HelloNavisViewModel.cs        — scaffold demo tool
│   ├── SearchSets/                   — Discover → Search Sets tool (3 files)
│   └── FloorSplit/
│       ├── FloorSplitPlugin.cs       — [Plugin] ribbon button
│       ├── FloorSplitModels.cs       — LevelDef / FloorBand / ItemZ
│       ├── FloorSplitViewModel.cs    — 3-step UI (514 lines)
│       └── NavisFloorSplit.cs        — Navisworks API layer (240 lines)
└── libs-navis/README.md              — vendored Navisworks API DLL fallback
```

**How it works now:** it discovers levels by reading each geometry item's
`Level` property and inferring an elevation from the lowest item carrying that
name. The user checks levels; N checked levels derive N−1 **Z bands**
(`−∞ → E2`, `E2 → E3`, … `E(n-1) → +∞`). Per band it hides every item whose
bounding-box Z falls outside the band, saves a viewpoint, and calls
`doc.TryExportToNwd(path, new NwdExportOptions { ExcludeHiddenItems = true })`
so the hidden geometry is physically dropped from the NWD.

## The three requested changes

### 1. Manual per-level model assignment (replaces Z-band splitting)

Today a level is a *number* — an elevation that cuts space horizontally.
The request makes a level a *bucket* — a named row that owns a set of appended
models, chosen by hand from a dropdown. Everything elevation-driven goes away:

| Old behaviour | Becomes |
|---|---|
| `BuildBands` / `FloorBand` (N levels → N−1 derived bands) | **removed** — each level row carries its own explicit `Bottom`/`Top`, no derivation |
| `DiscoverLevels` elevation inference | **kept** — pre-fills each row's band on "Rescan levels" |
| `GatherItemZ` / `ItemZ` / `HideSetFor` | **kept** — now an *optional per-level trim* layered on top of the model assignment |
| `StraddleRule` | **kept** — decides what happens to an item crossing the band edge |
| `UnitSuffix` / `FmtZ` | **kept** — elevation display on the row |

The primary hide is still model membership; the Z trim is a second, optional pass
*within* the assigned models.

The assignment is **many-to-many**: one model can be assigned to several levels
(a shared core/shell model belongs to every floor), and one level can hold
several models.

### 2. Save locally, not to the cloud

**Finding: there is no cloud code in the plugin to remove.** I grepped the whole
`LemoineNavisworks/` tree on both Navisworks branches for
`cloud|bim360|acc|publish` — zero hits. The tool already writes to a plain local
folder picked with `FolderBrowser`, via `doc.TryExportToNwd(path, …)`, which is a
local file write with no Autodesk cloud path.

So this item is either already satisfied or refers to something I haven't seen.
What the rewrite will do regardless, to make "local only" explicit and enforced:

- Validate the chosen output folder is a rooted local path before the run, and
  reject/warn on a UNC or unwritable path with a run-log line rather than
  failing per file.
- Never call any publish/upload API — `TryExportToNwd` only.
- State the resolved absolute output path in the run log at run start.

**Open question for you** — see "Decisions to confirm" below.

### 3. Hide everything not in the level's models before export

This is the same mechanism the tool already relies on, re-pointed at a new
keep-set:

- Keep-set = every geometry item belonging to the models assigned to that level.
- Hide-set = every other geometry item in the document.
- `NwdExportOptions.ExcludeHiddenItems = true` then physically drops the hidden
  items from the written NWD, so an unassigned model cannot ride along.
- Hiding is applied at the **model root item** where possible (one `SetHidden`
  call per model subtree) instead of per leaf item — far cheaper than the
  current per-item loop on a large federation. ⚠ needs a Windows check that
  `SetHidden` on a root item cascades to descendants; per-item fallback if not.
- Original visibility is snapshotted before the run and restored in a `finally`,
  as today.

## Framework drift — why this is a rewrite, not an edit

The Navisworks branch predates the `Source/Lemoine` → `Source/Framework`
refactor. Every shared type it references has been renamed:

| Old (Navis branch) | Current (`main`) |
|---|---|
| `Source/Lemoine/**` | `Source/Framework/**` |
| `LemoineTools.Lemoine` | `LemoineTools.Framework` |
| `LemoineTools.Lemoine.Controls` | `LemoineTools.Framework.Controls` |
| `ILemoineTool` | `IStepFlowTool` |
| `LemoineLog` | `DiagnosticsLog` |
| `LemoineInlineStepper` / `LemoineSingleSelect` / `LemoineFolderBrowser` / `LemoineTextField` / `LemoineToggleSwitches` | `InlineStepper` / `SingleSelect` / `FolderBrowser` / `TextField` / `ToggleSwitches` |
| `LemoineControlStyles` | `ControlStyles` |
| `LemoineFailureCapture` | `RevitFailureCapture` + `RunLogSink` |
| inline C# string literals | `AppStrings.T(key)` + `Strings/en/*.json` |
| — (absent) | `RunState` cancellation, `RunProgressReporter`, `IToolCleanup`, `IConditionalSteps` |
| — (absent) | navigation lives **inside** each step, not a shared footer |

Merging the old branch forward would conflict across the whole rename. The plan
is instead to **port the folder onto current `main`** and rewrite the tool
against today's contracts.

One thing got easier: on the old branch `Source/Lemoine/` held the framework
*and* the tools (`T01-AutoFilters/`, `T02-Ceilings/`, …), so the csproj needed a
long `Remove` list. On `main` the tools live in `Source/Tools/`, so the link
reduces to `..\Source\Framework\**` minus the 11 Revit-coupled files
(`DocumentKey`, `PickerViewGuard`, `ReloadHandler`, `RevitFailureCapture`,
`ToolsOverviewSampleCapture`, `Naming/ParameterCatalog`, `Naming/TokenContext`,
`Naming/TokenResolver`, `Project/ProjectLibraries`, `Project/ProjectLibraryStore`,
`Project/ProjectLibrarySaveHandler`) plus the `GlobalSettingsWindow.*` set.

## Files

### Added — `LemoineNavisworks/` ported onto `main`

```
LemoineNavisworks/
├── LemoineNavisworks.csproj          — link paths retargeted to Source\Framework\**;
│                                       Strings\en\*.json copied to the deploy folder
├── Source/
│   ├── NavisToolWindow.cs            — unchanged except namespace/type renames
│   ├── NavisLauncherPlugin.cs        — kept (scaffold launcher)
│   ├── SearchSets/                   — ported as-is to current names (3 files)
│   └── LevelModels/                  — the rewritten tool
│       ├── LevelModelsPlugin.cs      — [Plugin] ribbon button
│       ├── LevelModelsData.cs        — LevelDef { Name, AssignedModels }
│       ├── LevelModelsViewModel.cs   — 3-step UI
│       └── NavisLevelModels.cs       — Navisworks API layer
libs-navis/README.md                  — vendored API DLL fallback
Strings/en/navis.levelModels.json     — externalized UI + run-log text
```

`HelloNavisViewModel.cs` (scaffold demo) is dropped — the repo ships no demo
panels.

### Modified

- `LemoineTools.sln` — add the `LemoineNavisworks` project.
- `.gitignore` — add `libs-navis/*.dll`.
- `LemoineTools.csproj` — **no change needed**; the `LemoineNavisworks\**`
  `Remove` exclusion is already present at lines 123–126.
- `CLAUDE.md` — a short "Navisworks add-in" section recording the API
  constraints proven here (net48 not net8, `ExcludeHiddenItems` is 2026-only,
  plugin folder must equal the assembly name, no `ExternalEvent` — main thread).

## Step flow

- **S1 — Levels & models** (required). One row per level:
  `[name textbox] [models dropdown] [remove]`, plus `+ Add level`. The dropdown
  lists every appended model in `doc.Models`. Live summary:
  *"4 levels · 9 of 12 models assigned"*, and a warning line naming any model
  assigned to **no** level (it would silently never be exported) and any level
  with **no** models (it would export an empty NWD).
- **S2 — Output** (required). Output folder, filename pattern
  (`{level}`, `{model}`; `.nwd` appended automatically), and the export toggles
  that survive: embed xrefs, keep object properties, save a viewpoint per level.
- **S3 — Run** (final). Review summary of what will be written, Run button,
  progress + Output log, cancellable between levels via `RunState`.

## Core algorithm

```
1. Enumerate doc.Models -> model names (index + display name + source filename).
2. Snapshot current visibility so the model is restored afterwards.
3. For each level with >= 1 assigned model:
     a. Show every model, then hide every model NOT assigned to this level.
     b. Optionally save a viewpoint named after the level.
     c. TryExportToNwd(folder\resolvedName.nwd, ExcludeHiddenItems = true).
     d. Log one line: level, models included, item count, file written.
     e. Check RunState.CancelRequested -> log "Stopped by user - N of M", break.
4. finally: restore the snapshotted visibility; clear cached ModelItem refs.
```

Because the hide decision is now per-model rather than per-item, no geometry
traversal or bounding-box read is needed at all — the run is a few `SetHidden`
calls plus one export per level, which should be dramatically faster than the
current per-item Z scan on a large federation.

## Decisions — CONFIRMED

**A — the per-level dropdown. → CHOSEN: option 1, a true multi-select dropdown.**
The request says "a drop down for each level to
select the models for it" (models, plural), so it must be multi-select:

1. **A true multi-select dropdown** *(recommended)* — a row-height button
   reading "3 models ▾" that opens a checkbox list of every appended model.
   Closest to what was asked and stays compact on a row; needs one small new
   control, built with `StaysOpen=true` + window-level `PreviewMouseDown`
   dismissal per the popup crash rule in CLAUDE.md.
2. **`TagChipInput` per row** — existing house control, type-to-search with the
   picks shown as removable chips. No new control, but it reads as a tag field
   rather than a dropdown, and a row with 6 models assigned grows tall.
3. **`SingleSelect` per row** — one model per level only. Simplest, uses an
   existing control unchanged, but gives up multi-model levels.

**B — where the level list comes from. → CHOSEN: option 1, discovered names, editable.**

1. **Discovered names, editable** *(recommended)* — still read distinct `Level`
   property values to pre-fill the rows (names only, no elevations), with rename,
   remove and `+ Add level`. Assignment stays 100% manual.
2. **Pure manual** — start empty; the user types every level name. Matches
   "manually only" most literally, but is a lot of typing on a 30-storey job.
3. **Seed from model names** — pre-fill level rows by parsing the appended model
   filenames (e.g. `TOWER-L03-ARCH.nwc` → `L03`). Fast when the file naming is
   disciplined, wrong when it isn't.

**C — tool name. → CHOSEN: rename to "Level Models".** "Floor Splitter" describes Z-splitting, which is exactly what
is being removed. Suggested rename: **"Level Models"** (ribbon button
`Level\nModels`, tooltip "Export one NWD per level containing only the models
assigned to it"). Say if you'd rather keep the old name.

**D — the cloud item. → OPEN, non-blocking.** There is no cloud path in the code (see §2 above). Tell
me if you were seeing something specific — e.g. the output folder defaulting to
an ACC Desktop-synced folder, or Navisworks' own Publish dialog appearing — and
I'll target that instead of treating the item as already satisfied.

## Cutting geometry at levels — what Navisworks can and cannot do

Confirmed with the user after the first mockup. **Navisworks cannot cut geometry.**

1. **No cut/boolean/split API exists.** Navisworks is a review aggregator; scene
   geometry is baked, triangulated and read-only. `Autodesk.Navisworks.Api`
   exposes `ModelItem.BoundingBox()` and visibility, but nothing that modifies a
   solid or authors new geometry into the scene.
2. **Section planes do not survive the export.** Clipping planes give a visual
   cut, but Autodesk documents that `NwdExportOptions.ExcludeHiddenItems` does
   **not** drop section-clipped items — they stay in the tree, whole, in the
   written NWD. This is why the original Floor Splitter plan reads
   "**Hide, don't clip**". A clip yields a picture, never a smaller file.

### What is possible — element-granular Z trim (CHOSEN)

`SetHidden` accepts any `ModelItem`, not just a model root, so items *inside* an
assigned model can be filtered by Z extent:

- A riser modelled as **per-storey segments** (most MEP — pipes break at
  fittings) distributes across levels correctly.
- A column or shaft modelled as **one full-height solid** is a single item: it
  can be kept or dropped, never halved. `StraddleRule` decides which.

So the trim is honest about its granularity: it is element-level, not
geometry-level. It is **optional per level**, so a shared site/context model can
still be included whole.

### Clipped viewpoint per level (CHOSEN)

Each level's NWD also gets a saved viewpoint carrying clipping planes at the
level's band, so the file *opens* looking cut. Stated plainly in the UI and the
run log: the geometry is still in the file and a viewer can switch the clip off —
it is presentation, not separation. ⚠ needs a Windows check that clip state
persists into the exported NWD and is restored by the saved viewpoint.

### For a true geometric cut — do it upstream in Revit

`Split Elements by Levels` (Modify panel, `SplitByLevelEventHandler` →
`SplitElementsShared.SplitByLevel`) already splits walls, columns and MEP curves
at selected level elevations, because Revit *can* modify geometry. Followed by
Bulk Export's NWC output, that produces genuinely cut per-level models. It only
covers Revit-authored content — IFC/DWG/point clouds in the federation cannot be
cut by anything in this toolchain.

## Level row — revised shape

The row gains an expand caret (the house idiom for per-parent detail) so the
optional elevation work never clutters the scannable list:

```
[caret] [name]  [models dropdown]  [remove]
   └ expanded:
     Elevation band   Bottom [ 12.00 ] ft   Top [ 24.00 ] ft
     [ ] Trim assigned models to this band
```

`Rescan levels` pre-fills name *and* band (band = this level's elevation to the
next level's; the topmost row is left open-ended). Bands are explicit per row —
the old "N levels → N−1 floors" derivation is gone, since it made the last row's
meaning ambiguous.

Step 2 gains the straddle rule (shown only when at least one row has trim on) and
a "Save a clipped viewpoint per level" toggle.

## Base branch — CONFIRMED

**CHOSEN: branch from `main`** and port the folder in. Work lands on the
designated branch `claude/navis-level-generator-update-2vog8j`.

Branch from **`main`**, and port `LemoineNavisworks/` in from
`origin/navisworks-floor-splitter`, rewriting it against the current framework.

The alternative — branching from `navisworks-floor-splitter` and merging `main`
in — drags an ~80-commit merge across the `Source/Lemoine` → `Source/Framework`
rename, touching every file in the shared framework for no benefit, since the
tool's logic is being replaced anyway.

## Cannot be verified here

This project cannot build on Linux, and there is no Navisworks API DLL in the
repo (`libs-navis/` holds only a README). The following stay tagged
`⚠ verify` in the source and need a Windows / Navisworks 2026 run:

- `NwdExportOptions` member names and `TryExportToNwd`'s return contract.
- Whether `doc.Models.SetHidden` on a model **root item** cascades to its
  descendants (the per-model hide optimisation depends on it).
- `Model.DisplayName` / source-filename properties used to label the dropdown.
- That `Strings\en\*.json` deploys next to `LemoineNavisworks.dll` so
  `AppStrings` resolves (it reads `Assembly.GetExecutingAssembly().Location`).
- Saved viewpoints only record the hide state when Options ▸ Interface ▸
  Viewpoint Defaults ▸ "Save Hide/Required Attributes" is enabled.

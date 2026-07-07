# Plan — Ribbon Lifecycle Reorder, Link Audit, Copy Datums & Repository Restructure

One branch, six phases, each phase a separate commit (or small commit series) so any phase can
be reverted alone and the project **builds on Windows between phases**. This repo cannot be
compiled on Linux, so phases 5–6 (mass rename/moves) are scripted with count-checked Python
replacements and must be verified by a Windows build before the next phase starts.

---

## Phase 1 — Ribbon reorder (lifecycle order)

**File:** `Source/App.cs` (panel/button creation blocks in `OnStartup`), `Strings/en/ribbon.json`, `LEMOINE_UI.md` ribbon section if it lists panel order.

New panel order (agreed in chat):

| # | Panel | Buttons (in order) |
|---|-------|--------------------|
| 1 | **Setup** (renamed from "Coordination") | Upgrade & Link Models → Link Audit *(new, Phase 3)* → Align Coordinates → Compare Grids → Push Coordinates to Links |
| 2 | **Copy from Link** | Copy Datums *(renamed, Phase 2)* → Copy Linear Elements → Copy Elements from Link |
| 3 | **Modify** | Split Elements (pulldown) → Extend Walls |
| 4 | **Ceilings** | Ceiling Heatmap → Ceiling Grids (pulldown) |
| 5 | **Views** | Scope Boxes (pulldown, moved to front) → Bulk Views by Level → Duplicate Views (pulldown) → Explode View by Trade |
| 6 | **Filters & Legends** | Auto Filters → Legend Creation |
| 7 | **Dimensioning** (renamed from "Clash") | Clash Definitions → Clash Finder → Elevation Finder → Refine Dimensions |
| 8 | **Sheets** | Place Dependent Views → Align Sheet Views → Bulk Rename |
| 9 | **Export** | Bulk Export → Print View |
| 10 | **Settings** | Settings → Overview |
| 11 | **Developer** | (debug harnesses, unchanged) |

Also in this phase:
- Externalize the Coordination panel's currently hardcoded labels/tooltips (`Align Coordinates`,
  `Compare Grids`, `Push Coordinates to Links`, `Upgrade & Link Models`, panel title) into
  `Strings/en/ribbon.json` — done with a Python `str.replace()` script per the CLAUDE.md
  text-externalization rule, with key-existence verification.
- Rename the `ribbon.panels.clash` key/value to a `dimensioning` key ("Dimensioning") and update
  the panel-creation call. Persisted-token check: ribbon keys are display-only, safe to rename.

## Phase 2 — Copy Grids → **Copy Datums** (adds Levels)

Levels behave exactly like grids for this purpose: unique names, setter throws on duplicates,
`CopyElements` with the link transform places them at correct world elevation. Same
skip-and-log + per-element-fallback discipline as the existing grid path.

**Files:**
- `Source/Tools/T06-CopyLinear/CopyGridsModels.cs` — add `Levels` list to the link info +
  a `CopyDatumItem` shared shape (name / elemId / existsInHost).
- `Source/Commands/T06-CopyLinear/CopyGridsCommand.cs` — collect levels per link
  (`OfClass(typeof(Level))` in the link doc, flag host-name collisions) alongside grids.
- `Source/Tools/T06-CopyLinear/CopyGridsViewModel.cs` — single source step, one
  `LemoineMultiSelectTabs` with **two tabs: Grids and Levels**, both default all-selected
  (`SetGroups` auto-sorts tabs alphabetically → Grids, Levels — acceptable). Review panel
  reports both counts.
- `Source/Tools/T06-CopyLinear/CopyGridsRunHandler.cs` — accept a levels id list, copy levels
  in the same transaction after grids, skip-and-log name collisions, per-element fallback,
  cancel check between the two batches.
- Classes renamed `CopyGrids*` → `CopyDatums*` and files renamed to match (folded in here since
  Phase 6 renames wholesale anyway); ribbon id `LT_CopyGrids` → `LT_CopyDatums`.
- `Strings/en/copy.grids.json` → `copy.datums.json` with updated keys + new level log lines
  ("Level 'X' already exists in the host — skipped", zero-found lines for both kinds).

**Persisted-token check:** grep for any saved settings keyed on the old tool title before
renaming; ribbon button ids are not persisted state.

## Phase 3 — **Link Audit** (new read-only tool)

A one-screen health report over every `RevitLinkInstance` in the host. No mutations.

**New files** (created directly in the Phase-5 target folder to avoid double moves):
- `Source/Commands/Setup/LinkAuditCommand.cs` — standard StepFlow launcher (same pattern as
  `CopyGridsCommand`): captures audit data on the Revit main thread, opens the window on its
  own STA thread.
- `Source/Tools/Setup/LinkAuditScanHandler.cs` — `IExternalEventHandler` for the Reload
  re-capture path; clears its payload in `finally`.
- `Source/Tools/Setup/LinkAuditViewModel.cs` — `ILemoineTool`, two steps (Report / Run-less
  summary — see open question below), renders a per-link table.
- `Source/Tools/Setup/LinkAuditModels.cs` — one row per link instance.
- `Strings/en/setup.linkAudit.json`.

**Columns per link:**
| Data | Source |
|------|--------|
| Link name + type status (Loaded / Unloaded / Not Found) | `RevitLinkType.GetLinkedFileStatus()` |
| Positioning (Origin-to-Origin vs Shared Coordinates) | `RevitLinkInstance` shared-position state (`RevitLinkType.IsFromLocalPath` + `GetLinkedFileStatus` + instance `Name` suffix / `IsMonitoringLocalChanges`; exact API probed at build time — fallback: report "Shared" when the instance participates in shared positioning, per the Push Coordinates tool's existing detection) |
| Pinned | `Element.Pinned` |
| Workset | `Element.WorksetId` (guard `doc.IsWorkshared`; read name via `doc.GetWorksetTable()`) |
| Display mode ("By Host View" vs Custom) per active view | reuse the existing `ReportLinkDisplayModes` logic (T02-Ceilings) — promoted to a shared helper |
| Last saved | `BasicFileInfo.Extract` on the link path (guarded try/catch — cloud links may not expose a local path; report "n/a") |
| Grand totals line | "Found N links — X shared, Y origin-to-origin, Z unloaded…" (zero-found rule applies) |

Read-only tools need no cancel loop (single-shot scan). All findings also `pushLog`ged so the
run log doubles as a copyable report.

**Ribbon:** Setup panel, second button (after Upgrade & Link Models).

## Phase 4 — Clean house (unused files)

1. **Scripted reference audit** (Python, in `devtools/`): for every `.cs`/`.xaml` under `Source/`,
   check whether its primary type name is referenced anywhere outside its own file; cross-check
   `.csproj` excludes. Produce `audit-unused-files.md` listing candidates with evidence.
2. **Known candidates to verify:** the retired `LemoineNumberStepper` (CLAUDE.md says retired),
   `Source/Lemoine/LemoineSettingsWindow.xaml.cs` vs `GlobalSettingsWindow` (possible superseded
   window), `OverviewDemoTool`/`OverviewSamples` vs `ToolsOverviewDemos` overlap, anything under
   `Source/Tools/Debuggers/` whose investigation is resolved (`ScopeBoxProbe` stays — its button
   is live), `devtools/__pycache__` (add to `.gitignore`).
3. **Root plan files:** move the ~90 root `plan-*.md` + `audit-logic-consistency.md` into
   `docs/plans/` (17 already live there). This plan file itself moves there at the end.
4. **Present the deletion list to the user before deleting anything** (per the silent-failure/
   destructive-action discipline). Moves happen without confirmation; deletions wait.

## Phase 5 — Folder reorg into ribbon categories

Drop the stale `T0x-` numbering (it already collided — two T06 folders) and mirror the ribbon:

```
Source/Tools/{Setup, CopyFromLink, Modify, Ceilings, Views, FiltersLegends,
              Dimensioning, Sheets, Export, Debuggers}
Source/Commands/{same category folders}
```

Mapping:
- `T08-Coordinates` + `T09-UpgradeLinks` → `Setup`
- `T06-CopyLinear` + `T06-CopyFromLink` → `CopyFromLink`
- `T04-ModifyElements` → `Modify`
- `T02-Ceilings` → `Ceilings`
- `T03-LinkViews` (view tools) + `T10-ScopeBoxes` + `T07-ExplodeViews` → `Views`
- `T01-AutoFilters` + `Testing/LegendCreator` → `FiltersLegends`
- `T05-Clash` → `Dimensioning` (subfolders kept: AutoDimension, ClashDefinitions, …)
- `T03-LinkViews/BulkRename` + `Testing/PlaceDependentViews` + `Testing/AlignSheetViews` → `Sheets`
- `BulkExport` → `Export`
- `Source/Lemoine/T0x-*` per-tool control folders follow their tool's category; the rest of
  `Source/Lemoine` (framework + shared controls) → `Source/Framework` in Phase 6.
- `Source/Tools/Testing` dissolves; `TESTING_POLICY.md` moves to `docs/`.

Namespaces are updated to match the new folders (scripted, e.g. `LemoineTools.Tools.CopyLinear`
→ `LemoineTools.Tools.CopyFromLink`), and every `using` + the `App.cs` fully-qualified
references follow. Root namespace stays `LemoineTools` (assembly name, `.addin` manifest, and
`%AppData%\LemoineTools` are untouched). `git mv` for history preservation.

## Phase 6 — Remove the `Lemoine` prefix from file names (types follow)

**Constraint:** C# convention keeps file name = type name, so types rename with their files.
Bare stripping collides with WPF/Revit names (`Theme`, `Log`, `Settings`, `Run`), so each name
is replaced with a descriptive one, not just de-prefixed. Word-boundary regex (`\bLemoineX\b`),
longest-name-first, count-checked, via Python — never the Edit tool.

Representative renames (full table generated and count-checked at execution time):

| Current | New |
|---------|-----|
| `LemoineLog` | `DiagnosticsLog` |
| `LemoineRunLog` | `RunLogSink` |
| `LemoineRun` | `RunState` |
| `LemoineFailureCapture` | `RevitFailureCapture` |
| `LemoineSettings` | `AppSettings` |
| `LemoineStrings` | `AppStrings` (the `L` alias keeps call sites short) |
| `LemoineTheme` | `ThemePalette` |
| `LemoineMotion` | `MotionEffects` |
| `LemoineIcons` | `GlyphIcons` |
| `ILemoineTool` / `ILemoineReviewable` / `ILemoineToolCleanup` / `ILemoineConditionalSteps` | `IStepFlowTool` / `IReviewableTool` / `IToolCleanup` / `IConditionalSteps` |
| Controls: `LemoineMultiSelectTabs`, `LemoineInlineStepper`, `LemoineBrowserTreePicker`, `LemoineDragGhost`, `LemoineListReorder`, `LemoineSingleSelect`, `LemoineTagChipInput`, … | prefix dropped (`MultiSelectTabs`, `InlineStepper`, …) — none collide with WPF types |
| `Source/Lemoine/` folder + `LemoineTools.Lemoine` namespace | `Source/Framework/` + `LemoineTools.Framework` |

**Deliberately NOT renamed** (persisted/external contracts):
- Assembly/project/root namespace `LemoineTools`, `LemoineTools.addin`, `.csproj`, `.sln`
- `%AppData%\LemoineTools` paths, settings DTO/XML element names, Extensible Storage schema GUIDs
- XAML/theme **resource keys** (`LemoineText`, `LemoineRadius_Card`, `LemoineFS_SM`, …) — they
  appear as quoted strings in hundreds of `SetResourceReference` calls and in saved theme logic;
  renaming them is high-risk/zero-payoff. They are tokens, not file names.
- `LemoinePreview` sibling project (rename would touch `.sln`/csproj excludes; can be a later pass)

**Docs pass:** update `CLAUDE.md` and `LEMOINE_UI.md` references to the renamed types/folders in
the same commit so guidance never points at dead names.

## Phase 7 — Verification & wrap-up

- Strings key-set diff for every touched JSON (regex-scan `.cs` for `T("…")` keys vs flattened JSON).
- CLAUDE.md silent-failure scan over the full diff (new Link Audit + Copy Datums code).
- User builds on Windows (plain build auto-builds all four years) and smoke-tests: ribbon order,
  Copy Datums run, Link Audit report, one tool per renamed category opens.
- Move this plan file to `docs/plans/`.

## Risks

1. **No Linux build** — the rename/reorg phases are verified only by scripted count-checks until
   a Windows build runs. Mitigation: phase-per-commit, word-boundary regex, longest-first
   ordering, and a hold point after Phases 5 and 6 for a Windows build before proceeding.
2. **`LemoineRun` vs `LemoineRunLog` substring overlap** (and similar) — handled by longest-first
   word-boundary replacement with expected-count assertions.
3. **Stale `obj/` folders** after folder moves can poison the build (CLAUDE.md CS0579/CS0102
   pattern) — the plan notes to clean `obj/` on Windows after pulling the reorg.
4. **Link Audit positioning-mode detection** — exact Revit 2024 API for "is this link on Shared
   Coordinates" needs a probe on Windows; the plan ships with the best detection available and
   logs "unknown" rather than guessing.

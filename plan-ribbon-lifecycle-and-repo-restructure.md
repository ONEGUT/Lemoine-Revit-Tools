# Plan — Ribbon Lifecycle Reorder, Link Audit, Copy Datums & Repository Restructure

> **STATUS: APPROVED, NOT YET EXECUTED.** User approved on 2026-07-07 with these decisions:
> base branch **main**; root `plan-*.md` files are **deleted** (not archived); rename scope is
> **files + types**, keeping resource keys / assembly / `LemoinePreview` untouched.

---

## 0. Execution rules — READ FIRST, RE-READ AT EVERY PHASE START

These rules override any instinct to improvise. They exist because this repo has specific
failure modes documented in `CLAUDE.md` — read that file in full before Phase 1.

1. **Branch**: all work happens on `claude/tools-ribbon-lifecycle-order-qb7s4i`, based on
   `main`. Never push to `main`.
2. **One phase = one commit** (a phase may be 2–3 commits if it has distinct sub-steps, but
   never mix phases in a commit). Push after each phase. Commit messages are given per phase
   below — imperative mood, no trailing period.
3. **This repo CANNOT be built on Linux** (`UseWPF` needs the Windows SDK — see CLAUDE.md
   "Build Environment"). There is NO compile check available to you. Your only safety nets are
   the scripted count-checks described below. **HOLD POINTS**: after Phase 3, after Phase 5,
   and after Phase 6, stop and tell the user to run a Windows build (plain `dotnet build`
   auto-builds all 4 Revit years) before you continue. Do not start the next phase until the
   user confirms the build is green.
4. **Bulk text replacement is done with Python scripts, never the Edit tool.** Build a list of
   `(old, new, expected_count)` tuples; assert `content.count(old) == expected_count` (or the
   regex equivalent) BEFORE writing any file. For type renames use word-boundary regex
   `\bOldName\b`, applied longest-name-first. This is the CLAUDE.md rule for Unicode escapes
   and text externalization, generalized to every mass edit in this plan. Write the scripts
   into `devtools/` and keep them in the repo (they double as the change record).
5. **Every user-facing string goes through `LemoineStrings.T(key)`** with the key present in
   the matching `Strings/en/*.json` (JSONC). After ANY phase that touches strings, run the
   key-set verification: regex-scan the `.cs` files for `LemoineStrings\.T\(\s*"([^"]+)"`,
   flatten the JSON, and diff the sets. A missing key does NOT fail the build — this scan is
   the only guard.
6. **After every phase, run the CLAUDE.md silent-failure scan** on the phase's diff (empty
   catches, unawaited tasks, unchecked nulls at Revit boundaries, ignored failure returns) and
   report findings before committing.
7. **Deletions require the user's explicit OK on a presented list** (Phase 4). Moves/renames
   don't.
8. **Do not create a PR** unless the user asks.
9. When a step below conflicts with something you observe in the code, STOP and report the
   discrepancy instead of improvising. The line numbers cited are from the plan's writing time
   and may drift — anchor by the quoted code, not the number.

---

## Phase 1 — Ribbon reorder (lifecycle order)

**Goal:** `Source/App.cs` `OnStartup` builds the panels in coordination-lifecycle order, the
Clash panel becomes **Dimensioning**, and the Coordination panel's hardcoded strings move into
`Strings/en/ribbon.json`.

**Commit message:** `Reorder ribbon panels to coordination lifecycle order`

### 1.1 New panel order

The panel/button *creation blocks* in `OnStartup` (currently starting at the
`// ── Filters & Legends ──` comment, after the `Btn(...)` local helper) are reordered to:

| # | Panel title (string key) | Buttons in order (existing ids unless noted) |
|---|---|---|
| 1 | **Setup** (`ribbon.panels.setup`, NEW — panel was "Coordination", hardcoded) | `LT_UpgradeLinks` → *(Phase 3 inserts `LT_LinkAudit` here)* → `LT_AlignCoordinates` → `LT_CompareGrids` → `LT_PushCoordinates` |
| 2 | **Copy from Link** (`ribbon.panels.copyFromLink`) | `LT_CopyGrids` *(Phase 2 renames to `LT_CopyDatums`)* → `LT_CopyLinear` → `LT_CopyFromLink` |
| 3 | **Modify** (`ribbon.panels.modify`) | `LT_SplitElements` pulldown (Level/Grid/RefPlane/Cell order unchanged) → `LT_ExtendWalls` |
| 4 | **Ceilings** (`ribbon.panels.ceilings`) | `LT_CeilingHeatmap` → `LT_CeilingGrids` pulldown (Make/Project/Reproject unchanged) |
| 5 | **Views** (`ribbon.panels.views`) | `LT_ScopeBoxes` pulldown **moved to front** → `LT_LinkViewsLevel` → `LT_DuplicateViews` pulldown → `LT_ExplodeViewByTrade` |
| 6 | **Filters & Legends** (`ribbon.panels.filtersLegends`) | `LT_AutoFilters` → `LT_LegendSettings` |
| 7 | **Dimensioning** (`ribbon.panels.dimensioning`, renamed from `ribbon.panels.clash`) | `LT_ClashDefinitions` → `LT_ClashFinder` → `LT_ClashElevationFinder` → `LT_RefineDimensions` |
| 8 | **Sheets** (`ribbon.panels.sheets`) | `LT_PlaceDepViews` → `LT_AlignSheetViews` → `LT_BulkRename` |
| 9 | **Export** (`ribbon.panels.export`) | `LT_BulkExport` → `LT_PrintView` |
| 10 | **Settings** (`ribbon.panels.settings`) | `LT_OpenSettings` → `LT_Overview` |
| 11 | **Developer** (hardcoded "Developer" — leave hardcoded, it's dev-only) | `LT_ScopeBoxProbe` |

Mechanics: move the whole creation block for each panel (panel `CreateRibbonPanel` call +
all its `AddItem`/pulldown code) as a unit. Do not touch the ExternalEvent wiring above the
ribbon code. All button ids, glyphs, and command class strings stay identical in this phase.

### 1.2 Externalize the Coordination/Setup panel strings

`App.cs` currently hardcodes (find the `// ── Coordination ──` block):
panel title `"Coordination"`; buttons `"Align\nCoordinates"`, `"Compare\nGrids"`,
`"Push Coordinates\nto Links"`, `"Upgrade &\nLink Models"` and their 4 long tooltips.

- Add to `Strings/en/ribbon.json`: `panels.setup: "Setup"` and button objects
  `alignCoordinates`, `compareGrids`, `pushCoordinates`, `upgradeLinks` — each `{label, tip}`,
  labels keeping their `\n` line breaks, tips copied verbatim from `App.cs`.
- Rewire the four `Btn(...)` calls and the panel creation to `L.T(...)` keys.
- Rename key `panels.clash` → `panels.dimensioning` with value `"Dimensioning"` and update the
  `CreateRibbonPanel` call. Grep the whole repo for `ribbon.panels.clash` to catch any other
  reader (expect exactly 1 use in App.cs + the JSON definition).
- **Do this rewiring with a Python script** (rule 4) — the tooltips are long interpolation-free
  strings, still count-check each.

### 1.3 Same-phase consistency checks

- Grep for `panels.` usage: `Source/Lemoine/ToolsOverviewCatalog.cs` and
  `Source/Lemoine/GlobalSettingsWindow.ToolGroups.cs` group tools for display. If either
  hardcodes a "Clash"/"Coordination" group label or a panel ordering, update it to match the
  new names/order.
- Strings key-set verification (rule 5) on `ribbon.json`.
- Update the `LEMOINE_UI.md` ribbon/panel section if it lists the old order.

---

## Phase 2 — Copy Grids → **Copy Datums** (adds Levels)

**Goal:** the Copy Grids tool also copies Levels, and is renamed Copy Datums (display, files,
classes, strings). Levels behave exactly like grids here: **Revit enforces unique level names
and the setter throws on duplicates**, so the same skip-and-log discipline applies, and
`ElementTransformUtils.CopyElements` with the link transform places them at the correct world
elevation.

**Commit message:** `Add level copying to Copy Grids and rename tool to Copy Datums`

### 2.1 Files (rename with `git mv`, classes renamed to match)

| Old | New |
|---|---|
| `Source/Tools/T06-CopyLinear/CopyGridsModels.cs` | `CopyDatumsModels.cs` |
| `Source/Tools/T06-CopyLinear/CopyGridsViewModel.cs` | `CopyDatumsViewModel.cs` |
| `Source/Tools/T06-CopyLinear/CopyGridsRunHandler.cs` | `CopyDatumsRunHandler.cs` |
| `Source/Commands/T06-CopyLinear/CopyGridsCommand.cs` | `CopyDatumsCommand.cs` |
| `Strings/en/copy.grids.json` | `copy.datums.json` |

Type renames (word-boundary, count-checked): `CopyGridsRunHandler → CopyDatumsRunHandler`,
`CopyGridsViewModel → CopyDatumsViewModel`, `CopyGridsCommand → CopyDatumsCommand`,
`CopyGridLinkInfo → CopyDatumLinkInfo`, `CopyGridItem → CopyDatumItem`. Update `App.cs`
statics (`CopyGridsRunHandler/Event` properties → `CopyDatumsRunHandler/Event`), the
ribbon button id `LT_CopyGrids → LT_CopyDatums`, and the command string
`"LemoineTools.Commands.CopyGridsCommand" → "...CopyDatumsCommand"`.
String keys `copy.grids.* → copy.datums.*` in both JSON and `.cs` (scripted, count-checked).

**Persisted-state check before renaming:** grep `CopyGrids` across the repo for any settings
file path, XmlRoot, or Extensible Storage usage (none expected — the tool currently persists
nothing; `CopyLinearSettings`/`CopyLinearStampSchema` belong to Copy Linear, not this tool).
If anything persisted turns up, keep the persisted token unchanged and report it.

### 2.2 Model changes (`CopyDatumsModels.cs`)

```csharp
public sealed class CopyDatumLinkInfo
{
    public string Name       { get; set; } = "";
    public long   LinkInstId { get; set; }
    public List<CopyDatumItem> Grids  { get; set; } = new List<CopyDatumItem>();
    public List<CopyDatumItem> Levels { get; set; } = new List<CopyDatumItem>();  // NEW
}
// CopyDatumItem = the old CopyGridItem shape unchanged (Name, ElemId, ExistsInHost)
```

### 2.3 Command collection (`CopyDatumsCommand.cs`)

`CollectGridLinks` (rename `CollectDatumLinks`): alongside the existing grid collection, read
host level names into their own `HashSet<string>(StringComparer.OrdinalIgnoreCase)` via
`new FilteredElementCollector(doc).OfClass(typeof(Level))`, then per link collect
`OfClass(typeof(Level))` from `GetLinkDocument()` ordered by name, flagging `ExistsInHost`.
Keep the per-link `try/catch → LemoineLog.Swallowed` pattern for the level read, same as
grids. A link is included when it has grids **or** levels.

### 2.4 ViewModel (`CopyDatumsViewModel.cs`)

- Keep the two steps (`source`, `run`). One `LemoineMultiSelectTabs` gets **two group tabs:
  `"Grids"` and `"Levels"`** (SetGroups auto-sorts alphabetically → Grids, Levels; fine).
  Both default all-selected. Display names must be disambiguated across tabs — a grid and a
  level can share a name — so key the display→id maps per kind (prefix the dictionary key
  internally, e.g. `"G|"+name` / `"L|"+name`, or keep two dictionaries and resolve by which
  tab the item is in; `SelectionChanged` returns display strings, so if a grid and level share
  an exact name, suffix the level display with `" (level)"` and note it in the tooltip — the
  copied element keeps its real name).
- Subscribe `SelectionChanged` **before** `SetGroups` (LemoineMultiSelectTabs contract).
- Track `_selectedGridIds` and `_selectedLevelIds`; `IsValid("source")` = at least one of
  either selected.
- Review panel: rows for link, grids count, levels count.
- `Run(...)` passes both id lists to the handler.
- Hidden-because-existing counts reported per kind ("N grids / M levels already exist…").

### 2.5 Run handler (`CopyDatumsRunHandler.cs`)

- Add `List<long> LevelElemIds`; clear it in `finally` alongside `GridElemIds` (static-handler
  memory rule).
- Inside the SAME single transaction (rename it `"Copy Datums from Link"`): copy grids exactly
  as today, then check `LemoineRun.CancelRequested` between the grid and level batches
  (log "Stopped by user — …" and fall through to commit if set), then copy levels with the
  same pattern: pre-check host level names, skip-and-log collisions
  (`copy.datums.log.levelExists`), batch `CopyElements`, per-element fallback on a batch
  throw. Zero-to-copy for BOTH kinds → the existing "nothing to copy" warn path (message
  updated to mention datums).
- Completion log reports both kinds: "Copied A grids, B levels — skipped S, failed F".
- New string keys (in `copy.datums.json`): `log.levelExists`, `log.levelFail`,
  `log.noDatumsToCopy` (replaces `noGridsToCopy`), updated `log.done`, plus
  `labels.levelsToCopy`, `review.itemLevels`, `review.levelsValue` — mirror the existing
  grid-key naming (`copy.datums.` prefix).

### 2.6 Ribbon (in the already-reordered App.cs)

`ribbon.json`: rename the `copyGrids` button object to `copyDatums`, label
`"Copy\nDatums"`, tip: "Copy grids and levels from a linked model into this project.
Datums whose name already exists in the host are skipped — Revit requires unique grid and
level names."

---

## Phase 3 — **Link Audit** (new read-only tool)

**Goal:** a one-screen, read-only health report over every `RevitLinkInstance` in the host —
the "day one" button of the Setup panel. No transactions, no mutations.

**Commit message:** `Add Link Audit read-only report tool`

### 3.1 Files (created directly in Phase-5 target folders — see note)

> **Note:** if Phase 5 has not run yet (it hasn't — it comes later), create these under the
> CURRENT folder convention (`Source/Tools/T08-Coordinates/`, `Source/Commands/T08-Coordinates/`)
> so the tool rides Phase 5's move like everything else. Do NOT invent a new folder just for it.

- `Source/Commands/T08-Coordinates/LinkAuditCommand.cs` — launcher, exact same shape as
  `CopyDatumsCommand`: singleton `_window` re-activate guard, data captured on the Revit main
  thread inside `BuildTool()`, `StepFlowWindow` opened on its own STA background thread with
  `Dispatcher.Run()`.
- `Source/Tools/T08-Coordinates/LinkAuditModels.cs` — `LinkAuditRow` (one per link instance).
- `Source/Tools/T08-Coordinates/LinkAuditCapture.cs` — static `Capture(Document doc)` →
  `List<LinkAuditRow>`; ALL Revit reads live here so both the command and the Reload path
  share it. Runs on the main thread only.
- `Source/Tools/T08-Coordinates/LinkAuditViewModel.cs` — `ILemoineTool` +
  `ILemoineReviewable` + `ILemoineToolCleanup`.
- `Strings/en/setup.linkAudit.json`.

No ExternalEvent run handler is needed (nothing to execute); the standard Reload flow uses
`App.ReloadEvent` (tool-agnostic) — follow whatever pattern the other read-only tool
(`CompareGridsViewModel`) uses for reload/rerun; if Compare Grids uses a run handler to
re-scan on the Run click, mirror that pattern instead (one `LinkAuditScanHandler` +
`ExternalEvent` registered in `App.cs` statics like every other handler). **Read
`CompareGridsRunHandler.cs`/`CompareGridsViewModel.cs` first and copy its architecture** — it
is the existing read-only audit precedent in this repo.

### 3.2 `LinkAuditRow` fields and how each is read (Revit 2024 API)

| Field | Read via | Guards |
|---|---|---|
| Link name | `RevitLinkInstance.Name` (instance) + link doc `Title` | — |
| Load status | `RevitLinkType.GetLinkedFileStatus()` (get type via `doc.GetElement(instance.GetTypeId())`) | try/catch → status "Unknown" |
| Positioning | Instance name suffix Revit maintains (`<Not Shared>` vs shared-position name) AND `RevitLinkType` attachment/positioning members. **The exact reliable API is unverified on Windows** — implement best-effort: report `Shared` / `Origin-to-Origin` / `Unknown`, never guess silently. Cross-check how `PushCoordinatesToLinksRunHandler.cs` detects shared positioning and reuse that code path. | unverified-API risk: isolate in one method with try/catch |
| Pinned | `Element.Pinned` on the instance | — |
| Workset | `Element.WorksetId` → name via `doc.GetWorksetTable().GetWorkset(id).Name`; remember `WorksetId.IntegerValue` (int) not `.Value` | only when `doc.IsWorkshared`, else "—" |
| Display mode in active view | reuse/promote the `ReportLinkDisplayModes` logic from T02-Ceilings into a shared helper (`Source/Helpers/`), reporting "By Host View" vs "Custom/By Linked View" per link for the ACTIVE view | view types without link display → "n/a" |
| Last saved / file path | `RevitLinkType.GetExternalFileReference()?.GetAbsolutePath()` → `ModelPathUtils.ConvertModelPathToUserVisiblePath` → `BasicFileInfo.Extract(path)` for save info | cloud/BIM360 links have no local path: try/catch → "n/a (cloud)" |
| Nested? | `RevitLinkType.AttachmentType` (Attachment vs Overlay) | try/catch → "Unknown" |

### 3.3 UI

Two steps: `report` (required=false, the table) and `run` (the standard final step —
StepFlow's last step carries the Run button/log; Run re-captures and re-renders, and pushes
every row into the Output log so the report is copyable text). The table is a simple grid of
themed `TextBlock`s (no new controls; follow the summary-table style used by Compare Grids).
Totals line REQUIRED (zero-found rule): "Found N link instances — X shared, Y origin-to-origin,
Z unloaded, W pinned" or "No Revit links found in this model."
Every row also `pushLog`ged on Run with severity: `warn` for Origin-to-Origin, unloaded, or
non-By-Host-View links; `info` otherwise.

### 3.4 Wiring

- `App.cs`: statics + `ExternalEvent.Create` for the scan handler (if the Compare Grids
  pattern needs one), and the Setup panel button `LT_LinkAudit` (glyph suggestion:
  `0xE9D9` "Diagnostic" or `0xE721` search — pick one that isn't already used on this panel)
  inserted between `LT_UpgradeLinks` and `LT_AlignCoordinates`.
- `ribbon.json`: `buttons.linkAudit` — label `"Link\nAudit"`, tip: "Read-only report on every
  linked model: positioning mode (Shared vs Origin-to-Origin), display mode, pinned state,
  workset, load status, and last-saved time. Changes nothing."
- `ILemoineToolCleanup.OnWindowClosed` nulls any handler callbacks (memory rule).
- Register the tool in `ToolsOverviewCatalog.cs` (grep how existing tools register; every
  ribbon tool has an Overview entry).

**HOLD POINT — Windows build + smoke test (ribbon order, Copy Datums run on a linked model,
Link Audit report) before Phase 4.**

---

## Phase 4 — Clean house (delete unused files)

**Commit message(s):** `Delete root plan files` / `Remove unused source files`

### 4.1 Root markdown cleanup — DELETE (user chose delete over archive)

Delete ALL root-level `plan-*.md` files (~86 of them, `git rm 'plan-*.md'` from repo root —
they are fully preserved in git history) **except** `plan-ribbon-lifecycle-and-repo-restructure.md`
(this file — it is the live execution document; it is deleted in Phase 7 when work completes).
Also delete `audit-logic-consistency.md` (stale audit output).
`docs/plans/` (17 archived plans) is left alone.

### 4.2 Unused-code audit (list first, delete only after user OK)

Write `devtools/audit_unused_files.py`:
for every `.cs`/`.xaml` under `Source/`, extract its declared top-level type names; count
word-boundary references to each across all other source files (excluding the defining file
and its own `.xaml`/`.xaml.cs` partner). Types with zero external references are candidates.
Cross-check: XAML-only usage (`x:Class`, element usage like `<lemoine:LemoineTitleBar>`),
reflection/command-string usage (`"LemoineTools.Commands.X"` strings in App.cs), and
`.csproj` `Remove`/`Exclude` entries. Emit `audit-unused-files.md` with per-candidate
evidence.

Known candidates to verify explicitly:
- `Source/Lemoine/Controls/Input/LemoineNumberStepper.xaml(.cs)` — CLAUDE.md calls it
  "retired"; grep shows no external references. Expected: DELETE.
- `Source/Lemoine/LemoineSettingsWindow.xaml(.cs)` — referenced by `LemoineControlStyles.cs`
  and `GlobalSettingsWindow.xaml.cs`; likely still live (per-tool settings window). Expected:
  KEEP — but confirm with the reference scan, and if live it renames in Phase 6.
- `Source/Lemoine/OverviewDemoTool.cs` / `OverviewSamples.cs` vs `ToolsOverviewDemos.cs` —
  possible superseded overlap; decide on evidence.
- `devtools/__pycache__/` — add `__pycache__/` to `.gitignore`, `git rm -r --cached` it.

**Present the candidate list to the user and wait for their OK before deleting any code
file.** Root plan-file deletion (4.1) is pre-approved and needs no second confirmation.

---

## Phase 5 — Folder reorg into ribbon categories

**Goal:** folders mirror the lifecycle ribbon, stale `T0x-` numbers gone (they already
collided — two different `T06-` folders exist today).

**Commit message:** `Reorganize source folders to match ribbon categories`

### 5.1 Mapping — `Source/Tools/` (all moves via `git mv`)

| From | To |
|---|---|
| `T08-Coordinates/`, `T09-UpgradeLinks/` | `Setup/` (merge; keep file names) |
| `T06-CopyLinear/`, `T06-CopyFromLink/` | `CopyFromLink/` (merge) |
| `T04-ModifyElements/` | `Modify/` |
| `T02-Ceilings/` | `Ceilings/` |
| `T03-LinkViews/` (minus `BulkRename/`), `T10-ScopeBoxes/`, `T07-ExplodeViews/` | `Views/` (ScopeBoxes and ExplodeViews keep their own subfolders: `Views/ScopeBoxes/`, `Views/ExplodeViews/`) |
| `T01-AutoFilters/`, `Testing/LegendCreator/` | `FiltersLegends/` (LegendCreator keeps subfolder `FiltersLegends/LegendCreator/`) |
| `T05-Clash/` (all subfolders intact) | `Dimensioning/` |
| `T03-LinkViews/BulkRename/`, `Testing/PlaceDependentViews/`, `Testing/AlignSheetViews/` | `Sheets/` (each keeps a subfolder) |
| `BulkExport/` | `Export/` |
| `Debuggers/` | `Debuggers/` (unchanged) |
| `Testing/TESTING_POLICY.md` | `docs/TESTING_POLICY.md` |
| `Testing/` (now empty) | removed |

### 5.2 Mapping — `Source/Commands/` mirrors the same category names

Every `Source/Commands/T0x-*/` and `Commands/BulkExport`, `Commands/Testing` move to
`Source/Commands/<Category>/` with the same assignments as their tools (e.g.
`Commands/Testing/PlaceDependentViewsCommand.cs` → `Commands/Sheets/`).

### 5.3 Mapping — `Source/Lemoine/` per-tool UI folders

| From | To |
|---|---|
| `Source/Lemoine/T01-AutoFilters/` | `Source/Tools/FiltersLegends/Windows/` |
| `Source/Lemoine/T02-Ceilings/` | `Source/Tools/Ceilings/Windows/` |
| `Source/Lemoine/T03-LinkViews/` | `Source/Tools/Views/Windows/` |
| `Source/Lemoine/T05-Clash/` (incl. `ClashDefinitions/`) | `Source/Tools/Dimensioning/Windows/` |
| `Source/Lemoine/T10-ScopeBoxes/` | `Source/Tools/Views/ScopeBoxes/Windows/` |
| `Source/Lemoine/Testing/LegendCreator/` | `Source/Tools/FiltersLegends/LegendCreator/Windows/` |

The REST of `Source/Lemoine/` (framework, `Controls/`, `Templates/`) does NOT move in this
phase — it becomes `Source/Framework/` in Phase 6 together with its namespace rename.

### 5.4 Namespace updates

- Folder moves alone don't break compilation (C# namespaces ≠ folders), but namespaces are
  updated to match anyway, scripted: e.g. `LemoineTools.Tools.CopyLinear` and
  `LemoineTools.Tools.CopyFromLink` → `LemoineTools.Tools.CopyFromLink`;
  `LemoineTools.Tools.Coordinates` + `...UpgradeLinks` → `LemoineTools.Tools.Setup`;
  `LemoineTools.Tools.Clash[...]` → `LemoineTools.Tools.Dimensioning[...]` (preserve the
  sub-namespace tail: `.AutoDimension.Core` etc.); `LemoineTools.Tools.Testing.*` → their
  categories. Update every matching `using` and fully-qualified reference (App.cs uses many
  `LemoineTools.Tools.X.Y` inline) — scripted, count-checked.
- **BEFORE merging two namespaces, grep both folders for duplicate top-level type names** —
  a collision means keeping distinct sub-namespaces for the colliding files; report it.
- **`LemoineTools.Commands` namespace does NOT change** — the ribbon `PushButtonData` strings
  (`"LemoineTools.Commands.<X>Command"`) are runtime-reflection contracts; keeping the
  namespace means zero App.cs command-string churn. Only the command FILES move.
- `Strings/en/*.json` file names stay as they are in this phase (they key by tool, not folder).

### 5.5 Checks

- `LemoineTools.csproj`: default globs sweep everything under the project root, so moves need
  no csproj item edits — but grep the csproj for any PATH-specific `Compile Remove`/`Page`/
  `None` entries referencing moved folders (and the sibling-project exclusions, which must
  survive untouched).
- Grep for remaining `T0[0-9]-` strings repo-wide (docs, scripts, comments) and fix.
- Remind the user: after pulling this phase on Windows, delete stale `obj/` folders before
  building (CLAUDE.md CS0579/CS0102/CS1504 stale-obj patterns).

**HOLD POINT — Windows build before Phase 6.**

---

## Phase 6 — Remove the `Lemoine` prefix from file names (types follow)

**Goal:** no source FILE carries the `Lemoine` prefix; each renamed file's TYPE renames with
it (C# file=type convention). Collision-prone names get descriptive replacements, not bare
strips. `Source/Lemoine/` → `Source/Framework/`, namespace `LemoineTools.Lemoine` →
`LemoineTools.Framework`.

**Commit message:** `Rename Lemoine-prefixed files and types to descriptive names`

### 6.1 THE DO-NOT-TOUCH LIST (persisted/external contracts — renaming these breaks users)

1. Root namespace / assembly / project: `LemoineTools`, `LemoineTools.csproj`,
   `LemoineTools.sln`, `LemoineTools.addin` (the .addin references the assembly + `App` full
   name — `LemoineTools.App` must keep working).
2. `%AppData%\LemoineTools\` paths, and ALL persisted file names inside it — especially
   `LemoineAutoFiltersV2.xml`, the `[XmlRoot("LemoineAutoFilters")]` attribute in
   `AutoFiltersSettings.cs`, the export default `FileName = "LemoineAutoFilters.xml"`, and
   `diagnostics.log`.
3. XAML/theme **resource keys** (they are quoted strings resolved at runtime; renaming is
   high-risk, zero payoff). Full vocabulary present today: `LemoineAccent`, `LemoineAccentDim`,
   `LemoineBg`, `LemoineBorder`, `LemoineBorderMid`, `LemoineCanvas`, `LemoineFS_XS/SM/MD/LG/XL`,
   `LemoineGreen`, `LemoineH_BtnMin/BtnSm/Input`, `LemoineKnobOn`, `LemoineMonoFont`,
   `LemoineRadius_SM/MD/Chip/Card`, `LemoineRaised`, `LemoineRed`, `LemoineSelectBg`,
   `LemoineSurface`, `LemoineText`, `LemoineTextDim`, `LemoineTextSub`,
   `LemoineTh_BtnPad/BtnSmPad/CardPad/FooterPad/InputPad`, `LemoineUiFont`, `LemoineWarnBg`,
   `LemoineWarnText`. These appear ONLY inside string quotes (`SetResourceReference(...,"LemoineText")`)
   — the word-boundary type-rename regex must therefore run with a guard: **never rewrite a
   match inside a quoted string whose value is in this list** (simplest: because none of these
   collide with any TYPE name being renamed, just verify post-run that every one of these keys
   still has the same repo-wide count as pre-run).
4. Extensible Storage schema GUIDs and schema/field names (`AutoDimOwnerSchema`,
   `ClashTagSchema`, `CopyLinearStampSchema` — already un-prefixed, but verify no renamed
   string feeds a schema name).
5. Transaction display names (`"Lemoine — Bulk Rename"` etc.) and the SWC comment string in
   `PushCoordinatesToLinksRunHandler` — user-visible undo-history branding, KEEP.
6. The `LemoinePreview/` sibling project (files, csproj, namespaces) — untouched this pass.
7. `Strings/` JSON keys and file names — not Lemoine-prefixed anyway; untouched.
8. The ribbon tab title `"Lemoine Tools"` and panel/button user-facing text — branding, KEEP.
9. `LemoineLog`'s on-disk log location/format — the TYPE renames (→ `DiagnosticsLog`) but the
   `%AppData%\LemoineTools\diagnostics.log` path constant inside it must not change.

### 6.2 Type + file rename table (68 declared types; files rename to match new type name)

Default rule = drop the `Lemoine` prefix. Exceptions marked ★ (collision-prone or clarity).

| Old type | New type | Note |
|---|---|---|
| `LemoineLog` | `DiagnosticsLog` | ★ bare `Log` collides conceptually everywhere |
| `LemoineRunLog` | `RunLogSink` | ★ |
| `LemoineRun` | `RunState` | ★ (cancel-flag holder) |
| `LemoineSettings` | `AppSettings` | ★ |
| `LemoineStrings` | `AppStrings` | ★ — keep the `using L = ...` alias working at every site |
| `LemoineTheme` | `ThemePalette` | ★ WPF-adjacent `Theme` too generic |
| `LemoineMotion` | `MotionEffects` | ★ |
| `LemoineIcons` | `GlyphIcons` | ★ |
| `LemoineIcon` (enum) | `GlyphIcon` | ★ |
| `LemoineFailureCapture` | `RevitFailureCapture` | ★ |
| `LemoineDatePicker` | `DateField` | ★ WPF has `DatePicker` |
| `LemoineSettingsWindow` | `ToolSettingsWindow` | ★ (only if kept after Phase 4) |
| `LemoineNumberStepper` | — | expected DELETED in Phase 4; else `NumberStepper` |
| `LemoineUiSize` (enum) | `UiSize` | |
| `LemoineButtonVariant` (enum) | `ButtonVariant` | |
| `ILemoineTool` | `IStepFlowTool` | ★ |
| `ILemoineReviewable` | `IReviewableTool` | ★ |
| `ILemoineToolCleanup` | `IToolCleanup` | |
| `ILemoineConditionalSteps` | `IConditionalSteps` | |
| `ILemoineNavigable` | `IStepNavigable` | ★ |
| `ILemoineRunPausable` | `IRunPausable` | |
| `ILemoineRunResult` | `IRunResult` | |
| `ILemoineStepConfirmable` | `IStepConfirmable` | |
| `ILemoineToolSettings` | `IToolSettings` | |
| `LemoineToolSettingsSpec` | `ToolSettingsSpec` | |
| `LemoineSettingDef` | `SettingDef` | |
| `LemoineSettingsGroup` | `SettingsGroup` | |
| `LemoineReloadHandler` | `ReloadHandler` | property of same name in App.cs is legal C# |
| `LemoineBrowserNode` / `LemoineBrowserTree` / `LemoineBrowserTreePicker` | `BrowserNode` / `BrowserTree` / `BrowserTreePicker` | |
| `LemoineControlStyles` | `ControlStyles` | |
| `LemoineDragGhost` / `LemoineListReorder` | `DragGhost` / `ListReorder` | |
| `LemoineEyeGlyph` / `LemoineSwatchGlyph` | `EyeGlyph` / `SwatchGlyph` | |
| `LemoineFileBrowser` / `LemoineFolderBrowser` | `FileBrowser` / `FolderBrowser` | |
| `LemoineInlineEdit` / `LemoineInlineStepper` | `InlineEdit` / `InlineStepper` | |
| `LemoineLegendBlockRow` / `...Builder` / `...GroupCard` / `...LayoutBar` / `...Palette` / `...Preview` / `...Row` | `LegendBlockRow` / `LegendBuilder` / `LegendGroupCard` / `LegendLayoutBar` / `LegendPalette` / `LegendPreview` / `LegendRow` | |
| `LemoineMatrixInput` / `LemoineNamingSlots` / `LemoineNumberRange` / `LemoineTokenInput` / `LemoineTagChipInput` / `LemoineTextField` | `MatrixInput` / `NamingSlots` / `NumberRange` / `TokenInput` / `TagChipInput` / `TextField` | |
| `LemoineMultiSelectTabs` / `LemoineSingleSelect` / `LemoineSearchAutocomplete` | `MultiSelectTabs` / `SingleSelect` / `SearchAutocomplete` | |
| `LemoineColorPickerPanel` / `LemoineColorPickerWindow` / `LemoineSwatchPicker` / `LemoineCategoryChip` | `ColorPickerPanel` / `ColorPickerWindow` / `SwatchPicker` / `CategoryChip` | |
| `LemoineReviewSummary` / `LemoineSectionCard` / `LemoineTitleBar` / `LemoineToggleSwitches` / `LemoineWarnBanner` | `ReviewSummary` / `SectionCard` / `TitleBar` / `ToggleSwitches` / `WarnBanner` | |
| `LemoineTemplateInfo` / `LemoineTemplateStore` | `TemplateInfo` / `TemplateStore` | verify no XML root/element derives from these names before renaming (TemplateStore serializes tool DTOs, not itself — confirm) |

### 6.3 Execution mechanics (Python, one script, dry-run first)

1. Build the replacement list from 6.2 **sorted by descending length**, each applied as
   `\bOld\b` regex across every `.cs`, `.xaml`, and `.md` under `Source/`, `LEMOINE_UI.md`,
   `CLAUDE.md` (docs pass included — CLAUDE.md references `LemoineLog`, `ILemoineTool`,
   `LemoineMultiSelectTabs`, etc. extensively and MUST stay accurate).
2. Pre-count every pattern; store expected counts; dry-run prints per-file/per-pattern counts;
   only write after a human-readable summary is produced. Assert zero remaining
   `\b(class|interface|enum) I?Lemoine` declarations afterward (except the do-not-touch names,
   which contain no type declarations).
3. XAML: rename `x:Class="LemoineTools.Lemoine.LemoineX"` values, XML namespace imports
   (`xmlns:lemoine="clr-namespace:LemoineTools.Lemoine..."` → `...Framework...`), and element
   usages (`<lemoine:LemoineTitleBar` → `<lemoine:TitleBar`). The xmlns prefix string
   `lemoine:` itself may stay (it's a local alias, not a file name).
4. `git mv` each `Lemoine*.cs` / `Lemoine*.xaml` (+`.xaml.cs`) to its new-type file name.
   `git mv Source/Lemoine Source/Framework`. Then rename namespace
   `LemoineTools.Lemoine` → `LemoineTools.Framework` (word-boundary on the full dotted string,
   longest-first: `LemoineTools.Lemoine.Controls` before `LemoineTools.Lemoine`).
5. Post-run verification: repo-wide count of each resource key from 6.1(3) unchanged;
   `grep -rE '\bI?Lemoine[A-Z]'` residue review — every remaining hit must be on the
   do-not-touch list (resource keys, `LemoineTools`, `LemoineAutoFilters*`, branding strings,
   `LemoinePreview`); strings key-set verification; silent-failure scan is N/A (no logic
   changed) but run it anyway on the diff.
6. `LemoineTools.csproj`: update the two `Compile Remove` sibling-exclusion patterns ONLY if
   they reference `Source/Lemoine` paths (check; the known exclusions target sibling project
   folders, not `Source/`).

**HOLD POINT — Windows build (clean `obj/` first) + full smoke test before Phase 7.**

---

## Phase 7 — Wrap-up

**Commit message:** `Update docs for restructure and remove completed plan`

1. Final docs pass: `CLAUDE.md` "Key Files" table (`Source/Lemoine/...` → `Source/Framework/...`,
   renamed types), `LEMOINE_UI.md` (component names, folder map), any `docs/` references.
2. Full strings key-set verification across ALL JSON files.
3. Full-diff silent-failure scan, findings reported per CLAUDE.md protocol.
4. Delete `plan-ribbon-lifecycle-and-repo-restructure.md` (work complete; history keeps it).
5. Final push. PR only if the user asks.

---

## Risk register

| Risk | Mitigation |
|---|---|
| No Linux compile — mass renames verified only by count-checks | Phase-per-commit; 3 Windows-build hold points; word-boundary + longest-first + expected-count assertions; dry-run before write |
| `LemoineRun`/`LemoineRunLog`/`LemoineLog` substring family | `\b` boundaries make them independent; longest-first as belt-and-braces |
| Resource keys accidentally renamed (they share the prefix with types) | none of the 39 key names equals a renamed type name; post-run per-key count assertion |
| Persisted XML (`LemoineAutoFiltersV2.xml`, `[XmlRoot("LemoineAutoFilters")]`) | on the do-not-touch list; grep-verified after Phase 6 |
| Ribbon command strings (`"LemoineTools.Commands.X"`) break if command namespace changes | `LemoineTools.Commands` namespace is frozen; only `CopyGridsCommand`→`CopyDatumsCommand` changes, updated in the same commit as its button |
| Stale `obj/` after moves poisons Windows build (CS0579/CS0102/CS1504) | explicit "clean obj/ before building" instruction at both build hold points |
| Namespace merges collide (CopyLinear+CopyFromLink, Coordinates+UpgradeLinks) | pre-merge duplicate-type grep; keep sub-namespaces on collision |
| Link Audit positioning-mode API unverified on Revit 2024 | isolated best-effort method; reports "Unknown" rather than guessing; flagged for the Windows smoke test |
| Sonnet-5 executor drifting from plan | Section 0 rules; re-read at each phase; STOP-on-discrepancy rule |

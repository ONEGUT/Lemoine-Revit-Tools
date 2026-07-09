# Plan — Multi-Tool Upgrade Pass (2026-07)

This plan covers eleven workstreams requested in one message. It is written for an
implementing agent (Sonnet) that has NOT read the original conversation — every item
says exactly which files change, what the change is, and which Revit API constraints
apply. Feasibility for each request has been checked against the current source and
the checked-in Revit 2024 API DLL (`libs/RevitAPI.dll`); anything unverifiable from
Linux is flagged **[verify on Windows]**.

## How to implement

- **One branch per workstream** (CLAUDE.md branch rules apply). Suggested order and
  branch names are given per workstream. WS-10 (Settings) and WS-11 (Overview) must
  come **after** WS-2 and WS-8, because they document the post-rework ribbon.
- All new user-facing text goes through `AppStrings.T(...)` with keys added to the
  matching `Strings/en/*.json` file — never hardcoded literals (CLAUDE.md “Text
  Externalization”). Run-log lines included.
- **No UI mockups are required for this pass** — the user explicitly waived the
  mockup-before-code step for these changes. The `/revit-navisworks-ui` skill must
  still be invoked before any WPF work.
- After each workstream: run the CLAUDE.md post-change silent-failure scan and report
  it, and verify every referenced `AppStrings.T` key exists in the JSON.
- The project only compiles on Windows; on this repo only the 2024 configuration has
  real API DLLs (`libs2025/2026/2027` are placeholders). Anything gated to 2025+ can
  be scaffolded but not compile-verified here — see WS-8 notes.

## Decisions needed from the user (defaults are chosen; flag before/while implementing)

1. **WS-1** Placement list becomes exactly the four requested options (Internal
   Origin, Project Base Point, Center to Center, Survey Point). **“By Shared
   Coordinates” is removed** from the picker. Default: remove it. If the user wants
   it kept, add it back as a fifth option — the code path already exists.
2. **WS-6** A Revit level can carry only **one** scope box (its scope-box parameter is
   a single ElementId). In Per-Level mode with stacked boxes, level `L2` is the top of
   box `L1` *and* the bottom of box `L2` — both cannot be recorded. Default behavior
   chosen: each level is assigned to the box whose **bottom** sits on it; the very top
   level (top of the highest box) is also assigned to that highest box since nothing
   else claims it. With multiple clusters (A/B buildings) per level the conflict is
   worse (two boxes want the same level) — assign to the **first cluster (A)** and log
   a warn for the rest. Confirm this is acceptable.
3. **WS-7** The Revit API **cannot resize a scope box in X/Y** (empirically confirmed
   by the Scope Box Probe on Revit 2026: only `VOLUME_OF_INTEREST_HEIGHT` is
   writable; width/depth have no writable parameter, and `CopyElement` copies keep the
   seed’s footprint). Therefore grid-bound sides and split-at-gridline can position
   copies and set exact heights, but the X/Y footprint must be dragged by hand once —
   the tool logs the exact required W×D per box. The plan below implements it that
   way. Confirm this degraded form is still wanted, or defer WS-7’s bind/split until
   Autodesk exposes extents.
4. **WS-10** Tools should stop writing their last-run output folders/patterns back
   into the persisted defaults; the Settings window becomes the only writer of
   defaults, and path fields stay blank until the user sets them. This removes the
   “remembers the folder I used last run” convenience in Bulk Export / Upgrade &
   Link. Default: implement as requested.
5. **WS-10** Settings tabs for ribbon groups with **no** persistent settings (Modify,
   Sheets, Filters & Legends) — default: **omit the tab entirely** (“if there are no
   persistent default settings of a function it does not need to be listed”). If a
   1:1 ribbon mirror is preferred, keep the tab with a one-line note instead.

---

# WS-1 — Upgrade & Link Models (branch: `upgrade-links-placement-fixes`)

Files: `Source/Tools/Setup/UpgradeLinksModels.cs`, `UpgradeLinksViewModel.cs`,
`UpgradeLinksRunHandler.cs`, `UpgradeLinksScanHandler.cs`, `UpgradeLinksSettings.cs`,
`Strings/en/upgradeLinks.json`.

## 1a. Placement options → Internal Origin / Project Base Point / Center to Center / Survey Point

Current enum (`UpgradeLinksModels.cs`):
`UpgradePlacement { OriginToOrigin, CenterToCenter, SharedCoordinates, Site }`
mapped 1:1 to `ImportPlacement { Origin, Centered, Shared, Site }`.

API facts:
- `ImportPlacement.Origin`   = Auto – Internal Origin to Internal Origin.
- `ImportPlacement.Centered` = Auto – Center to Center.
- `ImportPlacement.Site`     = Auto – Project Base Point to Project Base Point.
  **[verify on Windows]** — this is the documented mapping of the 4th Auto option in
  the Link Revit dialog; verify once by linking a file with offset base points and
  checking it lands PBP-on-PBP. If `Site` turns out not to be PBP-to-PBP on the
  target Revit year, fall back to the manual-translate technique used for Survey
  Point below (translate by `hostPBP.Position − linkPBP.Position`).
- **Survey Point to Survey Point has no ImportPlacement value.** Implement manually:
  1. In the run handler, while the link document is still open (it always is — the
     tool opens each file to upgrade it), read
     `BasePoint.GetSurveyPoint(linkDoc).Position` (internal coords) and stash it on
     the per-file item. Read `BasePoint.GetSurveyPoint(hostDoc).Position` once.
  2. Link with `ImportPlacement.Origin`, keep the created `RevitLinkInstance` id
     (`RevitLinkInstance.Create` returns it — capture it; today the return value is
     discarded in `LinkIntoHost`).
  3. `ElementTransformUtils.MoveElement(hostDoc, instanceId, hostSP − linkSP)` inside
     the same transaction. If the instance is pinned, unpin → move → restore
     (CLAUDE.md “Unpin before transforming”).
  4. Null-check both base points; if either is missing, fall back to Origin and log a
     warn (`upgradeLinks.log.surveyFallback`).

Changes:
- Rename/replace enum members: `UpgradePlacement { InternalOrigin, ProjectBasePoint,
  CenterToCenter, SurveyPoint }`. **Migration:** `UpgradeLinksSettings` persists
  `DefaultPlacement` via XmlSerializer — old files contain `OriginToOrigin` /
  `SharedCoordinates` / `Site` strings, which would make deserialization of the whole
  settings file throw (and silently reset). Either keep the old enum member names as
  `[XmlEnum]`-compatible aliases (add new members, keep old ones marked obsolete and
  map them on load), or simplest: keep the CLR enum member names
  `OriginToOrigin/CenterToCenter/Site/…` — NO. Cleanest robust option: change the
  persisted property to a `string` token with a tolerant parse (unknown → InternalOrigin)
  and keep the enum internal to the session. Pick one and note it in the PR.
- `UpgradePlacementMap.ToImportPlacement`: InternalOrigin→Origin, CenterToCenter→
  Centered, ProjectBasePoint→Site, SurveyPoint→Origin (+ post-move flag).
- `UpgradeLinksViewModel.PlacementOrder` / `PlacementKey` / labels: four entries, keys
  `internalOrigin`, `projectBasePoint`, `center`, `surveyPoint`; update
  `upgradeLinks.json` `placement` block (labels: “Internal Origin”, “Project Base
  Point”, “Center to Center”, “Survey Point”).
- `LinkIntoHost` gains the survey-point translate step; the existing
  Shared-Coordinates fallback branch is removed with the option (see Decision 1).
- The run handler’s ReloadExisting path: when the type already exists and carries
  instances, placement is not re-applied — unchanged, but add a log line when the
  requested placement was therefore not applied.

## 1b. Fix the misaligned Save-as / Version / Placement columns (see user screenshot)

`UpgradeLinksViewModel.BuildHeaderRow`/`BuildFileRow`/`FileRowGrid`:
- The header grid and data-row grid use the same column widths, but the header has no
  element in column 4 (remove button, `Auto`) so header columns 2–3 sit further right
  than the data cells, and the version pill is `HorizontalAlignment.Left` inside a
  118 px column while the data rows are two lines tall (name box + path) with the
  badge vertically centered — reading as floating/offset.
- Fix: give column 4 a **fixed width** (e.g. 36) in `FileRowGrid()` so header and
  rows always align; add an empty placeholder (or the same fixed width) in the header
  row; top-align the version badge and placement picker with the name box
  (`VerticalAlignment.Top` + a top margin matching the name box height) OR vertically
  center the whole left cell — pick one and apply it to badge *and* placement picker
  so all three columns share one baseline. Header label margins must match data cell
  padding (data rows have `Margin 12,9,12,9`; the header uses the same grid — ensure
  header labels get the same 4 px left inset as content, currently the badge has a
  7 px internal padding that shifts it).

## 1c. Fix “Set all placement” always displaying Origin to Origin

Root cause (`UpgradeLinksViewModel.RebuildFilesTable`, line ~178): the Set-all
`SingleSelect` is recreated on every rebuild with
`SelectedItem = PlacementLabel(_defaultPlacement)`, and `_defaultPlacement` is a
`readonly` field from settings — so after every selection (which triggers
`RebuildFilesTable()`) the control resets to the default label even though the rows
were updated correctly.

Fix: add a `private UpgradePlacement? _setAllSelection;` field. The Set-all
`SelectionChanged` stores it before applying to rows; `RebuildFilesTable` seeds the
control with `_setAllSelection ?? _defaultPlacement`. (Do NOT stop rebuilding — the
rows’ own pickers need the refresh.)

## 1d. Newer-than-current versions cannot be imported/upgraded

Revit cannot open files saved in a **later** version (not backwards compatible).
- `UpgradeLinksScanHandler`: it already extracts the 4-digit year and the current
  `VersionNumber`. Add `public bool IsFutureVersion` to `UpgradeFileScan` — true when
  both parse as ints and `fileYear > currentYear`.
- `UpgradeFileRow`: mirror the flag. A future-version row is treated like an
  unreadable row (grayed name box, placement picker disabled, excluded from
  `ReadableCount()`/the run spec) but with its own badge: red pill
  `“2027 — too new”` (`upgradeLinks.labels.verTooNew`).
- Add a permanent hint line under the files table (always visible once ≥1 row
  exists): `upgradeLinks.labels.futureVersionNote` = “Files saved in a Revit version
  newer than this one (20XX) cannot be opened, upgraded, or linked — Revit is not
  backwards compatible.” Fill `{0}` with the current version captured at scan time
  (stash `current` on the scan result so the VM can show it).
- Review step: include future-version rows in the existing unreadable warning or add
  a dedicated `review.warnTooNew` line.

---

# WS-2 — Ribbon: deactivate Link Audit & Compare Grids, remove Scope Box Probe (branch: `ribbon-deactivate-audit-compare`)

File: `Source/App.cs`.

- Add near the top of `App`:
  `private const bool ShowRetiredSetupTools = false; // Link Audit + Compare Grids — deactivated, not deleted`
  and wrap the two `setupPanel.AddItem(...)` calls for `LT_LinkAudit` and
  `LT_CompareGrids` in `if (ShowRetiredSetupTools) { ... }`. Do **not** delete
  `LinkAuditCommand/Window/Capture/Models`, `CompareGrids*` — code stays, buttons go.
  Keep their `CompareGridsRunHandler` ExternalEvent registration (harmless, and
  reactivation is then one flag).
- **Scope Box Probe: full removal.** Delete the Developer-panel button block
  (`LT_ScopeBoxProbe`), and since that panel then has no buttons, delete the
  `devPanel` creation too (keep the comment block explaining the Developer panel is
  created on demand for future harnesses). Delete
  `Source/Tools/Debuggers/ScopeBoxProbe/ScopeBoxProbeCommand.cs` (its findings are
  already captured in CLAUDE.md and in `ScopeBoxCreatorRunHandler` comments).
- `Strings/en/ribbon.json`: keep the linkAudit/compareGrids keys (code still
  references them behind the flag).

---

# WS-3 — Align Coordinates: cross-grid filtering in the override pickers (branch: `align-coords-grid-filter`)

Files: `Source/Commands/Setup/AlignCoordinatesCommand.cs`,
`Source/Tools/Setup/CoordinatesModels.cs`, `AlignCoordinatesViewModel.cs`.

Answer to the user’s feasibility question: **do it up-front at window open, not per
selection.** Grid counts are tens-to-hundreds; capturing two endpoints per grid on
the main thread at command launch is negligible (micro-seconds per grid), and the
intersection test is then pure 2D math on the UI thread — no Revit call, no lag.

- `CoordinatesModels.cs`: add
  ```csharp
  public sealed class GridGeom
  {
      public string Name = "";
      public bool   IsLine;           // false → arc/spline: treated as always-crossing
      public double X0, Y0, X1, Y1;   // line endpoints, that document's internal coords
  }
  ```
  `AlignCoordinatesData.HostGrids` → `List<GridGeom>` (keep `HostGridNames` derived or
  replace usages), and `AlignLinkInfo.Grids` → `List<GridGeom>` alongside/replacing
  `GridNames`.
- `AlignCoordinatesCommand.CollectData`: for each `Grid`, read `grid.Curve`; if it is
  a `Line`, store endpoints (`GetEndPoint(0/1)`); otherwise `IsLine=false`. Wrap in
  try/catch → `DiagnosticsLog.Swallowed` per existing style. **Intersections are
  tested within each document’s own coordinate space** (host pair against host grids,
  link pair against that link’s grids), so no transform is needed.
- `AlignCoordinatesViewModel`: add
  `private static bool GridsCross(GridGeom a, GridGeom b)` — segment/segment
  intersection with a tolerance (extend each segment by ~1 ft at both ends to catch
  grids that meet exactly at their extents; treat non-line grids as crossing
  everything, i.e. never filtered out).
- Wire it in **both** places grids are picked:
  - Host pair (`RebuildHostGridFields`): when Grid 1 changes, rebuild Grid 2’s
    `Items` to only grids crossing Grid 1 (excluding Grid 1 itself). If the current
    Grid 2 no longer qualifies, select the first candidate. Symmetric behavior is NOT
    needed (only Grid 2 filters) — keep it simple and match the request.
  - Per-link override (`RebuildOverridePanel`): same pattern for `g2Sel` based on
    `spec.Grid1Name`.
  - If Grid 1 crosses nothing, show the existing `Dim(...)` style note
    (“No grids cross ‘{0}’ — pick a different Grid 1.”) and leave Grid 2 empty +
    step invalid.
- This tool’s strings are currently hardcoded (pre-externalization file). New strings
  should go to a new `Strings/en/setup.alignCoordinates.json` — externalize only the
  strings you touch/add; a full externalization pass of this file is out of scope.

---

# WS-4 — Copy Datums: show already-copied datums grayed out (branch: `copy-datums-show-existing`)

Files: `Source/Framework/Controls/Input/MultiSelectTabs.xaml.cs`,
`Source/Tools/CopyFromLink/CopyDatumsViewModel.cs`, `Strings/en/copy.datums.json`,
(scan already sets `CopyDatumItem.ExistsInHost` — no handler change).

- `MultiSelectTabs`: add
  `public IReadOnlyCollection<string>? DisabledItems { get; set; }` (set **before**
  `SetGroups`, same contract as `Hierarchy`). In the item-row builder: when an item
  is in `DisabledItems`, render the checkbox `IsEnabled=false`, label with
  `LemoineTextDim` foreground, skip it in the per-group “All” toggle and in
  `SelectionChanged` results, and ignore clicks. Update the class-level doc comment
  and the “MultiSelectTabs Contract” section of CLAUDE.md.
- `CopyDatumsViewModel.RebuildDatums`: stop filtering out `ExistsInHost` items. Build
  the same Grids/Levels tabs from **all** items; disabled set = items with
  `ExistsInHost`, displayed as `“{name}  (already in host)”`
  (`copy.datums.labels.existingSuffix`) so the reason is visible. Keep the
  name-collision “ (Level)” suffix logic, applied before the existing-suffix. Default
  selection stays “all copyable”. Remove the `someHidden` note (nothing is hidden
  anymore); keep `allExist` for the case where nothing is copyable (now shown with
  the full grayed list rather than an empty step).

---

# WS-5 — Ceiling Heatmap: generate RCPs like Ceiling Grids (branch: `heatmap-generate-rcps`)

Files: `Source/Commands/Ceilings/CeilingHeatmapCommand.cs`,
`Source/Tools/Ceilings/CeilingHeatmapViewModel.cs`, `CeilingHeatmapEventHandler.cs`,
`Strings/en/ceilings.heatmap.json`.

Model on `MakeCeilingGridsRunHandler.RunGrids` (find-or-create one RCP per level,
`ViewPlan.Create(doc, ceilingPlanVft.Id, level.Id)`, then run the normal pipeline on
those views).

- Command: also capture the level list (`Name`, `ElementId`, elevation) on the main
  thread and pass it to the VM.
- VM S1 becomes mode-switched (same `SingleSelect` pattern as Bulk Views’ mode):
  - **“Use existing RCP views”** — current `BrowserTreePicker` behavior, unchanged.
  - **“Generate heatmap RCPs per level”** — a `MultiSelectTabs` of levels (all
    pre-selected), plus a name-suffix text box defaulting to `_Heatmap`
    (find-or-create by name `Sanitize(level.Name) + suffix`, mirroring
    `_CeilingGrid`). Optional: a view-template `SingleSelect` (CeilingPlan templates,
    “(none)” default) — cheap to add since MakeCeilingGrids already proves the
    pattern; include it.
  - Validation: existing mode needs ≥1 view; generate mode needs ≥1 level.
- Handler: add `GenerateForLevelIds : List<ElementId>`, `GenerateSuffix`,
  `GenerateTemplateId` inputs. When non-empty, phase 0 of `Execute` find-or-creates
  the RCPs in their own transaction (reuse/extract the find-or-create +
  `SetCeilingOnlyVisibility`-equivalent from MakeCeilingGrids — do **not** hide
  ceilings here; the heatmap needs them visible, so only create + optional template),
  collects the created view ids into the existing per-view pipeline input, then
  proceeds exactly as today. Log “Created N RCP views / reused M”. Clear the new
  payload lists in the handler’s `finally` (memory discipline).
- Review step: show mode + level/view counts.

---

# WS-6 — Scope Box Creator: per-level datum assignment + end extension (branch: `scope-box-creator-levels`)

Files: `Source/Tools/Views/ScopeBoxes/ScopeBoxCreatorRunHandler.cs`,
`ScopeBoxSettings.cs`, `ScopeBoxCreatorViewModel.cs`, `Strings/en/scopeBoxes.creator.json`.

## 6a. Auto-assign bottom/top levels to each Per-Level box

After a copy is created and renamed in `RunBoxes` (PerLevel branch only): set
`level.get_Parameter(BuiltInParameter.DATUM_VOLUME_OF_INTEREST).Set(copyId)` for the
box’s bottom level, and for the box spanning the **highest** selected level also its
top level (the level above). Constraint & conflict policy per **Decision 2** above:
- Build the planned assignments first (`levelId → boxId`), resolving conflicts:
  bottom-of-box claim wins over top-of-box claim; first cluster wins over later
  clusters (log a warn naming the losing box).
- A datum can only carry a box whose extents it crosses — the bottom level always
  crosses its box; guard with try/catch and log-and-skip (Revit throws “Datum plane
  does not intersect the Scope Box” otherwise; see the Manager’s `IntersectsBox`).
- Skip levels whose parameter is null/read-only (same guards as
  `ScopeBoxManagerHandlers.SetParamTargets`).
- Do this inside the existing transaction, after all boxes are created (so the
  conflict resolution sees the full batch).

## 6b. Top and bottom boxes extend ±100 ft past the source levels (Per-Level mode)

- `ScopeBoxSettings`: add `public double PerLevelEndExtension { get; set; } = 100.0;`
  (feet).
- In `RunBoxes` PerLevel branch: identify the lowest and highest **selected** levels;
  for specs on the lowest level, `ZBot -= ext`; for specs whose top is the highest
  level’s top, `ZTop += ext`. Both work today: the copy is bottom-aligned by
  translation (ZBot) and `VOLUME_OF_INTEREST_HEIGHT` is writable (probe-confirmed),
  so the exact span lands without manual work.
- Expose the value as a third stepper in the Creator’s S1 geometry section
  (`scopeBoxes.creator.labels.endExtension` + hint) and in the Settings window’s
  Scope Box section (WS-10), saved via `ScopeBoxSettings.Save()` like its siblings.
- Note the interaction with 6a: the extended bottom box still has its bottom *level*
  = lowest selected level (assignment unchanged); only geometry extends.

---

# WS-7 — Scope Box Manager: duplicate / grid-bound sides / split (branch: `scope-box-manager-resize`)

Files: `Source/Tools/Views/ScopeBoxes/Windows/ScopeBoxManagerWindow.xaml.cs`,
`ScopeBoxManagerHandlers.cs`, `Strings/en/scopeBoxes.manager.json`.

**Hard API constraint (read first):** the probe confirmed scope boxes can be copied,
renamed, moved, rotated, and height-set (`VOLUME_OF_INTEREST_HEIGHT`, grows from
MinZ) — but X/Y width/depth **cannot** be changed by the API on any current Revit
year. Every feature below therefore produces boxes with the *source’s* footprint,
positioned at the computed target center with exact height, and logs/flashes the
required `W × D` per box for one manual handle-drag. Surface this in the UI with a
persistent note in each overlay (“Revit’s API cannot resize a scope box footprint —
after Apply, drag the highlighted box handles to W × D shown.”). See Decision 3.

The scan already carries per-box `MinX..MaxZ` and per-datum XY bounds/elevation
(`ManagerDatumRef`), so all target math is available without new capture.

## 7a. Duplicate

- New per-box action button next to Delete in the name card: **Duplicate**. Run
  handler action token `"Duplicate"`: `ElementTransformUtils.CopyElement(doc, boxId,
  XYZ.Zero)` → rename to a unique `“{name} - Copy”`/`“- Copy 2”` (pre-check against
  taken names, same discipline as the Creator), commit, rescan. Optionally offset the
  copy by a few feet in X so it isn’t perfectly hidden under the original — do offset
  by `(10, 0, 0)` ft and mention it in the status line.

## 7b. Bind sides to grids (N / S / E / W)

- New action in the box editor: **Bind sides to grids…** overlay. Four
  `SingleSelect`s — North, South, East, West — each listing grids from
  `_scan.Datums` where `Kind=="Grid"` and `HasBounds`, classified by orientation:
  a grid whose bbox is wide-and-flat (`(MaxX−MinX) > (MaxY−MinY)`) is “horizontal” →
  candidate for North/South (its Y defines the edge); tall-and-thin is “vertical” →
  East/West (its X defines the edge). Each dropdown also offers “(keep current
  edge)”.
- Target rect: `north → MaxY = grid.MidY`, `south → MinY`, `east → MaxX`,
  `west → MinX` (mid of the thin bbox axis). Levels: per the request “levels should
  be automatic” — after Apply, auto-assign every level whose elevation lies inside
  the box’s Z-range **and is not already carried by another box** to this box
  (single-carrier constraint again; skip-and-log conflicts).
- Apply (run handler action `"BindSides"`): move the existing box so its center lands
  on the target rect center (`MoveElement`), set height if the Z range was left
  unchanged (it is — binding is XY only, so height untouched), then log
  `“‘{name}’: drag handles to {W:0.#} × {D:0.#} ft (currently {w} × {d})”` as a
  `warn`-style status + a `DiagnosticsLog.Warn`. Rescan.

## 7c. Split at a gridline / split in the middle, with overlap

- New action: **Split…** overlay with:
  - Mode `SingleSelect`: “At a gridline” | “In the middle”.
  - Gridline mode: a `SingleSelect` of grids that actually cross the box
    (`IntersectsBox`) and are orientation-classified as above; split axis follows the
    grid’s orientation.
  - Middle mode: an axis choice (East–West | North–South).
  - Overlap `InlineStepper` (feet, default 0, min 0) — each half extends past the
    split line by half the overlap.
  - A read-only preview line: “Left/South half: X×Y ft · Right/North half: X×Y ft”.
- Apply (action `"Split"`): two `CopyElement` copies of the source box, renamed
  `“{name} - 1”` / `“{name} - 2”` (unique-checked), each **moved** so its center is
  the corresponding half’s center; height/Z copied as-is (same Z range). Then
  **delete the original** only if the user ticked “Delete original after split”
  (`CheckBox`, default on). Each half logs its required W×D (footprints can’t be set
  — see the constraint banner). Views/datums that referenced the original keep
  nothing once it’s deleted — so before deleting, reassign the original’s views and
  datums to half 1 (log this) or, simpler and safer: when “delete original” is on,
  first `SetParamTargets`-style move all its view/datum references to half 1, then
  delete. Rescan at the end (the existing post-action rescan covers it).

All three actions run through `ScopeBoxManagerRunHandler` — add the new action tokens
to `RunAction`’s switch, clear all new payload fields in the `finally`, and follow the
existing `ConfigureFailures` + counting patterns.

---

# WS-8 — Merge the view-creation tools into one “Bulk Views” tool (+ per-link views) (branch: `bulk-views-merge`)

This is the largest workstream. Goal: one ribbon button **Bulk Views** whose first
step is a mode dropdown, replacing: Bulk Views by Level (`LinkViewsLevel*`),
Duplicate Views (`ViewsBulkDuplicate*`), Bulk Views by Template (`ViewsByTemplate*`),
Replicate Dependent Views (`ReplicateDependentViews*`).

## 8a. Architecture

- **Keep all four run handlers unchanged** (they are clean, tested, and independent):
  `LinkViewsLevelRunHandler`, `ViewsBulkDuplicateRunHandler`,
  `ViewsByTemplateRunHandler`, `ReplicateDependentViewsRunHandler`, plus one new
  `ViewsByLinkRunHandler` (8c). The merge is a **ViewModel/Command-level** rework.
- New `Source/Tools/Views/BulkViewsViewModel.cs` implementing `IStepFlowTool,
  IConditionalSteps, IStepAware, IReviewableTool, IRunResult, IToolCleanup`.
  Mode tokens (logic identifiers, not externalized): `"ByLevel"`, `"Duplicate"`,
  `"ByTemplate"`, `"ReplicateDependents"`, `"ByLink"`.
- Steps (superset; hidden per mode via `IConditionalSteps.IsStepVisible`):

  | id | content | visible for |
  |----|---------|-------------|
  | `mode`    | mode dropdown + per-mode hint | always |
  | `srcLvl`  | levels + extents (By Level / By Scope Box sub-mode) — current LinkViewsLevel S1 | ByLevel |
  | `types`   | 3D/FP/RCP toggles + subdiscipline/template rows — current S2 | ByLevel |
  | `srcViews`| BrowserTreePicker of source views — shared builder | Duplicate, ByTemplate, ReplicateDependents |
  | `dupOpts` | duplicate-mode dropdown — current ViewsBulkDuplicate S2 | Duplicate |
  | `tmpl`    | template MultiSelectTabs — current ViewsByTemplate S2 | ByTemplate |
  | `deps`    | replicate-dependents source/target config — current ReplicateDependentViews steps (fold its content in; if it needs two steps keep two conditional ids) | ReplicateDependents |
  | `links`   | link picker + options | ByLink |
  | `naming`  | naming step — NamingSlots for ByLevel (as today), TokenInput for the rest (as today); build per-mode content in one step id, rebuilt on activation | all except ReplicateDependents (which has no naming today — keep hidden) |
  | `run`     | review (`IReviewableTool`) | always |

  `IConditionalSteps` rules: a conditional step must never be last — `run` is always
  visible ✔. On mode change: `Fire()` (re-evaluates visibility) and rebuild dependent
  steps via the `IStepAware` refresh callback on activation (`OnStepActivated` →
  `_refreshStep(stepId)` for every mode-dependent step — same pattern as
  BulkExport’s S2/S3/S6).
- Move the per-mode state and step-builders over from the four existing VMs
  **verbatim where possible** (they are self-contained builder methods). The old VM
  classes and their commands (`LinkViewsLevelCommand`, `ViewsBulkDuplicateCommand`,
  `ViewsByTemplateCommand`, `ReplicateDependentViewsCommand`) are deleted;
  `BulkViewsCommand` captures the union of main-thread data (levels, scope boxes,
  templates by family, all views + browser tree, dependent-view info, link list) —
  all of it is captured by the existing commands today, so consolidate their
  capture code into one `BuildTool()`.
- `Run(...)`: switch on mode, configure the matching handler exactly as each old VM’s
  `Run` does, raise that handler’s event. `OnWindowClosed` nulls callbacks on **all**
  handlers it may have touched.
- Ribbon (`App.cs`): remove `LT_LinkViewsLevel` and the `LT_DuplicateViews` pulldown;
  add one `LT_BulkViews` button (`ribbon.buttons.bulkViews.*`, glyph 0xE8A9 ViewAll).
  Scope Boxes pulldown stays. Strings: new `linkviews.bulkViews.json` for the merged
  chrome; per-mode step content keeps reusing the existing four JSON files’ keys
  (they already exist — reference them rather than duplicating).
- Old strings files stay (still referenced). Settings: none of the four persisted
  settings, so nothing to migrate.

## 8b. Optimization notes for the merge

- Capture the browser tree **once** and share it across modes.
- The heavy per-mode captures (dependent-view scan for ReplicateDependents) should be
  deferred: capture lazily via the existing `ReloadHandler` pattern is overkill —
  simplest is to capture everything up-front as the old commands did; the dependent
  scan is main-thread cheap. Only if `ReplicateDependentViewsCommand`’s capture is
  measurably slow should it move behind a scan handler. (Check its command before
  deciding; do not prematurely engineer.)

## 8c. New mode: one view per linked file (“By Link”)

Create, for each selected `RevitLinkInstance`, one view showing **only that link**.

API facts (verified against `libs/RevitAPI.dll`, i.e. already present in **2024**):
`View.GetLinkOverrides(ElementId)` / `View.SetLinkOverrides(ElementId,
RevitLinkGraphicsSettings)` and `RevitLinkGraphicsSettings.LinkVisibilityType`
(enum `LinkVisibility`: `ByHostView`, `Custom`, and by-linked-view) +
`RevitLinkGraphicsSettings.LinkedViewId` exist. Per-category *custom* control inside
the link is **not** exposed — so the user’s exact “Custom + model categories on in
link” recipe is approximated as below. **[verify on Windows]**: confirm the enum
member names (`ByHostView` / `ByLinkedView` / `Custom`) and that `SetLinkOverrides`
takes the **link type or instance** id (docs say the RevitLinkType id — test both).

Implementation (`ViewsByLinkRunHandler`):
1. Step content: `MultiSelectTabs` of loaded links (all pre-selected); view kind
   toggle (3D | Floor Plans per level | Both — default 3D only); optional view
   template; TokenInput naming with `{LinkName}` (+ `{Level}` when plans).
2. Per link, create the view (`View3D.CreateIsometric` / `ViewPlan.Create`), apply
   template first (before any geometry ops, per CLAUDE.md ordering).
3. Isolation, two layers:
   - **Hide the other links** (all years): `view.HideElements(otherLinkInstanceIds)`
     — guard each id with `element.CanBeHidden(view)`, skip-and-log.
   - **Hide host model content**: iterate the document’s categories
     (`doc.Settings.Categories`) and `view.SetCategoryHidden(catId, true)` for
     Model / Annotation / Analytical / Imported categories (guard
     `view.CanCategoryBeHidden`). That would also blank a `ByHostView` link — so set
     the **target link’s** override to *By Linked View* (pick the link doc’s default
     3D/plan view id for `LinkedViewId`) so it renders from its own view settings,
     immune to the host category hides. If `SetLinkOverrides` proves unavailable or
     rejects (older year quirk), fall back to hiding only the other link instances
     and leaving host categories visible, with a warn log — the view still shows
     “that link on, other links off”.
   - Also turn off Coordination Model display if the API year exposes it
     (`view.AreCoordinationModelHandlesVisible`-style — **[verify on Windows]**; if
     nothing is found, note it in the run log as not controlled).
4. `#if REVITxxxx` guards only if a real compile break appears (CLAUDE.md rule); the
   `SetLinkOverrides` surface already exists in 2024, so likely none are needed.
5. Memory discipline: never activate the created views; clear payload in `finally`.

---

# WS-9 — Bulk Export: Print Sets replace Packs (branch: `bulk-export-print-sets`)

Files: `Source/Tools/Export/BulkExportViewModel.cs`, `BulkExportEventHandler.cs`,
`BulkExportSettings.cs`, `Source/Commands/Export/BulkExportCommand.cs`,
`Strings/en/export.bulkExport.json`. `SheetPackLayout` / `SheetPackLayoutEditor`
become unused → delete (and remove `SavedPacks` from settings with a tolerant
deserialize so old XML files still load).

Revit API: saved print sets are `ViewSheetSet` elements —
`new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet))`, `set.Views`
(a `ViewSet` of views/sheets). Creating one:
`doc.PrintManager.PrintRange = PrintRange.Select; var vss =
doc.PrintManager.ViewSheetSetting; vss.CurrentViewSheetSet.Views = viewSet;
vss.SaveAs("name");` inside a transaction, on the Revit thread. Renaming/deleting:
`vss.Delete()` / assign `CurrentViewSheetSet`. **[verify on Windows]**: `SaveAs`
inside an ExternalEvent transaction works (it does in common practice, but confirm
no PrintManager modality issues).

Changes:
1. **Capture**: `BulkExportCommand` additionally captures
   `List<(string Name, List<ElementId> MemberIds)>` for every `ViewSheetSet`
   (main thread), passed to the VM.
2. **S1** gains a third mode button: `Sheets | Views | Print Sets`. In Print Sets
   mode the picker is replaced by a checkable list of print sets (name + member
   count). Selecting sets defines the selection (`_selectedNames` resolves through
   the sets’ member ids; members that are neither eligible sheets nor views are
   skipped-and-logged at run).
3. **S2 “Build Packs” → “Print Sets”** (visible in every mode):
   - A card per **selected/created** print set with: the set name (read-only for
     existing sets), a per-set **filename/pattern override** (TokenInput seeded from
     the global pattern; blank = use global), and per-set **format toggles**
     (PDF/DWG/NWC/IFC; default = the S3 global toggles). This delivers “naming and
     file type export by print set”.
   - “Save current selection as print set…” button (name box + save) — runs through
     a new lightweight ExternalEvent action on `BulkExportEventHandler`
     (`ActionCreatePrintSet`) since it needs a transaction; on completion the list
     refreshes. This delivers “If I can create them here that would be cool”.
   - In Sheets/Views mode, S2 is optional: with no set selected, the flat S1
     selection exports exactly as today.
4. **Run spec / handler**: replace the pack payload with
   `List<PrintSetSpec { Name, MemberIds, PatternOverride, Formats }>`. Combined-PDF
   grouping, per-format runs, and per-set subfolder/name behavior mirror what packs
   did (see `BulkExportEventHandler`’s current pack handling — port the grouping
   logic, keyed by set rather than pack). Non-set (“loose”) selection exports under
   the global pattern/formats as one group, as today.
5. Review step: list each set with its formats + resolved pattern.
6. “Everything else is good” — do not touch S3–S8 beyond what the spec plumbing
   requires.

---

# WS-10 — Global Settings window rework (branch: `settings-window-rework`)

Files: `Source/Framework/GlobalSettingsWindow.xaml.cs` (nav),
`GlobalSettingsWindow.ToolGroups.cs` (rebuilt), `GlobalSettingsWindow.Dimensions.cs`
(kept, re-homed under Dimensioning), `Strings/en/globalSettings.json`, plus the
settings singletons noted below.

## 10a. Tabs mirror the ribbon (current ribbon order)

Ribbon panels: **Setup → Copy from Link → Modify → Ceilings → Views → Filters &
Legends → Dimensioning → Sheets → Export** (+ Settings/Developer, not tabs).

New `_navDefs` (per Decision 5, groups with zero persistent settings are omitted):

| Tab id | Label | Contents |
|--------|-------|----------|
| `general` | General | unchanged (theme, UI size, language) |
| `setup` | Setup | **NEW** — Upgrade & Link Models section |
| `copy` | Copy from Link | Copy Linear Elements, Copy Elements from Link |
| `ceilings` | Ceilings | Ceiling Heatmap, Make Ceiling Grids |
| `views` | Views | Scope Box Creator (incl. new Per-Level End Extension from WS-6) |
| `dimensioning` | Dimensioning | current `BuildDimensionsContent()` (rename from “clash”) |
| `export` | Export | Bulk Export |

Omitted (no persistent defaults): Modify, Sheets, Filters & Legends (its tools own
their windows). If the user answers Decision 5 the other way, keep them with the
existing `AddGroupNote` pattern.

## 10b. Collapsible per-tool sections with explicit titles

- Add a small reusable expander builder in `GlobalSettingsWindow.ToolGroups.cs`
  (theme-token styled `Border` + clickable header row with a ▸/▾ caret `TextBlock` —
  do NOT use the WPF `Expander` default template; build it like the
  MultiSelectTabs hierarchy caret). Signature:
  `private FrameworkElement ToolSection(string title, bool startExpanded, Func<StackPanel> buildBody)`
  — body built lazily on first expand (cheap but tidy). Default **collapsed** when a
  tab has >1 section, expanded when it’s the only section.
- Section titles are the exact ribbon tool names + “Defaults”, e.g.
  “Upgrade & Link Models — Defaults”, “Ceiling Heatmap — Defaults”,
  “Make Ceiling Grids — Defaults”, “Scope Box Creator — Defaults”,
  “Copy Linear Elements — Defaults”, “Copy Elements from Link — Defaults”,
  “Bulk Export — Defaults”, “Auto Dimension — Defaults” (Dimensioning tab).
- Wrap every existing group body (heatmap colors, mg folder, scope box steppers,
  bulk export fields, copy sections, dimensions content) in these sections.

## 10c. New Setup tab — Upgrade & Link Models defaults

Expose `UpgradeLinksSettings`: Default placement (`SingleSelect` over the WS-1
four), Destination (`Local — selected folder` / `Local — current location` /
`Cloud`), Audit on open (toggle), Reload existing links (toggle). (No folder field —
see 10d.)

## 10d. Default file paths blank until user-set; tools stop writing them back

- Confirmed current defaults are already `""` (BulkExportSettings.OutputFolder,
  MakeCeilingGridsSettings.OutputFolder, UpgradeLinksSettings.LastSelectedFolder).
  Keep them blank.
- Per Decision 4: remove the write-backs where a **run** persists paths/patterns as
  defaults: `UpgradeLinksViewModel.SaveSettings()` (drop `LastSelectedFolder`,
  `Destination` persistence — keep nothing, or keep only toggles? drop path +
  destination, keep AuditOnOpen/ReloadExisting toggles which aren’t paths),
  `BulkExportViewModel` lines ~1215/~1434 (`s.Save()` calls that stamp the current
  run’s folder/pattern/format state back into settings — remove the path/pattern
  fields from that save; format toggles may stay if desired, but simplest and most
  predictable: the run saves nothing; only the Settings window writes).
  `UpgradeLinksViewModel` then seeds `_selectedFolder` from host folder → settings
  default → blank, unchanged order minus the auto-remember.
- Audit pass: grep every `*Settings.cs` for path-like fields and every
  `Instance.Save()` call site outside `GlobalSettingsWindow.*`; list them in the PR
  description with what was done (“run no longer persists X”).

## 10e. “Take another pass at all functions” — persistent-settings audit

For each tool, decide: has persisted defaults → section; else → nothing. Known
inventory (verify while implementing): UpgradeLinks ✔, ScopeBox ✔, CeilingHeatmap ✔,
MakeCeilingGrids ✔, BulkExport ✔, CopyLinear ✔, CopyFromLink ✔, ClashDimension ✔
(Dimensioning tab), AppSettings ✔ (General). Tools with own settings windows
(AutoFilters, Legend Creator, Clash Definitions) are **not** duplicated here.
PrintView, BulkRename, Split tools, ExtendWalls, AlignCoordinates, PushCoordinates,
CopyDatums, PlaceDependentViews, AlignSheetViews, Explode: no settings singletons →
nothing. If any hardcoded per-run default belongs as a persisted default (judgment
call), flag it in the PR rather than silently adding it.

---

# WS-11 — Tools Overview rework (branch: `overview-top-bar-rework`)

Files: `Source/Framework/ToolsOverviewWindow.xaml.cs`, `ToolsOverviewCatalog.cs`,
`Strings/en/overview.json`. (`ToolsOverviewDemos` keys must be re-pointed to the
new tool names where they changed — e.g. the merged Bulk Views.)

Do this **after** WS-2 and WS-8 so the catalog documents the final ribbon.

- **Remove the left category rail entirely** (`BuildBody`’s rail column, `_railRows`,
  `_railText`, `BuildRailRow`, `StyleRailRows`). The body becomes the full-width
  cards pane.
- **Top bar = the category selector.** Replace the six workflow-stage chips with one
  chip per ribbon panel, in ribbon order: Setup, Copy from Link, Modify, Ceilings,
  Views, Filters & Legends, Dimensioning, Sheets, Export. Clicking a chip shows that
  panel’s tool cards (reuse the existing chip styling/active-state code —
  `OverviewStage` can be repurposed 1:1 category↔chip, or delete the stage layer and
  drive chips directly from `Categories`; prefer deleting the stage layer for
  simplicity: `Stages`, `StageForCategory`, `IsStageActive`, stage badge on the
  header all go).
- **Catalog rebuild** (`ToolsOverviewCatalog.Categories`, new ids matching ribbon):
  - `setup`: Upgrade & Link Models, Align Coordinates, Push Coordinates to Links.
    (Link Audit and Compare Grids are ribbon-inactive → **remove their cards**.)
  - `copy`: Copy Datums, Copy Linear Elements, Copy Elements from Link. (Rename the
    current “Copy Grids” card to Copy Datums — it copies grids *and levels*.)
  - `modify`: Split by Level / Grid / Reference Plane / Cell, Extend Walls (as-is).
  - `ceilings`: Ceiling Heatmap (mention the new generate-RCPs mode from WS-5), Make
    Ceiling Grids, Project Grids, Reproject Grids (as-is, wording check).
  - `views`: Scope Box Creator, Scope Box Manager, **Bulk Views** (one card
    describing the five modes incl. per-link views), Explode View by Trade. (The
    separate Duplicate / By Template / Dependents cards collapse into Bulk Views.)
  - `filtersLegends`: Auto Filters, Legend Creation.
  - `dimensioning`: Clash Definitions, Clash Finder & Dimension, Clash Finder &
    Elevation, Refine Dimensions.
  - `sheets`: Place Dependent Views, Align Sheet Views, Bulk Rename.
  - `export`: Bulk Export (mention print sets per WS-9), Print View.
  - **Wording/accuracy pass on every blurb, example, and feeds/fed-by chip** against
    the tools’ current behavior after this whole pass (e.g. Bulk Export blurbs must
    say print sets, not packs; Scope Box Creator mentions level auto-assignment;
    Upgrade & Link placement options renamed; Feeds/FedBy chips must reference tool
    names that still exist so `TryResolveTarget` keeps them clickable).
  - All strings live in `overview.json` — restructure its keys to the new category
    ids; delete orphaned keys (`overview.cat.coordination.*`, stage keys) and verify
    the key-set diff per CLAUDE.md.
- Window chrome: `AllowsMaximize` stays; keep footer. Nothing else changes.

---

# Cross-cutting checklist (every workstream)

1. `/revit-navisworks-ui` skill before any WPF change.
2. New strings → JSONC files with comments; verify referenced keys exist (regex scan
   per CLAUDE.md Text Externalization).
3. Silent-failure scan on the diff; report findings + “No silent failures detected”
   when clean.
4. Session-long handlers: clear new payload fields in `finally`; new VM callbacks
   nulled in `OnWindowClosed`.
5. Subscribe `SelectionChanged` **before** `SetGroups`/`SetTree`; `SingleSelect=true`
   before `SetGroups` where used.
6. No `Popup StaysOpen=false`, no unfrozen shared Freezables, aliases for
   WPF/Revit-ambiguous types, `internal` across partials.
7. Uniqueness throws: pre-check names for scope boxes, views, print sets; wrap the
   actual set in try/catch anyway.
8. Anything marked **[verify on Windows]** gets a log line or comment so the first
   Windows run confirms it; do not assume.

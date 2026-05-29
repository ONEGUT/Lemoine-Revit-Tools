# Plan — Codebase Health Quick Wins + Plan-File Tidy

Scope chosen by user: **quick wins** + **tidy plan-\*.md files**. Silent-failure
cleanup (105 empty catches) is explicitly deferred to a later pass.

No behavioural/runtime logic changes. This is housekeeping + metadata only.

## Branch
Proposed: stay on the already-checked-out working branch
`claude/codebase-health-review-D8NAN` (the designated branch for this task).
One logical change set: codebase-health housekeeping.

---

## 1. Real add-in GUID
**File:** `LemoineTools.addin`
- Replace placeholder `AddInId` `A1B2C3D4-E5F6-7890-ABCD-EF1234567890`
  with a real GUID: `58F27C6A-BDD7-4FAD-90F6-DAC30F0A4638`.
- Remove the now-obsolete "Replace with a unique GUID" comment.
- **Why:** two installs sharing the placeholder ID collide in Revit's add-in registry.

## 2. Fix stale csproj folder-map comment
**File:** `LemoineTools.csproj` (comment block ~lines 22-50)
- Update documented folder names to the real `T01-/T02-/T03-/T04-` prefixed names.
- Add the undocumented `Source\Lemoine\Controls\` tree (Color/Input/Layout/Legend).
- Add `Source\Lemoine\Templates\`.
- Clarify: **folders are number-prefixed, namespaces are not**
  (e.g. folder `Tools\T01-AutoFilters\` → namespace `LemoineTools.Tools.AutoFilters`).
- Comment-only change; no compile impact.

## 3. Remove stale compile reference
**File:** `LemoinePreview/LemoinePreview.csproj` (line ~86)
- Remove the `<Compile Include="...SheetPackSettings.cs" />` entry — that file does
  not exist anywhere in the repo. (Rest of SheetPack is live, used by BatchExport.)

## 4. Normalise the odd-one-out namespace
`LemoineTagChipInput` is the only type under `LemoineTools.Lemoine.Controls.Input`;
its 19 sibling controls use `LemoineTools.Lemoine.Controls`.
- `Source/Lemoine/Controls/Input/LemoineTagChipInput.xaml` — `x:Class` drop `.Input`.
- `Source/Lemoine/Controls/Input/LemoineTagChipInput.xaml.cs` — `namespace` drop `.Input`.
- Delete the now-redundant `using LemoineTools.Lemoine.Controls.Input;` line in the
  3 consumers (each already imports `...Controls`):
  - `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs:15`
  - `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs:16`
  - `LemoinePreview/PreviewMainWindow.cs:13`

## 5. Tidy plan-\*.md files
- `git mv` the 16 root `plan-*.md` files into `docs/plans/`.
- **Includes this file** once approved (kept in root during review, moved at the end).
- Keeps repo root clean; preserves history.

---

## Out of scope (deferred, next pass)
- 105 empty `catch {}` blocks + 8 catch-and-log-only blocks.
- Duplicated nested data classes (`DocEntry`, `LinkEntry`, `ViewTemplateEntry`,
  `LegendGroupConfig`).
- Settings-persistence boilerplate consolidation.
- Moving `BrushHelper.cs` to `Source/Helpers/`.

## Post-change
- Run the mandatory silent-failure scan on the diff (expect: none — no logic touched).
- Commit with a clear message; push to `claude/codebase-health-review-D8NAN`.
- No PR unless requested.

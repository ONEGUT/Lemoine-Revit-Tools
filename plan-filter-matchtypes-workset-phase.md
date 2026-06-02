# Plan — Fix Auto Filter creation + add Workset/Phase filtering

## Problem 1 — "Auto Filter doesn't create any filters" (bug)

The rule-editor match-type dropdown and the filter engine have drifted apart.

- **UI** (`GlobalSettingsWindow.Filters.cs:1184`) offers 8 match types:
  `contains`, `does not contain`, `equals`, `does not equal`,
  `begins with`, `ends with`, `has a value`, `has no value`.
- **Engine** (`AutoFiltersEventHandler.cs`) handles only 3:
  `contains`, `equals`, and legacy `all`.

Effects:
- `has a value` / `has no value` rules (the replacement for the old `all`,
  which carry no keywords) are discarded by the run guard
  (`r.MatchType == "all" || r.Match.Count > 0`) → run aborts with
  *"No enabled rules…"* and **creates nothing**.
- `does not contain`, `does not equal`, `begins with`, `ends with`
  silently degrade to `contains`.
- `Discover` still writes `MatchType="all"`, not in the dropdown.

## Problem 2 — Filter by Workset and Phase (feature)

`Workset`, `Phase Created`, `Phase Demolished` are already in
`KnownParameters` but (a) hidden by per-category curated lists and
(b) unresolvable / unmatchable by the engine because their values are an
integer workset id and a phase `ElementId`, not text.

## Changes

### `Source/Tools/T01-AutoFilters/AutoFiltersEventHandler.cs`
1. **Unify match types.** Replace the 3-way `BuildRuleForKeyword` switch with
   a full map covering all 8 UI strings + `all` (alias of `has a value`):
   - `contains` → `CreateContainsRule`
   - `does not contain` → `CreateNotContainsRule`
   - `equals` → `CreateEqualsRule`
   - `does not equal` → `CreateNotEqualsRule`
   - `begins with` → `CreateBeginsWithRule`
   - `ends with` → `CreateEndsWithRule`
   - `has a value` / `all` → `CreateHasValueParameterRule` (no keyword)
   - `has no value` → `CreateHasNoValueParameterRule` (no keyword)
2. **Fix the "valueless" guards.** Introduce `IsValuelessMatch(matchType)`
   (`has a value`/`has no value`/`all`) and use it in the `totalRules` count
   (line 129), the orphan-skip (line 174), the `rulesDone` increments
   (281/290) and the skip guard (299) instead of the `== "all"` checks.
3. **Workset/Phase resolution.**
   - Add to `bipMap`: `Workset → ELEM_PARTITION_PARAM`,
     `Phase Created → PHASE_CREATED`, `Phase Demolished → PHASE_DEMOLISHED`.
   - Build a `worksetMap` (name → workset id int) and `phaseMap`
     (name → phase `ElementId`) once per run, pass into `ProcessRule`.
   - In `BuildRuleForKeyword`, special-case Workset (equals by int id) and
     Phase (equals by `ElementId`) like the existing `Structural Material`
     path. These params support only equals/has-value semantics.

### `Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs`
4. In `GetParametersFor`, always append the universal element params
   `Workset`, `Phase Created`, `Phase Demolished` to whatever curated list is
   returned (deduped), so every category exposes them in the dropdown.
5. Update the `MatchType` doc comment to list the 8 supported values.

### `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`
6. No structural change required (the dropdown already lists the 8 types and
   the chip reads `GetParametersFor`). Verify Workset/Phase now appear.

### Live workset/phase dropdown (user-approved)
7. `OpenFiltersSettingsCommand.cs` — on the Revit main thread (where the doc
   is available) collect user workset names and phase names and push them into
   the window via a new `SetWorksetPhaseLists(...)`, mirroring the existing
   `SetPatternLists` plumbing.
8. `FiltersSettingsWindow.xaml.cs` — add `WorksetNames` / `PhaseNames`
   properties + `SetWorksetPhaseLists`.
9. `GlobalSettingsWindow.Filters.cs` — when the rule's parameter is
   `Workset` / `Phase Created` / `Phase Demolished`, the value chip's
   `ItemsSource` becomes the live workset/phase list (refreshed when the
   parameter changes). Free-text stays enabled as a fallback (the in-tool gear
   entry point has no live doc, so its list is empty → free text).

## Silent-failure scan
Run after edits per CLAUDE.md and report findings.

## Build
Windows-only; cannot compile on Linux. Code-review the diff carefully.

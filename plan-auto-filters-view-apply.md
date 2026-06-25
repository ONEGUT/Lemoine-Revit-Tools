# Plan — Auto Filters: apply-to-view, FG/BG editing, live swatch, merge fix

Branch: `claude/auto-filters-view-apply-i2pq1u` (already designated)

## Requests
1. **Apply filters to current view** button on the Auto Filters screen — applies
   only the **active trade** to the active view.
2. **Foreground AND background** of the surface (and cut) fill must be editable in
   the rule editor — today only one colour (the foreground) is editable.
3. The **rule-row colour swatch** must come from the surface **foreground** colour
   and **update live** when the colour is changed in the editor (today it only
   refreshes on a full list rebuild).
4. **Filter-creation bug** — "from merged filters only one of the text values was
   applied."

## Findings on #4 (merge bug)
- `BuildElementFilter` correctly OR-combines multiple keywords
  (`LogicalOrFilter` of one `ElementParameterFilter` per keyword). The UI Merge
  feature correctly unions keywords into the rule model. Both are correct.
- **Root cause:** the *apply* paths reuse an existing `ParameterFilterElement`
  **without refreshing its definition**:
  - `ApplyFiltersToViewsEventHandler` only attaches + overrides; never rewrites
    the rule.
  - `AutoFiltersEventHandler` only rewrites the definition when
    `OverwriteFilterDefinition` is true (or, in CreateOnly mode, when the name is
    in `ChangedFilterNames`).
  So a filter created **before** a merge keeps its pre-merge single-keyword
  definition when re-applied → only one keyword takes effect.
- Fix: the new "Apply to current view" action rebuilds the active trade's filter
  definitions (OverwriteFilterDefinition = true) as part of applying, so the
  on-disk filter always matches the current (merged) rule.

## Changes

### Backend — `Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs`
- Add background fields to `FilterRuleConfig`:
  `SurfBgColor`, `SurfBgPattern`, `OverrideSurfBg`,
  `CutBgColor`, `CutBgPattern`, `OverrideCutBg` (XML attributes, sensible
  defaults, off by default so existing files are unchanged).
- Keep existing `SurfColor`/`SurfPattern`/`OverrideSurf` as the **foreground**.

### Backend — `Source/Tools/T01-AutoFilters/AutoFiltersEventHandler.cs`
- `ApplyRuleOverride`: when `OverrideSurfBg`/`OverrideCutBg` set, also call
  `SetSurfaceBackgroundPatternId/Color/Visible` (and cut equivalents) per the
  CLAUDE.md foreground/background note.

### UI — `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`
- `BuildOverrideStyleSection`: add background rows for Surface and Cut (layout per
  the chosen option below).
- Surface **foreground** colour setter also updates the active rule-row swatch
  live: store `_fActiveColorDot` in `BuildRuleListRow` (when active) and repaint
  it from the foreground hex on change.
- Rule-row `colorDot` already reads `rule.SurfColor` (= surface foreground) — keep.

### UI — `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs`
- Add an **"Apply to view"** button to the toolbar right panel. On click:
  persist the current buffer to `AutoFiltersSettings.Instance.Trades` (+ refresh
  the dirty snapshot so close doesn't double-run), then raise
  `AutoFiltersEventHandler` with `CreateOnly=false`,
  `OverwriteFilterDefinition=true`,
  `SelectedDisciplines = [activeTrade.Label]`. Flash status with the result.

## Verification
- Cannot build on Linux (net48 + WPF). Static review + Revit API existence check
  against `libs/`. Post-change silent-failure scan per CLAUDE.md.

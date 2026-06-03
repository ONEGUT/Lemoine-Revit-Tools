# Plan — Auto Filters creation fixes + "Whole category" select option

Branch: `claude/auto-filter-issues-Q3icS` (already designated/approved)

## Problem
The auto-filter engine fails to create filters correctly in the common
"geometry lives in linked models" workflow, and the rule editor offers no clear
way to select an entire category.

## Backend — `Source/Tools/T01-AutoFilters/AutoFiltersEventHandler.cs`
1. **Resolve parameters across links, not just host.** Thread `sourceDocs`
   (host + selected links) into `ProcessRule` → `ResolveParamId`; scan every
   source doc for a live element when resolving a non-BIP parameter. *(root cause)*
2. **Expand `bipMap`** with reliably-resolvable BuiltInParameters: System Name,
   Mark, Comments; add category-aware fallback for "System Type"
   (duct/pipe BIP) so it resolves with no live elements.
3. **Implement all match types** in `BuildElementFilter`/`BuildRuleForKeyword`:
   `does not contain`, `equals`, `does not equal`, `begins with`, `ends with`,
   `has a value`, `has no value` (currently everything except equals/all silently
   becomes "contains"). Multi-keyword: positive → OR, negative → AND.
4. **True "whole category" for `MatchType == "all"`**: match on
   `ELEM_CATEGORY_PARAM` HasValue (every element has a category) — no parameter
   resolution, no host-element dependency.
5. **Move the enabled/keyword gate to the top** of `ProcessRule` so disabled
   rules can't be reported as failures.
6. **Honor `FilterOn`**: `view.SetIsFilterEnabled(filterId, rule.FilterOn)`.
7. **Persist only filters that actually exist** in `CreatedFilterNames`.

## Backend — `Source/Tools/T01-AutoFilters/DiscoverEventHandler.cs`
8. Commit discovered per-value rules as `contains` (not `equals`) so values that
   only surface via `AsValueString` still match.

## Backend — `Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs`
9. Fix EL default Cable Tray / Conduit rules (`Parameter="Service Type"` never
   resolves) → `MatchType="all"` (whole category) so they create + color reliably.

## UI — `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`
10. Add a prominent **"Whole category"** toggle at the top of the FILTER LOGIC
    card. ON → `MatchType="all"`, disables the PARAMETER + SEARCH STRING rows and
    shows a hint ("Every element in the selected categories"). OFF → restores the
    keyword match type. Rule-list subtext shows "· whole category" when on.
    Add an `all`-aware path so the match dropdown only governs keyword modes.

## Verification
- Cannot build on Linux (net48 + WPF). Static review + API existence confirmed
  against `libs/RevitAPI.dll` (all methods/params present).
- Post-change silent-failure scan per CLAUDE.md.

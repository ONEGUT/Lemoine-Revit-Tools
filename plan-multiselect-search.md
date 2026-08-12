# Plan — Built-in search bar for `MultiSelectTabs`

## Goal

Add a search bar built into `MultiSelectTabs` itself — a full-width row inside the control's
own border, above the tab column and the checklist. Every existing caller (~30 call sites)
gets it with **zero call-site changes**.

Approved design (mockup-confirmed, Option B): **cross-tab "Results" tab**.

## Behaviour

- Typing auto-opens a pinned **Results** tab (index 0, above `Selected`) listing every match
  from every group, each row tagged at the right with its group name. Clearing the box returns
  to the tab the user was on before searching.
- Tab badges switch from `selected/total` to **match counts** while searching; zero-match tabs
  are dimmed but stay listed (never a silently shrinking tab list).
- Clicking a group tab mid-search filters that group to its own matches; the `All` row is
  scoped to the **visible matches** ("All 4 matches in Mechanical").
- The matched substring is highlighted in each row label (bold + `LemoineAccentDim`).
- Live match count + a `Clear` button appear beside the box only while a query is active.
- Empty state: *No items match "xyz".*
- The `Selected` tab stays **unfiltered** — it is the review list for the current picks, and its
  badge stays the total selected count.

## Contract preservation

- `SelectionChanged` still fires once at the end of `SetGroups` — search adds no extra fire.
- `SingleSelect` — Results tab hides its `All` row, checking still clears prior selection.
- `DisabledItems` — listed dimmed and non-interactive in Results and in filtered group lists;
  excluded from every `All` toggle's math. Now honoured on the searching path in `Hierarchy`
  mode too (searching flattens, so the flat path's disabled handling applies).
- `Hierarchy` — nesting is preserved when not searching. While searching the group list
  **flattens** to matching rows, so a match is never hidden behind a collapsed caret.

## Files changed

| File | Change |
|---|---|
| `Source/Framework/Controls/Input/MultiSelectTabs.xaml` | Wrap existing 3-column body in row 1 of a 2-row Grid; add row 0 search row (`TextBox` + hint overlay + actions slot); name `_outer` / `_tabColumn` / `_body` so the constructor stops theming by child index; `MinHeight` 120 → 150 to cover the added row |
| `Source/Framework/Controls/Input/MultiSelectTabs.xaml.cs` | Search state + matching helpers; pinned Results tab; `_tabByKey` map replacing index arithmetic in `RefreshAllCounters`; match-count/dim badges; flattened filtered group lists; generalized `All` row builder; optional group label + query highlight on `BuildCheckItem` |
| `Strings/en/controls.pickers.json` | 8 new `multiSelectTabs.*` keys (hint, results tab, counts, all-matches rows, no-matches, clear) |
| `CLAUDE.md` | Extend the *MultiSelectTabs Contract* section with the search behaviour |

## Not doing

- No new public API is *required* of callers. (`SetGroups` signature unchanged.)
- No per-caller opt-out switch — the search bar is unconditional, matching "built-in right on
  top of it". Add a `ShowSearch` flag later only if a caller actually needs it hidden.

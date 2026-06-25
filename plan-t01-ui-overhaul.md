# Plan — T01 Auto Filters UI & Functionality Overhaul

Organizes the requested changes into review-gated stages. **Every stage that touches the UI
gets a rendered mockup for approval before any code is written for that stage.**

### Locked decisions (from user)
- **Eye toggle:** merge `Enabled` into a single **"Apply"** concept; remove the per-row eye.
- **Close-time apply scope:** recolor only views/templates that **already carry** the filter.
- **Discover button:** left button **launches the existing** Discover window.
- **Base branch:** branch **from main** as `claude/busy-archimedes-yonid6`.

Primary files:
- `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs` (the window body — ~3450 lines)
- `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs` (toolbar, close commit, apply-to-view)
- `Source/Tools/T01-AutoFilters/AutoFiltersEventHandler.cs` (filter create/override engine)
- `Source/Tools/T01-AutoFilters/DeleteFiltersEventHandler.cs` / `DeleteFiltersFromProject*` (embed targets)
- `Source/Tools/T01-AutoFilters/Discover*` (embed target)
- `Source/App.cs` (ribbon registration — buttons to remove/repoint)

---

## Answers to the three questions asked

### 1. Can close-time updates also override filters set inside a View Template?
**Yes — but only by writing to the template, not the view.** A View Template is itself a `View`
element. When a view has a template assigned and that template *controls* "V/G Overrides Filters",
any `view.SetFilterOverrides(...)` on the view is ignored — the template governs it. To change those
filters' colors you must call `SetFilterOverrides` / `SetFilterVisibility` **on the template element**,
which then propagates to *every* view using that template. Detection: a view's template id is
`view.ViewTemplateId`; whether the template controls filters is read from
`template.GetNonControlledTemplateParameterIds()` (filters are controlled unless that category id is
in the non-controlled set). So: yes, possible and powerful — but editing a template is a project-wide
change, which is exactly why the scope needs your confirmation (see Stage 5 + Consequences).

### 2. Is an undo/redo for changes made while the Filters window is open possible?
**Yes — for the in-window edits (the practical interpretation).** The window edits an in-memory
working buffer (`_filterTrades`) and only commits to Revit on close. A snapshot-based undo/redo stack
over that buffer is straightforward: every mutating action pushes a serialized snapshot; Undo/Redo
restores one. A caret dropdown can list the labelled changes ("Recolor MD-Pipe", "Add rule", "Move 2
rules to HVAC"). **Caveat:** this undoes *menu* edits only. The actual Revit document filter/override
changes happen after the window closes and live on Revit's own native Undo stack (Ctrl+Z in Revit) —
the window cannot drive those. So "undo a color change" works while the window is open; once closed,
it's Revit's undo. Confirm that scope is acceptable.

### 3. Unintended consequences — see the dedicated section at the bottom. Several are blocking.

---

## Stage 1 — Relocate & rename the "Apply to view" actions  *(DONE)*
- Removed the toolbar "Apply to view" button (top-right).
- **"Apply trade to view"** docked at the bottom of the center rule-list column (applies the active trade).
- **"Apply selected trades to view"** docked at the bottom of the Trades sidebar.
- **Per-trade quick selection:** each sidebar trade row has a checkbox (tracked as an exclusion set, so
  trades default to checked). The footer applies the checked subset, not always all.
- **Externally-managed trades are NOT skipped** — when selected, the handler attaches their existing
  filters and re-applies the rule's stored overrides without regenerating their definitions
  (`AutoFiltersEventHandler.IncludeSelectedExternallyManaged` + `ApplyExistingFilterToView`).

## Stage 2 — Rule row & right-editor cleanup  *(DONE)*
- Removed the eye toggle AND the inline trash from each rule row — rows now carry only the **✎ edit**
  action (`BuildRuleToggle`/`BuildMoveCopyButton`/`BuildRuleIdentitySection` deleted).
- **Enabled merged into "Apply":** the single Appearance "Apply" toggle drives `rule.Enabled`
  (key `appearance.apply`); the handler's `SetIsFilterEnabled` now uses `Enabled`. Disabled rows dim.
- **Right editor:** NAME card removed — the editor starts at FILTER LOGIC.
- **Rule edit popup** (`ShowRuleEditPopup`, opens from ✎): Rule Name + **[Move | Copy] double-button**
  (`BuildRuleMoveCopyDouble` → `BuildTradeDestPopup` with `initialCopy`/`onDone`) + Delete. No Save.
- **Trade edit popup:** Save button removed — name/ID auto-commit on change; sidebar refresh deferred
  to popup close so renaming doesn't detach the anchor.

## Stage 3 — Discover as a split button next to "Add Trade"  *(UI — mockup first)*
- Replace the sidebar "＋ Add Trade" pill with a **double button**: left = **Discover**, right =
  **Add Trade**. Left opens the Discover flow; right is the current direct add.
- Simplest viable: left button launches the existing `DiscoverLaunchCommand` StepFlow window (it already
  captures categories on the Revit main thread). Fully-inline Discover is a much larger rework — propose
  the launch-existing-window approach unless you want inline.

## Stage 4 — Embed "Remove from view" + "Delete from project"  *(UI — mockup first)*
- **Remove from view:** one instant button (toolbar or sidebar footer). No selection — removes **all
  Auto-Filters-managed filters** (`AutoFiltersSettings.Instance.CreatedFilterNames`) from the active
  view via the existing `DeleteFiltersEventHandler`.
- **Delete from project:** a button that opens the **existing** `DeleteFiltersFromProject` window
  (no change to that window).
- Remove the now-duplicated ribbon split-button entries in `App.cs` (Apply to Views / Remove / Delete),
  or keep them — your call.

## Stage 5 — Close-time full apply of all changed rules + colors  *(backend — no mockup)*
- On close, in addition to definition refresh, **re-apply graphic overrides (colors, line, halftone,
  transparency, visibility) for changed rules** across the project.
- **Scope to confirm:** (a) only views/templates that *already carry* the filter, or (b) all views?
  Recommend (a) — re-color where the filter already lives, never blanket-attach. Includes
  template-controlled filters by writing to the template (Question 1).

## Stage 6 — Undo/redo for in-window edits  *(UI — mockup first)*
- Snapshot stack over `_filterTrades`; Undo/Redo buttons in the toolbar with a caret dropdown listing
  labelled changes. Scope = menu edits only (Question 2).

---

## Unintended consequences to resolve BEFORE coding

1. **The eye = `rule.Enabled`, NOT "Visible".** Three distinct flags exist: `Enabled` (rule produces a
   filter at all — the eye), `Visible` (elements shown/hidden), `FilterOn` (filter active in view).
   Removing the eye removes the only per-row enable/disable. Decide: move "Enabled" into the rule edit
   popup, or drop the concept entirely (every rule always produces a filter).
2. **"Apply all trades" + externally-managed trades.** Ceiling Heatmap registers an `ExternallyManaged`
   trade; the current apply path refuses these. "Apply all" must skip them (and say so), not error.
3. **Close-time color apply scope & cost.** Applying overrides across many views calls `doc.Regenerate`
   territory and can **overwrite manual per-view overrides** a user made outside the menu. Scope (a)
   above limits blast radius; still confirm.
4. **View-template edits are project-wide.** Recoloring a template changes every view using it — intended
   per your question, but worth a one-line confirmation in the run log.
5. **Removing the right-editor NAME box** means renaming moves entirely into the popup — confirm there's
   no other entry point lost.
6. **Removing ribbon buttons** (Stage 4) changes muscle memory for existing users; confirm removal vs keep.
7. **Undo/redo cannot reach post-close Revit changes** (Consequence of Question 2).

---

## Branch workflow
Per CLAUDE.md: confirm the base branch before any code. Designated dev branch for this task:
`claude/busy-archimedes-yonid6`. One stage per commit; mockup-approval gate on every UI stage.

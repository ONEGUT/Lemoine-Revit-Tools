# Plan — Project-Wide UI Consistency + Motion System

Goal: make every Lemoine tool feel like one cohesive, animated product. Enforce
consistency by centralising into the shared library (so we fix primitives once
instead of 23 tools), and add a unified motion layer for hover, press, step
transitions, list changes, and drag.

This document is the master vision. Execution is **phased** — each phase is one
logical branch (per CLAUDE.md "one logical change per branch").

---

## What the audits found (source of this plan)

1. **Last step not review-only** — 5 tools (CoordSet, ClashDimension, BatchExport,
   ApplyFiltersToViews, Discover) have inputs in the final step; 22 tools hand-roll
   a 2×2 card grid instead of the shared `LemoineReviewSummary`. Only DebugTool (now
   removed) used the real control.
2. **Input controls diverge** — booleans split CheckBox vs ToggleSwitches; numerics
   raw TextBox vs NumberStepper/Range; single-choice SingleSelect vs custom buttons/
   ComboBox; free text raw TextBox vs InlineEdit; **no `LemoineFolderBrowser`** (3 tools
   hand-roll textbox+dialog).
3. **Scrolling** — `WireBubblingScroll()` missing on LegendPalette/LegendBuilder.
4. **Dragging** — threshold inconsistent (SystemParameters vs hardcoded 6px), filter
   rows drag with no arming, color-picker sliders set no cursor.
5. **Hover/highlight** — log-tab `MouseLeave` bug (never resets), chips/swatches have
   no hover, "Add Trade" inverted hover, settings rows hover == selected.
6. **Dropdown/text** — autocomplete guard (`IsKeyboardFocusWithin`) missing on all 3
   autocompletes; font/size drift (Mono vs Ui, SM vs MD); placeholders inconsistent;
   `LemoineSingleSelect` inherits editable styling; TagChipInput list items unthemed.

---

## Library changes (ADD / MODIFY / REMOVE)

### ADD
- **`LemoineMotion`** (static helper, Revit-free): `WireHover(el, …)`, `WirePress(el)`,
  `FadeIn/FadeOut`, `AnimateHeight`, `WireDragArm(el, onDrag)`. Theme-aware hover via a
  fading overlay (keeps base brush bound to the theme so live theme-switch still works).
- **`LemoineFolderBrowser`** — folder twin of `LemoineFileBrowser`; replaces the 3
  hand-rolled `TextBox + FolderBrowserDialog` blocks.
- **`LemoineTextField`** — single-line themed text input with a real watermark
  (reuse the DatePicker `PART_Watermark` pattern); replaces ~dozen raw `WpfTextBox`.
- **Declarative review** — extend the tool contract so the framework builds the review:
  optional `ILemoineReviewable { (string id,string label)[] ReviewItems; IDictionary
  values; string[] chips; string? Note; string? Warning; }`. `StepFlowWindow` renders a
  `LemoineReviewSummary` on the last step automatically → the last step is **always**
  review + run + log, by construction, forever.
- **Motion tokens** — add `AnimPress` to `LemoineSettings` and a `LemoineHover` /
  `LemoineOverlayHover` brush token to `LemoineTheme`.

### MODIFY
- **`LemoineControlStyles`** — animate `FlatButtonTemplate` (hover + 0.97 press scale);
  add a **read-only ComboBox style** so `LemoineSingleSelect` stops inheriting the
  global `IsEditable=true`; add watermark support to the TextBox template; theme
  `ListBoxItem` (fixes TagChipInput); bake the `IsKeyboardFocusWithin` guard into the
  shared autocomplete path.
- **`StepFlowWindow`** — fix log-tab `MouseLeave` (reset to `LemoineTextDim`); add
  fade/slide on step content change; render the declarative review on the last step.
- **`LemoineReviewSummary`** — add optional `Note` and `Warning` slots so migrating
  tools keep their italic descriptions / warn banners.
- **`LemoineSearchAutocomplete` / `LemoineTagChipInput`** — guard + unify font
  (`LemoineUiFont`/`LemoineFS_MD`) + placeholder via `LemoineTextDim`.
- **Legend drag** (`LemoineLegendBlockRow`/`GroupCard`/`Palette`) — one drag-arm helper
  (SystemParameters threshold) + consistent cursors + ghost.
- **`WireBubblingScroll`** — apply to LegendPalette/LegendBuilder scrollers.

### REMOVE / DEPRECATE
- Per-tool `BuildReview()` / `CardDef` 2×2 card grids (×22) → deleted for the review spec.
- Raw `WpfComboBox` autocomplete in `FiltersSettingsWindow` → `LemoineSearchAutocomplete`.
- Raw `CheckBox` booleans → `LemoineToggleSwitches`; raw numeric `TextBox` →
  `LemoineNumberStepper`/`Range`; raw text `TextBox` → `LemoineTextField`.
- Ad-hoc per-element MouseEnter/Leave hover handlers → `LemoineMotion.WireHover`.

---

## Canonical control map (the "one control per data type" rule)

| Data type | Canonical control |
|---|---|
| Multi-select (levels/views/docs/filters/categories) | `LemoineMultiSelectTabs` |
| Single choice / mode | `LemoineSingleSelect` (read-only) |
| Boolean | `LemoineToggleSwitches` |
| Numeric | `LemoineNumberStepper` (single) / `LemoineNumberRange` (min-max) |
| File path | `LemoineFileBrowser` |
| Folder path | `LemoineFolderBrowser` *(new)* |
| Free text | `LemoineTextField` *(new)* / `LemoineInlineEdit` (in-place) |
| Pattern w/ tokens | `LemoineTokenInput` |
| Search | `LemoineSearchAutocomplete` |
| Color | `LemoineColorPicker` swatch |
| Review | framework-rendered `LemoineReviewSummary` |

---

## Motion system (the "movement")

All driven by existing tokens (`AnimFast/Med/Expand/Progress`) + new `AnimPress`:
- **Hover**: fading overlay (Opacity 0→1, `AnimFast`) on every interactive element —
  buttons, chips, rows, tabs, swatches. One helper, applied everywhere.
- **Press**: 0.97 `ScaleTransform` on pointer-down (`AnimPress`).
- **Step transitions**: content fade + slight Y-slide on activate (extends current
  `AnimateContent` height animation).
- **List add/remove**: fade + height collapse (chips, legend rows, settings rows).
- **Drag**: standardized ghost + insertion-line for legend reorder.
- **Completion**: progress fills (exists) + a check "pop" on the Close ✓ button.

Intensity is a tunable: a single `MotionLevel` setting could scale all durations /
disable on low-end machines.

---

## Phased execution (each = one branch)

- **P0 `fix-ui-interaction-bugs`** (low risk): log-tab hover, autocomplete guard,
  LegendPalette/Builder scroll bubbling, drag cursors. *Pure fixes, no contract change.*
- **P1 `add-motion-system`**: `LemoineMotion` + hover token + animated hover/press on
  buttons/chips/rows/tabs/swatches + step fade-slide. *Visible "movement" win.*
- **P2 `unify-input-controls`**: add `LemoineFolderBrowser` + `LemoineTextField`;
  read-only SingleSelect; dropdown font/placeholder unification; themed list items.
- **P3 `standardize-review-step`**: declarative review contract + StepFlowWindow
  auto-review + migrate all tools' last steps + move input bleed (CoordSet,
  ClashDimension, BatchExport, ApplyFiltersToViews) into their own pre-review steps.
  *Largest — may split per tool group (T01/T02/T03/T04/Testing).*
- **P4 `migrate-tool-inputs`**: replace raw CheckBox/TextBox/ComboBox usages with the
  canonical controls across tools. *May split per tool group.*
- **P5 `standardize-drag-interactions`**: unify legend drag threshold/arming/ghost.

Each branch ends with the CLAUDE.md silent-failure scan before commit. Cannot build on
Linux (per CLAUDE.md) — verification is Windows-only; I'll keep each phase compile-safe
and self-contained.

---

## Decisions (locked)
1. **Motion intensity** — **Rich & snappy** (fast 150–280ms, responsive not flashy).
2. **Review architecture** — **Declarative contract** (framework auto-builds the review;
   guarantees every tool's last step is review-only forever).
3. **Sequencing** — **Phased**, landed as sequential self-contained commits on
   `claude/happy-dijkstra-OzHjz` (the branch this session may push to).
4. **Base** — `claude/happy-dijkstra-OzHjz`.

## Status
- [x] **P0** interaction bug fixes — log-tab hover, autocomplete guards, scroll bubbling
- [x] **P1** motion foundation — `LemoineMotion` (WireHover/WirePress/FadeSlideIn) +
      `AnimPress` token + button press-scale + step fade-slide + animated dropdown hover
- [x] **P1.5** hover sweep — WireSwatchHover (color tiles: accent ring + shadow lift),
      WireHover(Panel)/WireToggleHover/WireTextHover; tabs, rows, pills, chips, text links
- [x] **P2** inputs — `LemoineFolderBrowser` + `LemoineTextField`; read-only `SingleSelect`
      (BuildReadOnlyComboBoxStyle); `BuildListBoxItemStyle`; TagChipInput popup StaysOpen
      crash fix + themed list items + FS_XS→FS_SM
- [x] **P3** review — `ILemoineReviewable` + `StepFlowWindow` auto-render +
      `LemoineReviewSummary` note/warning slots; migrated 22/23 tools (Discover left as-is,
      already review-only). Input-bleed tools (ApplyFiltersToViews, BatchExport,
      ClashDimension) split into an inputs step + a dedicated review step; CoordSet gained
      a review step. Per-tool hand-rolled card grids deleted.
- [ ] P4 migrate inputs · [ ] P5 drag · [ ] **P6 spacing**

### P3 cleanup carried forward
Unused private helpers left by the review-grid deletions (`AddCard` / `AddReviewCard` /
`BuildInfoPanel` and the `CardDef` structs) are dead but compile-safe (no
warnings-as-errors). Sweep in a follow-up. Discover can adopt `ILemoineReviewable` later to
drop its manual `UpdateS5Summary` wiring.

### P6 — Spacing tokens (from the spacing audit)
Worst offenders: 38 label→control gaps use `(0,0,0,2)` instead of the `(0,0,0,8)`
`LemoineTh_SubLabelMar` token; card padding has 5 vertical values (4–8); row gaps
split 4 ways (4/6/8/10). Missing tokens to add: `LemoineTh_CardPadCompact (8,4,8,4)`,
`LemoineTh_LegendHeaderPad (6,4,6,4)`, `LemoineTh_RowGapMed (0,0,0,6)`,
`LemoineTh_RowGapLg (0,0,0,10)`, `LemoineTh_DividerMar (0,8,0,8)`. Then migrate raw
`Thickness` literals to the tokens. (Pairs naturally with P4 since both touch every tool.)

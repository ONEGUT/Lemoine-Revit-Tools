# Plan — P4: Migrate Raw Inputs to Canonical Lemoine Controls

Goal: replace hand-rolled `CheckBox` / `ComboBox` / `TextBox` usages in the tools and
settings windows with the canonical Lemoine controls, so every input shares one look,
behavior, and theming — and, as a bonus, the remaining raw non-editable combos become
`LemoineSingleSelect`, clearing the last of the scroll-wheel-eating dropdowns.

Base branch: `claude/happy-dijkstra-OzHjz` (continues the phased work).

---

## Inventory (from the P4 audit)

~50 raw controls across 16 files. T04-ModifyElements is already fully migrated.

| Raw control | Count | Canonical target |
|---|---|---|
| Option `CheckBox` (standalone boolean) | ~6 | `LemoineToggleSwitches` |
| **Row-selection `CheckBox`** (inside list rows) | ~4 | **KEEP** (themed `CheckBox`) — see nuance |
| Non-editable `ComboBox` | 8 | `LemoineSingleSelect` (also fixes wheel-eat) |
| Editable/autocomplete `ComboBox` | 3 | `LemoineSearchAutocomplete` — **risky, defer** |
| Free-text `TextBox` | 15 | `LemoineTextField` |
| Numeric `TextBox` | 5 | `LemoineNumberStepper` / `LemoineNumberRange` |
| Folder-path `TextBox` (+dialog) | 5 | `LemoineFolderBrowser` |
| Conditional/visibility `TextBox` | ~3 | **risky, handle individually** |

---

## Scoping nuance — which `CheckBox`es convert

`LemoineToggleSwitches` is a *group of labeled boolean options*, not a checkbox-in-a-row.

- **Convert → ToggleSwitches:** standalone option booleans, e.g. BatchExport "Combine PDF"
  (765) / "Split output" (837), ClashDimension "Clear previous" (749), MakeCeilingGrids
  options, Discover include/select options that are settings (not per-row).
- **Keep as themed `CheckBox`:** per-row selection boxes inside scrolling lists —
  MakeCeilingGrids ceiling-type rows (296), CoordSet trade rows (132), Discover link/rule
  rows (234, 1042), and the "show all" header toggles (BatchDimension 131, BatchExport 195,
  ClashDimension 563). These are list affordances; a toggle switch per row is wrong UX.
  They already pick up the injected themed `CheckBox` style.

I'll make the convert/keep call per instance and call it out in each commit.

---

## What gets migrated (clear wins)

- **Free-text `TextBox` → `LemoineTextField`** (15): names, labels, prefixes, patterns.
- **Non-editable `ComboBox` → `LemoineSingleSelect`** (8): ramp/category/reference/tie/
  naming selectors. Bonus: kills wheel-eating on those.
- **Folder `TextBox` (+FolderBrowserDialog) → `LemoineFolderBrowser`** (5).
- **Numeric `TextBox` → `LemoineNumberStepper`/`Range`** (5): single value → Stepper,
  min/max or tolerance → Range, matching how each is parsed today.
- **Option `CheckBox` → `LemoineToggleSwitches`** (~6, per the nuance above).

## Deferred / risky (NOT in the first pass — flagged for a follow-up)

1. **Conditional TextBoxes** whose `Visibility` is driven by a sibling combo's "Custom"
   selection — LinkViewsLevel (594), ReplicateDependentViews (507). Need a show/hide
   wrapper; `LemoineTextField` supports it but the wiring is bespoke.
2. **Autocomplete ComboBoxes with custom filtering** — FiltersSettingsWindow (288),
   GlobalSettingsWindow.Filters (1186). Migrating to `LemoineSearchAutocomplete` may need
   the control to grow features; risk of regressing filter UX.
3. **MakeCeilingGrids folder box (370)** — verify it's actually a folder picker (browse
   button) before swapping.

These stay as-is until the clear wins land and are verified; then we tackle them with
individual care.

---

## Sequencing (one commit per group, each Windows-verifiable)

1. **P4a — T03-LinkViews** (LinkViewsDiscipline, LinkViewsLevel non-conditional fields):
   free-text → TextField, non-editable combo → SingleSelect. Smallest, low-risk.
2. **P4b — T02-Ceilings** (CeilingHeatmap ramp combo + name + tolerance; ProjectedCeiling
   folder; MakeCeilingGrids option checkbox): combo→SingleSelect (fixes the palette
   dropdown wheel), TextField, NumberRange, FolderBrowser.
3. **P4c — Testing** (BatchDimension, BatchExport, ClashDimension, CoordSet, CreateSheets):
   the bulk — combos→SingleSelect, option checkboxes→ToggleSwitches, free-text→TextField,
   numeric→Stepper/Range, folder→FolderBrowser.
4. **P4d — T01-AutoFilters tool + Discover** (Discover param combo, trade-name field).
5. **P4e — settings windows** (GlobalSettingsWindow partials, LegendSettingsWindow):
   free-text → TextField, numeric → Range, non-editable combo → SingleSelect.

Risky items (deferred list) handled in a final **P4f** only after the above is verified.

Each commit: migrate, run the CLAUDE.md silent-failure scan, keep it compile-safe, push.
Verification is Windows-only (per CLAUDE.md) — you confirm each group before I continue.

---

## Risks / notes

- `LemoineSingleSelect` is read-only single-choice; confirm each migrated combo is truly
  single-choice (all 8 are).
- `LemoineNumberStepper` vs `Range`: Stepper for a single integer (CreateSheets starting
  number), Range for a value with min/max or a tolerance (BatchDimension offset, tolerance).
- Field width: some raw TextBoxes set fixed `Width` (80/120/150); `LemoineTextField`/
  `SingleSelect` stretch — I'll constrain width where the layout needs it.
- No behavior change to validation/`IsValid` — only the control rendering swaps; the
  underlying fields and handlers stay.

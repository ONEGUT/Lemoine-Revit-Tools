# Plan — Rework Legend window to clone the Auto Filters layout

## Goal
Make the Legend Creator settings window a structural clone of the Auto Filters
window, give the right-hand data-input column a "rounder", scrollable, card-based
look (applied to **both** windows for consistency), and remove the redundant
"Legend Creation" ribbon button — rolling create/update fully into the single
remaining settings window, which is renamed **Legend Creation**.

## Key findings (research)
- `AutoFiltersLegendLaunchCommand` ("Legend Creation" ribbon button) and
  `OpenLegendSettingsCommand` ("Legend Settings" ribbon button) **both open the
  same `LegendSettingsWindow`**. Create/Update already lives inside that window
  (`HandleCreateUpdate` + the sidebar Create/Update button). So "rolling create
  into settings" = deleting the duplicate button and renaming the survivor.
- Auto Filters layout (`BuildFiltersContent`): 4 columns —
  `[trades 180] [rules * (min150)] [splitter 5] [editor 280 (min280)]`.
  The editor is a single `ScrollViewer` of rounded section **cards**
  (RULE / FILTER LOGIC / OVERRIDE STYLE / APPEARANCE), each = section label +
  `Border CornerRadius=6`.
- Legend layout today (`BuildMainLayout`): 3 columns —
  `[sidebar 220] [builder *] [right 260]`, no splitter. Right panel docks SIZING
  and TEXT STYLES at the top (flat, separator-divided) with PALETTE filling the
  rest. PALETTE also hosts the bulk-block editor (swapped via `BulkEditorChanged`).
- **Constraint:** `LemoineLegendPalette` uses an internal star-height (`*`) filter
  list, so it cannot live inside an outer vertical scroll — it must fill space.

## Decisions (confirmed with user)
- Right column = **scrollable rounded cards on top (SIZING, TEXT STYLES) + PALETTE
  filling the bottom** with its own scroll.
- **Add a GridSplitter** between the center canvas and the right column
  (5px, right column 280px min) to mirror the filter window.

## Files to change

### 1. Ribbon — `Source/App.cs`
- Remove the `legendPanel.AddItem(... "LT_AutoFiltersLegend" ... "Legend\nCreation"
  ... "AutoFiltersLegendLaunchCommand" ...)` button.
- Rename the surviving `LT_LegendSettings` button caption `"Legend\nSettings"` →
  `"Legend\nCreation"` (keep its `OpenLegendSettingsCommand` command + gear glyph,
  or switch to the colour glyph `` — will keep the gear unless told otherwise).

### 2. Delete orphaned command — `Source/Commands/T01-AutoFilters/AutoFiltersLegendLaunchCommand.cs`
- Now unreferenced after the ribbon button is removed. (It only opened the same
  window; create/update is unaffected — that uses `App.LegendCreatorEvent`.)

### 3. Design system — `Source/Lemoine/LemoineSettings.cs`
- Add `r["LemoineRadius_Card"] = new CornerRadius(10);` next to the other radii so
  both windows share one "rounder" card radius.

### 4. Legend window — `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml.cs`
- `BuildMainLayout`: convert to the 4-column filter clone —
  `[sidebar 180] [builder * (min150)] [splitter 5] [right 280 (min280)]` + a
  `GridSplitter` in col 2.
- `BuildRightPanel`: restructure into a DockPanel —
  - **Top:** a `ScrollViewer` containing a stack of rounded **cards**: SIZING card,
    TEXT STYLES card (reuse the same card chrome as the filter editor).
  - **Fill:** PALETTE / bulk-editor slot (`_paletteSlot`) keeps its star-fill
    height and own scroll; wrapped in matching card chrome so it "claims its space".
- Rebuild `BuildSizingSection` / `BuildTextStylesSection` as rounded cards
  (section label + `Border CornerRadius=LemoineRadius_Card`) instead of flat
  separator-divided blocks.
- Keep sidebar (legends list, Templates pill, Create/Update + Add Legend) — align
  widths/margins to the filter sidebar.

### 5. Filter window — `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`
- Bump the editor section cards from hardcoded `CornerRadius(6)` to
  `LemoineRadius_Card` (rounder), and ensure each section card claims full width
  consistently. (Visual-only; no behavioural change.)

## Out of scope / unchanged
- Drag-drop, multi-select, templates, preview, and create/update logic stay as-is.
- Threading/hosting unchanged (window already uses the STA + Dispatcher pattern).
- `OpenLegendSettingsCommand` remains the single entry point.

## Branch
Per task instructions, develop on `claude/pensive-hamilton-LbAT2` (already checked
out). No new branch needed.

## Post-change
Run the silent-failure scan and the UI bug-audit checklist before reporting done.
Cannot build on Linux (net48 + UseWPF) — changes are structural/manual-review only.

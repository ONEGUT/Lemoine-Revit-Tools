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

## Top-bar / footer / action-button rework (confirmed with user)

### Top bar (both windows) — make identical
`LemoineTitleBar` only: `[⚙ icon + window title]` (left, drag handle) … `[× close]` (right).
- Filters: **remove** the centered "Create Filters" button from the top bar.
- Legend: unchanged structurally (already icon + title + ×).

### Footer — REMOVED from both windows
- Root grid drops from `[38 toolbar | * content | 42 footer]` to `[38 toolbar | * content]`.
- `BuildFooter` / `_footerBorder` deleted; `UpdateRowHeights` only sets the toolbar row.
- Closing is via the top-bar `×`. Status text relocates to a transient label in the
  floating action area (above the pills, right-aligned).
- **Data-safety:** Filters loses its explicit Apply/Save button. To avoid losing
  buffered edits, add `OnClosed` → save `_filterTrades` to `AutoFiltersSettings`
  (Create already saves; Legend already saves on most edits).

### "Add" buttons — float at the bottom of their list (no sticky bar)
For trades, rules, and legends: delete the docked-bottom bordered bar and instead
append a rounded **ghost "＋ Add X" pill as the last child of the scrolling list
panel** (mirrors the existing `＋ Add New Group` affordance).
- Filters trades: in `FRefreshTradesSidebar` append to `_fTradeListPanel`.
- Filters rules: in `FRefreshRuleList` append to `_fRuleListPanel`.
- Legend: in `RebuildTabStack` append to `_tabStack`.

### Floating bottom-right action pills (over content, full rounded pill + shadow)
A vertical, right/bottom-aligned overlay in the content cell (high ZIndex,
margin ~16):
- **Both:** `Create` pill (Filters: "Create Filters" → `CreateFilters()`;
  Legend: "Create/Update Legend →" → `HandleCreateUpdate()`).
- **Legend only:** `Preview` pill stacked **directly above** the Create pill.
- Transient status text sits above the topmost pill (right-aligned).

### Legend Preview relocation + animation
- Remove the Preview button from `LemoineLegendLayoutBar` (layout bar becomes
  `[legend pill][✎ edit]`).
- Add a public `TogglePreview()` on `LemoineLegendBuilder`; the floating Preview
  pill calls it (host reads `PreviewVisible` to reflect on/off state).
- Re-origin the preview overlay `RenderTransformOrigin` from `(1,0)` → **`(1,1)`**
  so it grows from/to the bottom-right (the new Preview pill location).

### Files added to scope
- `Source/Lemoine/Controls/Legend/LemoineLegendLayoutBar.xaml.cs` (drop Preview btn).
- `Source/Lemoine/Controls/Legend/LemoineLegendBuilder.xaml.cs` (public TogglePreview,
  overlay origin (1,1)).

## Out of scope / unchanged
- Drag-drop, multi-select, templates, and create/update *logic* stay as-is.
- Threading/hosting unchanged (window already uses the STA + Dispatcher pattern).
- `OpenLegendSettingsCommand` remains the single entry point.
- Renamed ribbon button keeps its gear glyph `` (it still opens settings).

## Branch
Per task instructions, develop on `claude/pensive-hamilton-LbAT2` (already checked
out). No new branch needed.

## Post-change
Run the silent-failure scan and the UI bug-audit checklist before reporting done.
Cannot build on Linux (net48 + UseWPF) — changes are structural/manual-review only.

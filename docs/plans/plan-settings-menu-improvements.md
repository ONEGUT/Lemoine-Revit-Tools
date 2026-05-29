# Plan: Settings Menu Improvements

## Summary of Changes

Six targeted changes to the Filters/Settings window UI. No new files required —
all changes land in two existing files.

---

## Files Changed

| File | Reason |
|------|--------|
| `Source/Lemoine/GlobalSettingsWindow.xaml.cs` | Tab order, trash button centering |
| `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs` | Color swatches, chip pills, resizable panel, multi-select |

---

## Change Details

### 1 — Move Legend Creator tab behind Filters (xaml.cs: 170–181)

Reorder `_navDefs` so `("t08", "Legend Creator")` sits at index 2, immediately
after `("filters", "Filters / Color")`. No other code changes required because
`BuildTabNav` iterates the array in order.

**Before:**
```
General | Filters / Color | Ceiling Heatmap | Link Views | Legend Creator | …
```
**After:**
```
General | Filters / Color | Legend Creator | Ceiling Heatmap | Link Views | …
```

---

### 2 — Template dropdown buttons as chip pills (Filters.cs: 2241–2300)

`AddMenuRow` currently renders each action as a flat `Border` row with
transparent background. Restyle to match Lemoine chip visual language:

- `CornerRadius = 12` (full pill)
- `BorderThickness = 1`, `BorderBrush = LemoineBorder`
- `Background = LemoineRaised` (subtle fill, not transparent)
- Padding reduced to `5, 3, 10, 3` so chips sit compact
- Hover: `BorderBrush` shifts to `LemoineAccent`, background to `LemoineAccentDim`
- Section headers and separators remain unchanged

---

### 3 — Color swatches: circle → rounded square (Filters.cs: 694–704)

Replace the `Ellipse` (16×16) with a `Border`:

```csharp
var colorDot = new Border
{
    Width           = 14, Height = 14,
    Background      = BrushFromHex(rule.SurfColor ?? trade.Color),
    BorderThickness = new Thickness(1.5),
    CornerRadius    = new CornerRadius(3),
    Margin          = new Thickness(4, 0, 10, 0),
    VerticalAlignment = VerticalAlignment.Center,
};
colorDot.SetResourceReference(Border.BorderBrushProperty, "LemoineBorder");
```

Same change applies to the 10×10 trade-dot in the trade switcher pill
(Filters.cs ~197–203) for visual consistency.

---

### 4 — Center trash icon (xaml.cs: 756–771)

The `icon` TextBlock inside `BuildTrashConfirmButton` sets `VerticalAlignment`
but not `HorizontalAlignment`, leaving the glyph left-aligned within its
padding box. Fix: add `HorizontalAlignment = HorizontalAlignment.Center` and
`TextAlignment = TextAlignment.Center` to the icon TextBlock.

---

### 5 — Ctrl+click multi-select with dirty-field batch edit (Filters.cs)

**New fields on GlobalSettingsWindow (partial):**
```csharp
private readonly HashSet<string> _fSelectedRuleIds = new HashSet<string>();
private readonly HashSet<string> _fBatchDirtyFields = new HashSet<string>();
```

**Rule row click handler changes (`BuildRuleListRow`):**
- Normal click: clears `_fSelectedRuleIds`, sets `_fActiveRuleId`, refreshes editor
  as today.
- Ctrl+click: toggles `rule.Id` in `_fSelectedRuleIds` without clearing other
  selections. If the set has 2+ items, calls `FRefreshRuleEditor` which detects
  batch mode and renders the batch panel.

**Row visual states (added alongside existing active/hover states):**
- `_fSelectedRuleIds` members that are not `_fActiveRuleId` get a lighter tint
  (`LemoineAccentDim` at 60% opacity border, transparent bg) so users can see
  what is selected.

**`FRefreshRuleEditor` batch branch:**
- When `_fSelectedRuleIds.Count >= 2`, calls new `BuildBatchRuleEditor()` instead
  of the normal editor.
- `_fBatchDirtyFields` is cleared on entry.

**`BuildBatchRuleEditor()`:**
- Header: "Editing **N rules**" label (grayed out name field — replaced with a
  read-only italic label reading "— multiple rules —").
- Renders `BuildFilterLogicSection`, `BuildOverrideStyleSection`, and
  `BuildAppearanceSection` as normal, but every control fires a wrapper that
  records the field key into `_fBatchDirtyFields` before delegating to the
  active rule's mutation.
- A sticky "Apply to selection" bar at the bottom of the editor shows which
  sections are dirty (e.g. "Filter Logic ✓") and a primary "Apply to N rules"
  button. Clicking it iterates `_fSelectedRuleIds`, looks up each rule, and
  copies only the dirty fields from the active rule onto each target.
- Cancelling (clicking outside / switching rule) clears `_fBatchDirtyFields`
  and reverts `_fActiveRuleId` to single-select.

**Dirty field keys** (strings used as identifiers for dirty tracking):
- `"filter_logic"` — category chip input, logic operator, inclusion/exclusion
- `"override_styles"` — all sub-fields in `BuildOverrideStyleSection`
- `"appearance"` — all sub-fields in `BuildAppearanceSection`
- Granularity is per-section (not per-property), matching user intent.

---

### 6 — Resizable right editor panel (Filters.cs: 44–46 + panel wiring)

Convert the two-column `root` Grid in `BuildFiltersContent` to three columns:

```csharp
root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // left
root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                      // splitter
root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280), MinWidth = 280 });  // right
```

Add a `GridSplitter` in column 1:

```csharp
var splitter = new GridSplitter
{
    Width = 5,
    VerticalAlignment   = VerticalAlignment.Stretch,
    HorizontalAlignment = HorizontalAlignment.Center,
    ResizeDirection     = GridResizeDirection.Columns,
    Background          = Brushes.Transparent,
    Cursor              = Cursors.SizeWE,
};
Grid.SetColumn(splitter, 1);
root.Children.Add(splitter);
```

Move the existing right panel (editor border) to column 2. The right panel can
grow wider than 280 but not narrower. The left rule list picks up the freed
space automatically via the star column.

---

## What Is NOT Changing

- No changes to Legend Creator content, general tab, or any other tab.
- No changes to footer, toolbar, or theme/size rows.
- No data model or settings persistence changes.
- No new files.

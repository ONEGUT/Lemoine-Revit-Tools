# Plan — Legend Settings Redesign

## Goal

Consolidate legend creation and updating into the `LegendSettingsWindow`. Add a left sidebar that manages multiple legend slots simultaneously. Templates move to the top of the sidebar. Sizing/Scale/Text Styles move to the right panel. The old `StepFlowWindow` wizard flow (`AutoFiltersLegendLaunchCommand` + `LegendCreatorLaunchViewModel`) is retired.

---

## New Window Layout

```
┌─ Toolbar ─────────────────────────────────────────────────────────────────────────┐
├─ [Left Sidebar 220px] ──┬─ [Content area *] ──┬─ [Right Panel 260px] ────────────┤
│  TEMPLATES ˅ (pill btn) │  LegendLayoutBar     │  SIZING                          │
│  [template list]        │  (Templates pill     │    Scale / Swatch W×H            │
│                         │   removed)           │    Font / Gap                    │
│  ─────────────────────  │                      │  ──────────────────              │
│  LEGENDS                │  LemoineLegendBuilder│  TEXT STYLES                     │
│  [Legend 1 tab] ◀active │  canvas: rows/groups │    Title / Subtitle              │
│  [Legend 2 tab]         │  + drop bars         │    Group Header / Label          │
│  [Legend 3 tab]         │                      │  ──────────────────              │
│                         │                      │  PALETTE                         │
│  [Create Legend →]      │                      │  (LemoineLegendPalette or        │
│  (or Update Legend →)   │                      │   bulk block editor when 2+      │
│                         │                      │   blocks selected)               │
│  ─────────────────────  │                      │                                  │
│  ＋ Add Legend          │                      │                                  │
├─ Footer ────────────────┴──────────────────────┴──────────────────────────────────┤
│  [status text]                                                   [Apply]  [Close]  │
└────────────────────────────────────────────────────────────────────────────────────┘
```

**UX decisions confirmed:**
- Create/Update button: in the left sidebar under the active legend tab
- Text type picker: right panel, below sizing controls
- Legend naming: auto-mirrors `Layout.Title`; double-click tab label to rename

---

## Data Model Changes

### New `LegendEntry` class (added to `LegendCreatorSettings.cs`)

```csharp
public sealed class LegendEntry
{
    [XmlAttribute] public string  Id              { get; set; } = "";
    // Null = auto-mirror Layout.Title in the sidebar tab
    [XmlAttribute] public string? DisplayName     { get; set; }
    // Null = not yet created in Revit
    [XmlAttribute] public long?   RevitViewId     { get; set; }
    // Persisted TextNoteType ElementId.IntegerValue per role (null = project default)
    [XmlAttribute] public long?   TitleTypeId        { get; set; }
    [XmlAttribute] public long?   SubtitleTypeId     { get; set; }
    [XmlAttribute] public long?   GroupHeaderTypeId  { get; set; }
    [XmlAttribute] public long?   LabelTypeId        { get; set; }
    // Layout + rows (same as before, now per-entry)
    public LegendLayoutConfig        Layout { get; set; } = new LegendLayoutConfig();
    [XmlArray] public List<LegendRowConfig> Rows { get; set; } = new List<LegendRowConfig>();
    [XmlAttribute] public bool PreviewVisible { get; set; }
}
```

### Updated `LegendCreatorSettings` class

- Replace top-level `Layout`, `Rows`, `PreviewVisible` with `List<LegendEntry> Legends`.
- Keep old fields as `[XmlElement]` read-only for **migration only** (use `ShouldSerializeXxx() => false` so they are never re-written).
- On `Normalize()`: if `Legends.Count == 0` and legacy `Layout` was deserialized, create one `LegendEntry` from it (migration path for existing save files).
- `Templates` stays at the class level (unchanged).

---

## Files to Change

| File | Change |
|------|--------|
| `LegendCreatorSettings.cs` | Add `LegendEntry`; change to `List<LegendEntry> Legends`; migration path |
| `LegendSettingsWindow.xaml.cs` | Major rebuild: 3-column layout, sidebar, right panel with sizing + text styles |
| `LegendSettingsWindow.xaml` | (XAML is minimal — all layout is code-behind, no XAML changes needed) |
| `LemoineLegendBuilder.xaml.cs` | Remove `BuildSizingBar()`; expose `Layout` and `OnEdited` for external sizing controls |
| `LemoineLegendLayoutBar.xaml.cs` | Remove Templates pill (col 3); layout bar is now 3-col: [pill][edit btn][* preview centered] |
| `LegendCreatorTabContent.cs` | Update `BuildContent()` / `Apply()` for multi-legend model; `Apply(int entryIndex)` overload |
| `LegendCreatorEventHandler.cs` | Add `Action<ElementId>? OnLegendCreated` callback for returning created view ID |
| `OpenLegendSettingsCommand.cs` | Pre-query `TextNoteType` list and existing `Legend` views; pass to `LegendSettingsWindow` ctor |
| `AutoFiltersLegendLaunchCommand.cs` | Redirect to open `LegendSettingsWindow` (same as `OpenLegendSettingsCommand`), or delete if the ribbon button is unified |
| `LegendCreatorLaunchViewModel.cs` | **Delete** — replaced by inline logic in the sidebar |

---

## Detailed Behaviour

### Sidebar legend tabs
- Each tab displays `LegendEntry.DisplayName ?? LegendEntry.Layout.Title ?? "Untitled"`.
- Clicking a tab switches the content area to that legend's `LemoineLegendBuilder` (swap child).
- Double-clicking the tab label enters inline edit → writes `LegendEntry.DisplayName` on Enter/blur.
- Active tab: accent border + accent background.
- No limit on number of legends.

### Create / Update button (in sidebar below tabs)
- Label: **"Create Legend →"** when `activeEntry.RevitViewId == null`.
- Label: **"Update Legend →"** when `activeEntry.RevitViewId != null`.
- On click:
  1. Flush editing buffer → `LegendCreatorTabContent.Apply(activeIndex)`.
  2. Set `LegendCreatorHandler` properties from `activeEntry` (TextNoteType ids, view id, update mode).
  3. Raise `App.LegendCreatorEvent`.
  4. Disable button + show spinner text until `OnComplete` fires.
  5. `OnLegendCreated(viewId)` callback → store `viewId.IntegerValue` in `activeEntry.RevitViewId` → Save → re-label button "Update Legend →".

### Right panel — Sizing section
- Same controls as current `BuildSizingBar()` in `LemoineLegendBuilder`.
- Controls mutate `_activeBuilder.Layout` (direct reference); `OnEdited()` is called to propagate.
- Extracted to a private `BuildSizingPanel(LegendLayoutConfig layout, Action onEdited)` helper on the window.

### Right panel — Text Styles section
- 4 ComboBoxes (Title / Subtitle / Group Header / Label), each populated from the pre-queried `TextNoteType` list.
- Reads initial selection from `activeEntry.TitleTypeId` etc.
- On change: writes directly to `activeEntry.XxxTypeId`.

### Right panel — Palette section
- `LemoineLegendPalette` lives in the right panel (unchanged placement).
- Bulk block editor continues to replace the palette when 2+ blocks are selected (managed by `LemoineLegendBuilder`, which still owns `_rightPanelContent`).
  - **Constraint:** The right panel in the window has three stacked sections (Sizing, Text Styles, Palette). Only the Palette slot swaps. Sizing and Text Styles stay fixed.
  - Implementation: wrap the right panel in a `DockPanel`; Sizing + Text Styles are docked top; Palette fills the remaining space via `LastChildFill=true`. The builder's `RefreshRightPanel()` swaps the bottom child only.

### ＋ Add Legend button
- Creates a new `LegendEntry` with a fresh `LegendIdGen.New("legend")` Id.
- Appends to `LegendCreatorSettings.Instance.Legends`.
- Rebuilds sidebar; activates the new tab.

### Templates (top of sidebar)
- A "Templates ˅" pill button at the top of the left sidebar.
- Clicking it fires `LemoineLegendBuilder.ShowLegendTemplatesPopup(anchor)` on the active builder — no code duplication, existing popup logic reused.
- The layout bar's Templates pill is removed.

### `OpenLegendSettingsCommand` (updated)
- Queries `TextNoteType` list and existing `Legend` views from Revit **on the Revit main thread** (same pattern as `AutoFiltersLegendLaunchCommand`).
- Passes both lists to `LegendSettingsWindow(textTypes, legendViews)` constructor.

### `AutoFiltersLegendLaunchCommand` (disposition)
- If the ribbon still has a separate "Launch Legend" button, redirect it to open `LegendSettingsWindow` (same logic as `OpenLegendSettingsCommand`).
- The `LegendCreatorLaunchViewModel` + StepFlowWindow approach is fully removed.

---

## Migration Safety

Old `LegendCreatorSettings.xml` (single legend, flat `<Layout>` + `<Rows>`) will be automatically migrated to a single `LegendEntry` with `RevitViewId = null` on first load. No data is lost. The old XML nodes are read but not re-written.

---

## Silent Failure Audit (pre-commit)

Per CLAUDE.md, a silent-failure scan will be run after implementation and presented before committing.

---

## Branch base
_To be specified by user._

# Plan: Filters Standalone Window + Ribbon Dropdown

## Goal

1. Combine the three Auto Filters action buttons (Auto Filters, Discover Rules, Legend Creation) into a single `PulldownButton` in the ribbon.
2. Extract the Filters / Color tab from `GlobalSettingsWindow` into a fully standalone `FiltersSettingsWindow` opened by `OpenFiltersSettingsCommand`.

---

## Change 1 — Ribbon dropdown (App.cs)

**File:** `Source/App.cs`

Replace the current T01 Filters ribbon layout:
```
[Auto Filters large]  [Discover Rules stacked]  [Legend Creation large]  [Filter Actions split]
                      [Filters Settings stacked]
```

With:
```
[Filter Tools pulldown ▾]  [Filters Settings large]  [Filter Actions split]
  └─ Auto Filters
  └─ Discover Rules
  └─ Legend Creation
```

Steps:
- Create a `PulldownButtonData("LT_FilterTools", "Filter\nTools")` with a suitable glyph.
- Add it to the panel via `AddItem`, cast to `PulldownButton`.
- Add three `PushButtonData` children: `AutoFiltersLaunchCommand`, `DiscoverLaunchCommand`, `AutoFiltersLegendLaunchCommand`.
- Add `Filters Settings` as a standalone large `AddItem` (separate from the pulldown).
- Keep the existing `Filter Actions` `SplitButton` unchanged.

---

## Change 2 — Standalone FiltersSettingsWindow

### 2a — New files

| File | Purpose |
|---|---|
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml` | Minimal XAML shell (Window with named root Grid + toolbar/content/footer borders) |
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs` | Standalone Window class; owns all filter state, shared helpers, and P/Invoke |

The new window will:
- Have a `Grid` with 4 rows: toolbar (fixed px), pill-nav placeholder (none — no tabs needed), content (1*), footer (fixed px).
- Call `LemoineControlStyles.InjectInto(Resources, scrollBarWidth: 8)` in `OnLoaded`.
- Set `WindowInteropHelper.Owner = ComponentManager.ApplicationWindow` in `OnLoaded`.
- Use the same STA thread pattern as all other modeless Revit windows (via `OpenFiltersSettingsCommand`).

### 2b — State fields migrated to FiltersSettingsWindow

From `GlobalSettingsWindow.xaml.cs` (lines 36–98), the following move:

- `FillPatternNames`, `LinePatternNames`, `SetPatternLists()`
- `_filterTrades`, `_fActiveTradeId`, `_fActiveRuleId`
- `_fRuleListPanel`, `_fEditorBorder`, `_fTradeSwitcherBorder`, `_fStatusText`
- `_fActiveRowBorder`, `_fActiveNameTb`
- `_fSelectedRuleIds`, `_fShiftAnchorRuleId`, `_fMultiSelectBorders`
- `_dragRuleId`, `_dragSourceBorder`, `_dragSourceOrigIdx`, `_dragGhostClickOffset`, `_dragReadyBorder`
- `_dragTradeId`, `_tradeDragStart`, `_dragGhost`
- `_isRefreshingEditor`
- P/Invoke declarations: `GetCursorPos`, `GetWindowLong`, `SetWindowLong`, `GWL_EXSTYLE`, `WS_EX_TRANSPARENT`, `NativePoint`
- `_lastClickTime`, `_lastClickItemId`

### 2c — Shared helper methods migrated

From `GlobalSettingsWindow.xaml.cs`, these methods are called by Filters.cs and must move:

- `BrushFromHex(string? hex)` — thin wrapper around `BrushHelper.BrushFromHex`
- `HexToMediaColor(string hex)` — thin wrapper
- `BuildFlatButton(string label)` — wrapper around `LemoineControlStyles.BuildButton`
- `FlatSmBtn(string label)` — wrapper around `LemoineControlStyles.BuildSmallButton`
- `BuildAutoCompleteBox(...)` — ComboBox factory with autocomplete + IsKeyboardFocusWithin guard
- `ToLogicalPoint(int physX, int physY)`
- `ShowDragGhost(string label, string subtext, string colorHex, bool enabled)`
- `ShowDragGhostFromElement(FrameworkElement element, Point clickOffset)`
- `HideDragGhost()`
- `UpdateDragGhostPos()`
- `MakeGhostHwndTransparent(object sender, EventArgs e)`
- `BuildTrashConfirmButton(...)`, `BuildMoveCopyButton(...)`, `BuildRuleToggle(...)`
- `ContentHeader(string text)`, `MiniLabel(string text)`, `SubLabel(string text)`
- `BuildColorBar(...)` (used in filter tab)
- `FlashStatus(string msg)` (filter footer)
- `FindVisualChildren<T>(DependencyObject)` if present

### 2d — GlobalSettingsWindow.Filters.cs

Convert from partial class of `GlobalSettingsWindow` to **partial class of `FiltersSettingsWindow`**:
- Change `namespace LemoineTools.Lemoine` class declaration top: `public partial class FiltersSettingsWindow : Window`
- No other changes to the body — all method names and field references remain the same.

### 2e — Remove filters from GlobalSettingsWindow

In `GlobalSettingsWindow.xaml.cs`:
- Remove `("filters", "Filters / Color")` from `_navDefs`.
- Remove `case "filters": content = BuildFiltersContent(); break;` from `SwitchTab`.
- Remove `case "filters": ... AutoFiltersSettings.Instance.Save(); break;` from `ApplyCurrentTab`.
- Remove all filter state fields and their shared helpers (listed in 2b/2c above).
- Remove `SetPatternLists()` method.
- Remove `FillPatternNames` and `LinePatternNames` properties.
- Remove `BuildColorBar()` if only used by filters.

### 2f — Update OpenFiltersSettingsCommand

Replace `GlobalSettingsWindow` usage with `FiltersSettingsWindow` on a dedicated STA thread:

```csharp
// FiltersSettingsWindow singleton (like _window in other commands)
private static FiltersSettingsWindow? _window;

public Result Execute(...)
{
    // Bring existing window to front
    if (_window != null) { ... return Result.Succeeded; }

    // Query pattern lists on Revit main thread
    ...

    // Open on STA thread
    var win = new FiltersSettingsWindow();
    win.SetPatternLists(fillNames, lineNames);
    ...
    var thread = new Thread(() => { win.Show(); ... Dispatcher.Run(); });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    ready.Wait();
    _window = win;
    return Result.Succeeded;
}
```

---

## Files Changed

| File | Change |
|---|---|
| `Source/App.cs` | Replace Filters ribbon layout with pulldown + standalone Settings button |
| `Source/Commands/T01-AutoFilters/OpenFiltersSettingsCommand.cs` | Open `FiltersSettingsWindow` on STA thread instead of `GlobalSettingsWindow` |
| `Source/Lemoine/GlobalSettingsWindow.xaml.cs` | Remove filters tab, state fields, and shared helpers |
| `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs` | Change partial class to `FiltersSettingsWindow` |
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml` | New XAML shell |
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs` | New Window class with migrated state + helpers |

---

## Branch

`claude/hopeful-fermi-bZEKc` (current session branch — continuing from previous work)

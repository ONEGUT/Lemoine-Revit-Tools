# Plan: Legend Settings Standalone Window + Dedicated Ribbon Panel

## Goal

1. Extract the Legend Creator settings tab from `GlobalSettingsWindow` into a standalone `LegendSettingsWindow`.
2. Add a new `"T01B  Legend"` ribbon panel (within the existing "Lemoine Tools" tab) with a **Legend Creation** button and a **Legend Settings** button.
3. Move Legend Creation out of the T01 Filters panel entirely.
4. Ensure every ribbon button has an icon.

---

## Change 1 — New `LegendSettingsWindow`

### 1a — New files

| File | Purpose |
|---|---|
| `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml` | XAML shell: toolbar / content / footer (same 3-row structure as FiltersSettingsWindow) |
| `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml.cs` | Standalone `Window` class; hosts `LegendCreatorTabContent` |

### 1b — Window implementation

```csharp
public partial class LegendSettingsWindow : Window
{
    public LegendSettingsWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // theme + scale change subscriptions
    }

    private void OnLoaded(...)
    {
        // Apply theme, inject styles, build toolbar/footer
        _contentBorder.Child = LegendCreatorTabContent.BuildContent(this);
    }

    private void ApplyCurrent() => LegendCreatorTabContent.Apply();
}
```

Closed event (wired in command): `LegendCreatorTabContent.DiscardEdits()` on close.

### 1c — New command

**File:** `Source/Commands/Testing/OpenLegendSettingsCommand.cs`  
Opens `LegendSettingsWindow` on a dedicated STA thread (same pattern as `OpenFiltersSettingsCommand`).

### 1d — Remove Legend Creator tab from GlobalSettingsWindow

In `GlobalSettingsWindow.xaml.cs`:
- Remove `("t08", "Legend Creator")` from `_navDefs`.
- Remove `case "t08": content = LegendCreatorTabContent.BuildContent(this); break;` from `SwitchTab`.
- Remove `case "t08": LegendCreatorTabContent.Apply(); FlashStatus(...); break;` from `ApplyCurrentTab`.

`Source/Lemoine/Testing/GlobalSettingsWindow.LegendCreator.cs` — the file becomes empty (just a placeholder comment). Can delete it entirely.

---

## Change 2 — Ribbon layout

### T01 Filters panel (unchanged structure, Legend Creation removed)

```
[Filter Tools ▾]       — Auto Filters | Discover Rules
[Filters Settings]     — standalone
[Filter Actions ▾]     — Apply to Views | Remove from View | Delete from Project
```

### New T01B Legend panel (new)

```
[Legend Creation]      — AutoFiltersLegendLaunchCommand  (glyph: E8FD ColorSolid)
[Legend Settings]      — OpenLegendSettingsCommand        (glyph: E713 Settings gear)
```

### Icon audit — all buttons

| Button | Glyph | Source |
|---|---|---|
| Filter Tools ▾ | E71C (funnel) | existing |
| ↳ Auto Filters | E71C | existing |
| ↳ Discover Rules | E773 (Search) | new |
| Filters Settings | E713 (gear) | existing |
| Filter Actions ▾ | E700 (Settings cog) | new |
| ↳ Apply to Views | E710 (Add/Plus) | existing |
| ↳ Remove from View | E738 (Remove) | existing |
| ↳ Delete from Project | E74D (Delete trash) | existing |
| Legend Creation | E8FD (ColorSolid) | existing |
| Legend Settings | E713 (gear) | new |
| Ceiling Heatmap | E81D | existing |
| Ceiling Grids ▾ | E80A | existing |
| Link Views by Level | E8B7 (Layers) | existing |
| By Discipline | stacked — no large icon needed | — |
| Replicate Dep. Views | stacked — no large icon needed | — |
| Split by Level/Grid/Cell | stacked — no large icon needed | — |
| Extend Walls | E898 (Sort Up) | existing |
| Coord Drawing Set | E7C3 (Page) | existing |
| Batch Export | stacked — no large icon needed | — |
| Batch Dimension | stacked — no large icon needed | — |

---

## Files Changed

| File | Change |
|---|---|
| `Source/App.cs` | Remove Legend Creation from T01; add T01B Legend panel |
| `Source/Lemoine/GlobalSettingsWindow.xaml.cs` | Remove t08 from navDefs + SwitchTab + ApplyCurrentTab |
| `Source/Lemoine/Testing/GlobalSettingsWindow.LegendCreator.cs` | Delete (was already empty) |
| `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml` | New XAML shell |
| `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml.cs` | New standalone Window |
| `Source/Commands/Testing/OpenLegendSettingsCommand.cs` | New command |

---

## Branch

`claude/hopeful-fermi-bZEKc` (current session branch)

# Plan: Auto Filters Redesign

## Goal

Remove the Auto Filters step flow. Move filter creation into a "Create Filters" button
inside `FiltersSettingsWindow` (renamed "Auto Filters"). Creating filters updates/overwrites
`ParameterFilterElement` definitions in the project — no view assignment.

Workflow: Discover Rules → edit in Auto Filters window → Create Filters.

---

## Change 1 — `AutoFiltersEventHandler.cs`

Add a `CreateOnly` mode. When `true`:
- Skip `view.AreGraphicsOverridesAllowed()` check — no view needed.
- Skip `view.AddFilter()` and `ApplyRuleOverride()` in `ProcessRule`.
- Force `OverwriteFilterDefinition = true` (always overwrite — user's stated requirement).
- Transaction name becomes `"Auto Filters — Create"`.
- `SelectedDisciplines` left empty (all trades, same as current default).

The view parameter becomes nullable in `Run()`. `Execute()` passes `null` for `view`
when `CreateOnly = true`.

---

## Change 2 — `FiltersSettingsWindow.xaml.cs`

### 2a — Rename title
`LemoineTitleBar.Title` → `"Auto Filters"`.
XAML window `Title` attribute → `"Lemoine Tools — Auto Filters"`.

### 2b — Add "Create Filters" button to toolbar

Insert before the close button in `rightPanel` (same pattern as other toolbar action buttons).

On click:
1. Save current edits first (`ApplyCurrent()` — writes to `AutoFiltersSettings.Instance`).
2. Set handler: `App.AutoFiltersHandler.CreateOnly = true`, wire `OnComplete` callback.
3. Call `App.AutoFiltersEvent.Raise()`.
4. Flash `"Creating filters…"` in the footer status text immediately.

`OnComplete` callback (dispatched back via `Dispatcher.Invoke`):
- Success (pass > 0, fail == 0): Flash `"{pass} filter(s) created."`
- Partial: Flash `"{pass} created, {fail} failed."`
- All failed: Flash `"Failed — check Revit journal."` in red (LemoineRed / LemoineTextDanger
  if available, else LemoineText).

---

## Change 3 — `App.cs` ribbon

### T01 Filters panel

Before:
```
[Filter Tools ▾] → Auto Filters | Discover Rules
[Filters Settings]
[Filter Actions ▾]
```

After:
```
[Discover Rules]          ← standalone (was buried in pulldown)
[Auto Filters]            ← was "Filters Settings"
[Filter Actions ▾]
```

- Remove the `PulldownButton` "Filter Tools" entirely.
- Add `Discover Rules` as a standalone large button (glyph E773, same as before).
- Rename `"Filters\nSettings"` button label → `"Auto\nFilters"`, keep glyph E713.
- Keep `AutoFiltersHandler` / `AutoFiltersEvent` registered (still used by the window button).
- Remove `AutoFiltersLaunchCommand` registration (button deleted).

---

## Change 4 — Delete files

| File | Reason |
|------|--------|
| `Source/Commands/T01-AutoFilters/AutoFiltersLaunchCommand.cs` | Step flow entry point — no longer needed |
| `Source/Tools/T01-AutoFilters/AutoFiltersViewModel.cs` | Step flow view model — no longer needed |

The `AutoFiltersEventHandler.cs` is **kept** — the "Create Filters" button still uses it.

---

## Files Changed

| File | Change |
|------|--------|
| `Source/Tools/T01-AutoFilters/AutoFiltersEventHandler.cs` | Add `CreateOnly` property; skip view ops when true |
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs` | Rename title; add "Create Filters" toolbar button |
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml` | Update `Title` attribute |
| `Source/App.cs` | Remove pulldown, rename settings button, remove launch command |
| `Source/Commands/T01-AutoFilters/AutoFiltersLaunchCommand.cs` | **Delete** |
| `Source/Tools/T01-AutoFilters/AutoFiltersViewModel.cs` | **Delete** |

---

## Branch

`claude/hopeful-fermi-bZEKc` (current session branch)

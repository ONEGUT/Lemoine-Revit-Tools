# Plan — UI Inconsistency Fixes

Fixes the three design-system inconsistencies found in the UI sweep. One logical
theme: bring stray UI construction back onto the house tokens/controls.

## Branch
Work continues on the designated session branch `claude/wonderful-brown-AzQ83`.

## 1. Toggle switch — `"Transparent"` inside `SetResourceReference` (functional bug)

**File:** `Source/Lemoine/Controls/Input/LemoineToggleSwitches.xaml.cs`
**Lines:** 69–70 (BuildRow) and 171–172 (Toggle)

`"Transparent"` is not a registered resource key, so the lookup fails and WPF clears
`Background`/`BorderBrush` to `null`. An **off** row then loses hit-testing on the empty
gaps (only pill + text remain clickable) and drops its intended transparent border.

**Change:** split the ternary — keep `SetResourceReference` for the on-state key, and in
the off-state assign directly:
```csharp
if (on) {
    row.SetResourceReference(Border.BackgroundProperty,  "LemoineAccentDim");
    row.SetResourceReference(Border.BorderBrushProperty, "LemoineAccent");
} else {
    row.Background  = Brushes.Transparent;   // direct — "Transparent" is not a resource key
    row.BorderBrush = Brushes.Transparent;
}
```
Same treatment at lines 171–172 (`newOn`). Confirm `System.Windows.Media` is imported for
`Brushes` (it is — Ellipse/SolidColorBrush already used in this file).

## 2. Ad-hoc radius literals that duplicate a token value

**File:** `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`
**Lines:** 993, 1118, 1416, 1629, 3004 — each `CornerRadius = new CornerRadius(10), // … matches LemoineRadius_Card`

**Change:** remove the literal from the `Border` initializer and replace with a
`card.SetResourceReference(Border.CornerRadiusProperty, "LemoineRadius_Card");` after
construction (same pattern already used for Background/BorderBrush on those cards).

Scope note: the other ~120 `new CornerRadius(N)` literals in the codebase are left as-is —
they are intentional/contextual (8px window outer = Win11 DWM, pill half-heights, drag
drop-indicators) and not token duplicates. Only the five self-labelled card duplicates are
in scope.

## 3. Raw `TextBox` for a numeric setting → `LemoineInlineStepper`

**File:** `Source/Lemoine/T02-Ceilings/GlobalSettingsWindow.CeilingHeatmap.cs`
**Lines:** 122–145 (`case "number"`)

**Change:** replace the raw `TextBox` with the house numeric control, wired from `NumberOpts`
(`Min`, `Max`, `Step`; `Unit` keeps the existing trailing unit label):
```csharp
var stepper = new Controls.LemoineInlineStepper {
    MinValue = opts?.Min ?? 0,
    MaxValue = opts?.Max ?? 100,
    Step     = opts?.Step ?? 1,
    Decimals = (opts != null && opts.Step % 1 != 0) ? 2 : 0,
    Value    = double.TryParse(setting.Default?.ToString(), out var d) ? d : 0,
};
stepper.ValueChanged += v => ts?.ApplySettings(groupId, setting.Id, v);
row.Children.Add(stepper);
```
Keep the existing `opts?.Unit` label block unchanged.

## Verification
- Cannot build on Linux (net48 + UseWPF is Windows-only, per CLAUDE.md) — changes are
  reviewed for correctness, not compiled here.
- Post-change silent-failure scan per CLAUDE.md before commit.

## Commit
Single commit on `claude/wonderful-brown-AzQ83`:
`Fix toggle hit-testing and align radius/numeric inputs to house tokens`

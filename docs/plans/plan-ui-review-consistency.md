# Plan: UI Review — Consistency & Bug Fixes

## Summary

A full audit of all WPF windows and controls found 9 issues across 6 files. All fixes restore design-system compliance (resource keys, hit-testing, contrast). No new features or behaviour changes.

---

## Issues & Files Affected

### Critical — Hit-testing broken (SetResourceReference with "Transparent")

**Rule:** `SetResourceReference` looks up a resource key. `"Transparent"` is not a key — it resolves to `null`, which makes the element non-hit-testable (clicks fall through).

| # | File | Method | What breaks |
|---|------|--------|-------------|
| 1 | `Source/Lemoine/Controls/Input/LemoineMultiSelectTabs.xaml.cs` | `SetTabStyle()` | Inactive tabs can't be clicked |
| 2 | `Source/Lemoine/StepFlowWindow.xaml.cs` | `BuildTabButton()` | Inactive toolbar tabs unclickable |
| 3 | `Source/Lemoine/StepFlowWindow.xaml.cs` | `ActivateLogTab()` | Inactive log tab unclickable |
| 4 | `Source/Lemoine/StepFlowWindow.xaml.cs` | `ActivateStep()` | Incomplete/pending step circles/bars unclickable |

**Fix pattern:** Replace ternary `SetResourceReference` with `if (active) SetResourceReference(...) else direct = Brushes.Transparent`.

---

### Contrast — Hardcoded white on Stone theme accent

| # | File | Method | What breaks |
|---|------|--------|-------------|
| 5 | `Source/Lemoine/StepFlowWindow.xaml.cs` | `ActivateStep()` | `Brushes.White` on active/done step circles fails contrast on Stone theme (light accent `#9abdd4` uses dark knob `#2a2a2a`) |

**Fix:** Replace `_circleTexts[i].Foreground = Brushes.White` with `_circleTexts[i].SetResourceReference(TextBlock.ForegroundProperty, "LemoineKnobOn")`.

---

### Design-system — Inline values bypassing resource keys

These hardcoded values don't respond to `UiSizeChanged` (the UI scale preset feature).

| # | File | Property | Bypass |
|---|------|----------|--------|
| 6 | `Source/Lemoine/Controls/Layout/LemoineSectionCard.xaml.cs` | `FontSize = 11` | Should be `SetResourceReference(..., "LemoineFS_SM")` |
| 7 | `Source/Lemoine/Controls/Layout/LemoineSectionCard.xaml.cs` | `CornerRadius = new CornerRadius(4)` | Should be `SetResourceReference(..., "LemoineRadius_MD")` |
| 8 | `Source/Lemoine/Controls/LemoineCategoryChip.xaml` | `CornerRadius="10"` (XAML) | Should be `{DynamicResource LemoineRadius_Chip}` |

---

### Minor scaling — Inline computed values (respond to initial scale only)

| # | File | Method | Issue |
|---|------|--------|-------|
| 9 | `Source/Lemoine/StepFlowWindow.xaml.cs` | `BuildLogArea()` | `_logScroll.Height = Math.Round(90 * Scale)` set once; should use `SetResourceReference(..., "LemoineH_LogArea")` which already exists in `LemoineSettings.ApplyScaleTo()` |

(Step circle `CornerRadius` is constructed once at startup — fixing would require a new resource key `LemoineRadius_Circle` in `LemoineSettings.ApplyScaleTo()`. Low priority; left out of this plan to keep scope narrow.)

---

## Files to Change

1. `Source/Lemoine/Controls/Input/LemoineMultiSelectTabs.xaml.cs`
2. `Source/Lemoine/StepFlowWindow.xaml.cs`
3. `Source/Lemoine/Controls/Layout/LemoineSectionCard.xaml.cs`
4. `Source/Lemoine/Controls/LemoineCategoryChip.xaml`

No new files. No new resource keys needed (all keys already exist).

---

## Branch

`claude/ui-review-consistency-kc8OS` (pre-designated by session environment)

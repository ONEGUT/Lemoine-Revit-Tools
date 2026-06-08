# Plan: Fix Revit crash/hang when selecting a different theme

## Problem

Selecting a different theme in a settings window crashes (or hangs) Revit.

Root cause is a leaked, cross-thread WPF event subscription:

1. `LemoineSettings.SetTheme()` raises `ThemeChanged?.Invoke(theme)` **synchronously**
   on the calling window's STA thread (`Source/Lemoine/LemoineSettings.cs:50`).
2. Each subscriber marshals back to its own thread with a **blocking** `Dispatcher.Invoke`.
3. Each tool/settings window runs on its **own dedicated STA thread** and calls
   `Dispatcher.InvokeShutdown()` when it closes.
4. Four windows subscribe with **anonymous lambdas that are never unsubscribed**, so a
   closed window's handler keeps firing against its **terminated** dispatcher:
   - `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs:74`
   - `Source/Lemoine/T05-Clash/ClashDefinitions/ClashDefinitionsWindow.xaml.cs:41`
   - `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml.cs:67`
   - `Source/Lemoine/GlobalSettingsWindow.xaml.cs:37`

   `StepFlowWindow` (`Source/Lemoine/StepFlowWindow.xaml.cs:74,78`) is the correct
   reference: a named handler `-=`'d on `Closed`.

When a previously-opened-then-closed tool settings window's stale lambda runs,
`Dispatcher.Invoke` targets a shut-down dispatcher → either throws
`TaskCanceledException` on the theme-change thread (unhandled → crash) or blocks
forever (→ Revit hangs). Intermittent because it depends on what was opened/closed
earlier in the session.

## Fix

Three layers, defence in depth:

### 1. Unsubscribe on close (the actual leak — primary fix)

Convert each anonymous `ThemeChanged += t => Dispatcher.Invoke(...)` lambda to a
**named handler method**, subscribe in the constructor, and `-=` it in a `Closed`
handler — mirroring `StepFlowWindow`. Same for the matching `UiSizeChanged`
subscriptions in those windows (identical leak, same crash class).

Files:
- `Source/Lemoine/GlobalSettingsWindow.xaml.cs`
- `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs`
- `Source/Lemoine/T05-Clash/ClashDefinitions/ClashDefinitionsWindow.xaml.cs`
- `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml.cs`

### 2. Non-blocking, shutdown-guarded dispatch (prevents cross-thread deadlock)

In every theme/size handler, replace blocking `Dispatcher.Invoke(...)` with
non-blocking `Dispatcher.BeginInvoke(...)`, guarded by
`if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;`.
Applies to the four windows above **and** `StepFlowWindow.OnThemeChanged` /
`OnUiSizeChanged` (latent deadlock risk — blocking Invoke from the theme thread into
its STA thread). This removes the main-thread-blocking deadlock path.

### 3. Isolate subscriber invocation in SetTheme (stops one bad subscriber killing all)

In `LemoineSettings.SetTheme` / `SetUiSize`, walk the delegate invocation list and
wrap each subscriber call in try/catch routed through `LemoineLog.Swallowed(context, ex)`
(not a silent catch). A single dead/failing subscriber then can no longer abort theme
switching or crash the initiating thread.

File: `Source/Lemoine/LemoineSettings.cs`

## Files changed

| File | Change |
|------|--------|
| `Source/Lemoine/LemoineSettings.cs` | Isolate + log per-subscriber failures in `SetTheme`/`SetUiSize` |
| `Source/Lemoine/GlobalSettingsWindow.xaml.cs` | Named theme/size handlers, `-=` on Closed, BeginInvoke + shutdown guard |
| `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs` | Same |
| `Source/Lemoine/T05-Clash/ClashDefinitions/ClashDefinitionsWindow.xaml.cs` | Same |
| `Source/Lemoine/Testing/LegendCreator/LegendSettingsWindow.xaml.cs` | Same |
| `Source/Lemoine/StepFlowWindow.xaml.cs` | BeginInvoke + shutdown guard (already unsubscribes) |

## Not changed

- Theme definitions/brushes (`LemoineTheme.cs`) — already frozen, not the cause.
- The color picker / color-set code — unrelated (the report was the theme switcher).
- Build cannot be verified on Linux (net48 + UseWPF is Windows-only); user verifies on Windows.

## Verification

On Windows: open a tool's settings window (e.g. AutoFilters or Clash Definitions),
close it, then change the theme from the main settings window. Pre-fix this is the
crash repro; post-fix it should re-theme cleanly. Repeat with multiple tool windows
open simultaneously.

# Plan: Legend Creator Bug Fixes

Branch: `claude/hopeful-fermi-bZEKc`  
Based on: current HEAD (already contains Legend Creator files)

## Files to change

| File | Bugs fixed |
|------|-----------|
| `Source/Tools/Testing/LegendCreator/LegendCreatorSettings.cs` | #2 — add ViewScale property |
| `Source/App.cs` | #1 — register LegendCreatorHandler/Event statics |
| `Source/Commands/T01-AutoFilters/AutoFiltersLegendLaunchCommand.cs` | #1 — swap VM to LegendCreatorLaunchViewModel |
| `Source/Lemoine/Controls/Legend/LemoineLegendBuilder.xaml.cs` | #3, #7, #10 — idempotent Saved subscription; BeginInvoke; named toggle handler |
| `Source/Lemoine/Controls/Legend/LemoineLegendGroupCard.xaml.cs` | #4, #6 — dedup mouse handlers; capture block ID not index |
| `Source/Lemoine/Controls/Legend/LemoineLegendBlockRow.xaml.cs` | #4 — dedup mouse handlers |
| `Source/Tools/Testing/LegendCreator/LegendCreatorTabContent.cs` | #5 — unsubscribe before nullifying _builder in DiscardEdits |
| `Source/Tools/Testing/LegendCreator/LegendCreatorEventHandler.cs` | #8 — null-guard trade.Rules |
| `Source/Tools/Testing/LegendCreator/LegendCreatorLaunchViewModel.cs` | #9 — IsValid checks block count |

## Changes per bug (in recommended order)

### Bug 2 — LegendLayoutConfig missing ViewScale
Add `[XmlAttribute] public int ViewScale { get; set; } = 48;` to `LegendLayoutConfig`
and `ViewScale = ViewScale` to the `Clone()` return.

### Bug 1 — LegendCreatorEventHandler never wired
- App.cs: add `LegendCreatorHandler` / `LegendCreatorEvent` statics; register in OnStartup
- AutoFiltersLegendLaunchCommand.cs: instantiate `LegendCreatorLaunchViewModel` instead of `AutoFiltersLegendViewModel`

### Bugs 3 & 4 — Saved subscription leak + mouse handler accumulation (LemoineLegendBuilder)
- Line 140: add `AutoFiltersSettings.Saved -= OnFiltersSaved;` before `+=`
- Lines 193-196 (GroupCard): `-=` before `+=` for all four header mouse events; extract anonymous MouseLeave to named method
- Lines 159-162 (BlockRow): same pattern for outer mouse events

### Bug 6 — Stale capturedI in delete/drag lambdas
Replace `int capturedI = i` with `string capturedId = Group.Blocks![i].Id`
and update DeleteRequested to use `FindIndex(b => b.Id == capturedId)`.

### Bug 5 — DiscardEdits doesn't unsubscribe orphaned builder
Add explicit unsubscribe in `LegendCreatorTabContent.DiscardEdits()` before nullifying `_builder`.
Make `OnFiltersSaved` internal on `LemoineLegendBuilder` to allow the call.

### Bug 8 — Null trade.Rules swallowed silently
Add `if (trade.Rules == null) continue;` and `!string.IsNullOrEmpty(rule.Id)` guard
inside the ruleMap building loop.

### Bug 9 — IsValid only checks legend view count
Add block-count check: `s.Rows.Any(r => r.Groups?.Any(g => g.Blocks?.Count > 0) == true)`

### Bug 7 — Dispatcher.Invoke blocking in OnFiltersSaved
Change `Dispatcher.Invoke` to `Dispatcher.BeginInvoke(new Action(...))`.

### Bug 10 — Orphaned _previewToggle lambda
Extract anonymous Click handler to named `OnPreviewToggleClick` method.

# Unused-file audit

Scanned with `devtools/audit_unused_files.py` (word-boundary reference search across the whole
repo, partial-class-aware, excludes a control's own paired `.xaml`/`.xaml.cs` self-reference).

## Deleted (confirmed dead, zero live callers, traced end-to-end)

- `Source/Commands/T01-AutoFilters/ApplyFiltersToViewsLaunchCommand.cs` + `Source/Tools/T01-AutoFilters/ApplyFiltersToViewsViewModel.cs` + `ApplyFiltersToViewsEventHandler.cs` + their `App.cs` static wiring — no ribbon button ever pointed at this chain; "Apply to view" today runs through `ApplyTradesToView()` inside `FiltersSettingsWindow.xaml.cs`, which raises `App.AutoFiltersHandler`/`AutoFiltersEvent` directly.
- `Source/Commands/T01-AutoFilters/DeleteFiltersLaunchCommand.cs` + `Source/Tools/T01-AutoFilters/DeleteFiltersViewModel.cs` — same story; "Remove from view" today runs through `RemoveSelectedTradesFromView()` in `FiltersSettingsWindow.xaml.cs`, which raises `App.DeleteFiltersHandler`/`DeleteFiltersEvent` directly. Those two — `DeleteFiltersEventHandler.cs` and the `App.cs` handler/event — were kept; only the old launcher + its ViewModel wrapper were dead.
- `Source/Tools/T01-AutoFilters/AutoFiltersLegendViewModel.cs` + `AutoFiltersLegendEventHandler.cs` + their `App.cs` static wiring — never constructed anywhere; the live "Legend Creation" ribbon button uses the separate, unrelated `LegendCreatorEventHandler`/`LegendCreatorViewModel` family.
- `Source/Tools/T01-AutoFilters/AutoFiltersSettingsWindow.cs` — static `BuildPanel()` helper, zero callers anywhere.
- `Source/Tools/T05-Clash/AutoDimension/Core/ICollisionSource.cs` — `ICollisionSource`/`StaticCollisionSource`, a collision-source abstraction never wired into the real dimension engine.
- `Source/Lemoine/Controls/Input/LemoineNumberStepper.xaml` + `.xaml.cs` — CLAUDE.md itself calls it "the retired `LemoineNumberStepper`"; superseded by `LemoineInlineStepper`.
- `Source/Lemoine/LemoineSettingsWindow.xaml` + `.xaml.cs` — a full "Appearance Settings" window, never instantiated; superseded by `GlobalSettingsWindow`.
- (As part of the Link Audit rebuild) `Source/Tools/T08-Coordinates/LinkAuditViewModel.cs` + `LinkAuditRunHandler.cs` — replaced by the standalone `LinkAuditWindow`, which has nothing to run.

Stale comment mentions of the deleted `LemoineSettingsWindow` in `LemoineControlStyles.cs`,
`LemoineTitleBar.xaml.cs`, `LemoineSectionCard.xaml.cs`, and `GlobalSettingsWindow.xaml.cs` were
cleaned up too.

## Still pending a decision

- **`Source/Lemoine/LemoineIcons.cs`** (`LemoineIcon` enum + `LemoineIcons.Build(...)`) — the
  IcoMoon-based icon system. Nothing calls it; ribbon icons render through a completely separate
  mechanism (`App.cs`'s `CreateGlyphBitmap` + Segoe MDL2 Assets `char.ConvertFromUtf32(...)`).
  Related, not yet touched: `LemoineTheme.cs` lines ~68 and ~114 (the `IconFont` property and its
  `resources["LemoineIconFont"]` registration) and `Source/Resources/Fonts/IcoMoonFree.ttf`.
  Awaiting a decision on whether to wire this system into real use or remove it.

## Checked, NOT dead

- `OverviewDemoTool` / `OverviewSamples` / `ToolsOverviewDemos` — three cooperating, actively-used classes, not a dead overlap.
- `Source/Tools/Debuggers/` — contains only `ScopeBoxProbeCommand.cs`, whose button is live in the Developer panel.

# Unused-file audit

Scanned 292 `.cs` + 38 `.xaml` files under `Source/` with `devtools/audit_unused_files.py`
(word-boundary reference search across the whole repo, partial-class-aware, excludes a
control's own paired `.xaml`/`.xaml.cs` self-reference). Every candidate below was then
manually traced by reading the surrounding call sites before being listed — none are deletion
decisions, just evidence for review.

## Confirmed dead — chain A: "Apply Filters to Views" (no live entry point at all)

No `PushButtonData` anywhere references `ApplyFiltersToViewsLaunchCommand`, and nothing else
in the repo constructs the ViewModel or touches the handler outside this chain.

- `Source/Commands/T01-AutoFilters/ApplyFiltersToViewsLaunchCommand.cs`
- `Source/Tools/T01-AutoFilters/ApplyFiltersToViewsViewModel.cs` (only used by the above)
- `Source/Tools/T01-AutoFilters/ApplyFiltersToViewsEventHandler.cs` (only used by the above)
- `Source/App.cs`: `ApplyFiltersToViewsHandler` / `ApplyFiltersToViewsEvent` static properties + their `OnStartup` init lines (dead once the above are gone)

## Confirmed dead — chain B: "Delete Filters" standalone launcher (superseded, but the handler survives)

`DeleteFiltersLaunchCommand` has no ribbon button either — but `DeleteFiltersEventHandler` /
`App.DeleteFiltersHandler` / `App.DeleteFiltersEvent` are separately, genuinely called from
`FiltersSettingsWindow.xaml.cs:529-530` (the delete-from-view flow now lives inside the Auto
Filters window). Only the old standalone launcher + its ViewModel are dead.

- `Source/Commands/T01-AutoFilters/DeleteFiltersLaunchCommand.cs`
- `Source/Tools/T01-AutoFilters/DeleteFiltersViewModel.cs` (only used by the above)
- `DeleteFiltersEventHandler.cs` and `App.DeleteFiltersHandler`/`Event` **stay** — do not touch

## Confirmed dead — chain C: old Auto-Filters-Legend flow (superseded by Legend Creator)

Nothing constructs `AutoFiltersLegendViewModel` anywhere. The live "Legend Creation" ribbon
button (`LT_LegendSettings` → `OpenLegendSettingsCommand`) uses the separate, unrelated
`LegendCreatorEventHandler`/`LegendCreatorViewModel` family instead.

- `Source/Tools/T01-AutoFilters/AutoFiltersLegendViewModel.cs`
- `Source/Tools/T01-AutoFilters/AutoFiltersLegendEventHandler.cs` (only used by the above)
- `Source/App.cs`: `AutoFiltersLegendHandler` / `AutoFiltersLegendEvent` static properties + their `OnStartup` init lines

## Confirmed dead — standalone files

- `Source/Tools/T01-AutoFilters/AutoFiltersSettingsWindow.cs` — static `BuildPanel()` helper, zero callers anywhere.
- `Source/Tools/T05-Clash/AutoDimension/Core/ICollisionSource.cs` — `ICollisionSource` interface + `StaticCollisionSource` — a pluggable collision-source abstraction never wired into the real dimension engine (`LayoutScorer`/`GreedyLayoutEngine`/`LayoutSnapshot` each have their own unrelated `Obstacles` member, not this interface).
- `Source/Lemoine/Controls/Input/LemoineNumberStepper.xaml` + `.xaml.cs` — CLAUDE.md itself calls it "the retired `LemoineNumberStepper`" (superseded by `LemoineInlineStepper`); zero real code references, only that one doc mention.
- `Source/Lemoine/LemoineSettingsWindow.xaml` + `.xaml.cs` — a full "Appearance Settings" window, never instantiated anywhere (`new LemoineSettingsWindow(...)` appears only inside its own doc-comment as a usage *example*). Superseded by `GlobalSettingsWindow`, which is the one actually wired to the Settings ribbon button. Other files mention it only in "matches/replaces" comparison comments.

## Confirmed dead — icon system (3-part, needs a decision on scope)

Nothing calls `LemoineIcons.Build(...)` anywhere. The app renders every ribbon/UI glyph via
`char.ConvertFromUtf32(...)` against Segoe MDL2 Assets directly instead.

- `Source/Lemoine/LemoineIcons.cs` — the `LemoineIcon` enum (IcoMoon codepoints) + `LemoineIcons.Build(...)`
- `Source/Lemoine/LemoineTheme.cs` lines 69 and 115 — the `IconFont` property and its `resources["LemoineIconFont"]` registration (small edit inside a live, heavily-used file — not a file deletion)
- `Source/Resources/Fonts/IcoMoonFree.ttf` — the font asset backing `LemoineIconFont`, unused once the above are gone

## Checked, NOT dead (plan flagged these for verification; they're fine)

- `OverviewDemoTool` / `OverviewSamples` / `ToolsOverviewDemos` — three cooperating, actively-used classes (demo-tool wrapper, live document snapshot, demo-spec cache), not a dead overlap. `ToolsOverviewWindow.xaml.cs` and `OpenOverviewCommand.cs` both call into all three.
- `Source/Tools/Debuggers/` — contains only `ScopeBoxProbeCommand.cs`, whose button is live in the Developer panel. Nothing to clean up.

## Not evaluated for deletion

`GlobalSettingsWindow.Dimensions.cs` / `.ToolGroups.cs` / `T01-AutoFilters/GlobalSettingsWindow.Filters.cs` /
`T03-LinkViews/GlobalSettingsWindow.LinkViews.cs` (partial-class pieces of the live `GlobalSettingsWindow`),
and the various `*Models.cs` container files, were correctly recognized as alive by the
partial-class-aware script and are not candidates.

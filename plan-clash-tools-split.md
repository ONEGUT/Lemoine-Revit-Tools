# Plan — Split ClashDimension into cooperating tools

## Context

The existing **ClashDimension** tool (`Source/Tools/Testing/ClashDimension/`) does four
unrelated jobs in one ~1,850-line `IExternalEventHandler` + a ~1,160-line wizard: defines two
clash groups, detects clashes, draws colored markers, and places dimensions with a fragile
auto-layout engine. It's hard to use and hard to iterate because detection and dimensioning
are coupled into a single run. (An earlier experimental branch tried to fix the dimension
auto-layout in place and made it worse; that work was abandoned in favour of this split.)

We split it into a **definitions library** plus a **combined find/mark/dimension wizard**, and
**deprecate the old tool but keep its code**:

1. **Clash Definitions Builder** (own ribbon button, single window, UI-heavy) — a saved library
   of named clash definitions, each bundling the existing *Group 1 vs Group 2* system + marking
   settings, managed in a sidebar/editor UI styled like the Auto Filters trades/rules system.
2. **Clash Finder & Dimension** (own ribbon button, one StepFlow wizard) — pick saved
   definition(s) + views, detect clashes and place colored ES-tagged markers. A **checkbox**
   controls whether a **dimension pass runs afterward**. For now that pass is **discovery only**:
   it re-finds the tagged cross-lines and reports them (the real dimension placement is a
   separate rework, later). The dimension pass lives in **its own file**, invoked by the wizard's
   handler after marking completes.

The handoff is proven: cross-line `DetailCurve`s persist in the view and are ES-tagged, and the
dimension pass re-obtains references via `DetailCurve.GeometryCurve.GetEndPointReference(1)`.
`Reference` objects are never serialized.

## Guiding principle — additive, low risk

Do **not** refactor the existing ClashDimension handler/viewmodel; leave them untouched and only
hide the ribbon button. New tools get their own engine/UI code, **copied/adapted** from the old
handler. Some duplication is accepted (old tool is frozen, slated for later deletion). The one
thing new code must **share** with the old is the Extensible Storage **schema GUID + tag value**,
so markers/tags interoperate across tools.

## New code layout

```
Source/Tools/Testing/ClashShared/
  ClashTagSchema.cs     ES schema GUID 7D1F3A52-9C84-4F6B-BF21-2E6A8C4D10B9 + "LemoineCD"
                        + StampTag/HasTag/IsOurs/GetOrCreateTagSchema  (shared by marking + discovery)

Source/Tools/Testing/ClashDefinitions/                         <- Tool 1 (own button, single window)
  ClashDefinition.cs              DTO: Id, Name, Group1, Group2, marking settings
  ClashDefinitionsSettings.cs     XML singleton library (Save/Load/DeepCopy/Duplicate/Delete/Move)
  ClashGroupEditor.cs             reusable WPF builder for one ClashGroupSpec (copied from old VM)
  ClashDefinitionsWindow(.xaml).cs  sidebar + editor library window (Auto-Filters style)
Source/Commands/Testing/OpenClashDefinitionsCommand.cs

Source/Tools/Testing/ClashFinder/                              <- Tools 2+3 (one button, one wizard)
  ClashFinderViewModel.cs         ILemoineTool wizard (pick defs, pick views, options + run)
  ClashFinderEventHandler.cs      IExternalEventHandler: runs ClashEngine, then ClashDimensionPass if enabled
  ClashEngine.cs                  detection + marking (scan->find->mark->tag), copied from old handler
  ClashDimensionPass.cs           dimension pass in its OWN file; for now = discovery + report
Source/Commands/Testing/ClashFinderCommand.cs
```

`ClashGroupSpec` (`Source/Tools/Testing/ClashDimension/ClashGroupSpec.cs`) is already public &
`XmlSerializer`-safe — **reuse as-is** (don't move it; the old tool still references it).
Element picking reuses the existing `App.ClashPickHandler/ClashPickEvent` (generic, not
ClashDimension-specific).

## Tool 1 — Clash Definitions Builder

- **DTO `ClashDefinition`** (public, parameterless ctor): `[XmlAttribute] Id`, `[XmlAttribute] Name`,
  `Group1`/`Group2` (`ClashGroupSpec`), and marking settings lifted from `ClashDimensionSettings`:
  `ToleranceMm`, `FillStyle`, `FallbackColorHex`, `CrossLineTypeName`, `DimTarget`,
  `ClearPrevious`, `MaxClashes`.
- **`ClashDefinitionsSettings`** — mirror `AutoFiltersSettings`
  (`Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs`): `Lazy<T>` singleton, `Save/Load`,
  `static DeepCopy` (serialize-string round-trip), `Duplicate/Delete/Move`, `ExportTo/TryImportFrom`.
  File `%AppData%\LemoineTools\ClashDefinitions.xml`. On first load with an empty list, optionally
  seed one "Imported from Clash Dimension" definition from the old `ClashDimensionSettings.Instance`
  so users aren't empty-handed.
- **UI** — single Window cloned from `FiltersSettingsWindow` (`Source/Lemoine/T01-AutoFilters/`):
  `OnLoaded` applies theme + `LemoineControlStyles.InjectInto`; `OnClosed` dirty-check auto-saves
  (no Apply button, per CLAUDE.md). Left sidebar = definition rows (mirror `BuildTradeRow` /
  `FRefreshTradesSidebar` / `AppendAddTradePill`, "+ Add" via `LemoineControlStyles.BuildAddPill`,
  inline rename, duplicate/delete). Right editor = Name + **Group 1**/**Group 2** sections via
  `ClashGroupEditor` (each in a `LemoineSectionCard`) + marking-settings rows (copied from
  `BuildS5`, marking rows only) + fallback-color `LemoineColorPickerPanel`.
- **`ClashGroupEditor`** — copy the group builder from `ClashDimensionViewModel`
  (`BuildGroupStep/BuildRulesBody/BuildCategoryBody/BuildElementsBody`) + its filter/category/
  source-doc mapping helpers + `StartPick`, adapted to render into a panel and read/write a
  `ClashGroupSpec`. Reuse `LemoineMultiSelectTabs`, `LemoineSingleSelect`.
- **Command** `OpenClashDefinitionsCommand` — clone `OpenFiltersSettingsCommand` (ReadOnly, STA
  thread, `_window` re-activation guard); gather line-style names + source-document list on the
  main thread.

## Tools 2 + 3 — Clash Finder & Dimension (one button, one wizard)

- **Command** `ClashFinderCommand` — clone `ClashDimensionCommand`'s data gathering (plan views,
  line-style names, source docs) and STA launch of `StepFlowWindow(new ClashFinderViewModel(...))`.
  (No grid/floor/dim-style queries — not needed for marking/discovery.)
- **ViewModel** (`ILemoineTool`), `RunLabel` "Find & Mark Clashes ->":
  1. **S1 Pick definition(s)** — multi-select from `ClashDefinitionsSettings.Instance.Definitions`.
  2. **S2 Pick views** — copy `BuildS1`/`BuildViewGroups` from the old VM (active + selected plan
     views via `LemoineMultiSelectTabs`).
  3. **S3 Options & run** — run-level **Clear previous** toggle, **ShowAllDocuments**, and the
     **"Run dimension pass after marking" checkbox** (default off). Summary cards.
- **Handler** `ClashFinderEventHandler.Execute`:
  - One transaction "Lemoine - Clash Finder". If Clear-previous: delete `ClashTagSchema.IsOurs`
    filled regions + cross lines **once, before** the definition loop (so later definitions don't
    wipe earlier markers). Then for each selected `ClashDefinition`, call
    `ClashEngine.Run(doc, viewIds, def.Group1, def.Group2, markingOptionsFrom(def), log)`.
    Aggregate counts. Commit.
  - **After marking**, if the dimension-pass checkbox is set, call
    `ClashDimensionPass.Run(doc, viewIds, log)` (its own file). Report its results through the
    same `PushLog/OnComplete`.
- **`ClashEngine`** — copy scan->find->mark->tag from the old handler: `ScanGroupSpec/ScanRules/
  ScanCategories/ScanElements`, `FindClashes` + solid/bbox helpers, `CreateClashMarker` +
  `GetOrCreateFilledRegionType` + `CreateLine` + `ResolveRuleColor` + `ResolveLineStyleId`.
  Replace handler-property reads with a passed `ClashMarkingOptions` and `Log()` with an injected
  delegate. **Preserve the link-instance transform + `CreateLinkReference` logic** or
  cross-document clashes break. Tags via the shared `ClashTagSchema`.
- **`ClashDimensionPass`** (own file; discovery only for now) — per view:
  `FilteredElementCollector(doc, viewId).OfCategory(OST_Lines).WhereElementIsNotElementType()
  .Where(ClashTagSchema.IsOurs).OfType<DetailCurve>()`; re-obtain `dc.GeometryCurve?.
  GetEndPointReference(1)`; count curves, valid references, and tagged filled regions; report
  per-view + totals. No placement. Read-only (no transaction needed).

## App.cs registration

- Add **one** static handler/event pair for `ClashFinderEventHandler` next to the existing
  ClashDimension registration (Tool 1 reuses `ClashPick`; the dimension pass runs inside the
  finder handler, so no extra event).
- Add **two** ribbon buttons in the Testing panel via the `Btn()` helper (use
  `char.ConvertFromUtf32(...)` for any glyph, per CLAUDE.md): "Clash Definitions" ->
  `OpenClashDefinitionsCommand`; "Clash Finder & Dimension" -> `ClashFinderCommand`.
- **Deprecate the old button**: remove the `testingPanel.AddItem(Btn("LT_ClashDimension", ...))`
  call. Leave all ClashDimension classes and the `ClashDimensionHandler/Event` registration
  untouched.

## Reused symbols (paths)

- `ILemoineTool`, `StepDefinition`, `StepFlowWindow` — `Source/Lemoine/`.
- `ClashGroupSpec`, `ClashPickEventHandler` — `Source/Tools/Testing/ClashDimension/`.
- Library-UI template — `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs` +
  `GlobalSettingsWindow.Filters.cs`; settings template `Source/Tools/T01-AutoFilters/
  AutoFiltersSettings.cs`; command template `Source/Commands/T01-AutoFilters/
  OpenFiltersSettingsCommand.cs`.
- Wizard/data-gather template — `Source/Commands/Testing/ClashDimensionCommand.cs`.
- Controls — `Source/Lemoine/Controls/` (`LemoineMultiSelectTabs`, `LemoineSingleSelect`,
  `LemoineColorPickerPanel`, `LemoineSectionCard`), `LemoineControlStyles.cs`.
- `LemoineLog.Swallowed/Error` for every swallowed exception (no empty catches).

## CLAUDE.md gotchas to honor

- Files mixing WPF + `Autodesk.Revit.DB` need the alias block (`WpfGrid`, `WpfVisibility`,
  `WpfTextBox`, `RevitColor`, ...).
- `LemoineMultiSelectTabs`: subscribe to `SelectionChanged` **before** calling `SetGroups`.
- New settings DTOs must be **public** for `XmlSerializer`.
- Segoe MDL2 glyphs via `char.ConvertFromUtf32`; verify literal glyph strings with Python.
- Settings windows auto-save on change — no Apply button.

## Risks / decisions to flag

- **Engine duplication** (deliberate, for safety): fixes must touch both copies, but the old one
  is frozen. Shared `ClashTagSchema` prevents tag drift.
- **Clear-previous is run-level**, not per-definition (so multi-definition runs don't self-wipe).
- **Dimension pass is discovery-only**; on rediscovery there's no persisted way to know which line
  is the X- vs Y-measuring reference or which lines share a clash. Deferred to the rework; a small
  future Tool-2 enhancement could persist `cx/cy/axis` into the ES entity to make it unambiguous.
- Cannot build on Linux (CLAUDE.md) — verify by code review + the user's Windows build.

## Verification

1. **Tool 1**: create/duplicate/rename/reorder a definition with two groups + marking settings;
   reopen Revit and confirm it persisted to `ClashDefinitions.xml`.
2. **Finder**: select that definition + views, run with the dimension checkbox **off** -> colored
   ES-tagged markers + cross lines appear; log reports clash counts; re-run with Clear-previous
   and confirm old markers are removed.
3. **Dimension pass**: re-run with the checkbox **on** -> log reports the number of tagged cross
   lines + valid references + filled regions per view (matching the marker count), no errors in
   `diagnostics.log`.
4. Confirm the old ClashDimension button is gone and the plugin still loads.

## Build order

ClashShared (`ClashTagSchema`) -> Tool 1 (`ClashGroupSpec` reuse -> `ClashDefinition(s)` ->
`ClashGroupEditor` -> window -> command) -> Finder (`ClashEngine` -> `ClashDimensionPass` ->
handler -> viewmodel -> command) -> App.cs wiring + deprecate old button. Commit incrementally.

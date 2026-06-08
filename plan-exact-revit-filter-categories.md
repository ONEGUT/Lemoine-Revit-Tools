# Plan — Make the category picker exactly match Revit's built-in filter list

## Goal
The category / subcategory list in the Filters rule editor (and Discover / Clash,
which share the same data) should **exactly match the categories Revit shows in its
own "Edit Filters → Categories" list**, instead of the hand-maintained hardcoded map.

The only way to be exact is to read the list from the Revit API
(`ParameterFilterUtilities.GetAllFilterableCategories(doc)`) — Revit's filterable set
is document-dependent (enabled disciplines, loaded categories), so no static list can
match it for every model.

## The constraint we have to design around
`AutoFiltersSettings` is a Revit-free-ish settings singleton with **no document**.
But every window that hosts the category picker is launched from an `IExternalCommand`
that **does** run on Revit's main thread with the active `Document`
(`OpenFiltersSettingsCommand`, `DiscoverLaunchCommand`, `OpenClashDefinitionsCommand`).
This is exactly where pattern lists are already captured today
(`OpenFiltersSettingsCommand` builds fill/line pattern lists from the doc). So we
capture the filterable categories there too and hand them to the shared settings store
before the window thread starts.

## Approach
1. **`AutoFiltersSettings.cs`**
   - Keep the existing hardcoded dictionaries as **fallback defaults**, renamed
     `DefaultKnownCategoryMap`, `DefaultKnownCategoryDisplayNames`,
     `DefaultCategorySubcategories`.
   - Add nullable runtime fields `_runtimeCategoryMap`, `_runtimeDisplayNames`,
     `_runtimeSubcategories`.
   - Convert `KnownCategoryMap`, `KnownCategoryDisplayNames`, `CategorySubcategories`
     from `static readonly` **fields** into `static` **properties** that return the
     runtime snapshot when captured, else the hardcoded default. Call sites are
     unchanged (`AutoFiltersSettings.KnownCategoryMap`, etc.).
   - Add `public static void CaptureFilterableCategories(Document doc)`:
     - `ParameterFilterUtilities.GetAllFilterableCategories(doc)` → for each id that is
       a real `BuiltInCategory` (negative id, not `INVALID`), map `Category.Name`
       (Revit's exact display name) → the `OST_*` string.
     - Build the display list = sorted real names; build the caret hierarchy by
       intersecting the curated `DefaultCategorySubcategories` grouping with the real
       set (children/parents not actually filterable are dropped).
     - Store into the runtime fields. Wrap in try/catch → `LemoineLog.Swallowed`; on
       failure the snapshot stays null and the hardcoded fallback is used.

2. **Capture call sites** (add one line, on the main thread, before the window thread):
   - `OpenFiltersSettingsCommand.Execute()`
   - `DiscoverLaunchCommand`
   - `OpenClashDefinitionsCommand`

3. **No UI/XAML changes.** The carets, hierarchy, flat-search fallback, and the
   display↔OST conversions in `GlobalSettingsWindow.Filters.cs`, `DiscoverViewModel.cs`,
   and `ClashGroupEditor.cs` already key off these three statics, so they pick up the
   real data automatically.

## Files changed
- `Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs` — defaults→fallback, runtime
  snapshot, `CaptureFilterableCategories`.
- `Source/Commands/T01-AutoFilters/OpenFiltersSettingsCommand.cs` — capture call.
- `Source/Commands/T01-AutoFilters/DiscoverLaunchCommand.cs` — capture call.
- `Source/Commands/T05-Clash/OpenClashDefinitionsCommand.cs` — capture call.

## One open decision — subcategory depth
`GetAllFilterableCategories` returns mostly `BuiltInCategory` ids plus, occasionally,
**non-builtin custom subcategory** ids (positive ids). The filter engine stores
categories only as `OST_*` strings, so it **cannot** persist or build a filter for a
non-builtin subcategory today. This plan therefore includes **every BuiltInCategory-backed
filterable category exactly as Revit lists it** (the vast majority and the only ones that
produce a working filter) and skips non-builtin custom subcategories. Supporting those too
would require an `ElementId`-based category in the filter serialization + creation path — a
much larger change. Recommend scoping this PR to BuiltInCategory parity and revisiting
custom subcategories separately if you hit a model that needs them.

## Fallback behaviour
With no document (preview app, or API call throws) the runtime snapshot is null and the
picker shows today's hardcoded list — no regression.

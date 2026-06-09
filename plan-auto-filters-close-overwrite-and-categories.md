# Plan — AutoFilters: stop close-overwrite of unchanged rules + expand category picker

## Problem 1 — Closing the Auto Filters menu overwrites externally-edited filters

When the Filters settings window closes, `FiltersSettingsWindow.OnClosed` runs an
auto-create pass (`AutoFiltersEventHandler` with `CreateOnly = true`) whenever the
trade buffer is *dirty* **or** the saved manifest is stale. In `CreateOnly` mode the
handler computes `refreshDef = createOnly || overwriteDef`, which is **always true**,
so it rewrites **every** existing filter's categories + element-filter rule in place
(`SetCategories` / `SetElementFilter`) — even for rules the user never touched in the
menu. If a filter's rule was edited *outside* the menu (in Revit's own filter editor),
that manual edit is silently clobbered on close.

**Fix — only refresh definitions of rules whose definition actually changed in the menu.**

- `AutoFiltersSettings.cs`: add
  `ComputeChangedFilterNames(before, after)` → `HashSet<string>`. For each enabled,
  filter-producing, non-externally-managed rule in `after`, include its filter name when
  its **definition signature** (sorted BuiltInCategories | Parameter | MatchType | sorted
  Match keywords) differs from the matching rule in `before`, or no matching rule existed
  in `before`. Rules are matched by `MakeFilterName(trade.Id, rule.Name)`.
- `AutoFiltersEventHandler.cs`: add `public HashSet<string>? ChangedFilterNames { get; set; }`.
  Change the refresh gate in `ProcessRule` to:
  `refreshDef = overwriteDef || (createOnly && (ChangedFilterNames == null || ChangedFilterNames.Contains(filterName)))`.
  - `null` ⇒ today's behaviour (refresh all) — preserves every other caller.
  - Existing filters not in the set are reused **untouched** (definition preserved).
  - Brand-new filters (rules just added) don't exist yet, so they're still created
    regardless of the gate. Orphan cleanup is unchanged.
- `FiltersSettingsWindow.xaml.cs`: in the close path, deserialize the load-time
  `_filtersSnapshot` back to trades, compute the changed set vs the current buffer, and
  pass it to `handler.ChangedFilterNames` in `RaiseAutoCreate`. Add a `DeserializeTrades`
  helper mirroring the existing `SerializeTrades`.

Net effect: closing the menu only rewrites filters whose rule definition you changed in
the menu; everything else — including manual external edits — is left alone. Missing
filters are still (re)created; orphans are still removed.

## Problem 2 — Expand the category picker to all model categories/subcategories

Every category picker in the app (filters rule editor, Discover scan config, Clash group
editor) is driven by the single static table `AutoFiltersSettings.KnownCategoryMap`
(`KnownCategoryDisplayNames` = its keys). The table is a curated subset, so many model
categories/subcategories aren't selectable.

**Fix — expand `KnownCategoryMap` to a comprehensive model-category list.**

- `AutoFiltersSettings.cs`: add the missing model categories (e.g. Parking, Roads, Parts,
  Assemblies, Gutters, Fascias, Slab Edges, Roof Soffits, Wall Sweeps, Curtain Systems,
  Mass Floors, Wires, Plumbing Equipment, Mechanical Control Devices, Medical Equipment,
  Audio Visual Devices, Hardscape, Vertical Circulation, Signage, Fire Protection, …) to
  `KnownCategoryMap`. Because every picker reads from this one table, this covers
  "anywhere else where categories are being selected" automatically.
- Harden `KnownCategoryDisplayNames` so the picker never shows a dead entry: build it from
  only the map entries whose OST string parses to a valid `BuiltInCategory` (version-safe;
  silently drops any name not present in the running Revit). Requires adding
  `using Autodesk.Revit.DB;` to the settings file (project already references RevitAPI).
- `KnownBuiltInCategories` is unused dead code — left as-is (out of scope).

## Files touched
- `Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs`
- `Source/Tools/T01-AutoFilters/AutoFiltersEventHandler.cs`
- `Source/Lemoine/T01-AutoFilters/FiltersSettingsWindow.xaml.cs`

## Branch
Base off `main`; develop on `claude/exciting-hypatia-5wDLP`.

## Notes
- No WPF layout changes — pickers already render the expanded list from the same source.
- Post-change silent-failure scan will be run before commit.

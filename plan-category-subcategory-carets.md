# Plan — Expand/collapse subcategory carets in the category picker

## Goal
Keep the category list flat as today, but give categories that own related
sub-categories a small caret (▸/▾) that expands to reveal those sub-categories as
individually-selectable rows (e.g. *Duct Fittings / Insulation* under *Ducts*). Parents
remain selectable in their own right.

## Where it applies
- **Filters rule editor** category chip (`GlobalSettingsWindow.Filters.cs`, `catChip`,
  ~line 1144) — the only `LemoineTagChipInput` used for categories. Gets the caret tree.
- **Discover** and **Clash group editor** already group by discipline and use different
  controls — they **stay as-is**. They read `KnownCategoryMap`, so the newly-added
  categories are already exposed there.

## Design

### 1. Reusable hierarchy in `LemoineTagChipInput`
- New optional CLR property `Hierarchy : IReadOnlyDictionary<string, IReadOnlyList<string>>?`
  (parent display name → child display names). `null` ⇒ today's flat behaviour, untouched.
- Popup rendering:
  - **Search empty** → tree mode: render top-level rows = items not listed as a child of any
    parent. A parent (has children) shows a caret on the left; the caret toggles expansion
    (tracked in a `_expandedParents` set); expanding inserts indented child rows beneath it.
    The label area still selects the parent category itself.
  - **Search non-empty** → flat mode over all `ItemsSource` (parents + children) so search
    still finds nested items. (Existing flat path reused verbatim.)
  - Children are only rendered if present in `ItemsSource` (so the BuiltInCategory-validity
    filter on `KnownCategoryDisplayNames` still drops anything invalid).
  - Selected leaf rows hidden (as today); parents with children always shown so their
    children stay reachable, with a dim ✓ when the parent itself is selected.
- Caret glyph via `char.ConvertFromUtf32(0x25B8)` / `0x25BE` (ASCII codepoints in source).
- All current call sites (params, keywords, debug tags) pass no `Hierarchy` → unchanged.

### 2. Subcategory map in `AutoFiltersSettings`
Add `public static readonly Dictionary<string, IReadOnlyList<string>> CategorySubcategories`
mapping a parent display name → child display names (all real, filterable categories already
in `KnownCategoryMap`). Proposed grouping:

| Parent | Children |
|---|---|
| Ducts | Duct Fittings, Duct Accessories, Duct Insulation, Duct Linings, Flex Ducts, Air Terminals |
| Pipes | Pipe Fittings, Pipe Accessories, Pipe Insulation, Pipe Linings, Flex Pipes |
| Fabrication Ductwork | Fabrication Hangers, Fabrication Containment |
| Fabrication Pipework | Fabrication Hangers, Fabrication Containment |
| Mechanical Equipment | Mechanical Control Devices |
| Plumbing Fixtures | Plumbing Equipment |
| Cable Trays | Cable Tray Fittings |
| Conduits | Conduit Fittings |
| Lighting Fixtures | Lighting Devices |
| Electrical Equipment | Electrical Fixtures, Wires |
| Walls | Curtain Wall Panels, Curtain Wall Mullions, Curtain Systems, Wall Sweeps |
| Roofs | Fascias, Gutters, Roof Soffits |
| Floors | Slab Edges |
| Structural Framing | Structural Trusses, Structural Stiffeners, Structural Connections, Structural Rebar Couplers |
| Rebar | Area Reinforcement, Path Reinforcement, Fabric Reinforcement, Fabric Area |
| Site | Topography, Planting, Parking, Roads, Hardscape |

(Children are hidden from the flat top level and only appear under their parent's caret.
Note: filters can only target real model categories, so each "subcategory" here is itself a
filterable category — Revit's internal V/G subcategories aren't filterable and aren't used.)

### 3. Wire the Filters picker
`catChip.Hierarchy = AutoFiltersSettings.CategorySubcategories;` — one line.

## Files
- `Source/Lemoine/Controls/Input/LemoineTagChipInput.xaml.cs`
- `Source/Tools/T01-AutoFilters/AutoFiltersSettings.cs`
- `Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`

## Notes
- Flat path fully preserved when `Hierarchy == null` (every other chip input).
- StaysOpen=true popup pattern unchanged; rows stay non-focusable so multi-add works.
- Post-change silent-failure scan before commit. Cannot compile on Linux (Windows-only).

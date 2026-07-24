# Plan — Rename "Place Dependent Views" → "Create Sheets" + add a "Place Views" mode

## Goal
1. **Rename** the tool's user-facing name from *Place Dependent Views* to **Create Sheets**.
2. **Add a third mode** — *Place Views* — that creates one sheet per selected source view and places **just that one view** on it (one source view per page).

The tool already has two modes:
- `DependentsPerParent` — one sheet per parent view, holding its dependent views.
- `CompositeOneSheet` — one sheet per source view, holding the view + its visible callouts/sections/elevations.

The new mode sits alongside these as a third option.

## Design decisions (need confirmation)
- **Which views are selectable in Place Views mode?** Proposed: all non-template graphical views
  except the non-placeable system/schedule/sheet types (Schedule, Legend, DrawingSheet,
  ProjectBrowser, SystemBrowser, Internal, Undefined) — the same placeable set used by Bulk Views.
- **Rename scope:** change the user-facing title (window, ribbon label, overview catalog) to
  *Create Sheets*. Keep internal file/namespace/handler names (`PlaceDependentViews*`) and AppStrings
  key prefixes unchanged to avoid large, risky mechanical churn. (Ribbon label becomes `Create\nSheets`.)
- **Trim in Place Views mode:** honour the existing Trim toggle (default on) — trims the single view's
  annotation crop just like dependents mode. The composite-source "never trim" rule stays composite-only.

## Files to change
| File | Change |
|------|--------|
| `Source/Tools/Sheets/PlaceDependentViews/PlaceDependentViewsEventHandler.cs` | Add `PlaceViewsMode.OneViewPerSheet`; in the loop, build `candidateIds = { view }`, pre-check already-placed (skip empty sheets), new done-log line. |
| `Source/Tools/Sheets/PlaceDependentViews/PlaceDependentViewsViewModel.cs` | Third mode option in S1, third candidate list + selection mirror, mode note, review/summary text, `Run` maps the new mode. |
| `Source/Tools/Sheets/PlaceDependentViews/PlaceDependentViewsWebTool.cs` | Parity: third mode token/option, third candidate list, review/summary. |
| `Source/Commands/Sheets/PlaceDependentViewsCommand.cs` | Collect the placeable-views candidate list; pass to VM and web tool. |
| `Strings/en/testing.placeDependentViews.json` | Retitle to "Create Sheets"; add `modePlaceViews`, its note, review/summary strings, `doneOneView` log line, `alreadyPlacedOneView`. |
| `Strings/en/ribbon.json` | Ribbon label → `Create\nSheets`, updated tip. |
| `Source/Framework/ToolsOverviewCatalog.cs` + `overview.*` strings | Overview tool name → "Create Sheets" (blurb mentions the new mode). |
| `Source/Web/toolsoverview.html` | Overview card name/blurb. |

## Constructor signature change
`PlaceDependentViewsViewModel` and `PlaceDependentViewsWebTool` gain a `List<ParentViewEntry>? placeableViews`
parameter (both call sites in the command updated).

## Silent-failure scan
Run after the change per CLAUDE.md, before committing.

## Branch
Already on the designated branch `claude/place-dependent-views-sheets-5w72bc` (off `main`).

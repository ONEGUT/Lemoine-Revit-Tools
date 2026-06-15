# Plan — Project-Browser Tree Picker + Standard Checkbox

Approved design: the `LemoineBrowserTreePicker` mockup (Dark Mono render, 2026-06-12).
Branch: `claude/beautiful-cori-q9anvp` (designated remote session branch).

## Goal

1. A reusable tree picker that mirrors the source document's Project Browser
   organization **exactly** (folder titles, nesting, order, dependent views
   nested under their primary), captured fresh every time a tool opens.
2. The mockup checkbox becomes the project-wide standard for **all** checkboxes.

## New files

| File | Purpose |
|---|---|
| `Source/Lemoine/LemoineBrowserTree.cs` | Revit-free DTOs: `LemoineBrowserTree` (Views root + Sheets root), `LemoineBrowserNode` (Title, optional leaf `Id`, Children). |
| `Source/Helpers/BrowserTreeCapture.cs` | Revit-side capture, called on the main thread in each launch command: `BrowserOrganization.GetCurrentBrowserOrganization(doc)` / `GetCurrentBrowserOrganizationForSheets(doc)` + `GetFolderItems(id)` per view/sheet. Nests dependent views under their primary view (as the browser does). Root titles copied from the org (`Views (Discipline)`, `Sheets (all sheets)`). |
| `Source/Lemoine/Controls/Input/LemoineBrowserTreePicker.xaml(.cs)` | The picker: filter box (flattens to matches, keeps ancestors), expand/collapse-all, tri-state folder checkboxes, AccentDim selected-row fill, count badges on collapsed folders, footer with count pill + Clear. `SingleSelect` mode supported. `SetTree(tree, eligibleIds)` prunes to each tool's eligible leaves; `SelectionChanged` emits selected element ids (`long`). |

## Modified files

| File | Change |
|---|---|
| `Source/Lemoine/LemoineControlStyles.cs` | Replace `MakeCheckBoxStyle()` with a full ControlTemplate (XamlReader.Parse): 13×13 box, `LemoineRadius_SM`, `LemoineBorder` stroke; checked = `LemoineAccent` fill + `LemoineKnobOn` check; indeterminate = `LemoineAccentDim` fill + `LemoineAccent` border + accent dash. Injected style ⇒ every `CheckBox` in the project (incl. `LemoineMultiSelectTabs` rows) adopts it with no per-call-site edits. |
| 8 tool ViewModels + their launch commands | Swap the view/sheet selection step from `LemoineMultiSelectTabs` onto `LemoineBrowserTreePicker`; command captures the browser tree on the main thread and passes it into the ViewModel. Non-view/sheet steps (templates, filters, levels, docs) keep their current controls. |

Tools swapped: Bulk Export (views+sheets), Views by Template (views step),
Bulk Duplicate Views, Bulk Rename (views+sheets), Replicate Dependent Views
(single-select source + multi targets), Apply Filters to Views (views step),
Clash Finder (views step), Place Dependent Views (parent views).
Not swapped: Bulk Views by Level (picks levels/docs, not views), Create Sheets
(picks levels/rooms/scope boxes).

## Behaviour notes

- Capture runs in `IExternalCommand.Execute()` (main thread, doc in hand) —
  same snapshot-DTO discipline every command already uses.
- Folder checkbox = tri-state over all selectable descendants; checking selects
  all. A view-with-dependents row's own checkbox selects only itself.
- Picker is in-page: its scroller bubbles at limits via
  `LemoineControlStyles.WireBubblingScroll` (not self-contained popup rules).
- Cannot build on Linux — changes follow the Known Compile Error Patterns
  checklist; user builds on Windows.

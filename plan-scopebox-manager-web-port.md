# Plan — Finish web UI: Scope Box Manager port (+ scope confirmation)

Continuation of the WebView2 migration on `claude/webview2-testing-menu-9i6jz2`.
Goal: "finish all tools/UI except the auto filters and the legend creator."

## Scope after audit

- **Step-flow tools (34/34):** already ported to `IWebTool`, gated behind the Web UI flag. Done.
- **Push Coordinates `PublishReplace` parity:** fixed + committed (`b10b6bd`).
- **Color Picker (standalone `ColorPickerWindow`):** **no web port needed.** It is only ever
  used as an internal WPF helper (`PickColor` / `BuildColorPickerSwatch`) by CeilingHeatmap,
  Discover, ToolGroups, and Filters — all already replaced by the inline `WebInput.Color`
  input in their web ports (or are the excluded auto-filters surface). The inline colour input
  already covers every use, exactly as `web-migration-status.md` hypothesised.
- **Scope Box Manager (`ScopeBoxManagerWindow`, ~1400 lines):** the one real remaining port.
- **Excluded (per user):** Auto Filters (Settings→Filters tab + `FiltersSettingsWindow`) and
  Legend Creator (`LegendSettingsWindow` / lane grid).

## Scope Box Manager — port design

Reuses the Revit-side scan + action handlers **unchanged** (`ScopeBoxManagerScanHandler`,
`ScopeBoxManagerRunHandler`, `App.ScopeBoxManager*Event`). Only a view layer is added,
following the proven bespoke-window pattern (`WebWindowBase` + Clash Definitions).

New files:
| File | Role |
|---|---|
| `Source/Framework/Web/WebScopeBoxManager.cs` | Revit-free model: holds the `ManagerScanResult` + selection/filter/checked state, builds the `init` payload (sidebar, editor, overlay option-data, labels from `AppStrings`), resolves bulk-rename names via `TokenResolver`. |
| `Source/Framework/Web/WebScopeBoxManagerWindow.cs` | `WebWindowBase` subclass: raises the scan + run `ExternalEvent`s, marshals results back with `RunOnUiThread` → `SendInit`, maps page actions to run-handler configs. |
| `Source/Web/scopeboxmanager.html` | Page shell (+ standalone demo block). |
| `Source/Web/lib/scopeboxmanager.js` | UI: toolbar, sidebar (header + All/Used/Unused chips + box rows + bulk rename/delete-unused), main editor (inline-edit name, size, duplicate/delete/bind/split, views + datums sections), and the 6 in-page modal overlays (assign views = `browserTree`, assign datums = `multiSelectTabs`, bind sides = 4 `singleSelect`, split, rename = `tokenInput`, delete confirm). Status pill auto-hides client-side. |

Edited:
| File | Change |
|---|---|
| `Source/Commands/Views/ScopeBoxes/ScopeBoxManagerCommand.cs` | Add the `WebUiSettings.Instance.Enabled` branch → open `WebScopeBoxManagerWindow` on `WebUiThread`; keep the WPF window as the flag-off fallback (rule R25). |

Strings: reuse the existing `scopeBoxes.manager.*` JSON (toolbar/side/main/overlay/status) —
the web `labels` dict is built from those keys. No new tool strings expected.

CSS: add scope-box-manager classes to `lib/lemoine.css` (reuse the `l-cd-*` master/detail
idiom where possible).

## Verification
Linux cannot build this repo — Windows build + click-through is the confirmation step
(logged to `plan-webview2-ui-migration.md` §5). All changes are read-verified against the
WPF original and the established web pattern before committing.

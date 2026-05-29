# Plan: Auto Filters Menu Redesign

## Goal

Reorganise `FiltersSettingsWindow` so that trade management lives in a
permanent right-hand sidebar instead of a popup dropdown in the content header,
and simplify the toolbar to a single centred "Create Filters" button.

---

## New layout (top-down / left-to-right)

```
Window  1100 × 700
└── Border _outerBorder
    └── Grid _root  rows: [38px toolbar | * content | 42px footer]

        Row 0 — Toolbar (simplified)
          Grid  cols: [* | Auto | *]
            Col 0:  LemoineTitleBar (drag handle + icon + "Auto Filters")
            Col 1:  "Create Filters" button  ← centred
            Col 2:  "×" close button, right-aligned

        Row 1 — Content
          Grid  cols: [* min150 rule-list
                       | 5px GridSplitter
                       | 280min rule-editor
                       | 1px border separator
                       | 220px trades-sidebar]

            Col 0:  DockPanel (rule list)
                      Bottom: Border addRuleOuter ("＋ Add Rule")
                      Fill:   ScrollViewer → StackPanel _fRuleListPanel
                      (trade switcher header REMOVED from here)

            Col 1:  GridSplitter (existing, between list and editor)

            Col 2:  Border _fEditorBorder (rule editor — unchanged)

            Col 3:  1px visual divider (Border, no interaction)

            Col 4:  Border _fTradesSidebar  (new)
                      DockPanel LastChildFill=true
                        Top:   Templates dropdown button (full-width pill)
                        Top:   1px separator
                        Bottom: Border addTradeOuter ("＋ Add Trade")
                        Fill:  ScrollViewer → StackPanel _fTradeListPanel
                                 (one row per trade — see below)

        Row 2 — Footer (unchanged)
          DockPanel: [status text | Apply btn | Close btn]
```

### Trade row (inside _fTradeListPanel)

```
Border rowBorder  padding 10,7,10,7
└── Grid  cols: [Auto dot | * label | Auto edit | Auto copy | Auto delete]
      Col 0: Border (color swatch, 10×10, rounded)
      Col 1: TextBlock (trade label)
      Col 2: pencil edit button  — shown on hover or when active
      Col 3: copy/duplicate button
      Col 4: trash delete button (with confirm popup)
```

Active trade row: `LemoineAccentDim` background, `LemoineAccent` left border (2px).  
Hover: `LemoineRaised` background.

---

## Files changed

| File | Change |
|------|--------|
| `FiltersSettingsWindow.xaml.cs` | • Add `_fTradesSidebar` and `_fTradeListPanel` fields<br>• Remove `_fTradeSwitcherBorder` field<br>• Rewrite `BuildToolbar()` — centred Create Filters btn<br>• Update `BuildFiltersContent()` — 5-column Grid, remove trade header from left dock, add sidebar column |
| `GlobalSettingsWindow.Filters.cs` | • Remove `FRefreshTradeSwitcher()` → replace with `FRefreshTradesSidebar()`<br>• Move Templates popup trigger to sidebar button<br>• Update all callers of `FRefreshTradeSwitcher` → `FRefreshTradesSidebar`<br>• Remove `ShowTradeManagementPopover` (was already removed, just confirm)<br>• ShowAddTradeForm anchor changes to sidebar |

---

## Behaviour contract (unchanged)

- Selecting a trade row in the sidebar fires `_fActiveTradeId = id` then
  `FRefreshRuleList()` + `FRefreshRuleEditor()` (same as the popup did).
- Edit/copy/delete on a trade row call the same logic as the existing popup
  row buttons.
- `ShowTradeEditPopup`, `ShowAddTradeForm`, `BuildTrashConfirmButton` are
  reused verbatim — only their anchor element changes.
- Drag-and-drop rule reorder is unaffected (left panel unchanged structurally).

---

## What is NOT changed

- Rule list pills and all their interactions (drag-drop, multi-select, etc.)
- Rule editor panels (identity, filter logic, override style, appearance)
- Footer (Apply / Close / status flash)
- Templates popup content (`ShowTemplatesPopup`)
- All event handlers, data model, and settings persistence

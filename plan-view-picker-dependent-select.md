# Plan — Right-click to select dependents in the view picker

## Problem
In `LemoineBrowserTreePicker`, a parent view that owns dependent views is an
*eligible leaf with children*. Its checkbox already toggles **only the parent**
(`SetLeaf`), never the dependents — which is the desired default. What's missing
is a quick gesture to grab all of a parent's dependents without expanding the
node and checking each dependent by hand.

## Decision (confirmed with user)
- **Gesture:** immediate right-click on any row (no context menu).
- **Scope:** selects **only the dependents** (descendant leaves), leaving the
  clicked node itself unchecked. Additive to the current selection; no-op in
  `SingleSelect` mode.

## Changes
`Source/Lemoine/Controls/Input/LemoineBrowserTreePicker.xaml.cs`
1. Add `SelectDependents(Node n)` — unions every id in `n.LeafIds` except the
   node's own id into `_selected`, then `AfterSelectionChange()`. No-op when
   `SingleSelect` or when nothing changed.
2. In `BuildRow`, when the node has dependents and not `SingleSelect`:
   - wire `row.MouseRightButtonDown` → `SelectDependents(n)` (`e.Handled = true`).
   - set a `ToolTip` ("Right-click to select all dependent views") for
     discoverability of the otherwise-invisible gesture.

No data-model, capture, or ViewModel changes. `SelectionChanged` contract
unchanged.

## Not building Linux is unsupported for this project — verify on Windows.

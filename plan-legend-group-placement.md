# Plan — Legend Creator: Option A group placement (single live insertion marker)

## Goal
Replace the legend center section's thin, invisible, overlapping drop-bar system for
**placing groups** with **one live insertion marker over an always-visible row-lane grid**.
Scope is *group placement only* — block drag/drop inside a group card is a separate,
working live-snap system and is left untouched.

## Why (current problems being fixed)
1. All group drop targets are transparent slivers (18px edges, 8px between-rows, 4px→14px
   in-row slots) — invisible until a drag starts.
2. Two overlapping placement systems: builder edge/between bars (`WireExternalDropBar`) and
   row-internal slots (`MakeGroupSlot` → `GroupDropInRowRequested`) fight for the same intent.
3. Left/right edges guess the target row via `FindNearestRow(dropY)`.
4. Hovering a slot flips a 4px column to 14px, reflowing the row and shifting the aim target.
5. Bespoke grip/sliver reorder — violates the house "use `LemoineListReorder`/`LemoineDragGhost`"
   guidance.

## Target model (Option A)
- Rows render as **legible lanes at rest** (visible thin divider between rows; card gutters are
  real fixed margins, not interactive slivers).
- During a group drag (whole-group `Group` payload OR palette `PaletteCategory` payload), the
  whole rows area is **one drop surface**. An **overlay marker** (not an inserted child) snaps to
  the single nearest landing spot:
  - **In-row**: a vertical accent bar in the nearest card gutter (left half of a card = before,
    right half = after).
  - **New row**: a horizontal full-width accent lane when the cursor is in the band between two
    rows, above the first, or below the last.
- Because the marker is an overlay (`IsHitTestVisible = false`), the cards never reflow while
  aiming (fixes problem #4).
- Empty legend → whole canvas is a single "drop to create the first row" target.

## Files to change
1. **`Source/Lemoine/Controls/Legend/LemoineLegendBuilder.xaml.cs`** (primary)
   - `BuildCanvasGrid`: delete the four edge drop bars (`_topDropBar`/`_bottomDropBar`/
     `_leftDropBar`/`_rightDropBar`) and the inner 3-column grid. Replace with a `Grid` holding:
     a `ScrollViewer` (rows stack) + a sibling overlay `Canvas` (`_markerLayer`,
     `IsHitTestVisible=false`) drawn on top for the single insertion marker.
   - Make the rows container the single `AllowDrop` surface. Add `DragOver`/`DragLeave`/`Drop`
     handlers that:
     - `ComputeGroupDropTarget(Point)` → `struct { Kind (InRow|NewRowAt); int rowIndex; int groupIndex; }`
       computed from live row/card bounds via `TranslatePoint`.
     - Position/show the overlay marker (vertical bar for InRow, horizontal lane for NewRowAt).
     - On drop, apply via existing buffer mutations (`BuildGroupFromPayload`, `TakeGroup`,
       insert into `row.Groups` or a new `LegendRowConfig`), then `OnEdited()`.
   - Delete `WireExternalDropBar`, `DropBarRole`, `FindNearestRow`, the between-row bar creation
     in `RebuildRows`, and the `_betweenRowBars` list.
   - `RebuildRows`: drop the 8px between-row bars; add a thin **non-interactive** divider between
     rows so the lane structure reads at rest. Keep the "Add New Group" affordance.
   - Keep `LegendDragSession` Begin/End (drives the `LemoineDragGhost` and lets the new-row lanes
     show a faint hint while a compatible drag is in flight).
2. **`Source/Lemoine/Controls/Legend/LemoineLegendRow.xaml.cs`**
   - `BuildAll`: remove the 4px slot columns and `MakeGroupSlot` drop wiring. Lay group cards in a
     horizontal grid/stack with fixed gutter margins (Star columns kept so cards fill the row).
   - Remove `GroupDropInRowRequested` and `MakeGroupSlot`/`IsGroupAccepting` (group placement now
     owned entirely by the builder overlay). The header drag source (`GroupDragInitiated`) stays.
   - Block-row drop wiring inside cards is unchanged.
3. **`LemoineLegendBuilder.xaml` / `LemoineLegendRow.xaml`** — unchanged thin shells; the overlay
   canvas is added in code-behind.

## Out of scope (untouched)
- Block placement inside a group (`LemoineLegendGroupCard` live-snap, cross-group block moves).
- Palette, templates popup, footer counts, preview, settings/sizing.
- (Option B's explicit ◀▶▲▼ move-cluster is deferred — can be layered on later as an
  accessibility/precision path without conflicting with this work.)

## Risks / watch-items
- Overlay marker must be `IsHitTestVisible=false` so the drop surface beneath still receives
  events (same discipline as the existing cross-insert placeholder).
- `ComputeGroupDropTarget` reads live bounds via `TranslatePoint` against the rows ScrollViewer —
  account for scroll offset.
- New-row band threshold must be generous enough to hit but not swallow the in-row gutters.
- Keep `DropEmptyRows()` so dragging the last group out of a row still collapses it.
- Silent-failure scan + `// ⚠` audit before commit, per CLAUDE.md.

## Build/test note
Windows-only build (UseWPF + net48). Cannot compile on Linux — will be verified on Windows.

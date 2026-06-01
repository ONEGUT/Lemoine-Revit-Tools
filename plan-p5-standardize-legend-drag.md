# Plan — P5: Standardize Legend Drag Interactions

Goal: make every legend drag source feel identical — same arming, same ghost, same
cursor — by centralizing the drag scaffolding into shared helpers, WITHOUT touching the
drop/payload logic (which #33 just refactored and which works).

Base branch: `claude/happy-dijkstra-OzHjz`.

---

## Current state (post-#33 audit)

| Concern | Today | Verdict |
|---|---|---|
| Drag threshold | All sites use `SystemParameters.Minimum{H,V}DragDistance` | ✅ already consistent |
| Arming | BlockRow + Palette(trade pill, filter rows) hand-roll down/move threshold; Palette border + LegendRow go straight to `DoDragDrop`; GroupCard has its own flag flow | ❌ inconsistent |
| Ghost | Only `LemoineLegendGroupCard` (Popup + Show/Update/Hide + `row.Opacity=0`) | ❌ only one control |
| Cursor | Mix of `Cursors.Hand` and `Cursors.SizeAll` | ❌ inconsistent |

Drag sources: `LemoineLegendPalette` (filter row → new group; trade pill → group),
`LemoineLegendGroupCard` (block row reorder/move), `LemoineLegendRow` (card → slot).
Drop targets unchanged.

---

## Proposed changes

### 1. Shared drag-arm helper (low risk, behavior-preserving)
Add `LemoineMotion.WireDragArm(FrameworkElement source, Action<Point> onDragStart)`:
- `PreviewMouseLeftButtonDown` → record start point.
- `PreviewMouseMove` → if left button pressed AND moved past
  `SystemParameters.Minimum{H,V}DragDistance`, fire `onDragStart(startPoint)` once.
- `PreviewMouseLeftButtonUp` / leave → disarm.

Replace the duplicated arming blocks in BlockRow + Palette (×2) + GroupCard with this. The
`onDragStart` callback keeps each control's existing `DoDragDrop` + payload exactly as-is.

### 2. Shared drag ghost (medium risk — extract GroupCard's, reuse everywhere)
Extract GroupCard's Popup ghost into a reusable helper, e.g.
`LemoineDragGhost` (a small class) or `LemoineMotion.BeginDragGhost(visual)/UpdateDragGhost()/
EndDragGhost()`:
- Renders a faded snapshot of the dragged element following the cursor.
- Source element dims (`Opacity` lowered) during the drag, restored after.
Apply to the palette filter row + trade pill drags (which currently show no ghost) so every
drag looks the same as the group-card reorder.

### 3. Consistent cursors
- Reorder/move handles (group card header, block row) → `Cursors.SizeAll`.
- Click-or-drag affordances (palette rows, trade pill) → `Cursors.Hand` at rest.
(Keep it light — only where it's currently wrong.)

---

## Sequencing & risk

- **P5a** — add `WireDragArm`, apply to all arming sites. Pure consolidation; drop/payload
  logic untouched. Verify drags still start correctly.
- **P5b** — extract + reuse the drag ghost; apply to palette drags. Higher risk (touches the
  DoDragDrop flow shape); do carefully, keep GroupCard's behavior identical.
- **P5c** — cursor pass.

Each commit is self-contained and Windows-verifiable. Drop targets, payloads, and the #33
refactor's data flow are NOT changed — only the arming/ghost/cursor layer.

Open question for you: do P5 in full (a→c), or just **P5a (unify arming)** now and treat the
ghost/cursor polish as optional?

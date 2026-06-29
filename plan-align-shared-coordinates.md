# Plan — Align Shared Coordinates (Grid Intersection + Level)

## Goal

A new Lemoine tool that:

1. In the **host** file, moves the **Project Base Point and/or Survey Point** (user picks which, in the tool) to a **grid intersection** (two grids picked by name) at a **chosen level's elevation**.
2. **Aligns every loaded linked file** to the host so they all share the same coordinate system — by **matching grid names** between host and link, **translating and rotating** each link so its same-named grid intersection coincides with the host's, then **publishing the host's shared coordinates** into the link so the alignment persists as shared coordinates.

---

## Important reconciliation (please confirm)

You chose **"Acquire/Publish shared coords"** *and* **"match grid names + translate+rotate."** These are not actually alternatives — in Revit they must be combined:

- **Publish/Acquire Coordinates does not move geometry.** It only records a shared-coordinate relationship at the link's *current* position. If a link is currently misaligned, Publish alone will not line up its grids.
- To align by grid name **with rotation**, the tool must **reposition each link instance** (move + rotate its placement) so its grids land on the host's, **then** call `Document.PublishCoordinates(linkInstanceId)` to bake that into the link as shared coordinates.

So the workflow **does move the link instances** as the geometric step, with Publish as the persistence step. If you instead want *no instance movement* (only stamp shared coordinates at links' current positions, accepting whatever misalignment exists), say so — that removes the grid-matching/rotation entirely. **My recommendation: the combined move-then-publish workflow**, since that's what actually guarantees alignment.

---

## Key Revit API facts (verified against `libs/RevitAPI.dll`)

- `Document.PublishCoordinates(ElementId linkInstanceId)` and `Document.AcquireCoordinates(...)` exist. **They must be called OUTSIDE a transaction** (they manage their own and throw if one is open). The actual write-back of shared coords into the link file happens when the host is saved (governed by a save-shared-coordinates callback) — the tool will note this in the run log.
- `BasePoint.GetProjectBasePoint(doc)` and `BasePoint.GetSurveyPoint(doc)` return the point elements. Their position is moved via the position parameters (`BASEPOINT_EASTWEST_PARAM`, `BASEPOINT_NORTHSOUTH_PARAM`, `BASEPOINT_ELEVATION_PARAM`, and `BASEPOINT_ANGLETON_PARAM` for true-north angle) or `ElementTransformUtils.MoveElement`.
- **Clip-state subtlety:** moving the *unclipped* Survey Point relocates the shared-coordinate origin; moving the *clipped* point moves only the marker. The tool will set clip state deliberately for the chosen behaviour and log it. **This needs confirming on a Windows/Revit plot — it cannot be tested on Linux.**
- Link instances must be **unpinned** before move/rotate (`IsPinned`) — unpin, transform, re-pin.
- Grid intersection is computed from each grid's `Curve` (XY); parallel/non-intersecting named grids → **skip-and-log** that link.
- `RevitLinkInstance.GetTotalTransform()` maps link-internal coords → host world (existing pattern in `CopyLinearSource`).

---

## UX / Step flow (StepFlowWindow, `ILemoineTool`)

Following the `CopyGridsViewModel` template exactly (ViewModel + static `IExternalEventHandler` run handler + `ExternalEvent`, registered in `App.cs`).

**Step 1 — Host Reference**
- `LemoineSingleSelect` × 2: **Grid 1** and **Grid 2** (host grid names).
- `LemoineSingleSelect`: **Level** (host levels).
- Toggle(s) for which host point to move: **Survey Point** / **Project Base Point** (both on by default) — `LemoineToggleSwitches`.
- Valid when two *distinct, intersecting* grids + a level are chosen.

**Step 2 — Links to Align**
- List every loaded `RevitLinkInstance`. For each, show whether both named grids were found ("matched" vs "grid 'A' not found — will skip").
- Checkbox per link to include (default: all matched links).
- Toggle: **Rotate to align orientation** (default on).
- Toggle: **Publish shared coordinates** (default on — persists the alignment).

**Step 3 — Review & Run** (`ILemoineReviewable`)
- Summary of host point(s), grid pair, level, and N links to align. Run button.

---

## Run handler logic (`AlignCoordinatesRunHandler`)

1. Resolve host Grid 1 & Grid 2 by name → intersection point `P_host` (XY) + Level elevation (Z). Record host Grid-1 direction `d_host`.
2. **Transaction 1** (host point move): set Survey Point and/or Project Base Point to `P_host` per the toggles, handling clip state; commit.
3. **Transaction 2** (link instances): for each selected, matched link:
   - Find the two same-named grids in the link doc; compute their intersection in link coords, transform by `GetTotalTransform()` → world `P_link`; compute link Grid-1 world direction `d_link`.
   - If rotation on: angle = `atan2(d_host) − atan2(d_link)`; unpin and `ElementTransformUtils.RotateElement` about the vertical axis through `P_link`.
   - Recompute `P_link` after rotation; `MoveElement` by `P_host − P_link`; re-pin.
   - Log matched/skipped (missing grid, parallel grids, pinned-and-locked, etc.).
   - Commit.
4. **Outside any transaction:** `doc.PublishCoordinates(linkInstanceId)` for each successfully aligned link (if Publish toggle on). Log that shared coords are written on host save.
5. Cooperative cancellation (`LemoineRun.CancelRequested`) at the per-link progress point; partial work preserved. ~5% progress cadence per CLAUDE.md.

All Revit failures routed through `LemoineFailureCapture` / `LemoineRunLog`; every swallowed exception via `LemoineLog`. Zero-match runs say so explicitly ("No links matched both grids").

---

## Second tool — Compare Grids Across Links (grid audit)

A separate button (same Coordination panel) that, **once files are aligned**, audits grid lines across the host + every loaded link to flag inconsistencies. **Read-only — no transaction, no model changes.**

**Logic (`CompareGridsRunHandler`, single-shot scan):**
1. Collect grids from the host and every loaded `RevitLinkInstance`, transforming each grid's `Curve` to **host world coordinates** via `GetTotalTransform()` (so the comparison is in a common frame — valid once links are aligned/share coordinates).
2. Group by grid **name** (case-insensitive). For each name, compare across files:
   - **Missing:** name present in some files but absent in others → "Grid 'A' missing in [Link X]".
   - **Moved/rotated:** same name, but world line position or direction differs beyond tolerance → "Grid 'A' differs in [Link X]: offset 1' 2", angle 0.4°".
   - **Extra/unmatched:** a name only one file has → "Grid 'Z' only in [Link X]".
3. Report a per-grid, per-file discrepancy list in the run log and a `ILemoineReviewable` summary ("N grids consistent, M discrepancies"). A clean result says so explicitly ("All 24 grids consistent across 5 files").

Tolerances: a small length tolerance (e.g. ~1/16") for endpoint/offset and a small angular tolerance for direction; both exposed as numeric inputs (`LemoineInlineStepper`) with sensible defaults. Step 1 picks which files to include (default all loaded links + host); Step 2 is Review & Run.

**Note:** comparison is meaningful only after alignment / shared coordinates; the tool will state this and still run (it just compares current world positions).

## Files

**New — Align tool**
- `Source/Tools/T08-Coordinates/AlignCoordinatesModels.cs` — link/grid DTOs (shared with Compare).
- `Source/Tools/T08-Coordinates/AlignCoordinatesViewModel.cs` — `ILemoineTool`, `ILemoineReviewable`, `ILemoineToolCleanup`.
- `Source/Tools/T08-Coordinates/AlignCoordinatesRunHandler.cs` — `IExternalEventHandler`.
- `Source/Commands/T08-Coordinates/AlignCoordinatesCommand.cs` — opens `StepFlowWindow`, collects host grids/levels/links on the Revit thread.

**New — Compare tool**
- `Source/Tools/T08-Coordinates/CompareGridsViewModel.cs` — `ILemoineTool`, `ILemoineReviewable`, `ILemoineToolCleanup`.
- `Source/Tools/T08-Coordinates/CompareGridsRunHandler.cs` — `IExternalEventHandler` (read-only scan).
- `Source/Commands/T08-Coordinates/CompareGridsCommand.cs`.

**Edited**
- `Source/App.cs` — static handlers + `ExternalEvent`s for both tools (near the other handlers, ~line 123), and **two ribbon buttons** in a new **"Coordination"** panel: **Align Coordinates** and **Compare Grids**.

---

## Constraints / notes

- **Cannot be built or tested on Linux** (`UseWPF` + `net48`, per CLAUDE.md). I'll deliver complete code; you build/test in Revit on Windows. The clip-state and Publish-on-save behaviours specifically need a Windows verification pass.
- WPF UI work will follow the `/revit-navisworks-ui` skill before any control code is written, and a themed mockup image will be produced for approval first (per CLAUDE.md).
- One logical change, one branch, one (optional) PR only if you ask.

---

## Open questions before coding

1. Confirm the **move-then-publish** combined workflow above (vs. publish-only with no instance movement).
2. Ribbon placement: **new "Coordination" panel** (recommended) or append to "Copy from Link"?
3. **Which branch to base from?** (CLAUDE.md requires explicit approval before I touch any branch or write code.)

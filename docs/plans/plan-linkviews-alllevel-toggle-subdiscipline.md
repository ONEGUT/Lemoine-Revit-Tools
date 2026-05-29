# Plan: Link Views — "Show All Levels" Toggle + Sub Discipline Input

## What this plan covers

Two additions to the Link Views tools (Level and Discipline):

1. **Show All Levels toggle** — a fallback toggle in S2 of the Level tool that switches the level list from "levels with placed rooms only" to "all levels in the linked models," for use when room-based scanning finds nothing.
2. **Sub Discipline field per view type** — text inputs that let the user set the Revit "Sub Discipline" parameter on each created view at creation time. Applied to both the Level tool (one input per view type: 3D, FP, RCP) and the Discipline tool (one shared input).

---

## Files to change

| File | Change |
|------|--------|
| `Source/Tools/T03-LinkViews/LinkViewsLevelPhase1Handler.cs` | Always return all host levels; levels with no rooms get `RoomCount = 0, DocumentName = "(No rooms)"` |
| `Source/Tools/T03-LinkViews/LinkViewsLevelViewModel.cs` | Add `_showAllLevels` toggle in `PopulateS2()`; filter display by room count; add Sub Discipline inputs below view-type toggles; wire to RunHandler |
| `Source/Tools/T03-LinkViews/LinkViewsLevelRunHandler.cs` | Add `SubDisc3D/FP/RCP` properties; handle zero-room levels (create uncropped views); set Sub Discipline parameter on each created view |
| `Source/Tools/T03-LinkViews/LinkViewsDisciplineViewModel.cs` | Add sub-discipline text input in S1; wire to RunHandler |
| `Source/Tools/T03-LinkViews/LinkViewsDisciplineRunHandler.cs` | Add `SubDisc` property; set Sub Discipline parameter on each created view |

---

## Detailed design

### 1 · Phase1Handler — always return all levels

**Current:** scans rooms per doc per level, only produces results for levels that have rooms.

**New:** additionally sweeps all host `Level` elements; any level not already covered by a room result gets appended as a single entry with `RoomCount = 0` and `DocumentName = "(No rooms)"`.

This keeps the full result set in `_scannedLevels` without requiring a second scan.

---

### 2 · S2 "Show All Levels" toggle (Level tool)

Layout after the change:

```
[Levels MultiSelectTabs]       ← existing, filtered by RoomCount > 0 by default
[8px spacer]
[Toggle: Show all levels in linked models  (●──)]   ← new, default OFF
[12px spacer]
[VIEW TYPES TO CREATE label]   ← existing
[LemoineToggleSwitches 3D/FP/RCP]
[8px spacer]
[SUB DISCIPLINE label]         ← new
[  3D  [text input]]           ← new, visible only if _create3D
[  FP  [text input]]           ← new, visible only if _createFP
[  RCP [text input]]           ← new, visible only if _createRCP
```

Toggle behaviour:
- Off (default): filter `_scannedLevels` to `RoomCount > 0` before building the `groups` dict passed to `LemoineMultiSelectTabs`.
- On: pass all `_scannedLevels` including `RoomCount = 0` entries. These appear under the "(No rooms)" group.
- Toggling rebuilds the `LemoineMultiSelectTabs` in-place by calling `PopulateS2()` again.
- The toggle itself is built as a single-item `LemoineToggleSwitches` (same control used for view types).

"No levels" message logic:
- `_scannedLevels` empty → "No levels found in the selected documents." (truly empty model)
- All levels have `RoomCount = 0` and toggle is OFF → "No levels with placed rooms found. Enable 'Show all levels' below to select from all model levels."

---

### 3 · RunHandler — handle zero-room levels

When `_showAllLevels = true` and the user selects a level with no rooms:
- `roomsByLevel.TryGetValue(lname, ...)` returns false (no rooms for that level).
- **New behaviour:** instead of `continue`, create a single uncropped view per requested type.
  - **3D:** `Create3d(doc, n, vft3d.Id)` with no section box set.
  - **FP / RCP:** `ViewPlan.Create(doc, vftFP.Id, lvl.Id)`, rename, `CropBoxActive = false`. No `SetPlanCrop` call.
- Log with `"pass"` as normal.

---

### 4 · Sub Discipline fields

**Level tool (3 inputs):**
- `_subDisc3D`, `_subDiscFP`, `_subDiscRCP` — `string` fields, default `""`.
- Appear below the view-type toggles. Each input row: `[type label 40px] [TextBox 180px]`.
- Visibility of each row tracks the corresponding `_create*` boolean; updates on `StateChanged`.
- In `Run()`, set `_runHandler.SubDisc3D/FP/RCP` before raising the event.
- In `RunHandler`, after creating each view: `view.LookupParameter("Sub Discipline")?.Set(value)` (no-op if param not present or value is empty).

**Discipline tool (1 input):**
- Single `_subDisc` field.
- Added as a compact text input at the bottom of the S1 content, below the discipline chip scroll, with a small "SUB DISCIPLINE" label above.
- In `Run()`, set `_runHandler.SubDisc`.
- In `DisciplineRunHandler`, applied to each view created (combined + per-link).

---

## What is NOT changing

- S1, S3, S4 of the Level tool — no changes.
- Phase 1 scan logic for rooms — unchanged, only the output filtering step changes.
- Settings files, print-set logic, naming logic — unchanged.
- No new files created.

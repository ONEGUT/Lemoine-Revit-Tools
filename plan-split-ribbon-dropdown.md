# Plan: Split Ribbon — Dropdown Button + Icons + Renames

## What the user wants

1. Consolidate the three stacked split buttons into one large **PulldownButton** (dropdown).
2. Add **icons** for each sub-button (must not duplicate icons already in use).
3. Rename: **"Split by Level" → "Split by Levels"** and **"Split by Grid" → "Split by Grid Lines"**.
4. (Deferred) Expose all categories — waiting for categories-list branch to merge into main.
5. Identify improvements and additional similar tools.

---

## Current State

**Panel:** `T04  Modify`  
**Layout:** `AddStackedItems` with three small buttons side-by-side

| Button ID        | Label          | Icon | Command              |
|------------------|----------------|------|----------------------|
| LT_SplitByLevel  | Split by Level | none | SplitByLevelCommand  |
| LT_SplitByGrid   | Split by Grid  | none | SplitByGridCommand   |
| LT_SplitByCell   | Split by Cell  | none | SplitByCellCommand   |

**Icons already in use elsewhere in the ribbon:**

| Glyph    | Used by                          |
|----------|----------------------------------|
| `` | T01 Legend Creation              |
| `` | T02 Ceiling Heatmap              |
| `` | T02 Ceiling Grids pulldown + sub |
| `` | T02 Project Grids sub-button     |
| `` | T02 Reproject Grids sub-button   |
| `` | T03 Link Views by Level          |
| `` | T04 Extend Walls                 |
| `` | Testing CoordSet                 |
| `` | Settings UI Debug                |

---

## Planned Changes

### 1. App.cs — Ribbon Registration

Replace `AddStackedItems` with a `PulldownButton`:

```csharp
// ── Split Elements ─────────────────────────────────────────────────
var splitPulldownData = new PulldownButtonData("LT_SplitElements", "Split\nElements")
{
    ToolTip    = "Split elements at level elevations, grid planes, or a regular cell grid.",
    LargeImage = CreateGlyphBitmap(32, ""),   // Cut (scissors)
    Image      = CreateGlyphBitmap(16, ""),
};
var splitBtn = modifyPanel.AddItem(splitPulldownData) as PulldownButton;

splitBtn?.AddPushButton(new PushButtonData(
    "LT_SplitByLevel", "Split by Levels", dll, "LemoineTools.Commands.SplitByLevelCommand")
{
    ToolTip    = "Split walls, columns, and MEP curves at selected level elevations.",
    LargeImage = CreateGlyphBitmap(32, ""),   // RowsGroup (horizontal bands = level spans)
    Image      = CreateGlyphBitmap(16, ""),
});

splitBtn?.AddPushButton(new PushButtonData(
    "LT_SplitByGrid", "Split by Grid Lines", dll, "LemoineTools.Commands.SplitByGridCommand")
{
    ToolTip    = "Split walls and MEP curves at selected grid plane intersections.",
    LargeImage = CreateGlyphBitmap(32, ""),   // GridView
    Image      = CreateGlyphBitmap(16, ""),
});

splitBtn?.AddPushButton(new PushButtonData(
    "LT_SplitByCell", "Split by Cell", dll, "LemoineTools.Commands.SplitByCellCommand")
{
    ToolTip    = "Split floors, ceilings, and filled regions into a regular grid of cells.",
    LargeImage = CreateGlyphBitmap(32, ""),   // Table (cell grid)
    Image      = CreateGlyphBitmap(16, ""),
});
```

### 2. ViewModel Title Renames

| File                          | Property `Title`               |
|-------------------------------|--------------------------------|
| `SplitByLevelViewModel.cs`    | `"Split Elements by Levels"`   |
| `SplitByGridViewModel.cs`     | `"Split Elements by Grid Lines"` |

`SplitByCellViewModel.cs` title stays as is.

### 3. Categories (Deferred)

No changes to category logic. Waiting for categories-list branch to merge before expanding the picker.

---

## Proposed Improvements (for discussion)

### A — Element count badges in Step 1
Show `(N elements)` alongside each category label so the user knows immediately how many elements exist in the project for each selection.

### B — Active view filter
A toggle in Step 1: "Limit to active view only." Filters the element collector to only elements visible/present in the current Revit view. Avoids accidentally splitting all MEP risers in a project when you only mean to touch the current floor.

### C — Select All / Deselect All for Step 2 (levels or grids)
"All Levels" / "Clear" buttons in the level and grid picker step. Currently these require clicking every row individually.

### D — New command: Split by Reference Plane
A fourth entry in the dropdown. Reference planes are already used as organizational cut lines in many Revit workflows and would complement Split by Grid Lines. Would require:
- New `SplitByReferencePlaneCommand.cs`
- New `SplitByReferencePlaneViewModel.cs`
- New `SplitByReferencePlaneEventHandler.cs`
- Logic extension in `SplitElementsShared` using the reference plane's normal + origin as the cutting plane (identical math to `TryBuildGridPlane`)

### E — Pre-selection passthrough
If the user has elements selected in Revit when they launch any split tool, offer to operate on only those elements (skip the category step and go straight to Step 2). Requires reading `uidoc.Selection.GetElementIds()` in the Command and passing the list to the ViewModel constructor.

---

## Files to Change

| File                                            | Change                              |
|-------------------------------------------------|-------------------------------------|
| `Source/App.cs`                                 | Replace 3 stacked buttons with PulldownButton |
| `Source/Tools/T04-ModifyElements/SplitByLevelViewModel.cs` | Rename `Title`                |
| `Source/Tools/T04-ModifyElements/SplitByGridViewModel.cs`  | Rename `Title`                |

Optional (improvements D/E above would add new files).

---

## Branch to Create

To be decided with user. Suggested name: `split-ribbon-dropdown`

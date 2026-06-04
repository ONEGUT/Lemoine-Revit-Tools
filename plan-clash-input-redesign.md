# Plan: Clash Dimension — Input Redesign & Real Clash Detection

## Why

The tool is currently unusable for real testing because:

1. **Group inputs are too rigid.** Steps 2–3 only accept pre-authored AutoFilters
   rules. If a rule doesn't already describe exactly what you want to clash
   (e.g. "Pipes vs Floors"), you're blocked before you can run anything.
2. **No per-group document control.** You can't say "Group 1 = Pipes from the MEP
   link, Group 2 = Floors from the Structure link."
3. **Detection is bounding-box-only.** `FindClashes` compares axis-aligned
   bounding boxes, producing false positives (huge diagonal-pipe bboxes) and
   false negatives (no Z overlap) — so results never match a real clash.
4. **Active-view default hides linked floors.** The linked-floor elevation match
   in `ClashDimensionCommand` transforms a *level elevation* through the link
   transform; when a link's internal origin differs, every linked floor
   disappears from the S4 default list.

## Decisions (confirmed with user)

- **Group definition:** per group, a mode toggle — **Filter Rules** (primary) /
  **Categories** (fallback) / **Select Elements** (direct pick). Same for both
  groups.
- **Source scope:** per-group document picker (host + each loaded link).
- **Detection:** true solid intersection (bbox pre-screen → Boolean confirm).
- **S4:** keep active-view default, fix linked-floor matching.

---

## UI Changes (Steps 2 & 3) — invoke `/revit-navisworks-ui` before coding

Each group step (S2 = Group 1, S3 = Group 2) gets the same three-section layout,
wrapped in the existing `WrapInScroll` so the Confirm button stays reachable:

```
ScrollViewer
└── StackPanel
    ├── [Mode]   LemoineSingleSelect  "Filter Rules | Categories | Select Elements"
    ├── [Source] LemoineMultiSelectTabs  (host + each link, grouped)  ← which docs to scan
    ├── Divider
    └── [Body container]  ← cleared + rebuilt when Mode changes
          Mode = Filter Rules     → LemoineMultiSelectTabs of AutoFilters rules (current control)
          Mode = Categories       → LemoineMultiSelectTabs of clash-relevant Revit categories
          Mode = Select Elements  → "Pick in model" button + count of picked elements
```

- Mode switch uses the same clear-and-rebuild closure pattern already used in S4,
  with `Keyboard.ClearFocus()` after rebuild.
- "Select Elements" raises a dedicated ExternalEvent that runs
  `uidoc.Selection.PickObjects` on Revit's main thread (cannot pick from the STA
  UI thread), then writes the picked references back into the ViewModel and
  refreshes the count label.

## Settings model (`ClashDimensionSettings.cs`)

Replace the single rule-key pair with per-group, per-mode state:

```
string        Group1Mode / Group2Mode            // "Rules" | "Categories" | "Elements"
List<string>  Group1RuleKeys / Group2RuleKeys    // (existing) persistKeys
List<string>  Group1Categories / Group2Categories// OST_* strings
List<long>    Group1ElemIds / Group2ElemIds      // direct-pick element ids
List<long>    Group1ElemLinkIds / Group2ElemLinkIds   // parallel link-inst ids (0 = host)
List<long>    Group1SourceLinkIds / Group2SourceLinkIds// docs to scan (0 = host, >0 = link inst id)
```

Old `Group1RuleKeys`/`Group2RuleKeys` are reused as-is (back-compat). New fields
default to empty / mode "Rules" so existing saved settings still load.

## Event handler (`ClashDimensionEventHandler.cs`)

### Mode-aware scanning
`ScanGroup` becomes `ScanGroup(GroupSpec spec, sources)` where `GroupSpec`
carries Mode + the relevant selection lists + the allowed source doc set:

- **Rules:** existing keyword matching, but only over the group's selected source
  documents.
- **Categories:** `FilteredElementCollector(srcDoc).OfCategory(bic).WhereElementIsNotElementType()`
  for each selected category, over the group's source docs.
- **Elements:** resolve the stored (linkId, elemId) pairs directly.

Source documents are filtered to the group's `SourceLinkIds` (empty = all).

### Real clash detection (replaces `FindClashes`)
```
for each g1 in group1:
    for each g2 in group2:
        if AABB(g1) does not overlap AABB(g2) (with tolerance): continue   // fast pre-screen
        s1 = solids of g1 transformed to host coords (GetTransformed(linkTx))
        s2 = solids of g2 transformed to host coords
        inter = BooleanOperationsUtils.ExecuteBooleanOperation(s1, s2, Intersect)
        if inter != null and inter.Volume > epsilon:
            overlapBBox = inter.GetBoundingBox() projected to view-plane XY
            add ClashResult
        respect MaxClashes
```
- Solids pulled from each element's `get_Geometry(Options{ComputeReferences=false})`,
  union of solids per element. Link solids transformed via the link's total
  transform before the Boolean op (all math in host coords, per plan §45).
- Annotation placement keeps using the overlap bbox (now from the real
  intersection solid, not the loose AABB).
- Tolerance still expands the annotation box on all sides.

## Command (`ClashDimensionCommand.cs`) — S4 fix + source lists

- **Fix linked-floor active-view match:** stop transforming the level elevation.
  Instead compute the floor's own host-space Z from its bounding-box centre
  transformed by the link transform, and compare to the active view's level
  elevation (±tolerance). Robust to differing link origins.
- Build the host + link **document list** once and pass it to the ViewModel so
  each group's Source picker can list them.

## Files

| File | Change |
|------|--------|
| `ClashDimensionSettings.cs` | Add per-group Mode / Categories / ElemIds / ElemLinkIds / SourceLinkIds |
| `ClashDimensionCommand.cs` | Pass document list; fix linked-floor elevation match |
| `ClashDimensionViewModel.cs` | Rebuild S2/S3 with Mode + Source + body container; persist new state |
| `ClashDimensionEventHandler.cs` | Mode-aware `ScanGroup`; real solid-intersection detection; per-group source scoping |
| `GroupSpec.cs` (new) | Small carrier: Mode + selection lists + source docs for one group |
| `ClashPickEventHandler.cs` (new) | ExternalEvent handler for "Select Elements" picking |
| `App.cs` | Register the new pick ExternalEvent/handler |

## Out of scope (this branch)

- Changing the cross-annotation / dimensioning logic (S4 all-edges + grids stay
  as just implemented).
- Section/elevation views (plan views only).

## Branch

Continue on the already-designated dev branch `claude/auto-dimension-clash-workflow-3yklC`.
One logical change: clash-input redesign + real detection.

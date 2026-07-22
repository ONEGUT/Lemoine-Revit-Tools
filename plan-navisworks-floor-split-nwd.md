# Plan — Navisworks Floor Splitter (NWF → per-floor NWDs)

## Goal

A small Navisworks tool that takes one large federated model (NWF) and produces
**one NWD per floor**, where each NWD contains **only that floor's geometry**.
The user picks level boundaries; the tool derives floor bands, hides everything
outside each band, saves a viewpoint per floor, and exports each floor to NWD.

## Confirmed decisions (from the user)

1. **Floor bands:** *One floor between each pair of selected levels.* For sorted
   selected level elevations `E1 < E2 < … < En`, create **N−1** floors named by
   their lower level:
   - Floor 1: `−∞ → E2`  (bottom end stretches far below)
   - Floor i: `Ei → E(i+1)`
   - Floor N−1: `E(n-1) → +∞`  (top end stretches far above)

   Example — levels `L1=0', L2=12', L3=24', Roof=36'` →
   `L1: −∞→12'`, `L2: 12'→24'`, `L3: 24'→+∞`. (The topmost level's own
   elevation is only used as a cut line for the floor below it; everything above
   the last interior cut lands in the top floor.)

2. **Level source:** *Discover from the model, editable.* Scan the federated
   model's geometry-item properties for a `Level` / `Elevation` value, present a
   checklist with editable elevations, allow manual add. Falls back to manual
   entry if nothing is discovered.

3. **Run action:** *Auto-export every floor to a folder* **and** *create + save a
   named viewpoint per floor* (capturing its hide state) so the user can inspect,
   tweak, or re-export any floor manually. Both, not either/or.

4. **Straddle rule:** *Keep on both floors* — hide only items **entirely** outside
   `[low, high]` (`MaxZ < low || MinZ > high`); a column/riser crossing a floor
   line stays visible in both neighbouring floors' NWDs. Exposed as a toggle
   (alternative: assign by element centroid Z).

5. **Branch flow:** reset the designated dev branch
   `claude/navisworks-acc-plugin-plan-mjlc2r` onto `claude/focused-volta-5psr8p`
   (to inherit the Navisworks scaffold), then add the Floor Splitter tool and push
   there.

## Feasibility — verified against the Navisworks 2026 .NET API

- **Export with hidden geometry physically removed (2026 only):**
  `Document.TryExportToNwd(path, NwdExportOptions)` with
  `ExcludeHiddenItems = true` writes an NWD containing only visible items. This
  is the linchpin — hidden geometry is *dropped*, not just flagged.
  ```csharp
  var options = new NwdExportOptions {
      ExcludeHiddenItems         = true,
      FileVersion                = (int)DocumentFileVersion.Navisworks2026,
      EmbedXrefs                 = true,
      // PreventObjectPropertyExport = false,  // keep props by default
  };
  doc.TryExportToNwd(floorPath, options);
  ```
  Source: Autodesk AEC DevBlog, "Introducing NwdExportOptions in Navisworks 2026."
- **Hide, don't clip.** `doc.Models.SetHidden(items, true)` sets visibility.
  Autodesk explicitly warns that `ExcludeHiddenItems` does **not** drop items that
  are merely *section-clipped* (they stay in the tree) — so out-of-band elements
  must be genuinely **hidden**, never section-planed. This matches the user's
  "hide all elements outside the section plane" instinct.
- **Per-item Z extent:** `ModelItem.BoundingBox()` → `BoundingBox3D.Min/Max.Z`.
- **Saved viewpoints remember hide state only if the option is on.** The global
  "Save Hide/Required Attributes" viewpoint default is **off by default**; the
  tool must enable it before saving each floor's viewpoint, else the viewpoints
  won't reflect what was exported. (COM `InwOpView.ApplyHideAttribs`; .NET
  `DocumentSavedViewpoints.AddCopy` / `ReplaceFromCurrentView`.)
- **No `ExternalEvent`.** Navisworks runs plugin code on the main thread; the
  existing scaffold proves the shared Lemoine UI renders and `Run()` reads/writes
  the active document directly.
- **Cannot build/test on Linux** (per CLAUDE.md). This session delivers code +
  integration notes; build/verify happens on the user's Windows Navisworks 2026.

## Base branch & scaffold reuse

- **Base:** `claude/focused-volta-5psr8p` — it already carries the working
  `LemoineNavisworks` project (`net48`, links the shared `Source/Lemoine/**`
  framework + icon font, `AddInPlugin` launcher, `NavisToolWindow`, auto-deploy
  to the Plugins folder). The new tool is **one new `ILemoineTool`** + its own
  launcher button; no new infrastructure.
- **Conventions on this branch:** shared UI namespace is `LemoineTools.Lemoine`;
  tools follow `DiscoverSearchSetsViewModel` (step flow, `IStepAware`,
  `SetResourceReference` theming, inline strings). NOTE: `focused-volta` predates
  the current `Source/Framework/AppStrings.cs` externalization system described in
  the main-line CLAUDE.md, so this tool matches the branch's existing scaffold
  conventions (inline strings like `HelloNavis`/`DiscoverSearchSets`), not
  AppStrings. Flag for reconciliation if/when focused-volta is merged forward.
- **Push branch:** task-designated `claude/navisworks-acc-plugin-plan-mjlc2r`.
  Since that branch currently lacks the scaffold, the intended flow is to bring
  the `focused-volta` Navisworks scaffold into the designated branch first, then
  add this tool. **To confirm with the user before touching branches.**

## Reusable pieces already on the branch

`LemoineNavisworks/Source/SearchSets/NavisSearchSets.cs` already provides:
- `ModelNames(doc)` and per-model iteration over `doc.Models`.
- `GeometryItems(doc)` — walks `model.RootItem.DescendantsAndSelf`, filters
  `ModelItem.HasGeometry` (with try/catch guards). Directly reused to gather the
  leaf items to classify by Z, and to read level properties.
- Property reading via `item.PropertyCategories` → `DataProperty`. Reused to read
  each item's `Level`/`Elevation` value.

## Files to add

```
LemoineNavisworks/Source/FloorSplit/
├── FloorSplitPlugin.cs        — [Plugin] AddInPlugin launcher button
├── FloorSplitViewModel.cs     — ILemoineTool step flow (the UI)
├── LevelDiscovery.cs          — scan properties → distinct levels + elevations
├── FloorBandCalculator.cs     — levels → bands; classify items by Z extent
└── FloorSplitRunner.cs        — hide → save viewpoint → export → restore loop
```

## Step flow (mirrors DiscoverSearchSets)

- **S1 — Levels** (required): discovered level checklist (name + editable
  elevation), "+ Add level manually", live "N levels selected → N−1 floors"
  summary. If nothing discovered, show manual-entry rows.
- **S2 — Floors & options** (required): read-only preview of the derived bands
  (each floor's name + `low → high`, ends shown as `−∞ / +∞`); straddle rule
  toggle (default **"keep anything overlapping the band"** = hide only items
  *entirely* outside `[low, high]`; alternative **"assign by element centroid"**);
  output folder picker; NWD filename pattern (default `<ModelName>-<FloorName>`);
  export options (Exclude Hidden = always on; File Version 2026; Embed Xrefs;
  keep/strip object properties).
- **S3 — Run** (final, carries the Run button): for each floor, hide → save
  viewpoint → export → restore; steady progress + Output log; cancellable
  between floors.

## Core algorithm

1. **Gather once:** iterate `GeometryItems(doc)`, cache each item's
   `BoundingBox().Min.Z / Max.Z`. Compute the model's overall Z min/max for the
   `±∞` end bands (use `overallMin − margin` / `overallMax + margin`, so the ends
   truly capture footings below and roof/MEP above).
2. **Discover levels:** for each geometry item read a `Level` name and, where
   present, a numeric `Elevation`; where absent, infer a level's elevation from
   the min Z of items carrying that level name. De-dup, sort by elevation.
3. **Bands:** from sorted selected elevations `E`, for `i = 1..n-1`:
   `low = (i==1) ? −BIG : E[i]`, `high = (i==n-1) ? +BIG : E[i+1]`, name = level
   name of `E[i]`.
4. **Per floor:**
   a. Compute `hideSet` from cached Z extents per the straddle rule
      (`entirely-outside`: `MaxZ < low || MinZ > high`).
   b. `doc.Models.SetHidden(keepSet, false)` then `SetHidden(hideSet, true)`.
   c. Enable "Save Hide/Required Attributes"; save a viewpoint named after the
      floor into a "Floor Split" viewpoint folder.
   d. `doc.TryExportToNwd(Path.Combine(folder, name + ".nwd"), options)`; check
      the bool result, log success/failure per floor.
   e. Restore visibility before the next floor.
   Wrap each floor in try/catch → log + continue; report progress per floor.
5. **Restore original state at end:** snapshot the pre-run hidden set and restore
   it so the live model is left unchanged after the run (memory discipline —
   clear cached item refs in a `finally`).

## Open items to confirm before coding

- **Units:** elevations read/entered in the document's display units; verify
  `BoundingBox` units vs. display units on Windows and convert consistently.

## To verify on Windows (cannot compile here)

- `NwdExportOptions` member names + `DocumentFileVersion.Navisworks2026` enum.
- Exact API/option key to enable "Save Hide/Required Attributes" before saving
  viewpoints, and the `SavedViewpoints` add/name/folder calls.
- Whether `StepFlowWindow.Run` executes on Navisworks' main thread or needs
  marshaling for the document mutations (SetHidden / export).
- `SetHidden` performance on a large federated model (hundreds of thousands of
  items) — may need batching, already structured per-floor.
```

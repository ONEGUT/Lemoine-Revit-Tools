# Plan — Zones rework: Discover, Manager hierarchy, Views/Sheets rename, key-plan outline

Branch: `claude/revit-3d-view-templates-wwq3sk` (continue on it — do not branch again).

Seven issues from the user, in the order they were raised. Each section states what is
wrong, the evidence for it, and what changes.

### Decisions taken

| question | answer |
|----------|--------|
| Where Sheets sit in the tree | Top-level branch, sibling to the buildings (§5b) |
| What an area's view override covers | Per field, sparse — not a whole-def replacement (§7b) |
| A scope box resized after adoption | Auto re-adopt, every time, with a per-area drift log line (§6) |

The third reverses a rule the current code states explicitly and comments at length; §6 records
that the policy changed deliberately and what it costs, and the source comment asserting the
opposite is rewritten rather than left contradicting the code.

---

## 1. Zone Discover — Step 1 "Source model" needs the Discover Rules link-picker look

**Now:** `ZoneDiscoverViewModel.BuildSourceStep()` (`Source/Tools/Zones/ZoneDiscoverViewModel.cs:123`)
emits a bare `StackPanel` of naked `CheckBox`es. Nothing else in the repo picks links that way.

**Reference:** `DiscoverViewModel.BuildLinkRow()`
(`Source/Tools/FiltersLegends/DiscoverViewModel.cs:253`) — a bordered card per link
(`LemoineBorder` / `LemoineRaised`, `CornerRadius 4`, 4-column `Grid`: checkbox | label | "Trade" | trade box),
the whole list inside a `MaxHeight = 320` `ScrollViewer` wired with `ControlStyles.WireBubblingScroll`.

**Change:** rebuild `BuildSourceStep` on that card shape with **columns 3 and 4 (the trade
label and trade text box) dropped** — checkbox | document name | host tag. Zone Discover has
no trade concept, which is exactly the difference the user called out. The host row keeps its
`zones.discover.hostSuffix` tag, rendered as a dim chip rather than appended text.

---

## 2. Step flow restructure — back to the house standard

**Now:** three steps — `S1 Source`, `S2 What to discover`, `S3 Review`. The Scan button sits
mid-step-2 and leaves the user on step 2 with no signal; step 3 doubles as both "what was
found" and the run/commit step.

**Change — four steps:**

| id | title | content |
|----|-------|---------|
| S1 | Source model | the card list from §1 |
| S2 | Discover | the four what-to-find toggles + the **Scan model** button |
| S3 | Results | every proposal as a keep/drop tick box, grouped Buildings / Levels / Areas, plus scan notes |
| S4 | Confirm & commit | review summary + Run (`IReviewableTool` values, `ReviewNote`, `ReviewWarning`) |

- Step 2's title becomes plainly **"Discover"** (`zones.discover.steps.S2`).
- **Scan advances the flow.** `ZoneDiscoverViewModel` implements `IStepNavigable`; the scan's
  completion callback raises `NavigateRequested` with S3's index, so pressing Scan takes the
  user to what was found. `StepFlowWindow` already subscribes when the interface is present
  (`IStepFlowTool.cs:145`).
- S3 rebuilds itself in `OnStepActivated` through the existing `IStepAware` content-refresh
  callback — step content is built eagerly at construction, so this is mandatory, not optional.
- S3 is `required: false` and **not** last, so it may legally show an empty "nothing scanned
  yet" state; S4 is last and always visible, per the `IConditionalSteps` contract note.
- Navigation stays where the framework puts it — inside each step's own nav row. No footer
  navigation is added.

---

## 3. Scan is broken — it never reaches the UI (root cause found)

`ZoneDiscoverScanHandler.Execute` runs on **Revit's main thread**. The ViewModel wires its
callbacks with no marshalling at all (`ZoneDiscoverViewModel.cs:294-309`):

```csharp
_scanHandler.OnScanComplete = r => { _scanning = false; _result = r; RenderReview(); ... };
```

`RenderReview()` mutates `_reviewPanel.Children` — a WPF element created on the tool window's
**dedicated STA thread**. Touching it from Revit's thread throws
`InvalidOperationException`. That throw is caught by `Execute`'s own `catch`, which calls
`OnError` — which touches the same UI and throws again, out of the ExternalEvent, where Revit
discards it. Net effect: pressing Scan produces no proposals, no error, and no log line. That
matches "the best I can tell is that it never even scans".

`StepFlowWindow` marshals `pushLog` / `onProgress` / `onComplete` / the content-refresh
callback through `SafeBeginInvoke` (`StepFlowWindow.xaml.cs:1183-1191`, `:292`) — but these two
callbacks are wired by the ViewModel directly and bypass all of it.

**Fix (house pattern, copied from `DiscoverViewModel`):** capture the window's dispatcher
lazily in `GetStepContent` (`Dispatcher.CurrentDispatcher`, which runs on the STA thread during
window construction — capturing in the constructor is wrong, the ViewModel is built on Revit's
main thread by the command), store it, and marshal both `OnScanComplete` and `OnError` through
`BeginInvoke`, guarded by `HasShutdownStarted`. Add the same guard to any future handler callback.

**Also:** the scan currently reports failure only to `DiagnosticsLog`. It gains a visible
banner on S3 stating the failure, and the "Found 0 …" notes the collector already produces are
surfaced on S3 rather than only inside the review list.

---

## 4. Zone Discover moves inside the Zone Manager

`Discover Rules` has **no ribbon button** — it is reachable only from inside Auto Filters
(`App.cs:676`), launched via `App.OpenDiscoverEvent` → `OpenDiscoverEventHandler` →
`DiscoverLaunchCommand.Open`, because the window's setup needs Revit's main thread.

**Change, mirroring that exactly:**
- Delete the `LT_ZoneDiscover` push button from the Zones pulldown (`App.cs:603-610`). It is a
  `PulldownButton`, so removing one entry is free — no `AddStackedItems` arity constraint.
- Add `ZoneOpenDiscoverEventHandler` + `App.ZoneOpenDiscoverHandler` / `ZoneOpenDiscoverEvent`.
- Extract `ZoneDiscoverCommand`'s window-opening body into a static `Open(UIApplication)` so the
  command and the event handler share one launcher.
- Zone Manager toolbar gains a **Discover** button that saves the library first, then raises the
  event — the same "persist before launching so the other window reads current state" dance
  `FiltersSettingsWindow.LaunchDiscover` does (`:568-590`).
- Zone Manager reloads the library on `Activated` so zones committed by Discover appear on
  return, matching `FiltersSettingsWindow.OnWindowActivated`. Without this the Manager's
  `OnClosed` save would write its stale in-memory library straight over Discover's commit.

`ZoneDiscoverCommand` itself stays registered (it is the launcher target) but is no longer on
the ribbon.

---

## 5. Zone Manager — the hierarchy, the dead Add Building button, inline creation

### 5a. "Add building" genuinely does nothing

`AddBuilding()` (`ZoneManagerWindow.xaml.cs:520`) adds a `ZoneBuilding` and selects it. Then:
- `RebuildDetail()` (`:343`) tries Area → Level → Recipe → Layout. **There is no
  `BuildBuildingDetail`**, so a building id matches nothing and the detail pane falls through to
  the empty-library placeholder.
- `BuildStructureList()` (`:297`) does `if (rows.Count == 0) continue;` — a building with no
  levels or areas yet emits **no header row either**.

So the new building exists in the library and is invisible in both panes. Fix: add
`BuildBuildingDetail` (name, code, sort) and always render a building node even when empty.

### 5b. The ordering hierarchy

**Now:** one flat list. Levels and areas are both emitted at `indent: 1` under a building header
(`:293-295`), so a level and an area are visually identical siblings. Areas are grouped by
`BuildingId` only — never shown against the levels they apply to. Recipes and Layouts are
separate top-level tabs with no relationship to anything. That is the "no idea what belongs to
what".

**Change — one real tree**, replacing the Structure/Recipes/Layouts tab strip:

```
▾ Building A                                   [+ Level]
   ▾ Level 01                                  [+ Area] [+ View]
      ▾ Views                                            (the level's seed set)
           Floor Plan
           RCP
      ▾ Area 1                                 [+ View override]
         ▾ Views
              Floor Plan          inherited
              RCP                 overridden
      ▸ Area 2
   ▸ Level 02
▸ Building B                                   [+ Level]

▾ Sheets                                       [+ Sheet set]
   ▾ A1 — Overall
        Group A   ·  Area 1 + Area 2
        Group B   ·  Area 3
   ▸ A3 — Enlarged
```

**Sheets is a top-level branch, sibling to the buildings** (user decision) — a sheet set is keyed
by title-block size and spans every level and area, so nesting it inside one building or level
would misrepresent what it covers and force a duplicate set per parent.

- ▸/▾ expand carets per parent, per the house nesting rule (children belong under an expand
  caret on the parent, never in a parallel picker).
- Selecting any row opens that record's detail pane on the right. Group rows inside a sheet set
  still address their parent set, as today.
- Search filters the tree, auto-expanding ancestors of a match.

### 5c. Add buttons embedded in the hierarchy

**Now:** `_railActions` carries "Add building / Add level / Add area" as a footer row under the
list, and `AddLevel`/`AddArea` guess the parent with
`Lib.Buildings.FirstOrDefault()?.Id ?? ""` (`:534`, `:548`) — so on a two-building project a new
level silently lands under the wrong one.

**Change:** each parent row carries a trailing `+` that creates the correct child **in that
parent** — building → level, level → area or view def, area → view override, root → building.
The parent id comes from the clicked row, so nothing is guessed. The rail footer keeps only
Delete. The new child is selected and its parent auto-expanded so the detail pane opens on it.

---

## 6. Scope box selection must resolve extents there and then

**Now:** picking a scope box only writes the name
(`ZoneManagerWindow.xaml.cs:377-378`: `a.ScopeBoxName = v; RebuildList();`). Nothing ever fills
`MinX/MinY/MaxX/MaxY`, so `MakeReadOnlyField(... a.HasExtents ? … : extentsUnresolved)` keeps
reading "unresolved". It cannot work as written: `ZoneManagerCommand` captures scope boxes as
`List<string>` of **names only** (`:71-76`), discarding the bounds
`ZoneScopeBoxSync.CollectBoxes` already returned.

**Change:**
1. `ZoneManagerCommand` hands the window `List<ZoneScopeBoxSync.BoxInfo>` (name **and** bounds)
   instead of a name list.
2. On selection, call the existing `ZoneScopeBoxSync.AdoptExtents(area, box)` (`:221`) — it
   already writes extents and keeps an `ExtentsCentre` anchor consistent — then refresh, so the
   field reads `120' × 84'` immediately.
3. On window open, any area adopting a box that exists but has never been solved
   (`Status.FirstResolve`) is adopted automatically and reported in the status line.
4. **A box that exists but was resized since adoption is also re-adopted automatically**
   (user decision). This overrides the deliberate never-automatic rule documented in the
   `ZoneScopeBoxSync` header: re-solving extents moves an `ExtentsCentre` anchor, and an anchor
   that moves against an unchanged sheet coordinate shifts the drawing — measured at 5/8" on a
   1/8" plot for a 10 ft extents change (`SheetAnchorMath`). Because the movement is real and
   now silent by default, every automatic re-adopt **logs one line per area** naming the drift in
   feet and whether stored placements existed for it, so the shift is visible rather than
   discovered later on a plot. The `ZoneScopeBoxSync` header comment is updated to record that
   the policy changed and why — it currently asserts the opposite.
5. A box named in the library but absent from the document reports "missing" in the detail pane
   rather than reading as merely unresolved. It is never deleted (existing rule, unchanged).

---

## 7. Recipes → Views, Layouts → Sheets, and views nested under level/area

### 7a. Rename

Confined to the Zones files (10 files touched; nothing outside `Source/*/Zones/` references these types):

| now | becomes |
|-----|---------|
| `ZoneViewRecipe` | `ZoneViewDef` |
| `ZoneSheetLayout` | `ZoneSheetSet` |
| `ZoneLibrary.Recipes` | `ZoneLibrary.ViewDefs` |
| `ZoneLibrary.Layouts` | `ZoneLibrary.SheetSets` |
| `Lib.Recipe(id)` / `Lib.Layout(id)` | `Lib.ViewDef(id)` / `Lib.SheetSet(id)` |
| UI tabs "Recipes" / "Layouts" | tree branches "Views" / "Sheets" |

XML element names are renamed with them. This library has never shipped — the branch is
unmerged and `ZoneLibrary.CurrentVersion` is still 1 — so there is no stored data to stay
compatible with, and keeping `<Recipes>` on disk under a `ViewDefs` property would just
reintroduce the naming confusion in the file format.

All user-facing strings move with it in `Strings/en/zones.json`; the mode tokens
(`ZoneViewKind.*`, `ZoneScaleMode.*`, …) are persisted logic tokens and stay exactly as they are.

### 7b. Views belong to the level, overridable per area

> "views should be inside the area and level because every area should have a view dedicated to
> it. taking information seeded in the level by default but with the ability to override on each area."

**Model change:**
- `ZoneLevel.ViewDefs : List<ZoneViewDef>` — the level's seed set. Every area on that level
  inherits all of them, so "every area has a view dedicated to it" holds by construction.
- `ZoneArea.ViewOverrides : List<ZoneViewOverride>` — sparse, and **per field** (user decision):

  ```csharp
  public sealed class ZoneViewOverride
  {
      [XmlAttribute("base")] public string BaseId { get; set; } = "";

      /// Names of the ZoneViewDef fields this area actually overrides. A field absent
      /// from this list keeps tracking the level, whatever Values happens to hold.
      [XmlArray("Fields"), XmlArrayItem("F")]
      public List<string> OverriddenFields { get; set; } = new List<string>();

      /// The overriding values. Only the fields named above are ever read from it.
      public ZoneViewDef Values { get; set; } = new ZoneViewDef();
  }
  ```

  An explicit field-name list rather than nullable members: `XmlSerializer` cannot round-trip
  `Nullable<T>` on an `[XmlAttribute]` without the parallel `…Specified` convention, and for the
  value-typed fields (`Scale`, `AnnotationCropPaperFt`, `SectionBoxFromBand`) a default value is
  otherwise indistinguishable from "not overridden" — the same class of bug as `HasExtents`
  existing to separate unset from zero. It also gives the UI its per-field indicator and reset as
  a single `Contains` check.
- `ZoneLibrary.ViewDefs` remains as an optional project-wide template list a level can seed from,
  but generation reads the level's list, never the global one.

**Resolution rule**, one place, used by both the Manager preview and the run handler:
`ResolveViewDef(level, area, def)` → clone `def`, then for each name in the matching override's
`OverriddenFields`, copy that one field across from `Values`. `ViewRange` is overridden as a
whole sub-object under the single field name `ViewRange` — its four planes are not split further,
since a view range is edited as a unit.

**UI:** each field row on an area's view detail shows an inherited/overridden state and a
per-field **Reset to level** affordance; the view row in the tree summarises as
`inherited` or `overridden (3 fields)`.

**Consumers:**
- `ZoneViewsViewModel` S2 stops offering a flat global recipe list and instead offers the view
  kinds present across the selected cells' levels; the run resolves the actual def per cell.
- `ZoneViewsRunHandler` already loops `Cells` (level × area) at `:109-131` and pairs each with a
  recipe — it swaps `lib.Recipe(rid)` for `ResolveViewDefs(level, area)`.
- Manager tree shows each area's view rows tagged `inherited` / `overridden`, with **Override**
  and **Reset to level** actions on the row.

---

## 8. Key plan — one continuous outline, holes filled, no interior lines

> "should not just directly copy the lines for the floors. It should fill in any wholes inside
> the building and only have a single continues line for the outside of the building. it should
> also remove any extra lines on the inside of the slab like the ones for denoting slope."

**Why it does that now.** `ZoneSlabOutline.TryHosts` (`Source/Framework/Zones/ZoneSlabOutline.cs:193-198`)
emits, **per slab element and per top face**, the outer loop *plus* every inner loop over
100 ft². `ZoneKeyPlanRunHandler.BuildOne` (`:249-269`) then draws a detail line for every segment
of every ring. So the legend receives:
- one closed ring per floor element — abutting slabs draw the shared edge twice, straight down
  the middle of the building;
- every surviving opening as its own ring — the holes;
- **one extra ring per top face**, and a shape-edited / sloped slab has several top faces —
  those are the slope lines the user is seeing.

**Change — union the slabs, keep only the outer boundary:**
1. Collect each slab top face's **outer loop only**. Dropping inner loops at source is what fills
   every hole, with no area threshold to tune.
2. Flatten each loop to z=0: tessellate as today, dedupe near-coincident points, rebuild as a
   closed `CurveLoop` of straight `Line`s. A sloped face's loop is not planar, and
   `CreateExtrusionGeometry` requires planar closed loops — this also keeps the existing
   "everything becomes segments" simplification.
3. Extrude each flattened loop 1 ft with `GeometryCreationUtilities.CreateExtrusionGeometry`.
4. Fold them together with `BooleanOperationsUtils.ExecuteBooleanOperation(… Union)`, each fold in
   its own try/catch — a solid that refuses to union is skipped and logged, never allowed to lose
   the whole outline.
5. Read the union solid's upward-facing `PlanarFace`s and emit each one's **outermost** curve
   loop only. Disjoint masses (a campus, a detached wing) yield one face each, so they still get
   their own continuous ring — which is right.
6. Fallback: if the union produces nothing, fall back to today's per-slab outer rings and **say so
   in the run log**, so a downgrade is never silent.

**Before writing any of this**, confirm `GeometryCreationUtilities.CreateExtrusionGeometry`,
`BooleanOperationsUtils.ExecuteBooleanOperation`, `BooleanOperationsType`, `Solid.Faces` and
`PlanarFace.FaceNormal` against `libs/RevitAPI.dll` metadata with `dnfile`, scoped to their
declaring types — never by string-searching the DLL (the house rule that a name in the assembly
may belong to a different type entirely).

Marked **UNVERIFIED, needs a Windows/Revit run** in the code comments, alongside the existing
note about legend coordinate space.

---

## Files touched

**Modified**
- `Source/Tools/Zones/ZoneDiscoverViewModel.cs` — steps, source cards, dispatcher marshalling, navigation
- `Source/Tools/Zones/ZoneDiscoverModels.cs` — S3 keep/drop state if the proposal record needs it
- `Source/Tools/Zones/Windows/ZoneManagerWindow.xaml{,.cs}` — tree, inline add, building detail, scope-box adopt, Discover button, activate-reload
- `Source/Commands/Zones/ZoneManagerCommand.cs` — capture `BoxInfo` with bounds
- `Source/Commands/Zones/ZoneDiscoverCommand.cs` — extract shared `Open(...)` launcher
- `Source/Framework/Zones/ZoneModels.cs` — renames, `ZoneLevel.ViewDefs`, `ZoneArea.ViewOverrides`, `ZoneViewOverride`
- `Source/Framework/Zones/ZoneSlabOutline.cs` — union outline
- `Source/Framework/Zones/ZoneSettings.cs`, `ZoneNamingTokens.cs`, `ZonePlacementService.cs` — renames
- `Source/Tools/Zones/ZoneViewsViewModel.cs`, `ZoneViewsRunHandler.cs` — resolve per cell
- `Source/Tools/Zones/ZoneSheetsViewModel.cs`, `ZoneSheetsRunHandler.cs` — renames
- `Source/Tools/Zones/ZoneKeyPlanRunHandler.cs` — consume the unioned outline
- `Source/App.cs` — drop `LT_ZoneDiscover`, register the zone open-discover event
- `Strings/en/zones.json` — new/renamed keys

**Added**
- `Source/Tools/Zones/ZoneOpenDiscoverEventHandler.cs`

**Not touched:** `ZoneGroupSolver`, `ZoneScaleFit`, `SheetAnchorMath`, `ZoneOwnerSchema`,
`ZoneStore`, `ZoneTitleBlocks`, `ZoneViewRangeApplier`, `ZonePicker` (beyond the rename).

---

## Order of work

1. Scan-thread fix + Discover step flow (§2, §3) — the tool is unusable until the scan lands.
2. Discover into the Manager (§4).
3. Manager tree, inline add, building detail (§5).
4. Scope-box adopt (§6).
5. Rename + views-under-level/area (§7) — the largest mechanical change, done once the tree that
   displays it exists.
6. Key-plan outline (§8) — independent of everything above.

Each step gets a mockup image rendered and approved before code, per the WPF rule.
A silent-failure scan runs over the whole diff before commit.

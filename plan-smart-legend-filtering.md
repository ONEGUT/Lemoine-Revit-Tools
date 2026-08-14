# Plan — Smart Legend Filtering (view-aware self-filtering legends)

## 1. Problem

A project can carry 30+ ceiling-colour filters. Every one of them is applied to every RCP
view (via the view template), so a legend built from the filter library lists all 30 — even
on a sheet where only 3 of those colours actually appear. The user wants the legend to
**self-filter to the colours genuinely present in the view(s) it serves**, and to adapt per
area without hand-editing a legend per sheet.

"Applied to the view" is not the test. "At least one element visible in that view is
matched by that filter" is.

---

## 2. Behaviour specification

### 2.1 Resolution of the target view(s) — hybrid

Evaluated per legend entry, in order:

| # | Condition | Target views | Reported as |
|---|-----------|--------------|-------------|
| 1 | Entry has explicit view picks (`SmartTargetViewNames` non-empty) | Those views | `Manual — N view(s)` |
| 2 | No picks, and the entry's bound legend view is placed on exactly **one** sheet | Every model view placed on that sheet | `Auto — sheet <number> <name>` |
| 3 | No picks, and the legend is placed on **2+** sheets | Union of the model views on **all** those sheets | `Auto — N sheets (union)` + warn |
| 4 | No picks, and the legend is on no sheet (or not yet created) | — none — | warn: smart filtering skipped, full legend drawn |

Case 3 is not a degraded fallback — one legend view renders identically on every sheet it
sits on, so the union is the only correct content. The warn line tells the user to split
into one legend entry per area if they want per-area content.

Case 4 never blocks the run. A first-time Create has no bound view and therefore no sheet;
the legend draws in full, and the user re-runs Update once it's placed.

### 2.2 The liveness test (per candidate filter × target view)

A filter is **live** in a view when all of these hold:

1. Its `ParameterFilterElement` exists in the document.
2. It appears in `view.GetFilters()` (union'd with the view template's filters — see §5.3).
3. `view.GetIsFilterEnabled(id)` is `true` (a disabled filter overrides nothing).
4. `view.GetFilterVisibility(id)` is `true` (a filter set to *hide* makes its elements
   invisible — nothing is coloured, so it does not belong in a colour legend).
5. At least one element matches it **and is visible in the view** — host or linked.

The host test:

```csharp
new FilteredElementCollector(doc, view.Id)
    .WherePasses(new ElementMulticategoryFilter(pfe.GetCategories()))  // quick filter first
    .WhereElementIsNotElementType()
    .WherePasses(elementFilter)          // pfe.GetElementFilter(); skipped when null
    .FirstElementId() != ElementId.InvalidElementId
```

A view-scoped collector already honours crop, view range, hidden categories and hidden
elements, so "visible in the view" needs no extra work. A rule-less whole-category filter
returns `null` from `GetElementFilter()` (documented in CLAUDE.md) — the category filter
alone is then the whole test.

The linked test (only when the host test found nothing): for every `RevitLinkInstance`
visible in the view whose link display is **By Host View**, run the same category +
element filter against a collector on `link.GetLinkDocument()`, bounded by the view's
bounds transformed into link space. This mirrors `CeilingHeatmapEventHandler.ScanLinkedCeilings`
/ `GetViewBoundsFilter` — on these projects ceilings live in links, so a host-only test
would report every ceiling filter as dead. A link **not** on By Host View is not colour-
cascaded by host filters at all, so it is correctly excluded (and reported, exactly as
`ReportLinkDisplayModes` already does for the heatmap).

### 2.3 The fail-safe rule (non-negotiable)

> **A row is hidden only when its absence is positively proven. Anything that cannot be
> evaluated stays visible and is logged.**

| Situation | Outcome |
|-----------|---------|
| Filter applied, enabled, visible, ≥1 match (host or link) | **shown** |
| Filter applied but disabled / set to hide | hidden — reason logged |
| Filter applied, zero matching visible elements | hidden — reason logged |
| Filter not applied to any target view | hidden — reason logged |
| No `ParameterFilterElement` with that name exists in the project | hidden — reason logged (nothing in the model can carry that override) |
| Block has no `SourceRuleId` (custom colour / hand-made swatch) | **shown** — unprovable |
| Rule compares an `ElementId`-valued parameter and the target is a link | **shown** — unprovable cross-document, logged |
| Any exception during the test | **shown** — `DiagnosticsLog.Swallowed` + run-log warn |
| Smart filtering on but no scope resolved (case 4) | **all shown** — warn |

### 2.4 Structural consequences of hiding a row

- A group whose blocks are **all** hidden must not draw its header — otherwise the legend
  shows a title with nothing under it. The group is skipped entirely and its column stride
  is not advanced.
- A row whose groups are all empty must not consume vertical space (no `rowGap` advance).
- If **every** block in the legend resolves hidden, the run aborts **before** opening the
  transaction with a warn ("no live filters in the target view(s) — legend left unchanged").
  It must not clear an existing legend and leave it blank. This is why the whole liveness
  pass runs *before* `tx.Start()` — it is read-only and has no reason to be inside.

### 2.5 Run log

Per the repo's zero-result rule, the pass always states what it found:

```
Smart filtering: Auto — sheet A-101 FIRST FLOOR RCP (3 view(s))
Smart filtering: 4 of 31 filters live in the target view(s); 27 legend row(s) hidden.
  hidden — "10'-8" AFF": applied to the view, no matching elements visible
  hidden — "9'-0" AFF": no filter of that name exists in this project
  shown  — "Custom hatch": no source rule, cannot be tested
Link "ARCH-CORE.rvt" is not displayed By Host View in FIRST FLOOR RCP — its elements
  are not colour-cascaded and were not counted.
```

Per-row lines are `info`-toned so the handler's existing rollup suppression keeps the run
log to the summary; warnings and failures always print.

---

## 3. API surface — verified against `libs/RevitAPI.dll` metadata

Confirmed by walking `TypeDef` → `MethodList` scoped to the declaring type (and its base
chain), per the repo's "never confirm a member by string-searching the DLL" rule:

| Member | Declaring type | Kind |
|---|---|---|
| `GetFilters()` | `Autodesk.Revit.DB.View` | method |
| `GetIsFilterEnabled(ElementId)` | `View` | method |
| `GetFilterVisibility(ElementId)` | `View` | method |
| `GetFilterOverrides(ElementId)` | `View` | method |
| `GetLinkOverrides(ElementId)` | `View` | method |
| `IsTemplate` / `ViewTemplateId` / `CropBoxActive` | `View` | properties (`get_…`) |
| `ViewId` / `SheetId` | `Autodesk.Revit.DB.Viewport` | properties (`get_ViewId`, `get_SheetId`) |
| `GetElementFilter()` / `GetCategories()` | `ParameterFilterElement` | methods |
| `GetLinkDocument()` | `RevitLinkInstance` | method |
| `GetTotalTransform()` | `Autodesk.Revit.DB.Instance` (inherited) | method |
| `WherePasses` / `FirstElementId` | `FilteredElementCollector` | methods |
| `ElementMulticategoryFilter(ICollection<ElementId>)` | — | ctor |

`ViewSheet.GetAllPlacedViews()` exists but its treatment of legends is not documented
clearly enough to rely on. Sheet ↔ legend resolution therefore uses a document-wide
`FilteredElementCollector(doc).OfClass(typeof(Viewport))` matched on `vp.ViewId`, which
also handles the multi-sheet case natively (a legend is the one view type placeable on
many sheets).

---

## 4. Files

### New

| Path | Purpose |
|---|---|
| `Source/Tools/FiltersLegends/LegendCreator/SmartLegendScope.cs` | All Revit-side read logic: target-view resolution, per-filter liveness, link handling, result DTO. Pure reads, no transaction, no UI. |
| `Source/Tools/FiltersLegends/LegendCreator/SmartLegendPreviewEventHandler.cs` | `IExternalEventHandler` wrapper so the settings window can refresh its preview counts on demand without opening the Revit document itself. |

### Modified

| Path | Change |
|---|---|
| `LegendCreatorSettings.cs` | `LegendEntry.SmartFilterEnabled` (bool) + `SmartTargetViewNames` (`List<string>`); `Clone()` updated. |
| `LegendCreatorEventHandler.cs` | New inputs `SmartFilterEnabled` / `SmartTargetViewNames`; pre-transaction liveness pass; block/group/row skip logic; logs; payload cleared in `finally`. |
| `Windows/LegendSettingsWindow.xaml.cs` | `SMART FILTERING` card; view-picker popup; preview-refresh wiring; callbacks nulled on `Closed`. |
| `Source/App.cs` | Register `SmartLegendPreviewHandler` + its `ExternalEvent` alongside `LegendCreatorHandler`. |
| `Source/Commands/FiltersLegends/OpenLegendSettingsCommand.cs` | Capture `BrowserTreeCapture.Capture(doc)` on the main thread for the view picker. |
| `Strings/en/testing.legendCreator.json` | Handler run-log keys. |
| `Strings/en/testing.legendCreator.builder.json` | Window UI keys. |

---

## 5. Design detail

### 5.1 Why view **names**, not ElementIds, for the explicit picks

`LegendEntry` is serialized into `%AppData%\LemoineTools\LegendCreatorSettings.xml` *and*
into the `.rvt` via `ProjectLibraries` *and* into the shared seed library. An `ElementId`
in any of those means nothing outside its own document — the exact mistake `RevitViewId`
and the per-role text-style ids were removed for. Revit enforces uniqueness on `View.Name`,
so a name resolves deterministically inside a document and travels between them.

`SmartTargetViewNames` therefore stores names, resolved against the active document at run
time. **A name that does not resolve is reported**, never silently dropped — consistent
with how unresolvable `TitleTypeName` is handled today.

### 5.2 Block → filter name

```
block.SourceRuleId → (owning trade, rule) from AutoFiltersSettings.Instance.Trades
filterName        = AutoFiltersSettings.MakeFilterName(trade.Id, rule.Name)
```

The rule map is built as `ruleId → (trade, rule)` rather than `ruleId → rule`, so the trade
id comes from the trade that actually owns the rule instead of the block's possibly-stale
`SourceTradeId`. The handler already builds a `ruleId → rule` map at
`LegendCreatorEventHandler.cs:149` — it is widened, not duplicated.

### 5.3 View templates

`View.GetFilters()` is expected to report template-supplied filters, but that is not
verifiable without a Windows/Revit run. The pass therefore **unions** the view's own
filter ids with those of its `ViewTemplateId` view when one is assigned. A union can only
over-report (a filter that is listed but matches nothing still fails the element test), so
this is safe in the fail-safe direction and costs one extra `GetFilters()` per view.

### 5.4 Cost

Worst case ≈ (filters × views) existence checks, each a view-scoped collector fronted by a
quick category filter, plus one bounded link collector per visible link when the host test
misses. 30 filters × 5 views × 3 links ≈ 450 collectors — well inside the budget for a
button press, and memoized per `(viewId, filterId)` so a filter shared across views is
tested once per view only. **No `doc.Regenerate()` anywhere in this pass** — it is
read-only.

### 5.5 Window ↔ Revit threading

`LegendSettingsWindow` runs on its own STA thread and cannot touch the document. So:

- **The authoritative pass runs inside `LegendCreatorEventHandler`** on the Revit thread at
  Create/Update time. The legend always reflects the model at the moment it was drawn.
- **The window's preview is advisory**, refreshed on demand by a `Refresh` button that
  raises `SmartLegendPreviewEvent`; the handler returns a DTO (resolution label, live /
  hidden filter names, counts) via a callback marshalled back with non-blocking
  `Dispatcher.BeginInvoke` guarded by `Dispatcher.HasShutdownStarted`.
- Nothing is computed at window open, so opening the Legend Creator gets no slower.

`LegendSettingsWindow` is a bespoke `Window`, so `IToolCleanup.OnWindowClosed` is never
invoked for it (only `StepFlowWindow` calls that). The preview handler's callbacks are
therefore nulled in the window's existing `Closed` handler, and the handler clears its own
payload in a `finally` — otherwise the closed window's tree stays rooted on a session-long
static for the rest of the Revit session.

### 5.6 Smart-hidden ≠ user-hidden

`LegendBlockConfig.Visible` is the user's own eye toggle (`LegendBlockRow.xaml.cs:160`) and
is **never written** by this feature. Smart-hidden state is a separate, non-persisted
runtime marker so that turning smart filtering off restores exactly what the user
configured. In the builder, a smart-hidden row renders dimmed with a small "not in view"
chip; its eye toggle keeps working and keeps its own meaning.

---

## 6. UI

Per the repo's WPF rule, **a faithful mockup is rendered from the real `ThemePalette` and
approved before any UI code is written** (`/revit-navisworks-ui`, Step 7 workflow).

Proposed: a new `SMART FILTERING` card in the right panel, between `TEXT STYLES` and
`PALETTE`, matching the existing `WrapCard` treatment:

```
SMART FILTERING
┌────────────────────────────────────────────┐
│ [✓] Only show colours used in the view     │
│                                            │
│ Scope   Auto — sheet A-101 (3 views)       │
│         [ Choose views… ]                  │
│                                            │
│ 4 of 31 rows will be drawn      [ Refresh ]│
└────────────────────────────────────────────┘
```

- The checkbox drives `SmartFilterEnabled`; the rest of the card is disabled (dimmed) when off.
- `Choose views…` opens `BrowserTreePicker` (the house component for view/sheet selection,
  fed by `BrowserTreeCapture.Capture(doc)` captured in the command) in a popup with
  `StaysOpen=true` + window-level `PreviewMouseDown` dismissal — `StaysOpen=false` crashes
  Revit. Picking views switches the scope line to `Manual — N view(s)` and reveals a
  `Clear override` link returning to Auto.
- The scope line and count start as `—` until `Refresh` is pressed (the window cannot know
  them on its own), so nothing stale is ever displayed as current.

---

## 7. Edge cases handled explicitly

1. Legend not yet created (no bound view) → case 4, full legend, warn.
2. Legend deleted from the document → existing `boundViewMissing` fallback path already
   creates a fresh legend; smart filtering degrades to case 4 for that run.
3. Legend placed on a sheet with no model viewports (a legend sheet) → zero target views →
   treated as case 4, warn, full legend.
4. Target view is itself a legend/drafting/schedule → contributes no filters; harmless.
5. A filter live in one target view and dead in the others → **live** (union across views).
6. Every block hidden → abort before the transaction, existing legend untouched (§2.4).
7. `RunState.CancelRequested` during the liveness pass → stop, log
   "Stopped by user — N of M filters tested", and abort the run before mutating anything.
8. Duplicate view names across picks, or a pick naming a view that has since been renamed →
   reported by name in the log, run continues with the views that did resolve.
9. Smart filtering off → the pass is skipped entirely; behaviour is byte-identical to today.

---

## 8. Known limitation carried forward (stated, not silently accepted)

A filter that is applied, enabled, visible and matching, but that carries **no colour
override**, is counted as live in v1. Proving "this filter contributes no colour" means
inspecting `GetFilterOverrides` for empty surface/cut pattern colours, which is fiddly
enough to deserve its own change. It is noted here rather than shipped as an unspoken
assumption; it over-shows rather than under-shows, which is the safe direction.

Automatic refresh when the model changes (a `DocumentChanged` updater) is **out of scope**.
The legend re-filters when the user presses Update — the same trigger that redraws it today.

---

## 9. Post-change silent-failure scan

Run before committing, per CLAUDE.md, with particular attention to:
- every `try/catch` added around a Revit read routed through `DiagnosticsLog.Swallowed`
  with a human-readable context string;
- the fail-safe rule verified by inspection at each catch site (a swallowed exception must
  leave the row **visible**, never hidden);
- the new option traced from the checkbox all the way to the `if (!live)` skip in the draw
  loop — the Bulk Export "dead option" failure mode this repo has already shipped once;
- zero-result reporting on the resolution and the liveness pass.

---

## 10. Windows/Revit test checklist

1. RCP with 30 filters via template, one area with 3 colours → 3 rows drawn.
2. Same legend, second area → different 3 rows after Update.
3. Ceilings in a **link** (By Host View) → rows retained (this is the case a host-only
   test breaks).
4. Same link set to *Custom / By Linked View* → rows dropped **and** the link reported.
5. Filter present but disabled in the view → row dropped.
6. Filter set to hide → row dropped.
7. Legend on 2 sheets → union drawn + multi-sheet warn.
8. Legend on no sheet → full legend + warn.
9. Manual view picks override the sheet auto-detect.
10. Smart filtering off → output identical to the current build.
11. Custom (no source rule) blocks always survive.
12. All-hidden case leaves the existing legend untouched.
13. Cancel mid-pass leaves the document unmodified.
14. Re-open the window: the toggle and picks persist; a legend library moved to another
    project resolves view names or reports them.

---

## 11. Branch

Proposed name: `smart-legend-view-filtering`.

**Which branch should this be based on — `main`, or the current
`claude/smart-legend-filtering-v16y3g`?**

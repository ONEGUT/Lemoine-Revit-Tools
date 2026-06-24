# Plan — Explode 3D View by Trade (elevation-ordered)

## Goal

Take one source 3D view and produce **N new 3D views at the identical camera angle and
section box**, each isolating a single trade by toggling that trade's AutoFilters view
filters on and the other selected trades' filters off. Scan element elevations (host +
linked) to compute each trade's typical elevation, and **order the output views by
elevation** so they read as a vertical stack (top → bottom).

The tool **reuses the existing AutoFilters trades/filters** — it does not define its own
trade list or category rules.

---

## How it maps onto existing code

| Need | Existing mechanism reused |
|------|---------------------------|
| Pick the source 3D view | `LemoineBrowserTreePicker` + `BrowserTreeCapture.Capture(doc)`, `SingleSelect = true` |
| Pick which trades to explode | `AutoFiltersSettings.Instance.Trades` (`FilterTradeConfig` → `FilterRuleConfig`) |
| Resolve a trade's filters | `AutoFiltersSettings.MakeFilterName(tradeId, ruleName)` → `ParameterFilterElement` by name |
| Add/toggle filters per view | `View.AddFilter` / `SetIsFilterEnabled` / `SetFilterVisibility` (as in `ApplyFiltersToViewsEventHandler`) |
| Copy camera + section box | `View3D.Duplicate(ViewDuplicateOption.Duplicate)`; belt-and-suspenders `SetOrientation(src.GetOrientation())` + `SetSectionBox(src.GetSectionBox())` |
| Read linked elements for elevation | `RevitLinkInstance.GetLinkDocument()` + `GetTotalTransform()` + bbox query (as in `CeilingHeatmapEventHandler`) |
| Warn when filters won't affect a link | `ReportLinkDisplayModes` / `WarnLinksNotByHostView` pattern (link must be "By Host View") |
| Window / steps / run lifecycle | `StepFlowWindow` + `ILemoineTool` (model copied from `CeilingHeatmapViewModel`) |
| Run cancel / failure routing / logging | `LemoineRun`, `LemoineFailureCapture`, `LemoineRunLog`, `LemoineLog` |

---

## Tool flow

**Step 1 — Source 3D view** (`required`)
`LemoineBrowserTreePicker`, `SingleSelect = true`, fed only the non-template `View3D`
ids. The command collects 3D views on the main thread and captures the browser tree.

**Step 2 — Trades to explode** (`required`)
A multi-select list of the AutoFilters trades (label + color swatch). Each selected
trade resolves to its set of `ParameterFilterElement`s by name. A trade whose filters
do **not** yet exist in the project is shown disabled with a note ("Run AutoFilters →
Create Filters first") and skipped — this tool reuses filters, it does not create them.

**Step 3 — Options** (`optional`)
- **Order views by computed elevation** (default ON) — vs. AutoFilters config order.
- **Number-prefix view names** (default ON) — `01_`, `02_`… so the Project Browser
  sorts the stack top → bottom.
- **Name pattern** — default `{nn}_{Source} – {Trade}`.
- **Apply trade color override to the visible trade** (default ON) — uses the owning
  `FilterRuleConfig` overrides via `AutoFiltersEventHandler.ApplyRuleOverride`.

**Step 4 — Review & Run** (`required`, last) — framework review via `ILemoineReviewable`.

---

## Run handler logic (`ExplodeViewByTradeEventHandler`)

1. **Validate** source view is a `View3D`; collect the selected trades' existing filter
   ids by name. Skip-and-log trades with no filters.
2. **Elevation scan (read-only).** For each selected trade, collect its rule-category
   elements inside the source view's section box (host + every visible link via the link
   transform). Take each element's world-Z bbox centroid → per-trade **median + min/max**.
   Log `Found N elements for "<trade>" — median <ft-in> AFF (range …)`; a trade with zero
   matches is logged explicitly (never silent).
3. **Determine stack order** — sort trades by median Z descending (top first), or keep
   config order if the option is off. Log the resolved stack: `Top → … → Bottom`.
4. **Per trade, in one transaction:**
   - `dup = srcView3D.Duplicate(ViewDuplicateOption.Duplicate)`.
   - `SetOrientation(src.GetOrientation())`; if `src.IsSectionBoxActive` →
     `SetSectionBox(src.GetSectionBox())` + `IsSectionBoxActive = true`.
   - Build a unique name (pre-check existing `View.Name` + earlier names this run;
     skip-and-log + try/catch on the `Name =` set per the uniqueness rule).
   - Add every selected trade's filters to the view; `SetFilterVisibility(false)` for
     all trades **except** this one, `true` for this trade; `SetIsFilterEnabled(true)`.
     Optionally apply the visible trade's rule color override.
5. **Link display diagnostic** — report any visible link not "By Host View" (its
   elements won't obey the host filters). Does not change link display.
6. **Result chips** — views created, trades skipped, failures. Log the full stack order.
7. `finally` — clear the handler's per-run payload (memory discipline).

---

## Files

**New**
- `Source/Tools/T06-ExplodeViews/ExplodeViewByTradeEventHandler.cs`
- `Source/Tools/T06-ExplodeViews/ExplodeViewByTradeViewModel.cs`
  (`ILemoineTool, IStepAware, ILemoineReviewable, ILemoineRunResult, ILemoineToolCleanup`)
- `Source/Commands/T06-ExplodeViews/ExplodeViewByTradeCommand.cs`
  (collects `View3D`s + `BrowserTreeCapture.Capture(doc)` on the main thread; spins the
  STA `StepFlowWindow`)
- `Source/Tools/T06-ExplodeViews/ExplodeViewByTradeSettings.cs` *(only if persisting the
  option toggles — otherwise omit)*

**Edited**
- `Source/App.cs` — add `ExplodeViewByTradeHandler` + `ExternalEvent` statics, create them
  in startup, and add a ribbon button under a new **"T06  Views"** panel.

---

## Decisions needed before coding

1. **Isolation model.** Each exploded view toggles the **selected trades'** filters
   on/off (matches the "filters on/off" framing). Elements not covered by any selected
   trade — e.g. architecture/structure if not chosen — stay visible as background
   context. *Recommended:* keep that behaviour; to drop the background, the user includes
   those as trades too. (Alternative: a "hide everything not in a selected trade" option
   via category isolation — more complex, can add later.)
2. **Ribbon placement** — new **"T06  Views"** panel (recommended), or fold into an
   existing panel.
3. **Missing filters** — skip-and-log trades whose filters aren't created yet
   (recommended), vs. auto-running the AutoFilters create engine here.

---

## Out of scope (for now)
- Creating/maintaining the trade filters themselves (owned by AutoFilters).
- Sheet placement of the exploded views.
- Persisting per-run options unless requested.

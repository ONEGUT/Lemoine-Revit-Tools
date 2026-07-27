# Plan: Auto Filters — Apply Filters to View Templates

## Goal

Let Auto Filters attach its generated filters (with graphic overrides) to **Revit view
templates**, not just the active view. Applying to a template is the authoritative
placement — it propagates to every view governed by that template, and it sidesteps the
"template-governed view rejects `SetFilterOverrides`" throw the tool currently catches and
skips.

---

## 1. Feasibility — confirmed

A Revit view template **is** a `View` with `IsTemplate == true`. It exposes the identical
filter API the tool already calls on a normal view:

| Call | Works on a template? |
|---|---|
| `View.AddFilter(filterId)` | Yes |
| `View.GetFilters()` | Yes |
| `View.SetIsFilterEnabled(id, bool)` | Yes |
| `View.SetFilterOverrides(id, ogs)` | Yes — and this is where it *must* go for a template-governed view |
| `View.SetFilterVisibility(id, bool)` | Yes |

No new Revit capability is needed. The tool already touches templates in one place:
`ApplyChangedOverridesAcrossViews` (`AutoFiltersEventHandler.cs:643`) iterates
`AllViews(doc)` — which is `OfClass(typeof(View))` and therefore **already includes
templates** — and re-applies overrides to any that already carry the filter.

**The actual gap is target selection, not the API.** `Execute` hardcodes
`var view = doc.ActiveView;` (`AutoFiltersEventHandler.cs:112`) and every attach path
writes to that single view. Nothing lets the user choose a template as the target.

---

## 2. Naming clash — must be resolved first

The Auto Filters window **already has a "Templates" button** meaning *saved AutoFilters
presets* (`AutoFiltersSettings.Templates`, a `TemplateStore<List<FilterTradeConfig>>`
persisted to `%AppData%\LemoineTools\Templates\AutoFilters\`; see
`WebAutoFilters.cs:602-625`). That is an entirely different concept from a Revit view
template.

**Rule for this change:** the new feature is labelled **"View Templates"** in every
user-facing string, and every new identifier uses `ViewTemplate` / `viewTemplate`. The
existing preset feature keeps the bare "Templates" label. No new string may say just
"Templates".

---

## 3. UX options

The Apply action lives in two places today (`autofilters.js:192-197`, `247-249`):
sidebar footer **Apply to View** / **Remove from View**, and the middle column's
**Apply trade to view**.

### Option A — "Apply to…" target popup on the existing button *(recommended)*

The footer button becomes a split control: clicking **Apply to View** keeps today's
behaviour (active view, zero extra clicks); clicking the adjacent caret opens a popup
listing **Active View** (default, checked) plus a searchable checklist of the document's
view templates, grouped by `ViewType`. Selection persists for the session and the button
relabels to show the target count ("Apply to 3 targets").

- **Pro:** No new window, no new step. The common case (active view) is unchanged and
  still one click. Matches the tool's existing popup-driven sidebar.
- **Con:** Popup inside a bespoke window needs the click-off dismissal discipline
  (`StaysOpen=true` + `PreviewMouseDown`) on the WPF side.

### Option B — a "Targets" tab / column in the window

A third column listing Active View + all view templates as a persistent checklist.

- **Pro:** Always visible; no hidden state behind a popup.
- **Con:** Costs permanent horizontal space in an already three-column window for a
  setting most runs never change.

### Option C — apply to the template governing the active view, automatically

No picker. If the active view has a `ViewTemplateId`, write to the template instead of
the view.

- **Pro:** Zero UI.
- **Con:** Silently changes what "Apply to View" means and edits an object the user did
  not name — this violates the tool's own transparency posture. **Not recommended.**

**Recommendation: Option A.** It adds the capability without taxing the default workflow,
and the popup is the pattern the sidebar already uses.

> This is the one decision that needs the user's pick before implementation starts.
> Sections 4-7 below are written against Option A.

---

## 4. Files to change

| File | Change |
|---|---|
| `Source/Tools/FiltersLegends/AutoFiltersEventHandler.cs` | Add `TargetViewIds` property; resolve targets; loop the attach block over targets instead of a single view |
| `Source/Commands/FiltersLegends/OpenFiltersSettingsCommand.cs` | Capture the view-template list on the Revit main thread (alongside the existing pattern-list capture) and hand it to both window flavours |
| `Source/Framework/Web/WebAutoFilters.cs` | Hold the captured template list + the checked-target set; expose both in the init payload; add `TF(...)` label keys |
| `Source/Framework/Web/WebAutoFiltersWindow.cs` | New bridge cases (`setApplyTargets`); set `handler.TargetViewIds` before `evt.Raise()` in `ApplyTradesToView` |
| `Source/Web/lib/autofilters.js` | Target popup on the footer button; relabel button by target count |
| `Source/Web/autofilters.html` | Popup markup/anchor if not built purely in JS |
| `Source/Tools/FiltersLegends/Windows/FiltersSettingsWindow.xaml(.cs)` | WPF-flavour parity: same popup, same `TargetViewIds` wiring (flag-off fallback, rule R25) |
| `Strings/en/autofilters.filtersWindow.json` | New keys — see §7 |

**New file:** `Source/Tools/FiltersLegends/ViewTemplateEntry.cs` — `{ ElementId Id; string Name; ViewType Type; }`, the capture DTO. (Mirrors the shape used by
`CeilingHeatmapViewModel.HeatmapTemplateEntry` and the `ViewTemplateEntry` in
`docs/plans/plan-view-template-picker-silent-fixes.md`.)

---

## 5. Handler change — the core of the work

### 5.1 New input property

```csharp
/// Views and/or view templates to attach filters to. Empty = the active view
/// (backwards-compatible default). Cleared in Execute's finally, like every
/// other per-run input.
public List<ElementId> TargetViewIds { get; set; } = new List<ElementId>();
```

Add `TargetViewIds = new List<ElementId>();` to the `finally` block at
`AutoFiltersEventHandler.cs:129-133` — a session-long static handler must not retain
`ElementId`s across runs (Memory & Lifetime Discipline).

### 5.2 Target resolution in `Execute`

```csharp
var targets = new List<View>();
foreach (var id in TargetViewIds)
{
    if (doc.GetElement(id) is View v) targets.Add(v);
    else Log(AppStrings.T("...log.targetMissing", id.Value), "warn");   // never silent
}
if (targets.Count == 0) targets.Add(doc.ActiveView);
```

A target id that no longer resolves is **logged**, not skipped silently.

### 5.3 `Run(… View view …)` → `Run(… IList<View> targets …)`

The `view` parameter is used in only four places, so the refactor is contained:

| Line | Use | Becomes |
|---|---|---|
| `:193` | `!view.AreGraphicsOverridesAllowed()` guard | Per-target check; a target that refuses overrides is logged + counted `fail`, the **remaining targets still run** (do not abort the whole run) |
| `:266` | `existingViewFilterIds` = the view's current filters | `Dictionary<long, HashSet<long>>` keyed by target `ElementId.Value` |
| `:605-625` | attach + enable + override, inside `ProcessRule` | `foreach (var t in targets) { … }` over that block only |
| `:719-726` | same, inside `ApplyExistingFilterToView` | same loop |

**The expensive setup stays single-pass.** Source-document collection, `BuildBipMap`,
the material/fill/line pattern maps, the existing-filter index, orphan cleanup and every
filter *definition* build happen once per run regardless of target count — only the
cheap per-view attach repeats. The whole run stays inside the existing single
`Transaction`.

### 5.4 Per-target failure isolation

`AddFilter` can throw when a filter's categories aren't valid for that template's view
type. Wrap each per-target attach in its own `try/catch` → `Log(…, "fail")` + `fail++`,
so one incompatible template cannot sink the run. This matches the existing
catch-and-log-per-filter shape at `:627-632` — **no new silent swallow**.

### 5.5 Counters

`pass`/`fail` currently count *rules*. With N targets a rule can partly succeed. Count
**placements** (rule × target) and say so in the completion line, so "12 applied" is not
mistaken for 12 filters. New string key rather than reusing `log.completeSummary`.

---

## 6. Capture, transport, and the picker

**Capture (Revit main thread, in `OpenFiltersSettingsCommand.Execute`, beside the
existing `CaptureFilterableCategories(doc)` call at `:67`):**

```csharp
var viewTemplates = new FilteredElementCollector(doc)
    .OfClass(typeof(View)).Cast<View>()
    .Where(v => v.IsTemplate)
    .Select(v => new ViewTemplateEntry { Id = v.Id, Name = v.Name, Type = v.ViewType })
    .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
    .ToList();
```

If the list is **empty**, the run log / status must say so explicitly ("No view templates
found in this document") — a silently empty picker is indistinguishable from a broken
collector.

**Transport:** passed to the window constructor exactly as `fillNames`/`lineNames` are
today (`OpenFiltersSettingsCommand.cs:90`, `:105`); surfaced to JS in the init payload
alongside `["templates"]` (`WebAutoFilters.cs:698`) under the distinct key
`viewTemplates`.

**Picker:** group templates by `ViewType` and render with the existing
`lemoine.js` `multiSelectTabs` factory (`Source/Web/lib/lemoine.js:207`) — reuse, not a
hand-rolled checklist. `browserTree` (`:482`) is the wrong fit here: templates have no
Project Browser hierarchy.

**Scope note:** "Remove from View" (`RemoveTradesFromView`, `WebAutoFiltersWindow.cs:154`)
routes through `DeleteFiltersHandler`, which deletes filters project-wide by name — it is
not view-scoped and is therefore **out of scope** for this change. Flagged so the
asymmetry is a decision, not an oversight.

---

## 7. Strings

All new user-facing text goes to `Strings/en/autofilters.filtersWindow.json` via
`AppStrings.T` — no hardcoded literals. New keys:

- `sidebar.applyTargetsLabel`, `sidebar.applyTargetsTooltip`
- `targetsPopup.activeView`, `targetsPopup.viewTemplatesHeader`,
  `targetsPopup.noViewTemplates`, `targetsPopup.searchPlaceholder`
- `window.status.applyingToTargets`
- `log.targetMissing`, `log.targetNoOverrides`, `log.appliedToTarget`,
  `log.completeSummaryTargets`, `log.noViewTemplatesFound`

The JS additions must stay ASCII-only (rule R13) and any edit touching a line with
`\uXXXX` escapes goes through a count-checked Python `str.replace()` pass, not the Edit
tool.

---

## 8. What is NOT changing

- Filter **definition** logic — categories, rules, `ProcessRule`, `RebuildFilter`.
- The in-place update discipline (`SetCategories` / `SetElementFilter`, never
  delete + recreate) — critical here, since a rebuilt filter's new `ElementId` would
  detach it from every template that referenced it. `RebuildFilter` already captures and
  restores view/template assignments (`AutoFiltersEventHandler.cs:743-747`).
- `ApplyChangedOverridesAcrossViews` close-time propagation — already template-aware.
- Externally-managed trades — still attach-only, definitions never regenerated.
- Discover, Legend Creator, Delete-from-Project.

---

## 9. Verification (Windows / Revit — cannot be built or run on Linux)

1. Apply a trade to a view template; confirm every view governed by it picks up the
   filter and its overrides.
2. Apply to a template whose `ViewType` mismatches a rule's categories; confirm a
   logged `fail` for that target and that the other targets still complete.
3. Apply with no targets selected; confirm identical behaviour to today (active view).
4. Re-open the window, edit a colour, close; confirm close-time propagation reaches the
   template.
5. Confirm `%AppData%\LemoineTools\diagnostics.log` carries an entry for every skipped
   or failed target.
6. Post-change silent-failure scan per CLAUDE.md before commit.

---

## 10. Open question for the user

**Which of the three UX options in §3?** (recommendation: **A**, the target popup on the
existing Apply button.)

And: **which branch should this be based from?**

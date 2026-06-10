# Plan — AutoFilters (T01) Review Fixes + Merge-Rules Feature

Implements every finding from the T1 tool-set review, plus the new "merge similar
rules" feature. The Legend Creator layout overhaul is **explicitly out of scope** —
it gets its own plan after discussion.

---

## Phase 1 — Bugs (silent data loss / workflow traps)

### 1.1 S1 link toggle wipes S2 category selections
**File:** `Source/Tools/T01-AutoFilters/DiscoverViewModel.cs` (BuildLinkCard, ~line 413)
- Subscribe `catTabs.SelectionChanged` **before** calling `SetGroups` (per the
  LemoineMultiSelectTabs contract in CLAUDE.md).
- Pass `link.SelectedCategories` as the `initialSelected` argument so rebuilding
  S2 cards (triggered by any S1 checkbox change) restores each link's selection
  instead of clearing it.

### 1.2 Same value across multiple categories loses categories at commit
**Files:** `DiscoverViewModel.cs` (PopulateS4), `DiscoverEventHandler.cs` (RunCommit)
- In `PopulateS4`, merge non-whole-category scan results that share
  (TradeName, ParameterValue, Parameter) into ONE row whose `BuiltInCategories`
  is the union and whose `ElementCount` is the sum. One review row instead of
  N duplicates; the committed rule covers all categories.
- In `RunCommit`, change the duplicate-name skip into a merge: union the incoming
  spec's `BuiltInCategories` into the existing rule (covers re-runs against
  previously committed rules). Log "merged categories into existing rule" instead
  of "skipped".

### 1.3 "Keep Existing Overrides" prevents filter attachment
**File:** `AutoFiltersEventHandler.cs` (ProcessRule, ~line 485)
- When the filter exists in the project, `KeepExistingOverrides` is on, and the
  filter is **not** yet on the active view: still run `view.AddFilter` +
  `SetIsFilterEnabled` (there are no overrides to preserve), and apply the rule
  override since the view never had one.
- Only skip `ApplyRuleOverride` when the filter was already attached to the view.

---

## Phase 2 — Performance

### 2.1 Type-name cache in the Discover scan (biggest win)
**File:** `DiscoverEventHandler.cs` (RunMainScan / ReadParameterValue)
- Per scanned document, cache `ElementId(typeId) → ElementType` name /
  FamilySymbol family name in a `Dictionary<long,string>`, and cache the
  type-parameter fallback per `(typeId, paramName)`. Eliminates a
  `GetElement(GetTypeId())` per element on 100k-element links while Revit's UI
  thread is frozen.

### 2.2 Hoist rule matching out of the apply loop
**File:** `ApplyFiltersToViewsEventHandler.cs` (~line 159)
- Build one `filterName → FilterRuleConfig` dictionary before the view×filter
  loop (same pattern the legend handler already uses) and delete the per-op
  `MatchRule` scan.

### 2.3 Throttle progress callbacks
**File:** `ApplyFiltersToViewsEventHandler.cs`
- Only invoke `OnProgress` when the integer percentage changes (currently one
  dispatcher marshal per filter×view op).

### 2.4 Batch ColorMemory saves
**Files:** `ColorMemory.cs`, `DiscoverEventHandler.cs` (RunCommit)
- Add `SetColorDeferred(value, hex)` + `Flush()` (or a `SetColors` bulk call).
  RunCommit writes the XML file once instead of once per committed rule.

### 2.5 Snapshot view→filter assignments once per run in RebuildFilter
**File:** `AutoFiltersEventHandler.cs` (RebuildFilter)
- Pass a lazily-built `Dictionary<long /*filterId*/, List<View>>` (or a cached
  list of views + their filter sets) from `Run` so multiple rebuilds in one
  transaction don't each re-enumerate every view in the document.

### 2.6 Reverse category lookup map
**Files:** `DiscoverViewModel.cs` (CategoryDisplayName, PopulateS4),
`Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs` (BuildSubtext)
- Add `AutoFiltersSettings.OstToDisplayName` (built alongside the runtime/default
  maps) and replace the O(n) `FirstOrDefault(kv => kv.Value == ost)` scans.

---

## Phase 3 — UX improvements (Discover + small items)

### 3.1 Auto-advance the Discover step flow
**File:** `DiscoverViewModel.cs`
- `StartScan()` → `RaiseNavigate(index of S3)`; `OnScanComplete` (success) →
  `RaiseNavigate(index of S4)`. The `NavigateRequested` event is already wired
  in StepFlowWindow but never raised.

### 3.2 Invalidate stale scan results when S2 config changes
**File:** `DiscoverViewModel.cs`
- Where `_scanComplete = false` is set (category/parameter change), also clear
  `_discoveredRules`, reset the S4 panel to its "run the scan" note, and refresh
  the S5 summary — so stale rules can never be committed.

### 3.3 "Create filters after commit" on S5
**Files:** `DiscoverViewModel.cs`, the Discover launch command (Commands/), 
`DiscoverEventHandler.cs`
- Add a toggle on S5 (default ON): after `RunCommit` persists rules, chain an
  `AutoFiltersEventHandler` run in `CreateOnly` mode so the Revit filters exist
  without the user reopening the Auto Filters tool. The launch command passes
  the create handler + event into the ViewModel. Replace the "run Auto Filters
  after" warning banner with this toggle's description.

### 3.4 Host model as a scan source
**File:** `DiscoverViewModel.cs` + Discover launch command
- Add a "Host model (this document)" row at the top of S1 using the `-1`
  sentinel the handler already maps to the host document.

### 3.5 Small fixes
- S2 card headers show stale trade name after rename in S1 → update header text
  from `link.TradeName` when rebuilding / on trade-name change.
- `DeleteFiltersEventHandler`: report selected names not present on the view as
  skipped (parity with delete-from-project).
- `DiscoverEventHandler` scan log: "unknown category — skipped" logged with
  status `info` + skip count (status currently says fail while counting skip).

---

## Phase 4 — Standards / cleanup

- Replace bare empty catches with `LemoineLog.Swallowed`:
  `AutoFiltersSettingsWindow.cs:59`, `GetSolidLineId()` in
  `AutoFiltersEventHandler.cs` and `ApplyFiltersToViewsEventHandler.cs`.
- Delete dead `ReadParamValue` in `AutoFiltersEventHandler.cs:1024–1092`
  (no callers; Clash engine has its own copy).
- Null-guard the duplicated legend view (`dv!.Name`) in
  `AutoFiltersLegendEventHandler.cs:242` — fail with a logged message instead of
  a potential NRE inside the transaction.
- Discover S2 `CategoryGroups`: validate every label against
  `KnownCategoryMap` when building the tabs; drop unresolvable labels from the
  groups and `LemoineLog.Warn` them (today a mismatched label silently produces
  no config row). Full re-derivation from the captured snapshot is deferred —
  the curated discipline grouping is kept.

---

## Phase 5 — NEW: Merge multiple rules into one

**Where:** the Filters / Color Settings rules editor
(`Source/Lemoine/T01-AutoFilters/GlobalSettingsWindow.Filters.cs`), which already
has Ctrl/Shift multi-select (`_fSelectedRuleIds`) and a batch editor panel
(`BuildBatchRuleEditor`). A **"Merge N rules into one"** button is added to the
BATCH EDIT header card — it appears exactly when ≥2 rules are selected, matching
the requested "auto filters menu + multi-selection" interaction.

**Two actions, side by side in the BATCH EDIT card:**
1. **"Merge into one rule"** — destructive consolidation. The **anchor rule**
   (the active/last-plain-clicked rule, whose values the batch editor already
   displays) survives: it keeps its Id, name, colors, line/pattern settings, and
   list position. The other selected rules are removed from the trade.
2. **"Create combined rule"** — non-destructive. A NEW rule is appended to the
   trade with the same unioned definition (named "<anchor name> (combined)",
   editable immediately); all original rules are kept untouched. Useful when the
   individual filters are still wanted sometimes but a single combined filter is
   wanted for other views.

**Union semantics (both actions):**
- `Match` keywords → union, case-insensitive dedupe.
- `BuiltInCategories` → union.
- Graphics/overrides → taken from the anchor rule.
- **Guards:**
  - All selected rules must share the same `Parameter` — otherwise the button is
    disabled with an explanatory note (one filter rule can only bind one parameter).
  - Whole-category (`all`) and has-/has-no-value rules cannot merge with keyword
    rules — disabled with note.
  - Mixed `contains`/`equals` merge to `contains` (superset), stated in the
    confirm popup.
- **Confirm step** (house UX rule — explicit single action): a `StaysOpen=true`
  popup anchored to the button showing the resulting rule name, keyword list, and
  category list, with Merge (or Create) / Cancel. The popup states whether the
  source rules will be removed or kept.
- Afterward: clear multi-selection, refresh rule list + editor (for "create",
  the new rule becomes the active selection). Rules dropped by a destructive
  merge leave orphaned Revit filters that the next create pass cleans up via the
  existing `CreatedFilterNames` manifest (no extra Revit work needed).

---

## Phase 6 — Legend Creator: shared layout engine (preview = output)

**Problem.** The WPF preview (`LemoineLegendPreview`) and the Revit output
(`LegendCreatorEventHandler`) are two unrelated layout implementations, so the
preview cannot predict the generated legend:

| Divergence | Preview | Revit output |
|---|---|---|
| Text size | `FontPt` setting for everything; title ×1.4 | Each role's real `TextNoteType.TEXT_SIZE` (`FontPt` only as fallback) |
| `Gap` setting | Vertical gap between rows | Horizontal swatch→label gap (row gap hardcoded 0.8 ft) |
| Column width | Natural content width per group | Fixed stride: swatch + gap + 4 ft + 0.6 ft |
| Long labels | Never wrap | Wrap inside the 4 ft TextNote width → overlap the next entry (row pitch assumes one line) |
| Label vertical alignment | Centered on swatch | Placed at baseline = swatch center → text sits high |
| Group header underline | Colored underline drawn | Not created at all |

**Fix: one Revit-free layout core consumed by both sides.**

### 6.1 `LegendLayoutEngine` (new file, `Source/Tools/Testing/LegendCreator/`)
- Pure function: `(LegendLayoutConfig, rows, TextMetrics) → List<LegendPrimitive>`
  where primitives are `Text(role, x, baselineY, text)`, `SwatchRect(kind, fill,
  color, x0, y0, x1, y1)`, `UnderlineRect(...)` — **all coordinates in paper
  inches**, origin top-left.
- `TextMetrics` carries each role's text height (paper inches) and a width
  measurer. Label widths are measured once (WPF `FormattedText` at the role's
  paper height) and passed in, so both renderers see identical column widths.
- Column stride = widest measured label in the column + spacing (no fixed 4 ft).
- Row pitch per block = max(swatch height, label height) + spacing; group depth
  from actual block count — single-line labels guaranteed (see 6.3).
- Baseline math centralised: label baseline = swatch centerY + capHeight/2
  (TextNote Y is the baseline — documented Revit gotcha), so preview and output
  center identically.

### 6.2 Text metrics capture (Revit main thread)
- At window launch (same pattern as `CaptureFilterableCategories`), capture each
  TextNoteType's id, name, and `TEXT_SIZE` (paper feet → inches) into the
  settings the window thread reads. The per-role type pickers already exist in
  `LegendSettingsWindow` — the preview just never receives the sizes.
- Preview re-resolves metrics when a role's type selection changes.

### 6.3 `LegendCreatorEventHandler` consumes the engine
- Replace the bespoke layout loop with: run the engine, convert inches → feet
  (× scale ÷ 12), emit primitives.
- Create labels with the **non-wrapping `TextNote.Create` overload** (no width
  argument) — eliminates the wrap-overlap failure class entirely; the engine's
  measured widths drive column stride instead.
- Draw the group-header underline (thin filled region) so output matches the
  preview, or — if rejected in review — delete it from the preview.

### 6.4 `LemoineLegendPreview` consumes the engine
- Replace `Redraw`'s StackPanel layout with rendering the engine's primitives on
  a Canvas at 96 px/inch. Delete the `FontPt × 1.4` title factor; render each
  role at its captured paper height. `FontPt` remains only as the fallback for
  roles with no type selected (mirroring the handler).

### 6.5 Spacing settings made explicit
- `LegendLayoutConfig`: replace single `Gap` with `RowGap`, `ColGap`,
  `SwatchLabelGap` (paper inches). Migration in `Normalize()`: old `Gap` seeds
  `SwatchLabelGap`, new fields get defaults; old XML files load cleanly.
- `LegendSettingsWindow` sizing rows updated accordingly (`AddSizingRow`,
  ~line 1004–1011).

### 6.6 Crash fix (independent of the rework)
- `LemoineLegendLayoutBar.ShowEditPopup` uses `Popup { StaysOpen = false }`
  (`LemoineLegendLayoutBar.xaml.cs:171`) — in the documented Revit-crash table.
  Convert to `StaysOpen = true` + window-level `PreviewMouseDown` dismiss, as
  `LegendSettingsWindow` already does (its own popups note this).

### Also folded in from the T01 review
- `AutoFiltersLegendEventHandler` (the older filter-legend tool): null-guard the
  duplicated view, and route it through the same engine later — for now it keeps
  its quick-generate behaviour, with the option to retire it once the Legend
  Creator covers its use case.

---
## Suggested branch split (one logical change per branch)

1. `discover-scan-fixes` — Phases 1.1, 1.2, 2.1, 2.4, 2.6, 3.1, 3.2, 3.4, 3.5, 4 (Discover-side)
2. `filter-engine-fixes` — Phases 1.3, 2.2, 2.3, 2.5, 4 (engine-side)
3. `discover-chained-create` — Phase 3.3 (touches command wiring + VM)
4. `merge-rules-batch-editor` — Phase 5
5. `legend-layout-engine` — Phase 6 (the StaysOpen crash fix 6.6 can ride on any earlier branch if preferred)

Or a single branch if preferred. After each implementation pass: run the
post-change silent-failure scan per CLAUDE.md before committing.

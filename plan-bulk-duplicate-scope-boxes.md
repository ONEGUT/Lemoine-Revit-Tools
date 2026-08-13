# Plan — Bulk-Duplicate a View per Scope Box

## Goal

Add an **optional** mode to the existing "Duplicate Views" tool (Bulk Views →
Duplicate) that, instead of making one copy per selected source view, makes
**one copy per selected scope box** (for each selected source view), cropped
and bound to that scope box. The existing duplicate-mode choice (Duplicate /
Duplicate with Detailing / Duplicate as Dependent) is reused unchanged and
applies uniformly to every generated copy. Default is **off** — normal 1:1
duplication is untouched unless the user opts in.

## Where this lives

No new ribbon button, no new top-level tool. `LT_BulkViews` already opens a
merged `StepFlowWindow` (`BulkViewsViewModel`) whose "Duplicate" mode is
`ViewsBulkDuplicateViewModel` / `ViewsBulkDuplicateRunHandler`
(`Source/Tools/Views/ViewsBulkDuplicate*.cs`). This feature extends that
existing mode in place.

## UX structure (recommended — see chat for alternatives)

- **S1 (Source Views)** — unchanged `BrowserTreePicker`, plus a new checkbox
  below it: **"Create one copy per scope box"** (off by default).
- **New step "SB" (Scope Boxes)** — a `MultiSelectTabs` list of the
  project's scope boxes (`OST_VolumeOfInterest`), visible **only** when the
  S1 checkbox is on. Uses `IConditionalSteps` so the step's accordion row
  and pip disappear entirely when the checkbox is off.
- **S2 (Duplicate Mode)** — unchanged.
- **S3 (View Naming)** — unchanged control, but now offers a second,
  independently-remembered pattern (see Naming below) and a new
  `{ScopeBoxName}` token, shown only when scope-box mode is on.
- **S4 (Review & Run)** — adds a "Scope boxes" row and updates the
  "Views to create" total to `views × scope boxes` when the toggle is on.

When the toggle is off, every new code path is skipped and behavior is
byte-for-byte what it is today.

## Why a new step needs a small shared-file change

`BulkViewsViewModel` (the outer mode router) already implements
`IConditionalSteps`, but its `IsStepVisible` only checks which **mode** a
step belongs to — it does not consult the inner tool's own
`IConditionalSteps`, because no inner tool has ever needed one before. It
must delegate:

```csharp
public bool IsStepVisible(string stepId)
{
    if (stepId == "mode" || stepId == "run") return true;
    if (!stepId.StartsWith(_mode + "_", StringComparison.Ordinal)) return false;
    var innerId = stepId.Substring(_mode.Length + 1);
    return (CurrentInner as IConditionalSteps)?.IsStepVisible(innerId) ?? true;
}
```

This is additive and backward-compatible: the other four modes don't
implement `IConditionalSteps`, so `as IConditionalSteps` is `null` and the
`?? true` keeps their steps always-visible exactly as today.

## Files changed

1. **`Source/Tools/Views/BulkViewsViewModel.cs`** — `IsStepVisible`
   delegation fix above (~4 lines).

2. **`Source/Tools/Views/ViewsBulkDuplicateViewModel.cs`** — the bulk of the
   work:
   - New state: `_bindToScopeBoxes` (bool), `_scopeBoxes`
     (`List<ScopeBoxEntry>`, injected via constructor — see Command below),
     `_selectedScopeBoxIds`, a second name pattern
     `_scopeBoxNamePattern` (own `NamingPatternStore` key).
   - Implements `IConditionalSteps` (`IsStepVisible("SB") =>
     _bindToScopeBoxes`, else `true`).
   - `Steps` gains a `"SB"` entry between S1 and S2.
   - `BuildViewPicker()` (S1) grows a checkbox under the tree; toggling it
     calls `OnValidationChanged()` (this is what makes the outer accordion
     re-evaluate `IsStepVisible` live — same mechanism the mode dropdown
     already uses).
   - New `BuildScopeBoxPicker()` (SB) — single-tab `MultiSelectTabs` of
     scope-box names (empty-state message if the project has none).
   - `BuildNaming()` (S3) — builds **two** `TokenInput` panels (standard /
     scope-box), stacked in the same container, and toggles their
     `Visibility` from a `ValidationChanged` subscription (same pattern the
     existing preview-refresh subscription already uses) — no
     `IStepAware`/content-rebuild needed since both panels already exist in
     the live visual tree.
   - New computed token, declared beside the class:
     ```csharp
     private static readonly TokenDefinition[] ScopeBoxDuplicateComputedTokens = {
         new TokenDefinition("ScopeBoxName",
             AppStrings.T("naming.computed.duplicate.scopeBoxName.label"),
             TokenOrigin.Computed, TokenSubject.Target, TokenEntity.View,
             AppStrings.T("naming.computed.duplicate.scopeBoxName.desc")),
     };
     ```
   - `IsValid("SB")` → `!_bindToScopeBoxes || _selectedScopeBoxIds.Count > 0`.
   - `ReviewItems`/`ReviewValues`/`ReviewWarning` extended: scope-box count
     row, `views × scopeBoxes` total, and a new warning when scope-box mode
     is on but the active pattern has no `{ScopeBoxName}` (see Naming
     safety below).
   - `Run(...)` passes the new fields to the run handler.

3. **`Source/Tools/Views/ViewsBulkDuplicateRunHandler.cs`**:
   - New inputs: `BindToScopeBoxes`, `ScopeBoxes`
     (id/name/world-min/world-max), `SelectedScopeBoxIds`,
     `ScopeBoxNamePattern`.
   - `RunDuplicates` branches: unchanged 1:1 loop when off; when on, a
     nested loop (source view × selected scope box) that for each pair:
     resolves the name via `TokenResolver` with `ScopeBoxName` (and the
     existing `ViewName`/`ViewType`) in `TokenContext.Computed`, skips on
     name collision or `!CanViewBeDuplicated(option)` exactly like today,
     calls `view.Duplicate(option)` on the **original** source view (not
     chained off a prior copy — same approach
     `ReplicateDependentViewsRunHandler` already uses to mint several
     dependents from one parent), renames, then applies crop + scope box:
     ```csharp
     newView.CropBoxActive = true;
     newView.CropBoxVisible = true;
     var cb = newView.CropBox;
     cb.Min = box.WorldMin; cb.Max = box.WorldMax;
     newView.CropBox = cb;
     var p = newView.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
     if (p != null && !p.IsReadOnly) p.Set(box.Id);
     ```
     (same call sequence as `ReplicateDependentViewsRunHandler.ApplyCrop`,
     the proven reference for this exact operation). Unlike that existing
     helper, a failure here **logs a `pushLog` warning** (not just
     `DiagnosticsLog.Swallowed`) — for this tool, binding the crop *is* the
     point of the run, so a silent partial success would hide exactly the
     thing the user asked for.
     On any exception during duplicate/name/crop, the orphan copy is
     deleted, matching the existing catch block.
   - Cooperative cancel (`RunState.CancelRequested`) checked in the
     per-pair loop, same as the existing loop and
     `ReplicateDependentViewsRunHandler`'s nested loop.

4. **`Source/Commands/Views/BulkViewsCommand.cs`** — one-line change.
   `ScopeBoxCreatorScanHandler.CollectScopeBoxes(doc)` is **already**
   called here (line 70, currently only fed to the "By Level" mode) — pass
   the same `scopeBoxes` list into the `ViewsBulkDuplicateViewModel`
   constructor call. No new scan/event-handler pair needed.

5. **`Strings/en/linkviews.duplicate.json`** — new keys for the checkbox
   label/hint, the "SB" step title, the scope-box picker header/empty
   state, the second name-pattern header, the new review row, and the new
   warning. Existing `log.created` / `log.skipExists` / `log.failed` /
   `log.skipMode` keys are reused as-is (the message shape doesn't change).

6. **`Strings/en/naming.json`** (or wherever `naming.computed.*` keys
   live — confirming exact file at implementation time) — the new
   `ScopeBoxName` token's label/description, following the same
   `naming.computed.<tool>.<key>.label` convention
   `ReplicateDependentViewsViewModel` and `ScopeBoxCreatorViewModel` use.

No changes to `Source/Framework/IStepFlowTool.cs`, `MultiSelectTabs`,
`TokenInput`, `TokenResolver`, or `NamingTokenRegistry` — all existing
machinery is reused as-is.

## Naming safety (avoiding a silent under-count)

The existing tool skips a copy whenever its resolved name already exists.
If scope-box mode is on and the active pattern does **not** vary per scope
box (e.g. the plain default `"{ViewName} - Copy"`), every pair for the same
source view would resolve to the *same* name — only the first would be
created, the rest silently "skipped" as name collisions, and the user would
quietly get 1 view instead of N. Two guards against this:
- Scope-box mode gets its **own** default pattern and its own
  `NamingPatternStore` key (`"views.duplicateScopeBox"` vs. today's
  `"views.duplicate"`), defaulting to `"{ViewName} - {ScopeBoxName}"` so a
  first-time user already gets distinct names.
- `ReviewWarning` additionally flags (before the user can run) when
  scope-box mode is on and the current pattern has no `{ScopeBoxName}`.

## Fan-out scope

If more than one source view is selected together with scope-box mode, the
plan is to fan out **every selected view × every selected scope box**
(cross product), each pair guarded independently by `CanViewBeDuplicated`.
Selecting one view behaves exactly as described in the request; selecting
several is a natural generalization with no extra UI. Flagged in chat as
confirmable, not forced.

## Idempotent re-runs

No new Extensible Storage stamping. Re-running with overlapping
view/scope-box selections relies on the same name-collision skip the
existing tool already uses — consistent with today's Duplicate mode, and
proportionate to what was asked. Noted as a deliberate scope decision, not
an oversight.

## Known limitation carried over (not introduced by this change)

`ReplicateDependentViewsRunHandler.ApplyCrop` — the proven reference this
plan copies — only sets `View.CropBox`, which governs 2D crop (plan/
section/elevation/detail). It does not special-case `View3D.SetSectionBox`.
3D source views will get the scope-box **parameter** assigned (so the
Properties palette is correct) but may not visually re-crop the same way a
2D view does. This mirrors the existing tool's own behavior, not a
regression, and is treated as a follow-up rather than in-scope here.

## Testing

Cannot be built or run on Linux (WPF/.NET Framework 4.8, Windows-only SDK —
see CLAUDE.md Build Environment). Verification is a manual Windows/Revit
pass after merge: toggle off (regression-check unchanged 1:1 behavior),
toggle on with one view × several scope boxes in each duplicate mode, and a
naming-collision re-run.

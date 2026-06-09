# Plan — Bulk Views by Template (cross-multiply views × view templates)

## Goal
A new tool that takes **N source views** and **M view templates** and produces, for every
view×template pair, a **duplicate of the view with that template applied and a generated
name**. Example: 4 floor plans × 3 templates → up to 12 new views, each named from a
chip/token pattern (the same `LemoineTokenInput` control used by Bulk Export).

## Decisions (confirmed)
- **Duplicate mode:** `ViewDuplicateOption.WithDetailing` (copies view-specific annotations).
- **Type mismatch:** attempt to apply the template on every pair; if Revit rejects it
  (template view type ≠ view type), log a **fail** and delete the orphaned duplicate so the
  document is left clean.
- **Base branch:** `main`.

## UX / flow
Modelled on `LinkViewsLevelViewModel` (the existing T03 view-creation tool) and reusing the
established `StepFlowWindow` + `ILemoineTool` + `ExternalEvent` pattern.

Steps (accordion):
1. **S1 — Source Views** — `LemoineMultiSelectTabs`, views grouped by view type
   (Floor Plans / Ceiling Plans / Sections / Elevations / 3D / Drafting …). Only
   non-template, duplicatable graphical views (no Schedule/Legend). Pre-select none.
2. **S2 — View Templates** — `LemoineMultiSelectTabs`, all view templates grouped by their
   view type. Select the templates to apply.
3. **S3 — Naming** — `LemoineTokenInput` (the Bulk Export chip control). Tokens:
   `{ViewName}`, `{TemplateName}`, `{ViewType}`. Default pattern `{ViewName} - {TemplateName}`.
   Live preview resolved against the first selected view × first selected template.
4. **S4 — Review & Run** (`ILemoineReviewable`) — shows view count, template count, and
   "up to N new views", plus a note about name collisions / mismatches being skipped.

## Run logic (`ViewsByTemplateRunHandler`)
For each selected view × each selected template (inside one transaction):
- Skip (log) if `view.CanViewBeDuplicated(WithDetailing)` is false.
- Resolve the name from the pattern (`{ViewName}`, `{TemplateName}`, `{ViewType}`).
- Skip (log) if a view with that name already exists, or the name was already produced this
  run (collision guard against existing-names set + newly-created set).
- `newId = view.Duplicate(ViewDuplicateOption.WithDetailing)`.
- `try { newView.ViewTemplateId = templateId; newView.Name = name; pass++ }`
  `catch { delete newId; log fail }` — covers the incompatible-template case.
- Progress/log/complete via the standard callbacks.

## Files to add
- `Source/Commands/T03-LinkViews/ViewsByTemplateCommand.cs` — STA-thread launcher; collects
  available views + templates on the Revit main thread and opens `StepFlowWindow`.
- `Source/Tools/T03-LinkViews/ViewsByTemplateViewModel.cs` — `ILemoineTool` + `ILemoineReviewable`,
  4 steps, token naming, validation/summary, `Run()` wiring.
- `Source/Tools/T03-LinkViews/ViewsByTemplateRunHandler.cs` — `IExternalEventHandler` performing
  the duplicate/apply/rename transaction.

## Files to modify
- `Source/App.cs`
  - Add `ViewsByTemplateRunHandler` + `ExternalEvent` static properties and initialise them in
    `OnStartup` (mirrors the `LinkViewsLevelRunHandler` registration).
  - Add a ribbon button to the existing **T03 Bulk Views** panel
    (`"Views ×\nTemplates"`, command `ViewsByTemplateCommand`).

No `.csproj` change needed — the project uses default SDK compile items, so new files under
`Source/` are picked up automatically.

## Notes / constraints honoured
- STA thread + `Dispatcher.Run()` pump; `WindowInteropHelper.Owner`; no Revit API calls off
  the main thread; `ExternalEvent.Raise()` only after handler properties are set.
- `ConfigureFailures(tx)` at transaction start.
- All swallowed exceptions routed through `LemoineLog`; no empty catches.
- ViewModel file gets the WPF/Revit ambiguous-type aliases (`WpfTextBox`, `WpfVisibility`, …).
- Reuse `LemoineTokenInput`, `LemoineMultiSelectTabs`, `LemoineSingleSelect` — no hand-rolled UI.
- Working branch `claude/hopeful-bohr-5i10mz`, created from `origin/main`.

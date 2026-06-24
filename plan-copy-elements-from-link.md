# Plan — Copy Elements from Link (by Category → Family/Type)

## Goal

A new tool that copies elements **out of a chosen linked model into the host**, selected by:

1. **Source link** (which loaded link to pull from)
2. **Categories** (e.g. Mechanical Equipment, Plumbing Fixtures, Walls…)
3. **Families/Types within those categories** — the user ticks exactly which
   family-types to copy over

This is the plain-copy sibling of the existing **Copy Grids** tool, generalized to
any category and refined with a family/type pick step. Unlike **Copy Linear**, it does
**not** split or replace — it copies each matched element verbatim (link transform
applied) using the cross-document copy discipline already proven in `CopyLinearRunHandler`.

## Why this shape (mirrors existing T06 tools)

- Reuses the **scan → checklist → copy** pattern from `CopyGridsViewModel`.
- Reuses the **category multi-select grouped by discipline** pattern from
  `CopyLinearViewModel.BuildCategoryGroups()` / `AutoFiltersSettings`.
- Reuses the **cross-document `CopyElements` + `UseDestinationTypes`** handler pattern
  (suppresses the modal "Duplicate Types" dialog) and the **link-transform** resolution
  from `CopyLinearSource.Resolve`.
- Read of the link document happens on the **Revit main thread** via an `ExternalEvent`
  scan handler (same as `CopyLinearScanHandler`), because link documents can only be read
  on that thread.

## Proposed UX (StepFlowWindow / ILemoineTool)

Steps:

1. **Source** (required) — single-select source link + category multi-select
   (grouped by discipline, same as Copy Linear).
2. **Families** (required) — checklist of the distinct `Family: Type` names that actually
   exist in the chosen link for the selected categories, grouped under a tab **per category**
   (via `LemoineMultiSelectTabs.SetGroups`). Populated by a read-only scan triggered on
   step activation (`IStepAware.OnStepActivated`), exactly like Copy Linear's filter scan.
   Default: all ticked.
3. **Review & Run** (framework-rendered via `ILemoineReviewable`).

Recommended default: select-all families so the simplest path ("copy everything in these
categories") is one confirm. The family step lets the user narrow it.

## Files to add (new tool folder `Source/Tools/T06-CopyLinear/` siblings)

| File | Purpose |
|------|---------|
| `Source/Tools/T06-CopyFromLink/CopyFromLinkModels.cs` | `CopyFromLinkLinkInfo` (link name + id + uid), `CopyFromLinkTypeInfo` (category, FamilyTypeKey, sample element ids), `CopyFromLinkSpec` (link id, categories, selected type keys) |
| `Source/Tools/T06-CopyFromLink/CopyFromLinkScanHandler.cs` | Read-only `IExternalEventHandler`: for the chosen link + categories, enumerate distinct `Family: Type` keys and the element ids under each; return grouped result |
| `Source/Tools/T06-CopyFromLink/CopyFromLinkRunHandler.cs` | `IExternalEventHandler`: collect elements whose category is selected AND whose `Family: Type` key is ticked, then `ElementTransformUtils.CopyElements(linkDoc, ids, hostDoc, link.GetTotalTransform(), opts)` with `UseDestinationTypes`; one transaction, single regen, cancellation checkpoint, failure capture, full run-log counts |
| `Source/Tools/T06-CopyFromLink/CopyFromLinkViewModel.cs` | `ILemoineTool, ILemoineReviewable, IStepAware, ILemoineToolCleanup` — the three steps above |
| `Source/Commands/T06-CopyLinear/CopyFromLinkCommand.cs` | `IExternalCommand` launcher: `CaptureFilterableCategories`, collect loaded links, open `StepFlowWindow` on a dedicated STA thread (copy of `CopyLinearCommand` pattern) |

Reuse without duplication: `CopyLinearSource.Resolve` (link/transform), `AutoFiltersSettings`
(category groups + `KnownCategoryMap`), `LemoineMultiSelectTabs`, `LemoineSingleSelect`.

## Files to edit

| File | Change |
|------|--------|
| `Source/App.cs` | Add static `CopyFromLinkScanHandler`/`Event` + `CopyFromLinkRunHandler`/`Event`, create them in `OnStartup`, and add a ribbon button (`LT_CopyFromLink`, "Copy\nElements") to the Testing panel next to Copy Linear / Copy Grids |

## Behaviour / correctness notes (from CLAUDE.md)

- **Cross-doc copy:** use `CopyElements(srcDoc, ids, destDoc, transform, opts)` with
  `SetDuplicateTypeNamesHandler → UseDestinationTypes`. The `srcDoc == destDoc` overload throws,
  but the source here is always a real link, so the cross-doc overload is always correct.
- **Annotation categories excluded** — only model categories are offered (the request is about
  model families); `CategoryType == Model` filter, consistent with `CollectFamilies`.
- **Zero-result reporting:** scan and run must log "Found N…" / "No … found" — never a silent
  empty result.
- **Single regen** at end of the transaction; cooperative cancel checkpoint in the copy loop;
  `LemoineFailureCapture`/`SilentFailureHandler` wired so Revit's own failures land in the run log.
- **Memory:** scan/run handlers clear their payload in `finally`; the ViewModel nulls its
  parked callbacks in `OnWindowClosed`.
- No idempotent stamping in v1 (Copy Grids doesn't stamp either) — copies are plain elements.
  Can add an Extensible-Storage stamp later if re-run reconciliation is wanted.

## Out of scope (v1)

- Splitting / replacing / re-placement (that's Copy Linear).
- Parameter-value filtering (could be added later, mirroring Copy Linear's filter step).
- Idempotent re-run reconciliation (stamp schema) — deferred.

## Post-change silent-failure scan

Will run the mandatory scan over the diff before reporting complete.

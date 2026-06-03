# Plan — Ceiling Elevation Heatmap: always-on linked ceilings + diagnostics

## Problem

Running the Ceiling Elevation Heatmap on a model whose ceilings live entirely
in **linked** files fails with:

```
Found 8 distinct height offset buckets.
Could not resolve the ceiling height parameter.
```

Nothing is written to `diagnostics.log` because this is a graceful
`Log(..., "fail")` to the step-flow window, not a thrown exception.

### Root cause

`GetCeilingHeightParamId(doc)` (CeilingHeatmapEventHandler.cs:726) samples a
ceiling from the **host** document only:

```csharp
Element? sample = new FilteredElementCollector(doc).OfClass(typeof(Ceiling))...FirstOrDefault();
if (sample?.get_Parameter(BIP) != null) return bipId;
return ElementId.InvalidElementId;          // ← no host ceiling → fails
```

`new ElementId(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)` is **always**
a valid parameter id; the host-sample gate is the bug.

### Why the "contains vs equals" idea does not apply

The run exits at Phase 2 (param resolution), ~60 lines before the
`CreateEqualsRule` call (line 161), so the rule operator can't be the cause of
this error. Also, *Height Offset From Level* is a **Length** parameter;
`ParameterFilterRuleFactory.CreateContainsRule` only accepts string parameters
and would throw. Equals-with-tolerance is the correct rule for a numeric
elevation — keep it.

### What makes linked ceilings actually color

In **Revit 2024**, host view filters apply to linked elements **only when the
link's display is "By Host View"** (the default). Per the user's decision, the
tool will **not** change any link's display mode; instead it will **report**
links that are on *By Linked View* / *Custom* as skipped, so the user knows why
those ceilings aren't colored.

## Files changed

| File | Change |
|------|--------|
| `Source/Tools/T02-Ceilings/CeilingHeatmapEventHandler.cs` | Fix param resolution; always scan links; add diagnostics; report non-By-Host-View links |
| `Source/Tools/T02-Ceilings/CeilingHeatmapViewModel.cs` | Remove "Include linked ceilings" toggle + review row; always-on |
| `Source/Tools/T02-Ceilings/CeilingHeatmapSettings.cs` | Remove `IncludeLinks` setting |

## Changes in detail

### CeilingHeatmapEventHandler.cs
1. Delete `GetCeilingHeightParamId`; set
   `heightParamId = new ElementId(BuiltInParameter.CEILING_HEIGHTABOVELEVEL_PARAM)`
   directly. Remove the "Could not resolve" gate.
2. Phase 1: always call `ScanLinkedCeilings` (drop the `if (IncludeLinks)`).
   Track and log host-ceiling vs linked-ceiling counts per view.
3. Remove the `IncludeLinks` input property.
4. New diagnostic pass (after Phase 1, before filters): for each selected view ×
   visible link, read `view.GetLinkOverrides(link.Id)?.LinkVisibilityType`
   (default `ByHostView` when null). Log each link's mode; warn that links not on
   *By Host View* won't be colored. Wrap in try/catch → `LemoineLog`.
5. Route all new diagnostics through both the step-flow `Log(...)` and
   `LemoineLog.Info/Warn` so they persist to `diagnostics.log`.

### CeilingHeatmapViewModel.cs
- Remove the `links` `ToggleItem` from S2 and the `_includeLinks` field,
  `StateChanged` wiring, the `("links", ...)` review item + value, the
  `SummaryFor` "Include links" part, and the `Run()` persistence/handler
  assignment. Update `ReviewNote` to state linked ceilings are always scanned.

### CeilingHeatmapSettings.cs
- Remove the `IncludeLinks` property (back-compat: XML deserialize ignores the
  now-unknown element, so old settings files still load).

## Out of scope
- Forcing links to *By Host View* (user opted out).
- Tag placement already handles linked ceilings — unchanged.

## Branch
Develop on `claude/optimistic-albattani-1zA0r` (requested base
`auto-filter-issues-Q3icS` does not exist on the remote; building on the current
designated-branch tip instead).

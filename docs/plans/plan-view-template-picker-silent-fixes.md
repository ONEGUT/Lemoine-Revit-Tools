# Plan: View Template Picker + Silent Failure Fixes

## Silent failures found (scan results)

| # | Location | Issue | Severity |
|---|----------|--------|----------|
| 1 | `LinkViewsLevelRunHandler.RunViews` lines 184–186 | `FindVFT` returning null for FP/RCP silently skips all plan views with zero log output. Discipline tool correctly logs + fails; Level tool does not. | **High** |
| 2 | `LinkViewsLevelRunHandler.SetPlanCrop` line 413 | View range `try/catch` appends to `txLog` (logged as `"info"` at the end) but `pass` is already incremented before `SetPlanCrop` is called. A view with a broken cut plane counts as success. | **Medium** |
| 3 | `LinkViewsLevelHelpers.GetOrCreateViewSheetSet` lines 210–221 | Both `if (existing != null)` and `else` branches execute identical code — the branching is dead. The intent to "update existing" vs "create new" is not implemented differently, so set state may be wrong. | **Low** |
| 4 | `LinkViewsLevelPhase1Handler` | `CollectRooms` is called twice per source doc (once for `roomCounts`, once for `results`). Doubles scan time on large models. | **Perf** |

Fixes:
- **#1**: After computing `vftFP`/`vftRCP`, add `if (CreateFP && vftFP == null) Log(...)` / `if (CreateRCP && vftRCP == null) Log(...)` warn lines so users know why plan views weren't created.
- **#2**: Change `SetPlanCrop` view range catch to re-throw, moving the catch to the per-view try/catch in `RunViews` where `fail++` lives. View range failure → `fail++` and `"fail"` log; crop box still applied.
- **#3**: Collapse the redundant branches into one code path. The correct behaviour is to save to the existing set name (overwrite), which `vss.SaveAs` already does.
- **#4**: Merge the two `CollectRooms` passes into one: accumulate `byDoc[level][doc]` counts and build results in a single loop.

---

## View Template Picker

### New file
`Source/Tools/T03-LinkViews/LinkViewsViewTemplateEntry.cs` — shared data class used by both commands and both ViewModels.

```csharp
public sealed class ViewTemplateEntry
{
    public ElementId Id   { get; set; }
    public string    Name { get; set; }
}
```

### Files to change

| File | Change |
|------|--------|
| `LinkViewsViewTemplateEntry.cs` | New file — shared data class |
| `LinkViewsLevelCommand.cs` | Collect `templates3D`, `templatesFP`, `templatesRCP` on main thread; pass to ViewModel ctor |
| `LinkViewsLevelViewModel.cs` | Accept template lists in ctor; replace `BuildSubDiscRow` with `BuildViewTypeRow` (sub-disc + template on same row); track `_template3D/FP/RCP` ElementIds; wire to RunHandler in `Run()` |
| `LinkViewsLevelRunHandler.cs` | Add `Template3D/FP/RCP` ElementId properties; call `ApplyTemplate` before crop/section box |
| `LinkViewsDisciplineCommand.cs` | Collect `templates3D` on main thread; pass to ViewModel ctor |
| `LinkViewsDisciplineViewModel.cs` | Accept template list; extend sub-disc row to include template dropdown; wire to RunHandler |
| `LinkViewsDisciplineRunHandler.cs` | Add `Template3D` property; call `ApplyTemplate` on each created view |

### UI layout — combined row

Each view-type row (3D / FP / RCP) becomes:
```
[type  40px] [sub-disc TextBox 120px] [8px] [template LemoineSingleSelect 150px]
```
"(none)" is prepended to the template list as the default selection. Total row width ~318 px.

### Template application order

Template is applied **before** crop/section box so programmatic geometry always wins:
```csharp
private static void ApplyTemplate(View view, ElementId id)
{
    if (id == null || id == ElementId.InvalidElementId) return;
    try { view.ViewTemplateId = id; } catch { }
}
// Usage: ApplyTemplate(v, Template3D); then v.SetSectionBox(...);
```

### Template collection (in commands, on main thread)

```csharp
static List<ViewTemplateEntry> CollectTemplates(Document doc, ViewType vt) =>
    new FilteredElementCollector(doc)
        .OfClass(typeof(View))
        .Cast<View>()
        .Where(v => v.IsTemplate && v.ViewType == vt)
        .OrderBy(v => v.Name)
        .Select(v => new ViewTemplateEntry { Id = v.Id, Name = v.Name })
        .ToList();
```

### What is NOT changing
- Settings files, naming logic, S1/S3/S4 steps — unchanged.
- No new ExternalEvents — template list is captured on the command's main thread.

# Plan — Workset filtering per source document in Clash Definitions

## Goal
Let a clash group filter elements **in or out by workset**, per source document
(host + each linked model), with check/uncheck. Default = everything checked
(no filtering), so existing definitions are unchanged.

## Scope
Applies to **Rules** and **Categories** modes (the modes that scan whole
documents), mirroring how `SourceLinkIds` already works. **Elements** mode is
unaffected — the user has hand-picked exact elements there, so a workset filter
is meaningless.

Worksets only exist in workshared documents, so a document with no user
worksets simply shows no workset control.

## Files changed

### 1. `Source/Tools/T05-Clash/ClashShared/ClashGroupSpec.cs` (data model)
- Add to `ClashGroupSpec`:
  ```csharp
  // Per-source-document workset exclusions. Empty = no filtering (all worksets in).
  // Stored as EXCLUSIONS so the default (nothing listed) = "include everything",
  // keeping existing saved definitions unchanged. Rules + Categories modes only.
  public List<ClashWorksetFilter> WorksetFilters { get; set; } = new List<ClashWorksetFilter>();
  ```
- New persisted type (XmlSerializer-safe → must be `public`):
  ```csharp
  public sealed class ClashWorksetFilter
  {
      [XmlAttribute] public long LinkInstId { get; set; }       // 0 = host
      public List<int> ExcludedWorksetIds { get; set; } = new List<int>(); // unchecked worksets
  }
  ```
- Extend the UI-side `ClashDocInfo` (not part of saved definition) with the
  document's worksets, plus a `ClashWorksetInfo { int Id; string Name; }` type.
- Add `using System.Xml.Serialization;`.

### 2. `Source/Commands/T05-Clash/OpenClashDefinitionsCommand.cs` (capture, main thread)
- When building each `ClashDocInfo` (host + each link), read its user worksets
  on Revit's main thread:
  ```csharp
  if (d.IsWorkshared)
      foreach (var ws in new FilteredWorksetCollector(d).OfKind(WorksetKind.UserWorkset))
          list.Add(new ClashWorksetInfo { Id = ws.Id.IntegerValue, Name = ws.Name });
  ```
  Wrapped in try/catch → `LemoineLog.Swallowed` (workset reads can throw on
  detached/closed links).

### 3. `Source/Tools/T05-Clash/ClashDefinitions/ClashGroupEditor.cs` (UI)
- After the "Source documents" picker, add a **"Worksets (per model)"** section.
- See the UI-approach decision below for how the per-document checklist is rendered.
- Check state = NOT in `ExcludedWorksetIds`. Toggling updates
  `_spec.WorksetFilters` for that doc's `LinkInstId` (drop the entry entirely
  when nothing is excluded, to keep the spec clean), then `Notify()`.

### 4. `Source/Tools/T05-Clash/ClashFinder/ClashEngine.cs` (apply at scan time)
- Build a `Dictionary<long, HashSet<int>>` (linkId → excluded workset ids) from
  `spec.WorksetFilters` in `ScanGroupSpec`, pass into `ScanRules` / `ScanCategories`.
- Per source, look up that link's excluded set once; per element, after the
  rule/category match, drop it when its workset is excluded:
  ```csharp
  static bool PassesWorkset(Element el, HashSet<int>? excluded)
      => excluded == null || excluded.Count == 0
         || !excluded.Contains(el.WorksetId.IntegerValue);
  ```
  Wrapped so a workset read never aborts the scan.

## Persistence / back-compat
`WorksetFilters` defaults to an empty list → no behaviour change for existing
saved definitions; XmlSerializer just writes an empty element.

## Silent-failure scan
Will run the mandated post-change scan (workset reads, `WorksetId` access) before
reporting done.

## Open decision (UI approach) — see chat
Literal per-link dropdown popup vs. reusing the proven `LemoineMultiSelectTabs`
(tab per document, worksets as checkable items) right under the source picker.

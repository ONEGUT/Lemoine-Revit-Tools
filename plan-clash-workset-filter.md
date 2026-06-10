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
- Replace the source-documents `LemoineMultiSelectTabs` with an **inline document
  tree**: one row per document with a checkbox (is it scanned) and, when the
  document has worksets, a caret (▸/▾) that expands indented workset checkboxes.
- A workset row reads checked when the parent doc is selected and the workset is
  not in `ExcludedWorksetIds`. Toggling a workset updates `_wsExcluded`; toggling
  a document updates `_selectedDocs`.
- **Deselecting a document disables and unchecks its worksets** (an unselected
  model never shows selected worksets). `CommitSources` writes `SourceLinkIds`
  from the checked docs and emits a `ClashWorksetFilter` only for selected docs
  that have unchecked worksets.

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

## UI approach (decided)
Inline document tree: each document row carries a caret that expands its worksets
as indented checkboxes. Deselecting a document auto-clears (disables/unchecks) its
worksets so there is never an unselected model showing selected worksets. No popup
(avoids the Revit `Popup` crash constraints); rebuilt in place on each toggle.

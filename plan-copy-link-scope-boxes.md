# Plan — Scope Boxes as a copyable category in Copy Elements from Link

## 1. The finding

Scope Boxes are **already declared** everywhere the copy tools describe themselves, and the
option is **dead in every real document**:

| Where | What it says today |
|---|---|
| `CopyCategoryGroups.ExtraNonModelCategories` | lists `OST_VolumeOfInterest` + `OST_CLines` |
| `CopyCategoryGroups.Explicit` → `Datums & Reference` | lists `OST_VolumeOfInterest`, `OST_CLines` |
| `Strings/en/copy.fromLink.json` → `labels.catsHelp` | *"Datums & Reference holds grids, levels, **scope boxes** and reference planes."* |

But the picker never shows them. Root cause is in the capture:

```csharp
// AutoFiltersSettings.TryCaptureCategories
var ids = ParameterFilterUtilities.GetAllFilterableCategories();
foreach (var id in ids) { … if (cat.CategoryType != CategoryType.Model &&
                                 !allowedNonModel.Contains(bic)) continue; … }
```

`allowedNonModel` is only ever a **gate on categories already inside the filterable list** — it can
never *add* one. Scope Boxes and Reference Planes are not view-filterable in Revit (they are not in
Revit's own "Edit Filters → Categories" tree), so they never enter the loop and the extras are
silently dropped. The `extraNonModel` argument `CaptureCategoryMap` was built to honour has no
effect for exactly the two categories it exists to admit.

Grids and Levels work because they *are* filterable (only their `CategoryType` is non-Model) — which
is why the shared `AllowedNonModelCategories` mechanism looked like it would carry Scope Boxes too.

The hardcoded no-document fallback (`ExtraFallbackEntries`) does contain both, so the categories
appear only in the preview app where no `Document` exists — never in Revit.

This is the CLAUDE.md failure mode verbatim: *"An option can be fully plumbed … and still be dead,
because no call site passes it into the options object. Nothing fails, nothing logs."*

## 2. Changes

### 2.1 `Source/Tools/FiltersLegends/AutoFiltersSettings.cs` — make `CaptureCategoryMap` honour its contract

`CaptureCategoryMap(doc, extraNonModel)` is a **copy-tools-only** entry point (sole caller:
`CopyCategoryGroups.Capture`). Auto Filters reads `CaptureFilterableCategories` and is untouched.

After the shared `TryCaptureCategories` pass, resolve every requested extra **directly** through
`Category.GetCategory(doc, bic)` and add any that the filterable pass did not already produce:

- Uses `Category.GetCategory(Document, BuiltInCategory)` — confirmed present in `libs/RevitAPI.dll`
  metadata (`Autodesk.Revit.DB.Category GetCategory(Document, BuiltInCategory)`), read via `dnfile`
  rather than string-searched (CLAUDE.md Research Discipline).
- Real `Category.Name` is used, so the display token matches Revit's own wording.
- A display-name collision with an already-captured category is qualified with `[OST_…]`, the same
  guard `TryCaptureCategories` applies, so map keys stay unique.
- A category that cannot be resolved is reported via `DiagnosticsLog.Warn` — never dropped silently.
- A **failed** filterable pass still returns `null`, exactly as before. Returning a map holding only
  the two extras would look healthy to the caller, become the snapshot, and silently strip every
  model category from both copy pickers. `null` sends the caller to its hardcoded fallback, which
  already lists Scope Boxes and Reference Planes.

### 2.2 `Source/Tools/CopyFromLink/CopyCategoryGroups.cs` — report what was actually captured

`Capture` gains a check that every category in `ExtraNonModelCategories` survived into the snapshot,
and warns naming the missing ones. A zero-result capture already warns; this covers the partial case
that hid this bug (a full map that is quietly missing two entries).

### 2.3 `Source/Tools/CopyFromLink/CopyFromLinkRunHandler.cs` — scope-box name uniqueness

Revit enforces **unique scope box names**, the same constraint that makes Copy Datums skip-and-log
clashing grids and levels. Copying a scope box whose name already exists in the host is not a tidy
duplicate, it is a name Revit cannot accept.

- Before the copy loop but **inside the transaction, after the delete-previous pass** (so a re-run
  with delete-previous on still refreshes its own outputs rather than skipping them against names it
  is about to remove), read the host's scope box names and drop clashing sources from `toBuild`.
- Each skip is logged by name, counted into `skip`, and `total` is re-derived so the progress
  percentages stay honest.
- Only runs when the build set actually contains a scope box — every other category is untouched.

`SourceElem` gains `IsScopeBox` + `Name`, read once while gathering sources.

### 2.4 `Strings/en/copy.fromLink.json` — text only

- `labels.familiesHelp` notes that categories with no family types (scope boxes, reference planes)
  are listed by element name.
- Three new `log.*` keys for the scope box name-clash reporting.

## 3. Why the rest of the pipeline already works

Traced from the picker to the Revit call (CLAUDE.md: *"trace it to the Export/Set/Create argument"*):

| Stage | Behaviour for a scope box |
|---|---|
| `CopyLinearSource.Collect` | `OfCategory(OST_VolumeOfInterest).WhereElementIsNotElementType()` — the same collector `ScopeBoxCreatorScanHandler` already uses |
| `CopyFromLinkSource.TypeKey` | no `ElementType`, so it falls through to `el.Name` → each scope box is listed under its own name |
| `CopyFromLinkScanHandler` | groups by `Category.Name` → a "Scope Boxes" tab in the Families step |
| `CopyFromLinkSource.GeoHash` | `get_BoundingBox(null)` returns the box extents → move/resize is change-detected |
| `CopyFromLinkRunHandler` | cross-document `ElementTransformUtils.CopyElements` with the link transform — non-view-specific model-extent element, same path as grids/levels |

## 4. Not in scope

- **Reference Planes** (`OST_CLines`) become visible by the same fix — they were added by the same
  earlier commit and are left in place. Not removed, not separately verified.
- Copy Linear shares `CopyCategoryGroups`, so its category picker gains the same two entries. It
  filters to elements with a straight `LocationCurve`, so they collect nothing and its scan reports
  the zero count — the "Datums & Reference" tab already behaved this way for Grids/Levels.

## 5. Needs a Windows/Revit run to confirm

- Scope Boxes appear in the Categories step under "Datums & Reference".
- A cross-document `CopyElements` of a scope box lands in the host at the link's position.
- Whether Revit auto-renames or refuses a clashing scope box name — the skip-and-log guard makes the
  question moot in practice, but the assumption behind it is untested on a real model.

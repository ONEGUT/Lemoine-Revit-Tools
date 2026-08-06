# Data Storage Audit — What We Save, Where, and Where It *Should* Live

Audit of every byte the plugin persists, and a proposed re-scoping. No code changes yet — this is the picture and the decision list.

---

## 1. The headline

**There is effectively one storage tier.** Everything the user configures lands in a single per-Windows-user folder:

```
%AppData%\Roaming\LemoineTools\
```

There is **no project tier and no shared/company tier.** The only thing written into the `.rvt` is five Extensible Storage stamps on individual elements — and those exist purely for idempotent re-runs, never for settings.

That single tier is the root cause of both halves of the complaint:

- **Persists across projects but shouldn't** — project-scoped data (raw `ElementId`s, "which filters do I own in this model", "which legend view did I create") sits in a machine-wide file. Open a second project and it's still there, pointing at elements that don't exist or, worse, at *different* elements with the same id.
- **Should persist across projects but doesn't** — company standards (trades, clash definitions, legend templates, naming tokens) are locked in one user's `AppData`. Nobody on the team shares them, and there's no network path support. AutoFilters has manual export/import; nothing else does.

---

## 2. Full inventory

### 2.1 Machine-wide — `%AppData%\LemoineTools\`

| # | File | Written by | Contents | Natural scope |
|---|------|-----------|----------|---------------|
| 1 | `UISettings.xml` | `Framework/AppSettings.cs` | Theme, UI size, language | **Machine** ✅ |
| 2 | `diagnostics.log` + `diagnostics.prev.log` | `Framework/DiagnosticsLog.cs` | Rolling log, rotates at 1 MB | **Machine** ✅ |
| 3 | `colorpicker.xml` | `Framework/Controls/Color/ColorPickerPanel.xaml.cs` | Named colour sets + recent swatches, active set index | **Machine** ✅ |
| 4 | `NamingPatterns.xml` | `Framework/Naming/NamingPatternStore.cs` | `toolId → last-used {Token} pattern` | **Machine** ✅ (arguably Shared) |
| 5 | `NamingTokens.xml` | `Framework/Naming/UserTokenStore.cs` | User-defined tokens → Revit param (GUID-first) | **Shared** ⚠️ |
| 6 | `Templates\<toolId>\*.xml` | `Framework/Templates/TemplateStore.cs` | Named reusable templates. 3 consumers: AutoFilters trades, Legend Creator layouts, Ceiling colour ramps | **Shared** ⚠️ |
| 7 | `LemoineAutoFiltersV2.xml` | `Tools/FiltersLegends/AutoFiltersSettings.cs` | Trades → Categories → Rules (colours, patterns, keywords) **+ `CreatedFilterNames`** | **Split** 🔴 |
| 8 | `ColorMemory.xml` | `Tools/FiltersLegends/ColorMemory.cs` | Parameter-value → hex colour overrides | **Shared** ⚠️ |
| 9 | `LegendCreatorSettings.xml` | `Tools/FiltersLegends/LegendCreator/LegendCreatorSettings.cs` | Legend entries: layout, rows/groups/blocks, **`RevitViewId`, `TitleTypeId`, `SubtitleTypeId`, `GroupHeaderTypeId`, `LabelTypeId`** | **Split** 🔴 |
| 10 | `ClashDefinitions.xml` | `Tools/Dimensioning/ClashDefinitions/ClashDefinitionsSettings.cs` | Named clash definitions; each has two `ClashGroupSpec`s carrying **`ElemIds`, `ElemLinkIds`, `SourceLinkIds`** | **Split** 🔴 |
| 11 | `ClashDimensionSettings.xml` | `Tools/Dimensioning/ClashShared/ClashDimensionSettings.cs` | Tolerances, styles, lanes **+ `Group1/2ElemIds`, `Group1/2ElemLinkIds`, `Group1/2SourceLinkIds`, `GridIds`, `FloorIds`, `GridLinkIds`, `FloorLinkIds`** | **Split** 🔴 |
| 12 | `AutoDimension.xml` | `Tools/Dimensioning/AutoDimension/AutoDimensionConfig.cs` | Engine tuning: weights, tolerances, callout thresholds, `DimensionTypeName` | **Machine** ✅ |
| 13 | `LayoutSnapshots\<ts>_<view>.xml` | `Tools/.../Core/LayoutSnapshot.cs` | Opt-in debug dumps (`DumpLayoutSnapshots`). Obstacles, planned dims, scores. **Never pruned** | **Machine** ✅ (see §4) |
| 14 | `BulkExportSettings.xml` | `Tools/Export/BulkExportSettings.cs` | Formats, PDF/DWG/NWC/IFC options, filename patterns, **`OutputFolder`** | **Split** 🟡 |
| 15 | `CeilingHeatmapSettings.xml` | `Tools/Ceilings/CeilingHeatmapSettings.cs` | Low/mid/high colours, tolerance, tag/delete flags | **Machine** ✅ |
| 16 | `MakeCeilingGridsSettings.xml` | `Tools/Ceilings/MakeCeilingGridsSettings.cs` | **`OutputFolder`**, subfolder flag | **Split** 🟡 |
| 17 | `ScopeBoxSettings.xml` | `Tools/Views/ScopeBoxes/ScopeBoxSettings.cs` | Buffer, cluster threshold, level heights (all feet) | **Machine** ✅ |
| 18 | `CopyFromLinkSettings.xml` | `Tools/CopyFromLink/CopyFromLinkSettings.cs` | `DeletePrevious`, `OnlyChanged`, `DeleteOrphans` | **Machine** ✅ |
| 19 | `CopyLinearSettings.xml` | `Tools/CopyFromLink/CopyLinearSettings.cs` | Mode, spacing, offsets, rotations, **`FamilyKey`, `LengthParamName`** | **Split** 🟡 |
| 20 | `UpgradeLinksSettings.xml` | `Tools/Setup/UpgradeLinksSettings.cs` | Placement, destination, audit/reload flags, **`LastSelectedFolder`** | **Split** 🟡 |
| — | `BatchExportSettings.xml` | *(legacy)* | Read once, migrated into #14, then left behind | Dead |
| — | `LemoineAutoFilters.xml` | *(legacy V1)* | Explicitly **not** migrated. Orphaned on every existing machine | Dead |

**20 live files, 2 dead files, 2 subfolders.** All XML via `XmlSerializer` except `colorpicker.xml` (hand-rolled `XDocument`) and the plain-text log.

Common shape: `public sealed class X { static Lazy<X> _lazy; public static X Instance; Save()/Load() }` — a process-wide singleton, loaded once on first touch, saved on every change. **This is the mechanism that makes cross-project bleed invisible:** the singleton is never invalidated when the active document changes.

### 2.2 In the Revit document — Extensible Storage

Five schemas, all `AccessLevel.Public`, all stamped onto **created elements** (not on a settings-holder element):

| Schema | GUID | Fields | Purpose |
|--------|------|--------|---------|
| `LemoineClashTag` | `7D1F3A52-…` | group key (string) | Marker grouping |
| `LemoineUserCallout` | `9C5E21B8-…` | version, membershipRect, appliedRect | User-drawn callout adoption |
| `LemoineAutoDimOwner` | `1B7E4C90-…` | version, runId, targetKey | Ownership of generated dimensions |
| `LemoineCopyLinearStamp` | `9E2C5A41-…` | version, key, hash, runId, mode | Idempotent re-run reconciliation |
| `LemoineCopyFromLinkStamp` | `3C7A1E92-…` | version, key, hash, runId | Idempotent re-run reconciliation |

These are **correct and well-designed** — self-healing, no external database, discoverable via `ExtensibleStorageFilter`. They are the proof the project-tier pattern already works here; it just hasn't been applied to settings.

### 2.3 Not user data

- `Strings/<culture>/*.json` — shipped read-only localization assets next to the DLL.
- Export outputs (PDF/DWG/NWC/IFC) — user-chosen destinations, out of scope.
- No `DataStorage` elements, no shared-parameter file writes, no registry use.

---

## 3. The bugs, ranked

### 🔴 3.1 `CreatedFilterNames` — cross-project filter deletion

`AutoFiltersSettings.CreatedFilterNames` is a **per-document manifest** ("which `ParameterFilterElement`s does this tool own?") stored in a **machine-wide** file.

`AutoFiltersEventHandler.cs:392` reads it to drive an orphan-removal pass; `:507` overwrites it with the current document's result.

Consequence: run in Project A, open Project B, run again → B's orphan pass walks A's manifest and deletes any filter in B matching a name from A. And A's manifest is now gone, so A's real orphans are never cleaned. **This deletes model elements based on another project's state.** Highest severity in the audit.

### 🔴 3.2 Raw `ElementId`s in machine-wide files

`ClashDimensionSettings` stores ten lists of raw `long` element ids. `ClashGroupSpec` (inside every saved `ClashDefinition`) stores three more. `LegendEntry` stores five.

An `ElementId` is meaningless outside its document. In another project each id either resolves to nothing or — the dangerous case — to an unrelated element that happens to share the number.

Concretely:
- **Legend Creator** reads `RevitViewId != -1` and shows **"Update Legend"** instead of "Create Legend" (`LegendSettingsWindow.xaml.cs:1010`), then targets `new ElementId(entry.RevitViewId)` in a project that never had that legend.
- The stored `TitleTypeId`/`LabelTypeId` are `TextNoteType` ids. CLAUDE.md already documents that `TextNote.Create` with a type id from another project **throws without a clear message**.
- **Clash definitions** are meant to be a reusable library, but any definition saved in "Elements" mode silently carries Project A's picks into Project B.

### 🟡 3.3 Output folders remembered globally

`BulkExportSettings.OutputFolder`, `MakeCeilingGridsSettings.OutputFolder`, `UpgradeLinksSettings.LastSelectedFolder`.

Export Project A to `…\ProjectA\Deliverables`, open Project B, hit export → files land in Project A's delivery folder. `UpgradeLinksSettings.cs:22` explicitly documents this as intentional ("remembered generally (not per-project)") — worth revisiting, because "last folder I used" and "this project's delivery folder" are different concepts.

### 🟡 3.4 Name-based references that fail silently

`CopyLinearSettings.FamilyKey` / `LengthParamName`, `ClashDimensionSettings.DimStyleName` / `CrossLineTypeName`, `AutoDimensionConfig.DimensionTypeName`, `BulkExportSettings.DwgExportSetupName`.

These are strings, so they're portable in principle and degrade rather than corrupt. But if Project B lacks the named type, the tool falls back with no clear signal that a remembered setting was dropped. Lower severity, but it's the same "last project wins" pattern.

### ⚠️ 3.5 Nothing is shareable across a team

Trades, clash definitions, legend templates, naming tokens and colour memory are **company standards**. Today each user has a private copy in their own `AppData`, and only AutoFilters trades have export/import (`TryImportFrom` / `ExportTo`). Two people on the same job produce differently-coloured filters and there's no mechanism to converge.

---

## 4. Housekeeping found along the way

- `LayoutSnapshots\` is written per view per run when `DumpLayoutSnapshots` is on, and **never pruned** — unbounded growth on any machine that once enabled it.
- `BatchExportSettings.xml` is migrated but never deleted.
- `LemoineAutoFilters.xml` (V1) is orphaned by design on every existing install.
- `diagnostics.log` is the only file with a size policy (1 MB, one generation).

---

## 5. Proposed model — three tiers

| Tier | Location | Holds | Lifetime |
|------|----------|-------|----------|
| **Machine** | `%AppData%\LemoineTools\` | My preferences and my defaults | Per Windows user |
| **Shared** | Configurable network path, falls back to Machine | Company standards | Per office/team |
| **Project** | Extensible Storage on a `DataStorage` element in the `.rvt` | Anything naming an element in *this* model | Travels with the file |

**The test for Tier 3:** *does this value name something inside one specific model?* If yes — `ElementId`, a filter-ownership manifest, a "which view did I create" link — it belongs in the document.

### Tier assignment

**Machine** — `UISettings`, `diagnostics`, `colorpicker`, `AutoDimension` tuning, `CeilingHeatmapSettings`, `ScopeBoxSettings`, `CopyFromLinkSettings`, `CopyLinearSettings` (geometry/mode only), `BulkExportSettings` (format options only), `NamingPatterns`.

**Shared** — `NamingTokens`, `ColorMemory`, `Templates\*`, AutoFilters **trade/category/rule definitions**, `ClashDefinitions` **minus** the element picks.

**Project (new)** — `CreatedFilterNames`; every `ElemIds`/`ElemLinkIds`/`SourceLinkIds` list; `GridIds`/`FloorIds` + their link ids; `LegendEntry.RevitViewId` and the four `TextNoteType` ids; optionally the per-project output folder.

Note the two 🔴 files **split across tiers** — the definition is a portable standard, the ids are project state. They need separating, not just relocating.

### Two mechanical fixes that apply regardless of tiering

1. **Invalidate the singletons on document change.** Every `Instance` is a `Lazy<T>` that survives document switches. Even with perfect tiering, a project-tier store must reload when the active document changes.
2. **Report dropped references, don't fall back silently.** Per CLAUDE.md's silent-failure standard, a remembered `ElementId`/type name that doesn't resolve in the current document should log, not vanish.

---

## 6. Decisions needed

1. **Project-tier mechanism** — Extensible Storage on a `DataStorage` element (travels with the file, survives Save As, needs schema versioning) vs. a sidecar file beside the `.rvt` (easy to inspect/diff, breaks on Save As and on cloud/BIM360 models where `PathName` is a local cache). Cloud models make the sidecar option materially worse.
2. **Shared tier** — build it now, or defer and keep everything Machine until the project tier lands?
3. **Output folders** (§3.3) — per project, or keep the current "last used anywhere"?
4. **Migration** — on first run, split the existing machine-wide files and drop the project-scoped parts on the floor (clean, users re-pick), or attempt to attribute them to the first document opened (risky, and the ids are probably wrong already)?
5. **Housekeeping** (§4) — prune `LayoutSnapshots`, delete the two dead legacy files?

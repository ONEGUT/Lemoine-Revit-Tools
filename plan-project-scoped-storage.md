# Plan — Project-Scoped Storage via Extensible Storage

Implements the re-scoping from `data-storage-audit.md`. Moves project-scoped data out of machine-wide `%AppData%` XML and into the `.rvt` via Extensible Storage on a `DataStorage` element.

**Decisions taken:** Extensible Storage (not sidecar) · Legend Creator included · legacy files pruned · shared tier deferred · output folders per project.

---

## 1. API verification (done, on Revit 2024 metadata)

Read from `libs/RevitAPI.dll` metadata with a CLI metadata reader — not a string search (per CLAUDE.md).

| Member | Verdict |
|---|---|
| `Autodesk.Revit.DB.ExtensibleStorage.DataStorage` | Exists. **Note the namespace — `…DB.ExtensibleStorage`, not `…DB`** |
| `DataStorage.Create` | Exists, **static**, on `DataStorage` itself |
| `SchemaBuilder.AddSimpleField` / `AddArrayField` / `AddMapField` | All exist (instance) |
| `Entity.Get` / `Set`, `Schema.Lookup`, `ExtensibleStorageFilter` | Already proven by 5 working in-repo call sites |

Design uses only `AddSimpleField(name, typeof(string))`, which is exercised by every existing schema in the repo.

---

## 2. The binding constraint (shapes everything below)

Commands run on **Revit's main thread with the document**; tool windows run on their **own STA thread with no document**. And ES writes **require an open transaction**.

So the current model — a static `Lazy<T>` singleton that calls `Save()` on every UI change — **cannot work for project data**. Reads and writes must be split:

- **Read** — on the main thread at command launch, no transaction needed. Same pattern as the existing `BrowserTreeCapture.Capture(doc)` / `AutoFiltersSettings.CaptureFilterableCategories(doc)` main-thread captures. Snapshot is handed to the ViewModel.
- **Write** — inside an `ExternalEvent` handler, in a transaction.

CLAUDE.md forbids an "Apply" button (settings auto-save on change), so writes are flushed at two points rather than per-keystroke:

1. **During the tool's existing run transaction** — free, no new event.
2. **On window close** — a single shared `ProjectSaveEvent` raised from `IToolCleanup.OnWindowClosed`, so a pick made and *not* run is still preserved.

A per-change `ExternalEvent.Raise()` is explicitly rejected: one transaction per keystroke would flood the undo stack.

---

## 3. New: `Source/Framework/Project/`

### `ProjectDataSchema.cs`
ES schema `LemoineProjectData`, new hardcoded GUID, `AccessLevel.Public`, fields:

| Field | Type | Purpose |
|---|---|---|
| `Version` | `int` | Schema version for forward migration |
| `Payload` | `string` | The whole `ProjectDataDto` as XML |

**One string field, not a field-per-setting.** This reuses the `XmlSerializer` DTOs the tools already have, so adding a project-scoped field later is a DTO change with no schema migration. Payload is small — id lists and short name lists, no geometry.

Written onto a **single dedicated `DataStorage` element** per document (found via `FilteredElementCollector.OfClass(typeof(DataStorage))` + `ExtensibleStorageFilter`, created on first write). Not stamped onto model elements — this is document state, unlike the 5 existing per-element provenance stamps, which stay exactly as they are.

### `ProjectData.cs` — the DTO
```
ProjectDataDto
  Version                : int
  CreatedFilterNames     : List<string>              // AutoFilters ownership manifest
  LegendLinks            : List<LegendLinkDto>       // keyed by LegendEntry.Id
  ClashPicks             : List<ClashPickDto>        // keyed by ClashDefinition.Id
  ClashDatums            : ClashDatumsDto            // GridIds/FloorIds + link ids
  OutputFolders          : List<OutputFolderDto>     // keyed by toolId
```

**Association keys already exist** — `LegendEntry.Id` and `ClashDefinition.Id` are stable strings. The machine-wide file keeps the reusable *definition*; the project file keeps only `definitionId → this model's ids`. That is the split the audit called for, and it's why no data is duplicated across tiers.

### `ProjectStore.cs`
```
static ProjectDataDto Load(Document doc)                 // main thread, no transaction
static bool           Save(Document doc, ProjectDataDto) // main thread, INSIDE a transaction
static void           Stage(ProjectDataDto)              // STA thread: queue for next flush
```
`Load` returns an empty DTO (never null) when the document has no store yet. Every failure path routes through `DiagnosticsLog` per CLAUDE.md — a document that can't be read must say so, not silently look empty.

### `ProjectSaveEventHandler.cs`
One `IExternalEventHandler` registered in `App.cs` alongside the existing events. Opens a transaction, writes the staged DTO, clears the staged payload in a `finally` (per CLAUDE.md's memory-discipline rule for static handlers).

---

## 4. Per-tool changes

### 4.1 AutoFilters — `CreatedFilterNames` (the 🔴 deletion bug)
- `AutoFiltersSettings.cs` — **remove** the `CreatedFilterNames` property.
- `AutoFiltersEventHandler.cs:392` — read the manifest from `ProjectStore.Load(doc)` instead. This is the fix: the orphan-removal pass now walks *this document's* manifest.
- `AutoFiltersEventHandler.cs:507` — write back through `ProjectStore` inside the run's existing transaction.
- `FiltersSettingsWindow.xaml.cs:444` — read from the captured snapshot.

### 4.2 Legend Creator
- `LegendCreatorSettings.cs` — remove `RevitViewId`, `TitleTypeId`, `SubtitleTypeId`, `GroupHeaderTypeId`, `LabelTypeId` from `LegendEntry`. Layout/rows/groups/blocks stay machine-wide (reusable).
- `LegendSettingsWindow.xaml.cs` — `:802` (duplicate clears the id), `:1010` / `:1027` / `:1266` (Create-vs-Update label), `:1271` (target id), `:1303` (write-back) all resolve through the project snapshot.
- The existing *"stale view id → fresh legend created instead"* fallback at `:1288` becomes dead code and is removed — it was a band-aid for exactly this bug, and keeping it would mask a real stale link.

### 4.3 Clash Finder / Clash Definitions
- `ClashGroupSpec.cs` — remove `ElemIds`, `ElemLinkIds`, `SourceLinkIds`.
- `ClashDimensionSettings.cs` — remove all 10 id lists. Tolerances/styles/lanes stay machine-wide.
- `ClashEngine.cs:487` (`ScanElements`) and `:499` (`sourceLinkIds`) take the ids from the run's project snapshot.
- `ClashDefinitionsSettings.cs:175-191` — the `ClashDimensionSettings → ClashDefinition` import helper stops copying id lists.

**Behaviour note worth confirming:** `ClashGroupSpec.SourcesExplicit` currently distinguishes "empty means scan everything" from "empty means scan nothing". That flag is *definition* state and stays machine-wide, while `SourceLinkIds` moves to the project. A definition opened in a new project will have `SourcesExplicit = true` with no ids → "scan nothing". Plan is to treat a **missing project entry** (as opposed to an empty one) as "not yet picked for this model" and fall back to scan-all, logging the fallback.

### 4.4 Output folders (per project)
- `BulkExportSettings.OutputFolder`, `MakeCeilingGridsSettings.OutputFolder`, `UpgradeLinksSettings.LastSelectedFolder` move to `ProjectDataDto.OutputFolders`, keyed by tool id.
- `GlobalSettingsWindow.ToolGroups.cs` — the three folder rows read/write the project snapshot. Rows are **disabled with an explanatory hint when no document is open**, since a per-project folder is meaningless without one.
- `UpgradeLinksSettings.cs:22`'s comment ("remembered generally (not per-project)") is removed — it documents the behaviour being reversed.

---

## 5. Legacy pruning

New `LegacyFileCleanup.cs`, run once at startup, each delete logged via `DiagnosticsLog.Info`:

| File | Action |
|---|---|
| `BatchExportSettings.xml` | Delete **only after** confirming `BulkExportSettings.xml` exists (migration already ran) |
| `LemoineAutoFilters.xml` (V1) | Delete — explicitly never migrated |
| `LayoutSnapshots\` | Retain newest 50, delete the rest (currently unbounded) |

---

## 6. Migration of existing project-scoped values — **needs your call**

You didn't pick on this one. **Recommendation: drop, don't migrate.**

The values sitting in those machine-wide files today belong to whichever project last ran. Attributing them to the next document opened would be guessing, and a wrong `ElementId` is worse than an absent one — it resolves to a real but unrelated element. Dropping means users re-pick once, per project, and every subsequent pick is correct.

Concretely: on first run the project-scoped fields are read from the old files, **logged**, and discarded. Nothing silently disappears.

Say the word if you'd rather attempt attribution and I'll adjust §6 only.

---

## 7. Out of scope

Shared/company tier (deferred by your call). `NamingTokens.xml`, `ColorMemory.xml`, `Templates\*` and the AutoFilters trade definitions stay machine-wide for now — §5 of the audit stands as the record of why they'll want revisiting.

---

## 8. Risks

1. **`SourcesExplicit` semantics** (§4.3) — the one place where splitting a DTO changes behaviour rather than just relocating it. Called out above; will land with a run-log line either way.
2. **No Linux build.** Per CLAUDE.md this cannot be compiled here — it needs a Windows build and a Revit plot before it can be called done. I'll flag it as unverified on delivery.
3. **Schema GUID is permanent.** Generated once, hardcoded, never regenerated — same discipline as the 5 existing schemas.
4. **`DataStorage` survives Save As** (the reason it beat a sidecar), but a *detached* central model still carries it. Harmless — the ids remain valid for the detached copy.

---

## 9. Post-change

Mandatory silent-failure scan per CLAUDE.md before commit, plus a `CLAUDE.md` addition capturing the two new durable constraints: the `DataStorage` namespace gotcha, and the read-on-main-thread / write-in-transaction split for project settings.

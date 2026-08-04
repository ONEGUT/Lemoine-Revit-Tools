# Plan v2 — Project-Scoped Storage via Document-Owned Keys

Supersedes v1, which was rewritten after adversarial review. Implements the re-scoping from `data-storage-audit.md`.

**Decisions carried over:** legacy pruning · shared tier deferred · output folders per project.
**Decision reversed:** v1 stored a settings blob in the `.rvt` via a `DataStorage` element. **That is dropped.** See §1.

---

## 1. Why v1 was wrong

v1's load-bearing premise was *"association keys already exist — `LegendEntry.Id` and `ClashDefinition.Id` are stable strings."* Verified false:

- `LegendCreatorSettings.cs:323` — the default entry hardcodes `Id = "legend_seed_1"`. Identical on **every install**.
- `LegendCreatorSettings.cs:404-410` — `LegendIdGen.New` returns `{prefix}_{Ticks % 100000}_{n}` off a process-static counter reset each Revit restart.
- `ClashDefinitionsSettings.cs:147-150` — the seeded definition mints a fresh GUID and **never calls `Save()`**, so it re-mints every session.

v1 made those ids the join between a machine-wide library and data inside a **shared `.rvt`** — evaluating the join across different users' machines. Two people with the same seed id and different legend content would silently resolve to each other's views. That is audit §3.2's bug promoted from one-user-across-projects to whole-team-destructive.

Five further defects, all verified, are recorded in §7 so they are not re-introduced.

**Principle for v2:** *project-scoped data is keyed by something the document itself owns, never by a string minted in a machine-wide XML file.*

Consequence: the plugin stops writing settings into the `.rvt` altogether. Only **ownership stamps on elements the tools already create**, inside transactions that already exist. This removes — not mitigates — the dirty-document prompt, the undo-stack entry, the worksharing checkout, the missing flush hooks, the two-event race, and the cross-user filter deletion v1 would have caused.

---

## 2. Two mechanisms, no new subsystem

### 2.1 Element stamps (data the document owns)
An ES entity on an element the tool created. Ownership is discovered by scanning the document, so nothing external has to stay in sync. This is the pattern the audit already called correct (§2.2) and which five schemas prove in-repo.

### 2.2 `DocumentKey` (data with no element to stamp)
Element picks and output folders have no created element to hang off. They stay in `%AppData%` but become **per-document** — the DTO member changes shape from `List<long>` to a per-document map.

```csharp
// Source/Framework/DocumentKey.cs  — one small static helper, not a subsystem
static string? For(Document doc)
    doc.IsModelInCloud  -> doc.GetCloudModelPath()                 // stable across renames
    doc.IsWorkshared    -> doc.GetWorksharingCentralModelPath()    // central, not local
    otherwise           -> doc.PathName
    unsaved / no path   -> null  → caller uses machine-wide default and logs it
```
All three calls are already used in-repo at `UpgradeLinksRunHandler.cs:476-489, 564-565`, so they are known-good, not asserted.

This is **not** the sidecar rejected in the audit — nothing is written beside the `.rvt`, so the cloud/BIM360 objection does not apply. Entries are capped LRU (50 documents) so the files stay bounded.

**What each mechanism costs:** stamps travel with the file; `DocumentKey` data does not. That is deliberate — §7.2 shows sharing the AutoFilters manifest across users is a liability, not a feature.

---

## 3. Phases

Each is independently shippable and separately reviewable. v1 bundled all of this into one branch, violating CLAUDE.md's "one logical change per branch" against a codebase that **cannot be compiled here** (§8).

| # | Branch | Scope | Risk |
|---|---|---|---|
| 1 | `remove-dead-clash-id-lists` | Delete 10 dead fields + their only reader | None — pure deletion |
| 2 | `autofilters-owner-stamp` | Replace `CreatedFilterNames` with a per-filter stamp | Fixes the audit's worst bug; self-contained |
| 3 | `legend-document-owned-links` | Stable ids + legend-view stamp + type **names** | Medium |
| 4 | `output-folders-per-document` | `DocumentKey` proven on the smallest payload | Low |
| 5 | `clash-picks-per-document` | Element picks, `SourceLinkIds`, `SourcesExplicit`, `WorksetFilters` | Highest — largest surface |
| — | `storage-housekeeping` | `LayoutSnapshots` cap, legacy files | None; unrelated to tiering |

---

### Phase 1 — `remove-dead-clash-id-lists`

`ClashDimensionSettings`' ten id lists (`Group1/2ElemIds`, `Group1/2ElemLinkIds`, `Group1/2SourceLinkIds`, `GridIds`, `FloorIds`, `GridLinkIds`, `FloorLinkIds`) have **exactly two references** outside their own declaration — `ClashDefinitionsSettings.cs:191` and `:193`, both inside `SeedFromClashDimension()`. Nothing writes them. Verified by exhaustive grep.

Delete the ten fields and retire `SeedFromClashDimension()`, which is also the source of the re-minting id bug (§7.4). v1 booked this as re-scoping and allocated a project-tier DTO section for data that is always empty.

---

### Phase 2 — `autofilters-owner-stamp`

Fixes the audit's highest-severity finding (§3.1: cross-project filter deletion) **without any of v1's machinery**.

- New `AutoFilterOwnerSchema` (`LemoineAutoFilterOwner`): `Version`, `TradeId`, `RuleId`.
- Stamp at each creation site, inside the existing transaction: `AutoFiltersEventHandler.cs:714-715`, `:965-966`, `:1154`.
- Ownership discovery replaces the manifest: `FilteredElementCollector` + `ExtensibleStorageFilter`, matching the existing `MakeFilterName(tradeId, ruleName)` convention (`AutoFiltersSettings.cs:370`).
- **Delete** `CreatedFilterNames`; update its three readers (`AutoFiltersEventHandler.cs:392`, `:505-510`, `FiltersSettingsWindow.xaml.cs:444`).

**Why this beats moving the manifest into the document:** the orphan pass deletes filters that are in the manifest but not in the *running user's* expected set, computed from *their* machine-wide library (`AutoFiltersSettings.cs:394-408`). A manifest shared inside the `.rvt` would let User B systematically delete User A's filters. A per-element stamp naming its owning trade/rule can never do that — B's pass simply doesn't claim what B's library never created.

**Transaction-boundary correction:** v1 said to write at `AutoFiltersEventHandler.cs:507` "inside the run's existing transaction". `tx.Commit()` is at `:498` — `:507` is **outside** it and an ES write there would throw. The stamp goes at the creation sites instead, which are genuinely inside `tx`.

---

### Phase 3 — `legend-document-owned-links`

Three changes; the first is a prerequisite for the other two.

**3a. Make the ids real.** `LegendIdGen.New` → `Guid.NewGuid().ToString("N")`. One-time re-id on load for `legend_seed_1` and any `{prefix}_{5 digits}_{n}` value, logged. Without this, no keying scheme works.

**3b. Stamp the legend view.** New `LegendLinkSchema` (`LemoineLegendLink`): `Version`, `EntryId`. Written at the creation site inside the existing transaction (`LegendCreatorEventHandler.cs:~300`, right after `templateLegend.Duplicate(...)`). Resolution runs **document→settings**: scan stamped legend views for the entry id.

This also fixes a v1 error — v1 assigned the write-back to `LegendSettingsWindow.xaml.cs:1303`, which runs via `Dispatcher.BeginInvoke` on the **window's STA thread** after the handler's transaction has committed. There is no transaction or document there.

**Keep the existing name fallback** at `LegendCreatorEventHandler.cs:263-283`. v1 called it dead code; its own comment names two causes — *"The bound view was deleted, or this is a different project."* Stamping fixes the second only. Deleting a legend inside the same model is ordinary user behaviour, and removing the fallback would dead-end the run at `:294-298` where today it self-heals. Only its log line changes.

**3c. Store text **type names**, not ids.** `TitleTypeId` / `SubtitleTypeId` / `GroupHeaderTypeId` / `LabelTypeId` become names, resolved against the current document at launch. Sites v1 missed and this phase must cover:
- `LegendSettingsWindow.xaml.cs:1190-1193` — the **only** writer of all four.
- `:312-315` — reads all four to size the live preview.
- `:1273-1276` — pushes all four to the handler (v1 cited only `:1271`, the view id).
- `LegendCreatorSettings.cs:194-198` — `Clone()`, used by duplicate at `:800`.
- `OpenLegendSettingsCommand.cs:56-69` + `LegendTextTypeSizes` — the map is keyed by `long`; it becomes name-keyed.

Also fix the silent failure at `:1223-1236`: when a stored type is absent from the current document, `combo.SelectedIndex = 0` is set *before* `SelectionChanged` is wired at `:1231`, so the UI shows type #0 while the entry keeps the unresolvable value. Needs `DiagnosticsLog.Warn` plus a visible "remembered style not in this project" note, per CLAUDE.md's silent-failure standard.

---

### Phase 4 — `output-folders-per-document`

Proves `DocumentKey` on the smallest payload.

- `BulkExportSettings.OutputFolder` and `MakeCeilingGridsSettings.OutputFolder` become per-document maps.
- Sites: `GlobalSettingsWindow.ToolGroups.cs:197-198` and `:255-256` (**two** rows — v1 said three), plus two routes v1 missed entirely:
  - `PrintViewViewModel.cs:103` (read) and `:546` (write) — PrintView **shares** Bulk Export's folder. The key namespace must be defined explicitly: shared, or one key each.
  - `BulkExportViewModel.cs:1345` / `:1451` — the `IToolSettings` descriptor route, rendered generically and applied on change (`GlobalSettingsWindow.xaml.cs:295` documents this as why there is no Apply button).
- Rows are disabled with a hint when no document is open.

**`UpgradeLinksSettings.LastSelectedFolder` is dropped from this work.** It has one declaration (`:23`) and one read (`UpgradeLinksViewModel.cs:94`) and is **never written anywhere in the repo** — permanently `""`. v1 proposed migrating a value that never has content and deleting a comment describing behaviour that never existed. File as a separate dead-field bug.

---

### Phase 5 — `clash-picks-per-document`

Largest surface; last because it depends on `DocumentKey` proving out in Phase 4.

Per-document via `DocumentKey`, on `ClashGroupSpec`: `ElemIds`, `ElemLinkIds`, `SourceLinkIds`, **`SourcesExplicit`**, **`WorksetFilters`**.

- **`SourcesExplicit` must move with the ids.** v1 kept it machine-wide. `ClashGroupEditor.cs:107-108` derives it from whether the user checked every link in the *current* document, so leaving it machine-wide lets Project A's link count decide Project B's scan semantics.
- **`WorksetFilters` (`ClashGroupSpec.cs:51`) was missed by both the audit and v1.** It carries `LinkInstId` and per-document `ExcludedWorksetIds`, is live at `ClashEngine.cs:481`, and its own doc comment says it *"mirrors `SourceLinkIds`"*. A wrong workset exclusion is **invisible** — elements simply don't appear. Store workset **names** so a mismatch is detectable.
- **`ClashGroupEditor.cs` is the entire pick UI** and v1 never mentioned it: `:89`/`:109` (source links), `:347-348`/`:379-380` (element ids). Removing the properties without it breaks the build.
- **Two launch commands consume definitions**, not one: `ClashFinderCommand.cs:60-61` and `ClashElevationFinderCommand.cs:73-74`.
- **Fix the re-minting seed:** `ClashDefinitionsSettings.cs:147-150` must `Save()` the seeded definition before returning, or its id changes every session and orphans the picks keyed to it.

Because picks live in the same file as the definition, they are written by the existing eager `Save()` at `ClashDefinitionsWindow.xaml.cs:126-130`. No flush hook, no ExternalEvent, no transaction — which matters, because Clash Definitions is a **pick-only window with no run transaction at all**, so both of v1's write paths were absent for it.

---

### Phase 6 — `storage-housekeeping`

- `LayoutSnapshots\` — retain newest 50. This is the real unbounded-growth bug (audit §4).
- `BatchExportSettings.xml` — delete only after confirming `BulkExportSettings.xml` exists.
- `LemoineAutoFilters.xml` (V1) — **log, do not delete.** Recommendation changed from your "prune legacy files" call: it is the only remaining copy of a pre-V2 trade library (`AutoFiltersSettings.cs:16` — never migrated), deleting it is irreversible and saves a few KB, and `GlobalSettingsWindow.Filters.cs:3480` uses the same filename as the default for the trade **export** dialog, so a file by that name is not unambiguously legacy. Your call to overrule.

---

## 4. Not fixed by this plan

**Externally-managed trades are project-derived data written machine-wide.** `CeilingHeatmapEventHandler.cs:829+` (`RegisterCeilingHeatmapTrade`) and `MakeCeilingGridsRunHandler.cs:471+` (`RegisterHideTrade`) both `Rules.Clear()` and rebuild from *this model's* ceiling type names and height buckets, then `Save()` machine-wide. Run Ceiling Grids in A then B and A's rules are wiped while A's model still carries filters named from them.

They fail the audit's own Tier-3 test and were missed by the audit and v1 alike. Not folded in here because the fix belongs with those tools, not with this storage work. Flagged as a separate issue.

---

## 5. Open question

**Migration of existing project-scoped values.** Still unanswered from v1 §6. Recommendation unchanged: **drop, don't migrate** — today's stored ids belong to whichever project ran last, and a wrong `ElementId` resolves to a real but unrelated element, which is worse than an absent one. Values are read, logged, and discarded; users re-pick once per project.

---

## 6. Verification status

Every API member below was read from `libs/RevitAPI.dll` CLI metadata (TypeDef/MethodDef tables, signature blobs decoded) — not string-searched, per CLAUDE.md. Independently re-derived by a second reviewer.

| Member | Verified |
|---|---|
| `Autodesk.Revit.DB.ExtensibleStorage.DataStorage` | Exists; extends `Element`. **No longer used by this plan** |
| `Schema.Lookup`, `SchemaBuilder.AddSimpleField`, `Entity.Get/Set`, `Element.GetEntity/SetEntity`, `ExtensibleStorageFilter` | All exist; all proven by 5 in-repo call sites |
| `Document.IsModelInCloud` / `GetCloudModelPath` / `GetWorksharingCentralModelPath` | Exist; used at `UpgradeLinksRunHandler.cs:476-489, 564-565` |

**Latent bug found during review, independent of this work:** none of the five existing schemas calls `SchemaBuilder.SetVendorId`, and every `Finish()` sits inside `try/catch → DiagnosticsLog.Swallowed`. If Revit requires a vendor id, every stamp in the plugin may be failing silently today. Phase 2 must verify this on Windows before relying on stamps — it is a precondition for Phases 2 and 3.

---

## 7. Errors from v1, recorded so they don't return

1. **Unstable join keys** (§1) — the premise the whole design rested on.
2. **Manifest in the document = cross-user deletion** — moving `CreatedFilterNames` into the `.rvt` would let one user's library delete another's filters.
3. **Flush hook doesn't exist.** `IToolCleanup.OnWindowClosed()` has one caller (`StepFlowWindow.xaml.cs:156`), but `LegendSettingsWindow:21`, `FiltersSettingsWindow:17`, `ClashDefinitionsWindow:19` and `GlobalSettingsWindow:17` are all bare `Window`s. v1's close-flush existed for none of the windows it edited.
4. **Both write paths absent for Clash Definitions** — pick-only window, no run transaction.
5. **Transaction boundary** — `AutoFiltersEventHandler.cs:507` is after `tx.Commit()` at `:498`.
6. **Missed read/write sites** — `ClashGroupEditor.cs`, `PrintViewViewModel.cs`, `BulkExportViewModel.cs`, `LegendSettingsWindow.xaml.cs:1190-1193`/`:312-315`, `ClashElevationFinderCommand.cs`.
7. **Session-singleton windows survive document switches** (`OpenSettingsCommand.cs:15`, `OpenClashDefinitionsCommand.cs:24`), so a deferred write could land in the wrong model. v2 has no deferred document write at all.
8. **`ProjectSaveEvent` would have collided** with the existing, unrelated `App.ProjectEvent` (`App.cs:30-31`) — the Ceiling Grid projection handler.

---

## 8. Constraints

- **No Linux build.** Per CLAUDE.md this cannot be compiled or run here; every finding above is static analysis. Each phase needs a Windows build and a Revit plot before it is done, and I will report it as unverified on delivery.
- Schema GUIDs are generated once and hardcoded forever, matching the five existing schemas.
- Mandatory silent-failure scan per phase before commit.
- CLAUDE.md additions on completion: which windows can flush and which cannot (§7.3), and the `DocumentKey` convention.

---

## 9. Incidental, unrelated

`LegendBuilder.xaml.cs:673` and `:928` construct `Popup`s with `StaysOpen = false` — listed in CLAUDE.md as a confirmed Revit crasher. Separate ticket.

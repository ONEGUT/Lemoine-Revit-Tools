# Plan — Level Models v2 (13 changes)

Follow-up to `plan-navis-level-models.md`. v1 builds and runs; this is the
feature/UX batch that came out of using it.

## Confirmed decisions

| # | Decision |
|---|---|
| Level source | **The chosen model's tree groups** — the level nodes directly under the model root (`Model.RootItem.Children`), which is what Navisworks creates for an NWC exported "divided by level". No element scan at all. |
| Assignment | **Auto-match on scan, then edit by hand.** File name → level name. |
| Export version | **Stamp an explicit NWD version** (`NwdExportOptions.FileVersion = (int)DocumentFileVersion.Navisworks2026`), which v1 left at default. |
| Persistence | **Local per-document store.** There is NO API to attach custom data to an NWF — the whole assembly has no `XData`/`GetUserData`/`SetUserData` surface. |

## The 13 changes

1. **Scan step.** New first step "Scanning" with a throbber. Window opens
   immediately; the scan runs after, on the main thread, and auto-advances.
   Steps become: 1 Scanning · 2 Levels & models · 3 Output · 4 Run.
2. **Persist** level names, assigned models, bands and order per document.
3. **3a** Model picker for the level source, defaulting to the Arch file.
   **3b** Manual band entry when a model exposes no levels.
   **3c** Bands always in **feet**, converted via `UnitConversion.ScaleFactor`.
   **3d** Trim is **always on** — the per-level toggle is removed.
   **3e** Straddle default stays **keep-if-any-part-overlaps**.
4. **Rescan preserves edits** — only untouched levels refresh (`LevelDef.UserEdited`).
5. **No element-property scanning.** Levels from the tree; models matched to
   levels by file name.
6. **"Show selected only"** filter inside the dropdown.
7. **Reorder levels** with up/down buttons.
8. **Exported NWD carries only its own viewpoint**, positioned at the level's
   elevation and carrying its clip
   (`SavedViewpoints.CreateCopy` → `Clear` → add → export → restore).
9. Covered by the export-version row above.

## Files

| File | Change |
|---|---|
| `LevelModelsData.cs` | `LevelDef` gains `UserEdited`; `Trim` removed; bands in feet |
| `LevelModelsStore.cs` | **new** — per-document local persistence |
| `NavisLevelModels.cs` | tree-group level read, filename auto-match, feet conversion, viewpoint isolation, file version |
| `MultiSelectDropdown.cs` | "show selected only" filter |
| `LevelModelsViewModel.cs` | scan step, reorder, model picker, rewiring |
| `Strings/en/navis.levelModels.json` | new text |

## Risk

Viewpoint isolation clears the live document's saved viewpoints for the length
of the export. It is restored in a `finally`, but a hard crash mid-run would
leave the session without them (unsaved — the file on disk is untouched).

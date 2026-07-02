# Plan — Upgrade & Link Models Tool

Batch tool: pick separate Revit files (any older version, any folder), upgrade each to Revit 2024
by opening and re-saving it, then link it into the current document with a per-link placement
choice. Save destination is either over the originals or into a user-named subfolder next to the
host file, with optional cloud-model save as a second phase.

## Tool shape

Standard step-flow tool (`ILemoineTool` opened in `StepFlowWindow`), same architecture as
Copy From Link. New tool family folder `T09-UpgradeLinks`.

### Steps

1. **Pick files** — additive multi-select file list. An "Add files…" button opens a multiselect
   `OpenFileDialog` (`.rvt`); repeated picks from different folders accumulate into one list.
   Each row: file name, folder, detected saved-in version (read from the RVT's `BasicFileInfo`
   via `BasicFileInfo.Extract(path)` — no document open needed), remove button. Files already
   at 2024 are allowed (they just get re-saved/copied and linked). Duplicate paths are rejected;
   duplicate *file names* from different folders are flagged (they collide in the subfolder mode).
2. **Placement & options** — per-row `ImportPlacement` choice (Origin to Origin / Center to
   Center / Shared Coordinates / Site), defaulting to Origin to Origin, plus a set-all control.
   Global toggles: Audit on open (off by default), skip-if-already-linked vs reload existing link.
3. **Destination** — one of:
   - **Subfolder next to host** (default): editable subfolder name (default `Upgraded Links`),
     created under `Path.GetDirectoryName(hostDoc.PathName)`. Name-collision handling: suffix
     `(2)` etc., logged. If the host document is a cloud model / unsaved (no local `PathName`),
     this option is replaced by a browse-for-folder field.
   - **Overwrite originals**: save-in-place. Shown with an explicit destructive warning line
     (originals become 2024 files and can no longer open in older Revit).
   - **Cloud model (phase 2)**: visible only when the host is a cloud model — saves each file
     into the host's own ACC folder via harvested GUIDs. Hidden otherwise (per the UX rule:
     hide invalid options, don't disable them).
4. **Run** — review summary, Run button, output log, progress, cancel (always the last step;
   the Destination step is not conditional in phase 1, so no `ILemoineConditionalSteps` needed
   until the cloud option lands).

## Run handler — serial open → save → close → link loop

`UpgradeLinksRunHandler : IExternalEventHandler`, parked on `App` statics like the other
handlers. Everything runs on the Revit API thread; documents are processed strictly one at a
time for RAM control.

Per file:

1. `LemoineRun.CancelRequested` check — on cancel, log "Stopped by user — N of M processed;
   work so far preserved" and fall through to finish (already-saved files stay saved,
   already-created links stay linked).
2. Build `OpenOptions`:
   - workshared file (`BasicFileInfo.IsWorkshared`): `DetachAndPreserveWorksets` +
     `SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets))`
     — closed worksets are never loaded into memory (the dominant RAM saver) and are fully
     preserved on save.
   - `Audit = true` when the toggle is on.
3. `app.OpenDocumentFile(modelPath, openOptions)` — **`Application`, never
   `UIApplication.OpenAndActivateDocument`** (an activated view's graphics are held in native
   RAM for the whole session). The open itself performs the version upgrade in memory.
   Null/throw → skip-and-log, continue with the next file.
4. Save:
   - *Subfolder mode*: `doc.SaveAs(destPath, saveAsOptions)` with `OverwriteExistingFile = true`;
     workshared sources get `WorksharingSaveAsOptions { SaveAsCentral = true }`.
   - *Overwrite mode*: non-workshared → `doc.SaveAs(originalPath, overwrite)`; workshared →
     `SaveAs` back to the original path as a new central (logged: locals are orphaned).
5. `doc.Close(false)` in a `finally` — never two documents in memory at once.
6. Link into the host (per-file transaction so cancellation preserves committed links):
   `RevitLinkType.Create(hostDoc, ModelPathUtils.ConvertUserVisiblePathToModelPath(destPath), new RevitLinkOptions(false))`
   then `RevitLinkInstance.Create(hostDoc, typeId, row.Placement)`. A link type whose name
   already exists → skip-and-log or `LoadFrom` reload per the step-2 toggle. `Shared`
   placement can throw when the file has no shared coordinates relationship → catch,
   log, retry as `Origin`.
7. Progress: per-file log lines (`"Upgraded and linked 3/8: xyz.rvt (2021 → 2024)"`) — files
   are minutes each, so per-file cadence replaces the usual 5% batching. Progress bar driven
   by file index.

Failure routing: `LemoineFailureCapture.BeginRun()` + `LemoineRunLog.Set(pushLog)` so upgrade
warnings, missing-nested-link dialogs, and save failures land in the run's Output log instead
of blocking modal dialogs.

Memory discipline: handler clears its per-run payload (file list, options, callbacks) in a
`finally` at the end of `Execute`; ViewModel implements `ILemoineToolCleanup.OnWindowClosed`
to null parked callbacks. Zero-result guard: "No files selected" / "0 of N linked" always logged.

## Files

| File | Purpose |
|------|---------|
| `Source/Tools/T09-UpgradeLinks/UpgradeLinksViewModel.cs` | Step-flow tool: file list, per-row placement, destination, run wiring |
| `Source/Tools/T09-UpgradeLinks/UpgradeLinksModels.cs` | Row model (path, version, placement, status), destination enum, run spec |
| `Source/Tools/T09-UpgradeLinks/UpgradeLinksRunHandler.cs` | ExternalEvent handler: serial open/save/close/link loop |
| `Source/Tools/T09-UpgradeLinks/UpgradeLinksSettings.cs` | Persisted defaults (subfolder name, placement default, toggles) |
| `Source/Commands/T09-UpgradeLinks/UpgradeLinksCommand.cs` | Ribbon command: dedicated STA thread + `StepFlowWindow`, same pattern as `CopyFromLinkCommand` |
| `Source/App.cs` | Register handler/event statics + ribbon button |
| `Strings/en/upgradeLinks.json` | All user-facing text and run-log lines (`LemoineStrings.T`) |

UI work follows the `/revit-navisworks-ui` skill, mockup-first, before any code.

## Phase 2 — cloud model save (deferred)

- `Document.SaveAsCloudModel(accountGuid, projectGuid, folderId, modelName)` — requires the
  user signed in with ACC entitlement.
- No Revit API exists to browse ACC folders (that needs the APS/Forge REST API + OAuth — out
  of scope). Scope instead: when the host is a cloud model, harvest its
  `GetCloudModelPath()` / `GetCloudFolderId()` GUIDs and save the upgraded links into the
  **same ACC folder as the host**; link back via `ModelPathUtils.ConvertCloudGUIDsToCloudPath`.
- Destination step gains the third option (cloud-host only) and becomes the point where
  `ILemoineConditionalSteps` may be needed if a cloud-specific step is added.

## Known risks / accepted limits

- Revit never returns all native memory after `doc.Close()` — sequential processing is the
  API ceiling; a large batch still leaves the working set elevated until Revit restarts.
- Nested links inside non-workshared source files load on open (workshared sources avoid
  this via closed worksets). RAM note, not a blocker.
- Overwrite mode is destructive and version-irreversible; the UI says so explicitly.
- Upgrade duration is minutes per large file; the run is cancellable between files.

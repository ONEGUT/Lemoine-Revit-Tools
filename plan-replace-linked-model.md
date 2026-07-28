# Plan — Replace Link (swap a linked model for a new file, in place)

**Decisions taken:** base branch `main`; the tool queues several replacements per run
(§6 step 1), not one. Rebased onto the WPF-only `main` (PR #127) — **no web tool half**:
this is a `StepFlowWindow` ViewModel only.

## The ask

> Point at a file, tell Revit which link it replaces, and it upgrades the file to the
> current Revit version, writes it over the current link's location/name, and puts the
> new model in exactly where the old one stood (origin included).

**Verdict: yes, all of it is doable through the Revit 2024 API**, and most of the moving
parts already exist in this repo (Upgrade Links + Push Coordinates). One part —
"exactly where the old one stood" — needs a design decision rather than an API, because
"the same place" can mean two different things (see §4).

---

## 1. Why this is a new tool, not a flag on Upgrade Links

`UpgradeLinksRunHandler` already does *file → upgrade → save → link*. What it does **not**
do is start from an **existing link** and preserve its identity. Its reload path
(`ReloadExistingType`) matches an existing `RevitLinkType` by *name* only, and its whole
UX is "here are N loose files, bring them in".

Replace Link inverts the entry point: the user picks **the link in this model** first, and
the new file second. That changes the capture, the position handling, the destination
defaults, the backup requirement, and the review text — enough that bolting it onto
Upgrade Links would make both worse.

## 2. The mechanism (all confirmed API surface)

Given a target `RevitLinkType` **T** and a new source file **S**:

**a. Capture the "as it stands" state first** (read-only, Revit main thread, before
anything is touched):

| Captured | From |
|---|---|
| Current file path **P** | `T.GetExternalFileReference().GetAbsolutePath()` → `ModelPathUtils.ConvertModelPathToUserVisiblePath` |
| Attachment type, path type (relative/absolute) | `T.AttachmentType`, `T.PathType` |
| Load status | `T.GetLinkedFileStatus()` |
| Every instance of T: id, **total transform**, pinned, workset, name | `FilteredElementCollector` → `RevitLinkInstance.GetTotalTransform()` etc. |
| Per instance: the link doc's **Project Base Point** and **Survey Point** expressed in HOST internal coordinates | `instance.GetTotalTransform().OfPoint(BasePoint.GetProjectBasePoint(linkDoc).Position)` |
| Per instance: bounding-box centre in host coords | `instance.get_BoundingBox(null)` |

The last three rows are the position fingerprint — they are what makes "did the swap
actually land in the same place?" answerable instead of a guess.

**b. Release the old file.** `T.Unload(null)` — **outside any transaction**
(CLAUDE.md link-management rule; `UpgradeLinksRunHandler.UnloadIfCurrentlyLinked` is the
existing reference). Revit holds the linked file open, so P cannot be overwritten until
this happens.

**c. Back up the old model** (default on): copy P into a `_Superseded` sibling folder as
`<name>_yyyyMMdd-HHmm.rvt`. Overwriting the file a live link points at is irreversible;
this is the only safety net.

**d. Upgrade + write the new file.** `Application.OpenDocumentFile(S)` (background open —
never activated; opening *is* the upgrade), then `SaveAs(destPath, OverwriteExistingFile =
true)`, then close. Workshared sources open detached with all worksets closed and save with
`WorksharingSaveAsOptions { SaveAsCentral = true }` — `UpgradeLinksRunHandler.SaveLocal`
already does exactly this. `destPath` is **P** in the default "overwrite the linked file"
mode.

**e. Re-point the SAME link type — never delete and recreate.**
- `destPath == P` → `T.Reload()`
- `destPath != P` → `T.LoadFrom(destMp, worksetConfig)`

Both **outside any transaction**. Reusing the type preserves its `ElementId`, and with it:
every instance and its transform, per-view visibility and graphic overrides, view filters
that reference the link, copy/monitor relationships, phase mapping, workset assignment,
room-bounding, and the Manage Links row. This is the whole reason the tool can claim "in
exactly as the current one stands" — the same lifecycle rule already written down for
`ParameterFilterElement` (update in place, never churn the id).

**f. Verify and report.** Recompute the base-point/bbox-centre fingerprint from (a) and log
the delta per instance: `moved 0' - 0"` (good) or `moved 143' - 6" — check` (the new file
does not share the old file's origin). Silence here would be indistinguishable from a
broken swap.

## 3. Renaming

- **Overwrite in place** (default): `destPath = P`, so name, path, path-type and the link
  type's displayed name are all unchanged by construction.
- **Save beside it under a new name**: `destPath = <folder of P>\<new name>.rvt`, then
  `LoadFrom`. The type keeps its id but its *displayed name* follows the file name.
  Whether `RevitLinkType.Name` can be forced back to the old label is **unverified** —
  flagged in the plan, tested on Windows, and reported honestly in the log either way.

## 4. Position — the one genuine decision

`Reload`/`LoadFrom` keep the **instance transform**. Whether that equals "the same place"
depends on the new file:

- **Same model, newer issue** (the common case): internal origin is unchanged, the
  preserved transform lands the geometry pixel-identically. Nothing to do.
- **Different file, different internal origin** (a re-export, a different consultant, a
  remodelled file): the transform is preserved but the *building* moves.

So the Position step offers:

1. **Keep the existing placement** *(default)* — trust the transform, report the measured
   movement.
2. **Re-seat on Project Base Point** — after reload, translate each instance by
   `(capturedOldPbpInHost − newPbpInHost)`. Pure translation; per instance, so a type with
   several instances keeps each one's own offset. (`TranslateBasePointToBasePoint` in
   `UpgradeLinksRunHandler` is the maths, retargeted from "the host's point" to "where the
   old link's point was".)
3. **Re-seat on Survey Point** — same, with `BasePoint.GetSurveyPoint`.

Rotation rides along in the preserved transform, so a re-seat only ever needs the
translation. Matching a *different* grid direction (true rotation correction) is Align
Coordinates' job and stays there.

**Deliberately NOT offered in v1:** re-seating via `AcquireCoordinates` /
`ImportPlacement.Shared`. Per CLAUDE.md, making a shared-coordinate correction actually
take on an Origin-to-Origin link requires deleting and recreating the `RevitLinkInstance`
— which throws away the per-view overrides this tool exists to protect. Push Coordinates
already covers that workflow.

## 5. Limits, stated up front

| Case | Behaviour |
|---|---|
| Cloud-hosted link (`IsModelInCloud`) | Detected and reported as unsupported for in-place overwrite; user is pointed at Upgrade Links' Cloud flow. No silent skip. |
| Old link unloaded / file missing | Base points can't be captured → transform-only, warned in the log. |
| Old file is a workshared **central** others have locals of | Loud warning before the run: overwriting P orphans every local copy. Coordination call, not a code fix. |
| New file saved in a **later** Revit version | Rejected at scan (already handled by `UpgradeFileScan.IsFutureVersion`). |
| P open in another Revit session | `SaveAs` fails → reported per row, run continues. |
| Nested links inside the new file | Come in with the new file's own configuration; noted in the log, not managed. |

## 6. UI

A step-flow tool **Replace Link** in the **Setup** ribbon panel (after Upgrade Links),
built as the usual WPF ViewModel + Web tool pair, following `UpgradeLinks*`:

1. **Links** — table of the host's current links (name, path, status, instances, saved-in
   version), one row per replacement: *existing link* → *new file* (Browse) → *remove*.
   Queueing several replacements in one run costs nothing extra — the loop is identical.
2. **Destination** — *Overwrite the linked file* (default) / *Save beside it as…* /
   *Save to a folder…*, plus the backup toggle and the audit-on-open toggle.
3. **Position** — the three options from §4, plus "report measured movement" (on).
4. **Run** — review chips + log, cancellable at the per-row boundary via `RunState`.

## 7. Files

**New**
- `Source/Tools/Setup/ReplaceLinkModels.cs` — rows, spec, `ReplacePosition` / `ReplaceDestination` enums
- `Source/Tools/Setup/ReplaceLinkCapture.cs` — read-only capture of §2a (main thread)
- `Source/Tools/Setup/ReplaceLinkRunHandler.cs` — the run
- `Source/Tools/Setup/ReplaceLinkViewModel.cs` — WPF step flow
- `Source/Tools/Setup/ReplaceLinkWebTool.cs` — web port
- `Source/Tools/Setup/ReplaceLinkSettings.cs` — persisted defaults
- `Source/Commands/Setup/ReplaceLinkCommand.cs`
- `Strings/en/replaceLink.json`

**Changed**
- `Source/App.cs` — handler statics, `ExternalEvent`s, Setup-panel ribbon button
- `Source/Tools/Setup/UpgradeLinksRunHandler.cs` → extract `SaveLocal`, `SanitizeBaseName`,
  `UnloadIfCurrentlyLinked` and the base-point translate into a shared
  `Source/Tools/Setup/LinkFileOps.cs` so both tools run one implementation (no behaviour
  change to Upgrade Links)
- `Source/Tools/Setup/UpgradeLinksScanHandler.cs` — reused as-is for the new-file version scan
- `Source/Framework/GlobalSettingsWindow.ToolGroups.cs` + `Source/Framework/Web/WebSettings.cs`
  — default position mode / backup toggle in Global Settings

## 8. Verification

Cannot be compiled or run on Linux (`UseWPF` + net48 needs the Windows-only SDK). The
plan's Windows checklist:

1. Same-file re-issue → link reloads, log reports `moved 0'-0"`, view overrides intact.
2. Different-origin file with **Keep placement** → movement reported, not hidden.
3. Same file with **Re-seat on Project Base Point** → lands back on the captured point.
4. Type with two instances → both keep their own offsets.
5. Workshared central source → saves as central, links, stays workshared.
6. `RevitLinkType.Name` after a rename-save — confirm whether the old label can be kept.

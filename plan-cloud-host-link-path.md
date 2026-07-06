# Plan — Fix cloud host file-path handling in Upgrade & Link Models

> **Superseded (see below).** The design here (harvest hub/project/folder ids, build a
> Guid and call `SaveAsCloudModel(Guid, Guid, string, string)`) hit two hard blockers on
> a real build: `Document.GetHubId()` returns a string with no Guid-typed hub/account
> accessor anywhere in the API (CS0029), and the `CloudFolder`-object overload of
> `SaveAsCloudModel` takes an `Autodesk.Revit.DB.ForgeDM.CloudFolder`, which turned out to
> be an internal (`NotPublic`) type — unusable from a third-party plugin (CS0122).
>
> The Cloud destination now instead posts Revit's own native **Save As → Cloud Model**
> command (`UIApplication.PostCommand` + `RevitCommandId.LookupPostableCommandId
> (PostableCommand.SaveAsCloudModel)`) per file, and pauses the run (via a new
> `ILemoineRunPausable` Continue/Skip footer extension) until the user finishes in
> Revit's own dialog. No hub/project/folder ids are harvested or guessed at all —
> `Document.IsModelInCloud` is the only check left. Sections 1–2 below (stop trusting
> the Collaboration Cache path; prompt instead of guessing for the local "selected
> folder" destination) are unaffected and still describe the shipped behavior.

## Problem

`T09-UpgradeLinks` ("Upgrade & Link Models") resolves where upgraded linked files get
saved from the **host** document's own path (`UpgradeLinksCommand.cs:43-44`):

```csharp
if (doc != null && !string.IsNullOrEmpty(doc.PathName))
    hostFolder = Path.GetDirectoryName(doc.PathName);
```

When the host is a **cloud model** (opened from Autodesk Construction Cloud / BIM 360),
`Document.PathName` is not empty — but it reports Revit's own local **Collaboration
Cache** path (e.g. under `%LOCALAPPDATA%\Autodesk\Revit\...\CollaborationCache\...`).
That's Revit's private sync cache, not a real, user-owned folder. Today the tool
silently treats it as if it were a normal folder and would create the "subfolder next
to host" *inside Revit's cache* — exactly the "not where any file should be saved to"
problem reported.

Separately, the **Cloud** destination card (save the upgraded links back into the
host's own ACC folder) has been dead code since it was built: `UpgradeLinksCommand.cs:51`
hardcodes `hostCanCloud = false` with a comment that the Revit API can't resolve the
host's cloud ids, so `UpgradeLinksSpec.CloudAccountId/CloudProjectId/CloudFolderId`
are never populated and `UpgradeLinksRunHandler`'s existing cloud-save branch
(`UpgradeLinksRunHandler.cs:124-131`, already fully implemented) never fires.

## What I verified before writing this

I don't have a Windows/Revit environment to test against, so I checked the actual
`libs/RevitAPI.dll` (Revit 2024, the only year with real DLLs checked in) directly for
the symbols this plan depends on, rather than trusting memory. All of the following
**are present** in that assembly:

- `Document.IsModelInCloud` (property)
- `Document.GetCloudModelPath()`
- `Document.GetHubId()`
- `Document.GetCloudFolderId()`
- `ModelPath.GetProjectGUID()` / `ModelPath.GetModelGUID()`
- `Document.SaveAsCloudModel(...)` (already called by the run handler)
- A full cloud-browsing surface: `Autodesk.Revit.DB.ForgeDM.CloudHub / CloudProject /
  CloudFolder / CloudModel` (`GetAllHubs`, `GetProjects`, `GetFolders`, `GetModels`, etc.)

This means the original "no API to read the host's containing folder id" assumption
in `plan-upgrade-link-models.md` (Phase 2) was wrong — `Document.GetHubId()` +
`Document.GetCloudFolderId()` + `ModelPath.GetProjectGUID()` give exactly the three
ids `SaveAsCloudModel` needs, straight off the host document, no folder-browsing UI
required for the "same folder as host" case the tool already advertises
(`optCloudTitle`: *"Cloud model — same folder as this model"*).

**Caveat:** I verified this only against the Revit 2024 DLL. `libs2025/2026/2027`
are still placeholder READMEs (no real DLLs), so this needs re-confirming once those
are populated, per the multi-year build setup in `CLAUDE.md`.

There is, by contrast, **no Revit API at all** for "where does Desktop Connector mirror
this project locally on disk" — Desktop Connector is a separate sync client, not part
of the Revit SDK. Per your answer, this plan does **not** attempt to guess Desktop
Connector's local mount path. When the host is a cloud model, the tool always prompts
for the folder instead of guessing.

## Design

### 1. Stop treating the cloud cache path as a real folder

`UpgradeLinksCommand.BuildTool()`: check `doc.IsModelInCloud` before ever touching
`doc.PathName`. If the host is a cloud model, `hostFolder` stays `null` regardless of
what `PathName` reports — the Collaboration Cache path is never surfaced to the tool.

### 2. Prompt instead of guessing

`UpgradeLinksViewModel`'s Destination step currently shows a dead-end warning
(`upgradeLinks.labels.noHostFolder`) whenever `_hostFolder` is empty (covers both "host
is a cloud model" and "host has never been saved locally"). Replace that dead end with
an inline `LemoineFolderBrowser` (the existing reusable folder-picker control) so the
user can pick where the upgraded copies should be saved, with copy that explains *why*
they're being asked (cloud model vs. never-saved, worded differently). The picked path
feeds into the same `HostFolder`/subfolder-creation code path the run handler already
has — no changes needed in `UpgradeLinksRunHandler`.

**Addition beyond the literal ask, flagging for your approval/veto:** persist the
picked folder in `UpgradeLinksSettings`, keyed by the cloud model's GUID
(`ModelPath.GetModelGUID()`), so reopening the tool on the same cloud project doesn't
ask again. Small, follows the existing settings-persistence pattern in the same file.
Say so in your approval reply if you'd rather it re-prompt every time instead.

### 3. Make the Cloud destination real

`UpgradeLinksCommand.BuildTool()`: when `doc.IsModelInCloud`, harvest
`hubId = doc.GetHubId()`, `projectId = doc.GetCloudModelPath().GetProjectGUID()`,
`folderId = doc.GetCloudFolderId()` inside a try/catch (cloud calls can throw for a
not-fully-synced or signed-out model) → `LemoineLog.Swallowed` on failure, and
`hostCanCloud` stays `false` (fail closed — the Cloud card is already hidden when
`hostCanCloud` is false, per the existing "hide invalid options" pattern). On success,
pass the three ids down into `UpgradeLinksViewModel`, which now populates
`UpgradeLinksSpec.CloudAccountId/CloudProjectId/CloudFolderId` in `Run()` — those
fields exist today but nothing ever sets them. No changes needed to
`UpgradeLinksRunHandler`'s save/link logic, only to what feeds it.

## Files touched

| File | Change |
|---|---|
| `Source/Commands/T09-UpgradeLinks/UpgradeLinksCommand.cs` | Guard `hostFolder` behind `!doc.IsModelInCloud`; harvest hub/project/folder ids when cloud; drop the hardcoded `hostCanCloud = false` |
| `Source/Tools/T09-UpgradeLinks/UpgradeLinksViewModel.cs` | Constructor takes cloud ids + why-no-folder reason; Destination step's subfolder-extra becomes a `LemoineFolderBrowser` prompt instead of a dead-end warning; `Run()` populates the cloud ids on the spec |
| `Source/Tools/T09-UpgradeLinks/UpgradeLinksSettings.cs` | Add a persisted `Dictionary<string,string>` (cloud model GUID → chosen folder), if approved |
| `Strings/en/upgradeLinks.json` | New/updated copy for the folder-browse prompt (cloud vs. unsaved wording) |
| `plan-upgrade-link-models.md` | Update the Phase 2 section — this is no longer deferred/blocked |

## Silent-failure points I'll flag in the post-change scan

- New try/catch around `GetHubId()` / `GetCloudFolderId()` / `GetProjectGUID()` must
  route through `LemoineLog.Swallowed`, fail closed to `hostCanCloud = false`.
- New settings dictionary save/load follows the existing try/catch +
  `LemoineLog.Swallowed` pattern already in `UpgradeLinksSettings`.

## Out of scope / not attempted

- No filesystem search for Desktop Connector's local mirror (per your answer).
- No changes to any other tool that reads a link/host path (T06-CopyLinear,
  T06-CopyFromLink, T08-Coordinates) — this is scoped to T09-UpgradeLinks only, which
  is the only place "Update and link" / cloud-save applies today.

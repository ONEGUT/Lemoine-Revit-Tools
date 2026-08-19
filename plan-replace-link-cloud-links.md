# Plan — Replace Link: cloud links (option C, cloud → cloud replacement)

## The symptom

On one project, every link in the model is listed greyed out with **two** messages:

- `<name> — No external file reference` (blocked reason)
- `<name> — some details couldn't be read (file reference); what's shown for them is a
  placeholder, not the real value.` (read warning)

…and then `None of this model's links can be replaced in place`.

## Root cause — confirmed

`ReplaceLinkCapture.cs:67` calls `type.GetExternalFileReference()` **without the guard the
Revit API requires**:

```csharp
ExternalFileReference? extRef = null;
try { extRef = type.GetExternalFileReference(); }
catch (Exception ex) { … Note(info, "file reference"); }

if (extRef == null) { info.BlockedReason = "No external file reference"; continue; }
```

`Element.GetExternalFileReference()` **throws** when `Element.IsExternalFileReference()` is
`false`. A **cloud-hosted (ACC / BIM 360) link is not an external *file* reference at all** —
it is an external ***resource*** reference. So for every cloud link:

1. `GetExternalFileReference()` throws → catch fires → `Note(info, "file reference")` adds the
   read warning.
2. `extRef` stays `null` → the row is blocked with the **wrong** reason, `noReference`.
3. The method `continue`s at line 80 — so the tool's *actual* cloud detection (lines 96–115)
   **is never reached** and the `blocked.cloud` string is unreachable dead code.

The screenshot is the fingerprint of exactly this: **both** messages on every row. A
genuinely-null return would give the blocked reason with *no* read warning.

## API surface — verified against `libs/RevitAPI.dll` metadata (not a string search)

Read out of the `TypeDef` → `MethodList` tables with signature blobs decoded, scoped to their
declaring type:

| Member | Signature |
|---|---|
| `Element.IsExternalFileReference` | `bool IsExternalFileReference()` |
| `Element.RefersToExternalResourceReference` | `bool RefersToExternalResourceReference(ExternalResourceType)` |
| `Element.GetExternalResourceReference` | `ExternalResourceReference GetExternalResourceReference(ExternalResourceType)` |
| `ExternalResourceTypes.BuiltInExternalResourceTypes` | `RevitLink`, `IFCLink`, `CADLink`, `PointCloud`, … |
| `ExternalResourceReference.CreateFromCloudPath` | `static ExternalResourceReference CreateFromCloudPath(ModelPath)` |
| `ExternalResourceReference.GetResourceShortDisplayName` | `string GetResourceShortDisplayName()` |
| `ExternalResourceReference.InSessionPath` | `string` (get/set) |
| **`RevitLinkType.LoadFrom`** | **`LinkLoadResult LoadFrom(ExternalResourceReference, WorksetConfiguration)`** ← the cloud re-point |
| `ModelPath.CloudPath` | `bool` — direct cloud test |
| `ModelPathUtils.ConvertCloudGUIDsToCloudPath` | `static ModelPath ConvertCloudGUIDsToCloudPath(string region, Guid project, Guid model)` |
| `Document.GetCloudModelPath` | `ModelPath GetCloudModelPath()` |

### The ACC browsing API exists — `Autodesk.Revit.DB.ForgeDM`

This is what makes option C buildable rather than a GUID-typing exercise:

| Type | Members |
|---|---|
| `CloudHub` | `static IList<CloudHub> GetAllHubs()`, `Id`, `Name`, `Region`, `GetProjects()` |
| `CloudProject` | `Id`, `Name`, `GUID`, `GetHub()`, `GetFolders()` |
| `CloudFolder` | `Id`, `Name`, `GetProject()`, `GetFolders()`, `GetModels()` |
| `CloudModel` | `Id`, `Name`, `GUID`, `IsWorkshared`, `GetFolder()`, **`GetModelPath()`** |

So the replacement chain is: browse `CloudModel` → `GetModelPath()` →
`ExternalResourceReference.CreateFromCloudPath(path)` → `type.LoadFrom(err, wsConfig)`.

## What cloud → cloud replacement actually does

A cloud replacement is **structurally simpler and safer** than the file path, because nothing
is written over:

| Step (file row, today) | Cloud row |
|---|---|
| Back up the linked file | **not needed** — nothing is overwritten |
| `Unload` the link so the file can be opened | **not needed** |
| Unload any other link holding that file | **not needed** |
| Open the replacement, upgrade, `SaveAs`, close | **not needed** — the ACC model is already whatever version it is |
| `LoadFrom(ModelPath, wsConfig)` | `LoadFrom(ExternalResourceReference, wsConfig)` |
| Re-seat onto the old link's Survey Point / Project Base Point | **identical** — reads the loaded link doc, works the same |

Consequences that must show in the UI, not be discovered at runtime:

- **No version pre-scan.** There is no local file to read a version header from, so the
  version badge cannot be filled for a cloud row. Revit reports the outcome through
  `LinkLoadResult.LoadResult`, which the handler already checks (`log.reloadResult`).
- **No destination and no backup** for cloud rows. The Destination step is meaningless
  unless a file row is also queued.
- Still **outside a transaction** — `LoadFrom` is a link-management call (CLAUDE.md), then the
  base-point re-seat runs in its own transaction, exactly as now.

## The same guard bug at five other call sites

Every `GetExternalFileReference()` in the repo is unguarded and throws on a cloud link:

| File | Line | Effect on a cloud link today |
|---|---|---|
| `ReplaceLinkCapture.cs` | 67 | The reported symptom |
| `ReplaceLinkRunHandler.cs` | 178 | Fails as `noReference` instead of routing to the cloud path |
| `ReplaceLinkRunHandler.cs` | 648 | "is this file loaded as another link" probe throws per cloud link |
| `LinkAuditCapture.cs` | 72, 140 | Link Audit rows silently lose their path |
| `PushCoordinatesToLinksRunHandler.cs` | 195 | Cloud links can't be resolved |
| `UpgradeLinksRunHandler.cs` | 723 | Same probe as `ReplaceLinkRunHandler.cs:648` |

All six route through one new guarded resolver regardless of option C.

---

## Two UX decisions I need from you

### Decision 1 — where the cloud model picker lives

| Option | What it looks like | Trade-off |
|---|---|---|
| **1a. Separate browser window off the row's Browse button** *(recommended)* | Cloud link row shows `Browse cloud…`; clicking opens a modal picker window (hub → project → folder tree → model) | Mirrors the existing file-dialog flow exactly, row card stays compact, and the tree gets the space it needs. Different data type from the Links step, so it isn't a picker-inside-a-picker for the same data |
| **1b. Inline expansion inside the row card** | The tree drops open under the row | No second window, but a deep ACC folder tree inside a queued-row card makes the Links step very tall with several rows queued |
| **1c. A dedicated step before Links** | Pick hub + project once, then rows choose from that project's models | Fewest clicks when every replacement is in one project, but forces one project for the whole run |

**Recommendation: 1a.** The Browse button already exists on every row; it just becomes
context-aware — file link → file dialog, cloud link → cloud browser. Per CLAUDE.md, the
invalid choice is **hidden**, not shown disabled.

### Decision 2 — how much of ACC to enumerate

`GetAllHubs()` → `GetProjects()` → recursive `GetFolders()` → `GetModels()` are all network
calls. Enumerating an entire hub eagerly is not viable for a real firm's account.

| Option | Behaviour | Trade-off |
|---|---|---|
| **2a. Cascade, defaulted to the host's own project** *(recommended)* | Hub dropdown + Project dropdown (both pre-set from `doc.GetCloudModelPath()`), then that project's folder tree | One click in the common case (replacement lives in the same ACC project), but still reachable if it doesn't |
| **2b. Host project only, locked** | No hub/project choice at all | Simplest, but a replacement in a different project is impossible |
| **2c. Eager full tree of every hub** | One `SetTree` over everything | Hundreds of network calls; unusable on a large account |

**Recommendation: 2a.**

If you'd rather not decide item by item, say **"go with the recommendations"** and I'll take
1a + 2a and produce the mockup.

---

## Implementation plan (assuming 1a + 2a)

### 1. `Source/Tools/Setup/LinkReference.cs` — **new**

One guarded resolver answering "what does this link point at?", for both reference kinds, so
no call site has to remember the guard:

```csharp
public enum LinkReferenceKind { None, File, Cloud }

public sealed class LinkReferenceInfo
{
    public LinkReferenceKind Kind;         // None / File / Cloud
    public string            Path;         // user-visible file path — File only
    public string            DisplayName;  // cloud: GetResourceShortDisplayName()
    public LinkedFileStatus? Status;
    public bool              ReadFailed;   // a REAL read failure, not "it's cloud"
}

public static LinkReferenceInfo Resolve(RevitLinkType type);   // never throws
```

Order: `IsExternalFileReference()` → file; else `RefersToExternalResourceReference(RevitLink)`
and `IFCLink` → cloud; else `None`. (Two of the ten links in the screenshot are `.ifc`, so the
IFC resource type is not optional.)

### 2. `Source/Tools/Setup/CloudBrowseHandler.cs` — **new** `IExternalEventHandler`

The ForgeDM calls are Revit API calls; the tool window runs on its own STA thread, so every
fetch is `window → ExternalEvent → API thread → marshal back via BeginInvoke`. One handler
with a small request enum (`Hubs` / `Projects` / `Tree`), a result callback and an error
callback, cleared in `IToolCleanup.OnWindowClosed` per the memory discipline in CLAUDE.md.

Failure modes that must be *reported*, never silently empty (CLAUDE.md's zero-result rule):

- not signed in to Autodesk → "Sign in to Autodesk Docs in Revit, then reopen this tool"
- zero hubs / zero projects / zero models in a folder → "Found 0 …" stated explicitly
- network failure → the exception message in the picker, not a blank tree

### 3. `Source/Tools/Setup/CloudModelPickerWindow.xaml(.cs)` — **new**

Hub `SingleSelect` + Project `SingleSelect` + `BrowserTreePicker` (`SingleSelect = true`) for
the folder tree, with a loading state per fetch.

**`BrowserTreePicker` is reused, not re-rolled.** Its `BrowserNode` carries `long? Id` — folder
nodes get `Id = null`, model leaves get a synthetic index into a side
`Dictionary<long, CloudModel>`. Subscribe to `SelectionChanged` **before** `SetTree`, per the
control's contract.

### 4. `ReplaceLinkModels.cs`

`ReplaceRow` / `ReplaceItem` gain the cloud target: `IsCloudTarget`, `CloudRegion`,
`CloudProjectGuid`, `CloudModelGuid`, `CloudModelName`. `HostLinkInfo` gains
`LinkReferenceKind Kind`, and `Replaceable` becomes true for cloud links.

Persisted GUIDs are **per-run only**, never written to `%AppData%` settings — they name
something inside one specific ACC project, which is exactly the tier violation CLAUDE.md
calls out.

### 5. `ReplaceLinkCapture.cs`

Route through `LinkReference.Resolve`. Cloud links become **replaceable**, listed with their
resource display name. `ReadWarning` stays reserved for genuine read failures — being cloud
is a known state, not a failed read.

### 6. `ReplaceLinkViewModel.cs`

- Row card: Browse button is context-aware (file link → file dialog, cloud link → cloud
  browser window).
- Version badge is suppressed for cloud rows, replaced with a "version checked on load" note.
- Implement `IConditionalSteps`: hide the **Destination** step when every queued row is a
  cloud row. `dest` is the middle step, so the "a conditional step must never be last" rule
  holds.
- The version scan is raised only for rows with a local file.

### 7. `ReplaceLinkRunHandler.cs`

`ProcessOne` branches at the top on `LinkReference.Resolve(type).Kind`:

- **File** → today's path, unchanged.
- **Cloud** → `ConvertCloudGUIDsToCloudPath(region, project, model)` →
  `ExternalResourceReference.CreateFromCloudPath(path)` → `LoadFrom(err, wsConfig)` **outside
  any transaction** → check `LinkLoadResult.LoadResult` → re-seat base points in its own
  transaction → report. No backup, no unload, no open/save/close.

Also guard lines 178 and 648.

### 8. Other call sites

`LinkAuditCapture.cs:72,140`, `PushCoordinatesToLinksRunHandler.cs:195`,
`UpgradeLinksRunHandler.cs:723` → `LinkReference.Resolve`.

### 9. `Strings/en/replaceLink.json` + a new `cloudPicker.json`

All new user-facing text externalized per CLAUDE.md — picker chrome, the cloud run-log lines,
the sign-in / empty-result messages. `blocked.cloud` is no longer a blocked reason and is
removed; `log.cloudUnsupported` is replaced by the real cloud path.

## Files touched

| File | Change |
|---|---|
| `Source/Tools/Setup/LinkReference.cs` | **new** — guarded resolver for both reference kinds |
| `Source/Tools/Setup/CloudBrowseHandler.cs` | **new** — ExternalEvent handler for ForgeDM fetches |
| `Source/Tools/Setup/CloudModelPickerWindow.xaml(.cs)` | **new** — hub/project/folder-tree picker |
| `Source/Tools/Setup/ReplaceLinkModels.cs` | cloud target fields on row/item, `Kind` on `HostLinkInfo` |
| `Source/Tools/Setup/ReplaceLinkCapture.cs` | resolver; cloud links become replaceable |
| `Source/Tools/Setup/ReplaceLinkViewModel.cs` | context-aware Browse, `IConditionalSteps`, scan skip |
| `Source/Tools/Setup/ReplaceLinkRunHandler.cs` | cloud branch in `ProcessOne`; guard 178 + 648 |
| `Source/Tools/Setup/LinkAuditCapture.cs` | guard 72, 140 |
| `Source/Tools/Setup/PushCoordinatesToLinksRunHandler.cs` | guard 195 |
| `Source/Tools/Setup/UpgradeLinksRunHandler.cs` | guard 723 |
| `Source/Commands/Setup/ReplaceLinkCommand.cs` | wire the cloud browse ExternalEvent |
| `Source/App.cs` | register the cloud browse handler + event |
| `Strings/en/replaceLink.json`, `Strings/en/cloudPicker.json` | new/changed strings |

## Sequence — DONE

1. ~~Pick the UX options~~ — **1a + 2a** chosen.
2. ~~Mockup for approval~~ — rendered from the real `DarkMono` palette, approved.
3. ~~Implement, silent-failure scan, commit, push~~ — done; see the commit on this branch.

### Fixed during implementation (found by review, not by the compiler)

- `PickDefaultHub` fetched every hub's projects and then `ReadProjects` fetched the chosen
  hub's projects *again* — two network round-trips for the same data. Replaced by a single
  walk, region-matching hubs first, stopping at the host project.
- That walk then adopted the **first** hub as the default even when it had **zero** projects,
  so an account whose first hub is empty would open the picker blank. It now skips empty hubs.
- The picker's failure was being rendered with the version-scan's wording ("Couldn't read the
  file versions…"), which describes something else entirely. It has its own message now.
- `TitleBar.IconGlyph` renders in **Segoe MDL2 Assets**, so the geometric `▣` first used would
  not have resolved — replaced with `char.ConvertFromUtf32(0xE753)` (Cloud), ASCII in source
  per CLAUDE.md.

## Cannot be verified here — needs a Windows/Revit run

This project does not build on Linux, and the ForgeDM calls need a signed-in Autodesk session:

- Whether `CloudHub.GetAllHubs()` and friends require a full API context (ExternalEvent) or
  work on any thread. The plan assumes **ExternalEvent** because that is the safe reading; if
  they turn out to be thread-agnostic the handler becomes a simple background fetch.
- Whether `LoadFrom(ExternalResourceReference, …)` on a link whose current reference is cloud
  preserves the type id in practice (it should — it is the same "re-point, never recreate"
  call the file path already relies on).
- Behaviour when the replacement cloud model is a newer Revit version than the host.

## How to confirm the original diagnosis on your machine

`%AppData%\LemoineTools\diagnostics.log`, look for
`[WARNING]  ReplaceLink: read external file reference` once per link. If the detail names
`InvalidOperationException`, the cloud diagnosis is confirmed outright.

## Branch

`claude/replace-link-cloud-issue-o2s6ui`, cut from `main` at `848f5db`.

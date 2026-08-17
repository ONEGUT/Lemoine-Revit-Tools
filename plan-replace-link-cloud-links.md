# Plan — Replace Link: cloud links reported as "No external file reference"

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

`Element.GetExternalFileReference()` **throws** `Autodesk.Revit.Exceptions.InvalidOperationException`
when `Element.IsExternalFileReference()` is `false`. A **cloud-hosted (ACC / BIM 360) link is not
an external *file* reference at all** — it is an external ***resource*** reference, resolved through
the Autodesk Docs resource server. So for every cloud link:

1. `GetExternalFileReference()` throws → the catch fires → `Note(info, "file reference")`
   adds the read warning.
2. `extRef` stays `null` → the row is blocked with the **wrong** reason, `noReference`.
3. The method `continue`s at line 80 — so the tool's *actual* cloud detection
   (lines 96–115, `IsCloud` = empty user-visible path) **is never reached**. The
   `blocked.cloud` string ("Cloud-hosted — no local file to write over") is effectively
   dead code and can never be shown.

The screenshot is the signature of exactly this: **both** the blocked reason and the read
warning appear on every row. A genuinely-null return would produce the blocked reason with
*no* read warning — the pair only happens on the throw path.

### API members verified against `libs/RevitAPI.dll` metadata (not a string search)

Read out of the `TypeDef` → `MethodList` tables with signature blobs decoded, scoped to
`Autodesk.Revit.DB.Element`:

| Member | Signature |
|---|---|
| `Element.IsExternalFileReference` | `bool IsExternalFileReference()` |
| `Element.GetExternalFileReference` | `ExternalFileReference GetExternalFileReference()` |
| `Element.RefersToExternalResourceReference` | `bool RefersToExternalResourceReference(ExternalResourceType)` |
| `Element.GetExternalResourceReference` | `ExternalResourceReference GetExternalResourceReference(ExternalResourceType)` |
| `Element.GetExternalResourceReferences` | `IDictionary<ExternalResourceType, ExternalResourceReference> GetExternalResourceReferences()` |
| `ExternalResourceTypes.BuiltInExternalResourceTypes.RevitLink` | property (also `IFCLink`, `CADLink`, `PointCloud`, …) |
| `ExternalResourceReference.GetResourceShortDisplayName` | `string GetResourceShortDisplayName()` |
| `ExternalResourceReference.InSessionPath` | `string` (get/set) |
| `ModelPath.CloudPath` | `bool` — a direct cloud test, better than "user-visible path is empty" |
| `RevitLinkType.LoadFrom` | `LinkLoadResult LoadFrom(ExternalResourceReference, WorksetConfiguration)` — the cloud re-point overload |

## The same bug at four other call sites

Every `GetExternalFileReference()` in the repo is unguarded and will throw on a cloud link:

| File | Line | Effect on a cloud link today |
|---|---|---|
| `ReplaceLinkCapture.cs` | 67 | The reported symptom |
| `ReplaceLinkRunHandler.cs` | 178 | Fails as `noReference` ("can't tell what it points at") instead of the intended `cloudUnsupported` warning |
| `ReplaceLinkRunHandler.cs` | 648 | Scanning for the file loaded as another link — cloud links throw per link |
| `LinkAuditCapture.cs` | 72, 140 | Link audit rows lose their path |
| `PushCoordinatesToLinksRunHandler.cs` | 195 | Cloud links can't be resolved |
| `UpgradeLinksRunHandler.cs` | 723 | Same as `ReplaceLinkRunHandler.cs:648` |

## Proposed changes

### 1. New shared helper — `Source/Tools/Setup/LinkReference.cs`

One place that answers "what does this link point at?" for **both** reference kinds, so no
call site has to remember the guard:

```csharp
public enum LinkReferenceKind { None, File, Cloud }

public sealed class LinkReferenceInfo
{
    public LinkReferenceKind    Kind;         // None / File / Cloud
    public string               Path;         // user-visible file path — File only
    public string               DisplayName;  // cloud: GetResourceShortDisplayName()
    public LinkedFileStatus?    Status;
    public bool                 ReadFailed;   // a real read failure, not "it's cloud"
}

public static LinkReferenceInfo Resolve(RevitLinkType type)  // never throws
```

Order: `IsExternalFileReference()` → file path; else
`RefersToExternalResourceReference(BuiltInExternalResourceTypes.RevitLink)` (and `IFCLink`,
since two of the ten links in the screenshot are `.ifc`) → cloud; else `None`.

### 2. `ReplaceLinkCapture` — correct reason, no false read warning

Route through the helper. A cloud link becomes `Replaceable = false` with
`blocked.cloud`, **and no read warning** — being cloud-hosted is a known state, not a
failed read. `ReadWarning` stays reserved for genuine read failures, which is what it is for.

Cloud links also get their `DisplayName` and status filled in, so the row shows what the
link actually is instead of a bare name.

### 3. `ReplaceLinkRunHandler` — same guard at lines 178 and 648

So a cloud link that reaches the run is skipped with the existing `log.cloudUnsupported`
message ("cloud-hosted link; there's no local file to write over…"), which is the correct,
already-written message that is currently unreachable.

### 4. The three other call sites

Same helper, so an audit / push-coordinates / upgrade run over a cloud-linked model stops
silently losing every link's path.

### 5. Strings (`Strings/en/replaceLink.json`)

Adjust `blocked.cloud` to name the tool that *does* handle cloud models, matching the
run-handler wording:

```
"cloud": "Cloud-hosted (Autodesk Docs) — no local file to write over. Use Upgrade & Link Models."
```

## Decision needed from you — should cloud links become replaceable?

The above **fixes the reporting**; it does not make a cloud link replaceable. Whether it
*should* be is a UX call, so I'm not deciding it:

| Option | What it means | Trade-off |
|---|---|---|
| **A. Report correctly, stay blocked** *(recommended)* | Cloud links list as "Cloud-hosted — no local file to write over" | Honest and small; the tool's premise (overwrite the linked file with an upgraded copy) genuinely has no cloud equivalent |
| **B. Allow re-pointing a cloud link at a local file** | `LoadFrom(ModelPath, WorksetConfiguration)` on a currently-cloud link | Converts the link from cloud to a file path — a real change to the project's setup, and easy to do by accident |
| **C. Allow cloud → cloud replacement** | `ExternalResourceReference.CreateFromCloudPath(...)` + `LoadFrom(ExternalResourceReference, …)` | Keeps it cloud, but needs a cloud model *browser* in the picker (project GUID / model GUID), which is a much larger piece of work |

**Recommendation: A now.** It is the actual bug, it is contained, and B/C are separate
features that deserve their own branch.

## Files touched (option A)

| File | Change |
|---|---|
| `Source/Tools/Setup/LinkReference.cs` | **new** — guarded resolver for file *and* cloud link references |
| `Source/Tools/Setup/ReplaceLinkCapture.cs` | use the resolver; correct blocked reason; no false read warning |
| `Source/Tools/Setup/ReplaceLinkRunHandler.cs` | guard lines 178 and 648 |
| `Source/Tools/Setup/LinkAuditCapture.cs` | guard lines 72 and 140 |
| `Source/Tools/Setup/PushCoordinatesToLinksRunHandler.cs` | guard line 195 |
| `Source/Tools/Setup/UpgradeLinksRunHandler.cs` | guard line 723 |
| `Strings/en/replaceLink.json` | reword `blocked.cloud` |

No UI layout change, so no mockup pass is needed — the picker's structure is unchanged, only
the text in the already-existing blocked-reason rows.

## How to confirm the diagnosis on your machine

Open `%AppData%\LemoineTools\diagnostics.log` and look for:

```
[WARNING]  ReplaceLink: read external file reference  —  ...
```

repeated once per link. If the detail names `InvalidOperationException` (or a message about
the element not being an external file reference), the cloud diagnosis is confirmed outright.
If it names something else, tell me what it says — the fix changes.

## Branch

Already on `claude/replace-link-cloud-issue-o2s6ui`, cut from `main` at `848f5db`.

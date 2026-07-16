# Plan — Push Coordinates: stop loading nested links on the standalone open

## Problem
Push Coordinates opens each selected link file **standalone** (background) to move its Project
Base Point / Survey Point. The open uses `new OpenOptions { Audit = false }` with **no workset
configuration**, so a workshared central model opens with *all* worksets — and every nested
`RevitLinkInstance` in that file (they sit on user worksets) is loaded, pulling each nested link
off disk. The tool only needs the base points, which live on Revit **system worksets** and stay
open even when user worksets are closed. Result: slow opens for no benefit.

## Change (single, low-risk, gated on an existing flag)
File: `Source/Tools/Setup/PushCoordinatesToLinksRunHandler.cs` (~line 248, inside `PushOneLink`).

```csharp
var oo = new OpenOptions { Audit = false };
if (isWs)
    oo.SetOpenWorksetsConfiguration(
        new WorksetConfiguration(WorksetConfigurationOption.CloseAllWorksets));
linkedOpen = appApp.OpenDocumentFile(srcMp, oo);
```

`isWs` is already computed a few lines above (via `BasicFileInfo.Extract`).

### Why this is safe
- `CloseAllWorksets` closes only **user** worksets (nested links) — **system** worksets
  (base points, levels, grids) stay open and editable, so the base-point move still works.
- We do **not** detach, so `SynchronizeWithCentral` still writes the correction back to the
  team's central model.
- Non-workshared files have no worksets and no API lever — left unchanged.

## Not changing (audited, deliberately left alone)
- `UpgradeLinksRunHandler.cs:224` workshared path — already detaches + `CloseAllWorksets`.
- `UpgradeLinksRunHandler.cs:404` cloud path — foreground `OpenAllWorksets` is intentional
  (user must see the real model in Revit's native Save-As-Cloud dialog).
- `LoadFrom(... OpenAllWorksets)` at :306 / :378 / :880 — these reload the link *into the host*
  for display; the workset config there governs host visibility, not a background open.

## Verification
Cannot build/run on Linux (Revit + WPF are Windows-only). Needs a Windows/Revit plot to confirm
base points remain editable and sync succeeds under `CloseAllWorksets`. The `isWs` gate and the
existing all-or-nothing rollback (a failed move rolls back and closes without saving) mean a
worst case is a reported failure, never a corrupted file.

## Branch
Base: `main`. Develop on: `claude/push-link-coordinates-perf-qi20s7`.

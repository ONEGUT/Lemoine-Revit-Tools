# Plan — Split Align Coordinates into Align + Push Coordinates to Links

## Problem

`PublishCoordinates` is fixed (open transaction, correct `LinkElementId`
construction) but the user wants a different, more direct mechanism instead:
correct each link's own Project Base Point / Survey Point *inside its own
file*, save it, and reload — rather than writing shared-coordinate metadata
from the host. They also want this split into two separate tools: **Align**
(what exists today — resolves an anchor and repositions the *host's copy* of
each link instance) and a new **Push Coordinates to Links** tool that commits
that correction into the actual linked files.

Confirmed from you: these links use **Auto – Origin to Origin** positioning.
That's the load-bearing fact for this design — Origin-to-Origin is a fixed
per-instance transform set once at link-in time; it ignores base points
entirely, so **moving a link's own base point and calling `Reload()` changes
nothing in the host.** Something has to actively re-place the link instance
for the correction to take visual effect.

## Design

### Two tools, one hand-off

- **Align Coordinates** (already reworked, unchanged by this plan): resolves
  the host anchor (Internal Origin by default) and repositions each selected
  `RevitLinkInstance` in the host so it visually lines up. This stays exactly
  as it is — it's the "preview / apply in host" stage. Its "Publish shared
  coordinates" toggle and step get **removed**: publishing is no longer a
  user-facing choice here, since the new tool takes over persistence.
- **Push Coordinates to Links** (new): for each selected link, reads the
  link instance's *current* total transform (whatever Align — or the user
  manually — already put it at) and treats that as ground truth. It doesn't
  need to know how the position was derived; it only needs "where does this
  link sit right now."

### Per-link algorithm (new tool)

1. Read `RevitLinkInstance.GetTotalTransform()` — call it `T_current`. This
   is the transform we're about to bake into the link file.
2. Read the host's own `BasePoint.GetProjectBasePoint(doc).Position` and
   (if selected) `BasePoint.GetSurveyPoint(doc).Position` — these already sit
   at the resolved anchor from the last Align run.
3. Inverse-transform each host base point through `T_current` to get the
   equivalent point in the **link's own internal coordinates**:
   `linkInternalTarget = T_current.Inverse.OfPoint(hostBasePointWorld)`.
   This is what that link's own Project Base Point / Survey Point needs to
   become.
4. Open the link file in the background (`Application.OpenDocumentFile`,
   never an activated view — same rule as Upgrade Links). Detect worksharing
   via `BasicFileInfo.Extract`.
   - **Workshared source → open detached** (`DetachFromCentralOption.DetachAndPreserveWorksets`,
     all worksets closed), then **save as a new file** in a subfolder next to
     the host (mirrors `UpgradeLinksRunHandler`'s pattern exactly) — the live
     central model is never opened with worksharing enabled and never
     synced. This was your explicit call on the workshared question.
   - **Non-workshared source** → open normally, save in place (overwrite).
5. Inside that opened document, move `BasePoint.GetProjectBasePoint(ld)` /
   `BasePoint.GetSurveyPoint(ld)` (per the same "which point(s)" toggle
   Align already has) to `linkInternalTarget`, in its own transaction. Same
   unpin/move/re-pin discipline as `AlignCoordinatesRunHandler.MoveBasePoint`
   — this logic is lifted into a shared helper rather than duplicated.
6. Save (`SaveAs` per the destination rule above), close the document.
7. Back in the host: `doc.PublishCoordinates(...)` (host → link, now that
   both sides have matching real-world base points) to formally record the
   shared-coordinate relationship, then **delete the existing
   `RevitLinkInstance` and recreate it pointing at the (possibly relocated)
   file with `ImportPlacement.Shared`** ("Auto – By Shared Coordinates").
   This is the step that actually converts the link from the fragile
   "Origin-to-Origin + per-instance-transform" scheme to Revit's own
   self-correcting Shared Coordinates scheme — after this, the link
   continues to reposition itself correctly on any future reload, with no
   more per-instance transform math needed at all.
8. Report Aligned/Skipped/Failed per link with a reason, same discipline as
   the existing tool.

### ⚠ Needs a Windows/Revit plot to confirm

- That `ImportPlacement.Shared` on the recreated instance actually
  reproduces `T_current`'s position exactly, given a `PublishCoordinates`
  call made immediately beforehand in the same session. I'm confident in the
  mechanism (this is what Shared Coordinates positioning is for) but it
  hasn't been verified against a real project file with Origin-to-Origin
  links being converted mid-session.
- Whether deleting and recreating a `RevitLinkInstance` drops anything
  attached to the old instance (view-specific graphic overrides, worksets
  the instance itself was on, pinned state) that needs to be captured and
  reapplied to the new one.

## Files

- `Source/Commands/T08-Coordinates/PushCoordinatesToLinksCommand.cs` — new
  command, own `StepFlowWindow`, collects loaded-link data (same shape as
  `AlignCoordinatesCommand.CollectData`, reusable).
- `Source/Tools/T08-Coordinates/PushCoordinatesToLinksViewModel.cs` — new
  step-flow tool: pick links, pick which point(s) to correct (Project Base
  Point / Survey Point), pick the workshared-destination subfolder name,
  review, run.
- `Source/Tools/T08-Coordinates/PushCoordinatesToLinksRunHandler.cs` — the
  algorithm above. Shares `BasicFileInfo`/`OpenOptions`/`SaveAsOptions`
  patterns with `UpgradeLinksRunHandler.cs` (reference implementation for
  the open/detach/save/close sequence).
- `Source/Tools/T08-Coordinates/CoordinatesModels.cs` — add a
  `PushLinkSpec` (link id, which point(s) to move) alongside the existing
  `LinkAlignSpec`.
- `Source/Tools/T08-Coordinates/AlignCoordinatesViewModel.cs` /
  `AlignCoordinatesRunHandler.cs` — remove the `Publish` toggle/step and the
  `PublishCoordinates` call entirely; Align no longer touches shared
  coordinates at all, that responsibility moves fully to the new tool.
- `Source/App.cs` — register the new command's `ExternalEvent`/handler
  pair and its ribbon button, following the existing `T08-Coordinates`
  registration pattern.

## UI

Per `CLAUDE.md`, the `/revit-navisworks-ui` skill and a mockup-first pass
happen before any WPF code for the new tool's step flow — not part of this
planning pass.

## Silent-failure discipline to hold while implementing

- A link that fails to open, fails to save, or fails the recreate-instance
  step must be reported with its specific reason — never a silent skip.
- If `PublishCoordinates` fails right before the recreate step, do **not**
  proceed to delete the old instance — deleting it and failing to recreate
  would leave the link missing entirely. Recreate only after Publish
  succeeds; leave the original instance intact and reported as failed
  otherwise.

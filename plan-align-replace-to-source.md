# Plan — Align Replace-mode placements to the source element

**Base branch:** `claude/pensive-edison-7qyf81` (per user)

## Problem

In Copy Linear's **Replace** mode, placed family instances can land offset in Z
(and sideways) from the source run, and sometimes 90° rotated. Today's only
alignment control is the **"Rotate each instance to the run direction"** toggle
(`RotateToRun`), which spins each instance about the Z axis so the family's +X
axis points along the run (`CopyLinearEngine.PlanRotation`). It does nothing
about:

1. **Z / side offset** — the instance is placed at a point on the source's
   location line, but the family's origin may not be at its geometric centre
   (e.g. origin at the level, geometry above it), so the body sits below/beside
   the source.
2. **90° rotation** — a family modelled along its local Y axis comes in turned
   90° even after rotate-to-run.

## Approach — first-placement calibration (user's proposal)

Calibrate once from the **first source run + first placed instance**, then apply
the same correction to every following placement:

1. **Capture source boxes at gather time.** When source runs are collected,
   also store each run's world-space bounding-box corners (link box corners
   pushed through the link transform).
2. **Place the first instance normally**, then `doc.Regenerate()` **once** (a
   single calibration regen for the whole run — never per item) and read its
   bounding box.
3. **Compute the correction in pure math** (new `CopyLinearEngine` helpers, no
   element mutation):
   - Build a run-local frame: **along** = run direction, **side** = horizontal
     perpendicular, **up** = Z-ish third axis.
   - Measure both boxes' extents in that frame.
   - **Rotation:** test an extra 90° about Z (analytically rotating the
     measured box — no second regen). Keep whichever of 0°/90° makes the
     instance's cross-section (side-width × height) best match the source's.
   - **Translation:** offset so the instance box is **centred on the source box
     in side and up** (maximum face overlap), and its leading face sits **at
     the station** along the run (so consecutive sections line up end-to-end).
4. **Apply to the first instance** (`RotateElement` + `MoveElement`), then
   store the correction as *(delta angle, along/side/up offsets)* in run-frame
   components and apply it to every subsequent placement directly — re-projected
   through each run's own frame, with **no further regens**.
5. **UI:** new toggle in the Replace operation step — *"Align to source
   (calibrate from first placement)"*, default **on**, persisted in
   `CopyLinearSettings`. Clearer description on the existing rotate toggle.

Split mode is untouched — it copies the source element itself, so alignment is
inherent.

## Files changed

| File | Change |
|---|---|
| `Source/Tools/T06-CopyLinear/CopyLinearEngine.cs` | Run-frame + box-extents math, `ComputeAlignment` (pure XYZ math, reviewable in isolation) |
| `Source/Tools/T06-CopyLinear/CopyLinearRunHandler.cs` | Store source box corners on `SourceRun`; calibrate after first Replace placement; apply stored correction to all later placements |
| `Source/Tools/T06-CopyLinear/CopyLinearViewModel.cs` | "Align to source" toggle in Replace body; wire to run handler; review summary |
| `Source/Tools/T06-CopyLinear/CopyLinearSettings.cs` | Persisted `AlignToSource` (default true) |

## Notes / limits

- Calibration is **global** (first source + first instance, as requested).
  Offsets are stored relative to the station point on the location line, so
  they transfer across runs of differing direction; if a later run's *source*
  has a very different cross-section size, side/up centring is still correct
  as long as the location line is the element's centreline (true for
  ducts/pipes/tray).
- Bounding boxes are axis-aligned, so the 0°-vs-90° test is exact for
  orthogonal runs and approximate for diagonal ones; the cross-section
  comparison is done in the run frame to minimise that error.
- A failed calibration (no box, degenerate geometry) logs a warning and falls
  back to today's behaviour — never aborts the run.

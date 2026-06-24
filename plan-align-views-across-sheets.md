# Plan — Align Views Across Sheets

## Goal

A tool that takes one **source/reference sheet** as ground truth and, for every
selected **target sheet**, slides each viewport so its view's model location
registers *exactly* on top of the matching view from the source sheet. The
result: corresponding views overlay perfectly when you flip between sheets.

Views on the source sheet each cover a **different part of the building**; they
are independent and are NOT aligned to one another. Each is aligned only to its
counterpart on every target sheet. A target sheet missing a counterpart (or
carrying an unmatched extra) is a **reported error**, never a silent skip.

## Decisions (confirmed with user)

- **Matching:** model-region overlap — pair a source view with the target view
  whose crop region covers the same building area (shared world coordinates).
  Position- and name-independent.
- **Targets:** picked explicitly via `LemoineBrowserTreePicker`.

## The math (no regen needed)

A viewport maps a view's crop box onto the sheet linearly. For a world point `P`
visible in view `v`:

```
localP      = v.CropBox.Transform.Inverse.OfPoint(P)      // world -> view-local
cropCenter  = midpoint of (v.CropBox.Min, v.CropBox.Max)  // view-local
sheetOffset = ( (localP.X - cropCenter.X)/v.Scale,
                (localP.Y - cropCenter.Y)/v.Scale )
sheetPos(P) = viewport.GetBoxCenter() + sheetOffset
```

To overlay target view `T` onto source view `S`, choose a shared world anchor
`P` (the overlap-region centroid) and set:

```
T.SetBoxCenter( sheetPos_S(P) - sheetOffset_T(P) )
```

`SetBoxCenter` needs **no** `doc.Regenerate()` (CLAUDE.md viewport note), so the
run is fast even across hundreds of sheets — one regen at commit.

When `S` and `T` share scale + orientation the entire field overlays, not just
the anchor. Different scale or orientation can only register the anchor point —
that's flagged as a warning (see Edge cases).

## Matching algorithm

1. **Capture source viewports.** For each viewport on the source sheet: its
   `View`, `CropBox`, `Scale`, `ViewType`, `ViewDirection`, crop footprint in
   **world** coords (`CropBox.Transform.OfPoint(corners)`), and current
   `GetBoxCenter()`.
2. **Per target sheet**, capture the same for its viewports.
3. **Pair** each source view to the target view with the greatest world-region
   overlap, gated by: same `ViewType` and parallel `ViewDirection` (so a plan
   never matches a section). Overlap measured as intersection-over-union of the
   world footprints.
4. **Report** per target sheet:
   - `missing` — a source view with no target view above the overlap threshold.
   - `ambiguous` — two target views both strongly overlap one source view.
   - `extra` — a target view matching no source view.
   - `mismatch` — matched but differing scale / orientation (align anchor only).

## Tool structure (Step Flow)

Follows the repo's `ILemoineTool` + `StepFlowWindow` pattern.

- **Step 1 — Source sheet:** `LemoineBrowserTreePicker`, `SingleSelect = true`,
  fed by `BrowserTreeCapture.Capture(doc)` (captured on the Revit main thread).
- **Step 2 — Target sheets:** `LemoineBrowserTreePicker` (multi-select). Source
  sheet excluded from the candidate set.
- **Step 3 — Review & Run (always last, never conditional):** a "Scan" action
  builds the match report and lists every pairing + every error per target
  sheet; the Run button applies the alignment. Output log shows progress every
  ~5% and a `Found N … / No … found` summary.

## Files

New:
- `Source/Commands/Txx-.../AlignViewsAcrossSheetsCommand.cs` — `DebugToolCommand`-style launcher.
- `Source/Tools/.../AlignViewsAcrossSheets/AlignViewsAcrossSheetsViewModel.cs` — step content, picker wiring, `ILemoineToolCleanup`.
- `Source/Tools/.../AlignViewsAcrossSheets/AlignViewsAcrossSheetsScanHandler.cs` — `ExternalEvent`: capture + match + build report (read-only).
- `Source/Tools/.../AlignViewsAcrossSheets/AlignViewsAcrossSheetsRunHandler.cs` — `ExternalEvent`: apply `SetBoxCenter` in one transaction, one regen, cooperative cancel + checkpoints.
- `Source/Tools/.../AlignViewsAcrossSheets/ViewRegionMatch.cs` — match/result DTOs.

Edited:
- `Source/App.cs` — register the ribbon button + `ILemoineTool` entry.

(Exact `Txx` folder / panel placement to be confirmed against the existing tool
grouping when implementing.)

## Edge cases & failure routing

- **Scale mismatch** between matched views → warn in the run log + `LemoineLog.Warn`;
  align the anchor point only (no true field overlay possible).
- **Orientation / rotation mismatch** (non-parallel `RightDirection`) → same warn.
- **Rotated viewports** (`Viewport.Rotation`) folded into the transform or
  flagged if unsupported.
- **No viewports on source sheet** → abort with a clear message.
- **Section/elevation views** — world overlap uses the 3D footprint; `ViewDirection`
  gate prevents cross-type mis-matches.
- Handler clears its per-run payload in a `finally`; ViewModel nulls parked
  callbacks in `OnWindowClosed` (memory discipline).
- Empty/zero-match scan states "No counterpart views found" rather than silently
  doing nothing.

## Out of scope

- Aligning the source sheet's own views to each other (explicitly independent).
- Changing view scale/crop to *force* overlay — the tool only moves viewports.
- Creating missing views — a missing counterpart is reported, not synthesised.

## Build / test note

Windows-only build (UseWPF + net48). `/revit-navisworks-ui` skill will be
invoked before writing any WPF/XAML.

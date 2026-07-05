# Plan — Revive PDF Region Wand onto current main

Source: `claude/pdf-region-wand-qqh9in` (2 commits, ~6,875 lines, branched off `main` ~3 weeks
ago at `6df0e51`). Complete-looking: Revit-free geometry core + xUnit tests + Revit-side wiring
(command, modeless palette window, floor adapter, PDFium rasterizer). No TODOs/stubs found.

Chosen approach (per user decision): **fresh branch off current main, port the Revit-free core
close to verbatim, rewrite the Revit-side wiring against current conventions.**

## Why not a straight rebase

`main` has moved 71 commits past the branch's base, including three changes that touch every
file the old branch's Revit-side wiring depends on:
- Ribbon reorganized into named panels (`3f55436`) — the old branch's `App.cs` diff still
  assumes the pre-reorg "Testing" panel layout.
- Full text externalization (`2f9cc11`) — old branch has hardcoded UI/log strings.
- Multi-year build system (`0ef7716`) — old branch's `.csproj` predates the per-year
  Configuration scheme entirely.

A rebase would produce heavy, mechanical conflicts in exactly those three areas. Porting is
cleaner: the geometry core has zero Revit/ribbon/string dependencies and moves over unchanged;
only the Revit-side wiring needs a genuine rewrite.

## What ports ~as-is (Revit-free, low risk)

- `LemoineTools.PdfGeometry/` — Primitives, Raster, Vector, Simplify, Graph, Arcs, Pdf, Plans,
  Transform, Engine. `netstandard2.0`, only dependency is PdfPig.
- `LemoineTools.PdfGeometry.Tests/` — xUnit, targets `net48;net8.0`.
- Add both to `LemoineTools.sln`; add `ProjectReference` + glob-exclusion in
  `LemoineTools.csproj` (same pattern already used for `LemoinePreview`).

## What gets rewritten against current conventions

- `Source/Tools/Testing/PdfRegionWand/` (folder convention still valid — `Testing/` still holds
  experimental tools per current `LemoineTools.csproj` header comment).
  - `PdfRegionWandCommand.cs`, `PdfRegionWandEventHandler.cs`, `PdfRegionWandWindow.xaml(.cs)`,
    `FloorAdapter.cs`, `IRegionOutputAdapter.cs`, `PdfToModelTransform.cs`,
    `PdfiumPageRasterizer.cs`, `PdfRegionWandSettings.cs`.
  - All user-facing strings (window chrome, labels, run-log lines) go through
    `LemoineStrings.T("pdfRegionWand....")` backed by a new `Strings/en/pdfRegionWand.json`.
  - `App.cs`: register handler/event pairs following the existing block pattern; add one ribbon
    button (panel placement — asked separately).
  - `LemoineTools.csproj`: add `PdfiumViewer` + native package refs (net48 only for now — 2025+
    configs currently have placeholder `libs*` folders anyway, so PDFium's net8.0-windows
    compatibility is a later concern, not a blocker).

## Window UI

Modeless palette, custom chrome (`LemoineTitleBar` pattern, not `StepFlowWindow` — this is a
bespoke window like `FiltersSettingsWindow`). Sections: Source (PDF pick + scale + mode),
Actions (pick point / draw split lines), Preview (traced-loop canvas + confirm/discard), Regions
list, Output (adapter + Floor options), Settings, Log. Per `/revit-navisworks-ui` skill: mockup
first, WPF after approval.

## Sequencing

1. Ribbon placement decision (asked separately).
2. Mockup image for the palette window (asked separately).
3. Port `LemoineTools.PdfGeometry(.Tests)` verbatim + wire into `.sln`/`.csproj`.
4. Rewrite Revit-side wiring: settings → adapters → transform → rasterizer → event handler →
   window → command → `App.cs` → ribbon button → `Strings/en/pdfRegionWand.json`.
5. Post-change silent-failure scan (per CLAUDE.md) before calling it done.

## Known open risk (not blocking)

`PdfiumViewer` 2.13.0 + native x86_64 package — verified compatible with net48. Not yet verified
against net8.0-windows (Revit 2025+ configs). Flagged, not solved here, since those configs can't
build yet anyway (placeholder `libs2025/2026/2027`).

# Plan — PDF Region Wand

A persistent, modeless "magic wand" palette that extracts closed regions from an imported PDF
underlay and creates Revit elements (floors first) from them. Branch: `claude/pdf-region-wand-qqh9in`
(based on `main`).

---

## 1. Project layout — what gets added

The repo today is a single net48 csproj (`LemoineTools.csproj`) plus the preview app; the AutoDim
"Revit-free core" is a folder convention, not a separate project, and there is **no test project**.
This tool introduces the first real separate projects:

| Path | Purpose |
|---|---|
| `LemoineTools.PdfGeometry/LemoineTools.PdfGeometry.csproj` | **Revit-free geometry core.** Targets `netstandard2.0` (consumable by net48, testable by a modern test runner on any OS). References **PdfPig** only. Zero Revit references. |
| `LemoineTools.PdfGeometry.Tests/…Tests.csproj` | xUnit test project, multi-targeted `net48;net8.0` so tests run in VS on Windows and via `dotnet test` offline. |
| `Source/Tools/T08-PdfWand/` | Revit-side wiring: command, event handlers, palette window, transform, adapters, settings. |
| `LemoineTools.sln` | Both new projects added. |
| `LemoineTools.csproj` | `ProjectReference` to PdfGeometry + `PackageReference` PdfiumViewer (or chosen fork) + native pdfium deploy step. |

### 1.1 Geometry core (`LemoineTools.PdfGeometry`)

All in PDF point space, doubles, no WPF/Revit types.

```
Primitives/      Pt2, Seg2, Bounds2, GeomTol
Graph/           SharedEdge, RegionFace, FaceStatus, EdgeRef, RegionSession,
                 EdgeReconciler (match traced boundary → existing edges, split partial overlaps)
Raster/          BitGrid (1bpp barrier mask), ScanlineFloodFill (max-size leak guard),
                 MooreContourTracer (outer + hole contours), LineRasterizer (split lines → mask, 2–3 px)
Vector/          PdfPathExtractor (PdfPig path ops → flattened segments), SegmentGraph
                 (endpoint clustering), LoopFinder (smallest enclosing loop around seed)
Simplify/        DouglasPeucker, OrthoSnap (angle snap + collinear merge)
Pdf/             PdfContentProbe (PdfPig path-op count → Vector|Raster detection),
                 IPageRasterizer (interface only — PDFium impl lives in the addin),
                 PageRaster (grayscale buffer + Binarize(threshold))
Plans/           RegionPlan (serializable handoff to Revit side), ExtractionMode
```

Key decision: **PDFium stays out of the core.** `IPageRasterizer` is the seam; the net48 addin
implements it with PdfiumViewer. Tests feed synthetic `PageRaster`/`BitGrid` bitmaps, so the whole
core tests offline with no native deps. PdfPig is pure managed netstandard2.0, so detection and
vector extraction *are* in the core and testable against tiny synthetic PDFs.

**Shared-edge invariant** (the heart of the tool): `RegionSession.RegisterFace` runs every traced
boundary through `EdgeReconciler` — portions that coincide with existing `SharedEdge` polylines
(within `GeomTol`) are replaced by references to the stored records, splitting existing edges at
junction points when overlap is partial. User split lines enter as exact polylines and are used
verbatim by faces on both sides. New floors therefore share geometry by construction, never by
near-coincidence. Split lines added later invalidate any traced-but-uncreated face they cut.

### 1.2 Revit side (`Source/Tools/T08-PdfWand/`)

```
PdfWandCommand.cs            IExternalCommand — validates active plan view, launches palette on STA thread
PdfWandPaletteWindow.xaml(.cs)  Modeless palette (layout §2)
PdfWandViewModel.cs          Session orchestration, region list, output-adapter selection
PdfWandPickEventHandler.cs   PickObject(ImageInstance) / PickPoint, marshals result back
PdfWandSplitLineHandler.cs   PostCommand(DetailLine) setup + DocumentChanged capture + cleanup
PdfWandCreateEventHandler.cs One RegionPlan → one adapter call → ONE transaction ("PDF Wand Floor — Region N")
PdfWandWatchdog.cs           DocumentChanged subscription for the palette's life: created element
                             deleted (undo/manual) → face released back to Traced; named handler,
                             detached on window Closed (per crash rules)
PdfToModelTransform.cs       PDF pt / pixel ↔ model ft, both directions, from ImageInstance placement
                             + page size + DPI + drawing scale (Y-flip handled here, unit-tested)
PdfiumPageRasterizer.cs      IPageRasterizer impl; explicit LoadLibrary of native pdfium.dll from the
                             addin folder before first use (Revit's working dir is not the addin dir)
Adapters/IRegionOutputAdapter.cs   DisplayName + Create(RegionPlan, transform, doc, opts) → ElementIds
Adapters/FloorAdapter.cs     Floor type, level, structural toggle, holes → inner sketch loops or
                             ignored below area threshold
PdfWandSettings.cs           XML singleton (ClashDimensionSettings pattern): DPI (300), binarize
                             threshold (0.5), simplify tolerance (1" @ scale), ortho angle (2°),
                             default scale, max flood size, keep/delete split lines, detail/model line
App.cs                       Handler + ExternalEvent registration; ribbon button
```

External-event flow per wand click: palette button → VM sets seed request → `PickEvent.Raise()` →
`PickPoint` on Revit thread → inverse transform to PDF space → flood/loop in core (background, off
both UI threads) → preview in palette → Confirm → `CreateEvent.Raise()` → one transaction →
`ElementId` recorded on the face.

Split lines: **Draw split lines** → setup event subscribes `DocumentChanged`, posts
`PostableCommand.DetailLine` (works because the palette is modeless) → user draws with native
snapping → **Done drawing** → captured `CurveElement`s sampled at chord tolerance into exact
polylines → registered as barriers + shared edges → lines deleted (or kept, per setting). Capture
tolerates mid-session undo of a line (ids re-validated at Done).

---

## 2. Palette layout (proposal)

Fixed-width modeless window (~420 px, `SizeToContent="Height"` capped + scroll), custom chrome via
`LemoineTitleBar`, `LemoineControlStyles.InjectInto` on Loaded, owner = Revit HWND via
`WindowInteropHelper`. Never `Topmost=true` globally — the window must tolerate losing focus
constantly while picking/drawing; re-activation only on explicit user action.

```
┌─ PDF Region Wand ──────────────────────────── ✕ ─┐
│ SOURCE          (LemoineSectionCard)              │
│  [Select PDF underlay]   Plan_L2.pdf  (page 1/3)  │
│  Scale: [1/4" = 1'-0" ▾]  Page: [1] (stepper)     │
│  Mode:  ● Vector (auto)   [override ▾]            │
├───────────────────────────────────────────────────┤
│ ACTIONS                                           │
│  [ Pick point ]  [ Draw split lines ]             │
│  ( [Done drawing] replaces the row while active ) │
├───────────────────────────────────────────────────┤
│ PREVIEW   (in-palette canvas, §Q3)                │
│  traced loop over raster snippet; [Confirm][Discard]│
├───────────────────────────────────────────────────┤
│ REGIONS   (scrolling list)                        │
│  #4   312 ft²   Created    [zoom] [✕]             │
│  #5   180 ft²   Traced     [Create] [✕]           │
│  #6    95 ft²   Invalidated (split line)    [✕]   │
│  [ Create all traced (N) ]                        │
├───────────────────────────────────────────────────┤
│ OUTPUT: [Floor ▾]  Type: [Generic 6" ▾]           │
│         Level: [Level 2 ▾]  □ Structural          │
├───────────────────────────────────────────────────┤
│ ⚙ settings row (DPI, threshold, tolerance…)       │
│ LOG (standard Lemoine log panel, collapsible)     │
└───────────────────────────────────────────────────┘
```

Output selector is the `IRegionOutputAdapter` seam — Floor only in v1; the section's settings area
is provided by the adapter so ceilings/rooms/etc. slot in later. All numeric inputs use
`LemoineInlineStepper`. Status changes (face invalidated, floor deleted → released) appear both in
the region list and the log.

---

## 3. Build/dependency risks (will verify before dependent code)

- **PdfPig**: netstandard2.0, pure managed — restores under net48. Verify exact version pin.
- **PDFium wrapper**: PdfiumViewer is net40+ but stale; native binary must be deployed beside the
  addin and loaded with explicit `LoadLibrary` (Revit's process working directory ≠ addin folder).
  If the wrapper's resolve logic can't be pointed at the addin folder, fallback plan: P/Invoke the
  ~6 pdfium exports we need (`FPDF_LoadDocument`, `FPDF_RenderPageBitmap`, …) directly — small
  surface, removes the stale-wrapper risk. Flagged per the task; will confirm before wiring.
- **This container has no Windows/.NET Framework** — main addin compile-checks happen on the
  user's machine as usual; geometry core + tests will run here via dotnet SDK (to be installed).

## 4. Test plan (geometry core only)

Flood fill (rect / L-shape / column hole / leak guard); split-line compositing (one room → two
faces referencing the **identical** `SharedEdge`); shared-edge reuse incl. partial-overlap splits;
contour + DP + ortho-snap vertex counts; vector loop-finding with gapped endpoints;
`PdfToModelTransform` round-trips at multiple scales/rotations (transform math mirrored into a
core-side pure class so it's testable); face invalidation by split lines.

## 5. Build order

1. Geometry core + tests passing (offline, this container).
2. Transform class + tests.
3. Revit wiring: command, handlers, settings, FloorAdapter.
4. Palette UI (via `/revit-navisworks-ui` skill) + watchdog + split-line capture.
5. Silent-failure scan, then commit/push in reviewable chunks throughout.

## 6. Decisions (user-confirmed)

1. **Palette window pattern** — **one-off window** for this tool; extract a `PaletteWindow` base
   only when a second palette tool appears.
2. **Split lines** — **detail lines** by default, settings toggle for model lines.
3. **Preview before create** — **in-palette WPF preview canvas** (traced loop over the raster
   snippet); no Revit transactions for previews, keeping one-undo-step-per-floor clean.
4. **Arcs** — **reconstruct arcs in v1.** Arc fitting runs per `SharedEdge` as a deterministic
   function of the stored polyline (`ArcFitter` in the core), so adjacent faces referencing the
   same edge records reconstruct identical arcs — no sliver mismatch at shared boundaries.
   `RegionPlan` carries curve pieces (line/arc) alongside the raw polylines; `FloorAdapter` builds
   Revit `Line`/`Arc` curves from them.

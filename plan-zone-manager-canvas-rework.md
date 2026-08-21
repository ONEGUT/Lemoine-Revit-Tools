# Plan — Zone Manager canvas rework (design board `Zone Manager.dc.html`, screens 2a / 3a / 3b)

Branch: `claude/zone-manager-ui-rework-i630zq` (exists, off `main` — which already carries the
merged zones work from PR #158). **Confirm this is the base you want before I touch code.**

Source of truth: the supplied design bundle — `Zone Manager.dc.html` (board), its `README.md`
handoff, `github.md` (screen map). `support.js` is the Claude Design canvas runtime, not design
content; nothing in it is implemented.

---

## 1. What changes

The Zone Manager stops being *a tree beside a property pane* and becomes *a plan you can look at*.
The tree could not answer the questions the window exists for — do the areas tile the floor, do the
matchlines meet, does this group fit its title block — because it only ever showed names.

| | Today | After |
|---|---|---|
| Shell rows | 40 toolbar / \* / 44 footer | **38 toolbar / \*** — no footer bar |
| Content | 300 rail \| 6 splitter \| detail | **180 navigator \| \* canvas \| 5 splitter \| 300 properties** |
| Rail | one search + full node tree (buildings→levels→areas→views→sheet sets) | **two levels only** — levels with their areas; a SHEETS section; docked Re-solve footer |
| Middle | *(nothing)* | **interactive 2D canvas** — Plan or Sheet |
| Status | footer `_statusText` | **floating bottom-right chip** (Auto Filters pattern) |
| Flows | Discover button only | **Discover ↗ / Create Views ↗ / Build Sheets ↗** in the toolbar |
| Window radius | square | **8px**, contents clipped |

Screen 1a on the board is today's window, included for comparison only — not implemented.
Shells 2b and 2c are rejected alternatives — not implemented. **2a is the chosen direction.**

---

## 2. Files

### Changed

| File | Change |
|---|---|
| `Source/Tools/Zones/Windows/ZoneManagerWindow.xaml` | Reshaped: 38/\* rows, 8px radius, 4-column content grid, floating chip slot. ~116 → ~110 lines. |
| `Source/Tools/Zones/Windows/ZoneManagerWindow.xaml.cs` | Split into partials (below). Navigator/properties/flows rewritten; the existing field/card/row factories are **kept and reused**. |
| `Source/Commands/Zones/ZoneManagerCommand.cs` | Adds the per-level **geometry snapshot** capture on the Revit main thread. |
| `Source/Commands/Zones/ZoneViewsCommand.cs`, `ZoneSheetsCommand.cs` | Extract a `public static void Open(UIApplication)` from `Execute`, exactly as `ZoneDiscoverCommand` already does, so the toolbar can launch them. |
| `Source/App.cs` | Registers two more open-flow ExternalEvents beside `ZoneOpenDiscoverEvent`. |
| `Source/Framework/AppSettings.cs` | Adds the two scale keys the design needs and the repo lacks (§6). |
| `Strings/en/zones.json` | New keys for every new string (§7). |

### Added

| File | Purpose |
|---|---|
| `Source/Framework/Zones/ZoneGeometrySnapshot.cs` | Revit-free DTO: per-level outline rings, area extents, scope-box extents, anchors, model counts. The window's whole picture of the building. |
| `Source/Tools/Zones/Windows/ZoneManagerWindow.Navigator.cs` | Building selector, LEVELS/SHEETS sections, level/area/sheet-size/group rows, add buttons, docked footer. |
| `Source/Tools/Zones/Windows/ZoneManagerWindow.Canvas.cs` | Breadcrumb, Plan\|Sheet toggle, the plan renderer, hover readout, legend, scale chip, empty state. |
| `Source/Tools/Zones/Windows/ZoneManagerWindow.SheetCanvas.cs` | Sheet-mode renderer: paper, drawing area, title block + key-plan slot, placed views, matchlines, slack dimension, fits/overflow chips. |
| `Source/Tools/Zones/Windows/ZoneManagerWindow.Properties.cs` | The right pane: header + type pill, cards, 96\|\* field grid, warning/confirmation cards, list cards, area chips. |
| `Source/Tools/Zones/Windows/ZonePlanCanvas.cs` | The reusable canvas control itself — fit transform, hit-testing, constant-weight strokes. |

Partial-class split is because the code-behind is already 1,989 lines and this adds a renderer.
Methods shared across the partials go `internal`, never `private` (CS0122 — CLAUDE.md).

---

## 3. The canvas

A `Canvas` of `Path`/`Rectangle`/`TextBlock` children under a fitted
`ScaleTransform`+`TranslateTransform` — not a `DrawingVisual`, at this element count, and it gives
hit-testing for free. Each area shape carries its zone id in `.Tag`; one `MouseLeftButtonUp` sets
the selection, `MouseMove`/`MouseLeave` drive the readout.

Rules I will hold it to:

- **Stroke weights stay visually constant** under the fit transform — thickness divided by the
  scale factor, so a 2.0 outline is 2px on a 40ft floor and on a 400ft one.
- **The outline is one continuous ring.** An L-shaped floor is a single closed `PathGeometry`, so
  the interior reads as one slab and not as overlapping rectangles.
- **Dash arrays convert**: WPF `StrokeDashArray` is in multiples of stroke thickness, the design's
  numbers are absolute. Matchline `7 3 2 3` @1.2 → `5.83 2.5 1.67 2.5`. Scope box `4 3` @1.0 → `4 3`.
- **`TextOptions.TextFormattingMode="Display"` + `UseLayoutRounding="True"`** on the window, or the
  6–9.5px in-canvas Consolas turns to mush.
- **The canvas is read-only.** It selects; the properties pane edits. Dragging extents was
  considered and excluded by the brief.
- No animation, anywhere — hover and selection are immediate.

Layer semantics (fill / stroke / weight / dash), verbatim from the board: building outline
`Surface`/`TextSub` 2.0; area unselected `Accent`@7% / `Accent`@55% 1.0; area selected
`Accent`@20% / `Accent` 1.6; scope box none / `TextDim` 1.0 `4 3`; matchline none / `Green` 1.2
`7 3 2 3`; anchor cross `TextDim`→`Accent` 0.8/1.0. Selection = four 5×5 `Accent` corner handles.

---

## 4. Geometry capture

The window has no Revit API access, so `ZoneManagerCommand.Execute` captures, on the main thread,
before the STA thread starts — reusing `ZoneSlabOutline.Collect` per zone level, the *same* path
the key plan already draws from, so a plan on screen and a plan in a title block can never disagree.

```
ZoneGeometrySnapshot
├─ Levels : List<LevelOutline>       // hostLevelName, rings (closed loops, model ft), bounds, Source
├─ Counts : scope boxes, title block types, host levels, linked models   // the 3b card
└─ CapturedAt
```

Everything else the canvas draws — area rectangles, anchors, matchlines, handles, sheet placements —
is derived from that snapshot plus the library. No further model access, ever.

Two failure rules: a level with **no** captured outline draws its areas over an empty surface rather
than blocking; and a zero-result capture is **logged explicitly** ("Captured N outlines for M zone
levels"), never presented as a silent empty picture — a silent empty result is indistinguishable
from a broken collector.

Cost: one `ZoneSlabOutline.Collect` per zone level at window open. On a 30-level project that is 30
element collections on the main thread. If it measures slow I will capture lazily per level via an
ExternalEvent rather than blocking the open — flagged now, decided on a Windows run.

---

## 5. Behaviour

- **One selection field** (`_selectedNodeId`) drives navigator highlight, breadcrumb tail, canvas
  selection state and the whole properties pane. Both directions read the same field — clicking the
  plan and clicking the navigator cannot disagree because there is nothing to keep in sync.
- **Hover readout** follows the cursor: +12px right, vertically centred, non-hit-testable, positioned
  from `Mouse.GetPosition` relative to the plan host on an overlay `Canvas`.
- **Plan | Sheet toggle** switches what the canvas draws for the current selection. Sheet mode is
  only meaningful for a selection that resolves to a group; an area shows the group it sits on.
- **Flow buttons** raise `DiscoverRequested` / `CreateViewsRequested` / `BuildSheetsRequested`
  through ExternalEvents (the window cannot open a Revit-reading window itself), save the library
  first, and disable all three while a flow is open. On return: reload, recompute, repaint — the
  existing `OnWindowActivated` path already does exactly this for Discover.
- **Create Views / Build Sheets are disabled until at least one area has resolved extents**, and the
  empty state says so in its footnote.
- **Warning cards are driven by library state, never a dismissable flag** — the scope-box-resized
  card disappears when the placement stops being stale, not when someone clicks it away.
- **Re-solve** in the navigator footer, accent whenever any placement is stale, and repeated as an
  inline action on any warning card a re-solve would clear.

---

## 6. Tokens

Every colour goes through `SetResourceReference` against the existing theme keys — never a hex. The
design's Dark Mono hexes map exactly onto the existing palette, which is why they match:

`#111111` `LemoineBg` · `#1a1a1a` `LemoinePageBg`/panel · `#222222` `LemoineSurface` ·
`#2a2a2a` `LemoineRaised` · `#686868` `LemoineBorder` · `#d4d4d4` `LemoineText` ·
`#919191` `LemoineTextDim` · `#8c8c8c` `LemoineTextSub` · `#4f8fc4` `LemoineAccent` ·
`#1b2d3e` `LemoineAccentDim` · `#4ec994` `LemoineGreen` on `LemoineGreenDim` ·
`#f47067` `LemoineRed` on `LemoineRedDim`.

Two gaps in the scale, added to `AppSettings.ApplyScaleTo` so they scale with UI size like
everything else:

- `LemoineFS_Meta` = 9.5 — elevations, pills, chips, legend labels. (`LemoineFS_XS` is **not**
  reused: it is aliased to SM and re-pointing it would move text in every other window.)
- `LemoineRadius_Window` = 8 — the outer window radius.

In-canvas text (6 / 7 / 7.5px) is drawn *inside* the fit transform on primitives, where a resource
reference cannot apply cleanly; those go through `AppSettings.Instance.S(...)`, the framework's own
scale helper, and I will comment why at the call site.

---

## 7. Strings

Every new user-facing string goes in `Strings/en/zones.json` under `manager.*` —
`manager.flows.*`, `manager.nav.*`, `manager.canvas.*` (breadcrumb ghosts, legend labels, scale
chip), `manager.empty.*` (the three verbatim copy blocks), `manager.props.*`, `manager.status.*`.
Node-kind tokens and Segoe MDL2/glyph codepoints stay hardcoded, per the rule. Verified before
commit by flattening the JSON and diffing against every `AppStrings.T("zones.manager…")` in the
rewritten files — a missing key falls back silently rather than failing the build.

Glyphs used are all already available: `⚙ ✕ ˜ › ↗ ＋ ⌫ ● ✓`. Written as
`char.ConvertFromUtf32(...)` where PUA, so the Edit tool can handle the source.

---

## 8. Hosting constraints carried over (not re-derived)

- Bespoke `Window` on its own STA thread → **installs its own `Dispatcher.UnhandledException`**
  net, or a stray throw hard-crashes Revit with no diagnostics entry. Already present; kept.
- `IToolCleanup.OnWindowClosed` is never called for a bespoke window → save stays on `OnClosed`.
- Theme/UI-size subscriptions stay **named handlers**, detached in `OnClosed`, marshalled with
  non-blocking `BeginInvoke` behind a `HasShutdownStarted` guard.
- **No `WindowInteropHelper.Owner`.** The generic WPF skill asks for
  `ComponentManager.ApplicationWindow`; this repo forbids it (it crashes Revit) and removed window
  ownership by explicit decision. Repo rule wins.
- `Popup` for the hover readout would need `StaysOpen=true`; I will use an overlay `Canvas` instead
  and avoid the question entirely.
- Any element meant as a click target gets `Background = Brushes.Transparent` by **direct
  assignment** — a null background is only hit-testable on its glyphs.

---

## 9. Verification

The project cannot build on Linux (`UseWPF` + net48 needs the Windows-only desktop SDK), so I
cannot compile this here. What I will do instead, before reporting done:

1. Re-read the diff against the board's numbers, screen by screen, region by region.
2. Run the silent-failure scan CLAUDE.md requires and present the findings list.
3. Verify every `AppStrings` key resolves (§7).
4. Grep every new option/value from its handler property forward to the call that consumes it.

Compile errors from your first Windows build come straight back to this branch as fixes — no new
plan, no new branch.

## 10. Out of scope

The three step flows (Discover, Create Views, Build Sheets) keep their current design and are not
touched beyond gaining a `static Open(...)`. Canvas geometry editing is excluded by the brief.
Properties panels for building / level / view-definition / view-override selections are not drawn on
the board; they keep their current content, rebuilt on the new card and field-grid metrics.

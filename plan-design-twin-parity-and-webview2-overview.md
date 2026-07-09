# Plan — Design-Twin Parity Harness + WebView2 Overview Test Copy

Two deliverables:

- **Part A — Parity harness.** An HTML "design twin" of the design system plus tooling that
  measures the real WPF UI and the twin to sub-pixel precision, diffs them, and reports every
  deviation — so twin and plugin can be driven to exact agreement on every edge and text position.
- **Part B — WebView2 pilot.** A second, web-native copy of the Tools Overview window rendered
  in a WebView2 control inside the plugin, opened from its own Developer-panel button, so both
  directions (WPF + twin vs. web-native) can be tested side by side in Revit before committing
  to either.

The Tools Overview window is the pilot surface for **both** parts: it is read-only, fully
data-driven from the Revit-free `ToolsOverviewCatalog`, and modelled on `GlobalSettingsWindow`
(no wizard chrome) — the smallest real window that exercises tabs, cards, chips, scroll, theme,
and UI-size scaling.

---

## Part A — Parity Harness

### A1. Theme/token export (runs anywhere, no Windows needed)

- **`devtools/design-twin/export_palette.py`** — parses `Source/Framework/ThemePalette.cs`
  (plain C# hex/number literals) and emits:
  - `devtools/design-twin/palette.json` — every theme's colors + font sizes + radii,
  - `devtools/design-twin/palette.css` — one CSS custom-property block per theme, switched by
    `html[data-theme="dark-mono"]` etc., with variable names matching the WPF resource keys
    (`--lemoine-bg` ↔ `LemoineBg`, `--lemoine-radius-card: 10px` ↔ `LemoineRadius_Card`).
  - The parse is count-checked: if a theme or member fails to parse, the script exits nonzero
    with the member name — never a silently incomplete palette.
- Re-run whenever `ThemePalette.cs` changes; generated files are committed so the twin works
  without running the exporter.

### A2. The design twin

- **`devtools/design-twin/`**
  - `twin.css` — shared tokens + house component styles (cards, pills, tabs, steppers…),
    importing `palette.css`.
  - `index.html` — component gallery (one section per house control) for designing controls
    in isolation.
  - `pages/tools-overview.html` — the first full-page twin: the Tools Overview window,
    element-for-element.
- **`data-id` convention:** every twin element that corresponds to a WPF element carries
  `data-id="<stable-id>"`. On the WPF side the id is `FrameworkElement.Name` where one is set;
  where none is set the exporter falls back to a structural path (`Border[2]/StackPanel[0]/TextBlock[1]`).
  Explicit names are added to WPF elements only on surfaces being actively polished — no
  big-bang renaming pass across 70 files.

### A3. WPF snapshot exporter (LemoinePreview — Windows only)

- **`LemoinePreview/Parity/SnapshotExporter.cs`** — walks the visual tree of a rendered window
  and writes, per element: id, type, bounds in window-root DIPs (`TransformToAncestor`), text
  content, font size, corner radius, visibility → one JSON file. Plus a PNG of the window via
  `RenderTargetBitmap` at 96 DPI, so 1 JSON unit = 1 image pixel exactly.
- **`LemoinePreview/Parity/CaptureRunner.cs` + CLI mode** — `LemoinePreview.exe --capture [outDir]`
  renders each registered surface offscreen and captures it without any clicking:
  - Surfaces: Tools Overview (each category tab), the demo tool's StepFlow steps, the controls
    gallery tab. New surfaces register in one list.
  - Default matrix kept small for speed: **Dark Mono + Light Clean × Medium size**; `--full`
    captures all 8 themes × 3 sizes.
  - Animations are forced to zero duration in capture mode so no frame is captured mid-transition.
- Output: `devtools/design-twin/snapshots/wpf/<surface>.<theme>.<size>.{json,png}` — committed
  to the branch so the comparator (and Claude) can read them on Linux.
- An optional Visual Studio post-build hook that runs `--capture` is **documented but not
  enabled by default** — every plain build already fans out to 4 Revit years (CLAUDE.md), so
  capture stays an explicit, fast, separate command.

### A4. Twin measurement + comparator (runs on Linux / in Claude sessions)

- **`devtools/design-twin/measure.mjs`** — Playwright + the pre-installed headless Chromium
  (`executablePath: /opt/pw-browsers/chromium`) loads a twin page at the same logical viewport
  size, extracts the identical JSON schema from `getBoundingClientRect()` for every `data-id`
  element, screenshots → `devtools/design-twin/snapshots/twin/…`.
- **`devtools/design-twin/compare.py`** — joins WPF and twin JSON by id and reports:
  - every element whose x/y/width/height delta exceeds tolerance (default **0.5 DIP**),
    as a sorted worst-first table;
  - every id present on one side but missing on the other (reported, never silently dropped);
  - an overlay PNG (both screenshots blended, out-of-tolerance elements outlined) for human review;
  - exit code nonzero when anything is out of tolerance, so it can gate a pass/fail check.
  - Scoring is **geometry-only** (from the JSON). Raw pixel-diffing of text is advisory only —
    ClearType vs. Chromium glyph rasterization always differs even at identical text positions.
- **Font caveat handled here:** the Linux Chromium's Segoe UI substitute has different character
  widths, so auto-sized (text-hugging) elements will differ in width by font metrics alone.
  The comparator flags these; the fix is pinning the twin element's width to the WPF-measured
  value (WPF is always ground truth for *measurement*; the twin is ground truth for *intent*).

### A5. The working loop (who does what)

1. Design change is made on the twin (Claude design / HTML edit) — no Windows needed.
2. Claude translates the change into the WPF code.
3. User pulls, runs `LemoinePreview.exe --capture`, commits/pushes `snapshots/wpf/`.
4. Claude runs `measure.mjs` + `compare.py`; fixes any out-of-tolerance element; repeat 3–4
   (systematic offsets get folded into `twin.css` once, so later rounds converge in one pass).
5. Done when the report is all zeros.
- `devtools/design-twin/README.md` documents this loop and every command.

---

## Part B — WebView2 Tools Overview Copy

### B1. Dependency & build

- Add NuGet **`Microsoft.Web.WebView2`** to `LemoineTools.csproj` — one `PackageReference`,
  valid for net48 (2024) and net8.0-windows (2025–2027), all four year Configurations.
- The package's build targets copy the native `WebView2Loader.dll`; verify it reaches each
  year's `DeployDir` in the build log for all four years.
- Requires the **WebView2 Evergreen Runtime** on the user's machine (ships with Windows 10/11 /
  Edge, so effectively already present). If `CoreWebView2Environment` creation fails, the window
  shows a "WebView2 runtime not available" message and logs via `DiagnosticsLog.Error` — a
  missing runtime must never crash or silently show a blank window.

### B2. Web assets

- **`Source/Resources/Web/overview/`** — `index.html`, `overview.css`, `overview.js`.
  `overview.css` imports the **same generated `palette.css`** as the design twin — one token
  source for both.
  MSBuild copies `Source/Resources/Web/**` into each year's `DeployDir`; served to the control
  via `CoreWebView2.SetVirtualHostNameToFolderMapping` from the deploy folder — fully local,
  no network access.
- **Data-driven, no duplicated content:** C# serializes `ToolsOverviewCatalog.Categories`
  (already `AppStrings`-externalized), the active `ThemePalette`, and the UI scale to JSON and
  pushes it with `PostWebMessageAsJson`; `overview.js` builds the DOM from it. No user-facing
  string is hardcoded in HTML/JS.
- Glyphs: the catalog's Segoe MDL2 codepoints render via `font-family: "Segoe MDL2 Assets"`
  (present on every Windows machine WebView2 runs on). They show as tofu in the Linux twin
  preview — acceptable for the pilot, noted in the README.

### B3. Host window, command, ribbon button

- **`Source/Framework/Web/ToolsOverviewWebWindow.cs`** — plain WPF `Window` hosting the WebView2
  control, following the existing window conventions: no HWND owner; named
  `ThemeChanged`/`UiSizeChanged` handlers detached on `Closed`, marshalled with guarded
  `BeginInvoke`, pushing the new theme/scale JSON into the page live;
  `DefaultBackgroundColor` set to the theme page background so there is no white flash before
  first render. Opened on Revit's main STA thread exactly like the existing overview window —
  no new threading model.
- **`Source/Commands/OpenOverviewWebCommand.cs`** — mirrors `OpenOverviewCommand`
  (single instance via an `App` static, cleared on `Closed`).
- **Ribbon:** its own button ("Overview (Web)") on the **Developer panel** via `panel.AddItem`,
  keeping the experiment out of the production panels.

### B4. What to evaluate during the side-by-side test

Open both overview windows in Revit and compare: startup time and first-paint, theme switching,
scroll/resize feel, text crispness, memory footprint (WebView2 spawns a browser process per
window), and overall fidelity. This is the evidence for the "stay WPF + twin" vs. "migrate to
WebView2" decision.

---

## Phases

| Phase | Contents | Needs Windows? |
|---|---|---|
| 1 | A1 palette export + A2 twin scaffold + Tools Overview twin page | No |
| 2 | A3 snapshot exporter + `--capture` CLI in LemoinePreview | Build/run: yes |
| 3 | A4 measure + compare, first calibration round on Tools Overview | Capture: yes; compare: no |
| 4 | B WebView2 overview copy (independent of phases 2–3) | Build/test: yes |

## Files added / changed (summary)

- **New:** `devtools/design-twin/` (export_palette.py, palette.json/css, twin.css, index.html,
  pages/tools-overview.html, measure.mjs, compare.py, README.md, snapshots/),
  `LemoinePreview/Parity/SnapshotExporter.cs`, `LemoinePreview/Parity/CaptureRunner.cs`,
  `Source/Framework/Web/ToolsOverviewWebWindow.cs`, `Source/Commands/OpenOverviewWebCommand.cs`,
  `Source/Resources/Web/overview/` (index.html, overview.css, overview.js).
- **Changed:** `LemoinePreview/App.xaml.cs` (CLI capture mode), `LemoineTools.csproj`
  (WebView2 PackageReference + Web asset deploy copy), `Source/App.cs` (Developer-panel button +
  web-overview static), `Strings/en/` (any new UI strings for the web window chrome/errors).

## Risks & notes

- WebView2 adds a runtime dependency and a browser process per open window — that cost is part
  of what the pilot is measuring.
- Capture determinism depends on zeroed animations and a fixed 96-DPI render — both handled in
  capture mode, not globally.
- `Source/Resources/Web/**` must be excluded from anything that treats it as XAML/resource
  compile input (plain content copy only).
- Sheet-metal check per CLAUDE.md: all new user-facing strings go through `AppStrings`;
  the catalog content itself is already externalized.

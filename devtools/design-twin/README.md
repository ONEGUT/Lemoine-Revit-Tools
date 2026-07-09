# Design-twin parity harness

Measures the real WPF UI and an HTML "design twin" of it to sub-pixel
precision and reports every geometric deviation, so the twin can be edited in
Claude design and driven to exact agreement with the shipped plugin. See
`plan-design-twin-parity-and-webview2-overview.md` (repo root) for the full
design and rationale.

Pilot surface: **Tools Overview**, "Setup" category
(`Source/Framework/ToolsOverviewWindow.xaml[.cs]`).

## Pieces

| File | Runs where | What it does |
|---|---|---|
| `export_palette.py` | anywhere (Python) | Parses `Source/Framework/ThemePalette.cs` → `palette.json` + `palette.css` (+ a copy into `Source/Resources/Web/overview/` for the WebView2 pilot). Source of truth for every color/font token. |
| `twin.css` | browser | Shared component styles for the twin, `@import`s `palette.css`. |
| `pages/tools-overview.html` | browser | The twin page itself — every element tagged `data-id="..."` to match the WPF `Uid`/`Name` tags on the real window. |
| `measure.py` | anywhere (Python + the pre-installed Chromium) | Renders a twin page headless, measures every `[data-id]` element's exact bounds, writes the same JSON schema the WPF exporter produces. |
| `LemoinePreview/Parity/SnapshotExporter.cs` | **Windows only** | Walks a live WPF window's visual tree and writes the same JSON schema + a PNG. |
| `LemoinePreview/Parity/CaptureRunner.cs` | **Windows only** | Drives `SnapshotExporter` across every category tab / theme / size combo. Wired into `LemoinePreview.exe --capture`. |
| `compare.py` | anywhere (Python) | Joins a WPF JSON and a twin JSON by element id, reports every delta over tolerance, can render a visual overlay. |

## Element identity — how the two sides join

Every element that matters for parity carries a stable id:

- **WPF side**: `FrameworkElement.Uid` (set explicitly on `ToolsOverviewWindow`'s
  dynamically-built tabs/cards/chips — search for `Uid = "card-"` etc. in
  `ToolsOverviewWindow.xaml.cs`) or `x:Name` for the six chrome elements
  (`_outerBorder`, `_toolbarBorder`, ...). Anything untagged falls back to a
  structural path (`Border/StackPanel[0]/TextBlock[1]`) so nothing is silently
  dropped from the report — but a structural-path id won't match anything on
  the twin side, so `compare.py` will flag it as twin-only/wpf-only until it's
  either tagged or intentionally left out of the twin.
- **Twin side**: `data-id="..."` attributes, hand-written to match the WPF `Uid`s.

If you add a new element to `ToolsOverviewWindow` and want it covered, give it
a `Uid` there and add the matching `data-id` (plus markup) on the twin page.

## The loop

**1. Design on the twin.** Open `pages/tools-overview.html` in Claude design
(or any browser) and edit `twin.css` / the page markup directly — no Windows
needed for this step.

**2. Translate to WPF.** Whoever (Claude or you) is doing the C# side edits
`ToolsOverviewWindow.xaml[.cs]` to match.

**3. Capture the real WPF window (Windows only):**

```powershell
cd LemoinePreview
dotnet build -c Debug
bin\Debug\net48\LemoinePreview.exe --capture
```

Writes `snapshots/wpf/tools-overview.<category>.<theme>.<size>.{json,png}`
for every category tab, at Dark Mono + Light Clean × Medium (the default,
fast matrix). Pass `--full` for all 8 themes × 3 sizes, or a path as the first
argument to write somewhere other than `devtools/design-twin/snapshots/wpf/`.

Commit/push the `snapshots/wpf/` folder (or hand the files over directly) so
the next step can read them from Linux/Claude.

**4. Measure the twin and compare:**

```bash
python3 devtools/design-twin/measure.py devtools/design-twin/pages/tools-overview.html \
  devtools/design-twin/snapshots/twin/tools-overview.setup.json \
  --png devtools/design-twin/snapshots/twin/tools-overview.setup.png

python3 devtools/design-twin/compare.py \
  devtools/design-twin/snapshots/wpf/tools-overview.setup.dark-mono.medium.json \
  devtools/design-twin/snapshots/twin/tools-overview.setup.json
```

Exit code is nonzero if anything is out of tolerance (default 0.5px) or if
either side has an id the other doesn't. Add `--overlay out.png --wpf-png
snapshots/wpf/....png --twin-png snapshots/twin/....png` for a blended visual
diff (requires `pip install pillow`; the numeric report works without it).

**5. Fix and repeat.** Every reported delta needs a human/Claude judgment call
about which side (WPF margin/padding, or twin CSS) is wrong and how to fix it
— `compare.py` only ever reports, it never edits. Systematic offsets (e.g. a
button's default padding) belong in `twin.css` as a token fix so future
elements using that token match on the first pass.

## Regenerating the palette

Whenever `Source/Framework/ThemePalette.cs` changes:

```bash
python3 devtools/design-twin/export_palette.py
```

Regenerates `palette.json`, `palette.css`, and
`Source/Resources/Web/overview/palette.css`. The script fails loudly (nonzero
exit, names the missing theme/member) rather than silently emitting an
incomplete palette.

## Known limitations

- **Glyph rendering is never pixel-identical.** ClearType (WPF) vs. Chromium
  antialiasing draw individual letterforms differently even at an identical
  text box and baseline. `compare.py` scores **geometry only** (position/size
  from the JSON) — use the overlay PNG for human visual review, not a strict
  pixel diff.
- **The Linux Chromium used for `measure.py` substitutes a different font for
  "Segoe UI" / "Segoe MDL2 Assets"** than Windows, so any twin element sized
  to hug its own text (auto-width) will differ in width from the WPF capture
  by font-metrics alone. Treat the WPF-measured width as ground truth and pin
  the twin element to it explicitly rather than trusting `data-id` auto-sizing
  across platforms. On a real Windows/Chrome run this limitation disappears.
- **Segoe MDL2 glyph codepoints render as tofu/blank in the Linux preview**
  (font not installed) — expected, not a bug; verify glyph rendering on
  Windows.

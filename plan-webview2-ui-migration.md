# WebView2 UI Migration — Rules & Cold-Start Plan

Everything needed to migrate the Lemoine Tools UI from WPF to WebView2-hosted
HTML/CSS/JS, written so a fresh session (human or Claude) can execute it with no
prior conversation context. Read alongside `CLAUDE.md` and `LEMOINE_UI.md`.

---

## 0. Context — what exists and what is proven

The **WebView2 Test harness** (Developer ribbon panel → "WebView2 Test") was built
and run inside Revit on Windows. Verified facts — do not re-litigate these:

- **WebView2 renders inside a Lemoine tool window** (`StepFlowWindow`, own STA
  thread) and the HTML recreations of `InlineStepper` and `MultiSelectTabs` look
  just like the WPF originals.
- **The JS → C# bridge works** (`window.chrome.webview.postMessage` →
  `CoreWebView2.WebMessageReceived`).
- The four historical "blank control" failure modes are real and are neutralized
  by the wiring now in the repo (see Rules R1–R4).

Existing assets to build on (all on branch `claude/webview2-testing-menu-9i6jz2`,
merged or pending):

| Asset | Purpose |
|---|---|
| `Source/Tools/Debuggers/WebView2TestTool.cs` | Harness: preflight probes, phased init, bridge test. Becomes the **component gallery** in Phase 1. |
| `Source/Tools/Debuggers/WebView2TestPages.cs` | Token-templated HTML pages (smoke, stepper, tabs). The template/token pattern seeds the asset pipeline. |
| `Source/Commands/Debuggers/WebView2TestCommand.cs` | Command + STA window-open pattern for a WebView2-bearing tool. |
| `LemoineTools.csproj` | Pinned `Microsoft.Web.WebView2` (`$(WebView2Version)`) + `CopyWebView2Loader` target. |

**Still to verify on Windows** (carry as open items, do not assume):
- The WebView2 version Revit itself ships (run the harness "Dump loaded WebView2
  assemblies" probe; adjust the `WebView2Version` pin if the CLR binds Revit's copy).
- Behavior across all four Revit year configs (2024 net48 verified path; 2025/2026
  net8 need one smoke run each).
- Keyboard focus hand-off between WPF chrome and web content (only matters for the
  hybrid layout — see the architecture decision below).

---

## 1. Target architecture

**One full-page WebView2 per tool window.** The window (WPF) shrinks to: STA
thread + Win32 shell + WebView2 host + bridge + ExternalEvent plumbing. ALL
visible UI — toolbar, step accordion, inputs, log, footer — is one HTML app.

Why not a hybrid (WPF accordion hosting HTML step content):

1. WebView2 is an `HwndHost` — **WPF cannot render over it** (airspace). Every
   WPF Popup, adorner, drag ghost, and dropdown near web content breaks. Full-page
   HTML makes overlays an HTML problem, where they are trivial.
2. One control per window = one init, one bridge, one lifetime. A hybrid needs a
   WebView2 (or reparenting dance) per step.
3. The entire "Revit Crash Constraints" Popup/dropdown minefield in `CLAUDE.md`
   simply does not exist inside HTML. The more UI lives in the page, the less
   crash surface remains.

The alternative (hybrid per-step hosting) is documented here only so nobody
re-derives it: rejected for airspace + N-controls-per-window cost.

**What does NOT change:** ViewModels' domain state, ExternalEvent handlers, all
Revit API code, settings singletons, `DiagnosticsLog`, `AppStrings` JSON files,
run lifecycle (`RunState`, `RevitFailureCapture`, `RunLogSink`). The migration
replaces the *view* layer only; `IStepFlowTool`'s contract maps onto a bridge
protocol (R17) instead of returning `FrameworkElement`s.

---

## 2. The Rules

### Hosting & initialization

- **R1 — Explicit user-data folder, always, first.** Every environment is created
  with `CoreWebView2Environment.CreateAsync(null, %LocalAppData%\LemoineTools\WebView2, null)`
  (create the directory first). Never let WebView2 default next to `Revit.exe` —
  that is the #1 silent-blank-control cause.
- **R2 — `WebView2Loader.dll` ships beside `LemoineTools.dll`.** The
  `CopyWebView2Loader` csproj target guarantees it per year config; never remove
  it. A deploy without the loader fails init silently.
- **R3 — Nothing touches the control before `await EnsureCoreWebView2Async(env)`
  completes.** All navigation, settings, and `CoreWebView2.*` access go after the
  await. Phase-order in code: create env → create control → await ensure → wire
  CoreWebView2 events → navigate.
- **R4 — Pin the SDK version in one place.** `$(WebView2Version)` in
  `LemoineTools.csproj` drives both the PackageReference and the loader copy.
  Revit loads its own WebView2 assemblies; after the harness reports what Revit
  ships, keep the pin close to it and record the finding here.
- **R5 — One process-wide environment, one control per window.** Share a single
  lazily-created `CoreWebView2Environment` (all controls then share one browser
  process tree). Each tool window owns exactly one `WebView2` control.
- **R6 — Diagnostics events are always wired, before first navigation:**
  `CoreWebView2InitializationCompleted`, `NavigationCompleted`, `ProcessFailed`,
  and (dev) `WebMessageReceived` logging — each routed to `DiagnosticsLog` and,
  during a run, the run log. A WebView2 failure must never be quieter than a WPF
  exception. `ProcessFailed` must attempt one automatic re-init + re-navigate and
  log it; a dead renderer must not strand a tool window.
- **R7 — Production `CoreWebView2Settings`:** default context menus OFF, dev tools
  ON only in `DEBUG` builds, browser accelerator keys OFF, zoom control OFF,
  status bar OFF. The page must feel like a native window, not a browser.
- **R8 — `DefaultBackgroundColor` = active theme `Bg`** before init, so no white
  flash on open or navigate.

### Threading & Revit access

- **R9 — The STA window pattern is unchanged.** One dedicated STA thread +
  `Dispatcher.Run()` per tool window (copy `WebView2TestCommand`). WebView2 events
  arrive on that dispatcher; no extra marshalling for UI work.
- **R10 — JS never reaches Revit.** The only path to the Revit API remains:
  bridge message → C# ViewModel (window thread) → set handler payload →
  `ExternalEvent.Raise()`. Handler callbacks marshal back with `BeginInvoke` and
  are forwarded to the page (R19). No exceptions to this layering.
- **R11 — Window lifetime = control lifetime.** `IToolCleanup.OnWindowClosed`
  disposes the WebView2 control (try/catch → `DiagnosticsLog.Swallowed`) and nulls
  page-side callbacks, same memory discipline as CLAUDE.md's handler rules. The
  shared environment is never disposed (process-lifetime).

### Assets & theming

- **R12 — Colors and sizes only via CSS custom properties.** Pages style
  exclusively through `var(--lemoine-*)` variables (`--lemoine-bg`,
  `--lemoine-text`, `--lemoine-accent`, … mirroring every `ThemePalette` token,
  plus `--lemoine-scale` / font-size tokens mirroring `AppSettings`' `LemoineFS_*`
  and `LemoineH_*`). C# injects initial values into the template and **updates
  them live** on `AppSettings.ThemeChanged` / `UiSizeChanged` via
  `ExecuteScriptAsync` setting `documentElement.style` — no reload, matching
  WPF's DynamicResource behavior. (The harness's whole-hex `{{TOKEN}}` Replace
  is superseded by this; tokens now fill the `:root` variable block only.)
- **R13 — One template on disk = one page, ASCII-only.** HTML templates live as
  `.html` files under `Source/Web/` (loose-copied to the deploy dir like
  `Strings\`), written in plain ASCII with HTML entities (`&minus;`, `&#10003;`)
  for any non-ASCII glyph so the Edit tool can always match them. No `\uXXXX`, no
  literal PUA characters.
- **R14 — Pages must run in a plain browser.** Guard every bridge call with
  `if (window.chrome && window.chrome.webview)` and keep an in-page echo log (as
  the harness pages do). This is what makes R38's headless-Chromium verification
  loop possible — the page is testable without Revit, without Windows.
- **R15 — User-facing text is injected, not hardcoded.** Page templates carry
  string *keys*; C# resolves them through `AppStrings.T(...)` and hands the page a
  `{key: text}` map at init (part of the R17 init message). The existing per-tool
  `Strings/<culture>/*.json` files remain the single source of truth. Debug-only
  pages (the harness/gallery) stay hardcoded, per the existing exception.
- **R16 — Serve via `SetVirtualHostNameToFolderMapping`** (e.g. maps
  `https://lemoine.app/` → the deploy dir's `Web\` folder) once pages need
  shared `.css`/`.js` files or images; `NavigateToString` stays acceptable only
  for small single-file pages (< 2 MB, no shared assets). The component library
  (R22) forces the folder mapping — adopt it in Phase 0 and don't look back.

### Bridge protocol

- **R17 — One JSON message shape, both directions:**
  `{ "type": string, "id": string?, "payload": object }`.
  JS → C# via `chrome.webview.postMessage(JSON.stringify(msg))`; C# → JS via
  `CoreWebView2.PostWebMessageAsString(json)` with a page-side
  `chrome.webview.addEventListener('message', …)` dispatcher. Core message types
  (extend, don't fork): `init` (C#→JS: strings map, theme vars, tool model),
  `state` (JS→C#: a step's input values changed), `action` (JS→C#: button pressed
  — run, cancel, pick-in-Revit, browse), `log` (C#→JS: run-log line + status),
  `progress` (C#→JS), `complete` (C#→JS), `themeChanged` (C#→JS).
- **R18 — C#→JS only after `NavigationCompleted(IsSuccess)`.** Queue outbound
  messages until the page signals ready (its own `init-ack`), then flush. A
  message posted into a not-yet-loaded page vanishes silently — never fire and
  forget before the ack.
- **R19 — Run-log fan-out stays intact.** `pushLog` / `onProgress` / `onComplete`
  callbacks forward to the page as `log`/`progress`/`complete` messages;
  `RunLogSink`, `RevitFailureCapture`, and `DiagnosticsLog.EntryLogged` keep
  working unchanged because the ViewModel layer is untouched.
- **R20 — Unknown or malformed messages are logged, never dropped.**
  `DiagnosticsLog.Warn("Bridge: unknown message", raw)` on the C# side; a visible
  dev-console error on the JS side. A typo'd message type must surface, not no-op.
- **R21 — Validation lives in C#.** JS collects and echoes input state; the
  ViewModel remains the authority on `IsValid` (it still drives the Run gate).
  Duplicate cosmetic validation in JS is allowed; authoritative validation in JS
  is not.

### Component parity & migration order

- **R22 — Build a shared JS component library, not per-page copies.**
  `Source/Web/lib/lemoine.js` + `lemoine.css`: one implementation each of
  stepper, multi-select tabs, single-select, toggle switch, text field, tag-chip
  input, search autocomplete, file/folder row (button → C# dialog via bridge),
  section card, warn banner, review summary, step accordion, toolbar, footer,
  progress bar, run log. The harness's stepper/tabs JS is the seed — promote it
  into the library rather than duplicating.
- **R23 — Behavioral contracts carry over verbatim.** The WPF contracts in
  CLAUDE.md are the spec for their HTML twins: `SetGroups` fires one
  `SelectionChanged` at the end of setup; `SingleSelect` set before data; the
  `Hierarchy` caret/indeterminate rules; `DisabledItems` rendered dimmed and
  excluded from "All"; naming-slot tokens stay logic tokens. Port the contract,
  not just the look.
- **R24 — Visual parity is screenshot-checked.** Every component lands in the
  gallery page with a headless-Chromium screenshot placed next to a screenshot of
  the WPF original before its first consumer migrates. "Looks just like the
  original" is the acceptance bar the stepper/tabs pages already met.
- **R25 — A WPF control is deleted only at zero consumers.** Both stacks coexist
  per-tool during migration; the per-tool cutover is atomic (a tool is either all
  WPF or all HTML — no half-hybrid tool windows, per the architecture decision).
- **R26 — File dialogs, Revit pickers, and OS interactions stay native.** HTML
  buttons send `action` messages; C# runs `FolderBrowserDialog`, `PickObject`
  (via the existing PickerViewGuard patterns), etc., and posts results back.
  Never attempt file access from JS.

### Window & airspace

- **R27 — Never place WPF visuals over the WebView2 rectangle.** No WPF Popup,
  tooltip, adorner, or drag ghost overlapping web content. Anything that must
  float above page content is implemented in HTML inside the page.
- **R28 — The window stays unowned** (per the existing CLAUDE.md decision — no
  HWND owner, no `ComponentManager.ApplicationWindow`). The WPF shell keeps only:
  title-bar text sync (page can request via bridge), min size, and close
  handling.
- **R29 — In-page scrolling only.** The page owns all scrolling (no WPF
  ScrollViewer around the control). The Popup/bubbling scroll-wheel rules in
  CLAUDE.md are WPF-only legacy and do not apply inside the page.

### Build, deploy & multi-year

- **R30 — Web assets deploy loose, like `Strings\`.** `Source/Web/**` →
  `<DeployDir>\Web\` via `None Update` + `CopyToOutputDirectory=PreserveNewest`,
  for every year Configuration. Loose files mean a designer (or Claude) can edit
  a page and re-test with only a Revit restart — no rebuild.
- **R31 — All four year configs get the loader + assets.** Any csproj change here
  must be verified against `Release2024` … `Release2027` (the `BuildAllYears`
  flow). net48 vs net8 both resolve the same WebView2 package.
- **R32 — Linux cannot compile this repo.** C# verification happens on Windows
  only; page verification happens anywhere via headless Chromium (R38). Plan
  work so page iteration (cheap, local) is separated from C# iteration
  (Windows-gated).

### Process

- **R33 — One tool (or one library milestone) per branch**, kebab-case name,
  plan-first per CLAUDE.md's branch workflow.
- **R34 — The harness/gallery is updated before the thing it validates.** New
  component → gallery entry + screenshot first; new bridge message type → harness
  echo test first.
- **R35 — Silent-failure scan applies to JS too.** Empty `catch {}` in page
  script, unhandled promise rejections, and bridge messages with no handler are
  findings; wire `window.onerror` / `onunhandledrejection` in the shared lib to
  post an `error` message to C# → `DiagnosticsLog`.
- **R36 — Never regress the four failure modes.** Any new host code path must
  reuse the shared host/bootstrap (Phase 0's `WebHost`), which encodes R1–R8.
  No tool ever calls `CoreWebView2Environment.CreateAsync` itself.
- **R37 — Findings land in this file.** Each phase's Windows run appends
  verified facts / gotchas here (as CLAUDE.md does for Revit API constraints),
  so the next cold start inherits them.
- **R38 — Iterate on screenshots, not compiles.** For any page work: edit HTML →
  headless-Chromium screenshot (the recipe in the `/revit-navisworks-ui` skill,
  Step 7 — including the bottom-anchored-content culling workaround) → compare →
  only then touch C#. WebView2 *is* Chromium; what the screenshot shows is what
  Revit shows.

---

## 3. Phased plan

Each phase ends with a Windows verification run and a findings append to §5.

**Phase 0 — Shared host & pipeline** *(one branch, e.g. `webview2-host-core`)*
Extract from the harness into `Source/Framework/Web/`:
`WebHost` (env singleton + control factory encoding R1–R8, R11),
`WebBridge` (R17–R20 message router with pre-ack queueing),
`WebAssets` (virtual-host mapping R16, template loading, CSS-variable injection
R12, strings injection R15), csproj loose-copy of `Source/Web/**` (R30).
Repoint the harness to consume all of it (proof it works).
*Exit: harness runs on Windows through the new host layer; theme + UI-size
switch live-updates an open harness page.*

**Phase 1 — Component library & gallery** *(1–2 branches)*
Build `Source/Web/lib/` (R22) starting from the proven stepper/tabs code; convert
the harness's step 3 into a full gallery page with every library component in
every state (disabled, indeterminate, overflow, empty). Screenshot pairs vs WPF
originals (R24). Priority order: the step-flow chrome set (accordion, toolbar,
footer, progress, run log) first — every tool needs it — then inputs by usage:
SingleSelect, ToggleSwitches, TextField, InlineStepper, MultiSelectTabs,
SectionCard, WarnBanner, ReviewSummary, FileBrowser/FolderBrowser rows,
TagChipInput, SearchAutocomplete, NumberRange, DateField, InlineEdit, MatrixInput,
BrowserTreePicker (hardest — tree + right-click contract), color pickers/swatches,
Legend components, DragGhost/ListReorder equivalents (HTML drag-and-drop).
*Exit: gallery renders all components; contracts of R23 demonstrably ported.*

**Phase 2 — StepFlow shell in HTML + pilot tool** *(one branch)*
Build `stepflow.html`: the full window chrome (accordion steps, pips, summaries,
validation gating, Run/Reset/Cancel footer, output log) driven entirely by
`init`/`state`/`log`/`progress`/`complete` bridge messages. C# side:
`WebStepFlowWindow` — same public surface as `StepFlowWindow` (takes an
`IStepFlowTool`-shaped ViewModel) but the tool describes steps/inputs as **data**
(a serializable step/input spec) instead of `FrameworkElement`s.
Migrate ONE simple pilot: **Print View** or **Duplicate Views** (small step
count, no pickers-in-lists, real ExternalEvent run).
*Exit: pilot tool fully usable in Revit through HTML; run lifecycle (cancel,
progress cadence, failure capture) byte-for-byte equivalent in the log.*

**Phase 3 — Migration waves** *(one branch per tool)*
Wave order = ascending UI complexity, so the library hardens before the monsters:
1. Print View, Duplicate Views, Upgrade Links, Push/Align Coordinates
2. Bulk Export, Bulk Rename, Copy Datums/Linear/Elements, Scope Boxes
3. Ceiling Grids/Heatmap, Explode Views, Place Dependent/Align Sheet Views
4. Auto Filters (+ FiltersSettingsWindow), Clash Definitions window
5. Legend Creator (drag/drop lane grid), Refine Dimensions, Clash Finder
Per tool: branch → port `GetStepContent` panels to step specs → Windows run →
delete nothing yet (R25).

**Phase 4 — Non-StepFlow windows**
GlobalSettingsWindow, ToolsOverviewWindow (demos become actual HTML — they're
already fake UIs), ColorPickerWindow, LinkAuditWindow. Same host, bespoke pages.

**Phase 5 — Decommission**
Remove WPF controls at zero consumers (R25), collapse `StepFlowWindow` → thin
shell or delete, prune `ControlStyles`/`MotionEffects` dead paths, update
`LEMOINE_UI.md` to describe the HTML architecture, fold the durable rules of this
file into `CLAUDE.md`, retire the harness or keep it as the gallery's home.
Decide `LemoinePreview`'s fate: most likely superseded entirely by opening the
gallery page in a plain browser (it exists to preview UI without Revit — the web
stack gives that for free).

---

## 4. Inventory (cold-start reference)

**Windows:** StepFlowWindow (hosts every step-flow tool), GlobalSettingsWindow,
ToolsOverviewWindow, FiltersSettingsWindow, ClashDefinitionsWindow,
LinkAuditWindow, ColorPickerWindow.

**Controls to twin** (`Source/Framework/Controls/`):
Input — BrowserTreePicker, DateField, InlineEdit, InlineStepper, MatrixInput,
MultiSelectTabs, NumberRange, SearchAutocomplete, SingleSelect, TagChipInput,
TextField, ToggleSwitches, TokenInput/NamingSlots.
Layout — FileBrowser, FolderBrowser, ReviewSummary, SectionCard, TitleBar,
ToolSection, WarnBanner.
Color — ColorPickerPanel/Window, SwatchGlyph, SwatchPicker, EyeGlyph.
Legend — group/card/preview components (see folder).
Mechanisms — DragGhost, ListReorder, MotionEffects hovers, ControlStyles
scrollbars (all become CSS/JS in the shared lib).

---

## 5. Verified findings log (append per Windows run)

- **2026-07 (harness v1, Revit 2024):** WebView2 initializes and renders inside
  a StepFlowWindow on its own STA thread; explicit user-data folder +
  loader-copy target sufficient; JS→C# bridge delivers; HTML stepper/tabs
  visually match WPF originals.
- **2026-07 (Phase 0 landed — pending Windows verify):** shared host layer built
  under `Source/Framework/Web/` — `WebHost` (env singleton + control factory,
  R1/R3/R5/R7/R8/R36), `WebBridge` (JSON `{type,payload}` router with ready-queue
  + unknown-message logging, R17-R21), `WebAssets` (virtual-host serving of the
  deploy `Web\` folder + live `--l-*` CSS-variable injection, R12/R14/R16),
  `WebJson` (dependency-free serializer, complements MiniJson). Debug pages moved
  to loose files at `Source/Web/debug/*.html` + shared `Source/Web/lemoine-bridge.js`;
  csproj copies `Source/Web/**` → `<DeployDir>\Web\` per year (R30/R31). Harness
  repointed onto all of it (env via WebHost, pages served over `lemoine.app`
  virtual host, messaging via WebBridge). Pages verified rendering STANDALONE in
  headless Chromium from disk (CSS-variable fallbacks + `../lemoine-bridge.js`
  resolves + bridge degrades gracefully) — R14/R38 confirmed off-Windows.
  *Still to verify on Windows:* the harness runs end-to-end through the new layer;
  virtual-host navigation + live theme-variable push work inside Revit.
- **2026-07 (Phase 0, Revit 2026 Windows run):** `WebHost` created the shared
  environment cleanly — **Evergreen runtime 150.0.4078.65** at
  `%LocalAppData%\LemoineTools\WebView2` (R1 path confirmed writable in Revit).
  The SDK NuGet pin (1.0.2210.55) drives a runtime two major versions newer with
  no issue, so the pin does not need to chase the runtime. **Deploy bug found +
  fixed:** `None Update="Source\Web\**" TargetPath="Web\%(RecursiveDir)..."` did
  NOT re-root the copy (`%(RecursiveDir)` is empty on Update of default-glob
  items), so `<DeployDir>\Web\` never appeared and every `https://lemoine.app/...`
  navigation aborted (`WebErrorStatus=Unknown`/`ConnectionAborted`) against the
  missing folder. Replaced with an explicit `<Copy>` target (items from its own
  `**` wildcard → `%(RecursiveDir)` populated), mirroring `CopyWebView2Loader`.
  Loader copy + preflight probes worked first try. *Re-verify after the deploy
  fix:* virtual-host navigation renders; live theme-variable push; bridge messages.
- *(append here: assembly-dump probe output — the SDK assembly version Revit's own
  WebView2 loads; 2024/2025 smoke results; focus/keyboard findings)*

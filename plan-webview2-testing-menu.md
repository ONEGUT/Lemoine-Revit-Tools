# Plan — WebView2 Testing Menu (Debug Harness)

## Goal

Prove WebView2 can host our UI inside Revit — visibly, with no silent failures — and
exercise HTML recreations of two of our most common user inputs with a working
C# ↔ JavaScript bridge. Last attempt built fine but rendered blank; this harness is
designed so every known silent-failure mode is surfaced as a log line instead.

This is a debug harness per CLAUDE.md "Crashes & Large Ambiguous Issues": an
`IStepFlowTool` opened in `StepFlowWindow`, living in `Source/Tools/Debuggers/`,
reachable from the Developer ribbon panel (currently a placeholder comment in
`App.cs` ~line 618). Every suspect is lazily constructed behind a button — opening
the window or navigating steps must never touch WebView2.

## Known failure modes this harness bakes in (from the previous attempt)

1. **User data folder** — WebView2 defaults its cache folder next to the host exe
   (`Revit.exe` in Program Files — not writable → silent blank control). The
   environment is always created explicitly first:
   `CoreWebView2Environment.CreateAsync(userDataFolder: %LocalAppData%\LemoineTools\WebView2)`.
2. **Missing `WebView2Loader.dll`** — the NuGet native loader may not be copied
   next to the plugin DLL. The harness has a preflight button that scans the deploy
   folder and reports presence/absence, and the csproj gets an explicit copy step.
3. **Navigating before init completes** — all navigation happens strictly after
   `await EnsureCoreWebView2Async(env)`; the harness logs each phase separately.
4. **Version conflict with Revit's own WebView2** — a preflight button dumps every
   `Microsoft.Web.WebView2.*` assembly already loaded in the AppDomain (name,
   version, location) so a CLR bind to Revit's copy is visible, and the NuGet
   version is pinned (see csproj section).

Every init call is wrapped in try/catch that routes to **both** the step-flow run
log and `DiagnosticsLog.Error` — the silent failure becomes a visible, named one.

## Harness layout (3 steps, one suspect per button)

**Step 1 — Preflight** (no WebView2 objects created)
- "Check WebView2 runtime" → `CoreWebView2Environment.GetAvailableBrowserVersionString()`
  (Evergreen runtime installed? version?).
- "Check WebView2Loader.dll" → scan the folder containing `LemoineTools.dll` and log
  which WebView2 files are present.
- "Dump loaded WebView2 assemblies" → AppDomain scan (failure mode #4).

**Step 2 — Environment & control init** (each phase its own button)
- "Create environment" → explicit user-data-folder `CreateAsync`, result logged.
- "Create control + EnsureCoreWebView2Async" → adds the `WebView2` WPF control to a
  host border on this step, awaits init against the Step-2 environment, subscribes
  `CoreWebView2InitializationCompleted`, `ProcessFailed`, `NavigationStarting`,
  `NavigationCompleted` — every event logged.
- "Navigate smoke test" → `NavigateToString` of a trivial themed page ("If you can
  read this, WebView2 renders"). Only enabled after init completed.

**Step 3 — Common inputs over the bridge**
- "Load Stepper" → HTML/JS recreation of `InlineStepper` (typeable centre, ± buttons,
  min/max clamp, decimals).
- "Load Multi-Select Tabs" → HTML/JS recreation of `MultiSelectTabs` (group tabs,
  checkbox list, per-group "All" row).
- Both post value changes via `window.chrome.webview.postMessage`; C# side handles
  `WebMessageReceived` and echoes each change into the run log — proving two-way
  comms, not just rendering. Pages are styled from the live `ThemePalette` (colors
  injected as CSS variables), so they read as Lemoine UI.

Step navigation itself never constructs anything (`StepFlowWindow` builds step
content eagerly — the content is just buttons + an empty host area). Button click
handlers are the one permitted `async void` site, each fully guarded.

## Files

| File | Change |
|---|---|
| `Source/Tools/Debuggers/WebView2TestTool.cs` | New — the `IStepFlowTool` harness (steps, buttons, init phases, bridge handler) |
| `Source/Tools/Debuggers/WebView2TestPages.cs` | New — HTML for the smoke page + the two input recreations as C# string constants (theme colors templated in) |
| `Source/Commands/Debuggers/WebView2TestCommand.cs` | New — `IExternalCommand` that opens the harness in `StepFlowWindow` |
| `Source/App.cs` | Create the Developer ribbon panel (the reserved slot at ~line 618) with one "WebView2 Test" button |
| `LemoineTools.csproj` | Add `PackageReference` to `Microsoft.Web.WebView2` (first NuGet package in the project) + guarantee `WebView2Loader.dll` lands in each year's `DeployDir` |

No strings JSON — this is a developer-only harness (debug output stays hardcoded
per CLAUDE.md), and it gets removed or repointed once findings are captured.

## csproj notes (the risky part)

- Pin `Microsoft.Web.WebView2` — proposed **1.0.2210.55** (an older stable close to
  what Revit 2024/2025-era builds ship; exact Revit-shipped version gets confirmed by
  the Step-1 assembly dump on a real machine, and we adjust the pin after the first run).
- The package's managed assemblies (`Core`, `Wpf`) copy to output by default; the
  native `WebView2Loader.dll` sits in `runtimes\win-x64\native\` and does **not**
  reliably copy for a class-library plugin on net48 — add an explicit
  `None CopyToOutputDirectory` item pointing at the package's x64 loader so it lands
  beside `LemoineTools.dll` in `C:\ProgramData\Autodesk\Revit\Addins\<year>\`.
- Must survive all four year Configurations (net48 for 2024, net8.0-windows for
  2025–2027). WebView2 supports both TFMs from one package version.
- Cannot be compile-verified on Linux (Windows-only build) — the user builds and
  runs the harness on Windows; the harness itself is the test.

## Threading / crash-safety notes

- Each tool window runs on its own STA thread; `StepFlowWindow`'s
  `Dispatcher.UnhandledException` net covers stray throws. WebView2's WPF control
  is expected to work on a non-main STA dispatcher — if it doesn't, that is exactly
  the finding this harness exists to produce, isolated to one named button.
- The tool implements `IToolCleanup`-style teardown: dispose the WebView2 control
  and null the environment on window close so the browser process exits with the
  window.

## Process

1. Per the WPF-UI rule, before implementation I'll render the two HTML input
   recreations with headless Chromium and deliver screenshots for approval —
   conveniently, that HTML **is** the deliverable asset here (WebView2 is Chromium),
   so the mockup pass doubles as building the real pages.
2. Implement on branch `claude/webview2-testing-menu-9i6jz2` (based on `main`),
   invoking `/revit-navisworks-ui` first.
3. Silent-failure scan of the diff, then commit and push.
4. User builds on Windows, runs the harness, and the Step-1/2 logs tell us which
   (if any) of the four failure modes bit last time.

# Lemoine Revit Tools — Claude Guidelines

## Project Overview

This is a Revit plugin built with C# and WPF targeting .NET Framework 4.8. The primary project is `LemoineTools`. See `LEMOINE_UI.md` for the full UI architecture reference.

---

## Crashes & Large Ambiguous Issues — Build a Debugger First

When the user reports a **crash** (Revit closing/hanging) or any **large, ambiguous problem** that can't be pinned to a specific line by reading code, the FIRST move is to **build a dedicated debug harness — not to theorize from inspection**. Code-reading has repeatedly failed to find crash causes; a harness that reproduces and isolates the fault is the reliable path.

The harness is an `ILemoineTool` opened in `StepFlowWindow` (model: `MotionTestViewModel` / `DebugToolCommand`). For crashes specifically:

- **Lazily construct each suspect** behind a button, so merely opening the harness or navigating a step does NOT trigger the crash. `StepFlowWindow` builds every step's content eagerly at construction, so a crashing construct must be deferred to a button `Click` to be isolatable.
- Give each step/button ONE suspect (a single control, or the same control at scale — e.g. "build 60 swatches", "MultiSelectTabs with 8×40 items"). The button press that crashes Revit names the culprit.
- Hard crashes (no entry in `%AppData%\LemoineTools\diagnostics.log`) are native/WPF/message-loop or stack-overflow faults that `try/catch` cannot catch — only a probe harness isolates them. A managed exception WILL appear in the log via `LemoineLog`.
- Keep the harness in `Source/Tools/Debuggers/` and reachable from the reserved Developer-panel button; remove or repoint it once the issue is found.

Only after the harness pinpoints the construct should the fix be written.

---

## Branch Workflow — Read Before Any Code Changes

### 1. Always Plan First

Before creating, checking out, or pushing to any branch, Claude must:

1. Write a plan `.md` file to the repo root (e.g., `plan-<description>.md`) covering what files will be changed, what will be added, and why.
2. Summarise the plan in a brief chat message.
3. Ask the user which branch to branch from.
4. **Wait for explicit approval from the user** before touching any branch or writing any code.

Approval means the user has responded with clear confirmation (e.g. "looks good", "go ahead", "yes"). Ambiguous responses are not approval — ask for clarification.

### 2. Branch Naming Convention

Branch names must be a short, lowercase kebab-case description of the change — nothing more.

Examples:
- `color-picker-swatches`
- `settings-tab-layout`
- `update-claude-md`

Rules:
- Lowercase letters, numbers, and hyphens only.
- 3–5 words max.
- Never push directly to `main` or `master`.

### 3. Branch Lifecycle

```
Plan (.md file + chat summary) → Ask which branch to base from → User Approval → Create Branch → Implement → Commit → Push → PR (if requested)
```

- One logical change per branch. Do not bundle unrelated fixes.
- Do not create a pull request unless the user explicitly asks for one.

### 4. Commit Messages

Use the imperative mood, present tense. One subject line, no trailing period.

```
Add recent-swatches row to color picker
Fix settings tab not rendering on first open
Refactor ILemoineTool to support async execute
```

---

## Error Handling & Silent Failure Audit

### Standards for all C# code

- Never swallow exceptions silently — empty `catch` blocks and catch-and-log-only blocks that discard the error are forbidden unless the risk is explicitly acknowledged and the user has been told.
- `LemoineLog` is the central diagnostic sink (logs to `%AppData%\LemoineTools\diagnostics.log` plus an in-memory ring). Route every deliberately-swallowed exception through `LemoineLog.Swallowed(context, ex)` or `LemoineLog.Error(context, ex)` with a human-readable context string — never an empty `catch {}` or a `Debug.WriteLine`. It is Revit-free, so every layer can call it.
- Always `await` async operations or attach a `.ContinueWith` / `.GetAwaiter().GetResult()` handler. Fire-and-forget `Task` calls that can fail silently are not allowed.
- Check return values where failure is meaningful (e.g. Revit API calls that return `null` on failure, `bool` results indicating success).
- Validate inputs at system boundaries: user input, external APIs, Revit API calls, file I/O. Do not validate internal calls that are already guaranteed by the framework or calling code.

### Post-change silent failure scan

After **every** set of code changes, before reporting the task complete, Claude must:

1. Scan the diff for patterns that hide failures from the user at runtime:
   - Empty or catch-and-discard `catch` blocks
   - Unawaited `Task` / `async void` methods (outside event handlers)
   - Unchecked `null` returns at Revit API or external boundaries
   - Return values that signal failure being ignored
2. Produce a numbered list of findings — file path, approximate line, pattern type, and a one-sentence risk description.
3. Present the list to the user and ask for each finding: **warn**, **rethrow**, **log**, or **leave as-is**.
4. Apply the chosen handling before committing.

If no silent failures are found, state "No silent failures detected" explicitly so the user knows the scan ran.

---

## Communication Style

- Short replies from the user ("OK", "do it", "looks great", "that fixed it", "add it") are final answers. Acknowledge briefly and move on — do not ask follow-up questions.
- Keep post-task summaries to 1–2 sentences. Report results, not process.
- Do not narrate every step. State what changed and what's next.

---

## Decision Protocol

**UX / design decisions** (layout, interaction model, naming, workflow): Before writing any code, present 2–4 concrete options with a one-sentence tradeoff each and a clear recommendation. Wait for the user to pick one.

**Bug fixes and unambiguous feature requests**: Implement directly — no options, no pre-questions.

**Ambiguous requests**: Ask at most one clarifying question, then proceed once answered.

---

## Multi-Item Requests

When the user lists multiple changes in a single message, address all of them in one implementation pass. Do not stop mid-list to ask questions or seek approval for individual items.

---

## Build Errors

When the user pastes compiler errors directly into chat, fix them immediately on the current branch. Do not create a new plan file or branch for a build-error fix.

---

## Merge Signal

"Merge with main" or "merge this" means:

1. **Run `/pre-merge-review` first** — scan the branch commits and PR comments
   for new errors, preferences, or Revit constraints not yet in CLAUDE.md.
   Propose any additions, wait for a quick confirmation, apply them.
2. **Then create the PR and merge** via GitHub MCP. No further confirmation
   needed for the merge itself — the user already gave it when they said
   "merge with main".

---

## UX Philosophy

Before implementing any workflow, check whether it is practical:

- Never require a user to open a picker inside a picker for the same data type.
- UI state must always be unambiguous — if selecting vs. editing look identical, flag it before building.
- Prefer explicit single-action patterns (a dedicated save button, a confirm step) over implicit double-purpose interactions (one click that both selects and edits).
- If a workflow feels impractical, say so and propose an alternative before building it.
- When adding secondary actions (copy, delete) to sidebar item rows, note that this may clutter the row and ask the user whether they'd prefer those actions consolidated into the primary edit popup instead.
- Settings windows auto-save on change (theme/size on click, tool fields via `ApplySettings`). Do not add an "Apply" button — persistence is implicit per control.
- In a drag-able row, a name/label hit box must shrink to its text (`HorizontalAlignment.Left`), not fill the row — otherwise it covers the bar and blocks grabbing the row to drag it. The leftover space stays row background and remains drag-able; long names ellipsize at the column width.
- Rounding tokens: tabs and pills use `LemoineRadius_Card` (10) to match the add-trade button; small chips/inputs stay on `SM` (3) / `MD` (4). Don't introduce ad-hoc radius literals.

---

## Research Discipline

Always read the relevant source files before recommending or writing code. Never generate implementation from memory when the actual file is available.

---

## WPF UI Tasks

For any task that involves building, modifying, or debugging a WPF window or UserControl, invoke the `/revit-navisworks-ui` skill before writing any code. This applies even for small layout fixes.

---

## Edit Tool — C# Unicode Escape Sequences

The Edit tool cannot match C# string literals that contain `\uXXXX` escape sequences (e.g. `""` glyph strings in `App.cs`). The JSON parameter parser converts `\uXXXX` to the actual Unicode character before comparing, so the search always fails. Use a Python `str.replace()` script for any edit that touches those lines.

The same failure applies to literal Private Use Area (PUA) characters already in source (e.g. Segoe MDL2 Assets glyphs stored directly as Unicode chars). Additionally, Segoe MDL2 `Text` fields can be silently empty strings `""` in source — not a corrupt escape sequence, just never written. Always verify Segoe MDL2 glyph fields with Python before assuming they render correctly. Use Python `str.replace()` for any edit that inserts or modifies a Segoe MDL2 glyph.

When writing *new* code that needs a Segoe MDL2 glyph, prefer `char.ConvertFromUtf32(0xE74D)` (e.g. `Text = char.ConvertFromUtf32(0xE74D)` for the trash icon) over embedding the literal glyph. The codepoint is plain ASCII in source, so the Edit tool handles it normally and no Python pass is needed.

---

## Known Compile Error Patterns

These mistakes have appeared in multiple sessions. Check for them before committing.

### Ambiguous type aliases — required in any file that uses both WPF and Revit API types

```csharp
using WpfGrid       = System.Windows.Controls.Grid;
using WpfTextBox    = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;
using WpfPoint      = System.Windows.Point;
using RevitColor    = Autodesk.Revit.DB.Color;
```

Add whichever aliases are needed. Never use a bare `Grid`, `Visibility`, `Color`, `Point`, or `TextBox` in a ViewModel file that also imports `Autodesk.Revit.DB`.

### Missing `using` directives that keep appearing

| Symbol | Required namespace |
|--------|-------------------|
| `Brushes` | `System.Windows.Media` |
| `Math` | `System` |
| `OfType<>` | `System.Linq` |
| `LegendCreatorTabContent` | `LemoineTools.Tools.Testing.LegendCreator` |

### Access modifiers across partial files

Methods shared between partial class files must be `internal`, not `private`. A `private` method in one partial file is invisible to another — CS0122.

### CS0176 — enum/static shadowed by a `Window` instance property

Inside a `Window` (or other control) subclass, `Visibility.Visible` fails to compile: `Window.Visibility` is an **instance** property, so the bare name `Visibility` binds to it rather than the enum, and accessing a static/enum member through an instance is CS0176. Use the alias — `WpfVisibility.Visible`. The same shadowing can hit any enum whose name matches an inherited instance member.

### XmlSerializer requires public types

Any settings DTO serialized with `XmlSerializer` must be `public`. An `internal` (or otherwise non-public) root type throws *"only public types can be processed"* at `new XmlSerializer(typeof(T))` construction — and because that call usually sits inside a try/catch, every save and load fails **silently**, leaving settings stuck on defaults. This was the root cause of theme / UI-size resetting on restart (`UISettingsDto` was `internal`).

---

## Revit API Ordering Constraints

### ViewTemplateId before geometry

`view.ViewTemplateId = templateId` must be assigned **before** `SetSectionBox()` or any crop-box operation. The template assignment can reset view geometry; setting it first lets the subsequent programmatic geometry override it.

---

## Dimension Text & Leader Placement

- A Revit dimension's text leader is drawn by its **DimensionType** (the auto-dimension types here use **Arc** leaders). `Dimension.TextPosition` / `DimensionSegment.TextPosition` is the **only** handle — moving it both repositions the value text *and* lengthens the leader; there is no separate arc-vs-text control. To place moved value text readably, offset it **perpendicular** (to clear the arc) **and sideways along the measurement axis** so it sits beside the segment, not straight over it.
- Moved tags need their **own tag-vs-tag clash test at commit time**. The Revit-free layout core only models the dimension *band* (line + offset), so realized `LeaderOut`/`Staggered` text boxes can still overlap each other or other dimensions — build each moved tag's view-2D box and slide it further along-axis until it clears the tags already placed this run.

---

## LemoineMultiSelectTabs Contract

`SetGroups` fires `SelectionChanged` once at the end of setup. Any ViewModel that mirrors tab selection into a private field must subscribe to `SelectionChanged` **before** calling `SetGroups` — that callback is the only mechanism that populates the mirror field on initialisation.

---

## Reusable Components — Prefer Over Hand-Rolling

- **Numeric input:** `LemoineInlineStepper` is the house numeric field — a typeable centre plus ± buttons, `Decimals=0` for integers, clamped to `[MinValue, MaxValue]`, `ValueChanged` event. Use it for *every* numeric input; never a raw `TextBox` or the retired `LemoineNumberStepper`.
- **Drag ghost / list reorder:** use `LemoineDragGhost` and `LemoineListReorder` (see *WPF Drag Ghosts & Overlays*), never a bespoke Popup ghost or grip-handle reorder.

---

## WPF Hit-Testing

- A `TextBlock` or `Grid` with a null `Background` is only hit-testable on its rendered glyphs/borders, not its empty bounds. An element meant as a click target (a button label, a clickable row, the inner panel of a custom button) will respond only on the text unless you set `Background = Brushes.Transparent` (direct assignment — never via `SetResourceReference`). This was the "only the text is clickable" bug on the colour-picker set dropdown.

---

## Revit Crash Constraints

These patterns cause Revit to crash or hang. They have been discovered by breaking Revit in real sessions. Do not use them.

| ❌ Crashes Revit | ✅ Safe alternative |
|---|---|
| `Popup` with `StaysOpen=false` | `StaysOpen=true` + manual dismiss via `PreviewMouseDown` or a close button |
| `SizeToContent="WidthAndHeight"` + `WindowStyle="None"` | `Width=N` (fixed) + `SizeToContent="Height"` |
| `Autodesk.Windows.ComponentManager.ApplicationWindow` for window owner | Not referenced in this project — omit or use `WindowInteropHelper` with a Revit HWND |
| Shared `static` WPF Freezable (CubicEase easing, brush) left unfrozen | `.Freeze()` it at init — each tool window runs on its own STA thread, and cross-thread use of an unfrozen shared static crashes Revit (root cause of the easing crash, commit `86887ff`) |

### Why `Popup StaysOpen=false` crashes Revit

`StaysOpen=false` registers a `ComponentDispatcher.ThreadFilterMessage` hook to detect outside clicks. This fires on every Win32 message on Revit's main thread and corrupts the message loop.

### Dismissing a `StaysOpen=true` popup on click-off

Because `StaysOpen=false` crashes Revit, close an open popup by attaching a **window-level `PreviewMouseDown` handler only while it is open** and closing when the click lands outside the popup content (`!popupRoot.IsMouseOver`); detach on `Closed`. The popup hosts its own hwnd, so its own clicks never tunnel through the window — no `ThreadFilterMessage` hook, no crash.

### Revit API gotchas (Revit 2024)

| Wrong | Correct |
|---|---|
| `ZoomType.FitPage` | `ZoomType.FitToPage` |
| `RasterQualityType.Draft` | `RasterQualityType.Low` (Draft removed in 2024) |
| `PDFExportOptions.Zoom` | `PDFExportOptions.ZoomPercentage` |
| `ParameterFilterElement.AllFilterableCategories` | `ParameterFilterElement.GetAllFilterableCategories(doc)` |
| `TextNote` Y = top of text | TextNote Y is the **baseline** — cap height rises above it |
| App-level "font pt" field sizes generated text | A TextNote's size comes from its assigned `TextNoteType` (`TEXT_SIZE` param); a font-pt value can only drive a WPF preview, never the Revit output. Don't expose it as if it changed the legend. |
| `PickObject(ObjectType.Element)` to select an element **inside a link** | `PickObject(ObjectType.LinkedElement)` — `ObjectType.Element` returns the whole `RevitLinkInstance` (its `LinkedElementId` is unset), so the linked sub-element never resolves |

---

## WPF Drag Ghosts & Overlays

- A cursor-following drag ghost must be a window-space `AdornerLayer` overlay, **not** a `Popup`. `PlacementMode.AbsolutePoint` popups get nudged back on-screen near a screen edge, so the ghost drifts off the cursor (worst on the right).
- `AdornerLayer.GetAdornerLayer(source)` returns the *nearest* layer, which inside a `ScrollViewer` is clipped to that viewport — adorn `Window.GetWindow(source).Content` so the ghost spans the whole window.
- A `RenderTargetBitmap` / `VisualBrush` snapshot of an element whose `Background` is `Brushes.Transparent` captures only its text/borders on transparent pixels — paint a themed solid backing (`LemoineRaised`) behind the snapshot or it reads as invisible (this is why inactive, transparent-background tabs/rows showed no ghost).
- Anchor the ghost at the **grab point** (`e.GetPosition(source)`), not its centre — centring a wide row reads as "off the mouse" when grabbed near an edge.
- Don't hand-roll any of this: `LemoineDragGhost` (snapshot, grab-point anchored, solid backing) and `LemoineListReorder` (whole-row drag, persisted order) are the house mechanism.

---

## Build Environment

This project cannot be built on Linux. `UseWPF=true` + `net48` requires `Microsoft.NET.Sdk.WindowsDesktop`, which is Windows-only — neither the Linux .NET SDK nor Mono can satisfy it. Do not attempt Linux CI or cloud builds. Build and test on Windows only.

The Revit API DLLs (`RevitAPI.dll`, `RevitAPIUI.dll`) are checked in to `libs/`. The `.csproj` falls back to `libs/` when the standard Revit 2024 install path (`C:\Program Files\Autodesk\Revit 2024`) does not exist, so cloning the repo is sufficient to resolve references without a local Revit installation.

---

## Key Files

| Path | Purpose |
|------|---------|
| `LEMOINE_UI.md` | UI architecture, design system, component library, and tool contract |
| `LemoineTools.csproj` | Project file (targets .NET Framework 4.8) |
| `Source/` | All C# source and XAML files |

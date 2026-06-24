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
- A survey/collector that finds **zero** items must say so in the run log ("Found N …" / "No … found") — a silent empty result is indistinguishable from a broken collector, and that silence has hidden real bugs (the user-callout collector missing Detail-view callouts presented as "didn't even register").

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
- When child items belong to listed parents (e.g. worksets under documents), nest the children under a per-parent **expand caret** in the parent list — not a separate, parallel picker. Deselecting a parent must auto-clear/disable its children so a selected child can never sit under an unselected parent. This was the explicit correction that replaced the clash source-document/workset "separate tabs" layout with an inline document tree.

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

### CS0136 — inner-scope variable reuses an enclosing-scope name

A variable declared in a `foreach` or nested block that shares a name with a parameter or local in the enclosing method fails CS0136. The compiler rejects it even when C# would otherwise permit shadowing. Fix: rename the inner variable. (Root cause: `DiscoverEventHandler.ReadParameterValue` had `typeEl` in both the outer method and an inner `foreach`.)

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

### `RibbonPanel.AddStackedItems` — only 2 or 3 items

`AddStackedItems` has overloads for **exactly two or three** `RibbonItemData` arguments only. A four-argument call does not compile (no matching overload). To add a fourth button to a panel that already stacks three, give it its own `panel.AddItem(...)` rather than extending the stacked call.


---

## Revit API Ordering Constraints

### ViewTemplateId before geometry

`view.ViewTemplateId = templateId` must be assigned **before** `SetSectionBox()` or any crop-box operation. The template assignment can reset view geometry; setting it first lets the subsequent programmatic geometry override it.

Assigning `view.ViewTemplateId` a template whose `ViewType` differs from the view's **throws** (it does not silently no-op), and a **dependent** view cannot carry its own template at all. When applying templates across mixed view types, either filter to the matching type or wrap the assignment in try/catch and skip-and-log the mismatch (deleting any duplicate you created for it).

### Annotations in section/elevation views live in the view's cut plane

A `FilledRegion` or `DetailCurve` placed in a plan view can be built from world XY at `z=0`, but in a **section or elevation** the boundary must lie in the view's vertical cut plane. Build geometry from `view.RightDirection` / `view.UpDirection`, projecting the world point onto the plane by dropping its `view.ViewDirection` component (`p - ((p-origin)·n) n`). The plan-view world-XY trick silently produces empty/garbage regions in vertical views. A clash marker that must orient to an element's run (e.g. a rectangular duct) projects the element's world width/height axes into that same right/up basis.

---

## Dimension Text & Leader Placement

- A Revit dimension's text leader is drawn by its **DimensionType** (the auto-dimension types here use **Arc** leaders). `Dimension.TextPosition` / `DimensionSegment.TextPosition` is the **only** handle — moving it both repositions the value text *and* lengthens the leader; there is no separate arc-vs-text control. To place moved value text readably, offset it **perpendicular** (to clear the arc) **and sideways along the measurement axis** so it sits beside the segment, not straight over it.
- Moved tags need their **own tag-vs-tag clash test at commit time**. The Revit-free layout core only models the dimension *band* (line + offset), so realized `LeaderOut`/`Staggered` text boxes can still overlap each other or other dimensions — build each moved tag's view-2D box and slide it further along-axis until it clears the tags already placed this run.
- **Tag width must be sized from the dimension's real displayed value, not a decimal-feet string.** Imperial feet-inches (`0' - 11 5/8"`) is ~2–3× wider than `0.96'`, so estimating width from a decimal value badly under-sized the tag boxes and broke column/clash layout. Format the value through the type's own units (`DimensionType.GetUnitsFormatOptions()` → `Units.SetFormatOptions(SpecTypeId.Length, fo)` on the mutable `doc.GetUnits()` copy → `UnitFormatUtils.Format`) at plan time, and use the realized `DimensionSegment.ValueString` / `Dimension.ValueString` at commit time. `Units` has **no `UnitSystem` getter** in 2024 — override the Length format on the `GetUnits()` copy rather than constructing `new Units(...)`.

---

## LemoineMultiSelectTabs Contract

`SetGroups` fires `SelectionChanged` once at the end of setup. Any ViewModel that mirrors tab selection into a private field must subscribe to `SelectionChanged` **before** calling `SetGroups` — that callback is the only mechanism that populates the mirror field on initialisation.

For a **single-choice** picker, set `SingleSelect = true` **before** `SetGroups` rather than hand-rolling radios or coercing a multi-select down to one: checking an item then clears any prior selection (across all group tabs) and hides the per-group "All" row. Defaults to `false` (multi-select), so existing pickers are unaffected.

**Hierarchy nesting** — `LemoineMultiSelectTabs` accepts a `Hierarchy` property (`IReadOnlyDictionary<string, IReadOnlyList<string>>?`, same contract as `LemoineTagChipInput.Hierarchy`). Set it before `SetGroups`. Children whose parent is in the same group tab are hidden from the flat list and shown indented (32 px) under a ▸/▾ expand caret; the parent checkbox goes indeterminate when some-but-not-all of its children are selected. For any category picker, pass `AutoFiltersSettings.CategorySubcategories` as the source.

**Tab ordering** — `SetGroups` auto-sorts tabs alphabetically with `"Other"` pinned last. Callers do not need to pre-sort their group dictionaries.

**Annotation categories** — Annotation categories (`CategoryType != Model`) must never appear in model-element category pickers. `AutoFiltersSettings.CaptureFilterableCategories` already filters them out; do not re-introduce them.

---

## Step Flow — Conditional & Data-Dependent Steps

- **Step content is built eagerly at window construction.** A step whose content depends on an earlier step's choice (the live selection, the export mode, etc.) must implement `IStepAware` and rebuild itself in `OnStepActivated(stepId)` via the content-refresh callback — otherwise it renders once with stale/empty state and never updates. This was the root cause of Bulk Export's "Build Packs" appearing empty: the pack editor read the selection at construction (before anything was selected) and was never refreshed.
- **Hide steps conditionally with `ILemoineConditionalSteps`.** `IsStepVisible(stepId)` returning false collapses that step's accordion row and progress pip and skips it during forward/back navigation; visibility is re-evaluated on activation and on `ValidationChanged`. A conditional (hideable) step must **never be the last step** — the final step carries the Run button, log area, and review summary and is always shown. Tools that don't implement the interface are unaffected (every step visible).
- **Refresh a step ONLY through `IStepAware` — never re-parent children by hand.** To rebuild a step's content live (e.g. after a "Load preset" button), implement `IStepAware`, store the `SetContentRefreshCallback` delegate, and call it with the step id so `StepFlowWindow.RefreshStepContent` swaps the content child in place. **Do not** hand-roll a rebuild that moves children out of a freshly-built throwaway panel into the live container (`foreach (child in newPanel.Children) container.Children.Add(child)`) — a WPF `UIElement` can have only one logical parent, so `Children.Add` **throws `InvalidOperationException`** ("already the logical child of another element"), and that unhandled throw in a click handler hard-crashed Revit (Ceiling Heatmap "Load color ramp"). Even if it didn't throw, the rebuilt content's handlers would close over the orphan panel, not the live tree.

---

## Unhandled UI Exceptions Crash Revit — STA Dispatcher Safety Net

- **Every tool window runs on its own dedicated STA thread, and an unhandled exception on that dispatcher terminates Revit with NO `diagnostics.log` entry** (it is never routed through `LemoineLog`). `StepFlowWindow` installs a named `Dispatcher.UnhandledException` handler in its constructor (detached on `Closed`) that routes the exception through `LemoineLog.Error` and sets `e.Handled = true`, keeping the window alive. Keep this last-resort net; never assume a stray throw in an event handler is "just a managed exception that gets logged" — without this handler it is a silent hard crash.

---

## Export Filenames — Views vs Sheets

- A non-sheet `View` exposes **no** `SHEET_NUMBER` / `SHEET_NAME` parameters, so a sheet-token filename pattern (`{SheetNumber}-{SheetName}`) silently resolves to a degenerate name (e.g. `-`) for views, and every view collides on the same file. Name views from `view.Name` / `view.ViewType` instead, and offer a **mode-aware token vocabulary** (sheet tokens for sheets, view tokens for views) so only valid tokens are ever presented — never a silent fallback.
- **A resolved filename that is empty or has no alphanumeric character is a failure, not a fallback.** Detect it and report through both the run log (`pushLog(..., "warn")`) and `LemoineLog.Warn(...)` before substituting a deterministic name (`element.Name`, else element id). Export tooling must be **equally viable for views and sheets**.
- **Uniqueness differs by field.** Revit enforces uniqueness on **sheet numbers** (`SHEET_NUMBER`) and **view names** (`View.Name`) — both setters **throw** on a duplicate — but **sheet names are *not* unique**. A bulk rename/number tool must pre-check and **skip-and-log** collisions when rewriting sheet numbers or view names (against existing elements *and* earlier items in the same batch), while a sheet-*name* rewrite needs no uniqueness check. Even with the pre-check, wrap the actual `Set` / `Name =` in try/catch — a transient swap (A→B while B→A) still trips Revit's check and must be reported, not silently dropped.

---

## Viewports, Annotation Crop & Sheet Placement

Discovered building **Place Dependent Views** (one sheet per parent view, its dependents packed on it).

- **`doc.Regenerate()` recomputes the whole model — never call it per item in a loop.** It is the dominant cost of any bulk sheet/viewport tool. Do all mutations then regenerate once; regenerate per logical unit (e.g. per sheet) only when live progress matters more than raw speed. A single all-at-once regen also freezes the UI with no progress, so per-sheet regen reads as faster even when total work is similar — and offering a fast "estimate" mode (size from the crop box, skip the measure-regen) is worth it for expensive measure-based layout.
- **`Viewport.GetBoxOutline()` / `GetLabelOutline()` are only valid after a `doc.Regenerate()` following `Viewport.Create`.** The box outline is the true on-sheet footprint (sheet feet) and **excludes** the viewport label; union with `GetLabelOutline()` if titles must not overlap. `Viewport.SetBoxCenter()` positions the box and needs **no** regen (the commit recomputes once). The only reliable way to know a placed view's size is place → regen → read outline; sizing from the crop box alone is an estimate.
- **Trim a view's "bubbles"/annotations to its crop via the annotation crop, not by editing datum extents.** Enable `BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE` (with `CropBoxActive = true`), then set the four offsets on `view.GetCropRegionShapeManager()` — `Top/Bottom/Left/RightAnnotationCropOffset`. Offsets are **model feet**, so convert a desired paper gap with `view.Scale` (`modelFeet = paperFeet × Scale`). Revit **rejects 0 / negative** offsets — floor to a tiny positive value and wrap in try/catch (some view types can't carry an annotation crop) so a failure leaves the view untrimmed rather than aborting the run.
- **Drawing area = the placed title block's bounding box**, read via `titleBlockInstance.get_BoundingBox(sheet)` (valid only after a regen), minus per-side margins. Every sheet using the same title block type shares the same area — read it once and reuse.
- **Writing a value to a sheet's shared parameter by name is unreliable — KNOWN UNRESOLVED for the Sheet Series field.** `LookupParameter(name)` returns only the **first** name match and silently picks the wrong duplicate; `Element.GetParameters(name)` returns all matches (prefer a writable, String-storage, `IsShared` one), but even that did **not** reliably populate the shared "Sheet Series" parameter in testing. The robust fix (deferred) is to bind by **shared-parameter GUID** via `element.get_Parameter(Guid)`. Do not assume the Sheet Series write works.

---

## Reusable Components — Prefer Over Hand-Rolling

- **Numeric input:** `LemoineInlineStepper` is the house numeric field — a typeable centre plus ± buttons, `Decimals=0` for integers, clamped to `[MinValue, MaxValue]`, `ValueChanged` event. Use it for *every* numeric input; never a raw `TextBox` or the retired `LemoineNumberStepper`.
- **Drag ghost / list reorder:** use `LemoineDragGhost` and `LemoineListReorder` (see *WPF Drag Ghosts & Overlays*), never a bespoke Popup ghost or grip-handle reorder.
- **View / sheet selection:** use `LemoineBrowserTreePicker` — it mirrors the source document's Project Browser tree (folder titles, nesting, ordering, dependents nested under their primary), is fed by `BrowserTreeCapture.Capture(doc)` captured on the Revit main thread and handed over via `SetTree`, exposes `SingleSelect` for one-pick, and fires `SelectionChanged` once at the end of `SetTree` (same contract as `LemoineMultiSelectTabs.SetGroups` — subscribe first). Never hand-roll a `LemoineMultiSelectTabs` + label→ElementId map for picking views/sheets.

---

## Memory & Lifetime Discipline

Discovered auditing why tools held RAM after running.

- **Static ExternalEvent handlers live for the whole Revit session** (they're parked on `App` statics), so anything left on one outlives the run. Every handler must clear its per-run payload (input lists, specs, cached `View`/`Element` references, scan results) in a `finally` at the end of `Execute` — ViewModels reassign all inputs before each `Raise()`, so clearing is always safe. And every ViewModel that parks callbacks (`PushLog`, `OnProgress`, `OnComplete`, scan/pick callbacks) on a static handler must implement `ILemoineToolCleanup.OnWindowClosed` and null them — otherwise the closed window's ViewModel (and the WPF step content it references) stays rooted until the tool's next run, or forever that session.
- **`uidoc.ActiveView = view` opens that view in the Revit UI**, and Revit holds every open view's graphics in native RAM for the rest of the session — GC can never reclaim it. Any picker/loop that activates views (e.g. per-view `PickObject`) must snapshot the open `UIView`s and active view first, then afterwards restore the original active view and close only the views it opened (`PickerViewGuard` is the reference). Never close a view the user already had open.

---

## Run Lifecycle — Window Ownership, Failure Routing, Cancellation

- **The tool window is intentionally NOT owned to Revit — keep it independent.** Owning the window to Revit's main HWND (`new WindowInteropHelper(this).Owner = …` in `OnSourceInitialized`) glues it to Revit's z-order: it can no longer sit behind Revit, move to another monitor independently, or minimize/restore on its own. That pinning is worse than the problem it solved, so the owner was removed by explicit decision. **Accepted trade-off:** Revit's modal transaction-failure dialogs and `TaskDialog`s can again render *behind* the tool window — but those failures are still captured into the run's Output log via `LemoineFailureCapture` / `LemoineRunLog`, so nothing is silently lost. Do not re-add the HWND owner. (Never use `ComponentManager.ApplicationWindow` for an owner either — it crashes Revit.)
- **Route Revit's own warnings/errors/dialogs into the active run's Output log.** `LemoineFailureCapture` (process-wide `FailuresProcessing` + `DialogBoxShowing` handlers, subscribed once in `App`) feeds the active run's log via `LemoineRunLog`; both **no-op outside a Lemoine run** so other transactions are untouched. Call `LemoineFailureCapture.BeginRun()` + `LemoineRunLog.Set(pushLog)` at run start and `LemoineRunLog.Clear()` when the window closes.
- **Make every long run cooperatively cancellable.** `LemoineRun` holds a thread-safe cancel flag (`Begin`/`Checkpoint`); while a run is in flight the footer Reset button flips to a red Cancel. Every **looping** `ExternalEvent` handler must break at its Output-log checkpoint and **fall through to the existing commit** so committed work is preserved (finish state `Stopped` with partial counts). Read-only single-shot pick/print handlers need no cancel break.

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
| Per-STA-thread window subscribing to a global event (`LemoineSettings.ThemeChanged` / `UiSizeChanged`) with an anonymous lambda + blocking `Dispatcher.Invoke` | Named handler detached on `Closed`; marshal with non-blocking `BeginInvoke` guarded by `if (Dispatcher.HasShutdownStarted) return;` (root cause of the theme-switch crash) |

### Why leaked global-event subscriptions crash Revit

Each tool/settings window runs on its **own dedicated STA thread** that shuts down (`Dispatcher.InvokeShutdown()`) when the window closes. A subscription to a process-wide event like `LemoineSettings.ThemeChanged` made with an **anonymous lambda** can never be `-=`'d, so it **outlives the window**. The next time the event fires, the stale handler runs a blocking `Dispatcher.Invoke` into a **terminated dispatcher** — which throws on the firing thread (unhandled → crash) or blocks forever waiting for a thread that will never pump (→ Revit hangs). The bug is session-history-dependent (you must have opened *and closed* such a window earlier), so it presents as an intermittent crash.

Rules for any window subscribing to a global Lemoine event:
- Subscribe with a **named instance method**, never an anonymous lambda, and `-=` it in `Closed`/`OnClosed`. `StepFlowWindow` is the reference.
- In the handler, marshal back with **non-blocking `Dispatcher.BeginInvoke`** (not blocking `Invoke` — that can deadlock against Revit's main thread), guarded by `if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;`.
- The event raiser (`LemoineSettings.SetTheme`/`SetUiSize`) walks `GetInvocationList()` and wraps each subscriber in `try/catch` → `LemoineLog.Swallowed`, so one dead subscriber can't abort the chain or crash the initiating thread.

### Why `Popup StaysOpen=false` crashes Revit

`StaysOpen=false` registers a `ComponentDispatcher.ThreadFilterMessage` hook to detect outside clicks. This fires on every Win32 message on Revit's main thread and corrupts the message loop.

### Dismissing a `StaysOpen=true` popup on click-off

Because `StaysOpen=false` crashes Revit, close an open popup by attaching a **window-level `PreviewMouseDown` handler only while it is open** and closing when the click lands outside the popup content (`!popupRoot.IsMouseOver`); detach on `Closed`. The popup hosts its own hwnd, so its own clicks never tunnel through the window — no `ThreadFilterMessage` hook, no crash.

### Popup / dropdown scroll-wheel behaviour

These were discovered fixing the "category pill dropdown scrolls down but not up" bug.

- Inside an `AllowsTransparency=true` `Popup` (Revit's WPF hosting), the default `ScrollViewer` mouse-wheel handling is **unreliable and asymmetric** — it delivers down-scrolls but drops up-scrolls. Don't rely on it: handle `PreviewMouseWheel`, drive the offset yourself with `ScrollToVerticalOffset` (clamped to `[0, ScrollableHeight]`), and set `e.Handled = true`. Manual scrolling is symmetric by construction.
- A `Popup`'s routed events **bubble up into the owner window's element tree**, so a popup-hosted scroller that re-raises the wheel to its parent lets the *page's* scroll position govern the dropdown (the page being at the top blocks scrolling the popup up). Popup/dropdown scrollers must be **self-contained** — consume the wheel, never bubble it out to the page behind them. In-page nested scrollers are the opposite: they *should* bubble to the page at their limits (`LemoineControlStyles.WireBubblingScroll`).
- Visual-tree popup detection (walking `VisualTreeHelper` parents up to a `PopupRoot`) is **unreliable under Revit's hosting**. Tag popup scrollers authoritatively with `LemoineControlStyles.SetSelfContainedScroll(sv, true)`; the `PresentationSource.RootVisual == PopupRoot` check is only a backstop.

### Revit API gotchas (Revit 2024)

| Wrong | Correct |
|---|---|
| `ZoomType.FitPage` | `ZoomType.FitToPage` |
| `RasterQualityType.Draft` | `RasterQualityType.Low` (Draft removed in 2024) |
| `PDFExportOptions.Zoom` | `PDFExportOptions.ZoomPercentage` |
| `ParameterFilterElement.AllFilterableCategories` | `ParameterFilterElement.GetAllFilterableCategories(doc)` |
| `ParameterFilterUtilities.GetAllFilterableCategories(doc)` | `ParameterFilterUtilities.GetAllFilterableCategories()` — **parameterless** (CS1501 if a `doc` is passed); this is the static-utility overload, distinct from the `ParameterFilterElement.GetAllFilterableCategories(doc)` row above |
| Filter rule on `BuiltInParameter.ELEM_CATEGORY_PARAM` ("Category") | Rejected — *"parameter does not apply to this filter's categories"*. Build whole-category filters **rule-less** via the 3-arg `ParameterFilterElement.Create(doc, name, categories)` (matches every element in the categories, references no parameter) |
| Family Name filter on `ELEM_FAMILY_PARAM` (ElementId storage) | `ALL_MODEL_FAMILY_NAME` (String storage) — a string contains/equals rule can't be built on an ElementId parameter; the rule is silently dropped and the filter never matches |
| `TextNote` Y = top of text | TextNote Y is the **baseline** — cap height rises above it |
| Centering a TextNote row with baseline math | Set `VerticalTextAlignment.Middle` and pass the band's vertical centre as Y — the note's midpoint lands there regardless of font/size, eliminating baseline-vs-cap ambiguity |
| `TextNote.Create` with a TextNoteType ElementId from another project | Throws without a clear message — validate each type id with `doc.GetElement(id) as TextNoteType != null` before calling Create; fall back to a valid type in the current doc |
| `ElementCategoryFilter(OST_FilledRegion)` to collect FilledRegions for deletion | Use `ElementClassFilter(typeof(FilledRegion))` — category filters miss FilledRegion elements in drafting views; class filters are authoritative for deletion |
| App-level "font pt" field sizes generated text | A TextNote's size comes from its assigned `TextNoteType` (`TEXT_SIZE` param); a font-pt value can only drive a WPF preview, never the Revit output. Don't expose it as if it changed the legend. |
| `PickObject(ObjectType.Element)` to select an element **inside a link** | `PickObject(ObjectType.LinkedElement)` — `ObjectType.Element` returns the whole `RevitLinkInstance` (its `LinkedElementId` is unset), so the linked sub-element never resolves |
| `collector.Where(t => t.Category.Id == new ElementId(OST_SpotElevations))` to list spot-elevation types | `ElementType.Category` reads as **null** for annotation types and silently drops every match — enumerate with `new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_SpotElevations).OfClass(typeof(SpotDimensionType))` instead |
| Reading `…_DIAMETER` before width/height to classify an MEP cross-section | A rectangular duct can also expose an *equivalent-diameter* parameter, so test `RBS_CURVE_WIDTH_PARAM` + `RBS_CURVE_HEIGHT_PARAM` **first**; only fall back to a `…_DIAMETER` param when both are absent |
| `OfClass(typeof(ViewPlan))` to collect user-drawn callouts | Revit's default callout type is a **Detail view** (`ViewSection`), not a `ViewPlan`, so a ViewPlan filter silently misses most user callouts — probe **all** view classes and match `View.GetCalloutParentId() == parent.Id` |
| Matching clash markers across views by their stamped `ClashTagSchema` group key | Group ids are **fresh GUIDs minted per `PlaceInView` call** — the same clash carries a different group id in every view it is marked in, so cross-view key comparison matches nothing (and a key-based prune deletes everything). Match markers across views **geometrically** (clash anchor inside a world rectangle) |
| `Document.Create.NewSpotElevation(view, ref, …)` with no real geometry reference | The `Reference` must come from actual geometry — anchor it to a detail line via `CurveElement.GeometryCurve.Reference` (fallback `new Reference(element)`) |
| Read a value to filter on via the **property-palette display** (`LookupParameter`) | Read it through the **same built-in the filter binds** — a view filter's string-rule keyword is compared against the *bound* parameter's value, not the palette. The palette "Fabrication Service" is a composite (`<abbreviation>: <name>`, e.g. `DVG - MP: Chilled Water Supply`) but `FABRICATION_SERVICE_NAME` holds only the name (`Chilled Water Supply`), so a keyword carrying the prefix never matches |
| String filter rule against an **ElementId-storage** parameter (e.g. `ELEM_FAMILY_PARAM`) | `ParameterFilterRuleFactory.CreateContainsRule`/string rules require **String storage** — an ElementId param throws and the keyword is silently dropped (filter never created). For a string Family Name filter bind `ALL_MODEL_FAMILY_NAME`, not `ELEM_FAMILY_PARAM` |
| `Element.WorksetId.Value` to read a workset id | `WorksetId.IntegerValue` (an `int`) — `WorksetId` still uses `IntegerValue`, unlike `ElementId.Value` (a `long`) used everywhere else in 2024. Worksharing reads: guard with `doc.IsWorkshared`, enumerate user worksets via `new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)`, and read an element's workset with `Element.WorksetId`. A link's worksets live in its own `GetLinkDocument()`, so read/scan them there, not the host |
| `ElevationMarker.GetViewId(idx)` assuming it returns `InvalidElementId` for an empty slot | An **unused** marker index can **throw** rather than return invalid — loop `for (idx = 0; idx < m.MaximumViewCount; idx++)` and wrap each `GetViewId(idx)` in try/catch, `continue`-on-throw (then still skip `null` / `InvalidElementId`) |
| Resolving which views a section/callout marker points to by reading a target-id property | Section/callout markers are `OST_Viewers`; resolve each to its target view by **unique name** (`marker.Name` → view-by-name map). Scope the collector to the source view id (`new FilteredElementCollector(doc, source.Id)`) so only **visible** markers are returned — hidden ones are excluded |
| Coloring a surface fill via `OverrideGraphicSettings` without distinguishing foreground/background | Foreground and background are **independent** layers. A "solid color fill with black lines" needs the **background** pattern set (`SetSurfaceBackgroundPatternId(solidFill)` + `…Color(c)` + `…Visible(true)`, and the cut equivalents) and only the **foreground pattern *color*** set to black (`SetSurfaceForegroundPatternColor` / `SetCutForegroundPatternColor`) with **no** foreground pattern id — setting a pattern *color* without a pattern *id* yields the color with no fill pattern (the Fill Pattern Graphics dialog's `<No Override>` foreground). Putting the solid fill on the foreground instead hides the element's own linework |

---

## ParameterFilterElement Lifecycle (AutoFilters)

- **Update in place, don't churn.** To change an existing `ParameterFilterElement`, call `SetCategories` / `SetElementFilter` — this preserves its `ElementId`, so view assignments and legend links survive. Delete + recreate mints a new `ElementId` and silently detaches the filter from every view that referenced it. Rebuild (delete + recreate) **only** when an in-place edit can't yield the correct definition (e.g. converting a keyword rule to whole-category — old rules can't be cleared in place; or a category/parameter change incompatible with the current state). A rule-less filter reports `GetElementFilter() == null`, which is how you detect it still carries stale rules.
- **Discover must read the filter's parameter.** When a discover/scan captures keyword values, read them through the **same** `BuiltInParameter` the generated filter binds (e.g. `FABRICATION_SERVICE_NAME`), not the property-palette composite value — otherwise the captured `contains` keyword never matches what the view filter compares against.
- **Mirror Revit's category list from the API, never a curated map.** To make the category picker match Revit's "Edit Filters → Categories" tree exactly, capture it on the Revit main thread (the launch commands run there with the doc) via `ParameterFilterUtilities.GetAllFilterableCategories()` and resolve each with `Category.GetCategory(doc, id)` for its real `Category.Name`. Derive the picker's parent→child nesting from each category's actual `Category.Parent` — a hand-curated grouping is wrong (it nested flat siblings like Duct Fittings under Ducts; Revit only nests true sub-categories such as Roofs → Fascias/Gutters/Roof Soffits). `AutoFiltersSettings.CaptureFilterableCategories(doc)` is the reference; a hardcoded map stays only as the no-document (preview-app) fallback.
- **Only BuiltInCategory-backed categories are storable.** The filter engine persists categories as `OST_` strings, so capture only filterable categories whose id is a negative `BuiltInCategory`; non-builtin custom subcategories (e.g. `<Path of Travel Lines>`, `<Area Based Load Boundary>`) cannot be stored and must be skipped. Disambiguate duplicate Revit sub-category names (an `Insulation` under two parents) by qualifying with the parent name so each display token maps to exactly one `OST_` string.

---

## Cross-Document Copy & Idempotent Re-Runs

Discovered building **Copy Linear Elements** / **Copy Grids** (pull elements out of a link into the host).

- **Cross-document `ElementTransformUtils.CopyElements` pops a modal "Duplicate Types" dialog for every call** when any type already exists in the host (it does for nearly every MEP/grid copy). Pass a `CopyPasteOptions` whose `SetDuplicateTypeNamesHandler` returns `DuplicateTypeAction.UseDestinationTypes` (a tiny `IDuplicateTypeNamesHandler`) to suppress it and silently reuse the destination's types. `Transaction` `SetForcedModalHandling(false)` does **not** suppress this dialog — it is a copy-paste prompt, not a failure.
- **The cross-document `CopyElements(srcDoc, ids, destDoc, transform, opts)` overload throws when `srcDoc == destDoc`.** For a host-sourced copy use the same-document `ElementTransformUtils.CopyElement(doc, id, XYZ.Zero)` instead; only use the cross-doc overload for a real link (pass `link.GetTotalTransform()`).
- **Idempotent re-runs over linked sources: stamp, don't track externally.** Write an Extensible Storage `Entity` onto every created host element carrying the source `UniqueId` + a geometry/param hash (constant hardcoded `Schema` GUID, `Schema.Lookup` guard — same discipline as `AutoDimOwnerSchema`). A re-run reads all stamped outputs in one pass via `new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid))` and reconciles: rebuild changed/new keys (deleting their prior outputs first), leave unchanged keys, delete outputs whose source key is gone. No external database, self-healing.
- **Grids are unique by name and the setter throws on a duplicate**, so a grid copy must pre-check host grid names and **skip-and-log** any clash (it can never overwrite). Same family as the View.Name / sheet-number uniqueness rule.

---

## WPF Drag Ghosts & Overlays

- A cursor-following drag ghost must be a window-space `AdornerLayer` overlay, **not** a `Popup`. `PlacementMode.AbsolutePoint` popups get nudged back on-screen near a screen edge, so the ghost drifts off the cursor (worst on the right).
- `AdornerLayer.GetAdornerLayer(source)` returns the *nearest* layer, which inside a `ScrollViewer` is clipped to that viewport — adorn `Window.GetWindow(source).Content` so the ghost spans the whole window.
- A `RenderTargetBitmap` / `VisualBrush` snapshot of an element whose `Background` is `Brushes.Transparent` captures only its text/borders on transparent pixels — paint a themed solid backing (`LemoineRaised`) behind the snapshot or it reads as invisible (this is why inactive, transparent-background tabs/rows showed no ghost).
- Anchor the ghost at the **grab point** (`e.GetPosition(source)`), not its centre — centring a wide row reads as "off the mouse" when grabbed near an edge.
- Don't hand-roll any of this: `LemoineDragGhost` (snapshot, grab-point anchored, solid backing) and `LemoineListReorder` (whole-row drag, persisted order) are the house mechanism.

---

## View Filters & Linked Models

Discovered while making **Make Ceiling Grids** hide linked ceilings.

- **Host view filters only affect linked elements when the link is displayed "By Host View"** in that view. To hide or override linked elements, apply a `ParameterFilterElement` on the host view — do **not** rely on per-instance `view.HideElements`, because `FilteredElementCollector(doc, viewId)` (the host-view collector) never returns elements that live inside links, so it silently misses every linked ceiling. Warn the user about any link not set to "By Host View" (see `ReportLinkDisplayModes`) rather than changing the link's display.
- **A `ParameterFilterElement` rule matches a single parameter** — family AND type cannot be AND-combined in one rule. Match ceiling types by the link-safe built-in `ALL_MODEL_TYPE_NAME` ("Type Name"); link-safe built-in parameters are listed in `AutoFiltersSettings.LinkSafeParameters`.
- **Prefer the Ceiling Heatmap filter mechanism for any filter-driven tool.** Register an `ExternallyManaged` trade (`FilterTradeConfig`) with one rule per item, create one matching `ParameterFilterElement` per rule (reuse-by-name via `AutoFiltersSettings.MakeFilterName`), apply per-view inside a single transaction, and call `ReportLinkDisplayModes` — rather than hand-rolling a combined filter. `CeilingHeatmapEventHandler.RegisterCeilingHeatmapTrade` is the reference.

---

## Build Environment

This project cannot be built on Linux. `UseWPF=true` + `net48` requires `Microsoft.NET.Sdk.WindowsDesktop`, which is Windows-only — neither the Linux .NET SDK nor Mono can satisfy it. Do not attempt Linux CI or cloud builds. Build and test on Windows only.

The Revit API DLLs (`RevitAPI.dll`, `RevitAPIUI.dll`) are checked in to `libs/`. The `.csproj` falls back to `libs/` when the standard Revit 2024 install path (`C:\Program Files\Autodesk\Revit 2024`) does not exist, so cloning the repo is sufficient to resolve references without a local Revit installation.

**`LemoineTools.csproj` is a root-level SDK-style project, so its default `**\*` globs sweep every subfolder — including sibling sub-projects' `obj\` output.** Each sibling project (`LemoinePreview`, `LemoineNavisworks`, any future one) must be `Remove`-excluded from `Compile`/`Page`/`None`/`EmbeddedResource`, or MSBuild compiles its generated `*.AssemblyAttributes.cs` (→ **CS0579** duplicate `TargetFrameworkAttribute`, sometimes for *both* net48 and net8 targets) and its XAML `*.g.cs` (→ **CS0102** duplicate `_root`/`_outer` field). Keep the exclusion **unconditional**: an untracked `obj\` folder survives a branch switch, so a sibling project that lives only on another branch can still poison this build locally.

---

## Key Files

| Path | Purpose |
|------|---------|
| `LEMOINE_UI.md` | UI architecture, design system, component library, and tool contract |
| `LemoineTools.csproj` | Project file (targets .NET Framework 4.8) |
| `Source/Lemoine/LemoineFailureCapture.cs` | Process-wide `FailuresProcessing`/`DialogBoxShowing` capture that routes Revit's own failures into the active run's log |
| `Source/Lemoine/LemoineRunLog.cs` | Active run's log sink, set/cleared by the tool window |
| `Source/Lemoine/LemoineRun.cs` | Thread-safe cancel flag + `Checkpoint` for cooperative run cancellation |
| `Source/` | All C# source and XAML files |

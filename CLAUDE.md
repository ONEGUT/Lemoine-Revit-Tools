# Lemoine Revit Tools — Claude Guidelines

## Project Overview

This is a Revit plugin built with C# and WPF targeting .NET Framework 4.8. The primary project is `LemoineTools`. See `LEMOINE_UI.md` for the full UI architecture reference.

---

## Crashes & Large Ambiguous Issues — Build a Debugger First

When the user reports a **crash** (Revit closing/hanging) or any **large, ambiguous problem** that can't be pinned to a specific line by reading code, the FIRST move is to **build a dedicated debug harness — not to theorize from inspection**. Code-reading has repeatedly failed to find crash causes; a harness that reproduces and isolates the fault is the reliable path.

The harness is an `IStepFlowTool` opened in `StepFlowWindow` (model: `MotionTestViewModel` / `DebugToolCommand`). For crashes specifically:

- **Lazily construct each suspect** behind a button, so merely opening the harness or navigating a step does NOT trigger the crash. `StepFlowWindow` builds every step's content eagerly at construction, so a crashing construct must be deferred to a button `Click` to be isolatable.
- Give each step/button ONE suspect (a single control, or the same control at scale — e.g. "build 60 swatches", "MultiSelectTabs with 8×40 items"). The button press that crashes Revit names the culprit.
- Hard crashes (no entry in `%AppData%\LemoineTools\diagnostics.log`) are native/WPF/message-loop or stack-overflow faults that `try/catch` cannot catch — only a probe harness isolates them. A managed exception WILL appear in the log via `DiagnosticsLog`.
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
Refactor IStepFlowTool to support async execute
```

---

## Error Handling & Silent Failure Audit

### Standards for all C# code

- Never swallow exceptions silently — empty `catch` blocks and catch-and-log-only blocks that discard the error are forbidden unless the risk is explicitly acknowledged and the user has been told.
- `DiagnosticsLog` is the central diagnostic sink (logs to `%AppData%\LemoineTools\diagnostics.log` plus an in-memory ring). Route every deliberately-swallowed exception through `DiagnosticsLog.Swallowed(context, ex)` or `DiagnosticsLog.Error(context, ex)` with a human-readable context string — never an empty `catch {}` or a `Debug.WriteLine`. It is Revit-free, so every layer can call it.
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
- When a tool offers export formats/options that are only valid for certain input types (e.g. NWC/IFC require a 3D view), **hide** the invalid ones rather than showing them disabled or showing-all-then-skip-and-logging. The picker should only ever present choices that work for the current input. (This was the explicit choice for Print View's format toggles: PDF/DWG for any view or sheet, NWC/IFC only when the active view is 3D.)
- **For placing items on a 2D grid (rows × columns of cards), use a single live insertion marker over an always-visible lane grid — never multiple thin drop zones.** The Legend Creator's group placement was redesigned this way (one accent marker that snaps to the nearest gutter, or a full-width lane between/above/below rows for a new row): the prior system of thin transparent edge/between-row bars plus 4px in-row slots was explicitly rejected as invisible-until-drag, overlapping (builder edge bars vs. row-internal slots fighting for the same intent), guess-based (`FindNearestRow`), and reflowing the row while aiming (slot widened 4→14px on hover). Don't reintroduce sliver drop targets.

---

## Research Discipline

Always read the relevant source files before recommending or writing code. Never generate implementation from memory when the actual file is available.

---

## WebView2 UI Migration

The UI is migrating from WPF to WebView2-hosted HTML. **`plan-webview2-ui-migration.md`
is the authoritative rules + phase plan** — read it before any WebView2/host/bridge/HTML-page
work, and append verified Windows findings to its §5 log. The WebView2 Test harness
(Developer ribbon panel; `Source/Tools/Debuggers/`) is the proving ground: WebView2 renders
inside Revit tool windows, the JS↔C# bridge works, and HTML recreations of the house inputs
match the WPF originals. The four silent-blank-control failure modes (unwritable default
user-data folder, missing `WebView2Loader.dll`, navigating before `EnsureCoreWebView2Async`
completes, version clash with Revit's own WebView2) are encoded as rules R1–R4 there — never
regress them, and never create a `CoreWebView2Environment` outside the shared host layer.

---

## WPF UI Tasks

For any task that involves building, modifying, or debugging a WPF window or UserControl, invoke the `/revit-navisworks-ui` skill before writing any code. This applies even for small layout fixes.

For any UI tweak/build/layout change, **render a faithful mockup image for approval before writing code** — pull the real `ThemePalette` palette, build an HTML mockup, screenshot it with the pre-installed headless Chromium, and deliver it. The full workflow (and the headless-Chromium gotchas, incl. the bottom-anchored-content culling bug + normal-flow/spacer workaround) lives in the skill's Step 7. Iterate on the image, not on compiled code.

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
- **Below-line (Flipped) moved tags need an extra downward push — Revit anchors value text near its baseline.** A below tag whose `TextPosition` is set at the *same* perpendicular magnitude as an above (Staggered) tag renders hard against the dimension line (it appears to move only sideways), so above/below gaps look asymmetric. Bias the below base clearance down by ~one text height in both the commit (`PlaceColumn`) and the plan-time `TagColumnPlanner` so the scorer and commit agree. *(Observed from the symptom; the exact magnitude is tunable and still needs confirming on a Windows plot.)*
- **`AutoDimensionRunner.Run` dimensions existing in-view markers and never creates callouts.** The callout tiers (`SurveyDenseAreas` / `SurveyUserCallouts`) are orchestrated only by `ClashFinderEventHandler`, so a tool that calls the runner directly (e.g. Refine Dimensions) gets no callouts and no view-scale change for free — pass the views as `cropBoundedViewIds` to dimension to the nearest grid/slab edge visible in each view.

---

## MultiSelectTabs Contract

`SetGroups` fires `SelectionChanged` once at the end of setup. Any ViewModel that mirrors tab selection into a private field must subscribe to `SelectionChanged` **before** calling `SetGroups` — that callback is the only mechanism that populates the mirror field on initialisation.

For a **single-choice** picker, set `SingleSelect = true` **before** `SetGroups` rather than hand-rolling radios or coercing a multi-select down to one: checking an item then clears any prior selection (across all group tabs) and hides the per-group "All" row. Defaults to `false` (multi-select), so existing pickers are unaffected.

**Hierarchy nesting** — `MultiSelectTabs` accepts a `Hierarchy` property (`IReadOnlyDictionary<string, IReadOnlyList<string>>?`, same contract as `TagChipInput.Hierarchy`). Set it before `SetGroups`. Children whose parent is in the same group tab are hidden from the flat list and shown indented (32 px) under a ▸/▾ expand caret; the parent checkbox goes indeterminate when some-but-not-all of its children are selected. For any category picker, pass `AutoFiltersSettings.CategorySubcategories` as the source.

**Tab ordering** — `SetGroups` auto-sorts tabs alphabetically with `"Other"` pinned last. Callers do not need to pre-sort their group dictionaries.

**Disabled items** — `MultiSelectTabs` accepts a `DisabledItems` property (`IReadOnlyCollection<string>?`). Set it before `SetGroups`. A disabled item is still listed (so the user sees WHY it's absent, not a silently shrinking list) but rendered dimmed with a non-interactive checkbox, is excluded from the per-group "All" toggle's all-checked/some-checked math and from what "All" adds/removes, and can never appear in `SelectionChanged` results. This is the mechanism Copy Datums uses to show grids/levels that already exist in the host, greyed out, instead of hiding them. Only the flat (non-`Hierarchy`) list path honours it today.

**Annotation categories** — Annotation categories (`CategoryType != Model`) must never appear in model-element category pickers. `AutoFiltersSettings.CaptureFilterableCategories` already filters them out; do not re-introduce them.

---

## Step Flow — Conditional & Data-Dependent Steps

- **Step content is built eagerly at window construction.** A step whose content depends on an earlier step's choice (the live selection, the export mode, etc.) must implement `IStepAware` and rebuild itself in `OnStepActivated(stepId)` via the content-refresh callback — otherwise it renders once with stale/empty state and never updates. This was the root cause of Bulk Export's "Build Packs" appearing empty: the pack editor read the selection at construction (before anything was selected) and was never refreshed.
- **Hide steps conditionally with `IConditionalSteps`.** `IsStepVisible(stepId)` returning false collapses that step's accordion row and progress pip and skips it during forward/back navigation; visibility is re-evaluated on activation and on `ValidationChanged`. A conditional (hideable) step must **never be the last step** — the final step carries the Run button, log area, and review summary and is always shown. Tools that don't implement the interface are unaffected (every step visible).
- **Refresh a step ONLY through `IStepAware` — never re-parent children by hand.** To rebuild a step's content live (e.g. after a "Load preset" button), implement `IStepAware`, store the `SetContentRefreshCallback` delegate, and call it with the step id so `StepFlowWindow.RefreshStepContent` swaps the content child in place. **Do not** hand-roll a rebuild that moves children out of a freshly-built throwaway panel into the live container (`foreach (child in newPanel.Children) container.Children.Add(child)`) — a WPF `UIElement` can have only one logical parent, so `Children.Add` **throws `InvalidOperationException`** ("already the logical child of another element"), and that unhandled throw in a click handler hard-crashed Revit (Ceiling Heatmap "Load color ramp"). Even if it didn't throw, the rebuilt content's handlers would close over the orphan panel, not the live tree.

---

## Unhandled UI Exceptions Crash Revit — STA Dispatcher Safety Net

- **Every tool window runs on its own dedicated STA thread, and an unhandled exception on that dispatcher terminates Revit with NO `diagnostics.log` entry** (it is never routed through `DiagnosticsLog`). `StepFlowWindow` installs a named `Dispatcher.UnhandledException` handler in its constructor (detached on `Closed`) that routes the exception through `DiagnosticsLog.Error` and sets `e.Handled = true`, keeping the window alive. Keep this last-resort net; never assume a stray throw in an event handler is "just a managed exception that gets logged" — without this handler it is a silent hard crash.
- **A window that is NOT `StepFlowWindow` (e.g. `FiltersSettingsWindow`) has no such net, so any *auto-firing* callback on it must guard its own body.** A `DispatcherTimer.Tick` (or other timer/async continuation that fires without user action) that throws goes straight to the dispatcher and hard-crashes Revit — wrap the tick body in `try/catch → DiagnosticsLog.Swallowed(context, ex)`. User-initiated click handlers are lower risk but still safer guarded when they do I/O or (de)serialization.

---

## Export Filenames — Views vs Sheets

- A non-sheet `View` exposes **no** `SHEET_NUMBER` / `SHEET_NAME` parameters, so a sheet-token filename pattern (`{SheetNumber}-{SheetName}`) silently resolves to a degenerate name (e.g. `-`) for views, and every view collides on the same file. Name views from `view.Name` / `view.ViewType` instead, and offer a **mode-aware token vocabulary** (sheet tokens for sheets, view tokens for views) so only valid tokens are ever presented — never a silent fallback.
- **A resolved filename that is empty or has no alphanumeric character is a failure, not a fallback.** Detect it and report through both the run log (`pushLog(..., "warn")`) and `DiagnosticsLog.Warn(...)` before substituting a deterministic name (`element.Name`, else element id). Export tooling must be **equally viable for views and sheets**.
- **Uniqueness differs by field.** Revit enforces uniqueness on **sheet numbers** (`SHEET_NUMBER`) and **view names** (`View.Name`) — both setters **throw** on a duplicate — but **sheet names are *not* unique**. A bulk rename/number tool must pre-check and **skip-and-log** collisions when rewriting sheet numbers or view names (against existing elements *and* earlier items in the same batch), while a sheet-*name* rewrite needs no uniqueness check. Even with the pre-check, wrap the actual `Set` / `Name =` in try/catch — a transient swap (A→B while B→A) still trips Revit's check and must be reported, not silently dropped.

---

## Viewports, Annotation Crop & Sheet Placement

Discovered building **Place Dependent Views** (one sheet per parent view, its dependents packed on it).

- **`doc.Regenerate()` recomputes the whole model — never call it per item in a loop.** It is the dominant cost of any bulk sheet/viewport tool. Do all mutations then regenerate once; regenerate per logical unit (e.g. per sheet) only when live progress matters more than raw speed. A single all-at-once regen also freezes the UI with no progress, so per-sheet regen reads as faster even when total work is similar — and offering a fast "estimate" mode (size from the crop box, skip the measure-regen) is worth it for expensive measure-based layout.
- **`Viewport.GetBoxOutline()` / `GetLabelOutline()` are only valid after a `doc.Regenerate()` following `Viewport.Create`.** The box outline is the true on-sheet footprint (sheet feet) and **excludes** the viewport label; union with `GetLabelOutline()` if titles must not overlap. `Viewport.SetBoxCenter()` positions the box and needs **no** regen (the commit recomputes once). The only reliable way to know a placed view's size is place → regen → read outline; sizing from the crop box alone is an estimate.
- **Trim a view's "bubbles"/annotations to its crop via the annotation crop, not by editing datum extents.** Enable `BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE` (with `CropBoxActive = true`), then set the four offsets on `view.GetCropRegionShapeManager()` — `Top/Bottom/Left/RightAnnotationCropOffset`. Offsets are **model feet**, so convert a desired paper gap with `view.Scale` (`modelFeet = paperFeet × Scale`). Revit **rejects 0 / negative** offsets — floor to a tiny positive value and wrap in try/catch (some view types can't carry an annotation crop) so a failure leaves the view untrimmed rather than aborting the run.
- **Drawing area = the placed title block's bounding box**, read via `titleBlockInstance.get_BoundingBox(sheet)` (valid only after a regen), minus per-side margins. Every sheet using the same title block type shares the same area — read it once and reuse.
- **A viewport's view title (`Viewport.LabelOffset`) is positioned relative to its box, so moving the viewport (`SetBoxCenter`) drags the title with it — align titles LAST, after every box is in its final spot.** `LabelOffset` translates the whole label rigidly in sheet coordinates, so to overlay one title on another: set `Viewport.LabelLineLength` to match the source's underline, `doc.Regenerate()` **once** for the whole run so the moved/resized titles report real outlines, then add `(sourceLabelOutline.MinimumPoint − targetLabelOutline.MinimumPoint)` to the target's `LabelOffset`. Reading a *moved* viewport's `GetLabelOutline()` needs that regen — the "valid only after a regen following `Viewport.Create`" rule also applies to a viewport repositioned with `SetBoxCenter`. (Min-corner/line-start anchor used by Align Sheet Views; still provisional pending a Windows plot.)
- **User-drawn callouts adopted as clash groups crop and dimension to the boundary the user drew — NOT the containing room.** Growing the callout crop out to the room (as the automatic dense tier does) moved the dimensioned region away from where the callout was drawn and could leave the drawn spot bare. The drawn boundary is authoritative; a clash whose target sits outside it is reported unresolved, never silently relocated. (The automatic dense tier still grows to rooms — this applies only to `SurveyUserCallouts`.)
- **Writing a value to a sheet's shared parameter by name is unreliable — KNOWN UNRESOLVED for the Sheet Series field.** `LookupParameter(name)` returns only the **first** name match and silently picks the wrong duplicate; `Element.GetParameters(name)` returns all matches (prefer a writable, String-storage, `IsShared` one), but even that did **not** reliably populate the shared "Sheet Series" parameter in testing. The robust fix (deferred) is to bind by **shared-parameter GUID** via `element.get_Parameter(Guid)`. Do not assume the Sheet Series write works.

---

## Shared Coordinates & Base Points

Discovered building **Align Coordinates** (move host points to a grid intersection, then coordinate links).

- **`Document.AcquireCoordinates(LinkElementId locationId)` and `Document.PublishCoordinates(LinkElementId locationId)` exist in Revit 2024, and require an OPEN transaction** — calling either with none throws *"Modifying X is forbidden because the document has no open transaction"* (confirmed on a Windows/Revit run; this corrects an earlier, wrong assumption in this file that they must be called outside a transaction). Commit any link-instance moves in their own transaction first, then call Publish/Acquire inside a separate transaction of its own. Shared coordinates are written back into the link files when the host is saved.
- **Build the `LinkElementId` with its two-arg constructor, never the single-`ElementId` one, to identify a RevitLinkInstance for Publish/AcquireCoordinates.** `new LinkElementId(id)` sets `HostElementId`, not `LinkInstanceId` — Publish/AcquireCoordinates then throw *"locationId does not contain a valid linkInstanceId"* (confirmed on a Windows/Revit run). Use `new LinkElementId(linkInstanceId, ElementId.InvalidElementId)` — the invalid linked-element id means "the link instance itself," not a specific element inside it.
- **Publish/Acquire never moves geometry** — it only records a shared-coordinate relationship at the link's *current* position. To coordinate misaligned links, use **move-then-publish**: reposition each link instance (translate + rotate about the vertical axis so its grids match the host), *then* `PublishCoordinates`. Publish-only leaves whatever misalignment exists. (Aligning two grid intersections fixes only translation; match grid *directions* to also fix rotation — normalise the angle to (−90°, 90°] so a grid is never flipped end-for-end.)
- **Base-point handles:** `BasePoint.GetProjectBasePoint(doc)` / `BasePoint.GetSurveyPoint(doc)` return the point elements; `BasePoint.Position` is the location in **internal** coordinates, so move a point to a target with `ElementTransformUtils.MoveElement(doc, bp.Id, target − bp.Position)`. **Clip state governs whether the shared-coordinate origin travels with the marker** (clipped vs. unclipped) — *this and the publish-on-save behaviour are unverified on Windows and need a Revit plot.*
- **Unpin before transforming.** Link instances and base points can be pinned; read `Element.Pinned`, set it false, `MoveElement`/`RotateElement`, then restore the original pinned state. A pinned element silently refuses the move otherwise.
- **A link on Auto – Origin to Origin positioning ignores base points entirely.** Its instance transform is a fixed value set once at link-in time; moving the link file's own Project Base Point/Survey Point and calling `Reload()`/`LoadFrom()` changes nothing in the host — confirmed on a real project (Origin-to-Origin links, verified via Manage Links). To make a base-point correction actually take visual effect, **delete and recreate the `RevitLinkInstance`** against the same `RevitLinkType` with `ImportPlacement.Shared`, immediately after a successful `PublishCoordinates` call — this converts the link from the fixed Origin-to-Origin transform to Revit's self-correcting Shared Coordinates positioning. Never delete the old instance before `PublishCoordinates` has actually succeeded — a failed publish would otherwise leave the link missing entirely with no way back. (Reference: `PushCoordinatesToLinksRunHandler`.)

---

## Reusable Components — Prefer Over Hand-Rolling

- **Numeric input:** `InlineStepper` is the house numeric field — a typeable centre plus ± buttons, `Decimals=0` for integers, clamped to `[MinValue, MaxValue]`, `ValueChanged` event. Use it for *every* numeric input; never a raw `TextBox` or the retired `LemoineNumberStepper`.
- **Drag ghost / list reorder:** use `DragGhost` and `ListReorder` (see *WPF Drag Ghosts & Overlays*), never a bespoke Popup ghost or grip-handle reorder.
- **View / sheet selection:** use `BrowserTreePicker` — it mirrors the source document's Project Browser tree (folder titles, nesting, ordering, dependents nested under their primary), is fed by `BrowserTreeCapture.Capture(doc)` captured on the Revit main thread and handed over via `SetTree`, exposes `SingleSelect` for one-pick, and fires `SelectionChanged` once at the end of `SetTree` (same contract as `MultiSelectTabs.SetGroups` — subscribe first). Never hand-roll a `MultiSelectTabs` + label→ElementId map for picking views/sheets. **Dependent-view selection contract:** a parent view's checkbox selects **only the parent**, never its dependents (the parent is an eligible leaf with the dependents nested as children, so its binary checkbox routes through `SetLeaf` — the parent id alone). To grab a parent's dependents, **right-click any row**: it selects **only the dependents** beneath it (descendant leaves), leaving the clicked node itself unchecked — additive to the current selection, and a no-op in `SingleSelect` mode.
- **Naming patterns:** use `TokenInput` (`Source/Framework/Controls/Input/TokenInput.cs`) for *every* tool-generated or rewritten name — never a hand-rolled `{Token}` chip row or the retired Front/Center/End `NamingSlots` control (deleted; do not reintroduce). Build its chip list with `NamingTokenRegistry.TokensFor(entity, hasSource, extraComputed)` (`Source/Framework/Naming/`) so a picker only ever offers tokens valid for its context, and resolve patterns with `TokenResolver.Resolve` / `TokenResolver.GuardDegenerate` — never sequential `string.Replace`. A tool's own per-run values (e.g. `LevelName`, `Trade`) are declared as `TokenOrigin.Computed` `TokenDefinition`s beside the ViewModel and passed as `extraComputed`; they are not global registry entries. User-defined tokens (bound to a Revit parameter, GUID-first) are managed on the Global Settings **Naming** tab and persist machine-wide via `UserTokenStore`; each tool's last-used pattern persists via `NamingPatternStore` (Bulk Export keeps its own pre-existing settings file). See `LEMOINE_UI.md` §8.1 "TokenInput" for the full contract.

---

## Text Externalization — AppStrings

Every user-facing display string and run-log output line is externalized, not hardcoded. `AppStrings.T(key, ...args)` (`Source/Framework/AppStrings.cs`, namespace `LemoineTools.Framework`) is a zero-dependency loader/accessor over per-culture JSON files at `Strings/<culture>/*.json` (currently just `Strings/en/`), one JSON file per tool/window/handler, keys dot-flattened from nested JSON objects (e.g. `Strings/en/clashDefinitions.json`'s `window.title` → `AppStrings.T("clashDefinitions.window.title")`). Lookup falls back active culture → English → the key literal (logged via `DiagnosticsLog.Warn`), so a missing key surfaces in diagnostics rather than showing blank.

- **All new user-facing text goes here — never a hardcoded string literal.** This covers step-flow tool chrome (titles, labels, hints, review text), bespoke-window UI, and run-log output (`pushLog`/`Log(...)` calls). It does **not** cover debug-only output — `DiagnosticsLog.*` calls and `Debug.WriteLine` stay hardcoded, since they're developer diagnostics, not user-facing.
- **JSON files are JSONC** (plain JSON with `//` line/inline comments, documenting what each key is for) — a `StripComments` pre-pass removes them before the built-in `MiniJson` parser runs. `MiniJson` is a small recursive-descent reader (objects/arrays/strings/numbers/bool/null) written specifically so this stays dependency-free — no NuGet package, no `System.Text.Json`/`Newtonsoft`. **Never reference `System.Web.Extensions` for JSON parsing** — it drags `System.Web` into the WPF XAML compiler and throws MC1000 ("Could not find assembly 'System.Web...'"); this happened once and was fixed by dropping the reference and writing `MiniJson` instead.
- **Some strings stay hardcoded, deliberately** — never externalize: persisted/logic tokens compared with `==`/`switch` or used as dictionary keys (category group labels, mode tokens like `"Split"`/`"Replace"`, combo option VALUES that also drive behavior, naming-slot tokens), Segoe MDL2 glyph codepoints and `char.ConvertFromUtf32(...)` calls, `.NET` format specifiers (`"0.###"`, `"dd/MM/yy"`), and resource keys passed to `SetResourceReference`.
- **Rewiring existing string literals to `AppStrings.T(...)` must be done with a Python `str.replace()` script, never the Edit tool** — build a list of `(old, new, expected_count)` tuples and count-check every one (`src.count(old) == expected_count`) before applying. This generalizes the "Edit Tool — C# Unicode Escape Sequences" rule above: a bulk text-externalization pass touches many interpolated/Unicode-bearing strings where the Edit tool's exact-match requirement is unreliable, and the count-check catches accidental multi-match or zero-match replacements before they corrupt the file.
- **Verify before committing**: every `AppStrings.T("prefix...")` key referenced in a rewired `.cs` file must exist in the corresponding JSON (flatten the JSON, regex-scan the `.cs` file for `AppStrings\.T\(\s*"(prefix\.[^"]+)"`, diff the two key sets) — a missing key silently falls back to English-then-the-literal-key rather than failing to compile, so this check is the only guard against a typo'd key.
- **The active language is set via `AppSettings.Instance.SetLanguage(culture)`**, which reloads `AppStrings` and persists the choice; already-open tool windows keep their current language (only windows opened afterward pick up the change). The language picker lives in `GlobalSettingsWindow.General.cs`, directly under the UI Size section, listing every culture folder found via `AppStrings.AvailableCultures()`.

---

## Memory & Lifetime Discipline

Discovered auditing why tools held RAM after running.

- **Static ExternalEvent handlers live for the whole Revit session** (they're parked on `App` statics), so anything left on one outlives the run. Every handler must clear its per-run payload (input lists, specs, cached `View`/`Element` references, scan results) in a `finally` at the end of `Execute` — ViewModels reassign all inputs before each `Raise()`, so clearing is always safe. And every ViewModel that parks callbacks (`PushLog`, `OnProgress`, `OnComplete`, scan/pick callbacks) on a static handler must implement `IToolCleanup.OnWindowClosed` and null them — otherwise the closed window's ViewModel (and the WPF step content it references) stays rooted until the tool's next run, or forever that session.
- **`uidoc.ActiveView = view` opens that view in the Revit UI**, and Revit holds every open view's graphics in native RAM for the rest of the session — GC can never reclaim it. Any picker/loop that activates views (e.g. per-view `PickObject`) must snapshot the open `UIView`s and active view first, then afterwards restore the original active view and close only the views it opened (`PickerViewGuard` is the reference). Never close a view the user already had open.

---

## Run Lifecycle — Window Ownership, Failure Routing, Cancellation

- **The tool window is intentionally NOT owned to Revit — keep it independent.** Owning the window to Revit's main HWND (`new WindowInteropHelper(this).Owner = …` in `OnSourceInitialized`) glues it to Revit's z-order: it can no longer sit behind Revit, move to another monitor independently, or minimize/restore on its own. That pinning is worse than the problem it solved, so the owner was removed by explicit decision. **Accepted trade-off:** Revit's modal transaction-failure dialogs and `TaskDialog`s can again render *behind* the tool window — but those failures are still captured into the run's Output log via `RevitFailureCapture` / `RunLogSink`, so nothing is silently lost. Do not re-add the HWND owner. (Never use `ComponentManager.ApplicationWindow` for an owner either — it crashes Revit.)
- **Route Revit's own warnings/errors/dialogs into the active run's Output log.** `RevitFailureCapture` (process-wide `FailuresProcessing` + `DialogBoxShowing` handlers, subscribed once in `App`) feeds the active run's log via `RunLogSink`; both **no-op outside a Lemoine run** so other transactions are untouched. Call `RevitFailureCapture.BeginRun()` + `RunLogSink.Set(pushLog)` at run start and `RunLogSink.Clear()` when the window closes.
- **Make every long run cooperatively cancellable.** `RunState` holds a thread-safe cancel flag (`Begin` / `RequestCancel` / `CancelRequested` / `End`); while a run is in flight the footer Reset button flips to a red Cancel. Every **looping** `ExternalEvent` handler must test `RunState.CancelRequested` at its per-progress log point, log a "Stopped by user — N of M processed; work so far preserved" line, `break`, and **fall through to the existing commit** so committed work is preserved (finish state `Stopped` with partial counts). Use `RunProgressReporter` for the steady 5% log cadence — `CancelRequested` is only the stop signal. Read-only single-shot pick/print handlers need no cancel break.
- **Report long bulk runs to the Output log every ~5%, not just on the progress bar.** Split the work into ~20 batches sized `ceil(N / 20)` and `pushLog` a progress line after each one (percentage + processed/total + a result count), so the Output log shows steady `5% … 10% … 100%` cadence. This doubles as the cancellation/checkpoint boundary and, for tools that batch an expensive API call (e.g. cross-document `CopyElements`), as the batch size itself.

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
| Per-STA-thread window subscribing to a global event (`AppSettings.ThemeChanged` / `UiSizeChanged`) with an anonymous lambda + blocking `Dispatcher.Invoke` | Named handler detached on `Closed`; marshal with non-blocking `BeginInvoke` guarded by `if (Dispatcher.HasShutdownStarted) return;` (root cause of the theme-switch crash) |

### Why leaked global-event subscriptions crash Revit

Each tool/settings window runs on its **own dedicated STA thread** that shuts down (`Dispatcher.InvokeShutdown()`) when the window closes. A subscription to a process-wide event like `AppSettings.ThemeChanged` made with an **anonymous lambda** can never be `-=`'d, so it **outlives the window**. The next time the event fires, the stale handler runs a blocking `Dispatcher.Invoke` into a **terminated dispatcher** — which throws on the firing thread (unhandled → crash) or blocks forever waiting for a thread that will never pump (→ Revit hangs). The bug is session-history-dependent (you must have opened *and closed* such a window earlier), so it presents as an intermittent crash.

Rules for any window subscribing to a global Lemoine event:
- Subscribe with a **named instance method**, never an anonymous lambda, and `-=` it in `Closed`/`OnClosed`. `StepFlowWindow` is the reference.
- In the handler, marshal back with **non-blocking `Dispatcher.BeginInvoke`** (not blocking `Invoke` — that can deadlock against Revit's main thread), guarded by `if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;`.
- The event raiser (`AppSettings.SetTheme`/`SetUiSize`) walks `GetInvocationList()` and wraps each subscriber in `try/catch` → `DiagnosticsLog.Swallowed`, so one dead subscriber can't abort the chain or crash the initiating thread.

### Why `Popup StaysOpen=false` crashes Revit

`StaysOpen=false` registers a `ComponentDispatcher.ThreadFilterMessage` hook to detect outside clicks. This fires on every Win32 message on Revit's main thread and corrupts the message loop.

### Dismissing a `StaysOpen=true` popup on click-off

Because `StaysOpen=false` crashes Revit, close an open popup by attaching a **window-level `PreviewMouseDown` handler only while it is open** and closing when the click lands outside the popup content (`!popupRoot.IsMouseOver`); detach on `Closed`. The popup hosts its own hwnd, so its own clicks never tunnel through the window — no `ThreadFilterMessage` hook, no crash.

### Popup / dropdown scroll-wheel behaviour

These were discovered fixing the "category pill dropdown scrolls down but not up" bug.

- Inside an `AllowsTransparency=true` `Popup` (Revit's WPF hosting), the default `ScrollViewer` mouse-wheel handling is **unreliable and asymmetric** — it delivers down-scrolls but drops up-scrolls. Don't rely on it: handle `PreviewMouseWheel`, drive the offset yourself with `ScrollToVerticalOffset` (clamped to `[0, ScrollableHeight]`), and set `e.Handled = true`. Manual scrolling is symmetric by construction.
- A `Popup`'s routed events **bubble up into the owner window's element tree**, so a popup-hosted scroller that re-raises the wheel to its parent lets the *page's* scroll position govern the dropdown (the page being at the top blocks scrolling the popup up). Popup/dropdown scrollers must be **self-contained** — consume the wheel, never bubble it out to the page behind them. In-page nested scrollers are the opposite: they *should* bubble to the page at their limits (`ControlStyles.WireBubblingScroll`).
- Visual-tree popup detection (walking `VisualTreeHelper` parents up to a `PopupRoot`) is **unreliable under Revit's hosting**. Tag popup scrollers authoritatively with `ControlStyles.SetSelfContainedScroll(sv, true)`; the `PresentationSource.RootVisual == PopupRoot` check is only a backstop.

### Revit API gotchas (Revit 2024)

| Wrong | Correct |
|---|---|
| Enumerate Revit's predefined view-scale list via the API | No such API — hardcode the standard scale ladder (`1/32"=1'-0"` … `12"=1'-0"`, plus engineering `1"=10'-0"` … `1"=200'-0"`). Only built-in scales are coverable; user-custom scales can't be listed. A view scale is just an integer denominator (`View.Scale`) |
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
- **MEP fabrication categories carry an `MEP ` prefix in a live document.** A real doc reports them as `MEP Fabrication Ductwork` / `MEP Fabrication Pipework` / `MEP Fabrication Hangers` / `MEP Fabrication Containment` (Revit's `Category.Name`), **not** the short `Fabrication Ductwork`/… names. So a curated picker keyed by short labels (e.g. Discover's hardcoded `CategoryGroups`) can't resolve them against the document-captured `KnownCategoryMap` — the runtime key has the prefix, the lookup misses, and the category is **silently dropped** (no scan config row, no parameter dropdown). Resolve a curated label to its `OST_` via `AutoFiltersSettings.TryResolveCategoryOst` (runtime map first, then the short-name `DefaultKnownCategoryMap` fallback), never a runtime-map-only `TryGetValue`.

---

## Cross-Document Copy & Idempotent Re-Runs

Discovered building **Copy Linear Elements** / **Copy Grids** / **Copy Elements from Link** (pull elements out of a link into the host).

- **Cross-document `ElementTransformUtils.CopyElements` pops a modal "Duplicate Types" dialog for every call** when any type already exists in the host (it does for nearly every MEP/grid copy). Pass a `CopyPasteOptions` whose `SetDuplicateTypeNamesHandler` returns `DuplicateTypeAction.UseDestinationTypes` (a tiny `IDuplicateTypeNamesHandler`) to suppress it and silently reuse the destination's types. `Transaction` `SetForcedModalHandling(false)` does **not** suppress this dialog — it is a copy-paste prompt, not a failure.
- **The cross-document `CopyElements(srcDoc, ids, destDoc, transform, opts)` overload throws when `srcDoc == destDoc`.** For a host-sourced copy use the same-document `ElementTransformUtils.CopyElement(doc, id, XYZ.Zero)` instead; only use the cross-doc overload for a real link (pass `link.GetTotalTransform()`).
- **Batch the copy — one `CopyElements` call per element is dominated by per-call overhead** and is dramatically slower on large runs (batching also keeps connected MEP wired, since a network copies together). Pass many ids in one call; chunk the run for progress/cancellation and fall back to per-element copy for a chunk that throws so one bad element (e.g. an unsupported host) can't sink the whole batch. **A batched cross-document copy does NOT report which output came from which source** (no per-input mapping, and order isn't guaranteed). When per-source provenance is needed (stamping), attribute each output back to its source by **world-position identity hash**: the copy applied the link transform, so an output's host-world hash (computed with `Transform.Identity` after a single `doc.Regenerate()`) equals its source's link-world hash — match on that, and report any output that can't be attributed rather than mis-stamping it.
- **Idempotent re-runs over linked sources: stamp, don't track externally.** Write an Extensible Storage `Entity` onto every created host element carrying the source `UniqueId` + a geometry/param hash (constant hardcoded `Schema` GUID, `Schema.Lookup` guard — same discipline as `AutoDimOwnerSchema`). A re-run reads all stamped outputs in one pass via `new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid))` and reconciles: rebuild changed/new keys (deleting their prior outputs first), leave unchanged keys, delete outputs whose source key is gone. No external database, self-healing.
- **Grids are unique by name and the setter throws on a duplicate**, so a grid copy must pre-check host grid names and **skip-and-log** any clash (it can never overwrite). Same family as the View.Name / sheet-number uniqueness rule.

---

## WPF Drag Ghosts & Overlays

- A cursor-following drag ghost must be a window-space `AdornerLayer` overlay, **not** a `Popup`. `PlacementMode.AbsolutePoint` popups get nudged back on-screen near a screen edge, so the ghost drifts off the cursor (worst on the right).
- `AdornerLayer.GetAdornerLayer(source)` returns the *nearest* layer, which inside a `ScrollViewer` is clipped to that viewport — adorn `Window.GetWindow(source).Content` so the ghost spans the whole window.
- A `RenderTargetBitmap` / `VisualBrush` snapshot of an element whose `Background` is `Brushes.Transparent` captures only its text/borders on transparent pixels — paint a themed solid backing (`LemoineRaised`) behind the snapshot or it reads as invisible (this is why inactive, transparent-background tabs/rows showed no ghost).
- Anchor the ghost at the **grab point** (`e.GetPosition(source)`), not its centre — centring a wide row reads as "off the mouse" when grabbed near an edge.
- Don't hand-roll any of this: `DragGhost` (snapshot, grab-point anchored, solid backing) and `ListReorder` (whole-row drag, persisted order) are the house mechanism.
- **A drag-placement overlay that must catch one payload kind without intercepting other drags underneath it stays `IsHitTestVisible = false` and is flipped `true` only while the matching drag session is active** (toggle on the broadcast `LegendDragSession.Started`/`Ended` with the payload-kind guard, back to false on end). A topmost transparent `Canvas` overlay wired this way captures group/category drags for a single live insertion marker while leaving the block drag/drop inside the cards beneath it completely untouched — no need to add "don't swallow my payload" guards to every child drop target. Keep the marker itself non-hit-testable so the cards never reflow as you aim (this replaced the Legend Creator's thin drop-bar group placement).

---

## View Filters & Linked Models

Discovered while making **Make Ceiling Grids** hide linked ceilings.

- **Host view filters only affect linked elements when the link is displayed "By Host View"** in that view. To hide or override linked elements, apply a `ParameterFilterElement` on the host view — do **not** rely on per-instance `view.HideElements`, because `FilteredElementCollector(doc, viewId)` (the host-view collector) never returns elements that live inside links, so it silently misses every linked ceiling. Warn the user about any link not set to "By Host View" (see `ReportLinkDisplayModes`) rather than changing the link's display.
- **A `ParameterFilterElement` rule matches a single parameter** — family AND type cannot be AND-combined in one rule. Match ceiling types by the link-safe built-in `ALL_MODEL_TYPE_NAME` ("Type Name"); link-safe built-in parameters are listed in `AutoFiltersSettings.LinkSafeParameters`.
- **Prefer the Ceiling Heatmap filter mechanism for any filter-driven tool.** Register an `ExternallyManaged` trade (`FilterTradeConfig`) with one rule per item, create one matching `ParameterFilterElement` per rule (reuse-by-name via `AutoFiltersSettings.MakeFilterName`), apply per-view inside a single transaction, and call `ReportLinkDisplayModes` — rather than hand-rolling a combined filter. `CeilingHeatmapEventHandler.RegisterCeilingHeatmapTrade` is the reference.
- **To recolor/override a filter whose graphics are governed by a view template, write to the TEMPLATE, not the view.** A view template is itself a `View` that carries the filter; `SetFilterOverrides` / `SetFilterVisibility` / `SetIsFilterEnabled` on a template-controlled *view* throws (the template owns it) — so when re-applying overrides across the project, iterate **all** views *and* templates, apply only where the filter is already present, and **catch-and-skip** the throwers. The template in the list receives the authoritative override and propagates it to its dependent views. (Reference: `AutoFiltersEventHandler.ApplyChangedOverridesAcrossViews`, the close-time colour-propagation pass — scoped to filters already placed on a view/template, never blanket-attaching.)
- **Externally-managed trades** (Ceiling Heatmap / Ceiling Grids) name their filters with the same `AutoFiltersSettings.MakeFilterName(tradeId, ruleName)` convention, so a tool can **attach them to a view and re-apply the rule's stored overrides by name** without regenerating their (non-keyword) definitions — never rebuild a managed trade's filter definition. `AutoFiltersEventHandler.ApplyExistingFilterToView` (gated by `IncludeSelectedExternallyManaged`) is the reference; `ChangedOverrideFilterNames` excludes managed trades from definition churn.

---

## Build Environment

This project cannot be built on Linux. `UseWPF=true` + (`net48` or `net8.0-windows`) requires `Microsoft.NET.Sdk.WindowsDesktop`, which is Windows-only — neither the Linux .NET SDK nor Mono can satisfy it. Do not attempt Linux CI or cloud builds. Build and test on Windows only.

### Multi-year Revit support (2024-2027)

Revit 2024 runs .NET Framework 4.8; Revit 2025, 2026, and 2027 all run .NET 8. Because three of the four target years share one runtime, plain SDK `<TargetFrameworks>` multi-targeting can't produce four distinct outputs on its own (one output per framework moniker, max two here). Instead, each Revit year is its own build **Configuration** — `Debug2024`/`Release2024` … `Debug2027`/`Release2027` — and `TargetFramework`, `RevitDir`, `DeployDir`, and a `REVITxxxx` `DefineConstants` symbol are all selected by `Condition` on that Configuration in `LemoineTools.csproj`.

- Build a specific year with `dotnet build -c Release2026` (or the year of your choice). The bare `Debug`/`Release` Configuration (no year suffix, e.g. from an IDE that hasn't been told about the custom Configurations yet) falls back to 2024 behavior.
- **Every plain `Debug`/`Release` build (Visual Studio's Build/F5 button, or `dotnet build`/`msbuild` with no `-c`) auto-triggers all 4 years.** The `AutoBuildAllYears` target (`AfterTargets="Build"`, guarded to the bare `Debug`/`Release` Configuration names) calls `BuildAllYears`, which shells out to a separate `dotnet build -c ReleaseYYYY` **process** per year (`<Exec>`, not the in-process `<MSBuild>` task) and drops each year's output straight into that year's `DeployDir`. A failure in one year (e.g. an unpopulated `libsYYYY/`) is logged as a warning (`ContinueOnError="WarnAndContinue"`) and does not stop the remaining years from building. Check the log for "Build succeeded"/"Build FAILED" under each "Building ReleaseYYYY" banner to see which years actually produced output. Every normal build now does up to 4x the work — expect longer build times, including on F5 debug launches.
  **Must be a separate process, not the in-process `<MSBuild>` task**: when `BuildAllYears` runs as part of a live Visual Studio build of this same project, an in-process `<MSBuild Properties="Configuration=ReleaseYYYY">` call silently kept reusing the outer build's already-evaluated Debug/net48 project instance instead of actually switching Configuration — every "year" compiled from `obj\Debug\...` regardless of which year it claimed to build. A separate `dotnet build` process has no such shared state and reliably picks up its own Configuration and TargetFramework; its own implicit restore-before-build also means no explicit `Restore` target is needed here.
  **Even separate `dotnet build` processes need `/nodeReuse:false`**: MSBuild's persistent worker-node reuse can serve a build of the same project path from an already-warm node, which still returned a stale/cached Debug evaluation for all four year builds the first time this ran as plain `dotnet build -c ReleaseYYYY` with no node-reuse flag — all four sub-builds compiled from `obj\Debug\...` and hit identical errors regardless of the `-c` value. `/nodeReuse:false` on each invocation forces a fresh MSBuild process so no state can bleed between the four year builds or from the outer VS build that triggered them.
- To build only one specific year without the other three, use `dotnet build -c Release2026` (or the year of your choice) directly — this bypasses `AutoBuildAllYears` since the Configuration isn't the bare `Debug`/`Release`.
- **`Directory.Build.props` redirects `MSBuildProjectExtensionsPath` to `obj\$(Configuration)\`, but ONLY for the year-suffixed Configurations** (`*2024`/`*2025`/`*2026`/`*2027`). Without it, those Configurations shared one `obj\project.assets.json`, and since each year resolves a different `TargetFramework` (net48 vs net8.0-windows), restoring one year clobbered the file another year just restored into — root cause of a `NETSDK1005` ("doesn't have a target for 'net8.0-windows'") failure the first time `BuildAllYears` built the years back-to-back.
  **Redirect only `MSBuildProjectExtensionsPath`, not `BaseIntermediateOutputPath`.** `BaseIntermediateOutputPath` also drives the SDK's default compile-item exclude (`$(BaseIntermediateOutputPath)**` is excluded from the default `**\*.cs` glob) — redirecting it per-Configuration made a `Release2024` build only exclude its own `obj\Release2024\**`, leaving the bare `Debug` Configuration's leftover `obj\Debug\**\*.g.i.cs` (stale generated XAML code-behind from an earlier build) unexcluded, so the default glob swept those stale files into `@(Compile)` and the compiler choked on their now-invalid embedded source references (`CS1504` "could not be opened"). `IntermediateOutputPath` is already segmented per Configuration+TargetFramework by the SDK's own default computation, so only the shared NuGet restore-output path (`MSBuildProjectExtensionsPath`) ever needed redirecting.
  **The bare `Debug`/`Release` Configuration (Visual Studio's normal Build/F5) is deliberately left on the SDK's default path entirely** — redirecting it too broke VS's own automatic restore (`NETSDK1004` "assets file not found"), since VS's implicit restore-before-build doesn't reliably follow a path change the way an explicit CLI `dotnet build` invocation does.
- The Revit API DLLs (`RevitAPI.dll`, `RevitAPIUI.dll`) for each year are checked in to `libs/` (2024), `libs2025/`, `libs2026/`, `libs2027/`. The `.csproj` falls back to the matching `libs*` folder when that year's standard install path (`C:\Program Files\Autodesk\Revit 20XX`) does not exist, so cloning the repo is sufficient to resolve references without a local Revit installation — **once those DLLs are actually present**. `libs2025/`, `libs2026/`, and `libs2027/` currently only hold placeholder READMEs; add the real DLLs from a matching Revit install to build those configurations.
- **Revit 2027 has not shipped as of writing.** `Debug2027`/`Release2027` exist so the project is ready the day Autodesk releases it, but that Configuration cannot build until real 2027 API DLLs exist to reference.
- Only add `#if REVIT2024` / `#if REVIT2025` / etc. branches at a specific call site once a real build against that year's SDK actually breaks — never speculatively.

**`LemoineTools.csproj` is a root-level SDK-style project, so its default `**\*` globs sweep every subfolder — including sibling sub-projects' `obj\` output.** Each sibling project (`LemoinePreview`, `LemoineNavisworks`, any future one) must be `Remove`-excluded from `Compile`/`Page`/`None`/`EmbeddedResource`, or MSBuild compiles its generated `*.AssemblyAttributes.cs` (→ **CS0579** duplicate `TargetFrameworkAttribute`, sometimes for *both* net48 and net8 targets) and its XAML `*.g.cs` (→ **CS0102** duplicate `_root`/`_outer` field). Keep the exclusion **unconditional**: an untracked `obj\` folder survives a branch switch, so a sibling project that lives only on another branch can still poison this build locally.

---

## Key Files

| Path | Purpose |
|------|---------|
| `LEMOINE_UI.md` | UI architecture, design system, component library, and tool contract |
| `LemoineTools.csproj` | Project file (per-year Configuration selects net48 for 2024, net8.0-windows for 2025-2027) |
| `Source/Framework/RevitFailureCapture.cs` | Process-wide `FailuresProcessing`/`DialogBoxShowing` capture that routes Revit's own failures into the active run's log |
| `Source/Framework/RunLogSink.cs` | Active run's log sink, set/cleared by the tool window |
| `Source/Framework/RunState.cs` | Thread-safe cancel flag (`CancelRequested`) for cooperative run cancellation |
| `Source/` | All C# source and XAML files |

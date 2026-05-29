# Lemoine Revit Tools — Claude Guidelines

## Project Overview

This is a Revit plugin built with C# and WPF targeting .NET Framework 4.8. The primary project is `LemoineTools`. See `LEMOINE_UI.md` for the full UI architecture reference.

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

---

## Revit API Ordering Constraints

### ViewTemplateId before geometry

`view.ViewTemplateId = templateId` must be assigned **before** `SetSectionBox()` or any crop-box operation. The template assignment can reset view geometry; setting it first lets the subsequent programmatic geometry override it.

---

## LemoineMultiSelectTabs Contract

`SetGroups` fires `SelectionChanged` once at the end of setup. Any ViewModel that mirrors tab selection into a private field must subscribe to `SelectionChanged` **before** calling `SetGroups` — that callback is the only mechanism that populates the mirror field on initialisation.

---

## Revit Crash Constraints

These patterns cause Revit to crash or hang. They have been discovered by breaking Revit in real sessions. Do not use them.

| ❌ Crashes Revit | ✅ Safe alternative |
|---|---|
| `Popup` with `StaysOpen=false` | `StaysOpen=true` + manual dismiss via `PreviewMouseDown` or a close button |
| `SizeToContent="WidthAndHeight"` + `WindowStyle="None"` | `Width=N` (fixed) + `SizeToContent="Height"` |
| `Autodesk.Windows.ComponentManager.ApplicationWindow` for window owner | Not referenced in this project — omit or use `WindowInteropHelper` with a Revit HWND |

### Why `Popup StaysOpen=false` crashes Revit

`StaysOpen=false` registers a `ComponentDispatcher.ThreadFilterMessage` hook to detect outside clicks. This fires on every Win32 message on Revit's main thread and corrupts the message loop.

### Revit API gotchas (Revit 2024)

| Wrong | Correct |
|---|---|
| `ZoomType.FitPage` | `ZoomType.FitToPage` |
| `RasterQualityType.Draft` | `RasterQualityType.Low` (Draft removed in 2024) |
| `PDFExportOptions.Zoom` | `PDFExportOptions.ZoomPercentage` |
| `ParameterFilterElement.AllFilterableCategories` | `ParameterFilterElement.GetAllFilterableCategories(doc)` |
| `TextNote` Y = top of text | TextNote Y is the **baseline** — cap height rises above it |

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
